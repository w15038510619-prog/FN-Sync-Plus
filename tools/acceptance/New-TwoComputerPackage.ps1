[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Dotnet = 'dotnet'
)
$ErrorActionPreference='Stop'
$repo=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$sha=& git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sha -notmatch '^[a-f0-9]{40}$') { throw 'Cannot identify the build commit.' }
$dirty=& git -C $repo status --porcelain
if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit the source changes before producing the copyable package.' }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new package output directory.' }
$root=(New-Item -ItemType Directory -Path $OutputDirectory).FullName
$stage=Join-Path $root 'FN-Sync-Plus-TwoPC'
& (Join-Path $PSScriptRoot 'Publish-AcceptanceRunner.ps1') -Dotnet $Dotnet -SourceSha $sha -OutputDirectory $stage
& (Join-Path $PSScriptRoot 'Test-AcceptanceRunnerPublish.ps1') -Runner (Join-Path $stage 'FNSyncPlus.AcceptanceRunner.exe') -SourceSha $sha -OutputDirectory (Join-Path $root 'offline-runner-evidence')
if ($LASTEXITCODE -ne 0) { throw 'Standalone runner verification failed.' }
foreach ($name in 'Test-ComputerA.ps1','Test-ComputerB.ps1','Invoke-TwoComputerAcceptance.ps1','Merge-TwoComputerReports.ps1') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $stage
}
Copy-Item -LiteralPath (Join-Path $repo 'docs\two-computer-operator-guide.md') -Destination (Join-Path $stage 'README.md')
$session=@{ Schema=1; RunId=[guid]::NewGuid().ToString('D'); SourceSha=$sha; BuiltAtUtc=[DateTime]::UtcNow.ToString('o') }
$session | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'session.json') -Encoding UTF8
Get-ChildItem -LiteralPath $stage -File | Sort-Object Name | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content -LiteralPath (Join-Path $stage 'SHA256SUMS.txt') -Encoding ascii
$zip=Join-Path $root "FN-Sync-Plus-TwoPC-$($sha.Substring(0,12)).zip"
Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal
Get-FileHash -LiteralPath $zip,(Join-Path $stage 'FNSyncPlus.AcceptanceRunner.exe')
