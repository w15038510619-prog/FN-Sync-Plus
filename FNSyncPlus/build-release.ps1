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

    Signing needs a private key that must never be committed or shipped. Generate it once with:
        dotnet run --project tools/FNSyncPlus.UpdateTool -- keygen --private keys/update-private.pem --public keys/update-public.pem
    and copy update-public.pem to FNSyncPlus/FNSyncPlus.Core/Updates/update-public.pem before
    building the client, so the public key is embedded into the binaries.

.NOTES
    Without -SignManifest this script produces an unsigned manifest skeleton and says so. The
    resulting build is a controlled-pilot candidate, not a production release.
#>
param(
    [string]$Dotnet = 'dotnet',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\Release_WinUI'),
    [string]$Version = '0.4.0',
    [ValidateSet('stable', 'beta')][string]$Channel = 'beta',
    [switch]$SkipTests,
    [switch]$SignManifest,
    [string]$SigningKeyPath = '',
    [string]$ManifestBaseUrl = '',
    [int]$ReleaseBatch = 0,
    [string]$Audience = '*'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 命令失败，退出码 $LASTEXITCODE" }
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

    # 1. Application payload
    New-Item -ItemType Directory -Path $appDir -Force | Out-Null
    Invoke-Dotnet -Arguments @('publish', 'FNSyncPlus.WinUI\FNSyncPlus.WinUI.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:WindowsPackageType=None', "-p:Version=$Version", "-o", $appDir)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TEST_ONLY.txt') -Destination (Join-Path $appDir 'TEST_ONLY.txt') -Force

    $required = @('FNSyncPlus.WinUI.exe', 'resources.pri', 'hostfxr.dll', 'Microsoft.WindowsAppRuntime.Bootstrap.dll')
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $appDir $name) -PathType Leaf)) { throw "发布结果缺少 $name" }
    }

    # 2. Never ship credentials or configuration
    $privateNames = @('credentials.dat', 'settings.json', 'updates.json', 'data-format.json')
    $privateFiles = @(Get-ChildItem -LiteralPath $appDir -File -Recurse -Force | Where-Object {
        $privateNames -contains $_.Name -or ($privateNames | Where-Object { $_.Name -like "$_*" })
    })
    if ($privateFiles.Count -gt 0) { throw "程序目录意外包含凭据或配置：$($privateFiles[0].FullName)" }

    # 3. Stable, version-independent launcher in the install root
    $launcherTmp = Join-Path $OutputRoot 'updater-tmp'
    Invoke-Dotnet -Arguments @('publish', 'FNSyncPlus.Updater\FNSyncPlus.Updater.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        "-p:Version=$Version", '-o', $launcherTmp)
    Copy-Item -LiteralPath (Join-Path $launcherTmp 'FNSyncPlus.Updater.exe') -Destination (Join-Path $installDir 'FN_Sync_Plus.exe') -Force
    Copy-Item -LiteralPath (Join-Path $launcherTmp 'FNSyncPlus.Updater.exe') -Destination (Join-Path $installDir 'FNSyncPlus.Updater.exe') -Force
    Remove-Item -LiteralPath $launcherTmp -Recurse -Force

    # 4. Package the payload (what the updater extracts)
    Compress-Archive -Path (Join-Path $appDir '*') -DestinationPath $packagePath -CompressionLevel Optimal
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash
    $packageSize = (Get-Item -LiteralPath $packagePath).Length

    # 5. Initial pointers so the launcher works before the first update
    $payloadHash = 'built-' + (Get-Date -Format 'yyyyMMddHHmmss')
    @{ version = $Version; directory = $appDir; installed_at = (Get-Date).ToUniversalTime().ToString('o'); payload_sha256 = $payloadHash } |
        ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $installDir 'current.json') -Encoding utf8
    if (Test-Path -LiteralPath (Join-Path $installDir 'previous.json')) { Remove-Item -LiteralPath (Join-Path $installDir 'previous.json') -Force }

    # 6. Manifest
    $manifestPath = Join-Path $OutputRoot 'manifest.json'
    if ($SignManifest) {
        if ([string]::IsNullOrWhiteSpace($SigningKeyPath) -or -not (Test-Path -LiteralPath $SigningKeyPath)) {
            throw '已要求签名，但 -SigningKeyPath 缺失或不可读。私钥不得放在仓库内。'
        }
        if ([string]::IsNullOrWhiteSpace($ManifestBaseUrl)) { throw '已要求签名，但缺少 -ManifestBaseUrl（必须是 HTTPS）' }
        $descriptor = @{
            schema = 1; product = 'FN Sync Plus'; channel = $Channel; arch = 'x64'; version = $Version
            min_version = '0.0.0'; released_at = (Get-Date).ToUniversalTime().ToString('o')
            notes = "FN Sync Plus $Version ($Channel)"; url = "$ManifestBaseUrl/FN-Sync-Plus-$Version-x64.zip"
            sha256 = $packageHash; size = $packageSize; batch = $ReleaseBatch
            audience = @($Audience); min_compatible_client = '0.4.0'; signature = ''
        }
        $descriptorPath = Join-Path $OutputRoot 'descriptor.json'
        $descriptor | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $descriptorPath -Encoding utf8
        Invoke-Dotnet -Arguments @('run', '--project', '..\tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj', '-c', 'Release', '--',
            'sign', '--input', $descriptorPath, '--key', $SigningKeyPath, '--out', $manifestPath, '--package', $packagePath,
            '--version', $Version, '--channel', $Channel, '--url', "$ManifestBaseUrl/FN-Sync-Plus-$Version-x64.zip")
    }
    else {
        Write-Host '未签名：未提供 -SignManifest，生成的清单不会被客户端接受。'
    }

    Write-Host ''
    Write-Host "安装目录      : $installDir"
    Write-Host "启动器        : $(Join-Path $installDir 'FN_Sync_Plus.exe')"
    Write-Host "更新包        : $packagePath"
    Write-Host "更新包 SHA-256: $packageHash"
    Write-Host "更新包大小    : $packageSize"
    if (Test-Path -LiteralPath $manifestPath) { Write-Host "更新清单      : $manifestPath" }
    Write-Host '这是受控试点候选版；未完成安全验收前不得用于正式资料的唯一备份。'
}
finally { Pop-Location }
