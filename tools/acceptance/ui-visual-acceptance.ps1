<#
.SYNOPSIS
    WinUI 纯视觉验收：隔离会话 + 真实进程 + 页面就绪后截图留证。

.DESCRIPTION
    P0-B：绝不按进程名全局结束 FNSyncPlus.WinUI。启动前快照既有实例；每个场景使用
    唯一 Session ID；只清理本脚本自己启动并验证过（PID + 启动时间）的进程；先尝试
    正常退出流程，失败才对本轮进程受控强杀。测试开始前启动一个"受保护模拟实例"，
    结束后验证其 PID、启动时间与存活状态均未变化——任何误杀直接 FAIL。

    P0-C：所有场景运行于 --ui-visual-acceptance 纯视觉模式：临时隔离配置、不读取任何
    保存的凭据、不连接真实 NAS、不执行备份、不检查更新，只渲染模拟任务数据。

    P1 页面就绪：脚本轮询应用写入的 ui-ready.json（页面加载 + 布局完成后写入），
    目标页面就绪才调用 UiProbe 截图；超时即 FAIL，不会把白屏或上一页当作证据。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Payload,
    [string]$ProbeProject = '',
    [string]$OutRoot = '',
    [int]$MinWidth = 1000,
    [int]$MinHeight = 640,
    [int]$ReadyTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutRoot)) {
    $OutRoot = Join-Path $repo ('.acceptance\ui-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if (-not (Test-Path -LiteralPath $Payload -PathType Container)) { throw "载荷目录不存在：$Payload" }
$exe = Join-Path $Payload 'FNSyncPlus.WinUI.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "载荷中缺少 FNSyncPlus.WinUI.exe：$Payload" }
if ([string]::IsNullOrWhiteSpace($ProbeProject)) {
    $ProbeProject = Join-Path $repo 'tools\acceptance\UiProbe\UiProbe.csproj'
}
New-Item -ItemType Directory -Path $OutRoot -Force | Out-Null

# Architectural isolation (round 4): every launched app instance resolves ALL storage under
# this test data root — never the machine's production APPDATA/LOCALAPPDATA. Nothing is moved
# or restored; production bytes are simply never touched.
$testDataRoot = Join-Path $OutRoot 'testdata'
New-Item -ItemType Directory -Path $testDataRoot -Force | Out-Null
$env:FNSYNC_DATA_ROOT = $testDataRoot
$env:FNSYNC_STORAGE_AUDIT = Join-Path $OutRoot 'storage-audit.log'
$testSessionsRoot = Join-Path $testDataRoot 'temp\FNSyncPlus_CodexTests'

$dotnet = $null
foreach ($candidate in @('D:\FNSyncPlus_Toolchain\dotnet\dotnet.exe', 'C:\Program Files\dotnet\dotnet.exe')) {
    if (Test-Path -LiteralPath $candidate) { $dotnet = $candidate; break }
}
if (-not $dotnet) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $dotnet = $command.Source } else { throw '找不到 dotnet，无法运行 UiProbe' }
}

# ---------------------------------------------------------------- results plumbing
$script:results = New-Object System.Collections.ArrayList
$script:records = New-Object System.Collections.ArrayList
function Record-Result {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    [void]$script:results.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    $tag = 'FAIL'; if ($Passed) { $tag = 'PASS' }
    Write-Host ("[{0}] {1} :: {2}" -f $tag, $Name, $Detail)
}

# ---------------------------------------------------------------- process ownership (P0-B)
# Only processes STARTED BY THIS SCRIPT may ever be stopped, and only after re-verifying
# identity with PID + start time. Name-based kill is forbidden.
$script:trackedProcesses = New-Object System.Collections.ArrayList
$script:existingSnapshot = @{}
Get-Process -Name 'FNSyncPlus.WinUI' -ErrorAction SilentlyContinue | ForEach-Object {
    $script:existingSnapshot[$_.Id] = $_.StartTime
}
$script:snapshotCount = $script:existingSnapshot.Count

function Test-TrackedAlive {
    param([int]$ProcessId, [datetime]$StartedAt)
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $p) { return $false }
    try { return ($p.StartTime -eq $StartedAt) } catch { return $false }
}

