[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [ValidateSet('V1', 'V2')][string]$Phase = 'V1'
)

$runner = Join-Path $PSScriptRoot 'FNSyncPlus.AcceptanceRunner.exe'
if (-not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw '找不到独立验收程序。请完整解压测试 ZIP 后，在 acceptance 目录运行脚本。'
}
$operation = if ($Phase -eq 'V1') { '--new-fixture' } else { '--update-fixture' }

& $runner $operation --source $OutputDir --fixture $ManifestPath
exit $LASTEXITCODE
