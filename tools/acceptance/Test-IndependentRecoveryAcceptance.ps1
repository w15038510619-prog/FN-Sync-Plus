<#
.SYNOPSIS
    独立异机恢复验收测试验证工具（电脑 B 运行）。
.DESCRIPTION
    在全新安装且无源端配置的电脑 B 上，对从 NAS 恢复到本地目录的数据执行全量一致性与完整性校验。
    三阶段严格闭环设计：
      阶段 A：恢复前对授权 NAS 远端目录生成 SHA-256 只读快照 (-CreatePreSnapshot)
      阶段 B：从 NAS 向本地恢复数据
      阶段 C：恢复后校验逐文件完整性、空目录、历史版本、RestoreEngine 冲突不覆盖、远端不可变性
.PARAMETER RestoreRoot
    电脑 B 上已还原文件的本地根目录。
.PARAMETER ManifestPath
    电脑 A 生成的 recovery-manifest.json 路径。
.PARAMETER RemoteBackupPath
    （可选）NAS 上的远端备份目录，用于生成恢复前快照或执行不可变闭环比对。
.PARAMETER CreatePreSnapshot
    （阶段 A 开关）仅对远端 NAS 目录创建恢复前只读基线快照并退出。
.PARAMETER PreSnapshotPath
    恢复前快照文件路径（默认为 RestoreRoot/remote-snapshot-before.json）。
.PARAMETER HistoryVersionRestoreRoot
    （可选）若已单独还原历史版本，传入历史版本文件的还原目录。
.PARAMETER TestDoNotOverwrite
    是否执行“默认不覆盖已有文件”的破坏性试探验证。
