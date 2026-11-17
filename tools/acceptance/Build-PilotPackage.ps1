[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [Parameter(Mandatory = $true)][string]$InnoCompiler,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$sha = & git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[a-f0-9]{40}$') { throw 'Could not identify the source commit.' }
$dirty = & git -C $repo status --porcelain
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Build from a clean checkout so the package provenance is accurate.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new package output directory.' }
$inno = (Resolve-Path -LiteralPath $InnoCompiler).Path
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$publish = Join-Path $root 'acceptance-publish'
& (Join-Path $PSScriptRoot 'Publish-AcceptanceRunner.ps1') -Dotnet $Dotnet -OutputDirectory $publish -SourceSha $sha
$runner = Join-Path $publish 'FNSyncPlus.AcceptanceRunner.exe'
& (Join-Path $PSScriptRoot 'Test-AcceptanceRunnerPublish.ps1') -Runner $runner -SourceSha $sha -OutputDirectory (Join-Path $root 'offline-evidence')

$version = '0.4.0'
$product = Join-Path $root 'application-build'
& (Join-Path $repo 'FNSyncPlus\build-release.ps1') -Dotnet $Dotnet -OutputRoot $product -Version $version -Channel beta -SourceSha $sha
$payload = Join-Path $product 'install'
$pointerPath = Join-Path $payload 'current.json'
$pointer = Get-Content -LiteralPath $pointerPath -Raw | ConvertFrom-Json
$pointer.directory = "app-$version"
$pointer | ConvertTo-Json -Compress | Set-Content -LiteralPath $pointerPath -Encoding utf8

$stage = Join-Path $root 'FN-Sync-Plus-Pilot'
$installerName = 'FN-Sync-Plus-Beta-Pilot-Windows-x64-Setup'
foreach ($name in 'installer','acceptance','docs') {
    New-Item -ItemType Directory -Path (Join-Path $stage $name) | Out-Null
}
$installerDir = Join-Path $stage 'installer'
& $inno "/DPayloadDir=$payload" "/DAppVersion=$version" "/O$installerDir" "/F$installerName" (Join-Path $repo 'FNSyncPlus\installer\FNSyncPlus.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)." }
$installer = Join-Path $installerDir "$installerName.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'The installer was not created.' }

$acceptanceDir = Join-Path $stage 'acceptance'
Copy-Item -LiteralPath $runner -Destination $acceptanceDir
foreach ($script in 'Test-IndependentRecoveryAcceptance.ps1','Test-DualAccountAcceptance.ps1','New-RecoveryAcceptancePackage.ps1') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination $acceptanceDir
}
foreach ($document in '测试人员操作说明.md','异机恢复验收说明.md','双账号测试说明.md','问题反馈模板.md') {
    Copy-Item -LiteralPath (Join-Path $repo "docs\$document") -Destination (Join-Path $stage 'docs')
}

[ordered]@{
    Schema = 1
    Product = 'FN Sync Plus'
    Version = "$version-beta"
    Channel = 'controlled-pilot'
    Runtime = 'win-x64'
    InstallerSourceSha = $sha
    AcceptanceRunnerSourceSha = $sha
    BuiltAtUtc = [DateTime]::UtcNow.ToString('o')
    Installer = "installer/$installerName.exe"
    AcceptanceRunner = 'acceptance/FNSyncPlus.AcceptanceRunner.exe'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8

Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
    "$( (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() )  $relative"
} | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding utf8

$zip = Join-Path $root "FN-Sync-Plus-Beta-Pilot-Windows-x64-$($sha.Substring(0, 12)).zip"
Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal
Get-FileHash -LiteralPath $zip,$installer,$runner -Algorithm SHA256
