[CmdletBinding()]
param(
    [ValidateSet('LocalFixture', 'SnapshotBefore', 'RealNasBackup', 'VerifyCurrent', 'VerifyHistory')]
    [string]$Mode = 'LocalFixture',
    [ValidateSet('SmbShare', 'FnOsPersonal')][string]$Provider,
    [string]$RemoteBackupPath,
    [string]$Server,
    [string]$CertificateSha256,
    [string]$AcceptanceId,
    [string]$BaselinePath,
    [string]$RestoreRoot,
    [string]$ManifestPath,
    [switch]$AuthorizeNas,
    [switch]$SourceQuiescent
)

$runner = Join-Path $PSScriptRoot 'FNSyncPlus.AcceptanceRunner.exe'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw '找不到独立验收程序。请完整解压测试 ZIP 后，在 acceptance 目录运行脚本。'
}

switch ($Mode) {
    'LocalFixture' {
        $runnerArgs = @('--test-restore-conflict-engine', '--mode', 'LocalFixture')
    }
    { $_ -in 'VerifyCurrent', 'VerifyHistory' } {
        if (-not $RestoreRoot -or -not $ManifestPath) { throw '请提供本地恢复目录与样本校验清单。' }
        $runnerArgs = @('--verify-restored', '--target', $RestoreRoot, '--fixture', $ManifestPath)
        if ($Mode -eq 'VerifyHistory') { $runnerArgs += '--history' }
    }
    default {
        $operation = if ($Mode -eq 'SnapshotBefore') { '--snapshot-before' } else { '--test-restore-conflict-engine' }
        if (-not $Provider -or -not $RemoteBackupPath -or -not $AcceptanceId -or -not $BaselinePath) {
            throw '请提供连接方式、测试备份目录、验收编号与基线文件。'
        }
        $runnerArgs = @($operation, '--mode', 'RealNasBackup', '--provider', $Provider, '--source', $RemoteBackupPath,
            '--acceptance-id', $AcceptanceId, '--baseline', $BaselinePath)
        if ($Server) { $runnerArgs += @('--server', $Server) }
        if ($CertificateSha256) { $runnerArgs += @('--cert-sha256', $CertificateSha256) }
        if ($AuthorizeNas) { $runnerArgs += '--authorize-nas' }
        if ($SourceQuiescent) { $runnerArgs += '--source-quiescent' }
    }
}

& $runner @runnerArgs
exit $LASTEXITCODE