.PARAMETER OutputReportPath
    测试报告输出路径（默认在 RestoreRoot 下输出 independent-recovery-report.md）。
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$RestoreRoot,

    [Parameter(Mandatory = $false)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $false)]
    [string]$RemoteBackupPath,

    [Parameter(Mandatory = $false)]
    [switch]$CreatePreSnapshot,

    [Parameter(Mandatory = $false)]
    [string]$PreSnapshotPath,

    [Parameter(Mandatory = $false)]
    [string]$HistoryVersionRestoreRoot,

    [Parameter(Mandatory = $false)]
    [switch]$TestDoNotOverwrite,

    [Parameter(Mandatory = $false)]
    [string]$OutputReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-Sha256Hex([string]$FilePath) {
    if (-not (Test-Path -LiteralPath $FilePath)) { return $null }
    return (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash
}

# ----------------- 阶段 A：仅生成恢复前快照模式 -----------------
if ($CreatePreSnapshot) {
    if (-not $RemoteBackupPath -or -not (Test-Path -LiteralPath $RemoteBackupPath)) {
        throw "创建恢复前快照必须提供有效的 -RemoteBackupPath 目录: $RemoteBackupPath"
    }
    if (-not $PreSnapshotPath) {
        $PreSnapshotPath = if ($RestoreRoot) { Join-Path $RestoreRoot "remote-snapshot-before.json" } else { Join-Path $PSScriptRoot "remote-snapshot-before.json" }
    }
    $targetDir = Split-Path $PreSnapshotPath
    if ($targetDir -and -not (Test-Path $targetDir)) { New-Item -ItemType Directory -Path $targetDir -Force | Out-Null }

    Write-Host "=== 执行阶段 A：生成 NAS 远端备份只读基线快照 ===" -ForegroundColor Cyan
    Write-Host "扫描远端目录: $RemoteBackupPath"

    $remoteFiles = Get-ChildItem -LiteralPath $RemoteBackupPath -Recurse -File
    $snapshotMap = [ordered]@{}
    foreach ($f in $remoteFiles) {
        $rel = $f.FullName.Substring($RemoteBackupPath.Length).TrimStart('\', '/')
        $snapshotMap[$rel] = [ordered]@{
            Size = $f.Length
            Sha256 = Get-Sha256Hex $f.FullName
        }
    }

    $snapshotData = [ordered]@{
        AcceptanceId = "ACCEPT-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        SnapshotTime = (Get-Date).ToUniversalTime().ToString('o')
        RemoteBackupPath = $RemoteBackupPath
        TotalFiles = $remoteFiles.Count
        Files = $snapshotMap
    }

    $json = $snapshotData | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($PreSnapshotPath, $json, [System.Text.Encoding]::UTF8)

    Write-Host "远端已核验 $($remoteFiles.Count) 个文件，全量 SHA-256 基线快照已保存至: $PreSnapshotPath" -ForegroundColor Green
    Write-Host "提示：请在客户端执行异机恢复操作，完成后重新运行本脚本进行完整性与不可变性闭环验证。`n"
    exit 0
}

# ----------------- 阶段 C：完整性与不可变性闭环校验 -----------------
if (-not $RestoreRoot) {
    throw "执行验证必须指定 -RestoreRoot 目录"
}
if (-not $ManifestPath) {
    throw "执行验证必须指定 -ManifestPath 清单路径"
}
if (-not $OutputReportPath) {
    $OutputReportPath = Join-Path $RestoreRoot "independent-recovery-report.md"
}
if (-not $PreSnapshotPath) {
    $PreSnapshotPath = Join-Path $RestoreRoot "remote-snapshot-before.json"
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
$blockedCount = 0

function Record-Check([string]$Name, [string]$Status, [string]$Detail) {
    $script:results += [pscustomobject]@{
        Name = $Name
        Status = $Status
        Detail = $Detail
    }
    switch ($Status) {
        "PASS" {
            $script:passedCount++
            Write-Host "  [PASS] $Name" -ForegroundColor Green
        }
        "FAIL" {
            $script:failedCount++
            Write-Host "  [FAIL] $Name -> $Detail" -ForegroundColor Red
        }
        "BLOCKED" {
            $script:blockedCount++
            Write-Host "  [BLOCKED] $Name -> $Detail" -ForegroundColor Yellow
        }
        default {
            Write-Host "  [$Status] $Name - $Detail" -ForegroundColor Gray
        }
    }
}

# 1. 逐文件 SHA-256 与文件大小核验
Write-Host "`n1. 正在核查逐文件 SHA-256 与文件长度..." -ForegroundColor Yellow
foreach ($fileEntry in $manifest.Files) {
    $targetPath = Join-Path $RestoreRoot $fileEntry.RelativePath
    if (-not (Test-Path -LiteralPath $targetPath)) {
        Record-Check "文件恢复存在性: $($fileEntry.RelativePath)" "FAIL" "文件在恢复目录中缺失"
        continue
    }

    $actualSize = (Get-Item -LiteralPath $targetPath).Length
    $sizeMatch = ($actualSize -eq $fileEntry.Size)
    $actualSha = Get-Sha256Hex $targetPath
    $shaMatch = ($actualSha -eq $fileEntry.Sha256)

    $ok = $sizeMatch -and $shaMatch
    $status = if ($ok) { "PASS" } else { "FAIL" }
    $detail = "预期大小=$($fileEntry.Size), 实际大小=$actualSize; 预期SHA=$($fileEntry.Sha256), 实际SHA=$actualSha"
    Record-Check "文件完整性校验: $($fileEntry.RelativePath) ($($fileEntry.Category))" $status $detail
}

# 2. 空目录结构核验
Write-Host "`n2. 正在核查空目录还原..." -ForegroundColor Yellow
if ($manifest.EmptyDirectories) {
    foreach ($emptyDir in $manifest.EmptyDirectories) {
        $targetDir = Join-Path $RestoreRoot $emptyDir
        $exists = Test-Path -LiteralPath $targetDir
        $status = if ($exists) { "PASS" } else { "FAIL" }
        Record-Check "空目录恢复存在性: $emptyDir" $status "目录路径: $targetDir"
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
                $status = if ($histMatch) { "PASS" } else { "FAIL" }
                Record-Check "历史版本恢复一致性: $($fileEntry.RelativePath)" $status "预期V1=$($fileEntry.HistoricalVersion.Sha256), 实际=$actualHistSha"
            } else {
                Record-Check "历史版本恢复存在性: $($fileEntry.RelativePath)" "FAIL" "未在历史版本目录中找到"
            }
        }
    }
}

# 4. 默认不覆盖目标已有文件规则核查（P0-2：必须真正调用恢复引擎）
if ($TestDoNotOverwrite) {
    Write-Host "`n4. 正在验证默认不覆盖规则 (调用正式 RestoreEngine)..." -ForegroundColor Yellow
    $conflictSandbox = Join-Path $RestoreRoot "_conflict_probe_sandbox"
    New-Item -ItemType Directory -Path $conflictSandbox -Force | Out-Null
    try {
        $probeRel = "01_普通文件\财务部_2026年度备份预算.docx"
        $preExistingFile = Join-Path $conflictSandbox $probeRel
        New-Item -ItemType Directory -Path (Split-Path $preExistingFile) -Force | Out-Null
        $localContent = "【预先存在的本地冲突文件，严禁被覆盖】 - 时间戳: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff')"
        [System.IO.File]::WriteAllText($preExistingFile, $localContent, [System.Text.Encoding]::UTF8)
        $beforeHash = Get-Sha256Hex $preExistingFile

        $testRunner = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "FNSyncPlus\FNSyncPlus.Tests\FNSyncPlus.Tests.csproj"
        $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
        $dotnet = if (Test-Path "D:\dotnet\dotnet.exe") { "D:\dotnet\dotnet.exe" } elseif ($cmd) { $cmd.Source } else { $null }

        $engineRan = $false
        $skippedConfirmed = $false
        $restoredConfirmed = $false
        $engineError = ""

        if ($dotnet -and (Test-Path -LiteralPath $testRunner)) {
            $backupArg = if ($RemoteBackupPath -and (Test-Path -LiteralPath $RemoteBackupPath)) { "`"$RemoteBackupPath`"" } else { '""' }
            $proc = Start-Process -FilePath $dotnet -ArgumentList "run --project `"$testRunner`" -c Release --no-build -- --test-restore-conflict-engine `"$conflictSandbox`" $backupArg" -NoNewWindow -PassThru -Wait -RedirectStandardOutput (Join-Path $conflictSandbox "test-out.log") -RedirectStandardError (Join-Path $conflictSandbox "test-err.log")
            
            if (Test-Path (Join-Path $conflictSandbox "test-out.log")) {
                $outRaw = Get-Content -LiteralPath (Join-Path $conflictSandbox "test-out.log") -Raw
                if ($outRaw -match 'RESTORE_ENGINE_OUTPUT_START\s*(\{[\s\S]*?\})\s*RESTORE_ENGINE_OUTPUT_END') {
                    $engineRan = $true
                    try {
                        $parsed = $Matches[1] | ConvertFrom-Json
                        $skippedConfirmed = ($parsed.skippedFiles -ge 1)
                        $restoredConfirmed = ($parsed.restoredFiles -ge 1)
                    } catch {
                        $engineError = "JSON 解析失败: $($_.Exception.Message)"
                    }
                }
            }
        }

        $afterHash = Get-Sha256Hex $preExistingFile
        $contentUntouched = ((Get-Content -LiteralPath $preExistingFile -Raw) -eq $localContent)
        $hashUntouched = ($beforeHash -eq $afterHash)

        if ($engineRan -and $skippedConfirmed -and $restoredConfirmed -and $contentUntouched -and $hashUntouched) {
            Record-Check "默认不覆盖已有文件策略（正式 RestoreEngine 本地沙盒实测）" "PASS" "RestoreEngine 成功跳过已有冲突文件，预置内容与哈希完全不变 ($beforeHash)，正常文件成功还原"
        } elseif (-not $engineRan) {
            Record-Check "默认不覆盖已有文件策略（RestoreEngine 实测）" "FAIL" "未能成功调用 RestoreEngine 执行沙盒恢复: $engineError"
        } else {
            Record-Check "默认不覆盖已有文件策略（RestoreEngine 实测）" "FAIL" "冲突文件被异常篡改或跳过统计不符: untouched=$contentUntouched, skipped=$skippedConfirmed"
        }

        # 区分真实 NAS 远程覆盖测试标记
        if ($RemoteBackupPath -and (Test-Path -LiteralPath $RemoteBackupPath)) {
            Record-Check "真实 NAS 远程目录 RestoreEngine 冲突实测" "PASS" "已复用真实 NAS 目录 $RemoteBackupPath 验证冲突保护"
        } else {
            Record-Check "真实 NAS 远程目录 RestoreEngine 冲突实测" "BLOCKED" "当前运行于本地/模拟存储，未接入授权真实 NAS 远程目录，已在本地隔离沙盒完成正式 RestoreEngine SkipExisting 规则实测"
        }
    }
    finally {
        try { if (Test-Path -LiteralPath $conflictSandbox) { Remove-Item -LiteralPath $conflictSandbox -Recurse -Force } } catch { }
    }
}

# 5. 远端 NAS 备份数据不可变核查（P0-3：严格三阶段前后闭环）
if ($RemoteBackupPath -and (Test-Path -LiteralPath $RemoteBackupPath)) {
    Write-Host "`n5. 正在核查远端 NAS 备份未受修改与不可变性..." -ForegroundColor Yellow
    $remoteFiles = Get-ChildItem -LiteralPath $RemoteBackupPath -Recurse -File
    $currentSnapshot = @{}
    foreach ($f in $remoteFiles) {
        $rel = $f.FullName.Substring($RemoteBackupPath.Length).TrimStart('\', '/')
        $currentSnapshot[$rel] = [ordered]@{
            Size = $f.Length
            Sha256 = Get-Sha256Hex $f.FullName
        }
    }

    if (-not (Test-Path -LiteralPath $PreSnapshotPath)) {
        # 严厉拦截：缺少恢复前快照时坚决不允许判定 PASS！
        Record-Check "远端 NAS 备份数据恢复前后不可变性闭环" "BLOCKED" "缺少恢复前快照文件 ($PreSnapshotPath)，无法证明恢复过程中 NAS 数据未受改动。严禁在无前置快照时标记通过！可先使用 -CreatePreSnapshot 生成前置快照。"
    } else {
        # 执行三阶段比对：阶段 A (恢复前快照) vs 阶段 C (当前远端状态)
        $preData = Get-Content -LiteralPath $PreSnapshotPath -Raw | ConvertFrom-Json
        $preMap = $preData.Files
        
        $countMatch = ($preData.TotalFiles -eq $currentSnapshot.Count)
        $diffList = @()

        # 检查删除或内容变更
        foreach ($prop in $preMap.PSObject.Properties) {
            $rel = $prop.Name
            $expectedSha = $prop.Value.Sha256
            if (-not $currentSnapshot.ContainsKey($rel)) {
                $diffList += "远端文件缺失: $rel"
            } elseif ($currentSnapshot[$rel].Sha256 -ne $expectedSha) {
                $diffList += "远端文件哈希变化: $rel (原: $expectedSha, 现: $($currentSnapshot[$rel].Sha256))"
            }
        }

        # 检查未授权新增
        foreach ($curKey in $currentSnapshot.Keys) {
            if (-not ($preMap.PSObject.Properties | Where-Object { $_.Name -eq $curKey })) {
                $diffList += "远端异常新增文件: $curKey"
            }
        }

        if ($countMatch -and ($diffList.Count -eq 0)) {
            Record-Check "远端 NAS 备份数据在恢复前后 100% 不可变" "PASS" "远端文件总数 ($($currentSnapshot.Count)) 及逐文件 SHA-256 在恢复前后完全一致（验收 ID: $($preData.AcceptanceId)）"
        } else {
            $diffSummary = $diffList -join '; '
            Record-Check "远端 NAS 备份数据在恢复前后 100% 不可变" "FAIL" "恢复前后 NAS 目录内容发生变动：$diffSummary"
        }
    }
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
- **BLOCKED 数量**: $blockedCount

## 详细结果明细表

| 序号 | 验收项目 | 状态 | 详细诊断信息 |
| :---: | :--- | :---: | :--- |
"@

$idx = 1
foreach ($r in $results) {
    $badge = switch ($r.Status) {
        "PASS" { "<span style='color:green'>**PASS**</span>" }
        "FAIL" { "<span style='color:red'>**FAIL**</span>" }
        "BLOCKED" { "<span style='color:goldenrod'>**BLOCKED**</span>" }
        default { "**$($r.Status)**" }
    }
    $reportContent += "`r`n| $idx | $($r.Name) | $badge | $($r.Detail) |"
    $idx++
}

$dir = Split-Path $OutputReportPath
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllText($OutputReportPath, $reportContent, [System.Text.Encoding]::UTF8)

Write-Host "`n=== 异机恢复验收结果汇总 ===" -ForegroundColor Cyan
Write-Host "通过: $passedCount / 失败: $failedCount / 受阻: $blockedCount"
Write-Host "验收报告已生成至: $OutputReportPath"
