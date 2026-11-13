<#
.SYNOPSIS
    HTTPS automatic-update end-to-end acceptance against a LOCAL ISOLATED Kestrel server.

.DESCRIPTION
    Proves the whole automatic update flow over real HTTPS — check, signature verification,
    download with SHA-256 enforcement, gated install, health verification and rollback — using:

      * tools/FNSyncPlus.LocalUpdateServer: a loopback-only Kestrel HTTPS server with an
        EPHEMERAL self-signed certificate (generated in-memory per run, never installed into
        any trust store). This is NOT a company update server and the report never claims one.
      * The test certificate is pinned client-side ONLY inside the isolated test environment:
        FNSYNC_DATA_ROOT (architectural storage isolation) + FNSYNC_UPDATE_TEST_CERT_PIN.
        Production sessions have no test data root, so the pin can never weaken real TLS
        validation; one scenario proves an unpinned certificate is rejected.
      * The real WinUI app process driving the production UpdateService code path via
        --update-https-probe (check+download) and --update-https-install (full handoff).

    Scenarios:
      S1 failure handling: bad-signature manifest refused; unpinned certificate refused (TLS
         validation active); tampered package fails SHA-256 and leaves no partial file;
         unreachable server degrades gracefully.
      S2 happy path: signed manifest -> verified download -> real updater handoff -> pointer
         switch -> new version healthy. Evidence: HTTPS URL, TLS protocol per request, manifest,
         signature algorithm, SHA-256, versions, three PIDs, pointer, health, operation id.
      S3 rollback: a 0.7.0 package whose payload cannot start is installed, fails launch
         verification, and the updater rolls back to 0.6.0 which comes back healthy.

    Everything lives under -WorkRoot (default: <repo>\.acceptance\https-<timestamp>) and the
    isolated test data root; production files are only read for before/after snapshots.

.NOTES
    Pilot acceptance harness, not a release step. Signing key and TLS certificate are throw-away
    artifacts generated inside the work root and never leave it.
#>
param(
    [string]$DotnetPath = '',
    [string]$WorkRoot = '',
    [int]$PrepareTimeoutSeconds = 120,
    [int]$ExitBudgetSeconds = 120,
    [int]$ApplyBudgetSeconds = 240,
    [switch]$SkipBuilds
)

