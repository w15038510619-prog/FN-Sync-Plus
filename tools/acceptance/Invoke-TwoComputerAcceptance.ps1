[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('A','B')][string]$Role,
    [ValidateSet('Run','Peer','Status','Observe')][string]$Phase = 'Status',
    [string]$Server,
    [string]$CertificateSha256,
    [string]$StartAtUtc,
    [string]$PeerEvidence,
    [string]$OutputDirectory,
    [switch]$ClocksSynchronized,
    [string]$SessionFile = (Join-Path $PSScriptRoot 'session.json')
)
$ErrorActionPreference = 'Stop'
$utf8 = New-Object Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
$session = Get-Content -LiteralPath $SessionFile -Raw -Encoding UTF8 | ConvertFrom-Json
$runId = [guid]::ParseExact($session.RunId, 'D')
if ($runId -eq [guid]::Empty) { throw 'Use a nonempty session GUID.' }
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $env:USERPROFILE "FN-TwoPC-$($runId.ToString('N'))-$Role"
}
$root = [IO.Path]::GetFullPath($OutputDirectory)
if ($root -like '\\*' -or $root -eq [IO.Path]::GetPathRoot($root)) { throw 'Use a dedicated directory on a fixed local disk.' }
$runner = Join-Path $PSScriptRoot 'FNSyncPlus.AcceptanceRunner.exe'
$workspace = Join-Path $root 'workspace'

function Save-ReadableReport([string]$JsonPath) {
    $report = Get-Content -LiteralPath $JsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $lines = @("# Computer $Role acceptance", '', "Run $runId", '', "Build $($report.Build)", '',
        '| Test | Level | Status | Evidence and reason |', '| --- | --- | --- | --- |')
    foreach ($test in $report.Results) {
        $reason = $test.Reason.Replace('|','/').Replace("`r",' ').Replace("`n",' ')
        $lines += "| $($test.TestId) | $($test.TestLevel) | $($test.Status) | $reason |"
    }
    [IO.File]::WriteAllLines([IO.Path]::ChangeExtension($JsonPath,'.md'), [string[]]$lines, $utf8)
    foreach ($test in $report.Results) { Write-Host "$($test.Status) $($test.TestId) $($test.Reason)" }
}

function Invoke-Runner([string[]]$Arguments, [string]$ReportName) {
    $output = & $runner @Arguments
    $code = $LASTEXITCODE
    $path = Join-Path $root $ReportName
    [IO.File]::WriteAllText($path, ($output -join [Environment]::NewLine), $utf8)
    Save-ReadableReport $path
    $report = Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($code -ne $report.ExitCode) { throw 'Runner process and report exit codes differ.' }
    return $code
}

if ($Phase -eq 'Status') {
    if (!(Test-Path -LiteralPath $root)) { $null = New-Item -ItemType Directory -Path $root }
    $report = [ordered]@{ Build = 'not-run'; ExitCode = 2; Results = @(
        @{ TestId='P0-1'; TestLevel='RealNAS'; Status='BLOCKED'; Evidence=$null; Reason='Run both roles using their own authorized accounts, then exchange device-evidence.json and run Peer.' },
        @{ TestId='P0-2'; TestLevel='RealNAS'; Status='BLOCKED'; Evidence=$null; Reason='A shared start time and overlapping real worker intervals are required.' },
        @{ TestId='P0-3'; TestLevel='RealNAS'; Status='BLOCKED'; Evidence=$null; Reason='Installed-UI account switching and active network interruption require recorded operator observations.' },
        @{ TestId='PreviouslyAcceptedRecoveryAndUpdate'; TestLevel='RealNAS'; Status='SKIP'; Evidence='User-confirmed existing acceptance'; Reason='Completed current/history recovery and 0.4.1 to 0.4.2 upgrade are reused.' }
    ) }
    $path = Join-Path $root 'status.json'
    [IO.File]::WriteAllText($path, ($report | ConvertTo-Json -Depth 8), $utf8)
    Save-ReadableReport $path
    exit 2
}

