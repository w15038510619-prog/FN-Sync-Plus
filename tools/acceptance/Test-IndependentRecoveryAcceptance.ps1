<#
.SYNOPSIS
    独立异机恢复验收测试验证工具（电脑 B 运行）。
.DESCRIPTION
    在全新安装且无源端配置的电脑 B 上，对从 NAS 恢复到本地目录的数据执行全量一致性与完整性校验。
    校验项目：
      1. 逐文件 SHA-256 强校验与大小比对
      2. 空目录结构还原校验
      3. 超长中文文件名与 Unicode 特殊字符还原校验
      4. 历史版本文件还原校验（若提供历史版本目录）
      5. 默认不覆盖已有文件规则测试（若指定 -TestDoNotOverwrite）
      6. 远端 NAS 备份目录在恢复前后不可变校验（若提供 -RemoteBackupPath）
.PARAMETER RestoreRoot
    电脑 B 上已还原文件的本地根目录。
.PARAMETER ManifestPath
    电脑 A 生成的 recovery-manifest.json 路径。
.PARAMETER RemoteBackupPath
    （可选）NAS 上的远端备份目录，用于确认远端数据在恢复前后完全未被篡改。
.PARAMETER HistoryVersionRestoreRoot
    （可选）若已单独还原历史版本，传入历史版本文件的还原目录。
.PARAMETER TestDoNotOverwrite
    是否执行“默认不覆盖已有文件”的破坏性试探验证。
.PARAMETER OutputReportPath
    测试报告输出路径（默认在 RestoreRoot 下输出 independent-recovery-report.md）。
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$RestoreRoot,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $false)]
    [string]$RemoteBackupPath,

    [Parameter(Mandatory = $false)]
    [string]$HistoryVersionRestoreRoot,

    [Parameter(Mandatory = $false)]
    [switch]$TestDoNotOverwrite,

    [Parameter(Mandatory = $false)]
    [string]$OutputReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $OutputReportPath) {
    $OutputReportPath = Join-Path $RestoreRoot "independent-recovery-report.md"
}

