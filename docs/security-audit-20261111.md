# 数据安全与敏感信息审计（2026-11-11）

审计对象：分支 `workbuddy/enterprise-release-readiness`（基于 `codex/fix-backup-integrity` 的 `b9e5854`）及其历史、以及本次候选安装目录。

## 1. 结论摘要

| 项目 | 结果 |
|---|---|
| 源码/文档/脚本中的私钥、证书、令牌 | PASS — 未发现 |
| 硬编码账号密码 | PASS — 未发现 |
| Git 历史中的私钥/令牌特征串 | PASS — `git log -S` 未命中 |
| 候选安装目录中的凭据/配置 | PASS — 打包前检查未命中 |
| 内网资产信息（示例 IP） | **已修复** — 见 §2 |
| 正式代码签名 / 更新私钥托管 | BLOCKED — 无证书与密钥托管方案 |

## 2. 发现与处理

### 2.1 示例中的内网地址（低风险，已修复）

6 个文件在注释、示例与文档中使用了测试环境的私有地址 `192.168.8.120`，属于内网资产信息，不影响功能但不应出现在对外材料中。

处理：**当前工作树**统一替换为文档保留地址 `192.0.2.10`（TEST-NET-1，不会指向真实主机）：

```
docs/fnos_personal_space_technical_verification.md
FNSyncPlus/FNSyncPlus.App/MainWindow.xaml.cs
FNSyncPlus/FNSyncPlus.App/TaskLogDialog.xaml
FNSyncPlus/FNSyncPlus.Core/Utils/PathHelper.cs
FNSyncPlus/FNSyncPlus.Tests/FnOsPersonalSpacePoc.cs
FNSyncPlus/FNSyncPlus.Tests/Program.cs
```

**未改写 Git 历史**（按规则不得擅自改写共享历史）。历史提交中仍可看到该地址；如需彻底清除，应由仓库所有者评估后决定是否做历史重写。该地址本身不是凭据，无需轮换；但若曾配合真实口令使用，应按公司策略评估。

### 2.2 凭据存储方式

- 密码仅通过 Windows DPAPI（`ProtectedData`）加密后落盘；
- 无 DPAPI 环境（例如非 Windows）时 `SmbAuthService.IsCredentialProtectionAvailable` 返回 false，拒绝明文保存；
- 日志脱敏覆盖 `password|pwd|secret|token|accesstoken|longtoken`；
- 诊断包导出复用同一脱敏路径。

### 2.3 更新链路

- 私钥只存在于发布机，仓库与安装包均不含私钥；
- 客户端内置**公钥**；未内置公钥的构建会停用自动更新（fail-closed）；
- 清单签名覆盖全部字段（除 signature），并做产品/架构/通道/批次/受众/降级/HTTPS 校验；
- 更新包二次校验 SHA-256，解压做路径穿越、链接、体积与条目数限制；
- 本次演示生成的测试私钥已在验证后删除，仅保留公钥 `Release_WinUI\keys\test-public.pem` 用于复核演示清单。

## 3. 仍需处理（BLOCKED）

1. **正式 Authenticode 代码签名证书**：缺失，安装包与主程序未签名，SmartScreen 会告警。
2. **更新签名私钥托管**：需要公司密钥管理方案（HSM/离线机/双人保管），当前仅为流程说明。
3. **公司内网 HTTPS 更新服务器**：未部署、未验收；当前只有本地回环验证。
4. **敏感信息扫描的自动化**：目前是人工 + 命令扫描，建议在 CI 增加 `gitleaks` 类检查。

## 4. 复现命令

```bash
git ls-files | grep -iE "credential|\.pfx|\.p12|\.pem|\.key$|secret|password|token"
grep -rl "BEGIN.*PRIVATE KEY" --include=*.cs --include=*.md --include=*.json .
git log --all --oneline -S"BEGIN RSA PRIVATE KEY"
git log --all --oneline -S"ghp_"
```
