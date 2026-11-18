<#
.SYNOPSIS
    Produces a versioned, updatable FN Sync Plus installation plus a signed update manifest.

.DESCRIPTION
    Layout produced under -OutputRoot:

        <OutputRoot>/install/
            FN_Sync_Plus.exe          stable launcher (the updater, single file)
            current.json              points at the active version directory
            previous.json             last known-good version (for rollback)
            app-<version>/            immutable application payload
            update.log                append-only update journal

        <OutputRoot>/FN-Sync-Plus-<version>-x64.zip   payload package used by the updater
        <OutputRoot>/manifest.json                    signed manifest (only with -SignManifest)

    Generate the signing pair outside the repository. Supply -UpdatePublicKeyPath so the public
    key is embedded in both the application and launcher. The signing private key is read only
    by UpdateTool when -SignManifest is enabled.

.NOTES
    Without -SignManifest this script produces an unsigned manifest skeleton and says so. The
    resulting build is a controlled-pilot candidate, not a production release.
#>
param(
    [string]$Dotnet = 'dotnet',
    [ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceSha,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\Release_WinUI'),
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$Version = '0.4.4',
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')][string]$MinimumClientVersion = '0.4.0',
    [switch]$ControlledOnlineBeta,
    [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
    [switch]$SkipTests,
    [switch]$SignManifest,
    [string]$SigningKeyPath = '',
    [string]$ManifestBaseUrl = '',
    [int]$ReleaseBatch = 0,
    [string]$Audience = '*',
    # Public key pinned into the client (and reused by the updater). Required for any build that
    # must verify updates; omit it and the updater fails closed instead of trusting the network.
    [string]$UpdatePublicKeyPath = '',
    # Data format this build can read. Written into the payload and the pointer so a rollback can
    # be refused when it would move backwards onto data it cannot read.
    [int]$SupportedDataFormat = 1
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 命令失败，退出码 $LASTEXITCODE" }
}

$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ($ControlledOnlineBeta -and ($Channel -ne 'beta' -or !$UpdatePublicKeyPath)) {
    throw 'Controlled online Beta builds require the Beta channel and an explicit public key.'
}
if ($UpdatePublicKeyPath) {
    $UpdatePublicKeyPath = (Resolve-Path -LiteralPath $UpdatePublicKeyPath).Path
    $pem = [IO.File]::ReadAllText($UpdatePublicKeyPath)
    if (([regex]::Matches($pem, '-----BEGIN ')).Count -ne 1 -or $pem -notmatch '-----BEGIN PUBLIC KEY-----') {
        throw 'UpdatePublicKeyPath must contain only a public PEM key.'
    }
}
if ($SignManifest) {
    if (!$SigningKeyPath -or !$UpdatePublicKeyPath) { throw 'Signing requires both the private and public key paths.' }
    $SigningKeyPath = (Resolve-Path -LiteralPath $SigningKeyPath).Path
    $repoPrefix = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path.TrimEnd('\') + '\'
    if (($SigningKeyPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $SigningKeyPath.StartsWith($OutputRoot.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Keep the Beta signing private key outside the repository and output directory.'
    }
    [Uri]$baseUri = $null
    if (![Uri]::TryCreate($ManifestBaseUrl,[UriKind]::Absolute,[ref]$baseUri) -or
        $baseUri.Scheme -ne 'https' -or $baseUri.Query -or $baseUri.Fragment -or $baseUri.UserInfo) {
        throw 'ManifestBaseUrl must be an HTTPS directory without query, fragment or credentials.'
    }
    $ManifestBaseUrl = $baseUri.AbsoluteUri.TrimEnd('/')
    if ([version]$Version -le [version]$MinimumClientVersion) { throw 'An online release must be newer than its minimum client.' }
}

Push-Location $PSScriptRoot
try {
    if (-not $SkipTests) {
        # Never package a build whose safety regressions fail.
        Invoke-Dotnet -Arguments @('run', '--project', 'FNSyncPlus.Tests\FNSyncPlus.Tests.csproj', '-c', 'Release', '--', '--integrity-regressions')
    }

    $installDir = Join-Path $OutputRoot 'install'
    $appDir = Join-Path $installDir "app-$Version"
    $packagePath = Join-Path $OutputRoot "FN-Sync-Plus-$Version-x64.zip"
    foreach ($path in @($installDir, $packagePath)) {
        if (Test-Path -LiteralPath $path) { throw "输出路径已存在，拒绝覆盖：$path" }
    }

    # Compile the release tool before pinned payloads. Rebuilding it between publishes would
    # rebuild Core without release properties and could pollute incremental resource outputs.
    if ($UpdatePublicKeyPath -or $SignManifest) {
        Invoke-Dotnet -Arguments @('build', '..\tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj', '-c', 'Release')
    }

    # 1. Application payload
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    $commonPublish = @()
    if ($SourceSha) { $commonPublish += "-p:SourceRevisionId=$SourceSha" }
    if ($ControlledOnlineBeta) { $commonPublish += '-p:ControlledOnlineBeta=true' }
    if ($UpdatePublicKeyPath) { $commonPublish += "-p:UpdatePublicKeyPath=$UpdatePublicKeyPath" }
    # Changing the external pinned resource must never reuse an unpinned incremental assembly.
    Invoke-Dotnet -Arguments (@('clean', 'FNSyncPlus.WinUI\FNSyncPlus.WinUI.csproj', '-c', 'Release',
        '-p:RuntimeIdentifier=win-x64', '-p:WindowsPackageType=None', "-p:Version=$Version") + $commonPublish)
    Invoke-Dotnet -Arguments (@('publish', 'FNSyncPlus.WinUI\FNSyncPlus.WinUI.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:WindowsPackageType=None', "-p:Version=$Version", '-o', $appDir) + $commonPublish)
    if ($Channel -eq 'beta') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TEST_ONLY.txt') -Destination (Join-Path $appDir 'TEST_ONLY.txt') -Force }

    $required = @('FNSyncPlus.WinUI.exe', 'resources.pri', 'hostfxr.dll', 'Microsoft.WindowsAppRuntime.Bootstrap.dll')
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $appDir $name) -PathType Leaf)) { throw "发布结果缺少 $name" }
    }

    if ($UpdatePublicKeyPath) {
        # A child process releases the assembly before Windows PowerShell creates the ZIP.
        Invoke-Dotnet -Arguments @('run', '--project', '..\tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj', '-c', 'Release', '--no-build', '--',
            'verify-key', '--embedded-core', (Join-Path $appDir 'FNSyncPlus.Core.dll'), '--public', $UpdatePublicKeyPath, '--build-version', $Version)
    }

    # 2. Never ship credentials or configuration
    $privateNames = @('credentials.dat', 'settings.json', 'updates.json', 'data-format.json', 'update-private.pem', 'update-public.pem')
    $privateFiles = @(Get-ChildItem -LiteralPath $appDir -File -Recurse -Force | Where-Object {
        $name = $_.Name
        $match = $privateNames -contains $name
        if (-not $match) {
            # Preserved copies such as data-format.json.corrupt-<ts> are secrets too.
            foreach ($prefix in $privateNames) {
                if ($name -like "$prefix*") { $match = $true; break }
            }
        }
        $match
    })
    if ($privateFiles.Count -gt 0) { throw "程序目录意外包含凭据或配置：$($privateFiles[0].FullName)" }

    # 3. Stable, version-independent launcher in the install root.
    #    Published straight into the install root: a temporary publish directory would have to be
    #    deleted afterwards, and deleting hundreds of freshly published files is exactly what
    #    endpoint protection (and the safe-delete policy on build machines) refuses to do.
    Invoke-Dotnet -Arguments (@('publish', 'FNSyncPlus.Updater\FNSyncPlus.Updater.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        "-p:Version=$Version", '-o', $installDir) + $commonPublish)
    Copy-Item -LiteralPath (Join-Path $installDir 'FNSyncPlus.Updater.exe') -Destination (Join-Path $installDir 'FN_Sync_Plus.exe') -Force

    # 3b. Declare what this build can read. The updater uses it to refuse an unsafe rollback.
    $buildMetadata = @{ version = $Version; supported_data_format = $SupportedDataFormat }
    if ($SourceSha) { $buildMetadata.source_sha = $SourceSha }
    $buildMetadata |
        ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $appDir 'build-meta.json') -Encoding utf8

    # 4. Package the payload (what the updater extracts)
    Compress-Archive -Path (Join-Path $appDir '*') -DestinationPath $packagePath -CompressionLevel Optimal
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    $packageSize = (Get-Item -LiteralPath $packagePath).Length

    # 5. Initial pointers so the launcher works before the first update
    $payloadHash = 'built-' + (Get-Date -Format 'yyyyMMddHHmmss')
    @{ version = $Version; directory = "app-$Version"; installed_at = (Get-Date).ToUniversalTime().ToString('o'); payload_sha256 = $payloadHash; supported_data_format = $SupportedDataFormat } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $installDir 'current.json') -Encoding utf8
    if (Test-Path -LiteralPath (Join-Path $installDir 'previous.json')) { Remove-Item -LiteralPath (Join-Path $installDir 'previous.json') -Force }

    # 6. Manifest
    $manifestPath = Join-Path $OutputRoot 'manifest.json'
    if ($SignManifest) {
        $descriptor = @{
            schema = 1; product = 'FN Sync Plus'; channel = $Channel; arch = 'x64'; version = $Version
            min_version = $MinimumClientVersion; released_at = (Get-Date).ToUniversalTime().ToString('o')
            notes = "FN Sync Plus $Version ($Channel)"; url = "$ManifestBaseUrl/FN-Sync-Plus-$Version-x64.zip"
            sha256 = $packageHash; size = $packageSize; batch = $ReleaseBatch
            audience = @($Audience); min_compatible_client = $MinimumClientVersion; signature = ''
        }
        $descriptorPath = Join-Path $OutputRoot 'descriptor.json'
        $descriptor | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $descriptorPath -Encoding utf8
        Invoke-Dotnet -Arguments @('run', '--project', '..\tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj', '-c', 'Release', '--no-build', '--',
            'sign', '--input', $descriptorPath, '--key', $SigningKeyPath, '--out', $manifestPath, '--package', $packagePath,
            '--version', $Version, '--channel', $Channel, '--url', "$ManifestBaseUrl/FN-Sync-Plus-$Version-x64.zip")
        Invoke-Dotnet -Arguments @('run', '--project', '..\tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj', '-c', 'Release', '--no-build', '--',
            'verify', '--manifest', $manifestPath, '--public', $UpdatePublicKeyPath, '--current', $MinimumClientVersion,
            '--channel', $Channel, '--batch', "$ReleaseBatch", '--group', $Audience)
    }
    else {
        Write-Host '本次构建未生成在线更新清单。'
    }

    Write-Host ''
    Write-Host "安装目录      : $installDir"
    Write-Host "启动器        : $(Join-Path $installDir 'FN_Sync_Plus.exe')"
    Write-Host "更新包        : $packagePath"
    Write-Host "更新包 SHA-256: $packageHash"
    Write-Host "更新包大小    : $packageSize"
    if (Test-Path -LiteralPath $manifestPath) { Write-Host "更新清单      : $manifestPath" }
    Write-Host "已生成 $Channel 候选产物；实际投运结论以本轮验收报告为准。"
}
finally { Pop-Location }
