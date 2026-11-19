[CmdletBinding()]
param(
    [string]$Version = '0.4.6',
    [string]$MinimumClientVersion = '0.4.3',
    [Parameter(Mandatory)][string]$OutputRoot,
    [Parameter(Mandatory)][string]$PublicKeyPath,
    [Parameter(Mandatory)][string]$PrivateKeyPath,
    [string]$Dotnet = 'dotnet',
    [string]$Iscc = (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$sha = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[a-f0-9]{40}$') { throw 'Cannot resolve source commit.' }
if (& git -C $repo status --porcelain) { throw 'Commit source changes before producing a delivery.' }
$base = 'https://dev.sk-fj.com:8081/fnsync/stable'
$build = Join-Path $OutputRoot 'build'
$delivery = Join-Path $OutputRoot 'delivery'
if (Test-Path -LiteralPath $OutputRoot) { throw 'OutputRoot must be a fresh directory.' }
& (Join-Path $repo 'FNSyncPlus\build-release.ps1') -Dotnet $Dotnet -Version $Version -MinimumClientVersion $MinimumClientVersion -SourceSha $sha -OutputRoot $build -Channel stable -SignManifest -SigningKeyPath $PrivateKeyPath -UpdatePublicKeyPath $PublicKeyPath -ManifestBaseUrl $base
New-Item -ItemType Directory -Path $delivery | Out-Null
$core = Join-Path $build "install\app-$Version\FNSyncPlus.Core.dll"
& $Dotnet run --project (Join-Path $repo 'tools\FNSyncPlus.UpdateTool') -c Release --no-build -- verify --manifest (Join-Path $build 'manifest.json') --embedded-core $core --build-version $Version --current $MinimumClientVersion --channel stable
if ($LASTEXITCODE -ne 0) { throw 'Embedded client trust verification failed.' }
$setupName = "FN-Sync-Plus-$Version-Windows-x64-Setup"
& $Iscc "/DPayloadDir=$build\install" "/DAppVersion=$Version" "/O$delivery" "/F$setupName" (Join-Path $repo 'FNSyncPlus\installer\FNSyncPlus.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
Copy-Item -LiteralPath (Join-Path $build "FN-Sync-Plus-$Version-x64.zip"),(Join-Path $build 'manifest.json') -Destination $delivery
Copy-Item -LiteralPath $PublicKeyPath -Destination (Join-Path $delivery 'update-public.pem')
@{ version=$Version; channel='stable'; source_sha=$sha; authenticode='NotSigned'; update_manifest_signature='ECDSA-P256-SHA256'; built_at=(Get-Date).ToUniversalTime().ToString('o') } | ConvertTo-Json | Set-Content (Join-Path $delivery 'build-provenance.json') -Encoding utf8
Get-ChildItem -LiteralPath $delivery -File | Sort-Object Name | ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant())  $($_.Name)" } | Set-Content (Join-Path $delivery 'SHA256SUMS.txt') -Encoding ascii
Write-Output $delivery
