[CmdletBinding()]
param(
    [string]$Server,
    [string]$CertificateSha256,
    [string]$RootA,
    [string]$RootB,
    [switch]$AuthorizeNas,
    [switch]$AllowTestWrites
)

$runner = Join-Path $PSScriptRoot 'FNSyncPlus.AcceptanceRunner.exe'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw '找不到独立验收程序。请完整解压测试 ZIP 后，在 acceptance 目录运行脚本。'
}
$runnerArgs = @('--test-dual-account-live')
if ($Server) { $runnerArgs += @('--server', $Server) }
if ($CertificateSha256) { $runnerArgs += @('--cert-sha256', $CertificateSha256) }
if ($RootA) { $runnerArgs += @('--root-a', $RootA) }
if ($RootB) { $runnerArgs += @('--root-b', $RootB) }
if ($AuthorizeNas) { $runnerArgs += '--authorize-nas' }
if ($AllowTestWrites) { $runnerArgs += '--allow-test-writes' }

& $runner @runnerArgs
exit $LASTEXITCODE
