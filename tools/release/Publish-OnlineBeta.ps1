[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z_][a-zA-Z0-9_-]*@[a-zA-Z0-9][a-zA-Z0-9.-]*$')][string]$SshTarget,
    [ValidateRange(1,65535)][int]$SshPort = 22,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$PublicKeySha256,
    [ValidateSet('beta','stable')][string]$Channel = 'beta',
    [string]$Dotnet = 'dotnet',
    [switch]$ProvisionTrust,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
$baseUrl = "https://dev.sk-fj.com:8081/fnsync/$Channel"
$remoteRoot = '/data/fnsync-updates/.beta-staging'
$release = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$public = Join-Path $release 'update-public.pem'
if (!(Test-Path -LiteralPath $public)) { $public = Join-Path $release 'beta-update-public.pem' }
$manifest = Join-Path $release 'manifest.json'
$descriptor = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
$version = [string]$descriptor.version
$minimum = [string]$descriptor.min_compatible_client
if ($version -cnotmatch '^(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*)){2}$' -or
    $minimum -cnotmatch '^(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*)){2}$' -or
    [version]$minimum -ge [version]$version -or $minimum -cne $descriptor.min_version) {
    throw 'Invalid release or minimum client version.'
}
$name = "FN-Sync-Plus-$version-x64.zip"
$zip = Join-Path $release $name
$downloadUrl = "$baseUrl/$name"
$manifestUrl = "$baseUrl/manifest.json"
if ($descriptor.url -cne $downloadUrl -or $descriptor.channel -cne $Channel -or $descriptor.arch -cne 'x64') {
    throw 'The signed manifest does not target the expected Beta feed and payload.'
}
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant() }
function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed (exit $LASTEXITCODE)." }
}
$keyHash = $PublicKeySha256.ToLowerInvariant()
$zipHash = Get-Sha256 $zip
$zipSize = (Get-Item -LiteralPath $zip).Length
$manifestHash = Get-Sha256 $manifest
if ((Get-Sha256 $public) -cne $keyHash -or $zipHash -ne $descriptor.sha256 -or $zipSize -ne $descriptor.size) {
    throw 'Local public key or payload does not match the expected fingerprint, size, or hash.'
}
$tool = Join-Path $PSScriptRoot '..\FNSyncPlus.UpdateTool\FNSyncPlus.UpdateTool.csproj'
function Test-SignedManifest([string]$Path) {
    Invoke-Checked $Dotnet @('run','--project',$tool,'-c','Release','--','verify','--manifest',$Path,
        '--public',$public,'--current',$minimum,'--channel',$Channel)
}
Test-SignedManifest $manifest
Write-Host "SSH: $SshTarget port $SshPort"
Write-Host "Staging: $remoteRoot/<new unique staging directory>"
Write-Host "ZIP first: $downloadUrl ($zipSize bytes, SHA-256 $zipHash)"
Write-Host "Manifest last: $manifestUrl (SHA-256 $manifestHash)"
Write-Host "Pinned public key SHA-256: $keyHash; provision if absent: $ProvisionTrust"
if ($PlanOnly) { return }

$sshOpts = @('-o','StrictHostKeyChecking=yes','-o','BatchMode=yes','-o','ConnectTimeout=10')
$stageId = [Guid]::NewGuid().ToString('N')
$stage = "$remoteRoot/$stageId"
function Invoke-Remote([string]$Command) {
    Invoke-Checked 'ssh.exe' ($sshOpts + @('-p',"$SshPort",$SshTarget,$Command))
}
function Send-StagedFile([string]$LocalPath, [string]$Name) {
    # Use the current directory so OpenSSH never interprets a Windows drive letter as a host.
    Push-Location -LiteralPath (Split-Path -Parent $LocalPath)
    try {
        $leaf = '.\' + (Split-Path -Leaf $LocalPath)
        Invoke-Checked 'scp.exe' ($sshOpts + @('-P',"$SshPort",$leaf,"${SshTarget}:$stage/$Name"))
    } finally { Pop-Location }
}

