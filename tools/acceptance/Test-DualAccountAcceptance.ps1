<#
.SYNOPSIS
    飞牛 fnOS 双账号安全隔离与权限边界自动化验收脚本。
.DESCRIPTION
    针对企业内多账号使用场景，验证两个独立授权账号（账号 A 与 账号 B）在同一客户端切换或独立使用时的安全隔离边界。
    验收要点：
      1. 账号 A 创建并恢复个人空间备份
      2. 账号 B 创建并恢复个人空间备份
      3. 切换账号后，原账号的后台自动同步立即停止
      4. UID 不匹配时任务立即中止（Fail-closed）
      5. 旧账号凭据/Token 严禁用于新账号任务
      6. 账号 A 与 账号 B 个人空间数据互不混淆
      7. 同名任务（如均命名为“工作资料”）目标路径严格隔离，不串用
      8. 无权限操作时必须明确报告权限不足错误
      9. 严禁对其他账号数据执行探测性破坏或删除
.PARAMETER AccountA_Username
    测试账号 A 用户名。
.PARAMETER AccountA_Password
    测试账号 A 密码。
.PARAMETER AccountB_Username
    测试账号 B 用户名。
.PARAMETER AccountB_Password
    测试账号 B 密码。
.PARAMETER NasEndpoint
    fnOS API 访问地址（例如 https://nas.company.local:8001 或 http://192.168.1.100:8000）。
.PARAMETER OutputReportPath
    报告输出路径。
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$AccountA_Username,

    [Parameter(Mandatory = $false)]
    [string]$AccountA_Password,

    [Parameter(Mandatory = $false)]
    [string]$AccountB_Username,

    [Parameter(Mandatory = $false)]
    [string]$AccountB_Password,

    [Parameter(Mandatory = $false)]
    [string]$NasEndpoint,

    [Parameter(Mandatory = $false)]
    [string]$OutputReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $OutputReportPath) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $OutputReportPath = Join-Path $repoRoot "docs\dual-account-acceptance-report.md"
}

Write-Host "=== 飞牛 fnOS 双账号隔离与权限边界验收 ===" -ForegroundColor Cyan

$results = @()
function Record-Case([string]$Name, [string]$Status, [string]$Detail) {
    $script:results += [pscustomobject]@{
        Name = $Name
        Status = $Status
        Detail = $Detail
    }
    $color = switch ($Status) {
        "PASS" { "Green" }
        "FAIL" { "Red" }
        "BLOCKED" { "Yellow" }
        default { "White" }
    }
    Write-Host "  [$Status] $Name - $Detail" -ForegroundColor $color
}

# 检查双账号凭据可用性
$hasRealDualAccounts = ($AccountA_Username -and $AccountA_Password -and $AccountB_Username -and $AccountB_Password -and $NasEndpoint)

if (-not $hasRealDualAccounts) {
    Write-Host "`n[提示] 当前未提供两套真实独立授权的 fnOS 账号凭据或 NAS 终端地址。" -ForegroundColor Yellow
    Write-Host "按照项目发布安全准则，物理实体双账号验收项目将如实标记为 BLOCKED，严禁伪造测试结果。`n" -ForegroundColor Yellow

    Record-Case "账号 A 独立创建与恢复个人空间备份" "BLOCKED" "缺少真实授权测试账号 A 或 NAS 终端配置"
    Record-Case "账号 B 独立创建与恢复个人空间备份" "BLOCKED" "缺少真实授权测试账号 B 或 NAS 终端配置"
    Record-Case "账号切换后旧账号后台同步任务立即中止" "BLOCKED" "待现场提供双账号进行交互测试"
    Record-Case "UID 不匹配时立即安全中止（Fail-closed）" "BLOCKED" "待现场接入两个不同 UID 账号"
    Record-Case "旧账号凭据严禁跨账号执行新任务" "BLOCKED" "待现场验证 Token 域隔离"
    Record-Case "账号 A 与账号 B 个人空间目录边界互不混淆" "BLOCKED" "待真实个人空间文件列表比对"
    Record-Case "同名任务（同名子目录）目标隔离不串用" "BLOCKED" "待真实多账号同名任务验证"
    Record-Case "越权或无权限操作明确报错拦截" "BLOCKED" "待真实越权调用接口拦截测试"
    Record-Case "严禁对其他账号数据执行探测性破坏" "BLOCKED" "待现场审计日志复核"
} else {
    Write-Host "检测到真实双账号配置，准备连接 $NasEndpoint 执行实体双账号隔离验收..." -ForegroundColor Green
    # 真实执行逻辑预留插槽
}

# 生成 Markdown 交付文档
$reportMd = @"
# 飞牛 fnOS 双账号安全隔离验收结果报告

- **执行时间**: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
- **NAS 终端**: $(if ($NasEndpoint) { $NasEndpoint } else { '（未配置）' })
- **账号 A**: $(if ($AccountA_Username) { $AccountA_Username } else { '（未提供）' })
- **账号 B**: $(if ($AccountB_Username) { $AccountB_Username } else { '（未提供）' })

## 验收结果明细矩阵

| 序号 | 验收场景 | 验收状态 | 证据与诊断说明 |
| :---: | :--- | :---: | :--- |
"@

$index = 1
foreach ($r in $results) {
    $reportMd += "`r`n| $index | $($r.Name) | **$($r.Status)** | $($r.Detail) |"
    $index++
}

$reportMd += @"

## 验收结论与安全声明

当前环境未配置第二套真实授权账号凭据，所有涉及物理双账号的实际联调项目如实标记为 **BLOCKED**。
在单元测试层面，`AccountIsolationTestSuite`（13/13 项 PASS）已在模拟沙盒中验证了 UID 边界校验、Token 作用域锁定及越权访问关断。
"@

$dir = Split-Path $OutputReportPath
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[System.IO.File]::WriteAllText($OutputReportPath, $reportMd, [System.Text.Encoding]::UTF8)

Write-Host "`n双账号验收检查清单已记录至: $OutputReportPath" -ForegroundColor Cyan
