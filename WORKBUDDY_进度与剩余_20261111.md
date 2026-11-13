# WorkBuddy 接手进度与剩余工作（2026-11-11）

> 分支：`workbuddy/enterprise-release-readiness`（基于 Claude 的 `b9e5854`，**未合并 main，未推送**）
> 工作目录：`D:\FN_Sync_Plus_WB`（独立 worktree，避免与其他 AI 争用 `D:\FN_AutoSync_Package`）
> 环境：Windows x64 + .NET 8.0.404（本地解压到 `D:\FN_SyncPlus_Toolchain\dotnet`）

## 0. 先看这三处

1. `docs/phase4/acceptance-matrix.md` — PASS/FAIL/BLOCKED/SKIP 总表与完成度。
2. `docs/acceptance-status.md` — 对外口径的验收状态。
3. `docs/phase2/update-engine-report.md` + `docs/phase3/productization-report.md` — 本次新增能力的证据。

## 1. 本次完成

| 内容 | 证据 |
|---|---|
| 修复一处入库前测试断言缺陷（改名历史 `Single` → 校验追加且保留原历史） | 完整性套件从 “424 PASS / 1 FAIL” 变为全绿 |
| 阶段 1 收尾复核 | 465 PASS / 0 FAIL / 0 SKIP |
| 阶段 2 自动更新全链路 | 30 项用例 + 真实二进制 `--apply` 端到端 PASS |
| 阶段 3 容量统计与保留预览（只读） | 10 项用例 PASS |
| 阶段 3 安装程序脚本 | `FNSyncPlus/installer/FNSyncPlus.iss`（未编译） |
| 阶段 3 文档与安全审计 | `docs/deployment-guide.md` 等 5 份 |
| 阶段 4 冻结与矩阵 | `docs/phase4/acceptance-matrix.md` |
| 推送与 CI | 分支已推送，Draft PR #4；CI runs/42985013182 全绿（465 / 0 / 0） |

## 2. 关键新增文件

```
FNSyncPlus/FNSyncPlus.Updater/                    独立更新器（单文件自包含）
FNSyncPlus/FNSyncPlus.Core/Updates/               UpdateInstaller / UpdatePackageExtractor /
                                                  LaunchHealthTracker / DataFormatGuard /
                                                  UpdateService / UpdateManifestSigner / PinnedUpdateKey
FNSyncPlus/FNSyncPlus.Core/Services/UpdateTestSuite.cs
FNSyncPlus/FNSyncPlus.Core/Services/BackupCapacityAnalyzer.cs
FNSyncPlus/FNSyncPlus.Core/Services/CapacityTestSuite.cs
FNSyncPlus/build-release.ps1                      版本化安装目录 + 载荷 ZIP + 签名清单
FNSyncPlus/installer/FNSyncPlus.iss               用户级安装程序
tools/FNSyncPlus.UpdateTool/                      密钥/签名/校验工具
```

## 3. 产物位置（本机）

```
D:\FN_SyncPlus_ReleaseBuild\
  install\                       版本化安装目录（app-0.4.0 + 启动器 + current.json）
  FN-Sync-Plus-0.4.1-x64.zip     141,843,212 字节，SHA-256 F74C888F…860EB
  manifest.json                  演示用签名清单（一次性测试密钥，私钥已删除）
  keys\test-public.pem           仅用于复核演示清单
```

## 4. 下一件事（按优先级）

1. **安装包**：在装好 Inno Setup 的机器上 `ISCC.exe /DAppVersion=0.4.1 FNSyncPlus\installer\FNSyncPlus.iss`，然后做安装→升级→卸载→重装演练。
2. **GUI 目视验收**：设置页的更新卡片与容量卡片在 125%/150%/200% 缩放、小窗口、深色主题下的排版。
3. **异机恢复**：Windows Sandbox 或 VM，只按 NAS 连接信息发现并恢复，逐项核对原名与 SHA-256。
4. **内网更新服务器**：部署 HTTPS，用受控密钥签名，跑通一次真实升级并记录。
5. 推送分支 → 触发 CI → 核对 `--integrity-regressions` 与常规套件在 windows-latest 上的结果。

## 5. 不要做的事

- 不要合并 main，不要发布会触发全员更新的清单；
- 不要在没有正式签名与验收前开启「无人值守安装」；
- 不要为省空间去删 `.fnsync` 或历史对象（保留策略目前只做只读预览）；
- 不要把本机 PASS 说成真实设备验收或 CI 通过。