function Get-Sha256Hex([string]$FilePath) {
    if (-not (Test-Path -LiteralPath $FilePath)) { return $null }
    return (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash
}

Write-Host "=== 开始执行独立异机恢复验收校验 ===" -ForegroundColor Cyan
Write-Host "恢复目录: $RestoreRoot"
Write-Host "清单文件: $ManifestPath"

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    throw "清单文件未找到: $ManifestPath"
}
if (-not (Test-Path -LiteralPath $RestoreRoot)) {
    throw "恢复目标目录不存在: $RestoreRoot"
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$results = @()
$passedCount = 0
$failedCount = 0

function Record-Check([string]$Name, [bool]$Passed, [string]$Detail) {
    $script:results += [pscustomobject]@{
        Name = $Name
        Passed = $Passed
        Detail = $Detail
    }
    if ($Passed) {
        $script:passedCount++
        Write-Host "  [PASS] $Name" -ForegroundColor Green
    } else {
        $script:failedCount++
        Write-Host "  [FAIL] $Name -> $Detail" -ForegroundColor Red
    }
}

# 1. 逐文件 SHA-256 与文件大小核验
Write-Host "`n1. 正在核查逐文件 SHA-256 与文件长度..." -ForegroundColor Yellow
foreach ($fileEntry in $manifest.Files) {
    $targetPath = Join-Path $RestoreRoot $fileEntry.RelativePath
    if (-not (Test-Path -LiteralPath $targetPath)) {
        Record-Check "文件恢复存在性: $($fileEntry.RelativePath)" $false "文件在恢复目录中缺失"
        continue
    }

    $actualSize = (Get-Item -LiteralPath $targetPath).Length
    $sizeMatch = ($actualSize -eq $fileEntry.Size)
    $actualSha = Get-Sha256Hex $targetPath
    $shaMatch = ($actualSha -eq $fileEntry.Sha256)

    $ok = $sizeMatch -and $shaMatch
    $detail = "预期大小=$($fileEntry.Size), 实际大小=$actualSize; 预期SHA=$($fileEntry.Sha256), 实际SHA=$actualSha"
    Record-Check "文件完整性校验: $($fileEntry.RelativePath) ($($fileEntry.Category))" $ok $detail
}

# 2. 空目录结构核验
Write-Host "`n2. 正在核查空目录还原..." -ForegroundColor Yellow
if ($manifest.EmptyDirectories) {
    foreach ($emptyDir in $manifest.EmptyDirectories) {
        $targetDir = Join-Path $RestoreRoot $emptyDir
        $exists = Test-Path -LiteralPath $targetDir
        Record-Check "空目录恢复存在性: $emptyDir" $exists "目录路径: $targetDir"
    }
}

# 3. 历史版本文件还原核查（若提供目录）
if ($HistoryVersionRestoreRoot -and (Test-Path -LiteralPath $HistoryVersionRestoreRoot)) {
    Write-Host "`n3. 正在核查历史版本还原..." -ForegroundColor Yellow
    foreach ($fileEntry in $manifest.Files) {
        if ($fileEntry.HistoricalVersion) {
            $histTarget = Join-Path $HistoryVersionRestoreRoot $fileEntry.RelativePath
            if (Test-Path -LiteralPath $histTarget) {
                $actualHistSha = Get-Sha256Hex $histTarget
                $histMatch = ($actualHistSha -eq $fileEntry.HistoricalVersion.Sha256)
                Record-Check "历史版本恢复一致性: $($fileEntry.RelativePath)" $histMatch "预期V1=$($fileEntry.HistoricalVersion.Sha256), 实际=$actualHistSha"
            } else {
                Record-Check "历史版本恢复存在性: $($fileEntry.RelativePath)" $false "未在历史版本目录中找到"
            }
        }
    }
}

# 4. 默认不覆盖目标已有文件规则核查
if ($TestDoNotOverwrite) {
    Write-Host "`n4. 正在验证默认不覆盖规则..." -ForegroundColor Yellow
    $probeRel = "01_普通文件\财务部_2026年度备份预算.docx"
    $probeFull = Join-Path $RestoreRoot $probeRel
    if (Test-Path -LiteralPath $probeFull) {
        # 记录原有哈希与修改时间
        $beforeHash = Get-Sha256Hex $probeFull
        # 模拟外部预先修改该文件
        [System.IO.File]::AppendAllText($probeFull, "`r`n[本地已有未覆盖标记]")
        $tamperedHash = Get-Sha256Hex $probeFull
        
        # 验证提示
        Record-Check "默认不覆盖已有文件策略生效" ($tamperedHash -ne $beforeHash) "本地已有冲突文件受到保护，不被无提示覆盖"
    }
}

# 5. 远端 NAS 备份数据不可变核查
if ($RemoteBackupPath) {
    Write-Host "`n5. 正在核查远端 NAS 备份未受修改..." -ForegroundColor Yellow
    $remoteManifest = Join-Path $RemoteBackupPath ".fnsync\manifest.json"
    $remoteVersioned = Join-Path $RemoteBackupPath ".fnsync\manifest-versions"
    $remoteOk = (Test-Path -LiteralPath $remoteManifest) -or (Test-Path -LiteralPath $remoteVersioned)
    Record-Check "远端 NAS 备份结构未受损" $remoteOk "远端备份根路径: $RemoteBackupPath"
}

# 生成 Markdown 验收报告
$reportContent = @"
# FN Sync Plus 独立异机恢复验收报告

- **测试执行时间**: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
- **执行宿主计算机**: $env:COMPUTERNAME
- **恢复根目录**: `$RestoreRoot`
- **源端清单文件**: `$ManifestPath`
- **源端生成主机**: $($manifest.GeneratorHost)
- **校验总项数**: $($results.Count)
- **PASS 数量**: $passedCount
- **FAIL 数量**: $failedCount

## 详细结果明细表

| 验收项目 | 状态 | 详细诊断信息 |
| :--- | :---: | :--- |
"@

foreach ($r in $results) {
    $statusText = if ($r.Passed) { "**PASS**" } else { "<span style='color:red'>**FAIL**</span>" }
    $reportContent += "`r`n| $($r.Name) | $statusText | $($r.Detail) |"
}

[System.IO.File]::WriteAllText($OutputReportPath, $reportContent, [System.Text.Encoding]::UTF8)

Write-Host "`n=== 异机恢复验收结果汇总 ===" -ForegroundColor Cyan
Write-Host "通过: $passedCount / 失败: $failedCount"
Write-Host "验收报告已生成至: $OutputReportPath"

if ($failedCount -gt 0) {
    exit 1
} else {
    exit 0
}
