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

    Production isolation: the launched 0.6.0 is a REAL production instance, so the machine's
    production settings.json is moved aside for the duration of the run (backed up inside the
    work root, verified absent before the launch, restored byte-identically - hash-verified -
    afterwards). Without this, a production launch would load the machine's real enabled tasks
    and could run REAL backups against a REAL NAS. The harness refuses to run if the quarantine
    cannot be established, and the credentials file is never read (asserted unchanged by hash).
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
$sessionDirA = Join-Path $env:TEMP "FNSyncPlus_CodexTests\WinUiSession_$sessionA"
$probeReportPath = Join-Path $sessionDirA 'handoff-probe.json'

$appDataRoot = Join-Path $env:APPDATA 'FNSyncPlus'
$localAppDataRoot = Join-Path $env:LOCALAPPDATA 'FNSyncPlus'
$productionSettingsPath = Join-Path $appDataRoot 'settings.json'
$productionSettingsQuarantine = Join-Path $WorkRoot 'quarantine-production-settings.json'
$markerPath = Join-Path $localAppDataRoot 'data-format.json'
$markerBackup = Join-Path $WorkRoot 'marker-backup.json'

# The launched 0.6.0 is a VERSIONED-BETA session (published app-<version> layout): it loads its
# own profile from versioned-beta\ and would otherwise auto-connect with the credentials stored
# there during earlier beta testing. Both profile files are quarantined for the run as well.
$versionedBetaDir = Join-Path $localAppDataRoot 'versioned-beta'
$betaSettingsPath = Join-Path $versionedBetaDir 'settings.json'
$betaCredPath = Join-Path $versionedBetaDir 'credentials.dat'
$betaSettingsQuarantine = Join-Path $WorkRoot 'quarantine-beta-settings.json'
$betaCredQuarantine = Join-Path $WorkRoot 'quarantine-beta-credentials.dat'

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

# Populated during the run. quarantinePerformed gates EVERY restore in finally: if the script
# dies before the quarantine block, no restore (and no deletion) may ever run against the
# user's real files.
$script:quarantinePerformed = $false
$leftovers = New-Object System.Collections.ArrayList
$missing = New-Object System.Collections.ArrayList
$changedUnexpected = New-Object System.Collections.ArrayList
$probe = $null
$processA = $null
$processDecoy = $null
$processB = $null
$updaterEntry = $null
$launcherHashBefore = $null
$markerExisted = $false
$settingsExisted = $false
$settingsHashBefore = $null
$appDataBefore = @{}
$localAppDataBefore = @{}

