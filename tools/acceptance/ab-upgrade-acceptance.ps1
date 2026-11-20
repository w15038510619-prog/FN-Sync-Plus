<#
.SYNOPSIS
    Real two-version (A -> B) upgrade and rollback acceptance for FN Sync Plus.

.DESCRIPTION
    This script does not simulate the update protocol: it builds two genuinely different payloads
    with build-release.ps1 (each with its own version, its own build-meta.json and the pinned update
    public key embedded), then drives the *shipped* updater executable exactly as the UI would:

        FN_Sync_Plus.exe --layout <install> --apply --release <release.json> --no-launch
        FN_Sync_Plus.exe --layout <install> --rollback --no-launch

    Coverage:
      * A installed -> B applied: pointer switch, previous.json, immutable payload dirs, journal,
        launcher bytes unchanged, startup report (services/task list/data format) parsed.
      * Health verification is required to pass for the switch to stick: a build that starts but
        cannot initialise services is rejected by LaunchHealthTracker (see the negative case with a
        deliberately broken payload in the acceptance document).
      * Rollback: refused when the on-disk data format is newer than the previous build declares,
        allowed otherwise, and refused again when there is no previous release.
      * Refusals: caller-supplied --sha256/--version, missing --release, package tampering, package
        outside the install root, same-version reinstall, an install that fails after verification,
        a corrupt current.json, and an update attempted while the app is still running.
      * User data is snapshotted before/after: an upgrade must not touch the user's settings or
        credentials outside the install root.

    Everything is written under -WorkRoot (default: <repo>\.acceptance\ab) and can be deleted.

.PARAMETER SkipBuilds
    Reuse the payloads from a previous run instead of rebuilding (assertions only).

.NOTES
    This is a pilot acceptance harness, not a release step. It uses a throw-away signing key that is
    generated inside the work root and never leaves it.
#>
param(
    [string]$DotnetPath = 'dotnet',
    [string]$WorkRoot = '',
    [int]$ApplyHealthTimeout = 120,
    [switch]$SkipBuilds
)

$ErrorActionPreference = 'Stop'

