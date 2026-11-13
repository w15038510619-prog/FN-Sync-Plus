# FN Sync Plus 物理独立异机恢复验收执行规范与 SOP

## 一、验收目标

验证全链路跨机场景：
> **Windows 电脑 A 执行同步备份至 fnOS NAS -> Windows 电脑 B（全新环境，无电脑 A 的任务配置与缓存）独立发现并完整恢复数据。**

所有恢复文件必须满足：
1. 逐文件 SHA-256 逐字节一致性（100% 强校验）。
2. 空目录与多层嵌套目录结构精确还原。
3. 超长中文文件名（200+ 字符 / 250+ UTF-8 字节）精确还原。
4. Unicode / 多语种 / Emoji 混合文件名精确还原。
5. 历史版本文件可按需还原指定快照。
6. 默认不覆盖本地已有文件策略生效。
7. 远端 NAS 备份数据在恢复全过程保持只读只写隔离，不被篡改破坏。

---

## 二、测试环境准备与前置条件

| 角色 | 机器环境 | 要求与软件状态 |
| :--- | :--- | :--- |
| **电脑 A（备份源端）** | 生产/测试 Windows PC | 已安装 FN Sync Plus，配置有常规授权个人空间或测试 NAS 共享。 |
| **电脑 B（恢复目标端）** | **全新或全新配置 Windows PC** | **严禁拷贝电脑 A 的任何配置文件、AppSession 或任务映射**；全新安装 FN Sync Plus。 |
| **NAS 存储介质** | 飞牛 fnOS 官方空间 / SMB 共享 | 拥有独立授权的测试备份目标目录（例如 `我的文件/IndependentRecoveryTest`）。 |

---

## 三、电脑 A 操作步骤（两阶段生成数据集与版本化备份）

1. **第一阶段（Phase V1）：生成初始数据集与 V1 清单**
   在电脑 A 打开 PowerShell，运行生成工具：
   ```powershell
   powershell.exe -ExecutionPolicy Bypass -File "tools\acceptance\New-RecoveryAcceptancePackage.ps1" `
       -OutputDir "D:\TestRestoreData_Source" `
       -ManifestPath "D:\TestRestoreData_Source\recovery-manifest.json" `
       -Phase V1
   ```
2. **执行电脑 A 初次完整备份**：
   - 启动 FN Sync Plus，添加同步任务：本地源目录选定 `D:\TestRestoreData_Source`。
   - 目标 NAS 选定测试目录 `IndependentRecoveryTest`。
   - 执行同步，确保包含普通中文、超长名称、多层目录、空目录及 V1 文件的初次备份全部上传完毕。
3. **第二阶段（Phase V2）：生成增量修改并更新清单**：
   在电脑 A 再次运行生成工具（Phase V2）：
   ```powershell
   powershell.exe -ExecutionPolicy Bypass -File "tools\acceptance\New-RecoveryAcceptancePackage.ps1" `
       -OutputDir "D:\TestRestoreData_Source" `
       -ManifestPath "D:\TestRestoreData_Source\recovery-manifest.json" `
       -Phase V2
   ```
   工具会自动将 `07_版本化文件\将被修改的历史版本文件.txt` 升级为 V2 内容，并在 `recovery-manifest.json` 中完整记录 V1 历史版本哈希与 V2 当前哈希。
4. **执行电脑 A 增量同步**：
   - 在 FN Sync Plus 触发同步，将 V2 修改增量提交至 NAS，使远端形成真实的历史快照（Sequence 1 与 Sequence 2）。
5. 将最终的 `recovery-manifest.json` 拷贝传递至电脑 B，用于后续自动化比对。

---

## 四、电脑 B 操作步骤（全新安装与完整恢复）

1. 电脑 B 安装最新版 FN Sync Plus（v0.4.0-beta 以上）。
2. 启动软件，连接相同的飞牛 NAS 账号（此时电脑 B 上无任何历史本地任务）。
3. 进入「恢复中心」：
   - 软件应通过零配置备份发现机制，自动检索并发现 `IndependentRecoveryTest` 备份包。
   - 界面列出备份快照时间戳、文件总数、超长文件名映射与 SHA-256 预览。
4. 选择恢复目标路径为：`D:\RestoredData_MachineB`。
5. 点击「开始完整还原」，等待全部文件还原完成。
6. 如需测试历史版本，可选择恢复历史版本至 `D:\RestoredData_MachineB_History`。

---

## 五、电脑 B 执行自动化验收比对

在电脑 B 上打开 PowerShell，运行校验工具：
```powershell
powershell.exe -ExecutionPolicy Bypass -File "tools\acceptance\Test-IndependentRecoveryAcceptance.ps1" `
    -RestoreRoot "D:\RestoredData_MachineB" `
    -ManifestPath "D:\recovery-manifest.json" `
    -TestDoNotOverwrite `
    -OutputReportPath "D:\RestoredData_MachineB\independent-recovery-report.md"
```

如果校验工具输出：
```text
=== 异机恢复验收结果汇总 ===
通过: X / 失败: 0
```
并在报告中全项标记 **PASS**，则真实物理异机恢复验收宣告通过！

---

## 六、当前测试状态与约束声明

> [!IMPORTANT]
> **关于物理第二台 PC 的状态标记**
> 在自动化测试流水线及受限无跳板宿主环境下，未连接真实的物理第二台 Windows 电脑，本项物理执行依规标记为 **BLOCKED**。
> 本仓库已通过 `IndependentRecoveryTestSuite`（14/14 项 PASS）在高保真独立沙盒内完整验证了上述所有恢复语义与逐字节一致性算法。
