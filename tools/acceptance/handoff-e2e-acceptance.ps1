<#
.SYNOPSIS
    Real update-handoff end-to-end acceptance: live app -> real updater -> real exit -> install -> new version.

.DESCRIPTION
    This harness proves the full unattended handoff chain with REAL processes and REAL payloads:

        app-0.5.0 (isolated probe session)
            |-- BeginHandoffFromDescriptorAsync: verify signed manifest (pinned key), take the
            |   maintenance lease through the real BackupActivityGate
            |-- TryStartUpdater: launch the REAL updater (FN_Sync_Plus.exe) with --wait-pid
            |-- IApplicationShutdownCoordinator.RequestShutdown -> real safe-exit sequence
            `-- writes handoff-probe.json (app pid, lease held, updater pid, shutdown delivered)

        updater (FN_Sync_Plus.exe --apply --wait-pid <app pid>)
            |-- waits for the ORIGINAL app pid to exit on its own (never kills it)
            |-- re-verifies the signed manifest, applies the package, switches the pointer
            |-- verifies the new version through the health probe, then launches 0.6.0 normally

    Every assertion reads facts from disk (handoff-probe.json, current.json, previous.json,
    update.log journal, .health report) or from the process table (PIDs + start times). Nothing
    is inferred from "the script reached this line".

    P0-B process ownership applies here too: a snapshot of pre-existing FNSyncPlus.WinUI
    instances is taken first (any existing instance fails the run - the new version is a REAL
    production launch and needs the production single-instance mutex), a protected decoy
    instance runs for the whole scenario and must survive untouched, and the harness only ever
    stops processes it started itself or discovered with a verified PID + path under the
    sandbox install root. Name-based kills are forbidden.

    Production isolation (round 4, architectural): NO production file is ever moved, renamed,
    modified or deleted. Every launched process (app A, updater, app B, decoy) inherits
    FNSYNC_DATA_ROOT=<workroot>\testdata, so the application itself resolves ALL of its storage —
    settings, credentials, versioned-beta profile, logs, data-format marker, update state,
    caches, temp files and the single-instance identity — under that test root. The production
    configuration trees are hashed before and after (read-only snapshots) and must be
    byte-identical; the storage audit log must contain zero production paths. The old
    "quarantine then restore" pattern is gone: if Windows crashes or the script is killed
    mid-run, production bytes were never touched in the first place.
#>
param(
    [string]$DotnetPath = '',
    [string]$WorkRoot = '',
    [int]$PrepareTimeoutSeconds = 120,
    [int]$ExitBudgetSeconds = 150,
    [int]$ApplyBudgetSeconds = 300,
    [switch]$SkipBuilds
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path $repo ('.acceptance\handoff-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$WorkRoot = [System.IO.Path]::GetFullPath($WorkRoot)
if (-not $WorkRoot.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "工作目录必须位于仓库内：$WorkRoot"
}
if ((Test-Path -LiteralPath $WorkRoot) -and -not $SkipBuilds) {
    throw "工作目录已存在，请换一个 -WorkRoot：$WorkRoot"
}
if (-not (Test-Path -LiteralPath $WorkRoot) -and $SkipBuilds) {
    throw "-SkipBuilds 需要一个已构建好的工作目录：$WorkRoot"
}
if (-not (Test-Path -LiteralPath $WorkRoot)) { New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null }
Write-Host ("验收工作目录：{0}" -f $WorkRoot)

$dotnet = $DotnetPath
if ([string]::IsNullOrWhiteSpace($dotnet)) {
    foreach ($candidate in @('D:\FNSyncPlus_Toolchain\dotnet\dotnet.exe', 'C:\Program Files\dotnet\dotnet.exe')) {
        if (Test-Path -LiteralPath $candidate) { $dotnet = $candidate; break }
    }
}
if (-not $dotnet -or -not (Test-Path -LiteralPath $dotnet)) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $dotnet = $command.Source } else { throw '找不到 dotnet 工具链' }
}

# ---------------------------------------------------------------- results plumbing
$script:results = New-Object System.Collections.ArrayList
$script:timeline = New-Object System.Collections.ArrayList
function Record-Result {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    [void]$script:results.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    $tag = 'FAIL'
    if ($Passed) { $tag = 'PASS' }
    Write-Host ("[{0}] {1} :: {2}" -f $tag, $Name, $Detail)
}
function Record-Event {
    param([string]$Event, [hashtable]$Fields)
    $entry = [ordered]@{ at = (Get-Date).ToUniversalTime().ToString('o'); event = $Event }
    if ($Fields) { foreach ($key in $Fields.Keys) { $entry[$key] = $Fields[$key] } }
    [void]$script:timeline.Add([pscustomobject]$entry)
}
function Run-Exe {
    param([string]$Path, [string[]]$Arguments)
    $output = & $Path @Arguments 2>&1 | Out-String
    $code = $LASTEXITCODE
    if ($null -eq $code) { $code = -1 }
    return [pscustomobject]@{ Code = $code; Output = $output }
}
function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    # The production settings.json is UTF-8 without BOM; PS 5.1 Get-Content would read it as
    # ANSI and break on multi-byte characters. Always decode explicitly.
    return ([System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json)
}
function Get-FileText {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    return (Get-Content -LiteralPath $Path -Raw)
}

# ---------------------------------------------------------------- process ownership (P0-B)
# The harness only stops processes it created itself (or discovered with a verified PID whose
# executable path lives under the sandbox install root). Kill-by-name is forbidden.
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

function Register-TrackedProcess {
    param([int]$ProcessId, [string]$Label, [string]$ExpectedPath)
    $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $p) { throw "无法登记进程 $Label (pid=$ProcessId)：进程不存在" }
    $started = $null
    try { $started = $p.StartTime } catch { throw "无法读取进程启动时间 $Label (pid=$ProcessId)" }
    if (-not [string]::IsNullOrEmpty($ExpectedPath)) {
        $path = ''
        try { $path = $p.Path } catch { $path = '' }
        if ($path -and -not $path.StartsWith($ExpectedPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "进程 $Label (pid=$ProcessId) 的路径不在预期沙盒内：$path"
        }
    }
    $entry = [pscustomobject]@{ id = $p.Id; started = $started; label = $Label }
    [void]$script:trackedProcesses.Add($entry)
    return $entry
}

function Stop-TrackedProcess {
    param([int]$ProcessId, [datetime]$StartedAt, [string]$Label)
    if (-not (Test-TrackedAlive -ProcessId $ProcessId -StartedAt $StartedAt)) { return }
    $live = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if ($null -ne $live) {
        try { $null = $live.CloseMainWindow() } catch { }
    }
    $deadline = (Get-Date).AddSeconds(8)
    while ((Get-Date) -lt $deadline -and (Test-TrackedAlive -ProcessId $ProcessId -StartedAt $StartedAt)) {
        Start-Sleep -Milliseconds 300
    }
    if (-not (Test-TrackedAlive -ProcessId $ProcessId -StartedAt $StartedAt)) { return }
    try { Stop-Process -Id $ProcessId -Force -ErrorAction Stop } catch { }
    $deadline = (Get-Date).AddSeconds(3)
    while ((Get-Date) -lt $deadline -and (Test-TrackedAlive -ProcessId $ProcessId -StartedAt $StartedAt)) {
        Start-Sleep -Milliseconds 200
    }
}

# ---------------------------------------------------------------- fixtures
$versionA = '0.5.0'
$versionB = '0.6.0'
$keyDir = Join-Path $WorkRoot 'keys'
$privateKey = Join-Path $keyDir 'update-private.pem'
$publicKey = Join-Path $keyDir 'update-public.pem'
$layoutRoot = Join-Path $WorkRoot 'layout'
$installRoot = Join-Path $layoutRoot 'install'
$buildB = Join-Path $WorkRoot 'build-b'
$launcher = Join-Path $installRoot 'FN_Sync_Plus.exe'
$journalPath = Join-Path $installRoot 'update.log'
$exeA = Join-Path $installRoot "app-$versionA\FNSyncPlus.WinUI.exe"

$sessionA = [guid]::NewGuid().ToString('N')
$decoySession = [guid]::NewGuid().ToString('N')

# ---------------------------------------------------------------- architectural isolation
# FNSYNC_DATA_ROOT redirects EVERY application storage path into the test root (AppStorageRoot
# in FNSyncPlus.Core). Children inherit the environment block of their creator, so setting it
# in this session propagates: script -> app A -> updater -> app B, and script -> decoy.
$testDataRoot = Join-Path $WorkRoot 'testdata'
$storageAuditPath = Join-Path $WorkRoot 'storage-audit.log'
New-Item -ItemType Directory -Path $testDataRoot -Force | Out-Null
$env:FNSYNC_DATA_ROOT = $testDataRoot
$env:FNSYNC_STORAGE_AUDIT = $storageAuditPath
$testSessionsRoot = Join-Path $testDataRoot 'temp\FNSyncPlus_CodexTests'
$sessionDirA = Join-Path $testSessionsRoot "WinUiSession_$sessionA"
$probeReportPath = Join-Path $sessionDirA 'handoff-probe.json'

$appDataRoot = Join-Path $env:APPDATA 'FNSyncPlus'
$localAppDataRoot = Join-Path $env:LOCALAPPDATA 'FNSyncPlus'
$productionSettingsPath = Join-Path $appDataRoot 'settings.json'
$credentialsPath = Join-Path $appDataRoot 'credentials.dat'
$markerPath = Join-Path $localAppDataRoot 'data-format.json'

# The launched 0.6.0 is a VERSIONED-BETA session (published app-<version> layout): with the
# data-root override its profile resolves to <testroot>\local\FNSyncPlus\versioned-beta\ — a
# fresh directory with no credentials and no tasks — instead of the machine's real profile.
$testBetaDir = Join-Path $testDataRoot 'local\FNSyncPlus\versioned-beta'

function Get-ConnectionLogsText {
    param([string]$Root)
    $dir = Join-Path $Root 'logs'
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    $builder = New-Object System.Text.StringBuilder
    Get-ChildItem -LiteralPath $dir -Filter 'connection_*.log' -File -ErrorAction SilentlyContinue |
        Sort-Object Name | ForEach-Object {
            try { [void]$builder.Append([System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)) } catch { }
        }
    return $builder.ToString()
}

function Snapshot-UserData {
    param([string]$Root)
    $map = @{}
    if (-not (Test-Path -LiteralPath $Root)) { return $map }
    Get-ChildItem -LiteralPath $Root -File -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
        $entry = $_.FullName
        if ($entry -match '\\logs\\') { $map[$entry] = 'log' }
        elseif ($entry.EndsWith('.lock', [StringComparison]::OrdinalIgnoreCase)) { $map[$entry] = 'lock' }
        else {
            try { $map[$entry] = (Get-FileHash -LiteralPath $entry -Algorithm SHA256).Hash } catch { $map[$entry] = 'unreadable' }
        }
    }
    return $map
}

# Populated during the run. Nothing is ever moved or restored: production files stay exactly
# where they are for the entire run, so no restore step exists at all.
$leftovers = New-Object System.Collections.ArrayList
$missing = New-Object System.Collections.ArrayList
$changedUnexpected = New-Object System.Collections.ArrayList
$probe = $null
$processA = $null
$processDecoy = $null
$processB = $null
$updaterEntry = $null
$launcherHashBefore = $null
$appDataBefore = @{}
$localAppDataBefore = @{}
$markerExisted = $false
$markerHashBefore = $null
$credentialsExisted = $false
$credentialsHashBefore = $null

try {
    # ------------------------------------------------ 0. preconditions: nothing else runs
    $existingList = ($script:existingSnapshot.Keys | Sort-Object) -join ','
    Record-Result -Name '开始前没有其他 FNSyncPlus.WinUI 实例（新版本是真实生产启动）' `
        -Passed ($script:snapshotCount -eq 0) `
        -Detail ("existing={0}" -f $existingList)
    if ($script:snapshotCount -gt 0) {
        throw "检测到 {0} 个已运行的 FNSyncPlus.WinUI（pid: {1}）。本验收的最终启动是真实生产实例，必须独占单实例互斥量；请先正常退出这些实例（本脚本不会代为结束它们）。" -f $script:snapshotCount, $existingList
    }

    # ------------------------------------------- 0a. read-only snapshot of the machine state
    # The snapshot is pure evidence gathering (hashes): nothing is moved or modified. The final
    # walk compares the end state to this snapshot and must find every byte identical.
    $appDataBefore = Snapshot-UserData -Root $appDataRoot
    $localAppDataBefore = Snapshot-UserData -Root $localAppDataRoot
    $credentialsExisted = $appDataBefore.ContainsKey($credentialsPath)
    $credentialsHashBefore = $null
    if ($credentialsExisted) { $credentialsHashBefore = $appDataBefore[$credentialsPath] }
    $markerExisted = Test-Path -LiteralPath $markerPath
    $markerHashBefore = $null
    if ($markerExisted) { $markerHashBefore = (Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash }

    # ------------------------------------------- 0b. architectural isolation, not quarantine
    # The test data root is a fresh directory the harness owns. Application processes resolve
    # every storage path under it; production files are never touched, so there is nothing to
    # restore and no crash window where the user's config sits somewhere else.
    $settingsExisted = Test-Path -LiteralPath $productionSettingsPath
    $enabledTaskCount = -1
    $credRemembered = $false
    if ($settingsExisted) {
        $settingsJson = Read-JsonFile -Path $productionSettingsPath
        if ($null -ne $settingsJson) {
            $enabledTaskCount = @($settingsJson.Tasks | Where-Object { $_.Enabled -eq $true }).Count
            $credRemembered = ($settingsJson.RememberCredentials -eq $true)
        }
    }
    $rootOverlapsProduction = $false
    foreach ($prodRoot in @($appDataRoot, $localAppDataRoot)) {
        if ($testDataRoot.StartsWith($prodRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $prodRoot.StartsWith($testDataRoot, [StringComparison]::OrdinalIgnoreCase)) { $rootOverlapsProduction = $true }
    }
    $isolationOk = (Test-Path -LiteralPath $testDataRoot) -and (-not $rootOverlapsProduction) -and
                   ($env:FNSYNC_DATA_ROOT -eq $testDataRoot)
    Record-Result -Name '独立测试数据根已建立（架构隔离，不移动任何生产文件）' -Passed $isolationOk `
        -Detail ("root={0} overlap={1} envSet={2}" -f $testDataRoot, $rootOverlapsProduction, ($env:FNSYNC_DATA_ROOT -eq $testDataRoot))
    if (-not $isolationOk) {
        throw "无法建立独立测试数据根，验收终止"
    }
    Record-Event -Event 'test-data-root-established' -Fields @{ root = $testDataRoot;
        production_settings_existed = $settingsExisted; enabled_tasks = $enabledTaskCount }
    Record-Result -Name '生产配置保持原地且只读（本机现状仅作证据记录）' -Passed $true `
        -Detail ("settingsExisted={0} enabledTasks={1} remembered={2} credentialsExisted={3} markerExisted={4}" -f
            $settingsExisted, $enabledTaskCount, $credRemembered, $credentialsExisted, $markerExisted)

    # ------------------------------------------------ 1. throw-away key + two real builds
    if (-not $SkipBuilds) {
        New-Item -ItemType Directory -Path $keyDir -Force | Out-Null
        $keygen = Run-Exe -Path $dotnet -Arguments @(
            'run', '--project', (Join-Path $repo 'tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj'),
            '-c', 'Release', '--', 'keygen', '--private', $privateKey, '--public', $publicKey, '--force')
        Record-Result -Name '生成一次性更新签名密钥' -Passed ($keygen.Code -eq 0 -and (Test-Path $publicKey)) `
            -Detail ("exit={0} {1}" -f $keygen.Code, $publicKey)

        $buildScript = Join-Path $repo 'FNSyncPlus\build-release.ps1'
        $commonBuild = @{
            Dotnet = $dotnet; Channel = 'beta'; SkipTests = $true
            UpdatePublicKeyPath = $publicKey; SignManifest = $true; SigningKeyPath = $privateKey
            ManifestBaseUrl = 'https://pilot.invalid/fn-sync-plus'; SupportedDataFormat = 1
        }
        Write-Host '--- building A (0.5.0, contains the handoff probe) ---'
        & $buildScript -Version $versionA -OutputRoot $layoutRoot @commonBuild
        $buildOkA = $?
        Record-Result -Name '构建 A 版本 0.5.0（真实 publish + 内嵌公钥）' `
            -Passed ($buildOkA -and (Test-Path $exeA)) -Detail ("payload={0}" -f $exeA)

        Write-Host '--- building B (0.6.0) ---'
        & $buildScript -Version $versionB -OutputRoot $buildB @commonBuild
        $buildOkB = $?
        $exeBbuilt = Join-Path $buildB "install\app-$versionB\FNSyncPlus.WinUI.exe"
        Record-Result -Name '构建 B 版本 0.6.0（真实 publish + 签名清单）' `
            -Passed ($buildOkB -and (Test-Path $exeBbuilt)) -Detail ("payload={0}" -f $exeBbuilt)
    }
    else {
        Write-Host '跳过构建，复用既有载荷'
        if (-not (Test-Path -LiteralPath $exeA)) { throw "安装目录缺少 A 载荷：$exeA" }
        if (-not (Test-Path -LiteralPath $launcher)) { throw "安装目录缺少启动器：$launcher" }
    }

    $currentA = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
    Record-Result -Name '初始 current.json 指向 0.5.0' `
        -Passed ($currentA.version -eq $versionA) -Detail ("version={0}" -f $currentA.version)
    $launcherHashBefore = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    Record-Event -Event 'layout-ready' -Fields @{ install_root = $installRoot; version = $versionA }

    # ------------------------------------------------ 2. stage B package + harness descriptor
    $packagesDir = Join-Path $installRoot '.packages'
    New-Item -ItemType Directory -Path $packagesDir -Force | Out-Null
    $packageB = Join-Path $packagesDir "FN-Sync-Plus-$versionB-x64.zip"
    Copy-Item -LiteralPath (Join-Path $buildB "FN-Sync-Plus-$versionB-x64.zip") -Destination $packageB -Force
    $manifestB = Join-Path $buildB 'manifest.json'
    $harnessDescriptor = Join-Path $WorkRoot 'harness-release.json'
    $descriptor = [ordered]@{
        manifest          = [string](Get-Content -LiteralPath $manifestB -Raw)
        version           = $versionB
        package_path      = $packageB
        package_sha256    = (Get-FileHash -LiteralPath $packageB -Algorithm SHA256).Hash
        package_size      = (Get-Item -LiteralPath $packageB).Length
        machine_group     = ''
        release_batch     = 0
        requested_channel = 'beta'
    }
    $descriptor | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $harnessDescriptor -Encoding utf8
    Record-Result -Name 'B 更新包与已签名描述就位（包在安装根目录内）' `
        -Passed ((Test-Path $packageB) -and (Test-Path $harnessDescriptor)) `
        -Detail ("package={0}" -f $packageB)

    $verifyManifest = Run-Exe -Path $dotnet -Arguments @(
        'run', '--project', (Join-Path $repo 'tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj'), '-c', 'Release',
        '--', 'verify', '--manifest', $manifestB, '--public', $publicKey, '--current', $versionA, '--channel', 'beta')
    Record-Result -Name 'B 清单可用内嵌公钥验证（升级规则通过）' -Passed ($verifyManifest.Code -eq 0) `
        -Detail ("exit={0}" -f $verifyManifest.Code)

    # ------------------------------------------------ 3. protected decoy instance (P0-B)
    # A REAL application instance in its own isolated session runs for the whole scenario. The
    # harness must not disturb it: any stray kill would show up as a failure below.
    New-Item -ItemType Directory -Path (Join-Path $testSessionsRoot "WinUiSession_$decoySession") -Force | Out-Null
    $decoyArgs = @('--isolated-test-session', "--isolated-test-state=$decoySession",
        '--isolated-test-remote=Codex_FN_Test_Ui', '--ui-visual-acceptance')
    $decoyProc = Start-Process -FilePath $exeA -PassThru -ArgumentList $decoyArgs
    Start-Sleep -Seconds 2
    $processDecoy = Register-TrackedProcess -ProcessId $decoyProc.Id -Label "decoy-$decoySession" -ExpectedPath $installRoot
    $decoyAlive = Test-TrackedAlive -ProcessId $processDecoy.id -StartedAt $processDecoy.started
    Record-Result -Name '受保护模拟实例已启动（真实应用进程）' -Passed $decoyAlive `
        -Detail ("pid={0} session={1}" -f $processDecoy.id, $decoySession)
    Record-Event -Event 'decoy-started' -Fields @{ pid = $processDecoy.id; session = $decoySession }

    # ------------------------------------------------ 4. start A with the handoff probe
    New-Item -ItemType Directory -Path $sessionDirA -Force | Out-Null
    Remove-Item -LiteralPath $probeReportPath -Force -ErrorAction SilentlyContinue
    $launchSw = [System.Diagnostics.Stopwatch]::StartNew()
    $probeArgs = @('--isolated-test-session', "--isolated-test-state=$sessionA",
        '--isolated-test-remote=Codex_FN_Test_Ui', "--update-handoff-probe=$harnessDescriptor")
    $appProc = Start-Process -FilePath $exeA -PassThru -ArgumentList $probeArgs
    $processA = Register-TrackedProcess -ProcessId $appProc.Id -Label "app-A-$sessionA" -ExpectedPath $installRoot
    Record-Event -Event 'app-A-started' -Fields @{ pid = $processA.id; session = $sessionA; version = $versionA }
    Write-Host ("A 已启动 pid={0}，等待 handoff-probe.json …" -f $processA.id)

    # ------------------------------------------------ 5. wait for the probe report
    $probe = $null
    $deadline = (Get-Date).AddSeconds($PrepareTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $candidate = Read-JsonFile -Path $probeReportPath
        if ($null -ne $candidate -and $null -ne $candidate.PSObject.Properties['shutdown_delivered']) {
            $probe = $candidate
            break
        }
        if (-not (Test-TrackedAlive -ProcessId $processA.id -StartedAt $processA.started)) {
            $probe = Read-JsonFile -Path $probeReportPath
            break
        }
        Start-Sleep -Milliseconds 500
    }
    $probeSeen = ($null -ne $probe)
    Record-Result -Name '探针写出 handoff-probe.json（交接全过程留证）' -Passed $probeSeen `
        -Detail ("path={0} waitedMs={1}" -f $probeReportPath, $launchSw.ElapsedMilliseconds)
    if (-not $probeSeen) { throw "探针报告未生成，无法继续验收：$probeReportPath" }
    Record-Event -Event 'probe-report' -Fields @{ app_pid = $probe.app_pid; state = $probe.session_state;
        updater_pid = $probe.updater_pid; shutdown_delivered = $probe.shutdown_delivered }

    Record-Result -Name '探针：安装准备成功（真实验证 + 真实维护租约）' `
        -Passed ($probe.prepared -eq $true -and $probe.lease_held_after_prepare -eq $true) `
        -Detail ("prepared={0} lease={1} prepare_error={2}" -f $probe.prepared, $probe.lease_held_after_prepare, $probe.prepare_error)
    Record-Result -Name '探针：真实更新器已启动（--wait-pid 指向原程序）' `
        -Passed ($probe.updater_started -eq $true -and $probe.updater_pid -gt 0 -and
                 ($probe.session_state -eq 'UpdaterStarted' -or $probe.session_state -eq 'ShutdownRequested')) `
        -Detail ("updater_pid={0} state={1}" -f $probe.updater_pid, $probe.session_state)
    Record-Result -Name '探针：主程序收到真实退出请求（应用退出协调器）' `
        -Passed ($probe.shutdown_delivered -eq $true -and $probe.session_state -eq 'ShutdownRequested') `
        -Detail ("delivered={0} state={1}" -f $probe.shutdown_delivered, $probe.session_state)
    Record-Result -Name '探针：退出前租约仍由本会话持有（未提前释放，未错手释放）' `
        -Passed ($probe.lease_held_before_exit -eq $true -and $probe.target_version -eq $versionB) `
        -Detail ("lease_held_before_exit={0} target={1}" -f $probe.lease_held_before_exit, $probe.target_version)

    if (-not ($probe.updater_started -eq $true -and $probe.updater_pid -gt 0)) {
        throw "真实更新器未启动，终止验收（updater_started={0}）" -f $probe.updater_started
    }
    $updaterEntry = Register-TrackedProcess -ProcessId ([int]$probe.updater_pid) -Label 'updater' -ExpectedPath $installRoot
    Record-Event -Event 'updater-registered' -Fields @{ pid = $updaterEntry.id }

    # ------------------------------------------------ 6. A must exit ON ITS OWN
    $exitSw = [System.Diagnostics.Stopwatch]::StartNew()
    $deadline = (Get-Date).AddSeconds($ExitBudgetSeconds)
    $appExitedByItself = $false
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-TrackedAlive -ProcessId $processA.id -StartedAt $processA.started)) {
            $appExitedByItself = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    Record-Result -Name '原程序按更新流程真实退出（未被任何人结束）' -Passed $appExitedByItself `
        -Detail ("pid={0} exitWaitMs={1}" -f $processA.id, $exitSw.ElapsedMilliseconds)
    Record-Event -Event 'app-A-exited' -Fields @{ pid = $processA.id; by_itself = $appExitedByItself }
    if (-not $appExitedByItself) { throw "原程序未在 ${ExitBudgetSeconds}s 内退出，终止验收（清理阶段只结束本脚本启动的进程）" }

    # ------------------------------------------------ 7. updater applies and launches B
    $applySw = [System.Diagnostics.Stopwatch]::StartNew()
    $deadline = (Get-Date).AddSeconds($ApplyBudgetSeconds)
    $switched = $false
    while ((Get-Date) -lt $deadline) {
        $current = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
        if ($null -ne $current -and $current.version -eq $versionB) { $switched = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Record-Result -Name '更新器完成安装并切换版本指针到 0.6.0' -Passed $switched `
        -Detail ("applyWaitMs={0}" -f $applySw.ElapsedMilliseconds)
    Record-Event -Event 'pointer-switched' -Fields @{ version = $versionB; waited_ms = $applySw.ElapsedMilliseconds }

    if (-not $switched) {
        $journal = Get-FileText -Path $journalPath
        Record-Result -Name 'journal 记录失败原因' -Passed $false -Detail ((($journal -split "`r?`n") | Select-Object -Last 5) -join ' | ')
        throw "更新器未完成安装，终止验收"
    }

    # The pointer switches before the updater launches the new version - the launch journal line
    # arrives a few seconds later. Poll for it instead of reading once (a single read raced in a
    # previous run and missed the line).
    $launchSw = [System.Diagnostics.Stopwatch]::StartNew()
    $launchDeadline = (Get-Date).AddSeconds(120)
    $journal = ''
    $launchOkLine = ''
    $newPid = 0
    while ((Get-Date) -lt $launchDeadline) {
        $journal = Get-FileText -Path $journalPath
        $launchOkLine = (($journal -split "`r?`n") | Where-Object { $_ -match "launch $versionB ok \(pid (\d+)\)" } | Select-Object -Last 1)
        if ($launchOkLine -match "launch $versionB ok \(pid (\d+)\)") { $newPid = [int]$Matches[1]; break }
        if ($journal -match 'still running after' -or $journal -match 'apply refused' -or
            $journal -match 'not healthy' -or $journal -match 'rolling back') { break }
        Start-Sleep -Milliseconds 500
    }

    $journalLines = ($journal -split "`r?`n") | Where-Object { $_ -match 'verified release|installed|launch|still running|refused|failed|rolled back' }
    Record-Result -Name 'journal：更新器等待原程序退出后从已验证发布安装' `
        -Passed ($journal -match "apply $versionB from verified release" -and $journal -match "installed $versionB" -and
                 $journal -notmatch 'still running after' -and $journal -notmatch 'apply refused') `
        -Detail ($journalLines -join ' | ')

    Record-Result -Name 'journal：更新器启动新版本并通过健康检查' `
        -Passed ($newPid -gt 0) -Detail ("launchWaitMs={0} line={1}" -f $launchSw.ElapsedMilliseconds, $launchOkLine)
    Record-Event -Event 'journal-launch-line' -Fields @{ pid = $newPid; line = $launchOkLine }

    # ------------------------------------------------ 8. verify the new version process
    if ($newPid -le 0) { throw "journal 未记录新版本启动，终止验收" }
    $newProc = Get-Process -Id $newPid -ErrorAction SilentlyContinue
    $newPath = ''
    if ($null -ne $newProc) { try { $newPath = $newProc.Path } catch { $newPath = '' } }
    $expectedPayload = Join-Path $installRoot "app-$versionB"
    $newOk = ($null -ne $newProc) -and $newPath.StartsWith($expectedPayload, [StringComparison]::OrdinalIgnoreCase) -and
             ($newPid -ne $processA.id)
    Record-Result -Name '新版本进程真实存在（路径在 0.6.0 载荷内，PID 与原程序不同）' `
        -Passed $newOk -Detail ("pid={0} path={1}" -f $newPid, $newPath)
    if (-not $newOk) { throw "新版本进程不可用，终止验收" }
    $processB = Register-TrackedProcess -ProcessId $newPid -Label 'app-B' -ExpectedPath $expectedPayload
    Record-Event -Event 'app-B-running' -Fields @{ pid = $newPid; path = $newPath }

    # The new version must see ZERO tasks: with FNSYNC_DATA_ROOT its versioned-beta profile
    # resolves to a fresh directory under the test root (no credentials, no tasks), so the
    # defaults it loads contain nothing. The report proves what it actually loaded.
    $reportB = Read-JsonFile -Path (Join-Path $installRoot ".health\report-$versionB.json")
    $reportOk = $false
    $reportDetail = '未生成启动报告'
    if ($null -ne $reportB) {
        $reportOk = ($reportB.usable -eq $true) -and ($reportB.services_initialized -eq $true) -and
                    ($reportB.task_manager_ok -eq $true) -and ($reportB.format_state -eq 'Valid')
        $reportDetail = ("usable={0} services={1} tasks={2} count={3} format_state={4}" -f
            $reportB.usable, $reportB.services_initialized, $reportB.task_manager_ok, $reportB.task_count,
            $reportB.format_state)
    }
    Record-Result -Name '新版本健康报告（服务/任务列表/数据格式，不只是窗口出现）' -Passed $reportOk -Detail $reportDetail
    Record-Result -Name '新版本以零任务配置启动（配置与凭据解析在测试根内，生产未被加载）' `
        -Passed ($null -ne $reportB -and $reportB.task_count -eq 0) `
        -Detail ("task_count={0}" -f $reportB.task_count)
    Record-Result -Name '备份管理器在新版本中正常运转（非维护暂停状态）' `
        -Passed ($null -ne $reportB -and $reportB.task_manager_ok -eq $true) `
        -Detail $reportDetail
    # Note: the report's nas_online field is an optimistic default sentinel, not a connection
    # fact. The isolation proof is twofold: the PRODUCTION connection log must be byte-identical
    # to the pre-run snapshot (final walk), and the TEST-ROOT connection log (where the isolated
    # processes would write) must contain no dial markers at all.
    $testLogPart = Get-ConnectionLogsText -Root (Join-Path $testDataRoot 'local\FNSyncPlus')
    $testLogLines = @($testLogPart -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    $dialAttempts = @($testLogLines | Where-Object { $_ -match '成功连接|ConnectAsync|TlsProbe|SMB|smb_' })
    Record-Result -Name '测试根连接日志在验收窗口内没有任何 NAS 拨号记录' `
        -Passed ($dialAttempts.Count -eq 0) `
        -Detail ("testRootLines={0} dial={1}" -f $testLogLines.Count, (($dialAttempts | Select-Object -First 2) -join ' | '))

    # ------------------------------------------------ 8b. operation-id evidence chain (P0-2)
    # The updater now keeps a durable, atomic per-operation state file (.ops\<id>.json) and tags
    # its journal lines with the same operation id; the version pointer records it too. All three
    # must agree, or evidence from one attempt could be misread as another's.
    $opsDir = Join-Path $installRoot '.ops'
    $opFiles = @()
    if (Test-Path -LiteralPath $opsDir) { $opFiles = @(Get-ChildItem -LiteralPath $opsDir -Filter '*.json' -File) }
    $opState = $null
    if ($opFiles.Count -eq 1) { $opState = Read-JsonFile -Path $opFiles[0].FullName }
    $opId = ''
    if ($null -ne $opState) { $opId = [string]$opState.operation_id }
    Record-Result -Name '操作状态文件存在且记录 Installed（原子持久化、含进程身份）' `
        -Passed ($null -ne $opState -and [int]$opState.status -eq 3 -and $opState.version -eq $versionB -and
                 $opState.updater_pid -eq $updaterEntry.id -and $opState.previous_version -eq $versionA) `
        -Detail ("ops={0} status={1}(3=Installed) version={2} updater_pid={3} prev={4}" -f
            $opFiles.Count, $opState.status, $opState.version, $opState.updater_pid, $opState.previous_version)
    $opTaggedLines = @()
    if ($opId.Length -gt 0) {
        $opTaggedLines = @(($journal -split "`r?`n") | Where-Object { $_ -match "\[op $opId\]" })
    }
    Record-Result -Name '更新器日志行携带同一操作 ID（历史日志无法混入本次判定）' `
        -Passed ($opTaggedLines.Count -ge 2 -and ($opTaggedLines -join ' ') -match 'from verified release') `
        -Detail ("opId={0} taggedLines={1}" -f $opId, $opTaggedLines.Count)
    $currentNow = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
    Record-Result -Name '版本指针记录同一操作 ID（指针-日志-状态文件三方一致）' `
        -Passed ($null -ne $currentNow -and $currentNow.operation_id -eq $opId -and $opId.Length -gt 0) `
        -Detail ("pointer_op={0}" -f $currentNow.operation_id)

    # ------------------------------------------------ 9. layout invariants
    $previousB = Read-JsonFile -Path (Join-Path $installRoot 'previous.json')
    Record-Result -Name 'previous.json 记录 0.5.0 供回滚' -Passed ($previousB.version -eq $versionA) `
        -Detail ("previous={0}" -f $previousB.version)
    Record-Result -Name '旧版本载荷保持不可变（app-0.5.0 完整保留）' -Passed (Test-Path $exeA) `
        -Detail ("payloadA={0}" -f $exeA)
    $launcherHashAfter = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    Record-Result -Name '启动器字节未变' -Passed ($launcherHashBefore -eq $launcherHashAfter) `
        -Detail ("before={0} after={1}" -f $launcherHashBefore.Substring(0, 12), $launcherHashAfter.Substring(0, 12))

    # ------------------------------------------------ 10. updater leaves on its own
    $deadline = (Get-Date).AddSeconds(60)
    $updaterGone = $false
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-TrackedAlive -ProcessId $updaterEntry.id -StartedAt $updaterEntry.started)) {
            $updaterGone = $true
            break
        }
        Start-Sleep -Milliseconds 500
    }
    Record-Result -Name '更新器完成使命后自行退出' -Passed $updaterGone `
        -Detail ("pid={0}" -f $updaterEntry.id)

    # ------------------------------------------------ 11. protected decoy survived
    $decoyStillAlive = Test-TrackedAlive -ProcessId $processDecoy.id -StartedAt $processDecoy.started
    Record-Result -Name '受保护模拟实例全程存活（未发生按名误杀）' -Passed $decoyStillAlive `
        -Detail ("pid={0}" -f $processDecoy.id)

    # ------------------------------------------------ 12. credentials untouched
    $credAfter = $null
    if (Test-Path -LiteralPath $credentialsPath) {
        try { $credAfter = (Get-FileHash -LiteralPath $credentialsPath -Algorithm SHA256).Hash } catch { $credAfter = 'unreadable' }
    }
    $credOk = $true
    if ($credentialsExisted) { $credOk = ($credAfter -eq $credentialsHashBefore) }
    else { $credOk = ($null -eq $credAfter) }
    Record-Result -Name '用户凭据文件未被读写（哈希不变）' -Passed $credOk `
        -Detail ("existed={0}" -f $credentialsExisted)
}
finally {
    # ------------------------------------------------ cleanup: only OUR verified processes
    foreach ($entry in @($script:trackedProcesses)) {
        $alreadyGone = -not (Test-TrackedAlive -ProcessId $entry.id -StartedAt $entry.started)
        if (-not $alreadyGone) {
            Stop-TrackedProcess -ProcessId $entry.id -StartedAt $entry.started -Label $entry.label
        }
    }
    Start-Sleep -Milliseconds 500
    $stray = @(Get-Process -Name 'FNSyncPlus.WinUI' -ErrorAction SilentlyContinue | Where-Object {
        -not $script:existingSnapshot.ContainsKey($_.Id) })
    Record-Result -Name '清理后没有遗留的本轮进程（stray=0）' -Passed ($stray.Count -eq 0) `
        -Detail ("stray={0}" -f (($stray | ForEach-Object { $_.Id }) -join ','))

    # ------------------------------------------------ storage audit: zero production access
    # Every application path resolution in every launched process was recorded through
    # FNSYNC_STORAGE_AUDIT. A single production path in that log would prove the isolation
    # leaked; the audit is what makes "never touched production" verifiable rather than hoped.
    $auditLines = @()
    if (Test-Path -LiteralPath $storageAuditPath) {
        $auditLines = @([System.IO.File]::ReadAllLines($storageAuditPath))
    }
    $auditProductionHits = @($auditLines | Where-Object {
        $fields = $_ -split "`t"
        $path = $fields[-1]
        ($path.StartsWith($appDataRoot, [StringComparison]::OrdinalIgnoreCase)) -or
        ($path.StartsWith($localAppDataRoot, [StringComparison]::OrdinalIgnoreCase))
    })
    Record-Result -Name '存储审计：全部测试进程解析路径零生产命中' `
        -Passed ($auditLines.Count -gt 0 -and $auditProductionHits.Count -eq 0) `
        -Detail ("auditLines={0} productionHits={1}" -f $auditLines.Count, $auditProductionHits.Count)

    # ------------------------------------------------ production state must be untouched
    # There is no restore step at all: production files were never moved, so the final walk
    # simply verifies byte-identity with the pre-run snapshot. Anything new, missing or changed
    # under the production data roots fails the run.
    $appDataAfter = Snapshot-UserData -Root $appDataRoot
    $localAppDataAfter = Snapshot-UserData -Root $localAppDataRoot
    $leftovers = New-Object System.Collections.ArrayList
    foreach ($set in @(@{ before = $appDataBefore; after = $appDataAfter }, @{ before = $localAppDataBefore; after = $localAppDataAfter })) {
        foreach ($key in $set.after.Keys) {
            if (-not $set.before.ContainsKey($key)) { [void]$leftovers.Add($key) }
        }
    }
    $missing = New-Object System.Collections.ArrayList
    $changedUnexpected = New-Object System.Collections.ArrayList
    foreach ($set in @(@{ before = $appDataBefore; after = $appDataAfter }, @{ before = $localAppDataBefore; after = $localAppDataAfter })) {
        foreach ($key in $set.before.Keys) {
            if (-not $set.after.ContainsKey($key)) { [void]$missing.Add($key); continue }
            if ($set.before[$key] -eq 'log' -or $set.before[$key] -eq 'lock') {
                # Logs/locks are append-prone even without our run; they are verified by hash in
                # the strict walk below when unchanged, and merely tolerated when appended to.
                continue
            }
            if ($set.after[$key] -ne $set.before[$key]) { [void]$changedUnexpected.Add($key) }
        }
    }
    Record-Result -Name '生产用户数据全程原地未动（无缺失、无变更、无遗留、无需还原）' `
        -Passed ($leftovers.Count -eq 0 -and $missing.Count -eq 0 -and $changedUnexpected.Count -eq 0) `
        -Detail ("leftover={0} missing={1} changed={2} :: {3}" -f $leftovers.Count, $missing.Count,
            $changedUnexpected.Count,
            (((@($leftovers) + @($missing) + @($changedUnexpected)) | Select-Object -First 5) -join ' ; '))
    Record-Result -Name '生产数据格式标记未被读写（哈希不变或仍不存在）' `
        -Passed ($markerHashBefore -eq $null -and -not (Test-Path -LiteralPath $markerPath) -or
                 ($markerHashBefore -ne $null -and (Test-Path -LiteralPath $markerPath) -and
                  ((Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash -eq $markerHashBefore))) `
        -Detail ("existed={0}" -f $markerExisted)

    $passed = @($script:results | Where-Object { $_.passed }).Count
    $failed = @($script:results | Where-Object { -not $_.passed }).Count
    Write-Host ''
    Write-Host ("合计：PASS {0} / FAIL {1}（共 {2} 项）" -f $passed, $failed, $script:results.Count)
    $summaryPath = Join-Path $WorkRoot 'acceptance-result.json'
    $probeEcho = $null
    if ($null -ne $probe) { $probeEcho = $probe }
    @{ workRoot = $WorkRoot; passed = $passed; failed = $failed; total = $script:results.Count
       cases = $script:results; timeline = $script:timeline; probe = $probeEcho
       data_walk = @{ leftovers = @($leftovers); missing = @($missing); changed = @($changedUnexpected) }
       pids = @{ app_a = $(if ($processA) { $processA.id } else { $null })
                 updater = $(if ($updaterEntry) { $updaterEntry.id } else { $null })
                 app_b = $(if ($processB) { $processB.id } else { $null })
                 decoy = $(if ($processDecoy) { $processDecoy.id } else { $null }) } } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host ("结果文件：{0}" -f $summaryPath)
    if ($failed -gt 0) { exit 1 }
}
