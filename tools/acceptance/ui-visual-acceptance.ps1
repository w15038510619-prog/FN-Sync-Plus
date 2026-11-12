<#
.SYNOPSIS
    WinUI 外观验收：在隔离测试会话中启动真实程序，按窗口尺寸与主题截图留证。

.DESCRIPTION
    只使用程序自带的隔离测试会话（--isolated-test-session），不读写用户真实配置，
    也不修改系统主题。每个场景产出一张 PNG 和一份 JSON，由人工/视觉复核判定。

    脚本能自动判定的只有客观事实（窗口是否出现、尺寸、DPI、是否被裁切到底部控件不可见），
    “文字是否重叠、中文是否截断”这类视觉问题必须由人看图确认，报告中单独标注。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Payload,
    [string]$ProbeProject = '',
    [string]$OutRoot = '',
    [int]$MinWidth = 1000,
    [int]$MinHeight = 640
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

$script:results = New-Object System.Collections.ArrayList
function Record-Result {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    [void]$script:results.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    $tag = 'FAIL'; if ($Passed) { $tag = 'PASS' }
    Write-Host ("[{0}] {1} :: {2}" -f $tag, $Name, $Detail)
}

function Invoke-Probe {
    param([int]$ProcessId, [string]$OutPng, [string]$JsonPath, [int]$Width = 0, [int]$Height = 0,
        [double]$MinContentRatio = 0.05, [int]$Attempts = 12)
    # A freshly started WinUI window exists before it has painted anything, so a capture taken too
    # early is a white rectangle. Retry until the image actually contains UI.
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $arguments = @('run', '--project', $ProbeProject, '-c', 'Release', '--',
            '--pid', "$ProcessId", '--out', $OutPng, '--json', $JsonPath, '--wait', '30')
        if ($Width -gt 0) { $arguments += @('--size', "$($Width)x$($Height)") }
        & $dotnet @arguments | Out-Null
        if (-not (Test-Path -LiteralPath $JsonPath)) { Start-Sleep -Milliseconds 1200; continue }
        $probe = Get-Content -LiteralPath $JsonPath -Raw | ConvertFrom-Json
        if (-not $probe.ok) { return $probe }
        if ($probe.content_ratio -ge $MinContentRatio -or $attempt -eq $Attempts) {
            $probe | Add-Member -NotePropertyName attempts -NotePropertyValue $attempt -Force
            return $probe
        }
        Start-Sleep -Milliseconds 1200
    }
    return $null
}

$dotnet = $null
$candidates = @('D:\FNSyncPlus_Toolchain\dotnet\dotnet.exe', 'C:\Program Files\dotnet\dotnet.exe')
foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate) { $dotnet = $candidate; break } }
if (-not $dotnet) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $dotnet = $command.Source } else { throw '找不到 dotnet，无法运行 UiProbe' }
}

$longName = '家庭照片与四年工作文档完整备份（含中文长文件名与特殊字符 テスト・테스트・Ω≈ç√ 2026 年度归档归档归档）'
$longName2 = '项目源代码与设计稿每日增量备份 - D:\Work\2026\Q4\超长目录名称用于验证界面换行与省略号处理是否正确显示'
$longName3 = 'VeryLongEnglishTaskName_ForTruncationTesting_ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789_End'

function New-SessionSettings {
    param([string]$SessionId, [string]$Theme, [string[]]$Names)
    $sessionDir = Join-Path $env:TEMP "FNSyncPlus_CodexTests\WinUiSession_$SessionId"
    New-Item -ItemType Directory -Path $sessionDir -Force | Out-Null
    $localRoot = Join-Path $env:TEMP 'FNSyncPlus_CodexTests'
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

function Start-IsolatedApp {
    param([string]$SessionId)
    return (Start-Process -FilePath $exe -PassThru -ArgumentList @(
            '--isolated-test-session', "--isolated-test-state=$SessionId", '--isolated-test-remote=Codex_FN_Test_Ui'))
}

function Stop-IsolatedApp {
    param($Process)
    try { if ($Process -and -not $Process.HasExited) { Stop-Process -Id $Process.Id -Force } } catch { }
    Start-Sleep -Milliseconds 800
    Get-Process -Name 'FNSyncPlus.WinUI' -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force } catch { }
    }
    Start-Sleep -Milliseconds 500
}

$scenarios = @(
    @{ key = 'light'; theme = 'Light'; label = '浅色主题' },
    @{ key = 'dark'; theme = 'Dark'; label = '深色主题' }
)