if (!(Test-Path -LiteralPath $root)) { $null = New-Item -ItemType Directory -Path $root }
if ($Phase -eq 'Observe') {
    $path = Join-Path $root 'operator-observations.json'
    if (Test-Path -LiteralPath $path) { throw 'Preserve the existing observations. Use a different OutputDirectory for a new observation report.' }
    $tests = @(
        @('InstalledProfileIndependence','Confirm the installed app uses this PC local user profile. Confirm configurations and credentials were not copied from the other PC.'),
        @('ActivePauseResume','Using only a synthetic test task in the installed app, pause an active task, wait for it to stop, resume and verify its recovered marker SHA-256.'),
        @('ActiveDisconnect','Disconnect only the installed app NAS session during a synthetic task. Verify writing stops, the last committed test snapshot is recoverable, and any uncertain commit or lease is reported.'),
        @('AccountSwitchGuard','If switching accounts is authorized on this PC, verify the old test task does not write as the new account. Otherwise choose BLOCKED.'),
        @('ReconnectRecovery','Reconnect the original account. Verify safe test-task retry, current marker SHA-256 and retained history. Record any lock check or controlled recovery.'),
        @('TokenExpiry','Only if actual token/session rejection was observed, verify the task stops and prompts for authentication. Otherwise choose BLOCKED.')
    )
    $rows = foreach ($test in $tests) {
        Write-Host $test[1]
        $status = (Read-Host "$($test[0]) PASS / FAIL / BLOCKED / SKIP").Trim().ToUpperInvariant()
        if ($status -notin 'PASS','FAIL','BLOCKED','SKIP') { throw 'Enter one of the four statuses.' }
        $evidence = Read-Host 'Enter a log/report file path and the observed result. For FAIL include reproduction steps'
        $fileHash = $null
        if ($status -in 'PASS','FAIL') {
            $proof = Read-Host 'Path to the retained evidence file (required for PASS or FAIL)'
            if (!(Test-Path -LiteralPath $proof -PathType Leaf)) { throw 'An existing evidence file is required.' }
            $fileHash = @{ Path=[IO.Path]::GetFullPath($proof); Sha256=(Get-FileHash -LiteralPath $proof).Hash }
        }
        if ([string]::IsNullOrWhiteSpace($evidence)) { throw 'A reason is required.' }
        @{ TestId=$test[0]; TestLevel='RealNAS'; Status=$status; Evidence=@{ Kind='OperatorAttestation'; File=$fileHash }; Reason=$evidence }
    }
    $code=0
    if (@($rows | Where-Object Status -eq 'FAIL').Count) { $code=1 }
    elseif (@($rows | Where-Object Status -eq 'BLOCKED').Count) { $code=2 }
    $report=@{ Build='operator-observations'; ExitCode=$code; Results=@($rows); RunId=$runId; Role=$Role; AtUtc=[DateTime]::UtcNow.ToString('o') }
    [IO.File]::WriteAllText($path, ($report | ConvertTo-Json -Depth 10), $utf8)
    Save-ReadableReport $path
    exit $code
}

if (!(Test-Path -LiteralPath $runner -PathType Leaf)) { throw 'Fully extract the acceptance package, including its EXE.' }
if (!$Server) { $Server = Read-Host 'NAS HTTPS address, for example https://nas.example:5667' }
$arguments = @('--server',$Server,'--authorize-nas')
if ($CertificateSha256) { $arguments += @('--cert-sha256',$CertificateSha256) }
if ($Phase -eq 'Run') {
    if (Test-Path -LiteralPath $workspace) { throw 'This run already has a workspace. Preserve it and create a new session for another run.' }
    if (!$StartAtUtc) { $StartAtUtc = Read-Host 'Shared future UTC start time agreed with the other operator (ISO format)' }
    Write-Host "Role $Role creates 3 synthetic files including a 16 MiB payload. NAS root Codex_FN_Test_TwoPC_$($runId.ToString('N'))/$Role."
    Write-Host 'Log in using only this computer operator account. Password input is hidden. Both Windows clocks must be synchronized.'
    $arguments = @('--two-pc-run') + $arguments + @('--allow-test-writes','--role',$Role,'--run-id',$runId.ToString('D'),
        '--workspace',$workspace,'--start-at',$StartAtUtc)
    if ($ClocksSynchronized) { $arguments += '--clocks-synchronized' }
    $exitCode = Invoke-Runner $arguments 'run.json'
    Write-Host "Send only $workspace\device-evidence.json to the other operator. Keep logs, source-files.json and cleanup-inventory.json locally."
    exit $exitCode
}
if (!$PeerEvidence) { $PeerEvidence = Read-Host 'Path to the other computer device-evidence.json' }
$arguments = @('--two-pc-peer') + $arguments + @('--own',(Join-Path $workspace 'device-evidence.json'),'--peer',$PeerEvidence)
$exitCode = Invoke-Runner $arguments 'peer.json'
exit $exitCode