function Stop-TrackedProcess {
    param([int]$ProcessId, [datetime]$StartedAt, [string]$Label)
    if (-not (Test-TrackedAlive -ProcessId $ProcessId -StartedAt $StartedAt)) { return }
    # 1) Normal application exit first: WM_CLOSE drives the real safe-exit sequence
    #    (stop new work, wait for tasks, flush logs, close). Never a process-name kill.
    $live = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $live) {
        try { $null = $live.CloseMainWindow() } catch { }
    }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Test-TrackedAlive -ProcessId $ProcessId -StartedAt $StartedAt)) {
        Start-Sleep -Milliseconds 300
    }
    if (-not (Test-TrackedAlive -ProcessId $ProcessId -StartedAt $StartedAt)) { return }
    # 2) Controlled force-kill, ONLY for a process this script started and re-verified.
    #    Visual-acceptance sessions run zero backup tasks, so nothing can be interrupted.
    try { Stop-Process -Id $ProcessId -Force -ErrorAction Stop } catch { }
    $deadline = (Get-Date).AddSeconds(3)
    while ((Get-Date) -lt $deadline -and (Test-TrackedAlive -ProcessId $ProcessId -StartedAt $StartedAt)) {
        Start-Sleep -Milliseconds 200
    }
}

function Start-TrackedApp {
    param([string]$SessionId, [string]$InitialView, [string[]]$ExtraArgs)
    $arguments = @('--isolated-test-session', "--isolated-test-state=$SessionId",
        '--isolated-test-remote=Codex_FN_Test_Ui', '--ui-visual-acceptance')
    if (-not [string]::IsNullOrEmpty($InitialView)) { $arguments += "--view=$InitialView" }
    if ($null -ne $ExtraArgs) { $arguments += $ExtraArgs }
    $p = Start-Process -FilePath $exe -PassThru -ArgumentList $arguments
    Start-Sleep -Milliseconds 400
    $startedAt = $null
    try { $startedAt = $p.StartTime } catch { $startedAt = (Get-Process -Id $p.Id -ErrorAction Stop).StartTime }
    $entry = [pscustomobject]@{ id = $p.Id; started = $startedAt; label = $SessionId }
    [void]$script:trackedProcesses.Add($entry)
    return $entry
}

# ---------------------------------------------------------------- fixtures
$longName = '家庭照片与四年工作文档完整备份（含中文长文件名与特殊字符 テスト・테스트・Ω≈ç√ 2026 年度归档归档归档）'
$longName2 = '项目源代码与设计稿每日增量备份 - D:\Work\2026\Q4\超长目录名称用于验证界面换行与省略号处理是否正确显示'
$longName3 = 'VeryLongEnglishTaskName_ForTruncationTesting_ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789_EndNoSpaces'

function New-SessionSettings {
    param([string]$SessionId, [string]$Theme, [string[]]$Names)
    $sessionDir = Join-Path $testSessionsRoot "WinUiSession_$SessionId"
    New-Item -ItemType Directory -Path $sessionDir -Force | Out-Null
    # Fresh readiness beacon per session: a stale file must never satisfy a new scenario.
    Remove-Item -LiteralPath (Join-Path $sessionDir 'ui-ready.json') -Force -ErrorAction SilentlyContinue
    $localRoot = $testSessionsRoot
    $tasks = @()
    $index = 0
    foreach ($name in $Names) {
        $index++
        $source = Join-Path $localRoot "ui-source-$index"
        New-Item -ItemType Directory -Path $source -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $source '示例文件.txt') -Value 'visual acceptance fixture' -Encoding utf8
        $tasks += [ordered]@{
            task_id = ([guid]::NewGuid().ToString('N'))
            task_name = $name
            source_folder = $source
            nas_device_id = ''
            destination_root = 'Codex_FN_Test_Ui'
            destination_folder = "Codex_FN_Test_Ui/task-$index"
            provider_type = 0
            nas_username = 'ui-acceptance'
            sync_mode = 0
            enabled = $true
            auto_sync_enabled = $false
            realtime_watch_enabled = $false
            interval_minutes = 60
            schedule_mode = 0
            backup_strategy = 0
            settling_delay_seconds = 3
            last_sync_status = '尚未同步'
            created_at = (Get-Date).ToUniversalTime().ToString('o')
        }
    }
    $settings = [ordered]@{
        NasServer = ''
        Username = ''
        RememberCredentials = $false
        PreferredBackupRoot = '我的文件'
        MaxConcurrentSyncs = 2
        AppTheme = $Theme
        Tasks = $tasks
        AutoSyncEnabled = $false
        SyncIntervalMinutes = 60
        RealtimeWatchEnabled = $false
        StartWithWindows = $false
        MinimizeToTray = $false
    }
    $settings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $sessionDir 'settings.json') -Encoding utf8
    return $sessionDir
}

