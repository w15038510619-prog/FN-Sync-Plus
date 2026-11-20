param(
    [ValidateSet('Run','Peer','Status','Observe')][string]$Phase = 'Status',
    [string]$Server, [string]$CertificateSha256, [string]$StartAtUtc,
    [string]$PeerEvidence, [string]$OutputDirectory, [string]$SessionFile,
    [switch]$ClocksSynchronized
)
& (Join-Path $PSScriptRoot 'Invoke-TwoComputerAcceptance.ps1') -Role A @PSBoundParameters
exit $LASTEXITCODE