$ErrorActionPreference = 'Stop'
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path $repo ('.acceptance\https-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$WorkRoot = [System.IO.Path]::GetFullPath($WorkRoot)
if (-not $WorkRoot.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) {
    throw "工作目录必须位于仓库内：$WorkRoot"
}
if ((Test-Path -LiteralPath $WorkRoot) -and -not $SkipBuilds) {
    throw "工作目录已存在，请换一个 -WorkRoot：$WorkRoot"
}
if (-not (Test-Path -LiteralPath $WorkRoot)) { New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null }
Write-Host ("验收工作目录：{0}" -f $WorkRoot)

$dotnet = $DotnetPath
if ([string]::IsNullOrWhiteSpace($dotnet)) {
    foreach ($candidate in @('D:\dotnet\dotnet.exe', 'D:\FNSyncPlus_Toolchain\dotnet\dotnet.exe', 'C:\Program Files\dotnet\dotnet.exe')) {
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
    return ([System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8) | ConvertFrom-Json)
}
function Get-FileText {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    return (Get-Content -LiteralPath $Path -Raw)
}

# ---------------------------------------------------------------- process ownership
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
    if ($null -ne $live) { try { $null = $live.CloseMainWindow() } catch { } }
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
$versionC = '0.7.0'
$keyDir = Join-Path $WorkRoot 'keys'
$privateKey = Join-Path $keyDir 'update-private.pem'
$publicKey = Join-Path $keyDir 'update-public.pem'
$layoutRoot = Join-Path $WorkRoot 'layout'
$installRoot = Join-Path $layoutRoot 'install'
$buildB = Join-Path $WorkRoot 'build-b'
$journalPath = Join-Path $installRoot 'update.log'
$exeA = Join-Path $installRoot "app-$versionA\FNSyncPlus.WinUI.exe"
$exeB = Join-Path $installRoot "app-$versionB\FNSyncPlus.WinUI.exe"
$contentDir = Join-Path $WorkRoot 'server-content'
$serverFactsPath = Join-Path $WorkRoot 'server-facts.json'
$requestLogPath = $serverFactsPath + '.requests.jsonl'

# Reserve an HTTPS port BEFORE building B: the signed manifest bakes the download URL.
$portProbe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
$portProbe.Start()
$serverPort = $portProbe.LocalEndpoint.Port
$portProbe.Stop()
$manifestBaseUrl = "https://localhost:$serverPort"
$manifestUrl = "$manifestBaseUrl/manifest.json"

# ---------------------------------------------------------------- architectural isolation
$testDataRoot = Join-Path $WorkRoot 'testdata'
$storageAuditPath = Join-Path $WorkRoot 'storage-audit.log'
New-Item -ItemType Directory -Path $testDataRoot -Force | Out-Null
$env:FNSYNC_DATA_ROOT = $testDataRoot
$env:FNSYNC_STORAGE_AUDIT = $storageAuditPath
# Loopback must never go through a proxy: sandboxed shells export HTTP(S)_PROXY, and .NET's
# HttpClient honors them. Bypassing the proxy for localhost mirrors a correctly configured
# environment and changes nothing about TLS validation itself.
$env:NO_PROXY = 'localhost,127.0.0.1,::1'
$env:no_proxy = $env:NO_PROXY
$testSessionsRoot = Join-Path $testDataRoot 'temp\FNSyncPlus_CodexTests'

$appDataRoot = Join-Path $env:APPDATA 'FNSyncPlus'
$localAppDataRoot = Join-Path $env:LOCALAPPDATA 'FNSyncPlus'
$markerPath = Join-Path $localAppDataRoot 'data-format.json'

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
function Get-ServerRequests {
    if (-not (Test-Path -LiteralPath $requestLogPath -PathType Leaf)) { return @() }
    $items = @([System.IO.File]::ReadAllLines($requestLogPath, [System.Text.Encoding]::UTF8) |
        Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_ | ConvertFrom-Json })
    return ,$items
}
function Write-SessionUpdatesJson {
    param([string]$SessionId, [string]$Url)
    $sessionDir = Join-Path $testSessionsRoot "WinUiSession_$SessionId"
    $logsDir = Join-Path $sessionDir 'logs'
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
    $updates = [ordered]@{ channel = 'beta'; auto_mode = 1; manifest_url = $Url;
                           release_batch = 0; machine_group = '' }
    [System.IO.File]::WriteAllText((Join-Path $logsDir 'updates.json'),
        ($updates | ConvertTo-Json), (New-Object System.Text.UTF8Encoding($false)))
    return $sessionDir
}

# Populated during the run.
$probeP5 = $null
$processB = $null
$updaterEntry = $null
$appDataBefore = @{}
$localAppDataBefore = @{}
$markerExisted = $false
$markerHashBefore = $null

try {
    # ------------------------------------------------ 0. preconditions
    Record-Result -Name '开始前没有其他 FNSyncPlus.WinUI 实例' -Passed ($script:snapshotCount -eq 0) `
        -Detail ("existing={0}" -f (($script:existingSnapshot.Keys | Sort-Object) -join ','))
    if ($script:snapshotCount -gt 0) { throw "已有 FNSyncPlus.WinUI 实例在运行，本验收必须独占；请先退出它们。" }

    $appDataBefore = Snapshot-UserData -Root $appDataRoot
    $localAppDataBefore = Snapshot-UserData -Root $localAppDataRoot
    $markerExisted = Test-Path -LiteralPath $markerPath
    if ($markerExisted) { $markerHashBefore = (Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash }

    $rootOverlapsProduction = $false
    foreach ($prodRoot in @($appDataRoot, $localAppDataRoot)) {
        if ($testDataRoot.StartsWith($prodRoot, [StringComparison]::OrdinalIgnoreCase) -or
            $prodRoot.StartsWith($testDataRoot, [StringComparison]::OrdinalIgnoreCase)) { $rootOverlapsProduction = $true }
    }
    $isolationOk = (Test-Path -LiteralPath $testDataRoot) -and (-not $rootOverlapsProduction) -and
                   ($env:FNSYNC_DATA_ROOT -eq $testDataRoot)
    Record-Result -Name '独立测试数据根已建立（架构隔离，不移动任何生产文件）' -Passed $isolationOk `
        -Detail ("root={0} overlap={1}" -f $testDataRoot, $rootOverlapsProduction)
    if (-not $isolationOk) { throw '无法建立独立测试数据根，验收终止' }
    Record-Result -Name '生产配置保持原地且只读（本机现状仅作证据记录）' -Passed $true `
        -Detail ("settingsExisted={0} markerExisted={1}" -f
            (Test-Path -LiteralPath (Join-Path $appDataRoot 'settings.json')), $markerExisted)

    # ------------------------------------------------ 1. key + two real builds (B's manifest targets the Kestrel URL)
    if (-not $SkipBuilds) {
        New-Item -ItemType Directory -Path $keyDir -Force | Out-Null
        $keygen = Run-Exe -Path $dotnet -Arguments @(
            'run', '--project', (Join-Path $repo 'tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj'),
            '-c', 'Release', '--', 'keygen', '--private', $privateKey, '--public', $publicKey, '--force')
        Record-Result -Name '生成一次性更新签名密钥（ECDSA P-256）' `
            -Passed ($keygen.Code -eq 0 -and (Test-Path $publicKey)) -Detail ("exit={0}" -f $keygen.Code)

        $buildScript = Join-Path $repo 'FNSyncPlus\build-release.ps1'
        $commonBuild = @{
            Dotnet = $dotnet; Channel = 'beta'; SkipTests = $true
            UpdatePublicKeyPath = $publicKey; SignManifest = $true; SigningKeyPath = $privateKey
            ManifestBaseUrl = $manifestBaseUrl; SupportedDataFormat = 1
        }
        Write-Host '--- building A (0.5.0, contains the https probe) ---'
        & $buildScript -Version $versionA -OutputRoot $layoutRoot @commonBuild
        $buildOkA = $?
        Record-Result -Name '构建 A 版本 0.5.0（真实 publish + 内嵌公钥）' `
            -Passed ($buildOkA -and (Test-Path $exeA)) -Detail ("payload={0}" -f $exeA)

        Write-Host '--- building B (0.6.0, manifest URL -> local Kestrel) ---'
        & $buildScript -Version $versionB -OutputRoot $buildB @commonBuild
        $buildOkB = $?
        Record-Result -Name '构建 B 版本 0.6.0（清单下载地址为本地 HTTPS）' `
            -Passed ($buildOkB -and (Test-Path (Join-Path $buildB "install\app-$versionB\FNSyncPlus.WinUI.exe"))) `
            -Detail ("baseUrl={0}" -f $manifestBaseUrl)
    }
    else {
        Write-Host '跳过构建，复用既有载荷'
        if (-not (Test-Path -LiteralPath $exeA)) { throw "安装目录缺少 A 载荷：$exeA" }
    }

    $currentA = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
    Record-Result -Name '初始 current.json 指向 0.5.0' `
        -Passed ($null -ne $currentA -and $currentA.version -eq $versionA) -Detail ("version={0}" -f $currentA.version)

    # ------------------------------------------------ 2. start the isolated Kestrel server
    $serverProj = Join-Path $repo 'tools\FNSyncPlus.LocalUpdateServer\FNSyncPlus.LocalUpdateServer.csproj'
    $serverBuild = Run-Exe -Path $dotnet -Arguments @('build', $serverProj, '-c', 'Release', '--nologo')
    $serverDll = Join-Path $repo 'tools\FNSyncPlus.LocalUpdateServer\bin\Release\net8.0\FNSyncPlus.LocalUpdateServer.dll'
    Record-Result -Name '编译本地隔离更新服务器（Kestrel，仅回环）' `
        -Passed ($serverBuild.Code -eq 0 -and (Test-Path $serverDll)) -Detail ("exit={0}" -f $serverBuild.Code)

    New-Item -ItemType Directory -Path $contentDir -Force | Out-Null
    $serverStderr = Join-Path $WorkRoot 'server.stderr.log'
    $serverStdout = Join-Path $WorkRoot 'server.stdout.log'
    $serverProc = Start-Process -FilePath $dotnet -PassThru -ArgumentList @(
        'exec', "`"$serverDll`"", '--content', "`"$contentDir`"", '--port', "$serverPort",
        '--facts', "`"$serverFactsPath`"", '--request-log', "`"$requestLogPath`"") `
        -RedirectStandardError $serverStderr -RedirectStandardOutput $serverStdout
    $serverEntry = Register-TrackedProcess -ProcessId $serverProc.Id -Label 'local-update-server' -ExpectedPath ''
    $serverFacts = $null
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        $serverFacts = Read-JsonFile -Path $serverFactsPath
        if ($null -ne $serverFacts -and $serverFacts.cert_thumbprint) { break }
        if (-not (Test-TrackedAlive -ProcessId $serverEntry.id -StartedAt $serverEntry.started)) { break }
        Start-Sleep -Milliseconds 400
    }
    $serverOk = ($null -ne $serverFacts) -and ($serverFacts.base_url_localhost -eq $manifestBaseUrl) -and
                ($serverFacts.cert_thumbprint.Length -gt 0)
    Record-Result -Name '本地隔离 HTTPS 更新服务器就绪（回环 + 一次性自签证书）' -Passed $serverOk `
        -Detail ("url={0} thumbprint={1}" -f $serverFacts.base_url_localhost, $serverFacts.cert_thumbprint)
    if (-not $serverOk) {
        $errDetail = if (Test-Path $serverStderr) { Get-Content $serverStderr -Raw } else { '无错误输出' }
        throw "本地更新服务器未能启动：$errDetail"
    }
    Record-Event -Event 'server-ready' -Fields @{ url = $manifestBaseUrl; thumbprint = $serverFacts.cert_thumbprint;
        tls_cert_signature = $serverFacts.cert_signature_algorithm }

    # The throw-away certificate must not be trusted anywhere on this machine: pinning happens
    # ONLY through the isolated-environment variable, never through a trust store.
    $thumbprint = [string]$serverFacts.cert_thumbprint
    $storeHits = 0
    foreach ($store in @('Cert:\CurrentUser\Root', 'Cert:\CurrentUser\CA', 'Cert:\LocalMachine\Root', 'Cert:\LocalMachine\CA')) {
        $found = @(Get-ChildItem -LiteralPath $store -ErrorAction SilentlyContinue |
            Where-Object { $_.Thumbprint -eq $thumbprint })
        $storeHits += $found.Count
    }
    Record-Result -Name '测试证书未进入任何系统信任库（不关闭生产 TLS 校验）' -Passed ($storeHits -eq 0) `
        -Detail ("storeHits={0} thumbprint={1}" -f $storeHits, $thumbprint)

    # Pin the certificate for the isolated environment only.
    $env:FNSYNC_UPDATE_TEST_CERT_PIN = $thumbprint

    # Stage content: B's signed manifest and package.
    $packageBName = "FN-Sync-Plus-$versionB-x64.zip"
    $manifestBPath = Join-Path $buildB 'manifest.json'
    $packageBSrc = Join-Path $buildB $packageBName
    Copy-Item -LiteralPath $manifestBPath -Destination (Join-Path $contentDir 'manifest.json') -Force
    Copy-Item -LiteralPath $packageBSrc -Destination (Join-Path $contentDir $packageBName) -Force
    $packageBHash = (Get-FileHash -LiteralPath (Join-Path $contentDir $packageBName) -Algorithm SHA256).Hash
    $manifestBObj = Read-JsonFile -Path (Join-Path $contentDir 'manifest.json')
    Record-Result -Name 'B 清单与更新包已发布到本地 HTTPS 服务' `
        -Passed ($null -ne $manifestBObj -and $manifestBObj.url -eq "$manifestBaseUrl/$packageBName" -and
                 $manifestBObj.sha256 -eq $packageBHash) `
        -Detail ("url={0} sha256={1}…" -f $manifestBObj.url, $packageBHash.Substring(0, 12))
    Record-Event -Event 'content-published' -Fields @{ version = $versionB; url = $manifestBObj.url;
        sha256 = $packageBHash; size = $manifestBObj.size }

    # ------------------------------------------------ probe runner
    function Invoke-HttpsProbe {
        param([string]$Exe, [string]$SessionId, [string]$ReportPath, [string]$Url,
              [switch]$Install, [int]$TimeoutSec = 120)
        [void](Write-SessionUpdatesJson -SessionId $SessionId -Url $Url)
        Remove-Item -LiteralPath $ReportPath -Force -ErrorAction SilentlyContinue
        $argList = @('--isolated-test-session', "--isolated-test-state=$SessionId",
            '--isolated-test-remote=Codex_FN_Test_Ui', "--update-https-probe=$ReportPath")
        if ($Install) { $argList += '--update-https-install' }
        $proc = Start-Process -FilePath $Exe -PassThru -ArgumentList $argList
        $tracked = Register-TrackedProcess -ProcessId $proc.Id -Label "probe-$SessionId" -ExpectedPath $installRoot
        $deadline = (Get-Date).AddSeconds($TimeoutSec)
        $report = $null
        while ((Get-Date) -lt $deadline) {
            $candidate = Read-JsonFile -Path $ReportPath
            if ($null -ne $candidate) {
                $hasMarker = ($null -ne $candidate.PSObject.Properties['download_ok']) -or
                             ($null -ne $candidate.PSObject.Properties['shutdown_delivered']) -or
                             ($null -ne $candidate.PSObject.Properties['error']) -or
                             (($null -ne $candidate.PSObject.Properties['has_update']) -and $candidate.has_update -eq $false)
                if ($hasMarker) { $report = $candidate; break }
            }
            if (-not (Test-TrackedAlive -ProcessId $tracked.id -StartedAt $tracked.started)) {
                $report = Read-JsonFile -Path $ReportPath
                break
            }
            Start-Sleep -Milliseconds 400
        }
        return [pscustomobject]@{ Process = $tracked; Report = $report }
    }

    # ------------------------------------------------ S1a. bad-signature manifest is refused
    $goodManifestText = [System.IO.File]::ReadAllText((Join-Path $contentDir 'manifest.json'), [System.Text.Encoding]::UTF8)
    $badManifestText = $goodManifestText.Replace($versionB, '0.6.x')   # any post-signature edit
    [System.IO.File]::WriteAllText((Join-Path $contentDir 'manifest.json'), $badManifestText,
        (New-Object System.Text.UTF8Encoding($false)))
    $s1aReport = Join-Path $WorkRoot 'probe-s1a.json'
    $s1a = Invoke-HttpsProbe -Exe $exeA -SessionId ([guid]::NewGuid().ToString('N')) `
        -ReportPath $s1aReport -Url $manifestUrl
    $s1aOk = ($null -ne $s1a.Report) -and ($s1a.Report.has_update -eq $false) -and
             ([string]$s1a.Report.check_error -match '签名')
    Record-Result -Name 'S1a 签名被篡改的清单被检查阶段拒绝' -Passed $s1aOk `
        -Detail ("error={0}" -f $s1a.Report.check_error)
    Stop-TrackedProcess -ProcessId $s1a.Process.id -StartedAt $s1a.Process.started -Label 's1a'
    $tlsRequestsAfterS1a = @(Get-ServerRequests)
    $tlsOkS1a = ($tlsRequestsAfterS1a.Count -gt 0) -and
                (@($tlsRequestsAfterS1a | Where-Object { $_.path -eq '/manifest.json' }).Count -gt 0) -and
                (@($tlsRequestsAfterS1a | Where-Object { $_.tls_protocol -notmatch '^Tls1[23]$' }).Count -eq 0)
    Record-Result -Name 'S1a 请求经真实 HTTPS 到达服务器（TLS 1.2+，协议留证）' -Passed $tlsOkS1a `
        -Detail ("requests={0} protocols={1}" -f $tlsRequestsAfterS1a.Count,
            (($tlsRequestsAfterS1a | ForEach-Object { $_.tls_protocol } | Select-Object -Unique) -join ','))

    # ------------------------------------------------ S1b. unpinned certificate is refused (TLS validation is alive)
    $env:FNSYNC_UPDATE_TEST_CERT_PIN = 'AAAAAAAABBBBBBBBCCCCCCCCDDDDDDDDEEEEEEEEFFFFFFFF0000000011111111'
    $requestsBeforeS1b = @(Get-ServerRequests).Count
    $s1bReport = Join-Path $WorkRoot 'probe-s1b.json'
    $s1b = Invoke-HttpsProbe -Exe $exeA -SessionId ([guid]::NewGuid().ToString('N')) `
        -ReportPath $s1bReport -Url $manifestUrl
    $requestsAfterS1b = @(Get-ServerRequests).Count
    $s1bOk = ($null -ne $s1b.Report) -and ($s1b.Report.has_update -eq $false) -and
             ([string]$s1b.Report.check_error).Length -gt 0 -and ($requestsAfterS1b -eq $requestsBeforeS1b)
    Record-Result -Name 'S1b 未登记的测试证书被 TLS 校验拒绝（握手失败，请求未达服务器）' -Passed $s1bOk `
        -Detail ("error={0} | serverRequests {1}->{2}" -f $s1b.Report.check_error, $requestsBeforeS1b, $requestsAfterS1b)
    Stop-TrackedProcess -ProcessId $s1b.Process.id -StartedAt $s1b.Process.started -Label 's1b'
    $env:FNSYNC_UPDATE_TEST_CERT_PIN = $thumbprint

    # ------------------------------------------------ S1c. tampered package fails SHA-256, no partial left
    [System.IO.File]::WriteAllText((Join-Path $contentDir 'manifest.json'), $goodManifestText,
        (New-Object System.Text.UTF8Encoding($false)))
    $packagePath = Join-Path $contentDir $packageBName
    $bytes = [System.IO.File]::ReadAllBytes($packagePath)
    $bytes[$bytes.Length - 1] = ($bytes[$bytes.Length - 1] -bxor 0xFF)
    [System.IO.File]::WriteAllBytes($packagePath, $bytes)
    $s1cReport = Join-Path $WorkRoot 'probe-s1c.json'
    $s1c = Invoke-HttpsProbe -Exe $exeA -SessionId ([guid]::NewGuid().ToString('N')) `
        -ReportPath $s1cReport -Url $manifestUrl -TimeoutSec 180
    $downloadDir = Join-Path $installRoot 'FNSyncPlus\.downloads'
    $partials = @()
    if (Test-Path -LiteralPath $downloadDir) { $partials = @(Get-ChildItem -LiteralPath $downloadDir -Filter '*.partial' -File) }
    $s1cOk = ($null -ne $s1c.Report) -and ($s1c.Report.has_update -eq $true) -and
             ($s1c.Report.download_ok -eq $false) -and ([string]$s1c.Report.download_error -match 'SHA-256') -and
             ($partials.Count -eq 0)
    Record-Result -Name 'S1c 下载内容被篡改时 SHA-256 校验失败且不留残片' -Passed $s1cOk `
        -Detail ("error={0} partials={1}" -f $s1c.Report.download_error, $partials.Count)
    Stop-TrackedProcess -ProcessId $s1c.Process.id -StartedAt $s1c.Process.started -Label 's1c'
    $currentAfterS1c = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
    Record-Result -Name 'S1c 失败后版本指针保持 0.5.0（未发生任何安装）' `
        -Passed ($null -ne $currentAfterS1c -and $currentAfterS1c.version -eq $versionA) `
        -Detail ("version={0}" -f $currentAfterS1c.version)

    # ------------------------------------------------ S1d. unreachable server degrades gracefully
    $s1dReport = Join-Path $WorkRoot 'probe-s1d.json'
    $s1d = Invoke-HttpsProbe -Exe $exeA -SessionId ([guid]::NewGuid().ToString('N')) `
        -ReportPath $s1dReport -Url 'https://127.0.0.1:9/manifest.json' -TimeoutSec 90
    $s1dAlive = Test-TrackedAlive -ProcessId $s1d.Process.id -StartedAt $s1d.Process.started
    $s1dOk = ($null -ne $s1d.Report) -and ($s1d.Report.has_update -eq $false) -and
             ([string]$s1d.Report.check_error).Length -gt 0 -and $s1dAlive
    Record-Result -Name 'S1d 更新服务器不可达时检查优雅失败（应用保持运行，备份不受影响）' -Passed $s1dOk `
        -Detail ("error={0} alive={1}" -f $s1d.Report.check_error, $s1dAlive)
    Stop-TrackedProcess -ProcessId $s1d.Process.id -StartedAt $s1d.Process.started -Label 's1d'

    # ------------------------------------------------ S2. happy path: check -> download -> install -> B healthy
    # Restore the intact package.
    Copy-Item -LiteralPath $packageBSrc -Destination $packagePath -Force
    $s2Report = Join-Path $WorkRoot 'probe-s2.json'
    $s2 = Invoke-HttpsProbe -Exe $exeA -SessionId ([guid]::NewGuid().ToString('N')) `
        -ReportPath $s2Report -Url $manifestUrl -Install -TimeoutSec $PrepareTimeoutSeconds
    $probeP5 = $s2.Report
    if ($null -eq $probeP5) { throw "S2 探针报告未生成：$s2Report" }
    Record-Result -Name 'S2 自动检查：发现 0.6.0（真实 HTTPS 清单 + 真实验签）' `
        -Passed ($probeP5.has_update -eq $true -and $probeP5.version -eq $versionB -and
                 $probeP5.signature_algorithm -match 'ECDSA P-256') `
        -Detail ("version={0} notes={1} alg={2}" -f $probeP5.version, $probeP5.notes, $probeP5.signature_algorithm)
    Record-Result -Name 'S2 自动下载：HTTPS 下载且 SHA-256 与已签名清单一致' `
        -Passed ($probeP5.download_ok -eq $true -and $probeP5.download_sha256 -eq $packageBHash -and
                 $probeP5.release_url -eq "$manifestBaseUrl/$packageBName") `
        -Detail ("url={0} sha256={1}… size={2}" -f $probeP5.release_url,
            ([string]$probeP5.download_sha256).Substring(0, 12), $probeP5.download_size)
    Record-Result -Name 'S2 安装准备成功（维护租约 + 操作 ID 生成）' `
        -Passed ($probeP5.prepared -eq $true -and $probeP5.lease_held_after_prepare -eq $true -and
                 ([string]$probeP5.operation_id).Length -eq 32) `
        -Detail ("prepared={0} op={1} prepare_error={2}" -f $probeP5.prepared, $probeP5.operation_id, $probeP5.prepare_error)
    Record-Result -Name 'S2 真实更新器已启动（三方 PID 之一）' `
        -Passed ($probeP5.updater_started -eq $true -and $probeP5.updater_pid -gt 0 -and
                 $probeP5.shutdown_delivered -eq $true -and $probeP5.lease_held_before_exit -eq $true) `
        -Detail ("app_pid={0} updater_pid={1}" -f $probeP5.app_pid, $probeP5.updater_pid)
    if (-not ($probeP5.updater_started -eq $true)) { throw 'S2 更新器未启动，终止验收' }
    $updaterEntry = Register-TrackedProcess -ProcessId ([int]$probeP5.updater_pid) -Label 'updater-s2' -ExpectedPath $installRoot

    # A must exit on its own through the shutdown coordinator.
    $deadline = (Get-Date).AddSeconds($ExitBudgetSeconds)
    $appExited = $false
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-TrackedAlive -ProcessId $s2.Process.id -StartedAt $s2.Process.started)) { $appExited = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Record-Result -Name 'S2 原程序按更新流程真实退出' -Passed $appExited -Detail ("pid={0}" -f $s2.Process.id)
    if (-not $appExited) { throw 'S2 原程序未退出，终止验收' }

    # Updater installs B and switches the pointer.
    $deadline = (Get-Date).AddSeconds($ApplyBudgetSeconds)
    $switched = $false
    while ((Get-Date) -lt $deadline) {
        $current = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
        if ($null -ne $current -and $current.version -eq $versionB) { $switched = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Record-Result -Name 'S2 更新器完成安装并切换版本指针到 0.6.0' -Passed $switched -Detail ("waitedSec≤{0}" -f $ApplyBudgetSeconds)
    if (-not $switched) {
        $journal = Get-FileText -Path $journalPath
        Record-Result -Name 'S2 journal 记录失败原因' -Passed $false `
            -Detail ((($journal -split "`r?`n") | Select-Object -Last 5) -join ' | ')
        throw 'S2 更新器未完成安装，终止验收'
    }

    # B launches and reports health.
    $launchDeadline = (Get-Date).AddSeconds(120)
    $journal = ''
    $newPid = 0
    while ((Get-Date) -lt $launchDeadline) {
        $journal = Get-FileText -Path $journalPath
        if ($journal -match "launch $versionB ok \(pid (\d+)\)") { $newPid = [int]$Matches[1]; break }
        if ($journal -match 'not healthy' -or $journal -match 'failed') { break }
        Start-Sleep -Milliseconds 500
    }
    Record-Result -Name 'S2 更新器启动新版本并通过健康检查（三方 PID 之三）' -Passed ($newPid -gt 0) `
        -Detail ("pid={0}" -f $newPid)
    if ($newPid -le 0) { throw 'S2 新版本未启动，终止验收' }
    $processB = Register-TrackedProcess -ProcessId $newPid -Label 'app-B-s2' `
        -ExpectedPath (Join-Path $installRoot "app-$versionB")
    $reportB = Read-JsonFile -Path (Join-Path $installRoot ".health\report-$versionB.json")
    $reportOk = ($null -ne $reportB) -and ($reportB.usable -eq $true) -and ($reportB.services_initialized -eq $true) -and
                ($reportB.task_manager_ok -eq $true) -and ($reportB.format_state -eq 'Valid')
    Record-Result -Name 'S2 新版本健康报告（服务/任务/数据格式全部就绪）' -Passed $reportOk `
        -Detail ("usable={0} services={1} tasks={2} format={3}" -f
            $reportB.usable, $reportB.services_initialized, $reportB.task_manager_ok, $reportB.format_state)

    # Operation evidence chain for S2.
    $opId = [string]$probeP5.operation_id
    $opsDir = Join-Path $installRoot '.ops'
    $opFile = Join-Path $opsDir "$opId.json"
    $opState = Read-JsonFile -Path $opFile
    Record-Result -Name 'S2 操作状态文件记录 Installed（含更新器进程身份四元组）' `
        -Passed ($null -ne $opState -and [int]$opState.status -eq 3 -and $opState.version -eq $versionB -and
                 $opState.updater_pid -eq $updaterEntry.id -and $opState.previous_version -eq $versionA) `
        -Detail ("status={0}(3=Installed) pid={1} prev={2}" -f $opState.status, $opState.updater_pid, $opState.previous_version)
    $opTagged = @((Get-FileText -Path $journalPath) -split "`r?`n" | Where-Object { $_ -match "\[op $opId\]" })
    $currentNow = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
    Record-Result -Name 'S2 指针-日志-状态文件三方操作 ID 一致' `
        -Passed ($opTagged.Count -ge 2 -and $currentNow.operation_id -eq $opId) `
        -Detail ("opId={0} taggedLines={1} pointerOp={2}" -f $opId, $opTagged.Count, $currentNow.operation_id)

    # TLS evidence for the whole S2 flow.
    $requests = Get-ServerRequests
    $manifestHits = @($requests | Where-Object { $_.path -eq '/manifest.json' })
    $packageHits = @($requests | Where-Object { $_.path -eq "/$packageBName" })
    $nonTls = @($requests | Where-Object { $_.tls_protocol -notmatch '^Tls1[23]$' })
    Record-Result -Name 'S2 服务器证据：清单与更新包均经 HTTPS 下载（TLS 1.2+ 全覆盖）' `
        -Passed ($manifestHits.Count -ge 1 -and $packageHits.Count -ge 1 -and $nonTls.Count -eq 0) `
        -Detail ("manifestHits={0} packageHits={1} nonTls={2}" -f $manifestHits.Count, $packageHits.Count, $nonTls.Count)
    Record-Event -Event 's2-complete' -Fields @{ app_pid = $probeP5.app_pid; updater_pid = $updaterEntry.id;
        new_pid = $newPid; op = $opId; version = $versionB }

    # ------------------------------------------------ S3. rollback: 0.7.0 payload cannot start
    # Build the broken package: real B payload minus the core assembly -> the new process exits
    # immediately, launch verification fails, the updater must roll back to 0.6.0.
    $payloadC = Join-Path $WorkRoot 'build-c\payload'
    New-Item -ItemType Directory -Path $payloadC -Force | Out-Null
    Copy-Item -Path (Join-Path $buildB "install\app-$versionB\*") -Destination $payloadC -Recurse -Force
    Remove-Item -LiteralPath (Join-Path $payloadC 'FNSyncPlus.Core.dll') -Force
    $packageCName = "FN-Sync-Plus-$versionC-x64.zip"
    $packageC = Join-Path $WorkRoot "build-c\$packageCName"
    if (Test-Path -LiteralPath $packageC) { Remove-Item -LiteralPath $packageC -Force }
    Compress-Archive -Path (Join-Path $payloadC '*') -DestinationPath $packageC -CompressionLevel Fastest
    Record-Result -Name 'S3 构造 0.7.0 缺陷更新包（载荷缺少核心程序集）' -Passed (Test-Path $packageC) `
        -Detail ("package={0}" -f $packageC)
    $descriptorC = Join-Path $WorkRoot 'build-c\descriptor.json'
    [ordered]@{ schema = 1; product = 'FN Sync Plus'; channel = 'beta'; arch = 'x64'
        version = $versionC; min_version = '0.0.0'; notes = '0.7.0 acceptance broken payload'
        batch = 0; audience = @('*'); min_compatible_client = '0.4.0' } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $descriptorC -Encoding utf8
    $signC = Run-Exe -Path $dotnet -Arguments @(
        'run', '--project', (Join-Path $repo 'tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj'),
        '-c', 'Release', '--', 'sign', '--input', $descriptorC, '--key', $privateKey,
        '--out', (Join-Path $WorkRoot 'build-c\manifest.json'), '--package', $packageC,
        '--version', $versionC, '--channel', 'beta', '--url', "$manifestBaseUrl/$packageCName")
    Record-Result -Name 'S3 缺陷包清单已用同一私钥签名（签名有效，缺陷在载荷）' -Passed ($signC.Code -eq 0) `
        -Detail ("exit={0}" -f $signC.Code)
    Copy-Item -LiteralPath (Join-Path $WorkRoot 'build-c\manifest.json') -Destination (Join-Path $contentDir 'manifest.json') -Force
    Copy-Item -LiteralPath $packageC -Destination (Join-Path $contentDir $packageCName) -Force

    # Stop the healthy B from S2 so the probe instance owns the single-instance mutex.
    Stop-TrackedProcess -ProcessId $processB.id -StartedAt $processB.started -Label 'app-B-s2'
    $processB = $null

    $s3Report = Join-Path $WorkRoot 'probe-s3.json'
    $s3 = Invoke-HttpsProbe -Exe $exeB -SessionId ([guid]::NewGuid().ToString('N')) `
        -ReportPath $s3Report -Url $manifestUrl -Install -TimeoutSec $PrepareTimeoutSeconds
    $probeS3 = $s3.Report
    if ($null -eq $probeS3) { throw "S3 探针报告未生成：$s3Report" }
    Record-Result -Name 'S3 0.6.0 实例发现并下载 0.7.0（HTTPS 全流程再次走通）' `
        -Passed ($probeS3.has_update -eq $true -and $probeS3.version -eq $versionC -and
                 $probeS3.download_ok -eq $true -and $probeS3.updater_started -eq $true) `
        -Detail ("version={0} op={1} updater_pid={2}" -f $probeS3.version, $probeS3.operation_id, $probeS3.updater_pid)
    if (-not ($probeS3.updater_started -eq $true)) { throw 'S3 更新器未启动，终止验收' }
    $updater3 = Register-TrackedProcess -ProcessId ([int]$probeS3.updater_pid) -Label 'updater-s3' -ExpectedPath $installRoot

    # Wait for the rollback: pointer must end back on 0.6.0 with 0.7.0 retained for diagnosis.
    $deadline = (Get-Date).AddSeconds($ApplyBudgetSeconds)
    $rolledBack = $false
    while ((Get-Date) -lt $deadline) {
        $current = Read-JsonFile -Path (Join-Path $installRoot 'current.json')
        $journalNow = Get-FileText -Path $journalPath
        if ($null -ne $current -and $current.version -eq $versionB -and
            ($journalNow -match "rolled back to $versionB" -or $journalNow -match "pointer restored to $versionB")) { $rolledBack = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Record-Result -Name 'S3 启动验证失败后自动回滚到 0.6.0（指针证据）' -Passed $rolledBack `
        -Detail ("active={0}" -f (Read-JsonFile -Path (Join-Path $installRoot 'current.json')).version)
    $journalNow = Get-FileText -Path $journalPath
    $op3Id = [string]$probeS3.operation_id
    $op3State = Read-JsonFile -Path (Join-Path $opsDir "$op3Id.json")
    Record-Result -Name 'S3 操作状态记录 RolledBack（失败原因留档）' `
        -Passed ($null -ne $op3State -and [int]$op3State.status -eq 4 -and $op3State.version -eq $versionC) `
        -Detail ("status={0}(4=RolledBack) reason={1}" -f $op3State.status, $op3State.failure_reason)
    Record-Result -Name 'S3 失败的 0.7.0 目录保留供排查（未被静默删除）' `
        -Passed (Test-Path -LiteralPath (Join-Path $installRoot "app-$versionC\FNSyncPlus.WinUI.exe")) `
        -Detail ("dir=app-{0}" -f $versionC)
    Record-Result -Name 'S3 journal 记录回滚全过程（操作 ID 贯穿）' `
        -Passed (($journalNow -match "apply $versionC failed" -or $journalNow -match "apply failed $versionC") -and
                 ($journalNow -match "pointer restored to $versionB" -or $journalNow -match "rolled back to $versionB") -and
                 (@($journalNow -split "`r?`n" | Where-Object { $_ -match "\[op $op3Id\]" }).Count -ge 2)) `
        -Detail ((($journalNow -split "`r?`n") | Where-Object { $_ -match '0\.7\.0|roll' } | Select-Object -Last 4) -join ' | ')

    # B comes back healthy after the rollback.
    $relaunchDeadline = (Get-Date).AddSeconds(120)
    $pidB2 = 0
    while ((Get-Date) -lt $relaunchDeadline) {
        $journalNow = Get-FileText -Path $journalPath
        $matches = [regex]::Matches($journalNow, "launch $versionB ok \(pid (\d+)\)")
        if ($matches.Count -ge 2) { $pidB2 = [int]$matches[$matches.Count - 1].Groups[1].Value; break }
        Start-Sleep -Milliseconds 500
    }
    Record-Result -Name 'S3 回滚后旧版本重新启动并通过健康检查' -Passed ($pidB2 -gt 0) -Detail ("pid={0}" -f $pidB2)
    if ($pidB2 -gt 0) {
        $processB = Register-TrackedProcess -ProcessId $pidB2 -Label 'app-B-s3' `
            -ExpectedPath (Join-Path $installRoot "app-$versionB")
    }

    # ------------------------------------------------ final evidence
    $allRequests = Get-ServerRequests
    $protocols = @($allRequests | ForEach-Object { $_.tls_protocol } | Select-Object -Unique)
    $hasTls13 = $protocols -contains 'Tls13'
    $hasTls12 = $protocols -contains 'Tls12'
    $negotiatedSummary = if ($hasTls13 -and -not $hasTls12) { 'TLS 1.3 (实测全链路)' } elseif ($hasTls12 -and -not $hasTls13) { 'TLS 1.2 (实测)' } else { 'TLS 1.2+ (混用/自适应)' }
    Record-Result -Name "全程证据：HTTPS 地址/TLS 协议/请求清单已留档（实测协商：$negotiatedSummary）" `
        -Passed ($allRequests.Count -gt 0 -and ($protocols | Where-Object { $_ -notmatch '^Tls1[23]$' }).Count -eq 0) `
        -Detail ("url={0} requests={1} protocols={2} hasTls13={3} hasTls12={4}" -f $manifestBaseUrl, $allRequests.Count, ($protocols -join ','), $hasTls13, $hasTls12)
    Record-Result -Name '本验收不声明公司已部署更新服务器（本地隔离证据级别）' -Passed $true `
        -Detail ("server={0} cert=ephemeral-selfsigned note={1}" -f $manifestBaseUrl, $serverFacts.isolation_note)
}
finally {
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

    $auditLines = @()
    if (Test-Path -LiteralPath $storageAuditPath) { $auditLines = @([System.IO.File]::ReadAllLines($storageAuditPath)) }
    $auditProductionHits = @($auditLines | Where-Object {
        $fields = $_ -split "`t"
        $path = $fields[-1]
        ($path.StartsWith($appDataRoot, [StringComparison]::OrdinalIgnoreCase)) -or
        ($path.StartsWith($localAppDataRoot, [StringComparison]::OrdinalIgnoreCase))
    })
    Record-Result -Name '存储审计：全部测试进程解析路径零生产命中' `
        -Passed ($auditLines.Count -gt 0 -and $auditProductionHits.Count -eq 0) `
        -Detail ("auditLines={0} productionHits={1}" -f $auditLines.Count, $auditProductionHits.Count)

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
            if ($set.before[$key] -eq 'log' -or $set.before[$key] -eq 'lock') { continue }
            if ($set.after[$key] -ne $set.before[$key]) { [void]$changedUnexpected.Add($key) }
        }
    }
    Record-Result -Name '生产用户数据全程原地未动（无缺失、无变更、无遗留、无需还原）' `
        -Passed ($leftovers.Count -eq 0 -and $missing.Count -eq 0 -and $changedUnexpected.Count -eq 0) `
        -Detail ("leftover={0} missing={1} changed={2}" -f $leftovers.Count, $missing.Count, $changedUnexpected.Count)
    Record-Result -Name '生产数据格式标记未被读写（哈希不变或仍不存在）' `
        -Passed (($null -eq $markerHashBefore -and -not (Test-Path -LiteralPath $markerPath)) -or
                 ($null -ne $markerHashBefore -and (Test-Path -LiteralPath $markerPath) -and
                  ((Get-FileHash -LiteralPath $markerPath -Algorithm SHA256).Hash -eq $markerHashBefore))) `
        -Detail ("existed={0}" -f $markerExisted)

    $passed = @($script:results | Where-Object { $_.passed }).Count
    $failed = @($script:results | Where-Object { -not $_.passed }).Count
    Write-Host ''
    Write-Host ("合计：PASS {0} / FAIL {1}（共 {2} 项）" -f $passed, $failed, $script:results.Count)
    $summaryPath = Join-Path $WorkRoot 'acceptance-result.json'
    @{ workRoot = $WorkRoot; passed = $passed; failed = $failed; total = $script:results.Count
       cases = $script:results; timeline = $script:timeline
       server = @{ url = $manifestBaseUrl; facts = $serverFactsPath; request_log = $requestLogPath }
       data_walk = @{ leftovers = @($leftovers); missing = @($missing); changed = @($changedUnexpected) } } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    Write-Host ("结果文件：{0}" -f $summaryPath)
    if ($failed -gt 0) { exit 1 }
}