try {
    # ------------------------------------------------ 0. preconditions: nothing else runs
    $existingList = ($script:existingSnapshot.Keys | Sort-Object) -join ','
    Record-Result -Name '开始前没有其他 FNSyncPlus.WinUI 实例（新版本是真实生产启动）' `
        -Passed ($script:snapshotCount -eq 0) `
        -Detail ("existing={0}" -f $existingList)
    if ($script:snapshotCount -gt 0) {
        throw "检测到 {0} 个已运行的 FNSyncPlus.WinUI（pid: {1}）。本验收的最终启动是真实生产实例，必须独占单实例互斥量；请先正常退出这些实例（本脚本不会代为结束它们）。" -f $script:snapshotCount, $existingList
    }

    # ------------------------------------------- 0a. snapshot the machine state FIRST
    # The pre-run snapshot must be taken BEFORE anything is quarantined: the final walk compares
    # the end state to this snapshot, and restored (byte-identical) files must match it.
    $appDataBefore = Snapshot-UserData -Root $appDataRoot
    $localAppDataBefore = Snapshot-UserData -Root $localAppDataRoot
    $credentialsPath = Join-Path $appDataRoot 'credentials.dat'
    $credentialsExisted = $appDataBefore.ContainsKey($credentialsPath)
    $credentialsHashBefore = $null
    if ($credentialsExisted) { $credentialsHashBefore = $appDataBefore[$credentialsPath] }
    $connectionLogBefore = Get-ConnectionLogsText -Root $localAppDataRoot
    $markerExisted = Test-Path -LiteralPath $markerPath
    if ($markerExisted) { Copy-Item -LiteralPath $markerPath -Destination $markerBackup -Force }

    # ------------------------------------------- 0b. quarantine the production config
    # The launched 0.6.0 is a real production/versioned-beta instance. The machine's real
    # settings.json and the versioned-beta profile (server, credentials, tasks) are moved aside
    # for the run and restored byte-identically afterwards. The launch must see ZERO tasks and
    # must never dial any NAS.
    $settingsExisted = Test-Path -LiteralPath $productionSettingsPath
    $enabledTaskCount = -1
    $credRemembered = $false
    if ($settingsExisted) {
        $settingsHashBefore = (Get-FileHash -LiteralPath $productionSettingsPath -Algorithm SHA256).Hash
        $settingsJson = Read-JsonFile -Path $productionSettingsPath
        if ($null -ne $settingsJson) {
            $enabledTaskCount = @($settingsJson.Tasks | Where-Object { $_.Enabled -eq $true }).Count
            $credRemembered = ($settingsJson.RememberCredentials -eq $true)
        }
        Copy-Item -LiteralPath $productionSettingsPath -Destination (Join-Path $WorkRoot 'production-settings-original.json') -Force
        Move-Item -LiteralPath $productionSettingsPath -Destination $productionSettingsQuarantine -Force
    }
    $betaSettingsExisted = Test-Path -LiteralPath $betaSettingsPath
    $betaSettingsHashBefore = $null
    if ($betaSettingsExisted) { $betaSettingsHashBefore = (Get-FileHash -LiteralPath $betaSettingsPath -Algorithm SHA256).Hash }
    $betaCredExisted = Test-Path -LiteralPath $betaCredPath
    $betaCredHashBefore = $null
    if ($betaCredExisted) { $betaCredHashBefore = (Get-FileHash -LiteralPath $betaCredPath -Algorithm SHA256).Hash }
    if ($betaSettingsExisted) { Move-Item -LiteralPath $betaSettingsPath -Destination $betaSettingsQuarantine -Force }
    if ($betaCredExisted) { Move-Item -LiteralPath $betaCredPath -Destination $betaCredQuarantine -Force }

    $quarantineOk = (-not (Test-Path -LiteralPath $productionSettingsPath)) -and
                    (-not (Test-Path -LiteralPath $betaSettingsPath)) -and
                    (-not (Test-Path -LiteralPath $betaCredPath))
    Record-Result -Name '生产配置与 versioned-beta 凭据已隔离（真实启动不会加载任何任务或凭据）' -Passed $quarantineOk `
        -Detail ("settings={0} enabledTasks={1} remembered={2} betaSettings={3} betaCred={4}" -f
            $settingsExisted, $enabledTaskCount, $credRemembered, $betaSettingsExisted, $betaCredExisted)
    if (-not $quarantineOk) {
        throw "无法隔离生产配置或 versioned-beta 凭据，真实生产启动可能触发真实备份，验收终止"
    }
    $script:quarantinePerformed = $true
    Record-Event -Event 'production-settings-quarantined' -Fields @{ enabled_tasks = $enabledTaskCount;
        beta_profile = $betaSettingsExisted; beta_credentials = $betaCredExisted }

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
    New-Item -ItemType Directory -Path (Join-Path $env:TEMP "FNSyncPlus_CodexTests\WinUiSession_$decoySession") -Force | Out-Null
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

    # The new version must see ZERO tasks: both the production settings and the versioned-beta
    # profile (its real config source) are quarantined, so the fresh defaults it loads contain
    # no tasks and no credentials. The report proves what it actually loaded.
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
    Record-Result -Name '新版本以零任务配置启动（真实任务与凭据在隔离中，未被加载）' `
        -Passed ($null -ne $reportB -and $reportB.task_count -eq 0) `
        -Detail ("task_count={0}" -f $reportB.task_count)
    Record-Result -Name '备份管理器在新版本中正常运转（非维护暂停状态）' `
        -Passed ($null -ne $reportB -and $reportB.task_manager_ok -eq $true) `
        -Detail $reportDetail
    # Note: the report's nas_online field is an optimistic default sentinel, not a connection
    # fact. The real isolation proof is the production connection log: with the production and
    # versioned-beta configs quarantined, no NAS dial may appear during the run window.
    $connectionLogAfter = Get-ConnectionLogsText -Root $localAppDataRoot
    $newLogPart = ''
    if ($connectionLogAfter.Length -ge $connectionLogBefore.Length -and
        $connectionLogAfter.StartsWith($connectionLogBefore, [StringComparison]::Ordinal)) {
        $newLogPart = $connectionLogAfter.Substring($connectionLogBefore.Length)
    }
    else { $newLogPart = $connectionLogAfter }
    $newLogLines = @($newLogPart -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    $dialAttempts = @($newLogLines | Where-Object { $_ -match '成功连接|ConnectAsync|TlsProbe|SMB|smb_' })
    Record-Result -Name '生产连接日志在验收窗口内没有任何 NAS 拨号记录' `
        -Passed ($dialAttempts.Count -eq 0) `
        -Detail ("newLines={0} dial={1}" -f $newLogLines.Count, (($dialAttempts | Select-Object -First 2) -join ' | '))

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

    # ------------------------------------------------ restore the production state
    # Every restore is gated on quarantine actually having happened: a run that died before the
    # quarantine block moved nothing, so restoring (deleting!) real user files would be harmful.
    if (-not $script:quarantinePerformed) {
        Record-Result -Name '生产状态还原被跳过（隔离未执行，未移动任何真实文件）' -Passed $true `
            -Detail 'quarantinePerformed=False'
    }
    else {
    $settingsRestoreOk = $false
    try {
        if ($settingsExisted) {
            if (Test-Path -LiteralPath $productionSettingsPath) {
                Remove-Item -LiteralPath $productionSettingsPath -Force
            }
            Move-Item -LiteralPath $productionSettingsQuarantine -Destination $productionSettingsPath -Force
            $hashAfter = (Get-FileHash -LiteralPath $productionSettingsPath -Algorithm SHA256).Hash
            $settingsRestoreOk = ($hashAfter -eq $settingsHashBefore)
        }
        elseif (Test-Path -LiteralPath $productionSettingsPath) {
            # No production settings existed before the run: whatever the new version wrote must go.
            Remove-Item -LiteralPath $productionSettingsPath -Force
            $settingsRestoreOk = $true
        }
        else { $settingsRestoreOk = $true }
    }
    catch {
        Write-Host ("还原生产配置失败：{0}；原始备份位于 {1}" -f $_.Exception.Message,
            (Join-Path $WorkRoot 'production-settings-original.json'))
    }
    Record-Result -Name '生产配置已按字节还原（哈希校验通过）' -Passed $settingsRestoreOk `
        -Detail ("existed={0} restoredHashMatch={1}" -f $settingsExisted, $settingsRestoreOk)

    # --------------------------------------- restore the versioned-beta profile
    $betaSettingsRestoreOk = $false
    $betaCredRestoreOk = $false
    try {
        if ($betaSettingsExisted) {
            if (Test-Path -LiteralPath $betaSettingsPath) { Remove-Item -LiteralPath $betaSettingsPath -Force }
            Move-Item -LiteralPath $betaSettingsQuarantine -Destination $betaSettingsPath -Force
            $hashAfter = (Get-FileHash -LiteralPath $betaSettingsPath -Algorithm SHA256).Hash
            $betaSettingsRestoreOk = ($hashAfter -eq $betaSettingsHashBefore)
        }
        elseif (Test-Path -LiteralPath $betaSettingsPath) {
            Remove-Item -LiteralPath $betaSettingsPath -Force
            $betaSettingsRestoreOk = $true
        }
        else { $betaSettingsRestoreOk = $true }

        if ($betaCredExisted) {
            if (Test-Path -LiteralPath $betaCredPath) { Remove-Item -LiteralPath $betaCredPath -Force }
            Move-Item -LiteralPath $betaCredQuarantine -Destination $betaCredPath -Force
            $hashAfter = (Get-FileHash -LiteralPath $betaCredPath -Algorithm SHA256).Hash
            $betaCredRestoreOk = ($hashAfter -eq $betaCredHashBefore)
        }
        elseif (Test-Path -LiteralPath $betaCredPath) {
            Remove-Item -LiteralPath $betaCredPath -Force
            $betaCredRestoreOk = $true
        }
        else { $betaCredRestoreOk = $true }
    }
    catch {
        Write-Host ("还原 versioned-beta 配置失败：{0}" -f $_.Exception.Message)
    }
    Record-Result -Name 'versioned-beta 配置与凭据已按字节还原（哈希校验通过）' `
        -Passed ($betaSettingsRestoreOk -and $betaCredRestoreOk) `
        -Detail ("settings={0}/{1} credentials={2}/{3}" -f $betaSettingsExisted, $betaSettingsRestoreOk,
            $betaCredExisted, $betaCredRestoreOk)

    try {
        if ($markerExisted) {
            Copy-Item -LiteralPath $markerBackup -Destination $markerPath -Force
        }
        elseif (Test-Path -LiteralPath $markerPath) {
            Remove-Item -LiteralPath $markerPath -Force -ErrorAction SilentlyContinue
        }
    } catch { }

    # The new version may have created updates.json / a fresh settings.json; settings and the
    # marker are restored above. Known-benign run leftovers are removed; anything else that
    # appeared or changed under the production data roots is reported and fails the run.
    $appDataAfter = Snapshot-UserData -Root $appDataRoot
    $localAppDataAfter = Snapshot-UserData -Root $localAppDataRoot
    $leftovers = New-Object System.Collections.ArrayList
    foreach ($set in @(@{ before = $appDataBefore; after = $appDataAfter }, @{ before = $localAppDataBefore; after = $localAppDataAfter })) {
        foreach ($key in $set.after.Keys) {
            if (-not $set.before.ContainsKey($key)) {
                $leaf = Split-Path -Leaf $key
                if ($leaf -eq 'updates.json') {
                    try { Remove-Item -LiteralPath $key -Force -ErrorAction Stop } catch { }
                    continue
                }
                if ($set.after[$key] -eq 'log' -or $set.after[$key] -eq 'lock') { continue }
                [void]$leftovers.Add($key)
            }
        }
    }
    $missing = New-Object System.Collections.ArrayList
    $changedUnexpected = New-Object System.Collections.ArrayList
    foreach ($set in @(@{ before = $appDataBefore; after = $appDataAfter }, @{ before = $localAppDataBefore; after = $localAppDataAfter })) {
        foreach ($key in $set.before.Keys) {
            if (-not $set.after.ContainsKey($key)) { [void]$missing.Add($key); continue }
            if ($set.before[$key] -eq 'log' -or $set.before[$key] -eq 'lock') { continue }
            if ($set.after[$key] -ne $set.before[$key]) { [void]$changedUnexpected.Add($key) }
        }
    }
    Record-Result -Name '生产用户数据还原后与开始前一致（无缺失、无计划外变更、无遗留）' `
        -Passed ($leftovers.Count -eq 0 -and $missing.Count -eq 0 -and $changedUnexpected.Count -eq 0) `
        -Detail ("leftover={0} missing={1} changed={2} :: {3}" -f $leftovers.Count, $missing.Count,
            $changedUnexpected.Count,
            (((@($leftovers) + @($missing) + @($changedUnexpected)) | Select-Object -First 5) -join ' ; '))
    }

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