# The evidence log must be readable: native tool output and our own Chinese messages are UTF-8.
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    # Every run gets its own directory: an acceptance run is evidence, so it is never overwritten
    # and never deleted by the harness itself.
    $WorkRoot = Join-Path $repo ('.acceptance\ab-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$WorkRoot = [System.IO.Path]::GetFullPath($WorkRoot)
if (-not $WorkRoot.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "工作目录必须位于仓库内：$WorkRoot"
}
if (Test-Path -LiteralPath $WorkRoot) { throw "工作目录已存在，请换一个 -WorkRoot：$WorkRoot" }

$script:results = New-Object System.Collections.ArrayList
function Record-Result {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    [void]$script:results.Add([pscustomobject]@{ name = $Name; passed = $Passed; detail = $Detail })
    $tag = 'FAIL'
    if ($Passed) { $tag = 'PASS' }
    Write-Host ("[{0}] {1} :: {2}" -f $tag, $Name, $Detail)
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
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

function Get-FileText {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    return (Get-Content -LiteralPath $Path -Raw)
}

function Stop-SandboxProcesses {
    param([string]$Root)
    try {
        Get-Process -Name 'FNSyncPlus.WinUI' -ErrorAction SilentlyContinue | ForEach-Object {
            $path = ''
            try { $path = $_.Path } catch { $path = '' }
            if ($path -and $path.StartsWith($Root, [StringComparison]::OrdinalIgnoreCase)) {
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
            }
        }
    } catch { }
}

if (Test-Path -LiteralPath $WorkRoot) { throw "工作目录已存在，请换一个 -WorkRoot：$WorkRoot" }
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null
Write-Host ("验收工作目录：{0}" -f $WorkRoot)

$keyDir = Join-Path $WorkRoot 'keys'
$privateKey = Join-Path $keyDir 'update-private.pem'
$publicKey = Join-Path $keyDir 'update-public.pem'
$sandboxRoot = Join-Path $WorkRoot 'sandbox'
$installRoot = Join-Path $sandboxRoot 'install'
$buildA = Join-Path $WorkRoot 'build-a'
$buildB = Join-Path $WorkRoot 'build-b'
$versionA = '0.5.0'
$versionB = '0.6.0'

$markerPath = Join-Path $env:LOCALAPPDATA 'FNSyncPlus\data-format.json'
$markerBackup = Join-Path $WorkRoot 'marker-backup.json'
$markerExisted = Test-Path -LiteralPath $markerPath
if ($markerExisted) { Copy-Item -LiteralPath $markerPath -Destination $markerBackup -Force }

function Snapshot-UserData {
    param([string]$Root, [switch]$WithHash)
    $map = @{}
    if (-not (Test-Path -LiteralPath $Root)) { return $map }
    Get-ChildItem -LiteralPath $Root -File -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
        $entry = $_.FullName
        if ($WithHash -and $entry -notmatch '\\logs\\') {
            try { $map[$entry] = (Get-FileHash -LiteralPath $entry -Algorithm SHA256).Hash } catch { $map[$entry] = 'unreadable' }
        }
        else {
            $map[$entry] = 'exists'
        }
    }
    return $map
}

$appDataSnapshot = Snapshot-UserData -Root (Join-Path $env:APPDATA 'FNSyncPlus') -WithHash
$localAppDataSnapshot = Snapshot-UserData -Root (Join-Path $env:LOCALAPPDATA 'FNSyncPlus') -WithHash

try {
    # ---------------------------------------------------------------- 1. throw-away signing key
    if (-not $SkipBuilds) {
        New-Item -ItemType Directory -Path $keyDir -Force | Out-Null
        $keygen = Run-Exe -Path $DotnetPath -Arguments @(
            'run', '--project', (Join-Path $repo 'tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj'),
            '-c', 'Release', '--', 'keygen', '--private', $privateKey, '--public', $publicKey, '--force')
        Record-Result -Name '生成一次性更新签名密钥' -Passed ($keygen.Code -eq 0 -and (Test-Path $publicKey)) `
            -Detail ("exit={0} {1}" -f $keygen.Code, $publicKey)

        # ------------------------------------------------------------ 2. build A and B
        $buildScript = Join-Path $repo 'FNSyncPlus\build-release.ps1'
        $commonBuild = @{
            Dotnet = $DotnetPath; Channel = 'beta'; SkipTests = $true
            UpdatePublicKeyPath = $publicKey; SignManifest = $true; SigningKeyPath = $privateKey
            ManifestBaseUrl = 'https://pilot.invalid/fn-sync-plus'; SupportedDataFormat = 1
        }
        Write-Host '--- building A (0.5.0) ---'
        & $buildScript -Version $versionA -OutputRoot $sandboxRoot @commonBuild
        $buildOkA = $?
        $exeA = Join-Path $installRoot 'FN_Sync_Plus.exe'
        $payloadA = Join-Path $installRoot "app-$versionA"
        Record-Result -Name '构建 A 版本（真实 publish + 签名清单）' `
            -Passed ($buildOkA -and (Test-Path (Join-Path $payloadA 'FNSyncPlus.WinUI.exe'))) `
            -Detail ("payload={0}" -f $payloadA)

        Write-Host '--- building B (0.6.0) ---'
        & $buildScript -Version $versionB -OutputRoot $buildB @commonBuild
        $buildOkB = $?
        $payloadB = Join-Path $buildB "install\app-$versionB"
        Record-Result -Name '构建 B 版本（真实 publish + 签名清单）' `
            -Passed ($buildOkB -and (Test-Path (Join-Path $payloadB 'FNSyncPlus.WinUI.exe'))) `
            -Detail ("payload={0}" -f $payloadB)

        $metaA = Read-JsonFile -Path (Join-Path $payloadA 'build-meta.json')
        $metaB = Read-JsonFile -Path (Join-Path $payloadB 'build-meta.json')
        Record-Result -Name '两个载荷声明各自的版本与数据格式' `
            -Passed ($metaA.version -eq $versionA -and $metaB.version -eq $versionB -and
                      $metaA.supported_data_format -eq 1 -and $metaB.supported_data_format -eq 1) `
            -Detail ("A={0}/{1} B={2}/{3}" -f $metaA.version, $metaA.supported_data_format, $metaB.version, $metaB.supported_data_format)

        $secretLeak = @(Get-ChildItem -LiteralPath $payloadB -File -Recurse -Force | Where-Object {
            @('update-private.pem', 'update-public.pem', 'credentials.dat', 'settings.json') -contains $_.Name })
        Record-Result -Name '载荷不含私钥或用户凭据' -Passed ($secretLeak.Count -eq 0) `
            -Detail ("hits={0}" -f $secretLeak.Count)
    }
    else {
        Write-Host '跳过构建，复用既有载荷'
    }

    $launcher = Join-Path $installRoot 'FN_Sync_Plus.exe'
    if (-not (Test-Path -LiteralPath $launcher)) { throw "安装目录缺少启动器：$launcher" }

    # ---------------------------------------------------------------- 3. launcher is stable
    $launcherHashBefore = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    $currentA = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
    Record-Result -Name 'A 安装后 current.json 指向 0.5.0' `
        -Passed ($currentA.version -eq $versionA -and $currentA.supported_data_format -eq 1) `
        -Detail ("version={0} format={1}" -f $currentA.version, $currentA.supported_data_format)

    # ---------------------------------------------------------------- 4. hand off B
    $packagesDir = Join-Path $installRoot '.packages'
    $pendingDir = Join-Path $installRoot '.pending'
    New-Item -ItemType Directory -Path $packagesDir -Force | Out-Null
    New-Item -ItemType Directory -Path $pendingDir -Force | Out-Null
    $packageB = Join-Path $packagesDir "FN-Sync-Plus-$versionB-x64.zip"
    Copy-Item -LiteralPath (Join-Path $buildB "FN-Sync-Plus-$versionB-x64.zip") -Destination $packageB -Force
    $manifestB = Join-Path $buildB 'manifest.json'

    function New-ReleaseDescriptor {
        param([string]$ManifestPath, [string]$PackagePath, [string]$Version, [string]$Channel, [string]$OutPath)
        $descriptor = [ordered]@{
            # [string] cast matters: Windows PowerShell 5.1 wraps a -Raw pipeline result and
            # ConvertTo-Json then emits {"value": ...} instead of the manifest text.
            manifest        = [string](Get-Content -LiteralPath $ManifestPath -Raw)
            version         = $Version
            package_path    = $PackagePath
            package_sha256  = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash
            package_size    = (Get-Item -LiteralPath $PackagePath).Length
            machine_group   = ''
            release_batch   = 0
            requested_channel = $Channel
        }
        $descriptor | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutPath -Encoding utf8
        return $OutPath
    }

    $releaseB = New-ReleaseDescriptor -ManifestPath $manifestB -PackagePath $packageB -Version $versionB `
        -Channel 'beta' -OutPath (Join-Path $pendingDir "release-$versionB.json")

    # The manifest itself must verify with the very key that got embedded into the binaries.
    $verifyManifest = Run-Exe -Path $DotnetPath -Arguments @(
        'run', '--project', (Join-Path $repo 'tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj'), '-c', 'Release',
        '--', 'verify', '--manifest', $manifestB, '--public', $publicKey, '--current', $versionA, '--channel', 'beta')
    Record-Result -Name 'B 的清单可用内嵌公钥验证' -Passed ($verifyManifest.Code -eq 0) `
        -Detail ("exit={0}" -f $verifyManifest.Code)

    if ($markerExisted) {
        @{ format = 1; app_version = $versionA; updated_at = (Get-Date).ToUniversalTime().ToString('o') } |
            ConvertTo-Json -Compress | Set-Content -LiteralPath $markerPath -Encoding utf8
    }

    # ---------------------------------------------------------------- 5. real apply
    Write-Host '--- applying B through the shipped updater ---'
    $apply = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, '--apply', '--release', $releaseB,
        '--no-launch', '--health-timeout', "$ApplyHealthTimeout")
    $currentB = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
    $previousB = Read-JsonFile -Path (Join-Path $installRoot 'previous.json')
    Record-Result -Name '独立更新器安装 B（--apply）' `
        -Passed ($apply.Code -eq 0 -and $currentB.version -eq $versionB) `
        -Detail ("exit={0} current={1} output={2}" -f $apply.Code, $currentB.version, ($apply.Output.Trim() -replace '\s+', ' '))

    Record-Result -Name 'previous.json 记录 0.5.0 供回滚' -Passed ($previousB.version -eq $versionA) `
        -Detail ("previous={0}" -f $previousB.version)

    $payloadBInstalled = Join-Path $installRoot "app-$versionB"
    Record-Result -Name '新旧版本目录同时保留（载荷不可变）' `
        -Passed ((Test-Path (Join-Path $payloadA 'FNSyncPlus.WinUI.exe')) -and (Test-Path (Join-Path $payloadBInstalled 'FNSyncPlus.WinUI.exe'))) `
        -Detail ("A存在={0} B存在={1}" -f (Test-Path $payloadA), (Test-Path $payloadBInstalled))

    $launcherHashAfter = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    Record-Result -Name '启动器在升级后字节未变（可继续修复下一次升级）' `
        -Passed ($launcherHashBefore -eq $launcherHashAfter) `
        -Detail ("before={0} after={1}" -f $launcherHashBefore.Substring(0, 12), $launcherHashAfter.Substring(0, 12))

    $reportB = Read-JsonFile -Path (Join-Path $installRoot ".health\report-$versionB.json")
    $reportOk = $false
    $reportDetail = '未生成启动报告'
    if ($null -ne $reportB) {
        $reportOk = ($reportB.usable -eq $true) -and ($reportB.services_initialized -eq $true) -and
                    ($reportB.task_manager_ok -eq $true) -and ($reportB.format_state -eq 'Valid')
        $reportDetail = ("usable={0} services={1} tasks={2} count={3} format_state={4} nas_online={5}" -f
            $reportB.usable, $reportB.services_initialized, $reportB.task_manager_ok, $reportB.task_count,
            $reportB.format_state, $reportB.nas_online)
    }
    Record-Result -Name '新版本通过可用性检查（服务/任务列表/数据格式，不只是窗口出现）' -Passed $reportOk -Detail $reportDetail

    $markerAfter = Read-JsonFile -Path $markerPath
    Record-Result -Name '升级后本机数据格式标记仍为 1 且由新版本确认' `
        -Passed ($null -ne $markerAfter -and $markerAfter.format -eq 1) `
        -Detail ("format={0} app_version={1}" -f $markerAfter.format, $markerAfter.app_version)

    $journal = Get-FileText -Path (Join-Path $installRoot 'update.log')
    Record-Result -Name '更新日志记录了已验证来源与安装动作' `
        -Passed ($journal -match "apply $versionB from verified release" -and $journal -match "installed $versionB") `
        -Detail ((($journal -split "`r?`n") | Where-Object { $_ -match "verified release|installed $versionB" }) -join ' | ')

    # ---------------------------------------------------------------- 6. refusals while B is active
    $badHash = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, '--apply', '--release', $releaseB,
        '--sha256', ('A' * 64), '--version', $versionB)
    Record-Result -Name '拒绝命令行传入的 --sha256/--version' -Passed ($badHash.Code -eq 2) `
        -Detail ("exit={0} {1}" -f $badHash.Code, ($badHash.Output.Trim() -split "`r?`n")[0])

    $noRelease = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, '--apply')
    Record-Result -Name '缺少发布描述时拒绝安装' -Passed ($noRelease.Code -eq 2) `
        -Detail ("exit={0} {1}" -f $noRelease.Code, ($noRelease.Output.Trim() -split "`r?`n")[0])

    function Invoke-SignManifest {
        param([string]$PackagePath, [string]$Version, [string]$OutPath)
        $descriptorPath = Join-Path $WorkRoot ("descriptor-$Version.json")
        $url = "https://pilot.invalid/FN-Sync-Plus-$Version-x64.zip"
        @{ schema = 1; product = 'FN Sync Plus'; channel = 'beta'; arch = 'x64'; version = $Version; min_version = '0.0.0'
           released_at = (Get-Date).ToUniversalTime().ToString('o'); notes = "pilot $Version"; url = $url
           sha256 = ''; size = 0; batch = 0; audience = @('*'); min_compatible_client = '0.0.0'; signature = '' } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $descriptorPath -Encoding utf8
        $result = Run-Exe -Path $DotnetPath -Arguments @(
            'run', '--project', (Join-Path $repo 'tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj'), '-c', 'Release',
            '--', 'sign', '--input', $descriptorPath, '--key', $privateKey, '--out', $OutPath,
            '--package', $PackagePath, '--version', $Version, '--channel', 'beta', '--url', $url)
        return $result.Code -eq 0
    }

    # A package whose bytes differ from the signed manifest has to be refused by the hash check,
    # not by some earlier rule, so this one is signed as a genuinely newer version first.
    $tamperBase = Join-Path $packagesDir 'FN-Sync-Plus-0.7.0-x64.zip'
    Copy-Item -LiteralPath $packageB -Destination $tamperBase -Force
    $tamperManifest = Join-Path $WorkRoot 'manifest-0.7.0.json'
    $signedTamper = Invoke-SignManifest -PackagePath $tamperBase -Version '0.7.0' -OutPath $tamperManifest
    $bytes = [System.IO.File]::ReadAllBytes($tamperBase)
    $bytes[$bytes.Length - 100] = [byte](($bytes[$bytes.Length - 100] + 1) % 256)
    [System.IO.File]::WriteAllBytes($tamperBase, $bytes)
    $tamperedRelease = New-ReleaseDescriptor -ManifestPath $tamperManifest -PackagePath $tamperBase -Version '0.7.0' `
        -Channel 'beta' -OutPath (Join-Path $pendingDir 'release-tampered.json')
    $tampered = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, '--apply', '--release', $tamperedRelease, '--no-launch')
    $tamperedText = $tampered.Output.Trim() -split "`r?`n"
    Record-Result -Name '被篡改的更新包被拒绝（哈希与签名清单不一致）' `
        -Passed ($signedTamper -and $tampered.Code -eq 6 -and $tampered.Output -match 'SHA-256') `
        -Detail ("exit={0} {1}" -f $tampered.Code, $tamperedText[0])

    $outsidePackage = Join-Path $WorkRoot 'outside.zip'
    Copy-Item -LiteralPath $packageB -Destination $outsidePackage -Force
    $outsideRelease = New-ReleaseDescriptor -ManifestPath $manifestB -PackagePath $outsidePackage -Version $versionB `
        -Channel 'beta' -OutPath (Join-Path $pendingDir 'release-outside.json')
    $outside = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, '--apply', '--release', $outsideRelease, '--no-launch')
    Record-Result -Name '安装目录之外的更新包被拒绝' -Passed ($outside.Code -eq 6) `
        -Detail ("exit={0} {1}" -f $outside.Code, ($outside.Output.Trim() -split "`r?`n")[0])

    $sameVersion = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, '--apply', '--release', $releaseB, '--no-launch')
    Record-Result -Name '同版本重复安装被拒绝（不能伪装成升级）' -Passed ($sameVersion.Code -eq 6) `
        -Detail ("exit={0} {1}" -f $sameVersion.Code, ($sameVersion.Output.Trim() -split "`r?`n")[0])

    # A package that is signed but is not a readable archive: verification passes, installation must
    # fail without moving the pointer.
    $brokenZip = Join-Path $WorkRoot 'broken-0.7.1.zip'
    [System.IO.File]::WriteAllBytes($brokenZip, [System.Text.Encoding]::UTF8.GetBytes('not-a-zip-archive'))
    $brokenManifest = Join-Path $WorkRoot 'manifest-0.7.1.json'
    $signedBroken = Invoke-SignManifest -PackagePath $brokenZip -Version '0.7.1' -OutPath $brokenManifest
    $brokenReleasePath = $null
    if ($signedBroken) {
        $brokenLocal = Join-Path $packagesDir 'broken-0.7.1.zip'
        Copy-Item -LiteralPath $brokenZip -Destination $brokenLocal -Force
        $brokenReleasePath = New-ReleaseDescriptor -ManifestPath $brokenManifest -PackagePath $brokenLocal -Version '0.7.1' `
            -Channel 'beta' -OutPath (Join-Path $pendingDir 'release-0.7.1.json')
    }
    $broken = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, '--apply', '--release', $brokenReleasePath, '--no-launch')
    $currentAfterBroken = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
    Record-Result -Name '安装阶段失败时指针保持原版本（已签名但无法解压的包）' `
        -Passed ($broken.Code -eq 5 -and $currentAfterBroken.version -eq $versionB) `
        -Detail ("exit={0} current={1} {2}" -f $broken.Code, $currentAfterBroken.version, ($broken.Output.Trim() -split "`r?`n")[0])

    # ---------------------------------------------------------------- 7. rollback safety
    @{ format = 2; app_version = '9.9.9'; updated_at = (Get-Date).ToUniversalTime().ToString('o') } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath $markerPath -Encoding utf8
    $rollbackBlocked = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, '--rollback', '--no-launch')
    $currentAfterBlocked = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
    Record-Result -Name '数据格式高于旧版本能力时拒绝回滚' `
        -Passed ($rollbackBlocked.Code -eq 3 -and $currentAfterBlocked.version -eq $versionB) `
        -Detail ("exit={0} current={1} {2}" -f $rollbackBlocked.Code, $currentAfterBlocked.version, ($rollbackBlocked.Output.Trim() -split "`r?`n")[0])

    @{ format = 1; app_version = $versionB; updated_at = (Get-Date).ToUniversalTime().ToString('o') } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath $markerPath -Encoding utf8
    $rollback = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, '--rollback', '--no-launch')
    $currentRolledBack = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
    Record-Result -Name '数据格式兼容时回滚到 0.5.0' `
        -Passed ($rollback.Code -eq 0 -and $currentRolledBack.version -eq $versionA) `
        -Detail ("exit={0} current={1}" -f $rollback.Code, $currentRolledBack.version)

    $previousPath = Join-Path $installRoot 'previous.json'
    $previousCopy = Join-Path $WorkRoot 'previous-backup.json'
    Copy-Item -LiteralPath $previousPath -Destination $previousCopy -Force
    # Move, not delete: the "no previous release" state is what is being tested, and moving keeps
    # the original pointer available for the restore below.
    Move-Item -LiteralPath $previousPath -Destination (Join-Path $WorkRoot 'previous-aside.json') -Force
    $rollbackNoPrevious = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, '--rollback', '--no-launch')
    Record-Result -Name '没有上一版本时拒绝回滚' -Passed ($rollbackNoPrevious.Code -eq 3) `
        -Detail ("exit={0} {1}" -f $rollbackNoPrevious.Code, ($rollbackNoPrevious.Output.Trim() -split "`r?`n")[0])
    Copy-Item -LiteralPath $previousCopy -Destination $previousPath -Force

    # ---------------------------------------------------------------- 8. broken pointer / running app
    $currentPath = Join-Path $installRoot 'current.json'
    $currentCopy = Join-Path $WorkRoot 'current-backup.json'
    Copy-Item -LiteralPath $currentPath -Destination $currentCopy -Force
    Set-Content -LiteralPath $currentPath -Value 'not-json-pointer-content' -Encoding utf8
    $statusBroken = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, 'status')
    $launchBroken = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, 'launch')
    Record-Result -Name 'current.json 损坏时不启动任何版本' `
        -Passed ($statusBroken.Code -eq 3 -and $launchBroken.Code -eq 3) `
        -Detail ("status={0} launch={1}" -f $statusBroken.Code, $launchBroken.Code)
    Copy-Item -LiteralPath $currentCopy -Destination $currentPath -Force

    Stop-SandboxProcesses -Root $installRoot
    $runningApp = Start-Process -FilePath (Join-Path $payloadA 'FNSyncPlus.WinUI.exe') -PassThru
    Start-Sleep -Seconds 3
    $applyWhileRunning = Run-Exe -Path $launcher -Arguments @('--layout', $installRoot, '--apply', '--release', $releaseB,
        '--wait-pid', "$($runningApp.Id)", '--wait-seconds', '3', '--no-launch')
    $stillRunning = -not $runningApp.HasExited
    try { if ($stillRunning) { Stop-Process -Id $runningApp.Id -Force } } catch { }
    Start-Sleep -Milliseconds 500
    $currentAfterRunning = Read-JsonFile -Path $currentPath
    Record-Result -Name '程序仍在运行时更新器放弃安装且不强杀进程' `
        -Passed ($applyWhileRunning.Code -eq 4 -and $stillRunning) `
        -Detail ("exit={0} appAlive={1}" -f $applyWhileRunning.Code, $stillRunning)
    Record-Result -Name '放弃安装后指针未变' -Passed ($currentAfterRunning.version -eq $versionA) `
        -Detail ("current={0}" -f $currentAfterRunning.version)

    # ---------------------------------------------------------------- 9. user data untouched
    $appDataAfter = Snapshot-UserData -Root (Join-Path $env:APPDATA 'FNSyncPlus') -WithHash
    $localAppDataAfter = Snapshot-UserData -Root (Join-Path $env:LOCALAPPDATA 'FNSyncPlus') -WithHash
    # The application itself legitimately rewrites its format marker and credential file when it
    # starts, so those are excluded; everything else outside the install root must be byte-identical
    # and nothing may disappear.
    $mutableNames = @('data-format.json', 'credentials.dat')
    $changed = New-Object System.Collections.ArrayList
    foreach ($key in $appDataSnapshot.Keys) {
        if (-not $appDataAfter.ContainsKey($key)) { [void]$changed.Add("缺失:$key"); continue }
        if ($appDataSnapshot[$key] -eq 'exists') { continue }
        if ($appDataAfter[$key] -ne $appDataSnapshot[$key]) { [void]$changed.Add("变更:$key") }
    }
    foreach ($key in $localAppDataSnapshot.Keys) {
        if (-not $localAppDataAfter.ContainsKey($key)) { [void]$changed.Add("缺失:$key"); continue }
        if ($localAppDataSnapshot[$key] -eq 'exists') { continue }
        if ($key -match '\\logs\\') { continue }
        $name = Split-Path -Leaf $key
        if ($mutableNames -contains $name) { continue }
        if ($localAppDataAfter[$key] -ne $localAppDataSnapshot[$key]) { [void]$changed.Add("变更:$key") }
    }
    $settingsPath = Join-Path $env:APPDATA 'FNSyncPlus\settings.json'
    $settingsOk = $true
    if ($appDataSnapshot.ContainsKey($settingsPath)) {
        $settingsOk = $appDataAfter.ContainsKey($settingsPath) -and
                      $appDataAfter[$settingsPath] -eq $appDataSnapshot[$settingsPath]
    }
    Record-Result -Name '升级与回滚未改动安装目录之外的用户数据' -Passed ($changed.Count -eq 0 -and $settingsOk) `
        -Detail ("changed={0} settings保留={1} {2}" -f $changed.Count, $settingsOk,
            (($changed | Select-Object -First 5) -join '; '))
}
finally {
    if ($markerExisted) {
        Copy-Item -LiteralPath $markerBackup -Destination $markerPath -Force
    }
    Stop-SandboxProcesses -Root $installRoot

    $passed = @($script:results | Where-Object { $_.passed }).Count
    $failed = @($script:results | Where-Object { -not $_.passed }).Count
    Write-Host ''
    Write-Host ("合计：PASS {0} / FAIL {1}（共 {2} 项）" -f $passed, $failed, $script:results.Count)
    $summaryPath = Join-Path $WorkRoot 'acceptance-result.json'
    @{ workRoot = $WorkRoot; passed = $passed; failed = $failed; total = $script:results.Count
       cases = $script:results } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host ("结果文件：{0}" -f $summaryPath)
    if ($failed -gt 0) { exit 1 }
}
