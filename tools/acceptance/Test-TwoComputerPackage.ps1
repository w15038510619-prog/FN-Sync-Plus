[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new offline test directory.' }
$root=(New-Item -ItemType Directory -Path $OutputDirectory).FullName
$stage=(Resolve-Path -LiteralPath $PackageDirectory).Path
foreach ($role in 'A','B') {
    $entry=Join-Path $stage "Test-Computer$role.ps1"
    $roleOutput=Join-Path $root $role
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $entry -Phase Status -OutputDirectory $roleOutput
    if ($LASTEXITCODE -ne 2) { throw "Computer $role did not return BLOCKED before a real test." }
    $status=Get-Content -LiteralPath (Join-Path $roleOutput 'status.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (@($status.Results | Where-Object Status -eq 'PASS').Count) { throw 'Unexecuted real tests must not PASS.' }
}
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $stage 'Merge-TwoComputerReports.ps1') -ComputerA (Join-Path $root 'A') -ComputerB (Join-Path $root 'B') -OutputDirectory (Join-Path $root 'merged')
if ($LASTEXITCODE -ne 2) { throw 'The report merger must mark missing run/peer evidence BLOCKED.' }
$merged=Get-Content -LiteralPath (Join-Path $root 'merged\two-computer-result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if (@($merged.Results | Where-Object Status -eq 'PASS').Count) { throw 'Missing device evidence incorrectly passed.' }
# A failure must survive aggregation even when device identity and other reports are missing.
@{ Results=@(@{ TestId='OwnInitial'; Status='FAIL'; Reason='Synthetic regression check, not a NAS result'; Evidence=$null }) } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'A\run.json') -Encoding UTF8
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $stage 'Merge-TwoComputerReports.ps1') -ComputerA (Join-Path $root 'A') -ComputerB (Join-Path $root 'B') -OutputDirectory (Join-Path $root 'failed-merge')
if ($LASTEXITCODE -ne 1) { throw 'A real failure must not disappear under missing peer evidence.' }
'PASS both Windows PowerShell 5.1 role entry points, missing-evidence aggregation, and failure propagation'
exit 0