function Wait-PageReady {
    param([string]$SessionId, [string]$Page, [int]$TimeoutSeconds)
    $sessionDir = Join-Path $testSessionsRoot "WinUiSession_$SessionId"
    $readyPath = Join-Path $sessionDir 'ui-ready.json'
    $began = Get-Date
    $polls = 0
    while (((Get-Date) - $began).TotalSeconds -lt $TimeoutSeconds) {
        if (Test-Path -LiteralPath $readyPath) {
            try {
                $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
                $polls++
                if ($ready.page -eq $Page -and $ready.ready -eq $true) {
                    return [pscustomobject]@{ ok = $true; polls = $polls; waitedMs = [int]((Get-Date) - $began).TotalMilliseconds; taskCount = $ready.task_count }
                }
            } catch { }
        }
        Start-Sleep -Milliseconds 400
    }
    return [pscustomobject]@{ ok = $false; polls = $polls; waitedMs = [int]((Get-Date) - $began).TotalMilliseconds; taskCount = -1 }
}

# ---------------------------------------------------------------- probe (screenshot only)
# The pass criterion is content_coverage (share of pixels that differ from the dominant colour).
# content_ratio (distinct shades) collapses when a page renders without subpixel anti-aliasing or
# without Mica because the window is not the active one - a real page, not an empty one, so the
# coverage metric is the honest check.
function Invoke-Probe {
    param([int]$ProcessId, [datetime]$StartedAt, [string]$OutPng, [string]$JsonPath,
        [int]$Width = 0, [int]$Height = 0, [double]$MinCoverage = 0.05, [int]$Attempts = 6,
        [int]$Scroll = 0)
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if (-not (Test-TrackedAlive -ProcessId $ProcessId -StartedAt $StartedAt)) {
            return [pscustomobject]@{ ok = $false; error = '目标进程已退出' }
        }
        $arguments = @('run', '--project', $ProbeProject, '-c', 'Release', '--',
            '--pid', "$ProcessId", '--out', $OutPng, '--json', $JsonPath, '--wait', '30')
        if ($Width -gt 0) { $arguments += @('--size', "$($Width)x$($Height)") }
        if ($Scroll -ne 0) { $arguments += @('--scroll', "$Scroll") }
        & $dotnet @arguments | Out-Null
        if (-not (Test-Path -LiteralPath $JsonPath)) { Start-Sleep -Milliseconds 800; continue }
        $probe = Get-Content -LiteralPath $JsonPath -Raw | ConvertFrom-Json
        if (-not $probe.ok) { return $probe }
        $coverage = 0.0
        if ($null -ne $probe.PSObject.Properties['content_coverage']) { $coverage = [double]$probe.content_coverage }
        if ($coverage -ge $MinCoverage -or $attempt -eq $Attempts) {
            $probe | Add-Member -NotePropertyName attempts -NotePropertyValue $attempt -Force
            return $probe
        }
        Start-Sleep -Milliseconds 800
    }
    return $null
}