$download = Join-Path ([IO.Path]::GetTempPath()) "FNSyncPlus-Beta-Publish-$stageId"
New-Item -ItemType Directory -Path $download | Out-Null
try {
    Invoke-Remote "command -v python3 >/dev/null && command -v openssl >/dev/null && test -d /data/fnsync-updates/$Channel && umask 077 && mkdir -p '$remoteRoot' && chmod 700 '$remoteRoot' && mkdir '$stage'"
    Send-StagedFile (Join-Path $PSScriptRoot 'publish-beta.py') 'publish-beta.py'
    $publisher = "python3 '$stage/publish-beta.py'"
    $identity = "--stage $stageId --key-sha256 $keyHash --channel $Channel"
    $artifact = "$identity --version $version --size $zipSize --sha256 $zipHash"
    if ($ProvisionTrust) {
        Send-StagedFile $public 'public.upload'
        Invoke-Remote "$publisher pin $identity"
    }
    Send-StagedFile $zip 'payload.upload'
    Invoke-Remote "$publisher zip $artifact"

    $remoteZip = Join-Path $download $name
    Invoke-Checked 'curl.exe' @('--fail','--silent','--show-error','--proto','=https','--max-time','600',
        '--output',$remoteZip,$downloadUrl)
    if ((Get-Item -LiteralPath $remoteZip).Length -ne $zipSize -or (Get-Sha256 $remoteZip) -cne $zipHash) {
        throw 'The HTTPS ZIP does not match. The manifest has not been uploaded.'
    }
    $publicArtifacts = @{
        "FN-Sync-Plus-$version-Windows-x64-Setup.exe" = "FN-Sync-Plus-$version-Windows-x64-Setup.exe"
        'SHA256SUMS.txt' = "SHA256SUMS-$version.txt"
        'release-notes.md' = "release-notes-$version.md"
        'rollback.md' = "rollback-$version.md"
        'build-provenance.json' = "build-provenance-$version.json"
    }
    foreach ($localName in $publicArtifacts.Keys) {
        $localFile = Join-Path $release $localName
        if (!(Test-Path -LiteralPath $localFile -PathType Leaf)) { continue }
        $remoteName = $publicArtifacts[$localName]
        $artifactSize = (Get-Item -LiteralPath $localFile).Length
        $artifactHash = Get-Sha256 $localFile
        Send-StagedFile $localFile 'artifact.upload'
        Invoke-Remote "$publisher artifact $identity --version $version --name $remoteName --size $artifactSize --sha256 $artifactHash"
        $verified = Join-Path $download $remoteName
        Invoke-Checked 'curl.exe' @('--fail','--silent','--show-error','--proto','=https','--max-time','600','--output',$verified,"$baseUrl/$remoteName")
        if ((Get-Item -LiteralPath $verified).Length -ne $artifactSize -or (Get-Sha256 $verified) -cne $artifactHash) { throw "HTTPS artifact mismatch: $remoteName" }
        Remove-Item -LiteralPath $verified
    }
    Send-StagedFile $manifest 'manifest.upload'
    Invoke-Remote "$publisher manifest $artifact --minimum-client $minimum --manifest-sha256 $manifestHash"

    $remoteManifest = Join-Path $download 'manifest.json'
    Invoke-Checked 'curl.exe' @('--fail','--silent','--show-error','--proto','=https','--max-time','30',
        '--header','Cache-Control: no-cache','--output',$remoteManifest,$manifestUrl)
    if ((Get-Sha256 $remoteManifest) -cne $manifestHash) { throw 'The HTTPS manifest SHA-256 does not match.' }
    Test-SignedManifest $remoteManifest
    Write-Host "Published and verified over HTTPS: $Channel $version"
} finally {
    foreach ($file in $name,'manifest.json') {
        $path = Join-Path $download $file
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path }
    }
    Remove-Item -LiteralPath $download
}
