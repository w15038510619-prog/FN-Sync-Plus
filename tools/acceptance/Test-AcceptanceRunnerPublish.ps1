[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Runner,
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]{40}$')][string]$SourceSha,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$resolved = (Resolve-Path -LiteralPath $Runner).Path
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new smoke-test output directory.' }
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$standalone = Join-Path $root 'exe-only'
New-Item -ItemType Directory -Path $standalone | Out-Null
$exe = Join-Path $standalone 'FNSyncPlus.AcceptanceRunner.exe'
Copy-Item -LiteralPath $resolved -Destination $exe

function Invoke-Report {
    param([string]$Name, [int]$Expected, [string[]]$Arguments)
    $raw = & $exe @Arguments
    $processCode = $LASTEXITCODE
    $raw | Set-Content -LiteralPath (Join-Path $root "$Name.json") -Encoding utf8
    $report = ($raw -join [Environment]::NewLine) | ConvertFrom-Json
    if ($processCode -ne $Expected -or $report.ExitCode -ne $processCode -or -not $report.Results) {
        throw "$Name expected $Expected, got process code $processCode and JSON code $($report.ExitCode)."
    }
    foreach ($row in $report.Results) {
        if (!$row.TestId -or $row.TestLevel -notin 'Mock','Local','RealNAS' -or
            $row.Status -notin 'PASS','FAIL','BLOCKED','SKIP' -or !$row.Reason -or
            $null -eq $row.PSObject.Properties['Evidence']) { throw "$Name contains an incomplete result." }
        if ($row.TestLevel -eq 'RealNAS' -and $row.Status -eq 'PASS') { throw "$Name unexpectedly reports a real NAS PASS." }
    }
    Write-Host "$Name OK ($processCode)"
    return $report
}

$originalPath = $env:Path
$originalDotnetRoot = $env:DOTNET_ROOT
$originalDotnetX64 = $env:DOTNET_ROOT_X64
$originalExtract = $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
Push-Location -LiteralPath $standalone
try {
    $env:Path = ''
    $env:DOTNET_ROOT = Join-Path $root 'missing-dotnet'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $root 'runtime-extraction'
    $helpText = & $exe --help
    if ($LASTEXITCODE -ne 0 -or ($helpText -join ' ') -notmatch 'LocalFixture') { throw 'Help failed.' }
    $helpText | Set-Content -LiteralPath (Join-Path $root 'help.txt') -Encoding utf8
    $versionText = & $exe --version
    if ($LASTEXITCODE -ne 0) { throw 'Version query failed.' }
    $version = ($versionText -join [Environment]::NewLine) | ConvertFrom-Json
    if ($version.BuildSha -ne $SourceSha) { throw 'Incorrect executable Git source SHA.' }
    $versionText | Set-Content -LiteralPath (Join-Path $root 'version.json') -Encoding utf8

    $null = Invoke-Report 'self-test' 0 @('--self-test')
    $local = Invoke-Report 'local-conflict' 0 @('--test-restore-conflict-engine','--mode','LocalFixture')
    if ($local.Results[0].Evidence.ProviderType -ne 'LocalFileSystem') { throw 'Local evidence was misclassified.' }
    $dual = Invoke-Report 'dual-account-no-credentials' 2 @('--test-dual-account-live')
    if (@($dual.Results).Count -ne 7 -or @($dual.Results | Where-Object Status -ne 'BLOCKED').Count -ne 0) {
        throw 'Expected each dual-account result to be blocked.'
    }
    $id = [guid]::NewGuid().ToString('D')
    $missing = Join-Path $root 'missing.json'
    $localDir = Join-Path $root 'fixture source'
    $fixtureJson = Join-Path $root 'fixture.json'
    $null = Invoke-Report 'missing-baseline' 2 @('--test-restore-conflict-engine','--mode','RealNasBackup','--acceptance-id',$id,'--baseline',$missing)
    $null = Invoke-Report 'invalid-remote-path' 1 @('--snapshot-before','--mode','RealNasBackup','--acceptance-id',$id,'--baseline',$missing,
        '--provider','SmbShare','--source',$localDir,'--authorize-nas','--source-quiescent')
    $null = Invoke-Report 'invalid-option' 1 @('--test-restore-conflict-engine','--nonsense')
    $null = Invoke-Report 'fixture-v1' 0 @('--new-fixture','--source',$localDir,'--fixture',$fixtureJson)
    $null = Invoke-Report 'verify-v1' 0 @('--verify-restored','--target',$localDir,'--fixture',$fixtureJson)
    $null = Invoke-Report 'fixture-v2' 0 @('--update-fixture','--source',$localDir,'--fixture',$fixtureJson)
    $null = Invoke-Report 'verify-v2' 0 @('--verify-restored','--target',$localDir,'--fixture',"$fixtureJson.v2.json")
    $null = Invoke-Report 'reject-current-as-history' 1 @('--verify-restored','--target',$localDir,'--fixture',"$fixtureJson.v2.json",'--history')
} finally {
    Pop-Location
    $env:Path = $originalPath
    $env:DOTNET_ROOT = $originalDotnetRoot
    $env:DOTNET_ROOT_X64 = $originalDotnetX64
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = $originalExtract
}