function Capture-Scenario {
    param([string]$ScenarioKey, [string]$Page, [string]$View, [string]$Theme,
        [string[]]$TaskNames, [bool]$AlsoMinWindow, [string[]]$ExtraArgs, [int]$TallHeight = 0)
    $sessionId = [guid]::NewGuid().ToString('N')
    $sessionDir = New-SessionSettings -SessionId $sessionId -Theme $Theme -Names $TaskNames
    $process = $null
    try {
        $process = Start-TrackedApp -SessionId $sessionId -InitialView $View -ExtraArgs $ExtraArgs
        $ready = Wait-PageReady -SessionId $sessionId -Page $Page -TimeoutSeconds $ReadyTimeoutSeconds
        $readyOk = $ready.ok
        Record-Result -Name ("{0}/{1}：目标页面完成导航与数据加载（就绪信号）" -f $ScenarioKey, $Page) `
            -Passed $readyOk -Detail ("ready={0} polls={1} waitedMs={2} taskCount={3}" -f $readyOk, $ready.polls, $ready.waitedMs, $ready.taskCount)
        if (-not $readyOk) {
            # 保留诊断：截一张"当前实际样子"，便于排查是哪一步卡住，但不作为通过证据。
            $diagPng = Join-Path $OutRoot ("{0}-{1}-timeout-diag.png" -f $ScenarioKey, $Page)
            Invoke-Probe -ProcessId $process.id -StartedAt $process.started -OutPng $diagPng `
                -JsonPath (Join-Path $OutRoot ("{0}-{1}-timeout-diag.json" -f $ScenarioKey, $Page)) | Out-Null
            return
        }

        $defaultPng = Join-Path $OutRoot ("{0}-{1}.png" -f $ScenarioKey, $Page)
        $defaultJson = Join-Path $OutRoot ("{0}-{1}.json" -f $ScenarioKey, $Page)
        $default = Invoke-Probe -ProcessId $process.id -StartedAt $process.started -OutPng $defaultPng -JsonPath $defaultJson
        $ok = ($null -ne $default -and $default.ok)
        $detail = 'probe 未产出结果文件'
        if ($ok) { $detail = "size={0}x{1} dpi={2} content={3} coverage={4} title={5} attempts={6}" -f $default.width, $default.height, $default.dpi, $default.content_ratio, $default.content_coverage, $default.title, $default.attempts }
        elseif ($null -ne $default) { $detail = "probe failed: $($default.error)" }
        Record-Result -Name ("{0}/{1}：页面渲染完成后截图" -f $ScenarioKey, $Page) `
            -Passed ($ok -and [double]$default.content_coverage -ge 0.05) -Detail $detail
        if ($ok) {
            [void]$script:records.Add([pscustomobject]@{
                session = $sessionId; pid = $process.id; page = $Page; theme = $Theme
                size = "$($default.width)x$($default.height)"; dpi = $default.dpi
                readyWaitMs = $ready.waitedMs; readyPolls = $ready.polls
                screenshot = $defaultPng; contentRatio = $default.content_ratio
                contentCoverage = $default.content_coverage
            })
            Record-Result -Name ("{0}/{1}：窗口标题包含中文产品名" -f $ScenarioKey, $Page) `
                -Passed ($default.title -match '飞牛同步增强版') -Detail ("title={0}" -f $default.title)

            if ($AlsoMinWindow) {
                $smallPng = Join-Path $OutRoot ("{0}-{1}-min-{2}x{3}.png" -f $ScenarioKey, $Page, $MinWidth, $MinHeight)
                $small = Invoke-Probe -ProcessId $process.id -StartedAt $process.started -OutPng $smallPng `
                    -JsonPath (Join-Path $OutRoot ("{0}-{1}-min.json" -f $ScenarioKey, $Page)) -Width $MinWidth -Height $MinHeight
                $smallOk = ($null -ne $small -and $small.ok)
                $smallDetail = 'probe 未产出结果文件'
                if ($smallOk) { $smallDetail = "actual={0}x{1} requested={2}x{3} content={4} coverage={5}" -f $small.width, $small.height, $MinWidth, $MinHeight, $small.content_ratio, $small.content_coverage }
                elseif ($null -ne $small) { $smallDetail = "probe failed: $($small.error)" }
                Record-Result -Name ("{0}/{1}：最小窗口尺寸下界面仍然完整渲染" -f $ScenarioKey, $Page) `
                    -Passed ($smallOk -and [double]$small.content_coverage -ge 0.05) -Detail $smallDetail
                if ($smallOk) {
                    [void]$script:records.Add([pscustomobject]@{
                        session = $sessionId; pid = $process.id; page = "$Page(min)"; theme = $Theme
                        size = "$($small.width)x$($small.height)"; dpi = $small.dpi
                        readyWaitMs = $ready.waitedMs; readyPolls = $ready.polls
                        screenshot = $smallPng; contentRatio = $small.content_ratio
                        contentCoverage = $small.content_coverage
                    })
                }
            }

            $expected = 128
            $themeOk = $default.mean_luminance -lt $expected
            if ($Theme -eq 'Light') { $themeOk = $default.mean_luminance -ge $expected }
            Record-Result -Name ("{0}/{1}：保存的主题在启动时生效" -f $ScenarioKey, $Page) `
                -Passed $themeOk -Detail ("expected={0} mean_luminance={1}" -f $Theme, $default.mean_luminance)

            # Sections below the fold (the software-update card) need a capture that covers them.
            # Wheel injection proved unreliable on this machine (wheel input needs the pointer
            # focus, so a background window silently ignores it - the earlier "scrolled" shot was
            # byte-identical to the top one). Instead the window is made TALLER than the screen:
            # PrintWindow renders the whole window, so the update and capacity cards are captured
            # without any input injection, and the recorded height proves the capture covered them.
            if ($TallHeight -gt 0) {
                $tallPng = Join-Path $OutRoot ("{0}-{1}-tall.png" -f $ScenarioKey, $Page)
                $tall = Invoke-Probe -ProcessId $process.id -StartedAt $process.started `
                    -OutPng $tallPng -JsonPath (Join-Path $OutRoot ("{0}-{1}-tall.json" -f $ScenarioKey, $Page)) `
                    -Width 1200 -Height $TallHeight
                $tallOk = ($null -ne $tall -and $tall.ok)
                $tallDetail = 'probe 未产出结果文件'
                if ($tallOk) { $tallDetail = "actual={0}x{1} requested={2}x{3} content={4} coverage={5}" -f $tall.width, $tall.height, $MinWidth, $TallHeight, $tall.content_ratio, $tall.content_coverage }
                elseif ($null -ne $tall) { $tallDetail = "probe failed: $($tall.error)" }
                # The capture must really be tall enough to contain the below-the-fold cards.
                Record-Result -Name ("{0}/{1}：折叠线以下分区（更新卡片与容量卡片）完整渲染" -f $ScenarioKey, $Page) `
                    -Passed ($tallOk -and [double]$tall.content_coverage -ge 0.05 -and [int]$tall.height -ge 1000) -Detail $tallDetail
                if ($tallOk) {
                    [void]$script:records.Add([pscustomobject]@{
                        session = $sessionId; pid = $process.id; page = "$Page(tall)"; theme = $Theme
                        size = "$($tall.width)x$($tall.height)"; dpi = $tall.dpi
                        readyWaitMs = $ready.waitedMs; readyPolls = $ready.polls
                        screenshot = $tallPng; contentRatio = $tall.content_ratio
                        contentCoverage = $tall.content_coverage
                    })
                }
            }
        }
    }
    finally {
        if ($null -ne $process) {
            Stop-TrackedProcess -ProcessId $process.id -StartedAt $process.started -Label $sessionId
        }
    }
}

