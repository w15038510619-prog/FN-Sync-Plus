# FN Sync Plus Beta 测试包交付验收

验收日期 2026-11-17。工作树 `D:\FN_Sync_Plus_WB` 接手时位于 `workbuddy/enterprise-release-readiness`，受跟踪工作区干净。基准提交 `ff6507e3afb862e0c8f4bec0b4fcb4c3930c179b` 与远端一致。原包已保留。本轮提交继续推送到同一分支。[Draft PR #4](https://github.com/w15038510619-prog/FN-Sync-Plus/pull/4)

## 整改与证据

| 缺陷 | 源码位置与验证 |
| --- | --- |
| 空目录、缺失路径和普通异常被误认成 Token 权限拒绝 | `FNSyncPlus/FNSyncPlus.AcceptanceRunner/DualAccountAcceptance.cs` 使用两个独立真实会话。先用 B 访问确实非空的目录，再用 A 向服务端请求同一原始路径。离线规则检验区分明确拒绝、可访问、空目录、路径不存在、连接失败和未知回复 |
| Worker 停止与同名任务仅有模拟证据 | 同一文件接入实际 `TaskManager`，要求两个独立账号分别完成任务、核实目录所有者、从各自备份读取不同样本并校验。账号切换时运行中 Worker 的停止仍需可重复的现场证据，结果为 BLOCKED |
| 删除审计无证据 | `FNSyncPlus/FNSyncPlus.Core/Storage/FnOs/FnOsApiClient.cs` 提供可选的真实发出请求类型记录。验收检查两个任务的实际轨迹，未分类请求阻止通过。只记操作类型及计数 |
| 清单缺失时恢复验收回退到本地样本 | `FNSyncPlus/FNSyncPlus.AcceptanceRunner/RecoveryAcceptance.cs` 要求显式选择模式。真实路径验证严格读取版本化清单或旧版主清单，异常返回 FAIL 或 BLOCKED。本地测试覆盖版本化恢复及损坏记录拒绝 |
| 恢复源完整性无法独立确认 | 同一文件以同一个验收编号绑定 NAS、账号或共享目录、快照标识和全目录 SHA-256 基线。源端写入变化会输出差异。冲突验收使用正式 RestoreEngine，校验冲突文件原样保留与其他文件的 SHA-256 |
| 验收脚本依赖源码与 SDK | 发布 `FNSyncPlus.AcceptanceRunner.exe` 为 .NET 8 Windows x64 单文件自包含程序。三个 PowerShell 入口调用同目录 EXE。离线冒烟在空 PATH、无可用 DOTNET_ROOT 且当前目录仅有 EXE 时执行 |
| 旧回归测试会在当前用户应用目录留下测试数据 | `FNSyncPlus/FNSyncPlus.Tests/Program.cs` 将完整性回归和会话样本定位到临时数据根。HTTPS 验收保留原始错误，并对隔离安装目录内属于本轮的回滚进程执行清理 |
| CI 将预期的失败退出码当成冒烟整体失败 | `tools/acceptance/Test-AcceptanceRunnerPublish.ps1` 检查进程码与 JSON 后显式返回整体成功状态。打包脚本保留 UTF-8 BOM 以兼容 Windows PowerShell 5.1 |

## 回归记录

| 项目 | 本机结果 |
| --- | --- |
| 完整性 | 551 PASS / 0 FAIL |
| P0 安全专项 | 86 PASS / 0 FAIL |
| 存储隔离 | 24 PASS / 0 FAIL |
| 账号隔离专项 | 13 PASS / 0 FAIL |
| 签名审计 | 11 PASS / 0 FAIL |
| 独立恢复专项 | 14 PASS / 0 FAIL |
| 更新进程交接 | 27 PASS / 0 FAIL |
| UI 隔离 | 3 PASS / 0 FAIL |
| 新增验收自检 | 10 PASS / 0 FAIL |
| 独立 EXE 冒烟 | 帮助、构建 SHA 及 11 组带退出码的检查通过 |
| HTTPS 回环 | 重跑 42 PASS / 0 FAIL。覆盖 TLS、签名、哈希、升级健康检查和故障回滚 |

首次本机 HTTPS 验收遇到原生启动崩溃，更新器回滚，随后独立重跑通过。首次并行回归在当前用户应用目录留下合成数据。已将 4 个经确认的合成迁移记录移到本地证据目录，共用连接日志保留。最终打包回归与 HTTPS 重跑均未命中生产存储路径，HTTPS 重跑前后用户数据对比一致。自动审批以策略阻止了清理本机回环测试生成的两枚一次性签名私钥。两个密钥留在本地忽略目录，未进入交付物。

候选包源码提交 `4cbb03d77dc3519ebb94dd0760d492aaf545d83e`。后续提交更新了 CI 冒烟脚本和交付报告，安装包及验收程序的构建来源由 ZIP 内 `manifest.json` 记录。

- [Build & Test](https://github.com/w15038510619-prog/FN-Sync-Plus/actions/runs/44035738436) 已通过。验证代码的提交为 `a402e859bf53d60670e0ff922abfc29b2bdc4ad6`。
- [HTTPS Update Acceptance](https://github.com/w15038510619-prog/FN-Sync-Plus/actions/runs/44035738389) 已通过，对应同一提交。
- [查看 Draft PR 当前检查与提交](https://github.com/w15038510619-prog/FN-Sync-Plus/pull/4/checks)

## 测试包

| 文件 | 路径 | SHA-256 |
| --- | --- | --- |
| Beta ZIP | `D:\FN_Sync_Plus_Pilot_20261117\build-4cbb03d\FN-Sync-Plus-Beta-Pilot-Windows-x64-4cbb03d77dc3.zip` | `41B4037D7DF25B3527495794D2D645BA6F72E5AB03D07D578B1DF633D1E2C53F` |
| x64 安装器 | `D:\FN_Sync_Plus_Pilot_20261117\build-4cbb03d\FN-Sync-Plus-Pilot\installer\FN-Sync-Plus-Beta-Pilot-Windows-x64-Setup.exe` | `DE91967E3A2280519BCF86577A2DE42CC8D5337698E9E7D79A5E1E371CE2EF3B` |
| 独立验收 EXE | `D:\FN_Sync_Plus_Pilot_20261117\build-4cbb03d\FN-Sync-Plus-Pilot\acceptance\FNSyncPlus.AcceptanceRunner.exe` | `904BCCF3BC9EBB341BD7F768EE81D1B21EB89F1A8900BE1D97A266259B9EC84D` |

最终 ZIP 解压后校验 10 个条目的 SHA-256，核对两个构建来源和验收程序版本，并用 Windows PowerShell 5.1 从解压后的目录实际运行三个脚本。候选应用载荷在开发机的隔离用户配置中启动，健康检查返回可用。

## 现场状态与准入

| 项目 | 结果 | 下一步 |
| --- | --- | --- |
| 干净 Windows x64 安装与启动 | BLOCKED | 当前只有开发机。用没有源码、Git、SDK 和已存凭据的第二台电脑或独立 Windows 虚拟机安装并运行 |
| 真实 NAS 冲突和快照不变 | BLOCKED | 在已授权的测试备份目录暂停写入，用同一验收编号生成基线、恢复并逐项比对 |
| 第二台 Windows 的当前与历史恢复 | BLOCKED | 用测试文件完成两次备份，在第二台电脑恢复两版，核对超长原名、目录与 SHA-256 |
| 真实双账号认证与隔离 | BLOCKED | 等两位授权测试账号的持有人在各自的测试目录配合，运行器在无凭据时已验证返回 7 项 BLOCKED |
| 真实账号 Worker 停止 | BLOCKED | 在实际正在运行的备份任务中触发正式断开或切换，并验证旧任务已停、断开后无远端写入 |

代码整改与测试包校验已完成。可以邀请一位同事使用干净 Windows 和合成样本开始受控 Beta 验收。真实 NAS、异机恢复与双账号项目按上表保留为 BLOCKED，正式资料部署尚未达到准入条件。

现场步骤见 [测试人员操作说明](测试人员操作说明.md)、[异机恢复验收说明](异机恢复验收说明.md) 和 [双账号测试说明](双账号测试说明.md)。每人用自己的账号与独立目录。本轮仅推送源码与验收报告，正式发布和更新服务器均未启用。
