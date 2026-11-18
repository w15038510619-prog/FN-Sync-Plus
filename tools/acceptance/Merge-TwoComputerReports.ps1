[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ComputerA,
    [Parameter(Mandatory)][string]$ComputerB,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new report directory.' }
$root = (New-Item -ItemType Directory -Path $OutputDirectory).FullName
$reports = @{}
$evidenceFiles = @()
foreach ($role in 'A','B') {
    $directory = $ComputerA
    if ($role -eq 'B') { $directory = $ComputerB }
    foreach ($phase in 'run','peer','operator-observations') {
        $path = Join-Path $directory "$phase.json"
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $data = Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json
            foreach ($row in $data.Results) {
                if ($row.Status -notin 'PASS','FAIL','BLOCKED','SKIP') { throw "Invalid status in $path" }
            }
            $reports["$role-$phase"] = $data
            $evidenceFiles += @{ Path=[IO.Path]::GetFullPath($path); Sha256=(Get-FileHash -LiteralPath $path).Hash }
        }
    }
}
function Row([string]$Id,[string]$Status,[string]$Reason) {
    @{ TestId=$Id; Status=$Status; Reason=$Reason }
}
function Evaluate([string]$Id,[string[]]$Required,[string]$Reason,[switch]$AllowSkip) {
    $statuses = foreach ($requirement in $Required) {
        $role,$phase,$testId = $requirement -split '/',3
        $report = $reports["$role-$phase"]
        $matched = @($report.Results | Where-Object TestId -eq $testId)
        if ($matched.Count -ne 1) { 'BLOCKED' } else { $matched[0].Status }
    }
    $status='PASS'
    if ($statuses -contains 'FAIL') { $status='FAIL' }
    elseif ($statuses -contains 'BLOCKED') { $status='BLOCKED' }
    elseif ($AllowSkip -and $statuses -contains 'SKIP') { $status='SKIP' }
    elseif (@($statuses | Where-Object { $_ -ne 'PASS' }).Count) { $status='BLOCKED' }
    Row $Id $status $Reason
}
$identityOk = $false
$a = @($reports['A-run'].Results | Where-Object TestId -eq 'DeviceEvidence')
$b = @($reports['B-run'].Results | Where-Object TestId -eq 'DeviceEvidence')
if ($a.Count -eq 1 -and $b.Count -eq 1) {
    $a = $a[0].Evidence
    $b = $b[0].Evidence
    $identityOk = $a.Role -eq 'A' -and $b.Role -eq 'B' -and $a.RunId -eq $b.RunId -and
        $a.MachineKey -ne $b.MachineKey -and $a.Server -eq $b.Server -and $a.BuildSha -eq $b.BuildSha
}
$rows = @(
    (Evaluate 'P0-1-OwnAccountBackupAndHistory' @('A/run/DeviceEvidence','B/run/DeviceEvidence','A/run/RequestAudit','B/run/RequestAudit') 'Each real account completed its own synthetic current and historical hash checks.'),
    (Evaluate 'P0-1-CrossAccountSoftwareGuard' @('A/peer/DistinctRealAccounts','B/peer/DistinctRealAccounts','A/peer/PeerReadPermission','B/peer/PeerReadPermission','A/peer/PeerTaskWriteBlocked','B/peer/PeerTaskWriteBlocked','A/peer/RequestAudit','B/peer/RequestAudit') 'Both directions require explicit server read denial and a real worker UID mismatch rejection with zero outgoing requests.'),
    (Evaluate 'P0-2-ConcurrentBackup' @('A/peer/ConcurrentInitial','B/peer/ConcurrentInitial','A/peer/ConcurrentIncremental','B/peer/ConcurrentIncremental','A/peer/OwnSnapshotAfterPeerRun','B/peer/OwnSnapshotAfterPeerRun','A/peer/OwnHashesAfterBothRun','B/peer/OwnHashesAfterBothRun') 'Both phases must overlap and retain the original own-account snapshot and actual recovered file bytes.'),
    (Evaluate 'P0-3-IsolatedSchedulerGuards' @('A/run/PausedTriggerBlocked','B/run/PausedTriggerBlocked','A/run/OfflineTriggerBlocked','B/run/OfflineTriggerBlocked','A/run/LoggedOutWriteBlocked','B/run/LoggedOutWriteBlocked','A/run/GuardSnapshotUnchanged','B/run/GuardSnapshotUnchanged') 'Real NAS session with isolated scheduler guards. Offline flag checks do not count as physical network interruption.'),
    (Evaluate 'P0-3-InstalledAppOperations' @('A/operator-observations/ActivePauseResume','A/operator-observations/ActiveDisconnect','A/operator-observations/AccountSwitchGuard','A/operator-observations/ReconnectRecovery') 'One authorized pilot PC can supply the installed-app checkpoints. Token expiry remains separately classified.'),
    (Evaluate 'P0-4-RunnerIndependence' @('A/peer/DistinctMachinesAndProfiles','B/peer/DistinctMachinesAndProfiles') 'Independent physical Windows identities and independently created transient profiles.'),
    (Evaluate 'P0-4-InstalledProfileIndependence' @('A/operator-observations/InstalledProfileIndependence','B/operator-observations/InstalledProfileIndependence') 'Operators confirm installed configurations remain local and credentials were not copied between computers.'),
    (Evaluate 'P0-3-ActualTokenExpiry' @('A/operator-observations/TokenExpiry') 'Requires an actual observed session rejection. An operator may explicitly SKIP this unscheduled failure condition with a reason.' -AllowSkip),
    (Row 'PeerServerWriteAcl' 'SKIP' 'No direct write probe is sent to the other personal space. Software UID guard and server read ACL evidence are reported independently.'),
    (Row 'CompletedRecoveryAndOnlineUpgrade' 'SKIP' 'Reuse user-confirmed completed real recovery and 0.4.1 to 0.4.2 acceptance.')
)
if (!$identityOk) {
    foreach ($row in $rows | Where-Object Status -eq 'PASS') {
        $row.Status='BLOCKED'
        $row.Reason += ' The two supplied run reports do not yet establish distinct devices, opposite roles and one common session.'
    }
}
# Never suppress a failure merely because another required test or report is missing.
$actualFailures = @()
foreach ($key in $reports.Keys) {
    foreach ($row in $reports[$key].Results | Where-Object Status -eq 'FAIL') {
        $actualFailures += @{ Report=$key; TestId=$row.TestId; Reason=$row.Reason; Evidence=$row.Evidence }
    }
}
$exitCode=0
if (@($rows | Where-Object Status -eq 'FAIL').Count -or $actualFailures.Count) { $exitCode=1 }
elseif (@($rows | Where-Object Status -eq 'BLOCKED').Count) { $exitCode=2 }
$result=@{ Schema=1; AtUtc=[DateTime]::UtcNow.ToString('o'); ExitCode=$exitCode; Results=$rows; Evidence=$evidenceFiles; Failures=$actualFailures }
[IO.File]::WriteAllText((Join-Path $root 'two-computer-result.json'),($result | ConvertTo-Json -Depth 18),$utf8)
$lines=@('# Two-computer acceptance', '', '| Test | Status | Reason |', '| --- | --- | --- |')
foreach ($row in $rows) { $lines += "| $($row.TestId) | $($row.Status) | $($row.Reason.Replace('|','/')) |" }
foreach ($failure in $actualFailures) { $lines += "`nFAIL $($failure.Report) $($failure.TestId) $($failure.Reason)" }
[IO.File]::WriteAllLines((Join-Path $root 'two-computer-result.md'),[string[]]$lines,$utf8)
$rows | ForEach-Object { [pscustomobject]$_ } | Format-Table TestId,Status -AutoSize
exit $exitCode
