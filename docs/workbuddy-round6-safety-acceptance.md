# FN Sync Plus 第六轮安全整改与真实验收交付报告

- **报告日期**: 2026-11-13
- **项目仓库**: [FN-Sync-Plus](https://github.com/w15038510619-prog/FN-Sync-Plus)
- **开发分支**: `workbuddy/enterprise-release-readiness`
- **Draft PR**: [#4](https://github.com/w15038510619-prog/FN-Sync-Plus/pull/4)
- **基准提交**: `6843702ae89afc4759e9d5a0e88acf08365106d3`
- **环境等级**: Windows 11 x64, .NET 8.0 SDK (`8.0.404`), WinUI 3 Desktop

---

## 一、本轮核心任务与整改总览

针对前序独立源码审查中识别出的四项关键安全隐患与验收缺陷，本轮实施了彻底的代码重构、Fail-Closed 安全加固、故障注入验证以及真实验收工具链升级：

1. **P0-1：DataFormatGuard 回滚机制重构与备份文件严格保全**
   - 引入显式写入事务生命周期状态机：`Prepared -> BackupVerified -> Replacing -> Verifying -> Committed`；异常路径流转为 `RollbackRequired -> RolledBack` 或 `RollbackRequired -> RecoveryFailed`。
   - 替换前对原始标记文件生成 `.bak` 备份并执行**逐字节强一致性回读校验**。
   - 彻底消灭回滚失败时误删备份的隐患：在 `RecoveryFailed` 状态下，`.bak` 备份文件**受顶层保护绝对不得被清理删除**，强制保留在磁盘供人工介入排查。
   - 增加 4 处底层故障注入测试钩子，覆盖创建备份失败、原子替换失败、回读校验拦截以及回滚写入失败全场景。
2. **P0-2：AppStorageRoot 彻底消灭 Fail-Open 语义与 CI 快照断言修复**
   - 重构 `ResolvePhysicalPath` 与 `VerifyNoSubdirectoryReparsePointsToProduction`，消灭所有吞异常放行的 `catch` 块；任何断链、循环引用、权限拒绝或不可解析的重解析点，一律严格判定为不安全并抛出 `InvalidOperationException`（Fail-Closed 原则）。
   - 修复 CI 运行中 `Run Storage Isolation Acceptance` 偶发失败问题：彻底移除测试中在生产目录调用 `Directory.CreateDirectory(prod)` 的不良代码，改为以系统原生必存在的 `%APPDATA%`（已在禁止列表中）为联接目标，实现真正的“零生产文件创建与零改动”，保证前后 SHA-256 快照 100% 绝对一致。
3. **P1-1：异机恢复验收脚本真实性增强（不覆盖与远端不可变）**
   - 升级 `Test-IndependentRecoveryAcceptance.ps1`，不再简单通过本地追加字符串模拟不覆盖，而是建立独立的本地冲突沙盒，运行真实 `RestoreEngine` 冲突跳过逻辑（`ConflictResolution.SkipExisting`），断言冲突文件哈希与内容未受任何触碰。
   - 增加恢复前后远端 NAS 备份目录全量递归 SHA-256 快照比对，严格证明恢复全生命周期对远端 NAS 数据为 100% 只读不可变。
4. **P1-2：历史版本生成解耦为显式顺序流程（V1 -> V2）**
   - 修复 `New-RecoveryAcceptancePackage.ps1` 中一次性写入 V2 导致 NAS 未曾获得 V1 备份的逻辑倒置。
   - 解耦为标准两阶段执行支持：`-Phase V1`（生成初始集并留存 V1，指导源端 PC A 进行初次完整备份）、`-Phase V2`（升级指定文件为 V2 并刷新清单，指导源端 PC A 进行增量备份以产生远端历史版本）。
5. **P1-3：双账号隔离验收脚本真实验收逻辑与合规留痕**
   - 升级 `Test-DualAccountAcceptance.ps1`，补充 `-Interactive` 模式，采用 `Read-Host -AsSecureString` 在内存中加密获取凭据，绝不落地明文日志。
   - 实现真实双账号认证、唯一 UID 比对、账号热切换时旧账号后台 Worker 强制关断、Token 作用域锁定及同名任务路径隔离。
   - 在未配置第 2 个物理真实 fnOS 账号时，自动拉起内置 `AccountIsolationTestSuite` 执行逻辑沙盒全量验证，并**如实将物理多账号状态标记为 BLOCKED**，坚决杜绝伪造实测通过。

---

## 二、关键技术整改与代码对比

### 1. DataFormatGuard 写入事务状态机与绝对保全设计

在 `FNSyncPlus.Core/Updates/DataFormatGuard.cs` 中：
```csharp
public enum WriteTransactionState
{
    Prepared,
    BackupVerified,
    Replacing,
    Verifying,
    Committed,
    RollbackRequired,
    RolledBack,
    RecoveryFailed
}
```
在原子写流程中：
- 写入前读取原数据并复制 `.bak`，通过 `SequenceEqual` 强校验副本；
- 临时文件写入并 Flush 到磁盘，通过 `File.Move(..., overwrite: true)` 替换；
- 替换后立刻调用 `Probe()` 回读，若校验不通过立即触发 `ExecuteRollback`；
- **回滚故障处理**：若回滚写入异常或回读仍未恢复，状态机立即置为 `RecoveryFailed`，记录 `LastPreservedBackup = backup`；
- 在 `finally` 块中：
  ```csharp
  if (LastTransactionState == WriteTransactionState.Committed || LastTransactionState == WriteTransactionState.RolledBack)
  {
      try { if (File.Exists(backup)) File.Delete(backup); } catch { }
  }
  else if (backupCreated && File.Exists(backup))
  {
      // 绝不在 RecoveryFailed 状态下删除 backup！
      LastPreservedBackup = backup;
      _log?.Invoke($"安全防御：因事务未成功提交，原有效标记备份已严格保留：{backup}");
  }
  ```

### 2. AppStorageRoot Fail-Closed 重解析点解析与快照保护

在 `FNSyncPlus.Core/Services/AppStorageRoot.cs` 中：
- `ResolvePhysicalPath` 增加循环路径检测（`HashSet<string> visited`）与跳数上限（`maxHops = 64`）。
- 遇到断链、无权限或无法解析的目标，立即抛出 `InvalidOperationException("无法安全解析重解析点物理路径（无法确认安全即视为不安全）")`。
- `VerifyNoSubdirectoryReparsePointsToProduction` 使用 `GetFileSystemInfos()` 彻底拦截所有子目录中的断链、循环链接和越界重解析点。
- `StorageIsolationTestSuite.cs` 中移除原有的 `Directory.CreateDirectory(prod)`，彻底消除在测试机器上创建 `%APPDATA%\FNSyncPlus` 的副作用，解决 GitHub Runner CI 上快照比对不一致问题。

---

## 三、本地全量自动化回归测试结果

| 测试套件 / 验收专项 | 测试命令 | 结果 (PASS / FAIL / SKIP) | 备注说明 |
| :--- | :--- | :---: | :--- |
| **P0 整改专项套件** | `--p0-tests` | **85 PASS / 0 FAIL** | 覆盖租约、发布信任链、数据格式守卫原子写入、故障注入 4 场景 |
| **存储隔离验收套件** | `--storage-isolation-tests` | **24 PASS / 0 FAIL** | 覆盖子进程真实运行、断链拦截、循环链接拦截、生产配置 SHA-256 前后 100% 一致 |
| **完整性回归全量测试** | `--integrity-regressions` | **550 PASS / 0 FAIL** | 覆盖独占锁、版本化清单、长文件名映射、网络中断恢复全场景 |
| **多账号隔离验收** | `--account-isolation` | **13 PASS / 0 FAIL** | 覆盖 UID 判定、大小写敏感、沙盒隔离、跨会话不可见 |
| **签名链审计专项** | `--signature-audit-tests` | **11 PASS / 0 FAIL** | 覆盖 ECDSA P-256、SHA-256、曲线错配、离线签名往返工具 |
| **独立异机恢复专项** | `--independent-recovery-tests` | **14 PASS / 0 FAIL** | 覆盖全场景文件、历史版本恢复、不覆盖规则、远端只读快照 |
| **更新交接与看门狗** | `--handoff-process-tests` | **27 PASS / 0 FAIL** | 覆盖进程级租约传递、16 种更新器故障注入、原子状态流转 |
| **UI 验收隔离测试** | `--ui-isolation-tests` | **3 PASS / 0 FAIL** | 覆盖实例共存互斥与会话凭据剥离 |
| **核心底层与业务测试** | (默认无参运行) | **333 PASS / 0 FAIL** | 核心组件、SMB 协议翻译、向导去重规划全量通过 |
| **异机恢复脚本验证** | `Test-IndependentRecoveryAcceptance.ps1` | **9 PASS / 0 FAIL** | 涵盖普通、超长、嵌套、大文件、V1/V2、空目录及冲突沙盒强断言 |
| **双账号隔离工具验证**| `Test-DualAccountAcceptance.ps1` | **1 PASS / 0 FAIL (9 BLOCKED)** | 沙盒逻辑全通，物理第二账号如实合规标记为 BLOCKED |

---

## 四、真实硬件与受限环境状态客观声明

依据项目企业级发布准则与诚信合规要求，以下两项因受限环境缺乏物理第二设备或独立授权账号，**如实声明为 BLOCKED**：

1. **真实独立第二台 Windows 电脑（异机恢复）**：
   - 现场环境无物理第二台 PC 且无 Hyper-V 嵌套虚拟化权限。
   - **状态**：**BLOCKED**。
   - **替代证明**：已在独立沙盒中使用 `IndependentRecoveryTestSuite`（14/14 PASS）与 PowerShell 验收套件完成高保真验证。
2. **真实第二个独立授权 fnOS 账号（双账号隔离）**：
   - 现场环境未接入第二个物理授权账号凭据。
   - **状态**：**BLOCKED**。
   - **替代证明**：已在 `AccountIsolationTestSuite`（13/13 PASS）与 `Test-DualAccountAcceptance.ps1` 中完成全套权限边界与 Worker 关断验证。

---

## 五、交付文件清单

1. `FNSyncPlus/FNSyncPlus.Core/Updates/DataFormatGuard.cs` (P0-1 写入事务状态机与 `.bak` 绝对保全)
2. `FNSyncPlus/FNSyncPlus.Core/Services/DataFormatTestSuite.cs` (P0-1 4项故障注入与回滚恢复测试)
3. `FNSyncPlus/FNSyncPlus.Core/Services/AppStorageRoot.cs` (P0-2 Fail-closed 重解析点与循环检测)
4. `FNSyncPlus/FNSyncPlus.Core/Services/StorageIsolationTestSuite.cs` (P0-2 断链与循环测试，移除生产目录创建)
5. `tools/acceptance/New-RecoveryAcceptancePackage.ps1` (P1-2 解耦 Phase V1 / Phase V2 增量版本化生成)
6. `tools/acceptance/Test-IndependentRecoveryAcceptance.ps1` (P1-1 真实冲突沙盒不覆盖校验与远端全量 SHA-256 快照)
7. `tools/acceptance/Test-DualAccountAcceptance.ps1` (P1-3 安全交互凭据输入、实体联调与模拟沙盒双模式)
8. `docs/independent-recovery-acceptance-guide.md` (异机恢复两阶段 SOP 指南)
9. `docs/dual-account-acceptance-report.md` (双账号验收报告)
10. `docs/workbuddy-round6-safety-acceptance.md` (本交付报告)
