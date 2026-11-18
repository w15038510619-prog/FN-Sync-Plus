[CmdletBinding()]
param(
    [string]$Dotnet = 'dotnet',
    [Parameter(Mandatory)][string]$InnoCompiler,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$PublicKey,
    [Parameter(Mandatory)][string]$PrivateKey,
    [string]$BaseVersion = '0.4.1',
    [string]$UpdateVersion = '0.4.2',
    [string]$ManifestBaseUrl = 'https://dev.sk-fj.com:8081/fnsync/beta'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sha = & git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[a-f0-9]{40}$') { throw 'Cannot identify the build source.' }
$dirty = & git -C $repo status --porcelain
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Build from a clean checkout.' }
if ($BaseVersion -notmatch '^\d+\.\d+\.\d+$' -or $UpdateVersion -notmatch '^\d+\.\d+\.\d+$' -or
    [version]$BaseVersion -ge [version]$UpdateVersion) { throw 'Supply two ascending numeric releases.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory.' }
$root = [IO.Path]::GetFullPath($OutputDirectory)
$public = (Resolve-Path -LiteralPath $PublicKey).Path
$private = (Resolve-Path -LiteralPath $PrivateKey).Path
$inno = (Resolve-Path -LiteralPath $InnoCompiler).Path
foreach ($parent in $root,$repo) {
    if ($private.StartsWith($parent.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Keep the private key outside build output and source control.'
    }
}
$release = Join-Path $root 'delivery'
New-Item -ItemType Directory -Path $release | Out-Null
$base = Join-Path $root "build-$BaseVersion"
$update = Join-Path $root "build-$UpdateVersion"
$common = @{
    Dotnet = $Dotnet; Channel = 'beta'; ControlledOnlineBeta = $true
    SourceSha = $sha; UpdatePublicKeyPath = $public; MinimumClientVersion = $BaseVersion
}
$builder = Join-Path $repo 'FNSyncPlus\build-release.ps1'
& $builder @common -Version $BaseVersion -OutputRoot $base
& $builder @common -Version $UpdateVersion -OutputRoot $update -SkipTests -SignManifest -SigningKeyPath $private -ManifestBaseUrl $ManifestBaseUrl

$tool = Join-Path $repo 'tools\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj'
$manifest = Join-Path $update 'manifest.json'
foreach ($pair in @(@($base,$BaseVersion),@($update,$UpdateVersion))) {
    $core = Join-Path $pair[0] "install\app-$($pair[1])\FNSyncPlus.Core.dll"
    & $Dotnet run --project $tool -c Release --no-build -- verify --manifest $manifest --embedded-core $core --build-version $pair[1] --require-controlled-beta --current $BaseVersion --channel beta
    if ($LASTEXITCODE -ne 0) { throw 'The deployed client did not verify the update using its embedded public key.' }
}

$zipName = "FN-Sync-Plus-$UpdateVersion-x64.zip"
$setupName = "FN-Sync-Plus-$BaseVersion-Beta-Setup"
$payload = Join-Path $base 'install'
& $inno "/DPayloadDir=$payload" "/DAppVersion=$BaseVersion" "/O$release" "/F$setupName" (Join-Path $repo 'FNSyncPlus\installer\FNSyncPlus.iss')
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath (Join-Path $release "$setupName.exe"))) { throw 'The base installer was not built.' }

$zip = Join-Path $update $zipName
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($file in 'FNSyncPlus.WinUI.exe','FNSyncPlus.Core.dll','resources.pri','build-meta.json') {
        if ($null -eq $archive.GetEntry($file)) { throw "Payload ZIP lacks required root entry: $file" }
    }
    if (@($archive.Entries | Where-Object FullName -match '(?i)(^|/)(app-[0-9.]+/|install/|acceptance/|installer/|.*setup\.exe$|.*private.*\.pem$)').Count) {
        throw 'Wrong ZIP layout or unexpected installer or private key.'
    }
} finally { $archive.Dispose() }
Copy-Item -LiteralPath $zip,$manifest -Destination $release
Copy-Item -LiteralPath $public -Destination (Join-Path $release 'beta-update-public.pem')
[ordered]@{
    source_commit = $sha; channel = 'beta'; base_version = $BaseVersion
    online_version = $UpdateVersion; update_mode = 'check-only'
    manifest_url = $ManifestBaseUrl.TrimEnd('/')+'/manifest.json'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $release 'build-provenance.json') -Encoding utf8
Get-ChildItem -LiteralPath $release -File | Sort-Object Name | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Get-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt')
