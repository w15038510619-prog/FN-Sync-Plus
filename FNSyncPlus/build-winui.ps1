param(
    [string]$Dotnet = 'dotnet',
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\Release_WinUI')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & $Dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 命令失败，退出码 $LASTEXITCODE" }
}

Push-Location $PSScriptRoot
try {
    Invoke-Dotnet -Arguments @('--version')
    Invoke-Dotnet -Arguments @('run', '--project', 'FNSyncPlus.Tests\FNSyncPlus.Tests.csproj', '-c', 'Release', '--', '--integrity-regressions')

    $buildId = [guid]::NewGuid().ToString('N')
    $buildRoot = Join-Path $OutputRoot "candidate_$buildId"
    $publishDir = Join-Path $buildRoot 'publish'
    $zipPath = Join-Path $OutputRoot "FN_Sync_Plus_v0.4.0-beta_WinUI3_x64_candidate_$buildId.zip"
    if ((Test-Path -LiteralPath $buildRoot) -or (Test-Path -LiteralPath $zipPath)) {
        throw '候选输出路径已存在，拒绝覆盖。'
    }
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    Invoke-Dotnet -Arguments @('publish', 'FNSyncPlus.WinUI\FNSyncPlus.WinUI.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:WindowsPackageType=None', '-o', $publishDir)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TEST_ONLY.txt') -Destination (Join-Path $publishDir 'TEST_ONLY.txt')
    $required = @('FNSyncPlus.WinUI.exe', 'resources.pri', 'hostfxr.dll', 'Microsoft.WindowsAppRuntime.Bootstrap.dll', 'TEST_ONLY.txt')
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir $name) -PathType Leaf)) { throw "发布结果缺少 $name" }
    }
    $privateFiles = @(Get-ChildItem -LiteralPath $publishDir -File -Recurse -Force |
        Where-Object { $_.Name -in @('credentials.dat', 'settings.json') -or $_.Name -like 'credentials.dat.*' -or $_.Name -like 'settings.json.*' })
    if ($privateFiles.Count -gt 0) { throw '发布目录意外包含凭据或配置，已停止打包。' }

    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $privateEntries = @($zip.Entries | Where-Object {
            $name = [IO.Path]::GetFileName($_.FullName)
            $name -in @('credentials.dat', 'settings.json') -or $name -like 'credentials.dat.*' -or $name -like 'settings.json.*'
        })
        if ($privateEntries.Count -gt 0) { throw '压缩包意外包含凭据或配置，已停止交付。' }
        foreach ($name in $required) {
            $entry = $zip.GetEntry($name)
            if ($null -eq $entry) { throw "压缩包缺少 $name" }
            $stream = $entry.Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $archiveHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
            finally { $stream.Dispose(); $sha.Dispose() }
            $sourceHash = (Get-FileHash -LiteralPath (Join-Path $publishDir $name) -Algorithm SHA256).Hash
            if ($archiveHash -cne $sourceHash) { throw "压缩包文件校验失败：$name" }
        }
    }
    finally { $zip.Dispose() }

    Write-Host "WinUI x64 测试候选包：$zipPath"
    Write-Host '本脚本运行了隔离安全回归；正式验收完成前请勿用于正式文件。'
}
finally { Pop-Location }