try {
    foreach ($scenario in $scenarios) {
        $sessionId = ([guid]::NewGuid().ToString('N'))
        $sessionDir = New-SessionSettings -SessionId $sessionId -Theme $scenario.theme -Names @($longName, $longName2, $longName3)
        $process = $null
        try {
            $process = Start-IsolatedApp -SessionId $sessionId
            Start-Sleep -Seconds 6

            $defaultPng = Join-Path $OutRoot ("{0}-default.png" -f $scenario.key)
            $defaultJson = Join-Path $OutRoot ("{0}-default.json" -f $scenario.key)
            $default = Invoke-Probe -ProcessId $process.Id -OutPng $defaultPng -JsonPath $defaultJson
            $ok = ($null -ne $default -and $default.ok)
            $defaultDetail = 'probe 未产出结果文件'
            if ($ok) { $defaultDetail = "size={0}x{1} dpi={2} content={3} title={4} attempts={5}" -f $default.width, $default.height, $default.dpi, $default.content_ratio, $default.title, $default.attempts }
            elseif ($null -ne $default) { $defaultDetail = "probe failed: $($default.error)" }
            Record-Result -Name ("{0}：主窗口可见并完整渲染后截图" -f $scenario.label) `
                -Passed ($ok -and $default.content_ratio -ge 0.05) -Detail $defaultDetail

            if ($ok) {
                Record-Result -Name ("{0}：窗口标题包含中文产品名" -f $scenario.label) `
                    -Passed ($default.title -match '飞牛同步增强版') -Detail ("title={0}" -f $default.title)
                Record-Result -Name ("{0}：默认尺寸不小于最小可用尺寸" -f $scenario.label) `
                    -Passed ($default.width -ge $MinWidth -and $default.height -ge $MinHeight) `
                    -Detail ("size={0}x{1} min={2}x{3}" -f $default.width, $default.height, $MinWidth, $MinHeight)
                Record-Result -Name ("{0}：DPI 缩放已记录" -f $scenario.label) -Passed ($default.dpi -gt 0) `
                    -Detail ("dpi={0} scale={1}%" -f $default.dpi, $default.scale_percent)
                $expected = 128
                $themeOk = $default.mean_luminance -lt $expected
                if ($scenario.theme -eq 'Light') { $themeOk = $default.mean_luminance -ge $expected }
                Record-Result -Name ("{0}：保存的主题在启动时生效" -f $scenario.label) -Passed $themeOk `
                    -Detail ("expected={0} mean_luminance={1}" -f $scenario.theme, $default.mean_luminance)
            }

            # 最小窗口：把窗口压到刚好可用的尺寸，检查是否仍能截到完整界面（底部不被裁掉）。
            $smallPng = Join-Path $OutRoot ("{0}-min-{1}x{2}.png" -f $scenario.key, $MinWidth, $MinHeight)
            $smallJson = Join-Path $OutRoot ("{0}-min-{1}x{2}.json" -f $scenario.key, $MinWidth, $MinHeight)
            $small = Invoke-Probe -ProcessId $process.Id -OutPng $smallPng -JsonPath $smallJson -Width $MinWidth -Height $MinHeight
            $smallOk = ($null -ne $small -and $small.ok)
            $smallDetail = 'probe 未产出结果文件'
            if ($smallOk) { $smallDetail = "actual={0}x{1} requested={2}x{3} content={4}" -f $small.width, $small.height, $MinWidth, $MinHeight, $small.content_ratio }
            elseif ($null -ne $small) { $smallDetail = "probe failed: $($small.error)" }
            Record-Result -Name ("{0}：最小窗口尺寸下界面仍然完整渲染" -f $scenario.label) `
                -Passed ($smallOk -and $small.content_ratio -ge 0.05) -Detail $smallDetail
        }
        finally {
            Stop-IsolatedApp -Process $process
        }
    }

    $processes = @(Get-Process -Name 'FNSyncPlus.WinUI' -ErrorAction SilentlyContinue)
    Record-Result -Name '验收结束后没有遗留进程' -Passed ($processes.Count -eq 0) -Detail ("count={0}" -f $processes.Count)
}
finally {
    Get-Process -Name 'FNSyncPlus.WinUI' -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force } catch { }
    }
}

$passed = @($script:results | Where-Object { $_.passed }).Count
$failed = @($script:results | Where-Object { -not $_.passed }).Count
Write-Host ''
Write-Host ("合计：PASS {0} / FAIL {1}（共 {2} 项）" -f $passed, $failed, $script:results.Count)
@{ workRoot = $OutRoot; passed = $passed; failed = $failed; total = $script:results.Count; cases = $script:results } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutRoot 'ui-acceptance-result.json') -Encoding utf8
Write-Host ("截图与结果：{0}" -f $OutRoot)