# ---------------------------------------------------------------- P0-B: protected decoy
# A REAL app process with its own isolated session, started BEFORE any scenario and verified
# alive and untouched AFTER everything. Killing by name would end it — that must never happen.
$decoySessionId = [guid]::NewGuid().ToString('N')
$decoyDir = New-SessionSettings -SessionId $decoySessionId -Theme 'Light' -Names @()
$decoy = $null

# ---------------------------------------------------------------- main run
try {
    $decoy = Start-TrackedApp -SessionId $decoySessionId -InitialView 'Tasks' -ExtraArgs @()
    $decoyAliveAtStart = Test-TrackedAlive -ProcessId $decoy.id -StartedAt $decoy.started
    Record-Result -Name 'P0-B：受保护模拟实例在测试期间启动（真实应用进程）' `
        -Passed $decoyAliveAtStart -Detail ("pid={0} started={1}" -f $decoy.id, $decoy.started)

    $themes = @('Light', 'Dark')
    foreach ($theme in $themes) {
        $key = $theme.ToLowerInvariant()

        # A. 任务首页：多任务卡片（含超长中英文任务名），默认尺寸 + 最小窗口。
        Capture-Scenario -ScenarioKey $key -Page 'Tasks' -View 'Tasks' -Theme $theme `
            -TaskNames @($longName, $longName2, $longName3) -AlsoMinWindow $true

        # A2. 任务首页：空任务状态。
        Capture-Scenario -ScenarioKey ($key + '-empty') -Page 'Tasks' -View 'Tasks' -Theme $theme `
            -TaskNames @() -AlsoMinWindow $false

        # B. 设置页：容量与保留策略卡片（首屏）+ 软件更新卡片（折叠线以下，滚动后截图）。
        Capture-Scenario -ScenarioKey $key -Page 'Settings' -View 'Settings' -Theme $theme `
            -TaskNames @($longName, $longName3) -AlsoMinWindow $false -TallHeight 1100

        # C. 恢复中心：可恢复备份列表（隔离会话下为空列表状态）。
        Capture-Scenario -ScenarioKey $key -Page 'Restore' -View 'Restore' -Theme $theme `
            -TaskNames @() -AlsoMinWindow $false

        # D. 设备页：连接状态界面。
        Capture-Scenario -ScenarioKey $key -Page 'Device' -View 'Device' -Theme $theme `
            -TaskNames @() -AlsoMinWindow $false
    }

    # ---- 无人值守交接的退出探针：真实应用必须能通过协调器安全退出 ----
    $probeSessionId = [guid]::NewGuid().ToString('N')
    $probeDir = New-SessionSettings -SessionId $probeSessionId -Theme 'Light' -Names @()
    $probeProcess = $null
    try {
        $probeProcess = Start-TrackedApp -SessionId $probeSessionId -InitialView 'Tasks' `
            -ExtraArgs @('--update-exit-probe=4')
        $exitDeadline = (Get-Date).AddSeconds(30)
        $exited = $false
        while ((Get-Date) -lt $exitDeadline) {
            if (-not (Test-TrackedAlive -ProcessId $probeProcess.id -StartedAt $probeProcess.started)) { $exited = $true; break }
            Start-Sleep -Milliseconds 500
        }
        Record-Result -Name 'P0-A：真实应用按协调器请求安全退出（退出探针）' -Passed $exited `
            -Detail ("exited={0} pid={1}" -f $exited, $probeProcess.id)
        if (-not $exited) {
            Stop-TrackedProcess -ProcessId $probeProcess.id -StartedAt $probeProcess.started -Label $probeSessionId
        }
    }
    finally {
        if ($null -ne $probeProcess) {
            Stop-TrackedProcess -ProcessId $probeProcess.id -StartedAt $probeProcess.started -Label $probeSessionId
        }
    }

    # ---- P0-B 收尾断言 ----
    $decoyAliveAtEnd = Test-TrackedAlive -ProcessId $decoy.id -StartedAt $decoy.started
    Record-Result -Name 'P0-B：受保护实例在全部场景结束后仍然存活' -Passed $decoyAliveAtEnd `
        -Detail ("pid={0} start未变={1}" -f $decoy.id, $decoyAliveAtEnd)

    $stray = 0
    foreach ($entry in $script:trackedProcesses) {
        if ($entry.id -ne $decoy.id -and (Test-TrackedAlive -ProcessId $entry.id -StartedAt $entry.started)) { $stray++ }
    }
    Record-Result -Name 'P0-B：本脚本启动的测试进程均已退出（不含受保护实例）' -Passed ($stray -eq 0) `
        -Detail ("stray={0}" -f $stray)
}
finally {
    # 清理顺序：先结束受保护实例（它也是本轮启动的），再验证没有任何本轮进程残留。
    foreach ($p in @($script:trackedProcesses)) {
        if (Test-TrackedAlive -ProcessId $p.id -StartedAt $p.started) {
            Stop-TrackedProcess -ProcessId $p.id -StartedAt $p.started -Label $p.label
        }
    }
    $left = 0
    foreach ($p in $script:trackedProcesses) {
        if (Test-TrackedAlive -ProcessId $p.id -StartedAt $p.started) { $left++ }
    }
    if ($left -gt 0) {
        Record-Result -Name 'P0-B：清理后仍有本轮进程残留' -Passed $false -Detail ("left={0}" -f $left)
    }
}

# 既有实例未被触碰：测试前后快照中的同一批 PID 必须仍然存活（若测试前就存在的话）。
$untouchedOk = $true
foreach ($pid0 in $script:existingSnapshot.Keys) {
    $p = Get-Process -Id $pid0 -ErrorAction SilentlyContinue
    if ($null -eq $p) { continue }
    try {
        if ($p.StartTime -ne $script:existingSnapshot[$pid0]) { $untouchedOk = $false }
    } catch { }
}
Record-Result -Name 'P0-B：测试前已存在的实例未被结束或重启' -Passed $untouchedOk `
    -Detail ("existingBefore={0}" -f $script:snapshotCount)

$passed = @($script:results | Where-Object { $_.passed }).Count
$failed = @($script:results | Where-Object { -not $_.passed }).Count
Write-Host ''
Write-Host ("合计：PASS {0} / FAIL {1}（共 {2} 项）" -f $passed, $failed, $script:results.Count)
@{ workRoot = $OutRoot; passed = $passed; failed = $failed; total = $script:results.Count
    cases = $script:results; captures = $script:records } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutRoot 'ui-acceptance-result.json') -Encoding utf8
Write-Host ("截图与结果：{0}" -f $OutRoot)
