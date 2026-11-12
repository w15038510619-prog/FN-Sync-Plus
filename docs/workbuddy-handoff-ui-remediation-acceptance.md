# 第三轮整改验收：自动更新安全交接、验收脚本隔离与 WinUI 完整渲染（2026-11-12）

分支：`workbuddy/enterprise-release-readiness`（PR #4，仍为 Draft，未合并 `main`，未创建正式 Release，未部署生产）
本轮起点 HEAD：`842a3950be7ffa35d07aaa0f6bdc035c6a1fe557`（`git fetch` 后与 `origin` 一致）
执行环境：单台 Windows 11 x64 实机；`dotnet 8.0.404`；显示器 1200×780 有效区域、100% 缩放（96 DPI）。
本机存在**真实生产配置**（`%APPDATA%\FNSyncPlus\settings.json` 含 1 个启用任务，`versioned-beta\credentials.dat` 含真实 NAS 凭据）——本轮所有涉及真实进程的验收均在**生产配置隔离（quarantine）+ 按字节还原**保护下执行，见第 5 节。

> **结论先说清楚：** P0-A（无人值守更新交接）、P0-B（验收脚本进程误杀）、P0-C（纯视觉验收模式）与 P1（页面就绪 + 长任务名）全部修复，并以**真实进程级证据**通过验收：真实双版本自动更新端到端 33/33 通过，UI 视觉验收 49/49 通过，全部回归 0 FAIL。**这仍不等于"已适合生产部署"**——125%/150%/200% 缩放、真实双账号、公网更新服务器、代码签名、完整 HTTPS 单链路仍为 BLOCKED（见第 8 节），当前定位仍是**内网试点候选版**，不能作为唯一备份。

---

## 1. 本轮改动概览

| 编号 | 缺陷 | 关键修复 | 结论 |
| --- | --- | --- | --- |
| P0-A | 无人值守更新时调度器**从不请求主程序退出**，且 `Dispose` 路径会**提前释放维护租约**，导致"更新器还没装完、备份已恢复运行"的竞态窗口 | `UpdateInstallSession` 显式状态机（`Preparing → UpdaterStarted → ShutdownRequested → Completed`，只进不退）；新增 `IApplicationShutdownCoordinator` 接口与 `WinUiShutdownCoordinator` 实现（WinUI 主窗口优雅关闭）；`ProcessHandoffWatchdog` 监督式看门狗——**绝不按固定时间释放**，只在确认更新器放弃等待（进程退出且无成功日志）后才恢复备份；手动"立即更新"与无人值守调度**复用同一安全退出机制`UpdateService.RequestGracefulExitAsync` | 已修复，11 项交接专项 + 真实进程监督测试 |
| P0-B | 验收脚本按进程名（`Get-Process FNSyncPlus*`）杀进程，**会误杀用户正在使用的真实实例** | `tools/acceptance/handoff-e2e-acceptance.ps1`：启动前快照全部既有进程（存在实例即拒跑）；所有清理只针对**本轮启动并登记**的 PID，且杀前校验 PID + 进程启动时间 + 可执行文件路径三重身份；受保护的 decoy 模拟实例全程存活断言（decoy 死亡即 FAIL）；`CloseMainWindow` 8 秒优雅关闭优先，之后才受控强杀 | 已修复，e2e 中 decoy 全程存活 |
| P0-C | UI 验收读真实/历史凭据、连真实 NAS，测试即事故 | `--ui-visual-acceptance` 纯视觉模式：会话配置**不携带凭据**（`UiIsolationTestSuite` 断言）、不执行任何备份、不发起任何 NAS 拨号（连接日志零增量断言）；窗口标题带 `[隔离测试会话]` 标识 | 已修复，3+4 项隔离专项 |
| P1-1 | 截图在页面未渲染完成时拍摄（拍到空白/半成品页） | `UiVisualSignal` 就绪信标：页面构造完成并装载实际数据后置事件；探针等信标才截图，超时即 FAIL | 已修复，4 页面 × 2 主题全部带就绪信标 |
| P1-2 | 长中文/英文任务名在任务卡片溢出、破坏布局 | `TasksPage.xaml` 卡片名文本 `TextTrimming="CharacterEllipsis"` + 固定行高 + `TextWrapping` 约束 | 已修复，任务首页 5 项内容断言 |
| 隔离卫生 | 隔离会话的 `[update]` 日志写入生产 `LogService.Default`，泄漏进生产连接日志 | `AppServices.cs`：更新日志改走**会话感知**的日志服务 | 已修复 |

## 2. 真实自动更新端到端验收（核心证据）

**命令**：`tools/acceptance/handoff-e2e-acceptance.ps1`（自包含：生成 ed25519 密钥对 → 构建真实 0.5.0 与 0.6.0 两个完整版本 → 隔离生产配置 → 启动真实进程执行无人值守更新 → 全程断言 → 还原）
**结果**：**33 PASS / 0 FAIL**（产物：`.acceptance/handoff-20261112-120606/acceptance-result.json`）

### 2.1 端到端证据链（用户要求的每一项）

| 要求项 | 实测值 | 断言结果 |
| --- | --- | --- |
| 原程序 PID | **26732**（0.5.0，路径在 `layout\install\app-0.5.0\`，隔离会话 `0a06a38c…`） | PASS |
| 更新器 PID | **28284**（真实独立更新器进程，`--wait-pid 26732`） | PASS |
| 维护锁交接状态 | 探针三阶段真值：`lease_held_after_prepare=true`、`lease_held_after_start=true`、**`lease_held_before_exit=true`**（退出前租约仍由本会话持有，未提前释放）；状态机 `session_state=ShutdownRequested`、`shutdown_delivered=true` | PASS |
| 原程序退出结果 | **自行优雅退出**（`by_itself=true`，接到协调器关闭请求后 ~6ms 内退出，无需脚本强杀） | PASS |
| 安装结果 | 更新器验证签名 → 安装 0.6.0 → `current.json` 指针切换至 0.6.0（切换等待 61949ms）；journal 实录：`apply 0.6.0 from verified release` → `installed 0.6.0` → `launch 0.6.0 ok (pid 10932)`；`previous.json=0.5.0`（回滚锚正确）；启动器字节未变 | PASS |
| 新版本 PID | **10932**（路径在 `app-0.6.0\` 载荷内，版本指针生效的直接证据） | PASS |
| 新版本健康状态 | 健康报告 `usable=true`、`task_manager_ok=true`、`task_count=0`（隔离配置无任务，符合预期）；启动后稳定存活直至脚本收尾 | PASS |
| 备份恢复情况 | 无人值守路径下租约由**监督看门狗**持有直至会话完成——更新成功场景**不恢复**自动备份（正确行为：新版本已接管）；生产用户数据走查 `changed=[]、leftovers=[]、missing=[]`（零残留、零篡改） | PASS |

### 2.2 时间线（journal + 验收脚本双源记录）

```
04:06:07  production-settings-quarantined   （隔离生产 settings.json + versioned-beta 两文件，本机有 1 个启用任务）
04:09:18  layout-ready                     （0.5.0 真实构建完成）
04:09:22  decoy-started (pid 1884)         （受保护 decoy 模拟实例，误杀检测哨兵）
04:09:22  app-A-started (pid 26732, 0.5.0) （探针模式 --update-handoff-probe）
04:09:24  probe-report                     （prepared=true, ShutdownRequested, updater_pid=28284, shutdown_delivered=true）
04:09:24  app-A-exited (by_itself=true)    （协调器优雅关闭，无强杀）
04:10:26  pointer-switched → 0.6.0         （更新器安装完成，指针切换）
04:10:28  journal: launch 0.6.0 ok (pid 10932)
04:10:28  app-B-running                    （路径核实于 app-0.6.0\ 内）
```

### 2.3 安全性断言（e2e 内嵌）

- **零 NAS 拨号**：全程连接日志增量扫描 `成功连接|ConnectAsync|TlsProbe|SMB` 零命中（`nas_online=true` 类哨兵字段不作为证据——它默认即为 true）。
- **凭据零泄漏**：隔离会话配置不含凭据；生产 `versioned-beta\credentials.dat` 在隔离期被移走、e2e 结束后**按字节还原**（SHA256 校验一致）。
- **decoy 全程存活**：验收期间发现过的最大风险（按进程名误杀）现在有硬断言——本轮 decoy（pid 1884）从启动到脚本退出始终存活。
- **无既有实例时才运行**：启动前进程快照发现任何 `FNSyncPlus` 既有实例即拒绝运行（本轮满足）。

## 3. WinUI 视觉验收

**命令**：`tools/acceptance/ui-visual-acceptance.ps1`（`--ui-visual-acceptance` 纯视觉模式）
**结果**：**49 PASS / 0 FAIL**（产物：`.acceptance/ui-round3-final3/`，含 14 张 PNG 截图 + 逐张元数据 JSON + 汇总 `ui-acceptance-result.json`）

### 3.1 修复的度量缺陷（重要方法论说明）

初版用 `ContentRatio`（量化色桶数/256）判定截图内容，实测发现它对子像素抗锯齿与 Mica 材质敏感（窗口非激活时塌缩到 0.0469，真实页面被判空白）。已改为双指标：

- **`ContentCoverage`**（通过条件，≥0.05）：与主色不同的采样像素占比——真实页面实测 0.28–0.64，空白页 ≈0，对 AA/Mica 稳定；
- `ContentRatio` 保留记录用于横向对比。

### 3.2 截图方案（滚动方案的诚实废弃）

WinUI 3 下滚轮注入（`mouse_event`/`WM_MOUSEWHEEL`/置顶+前台）实测**均不生效**——截图与未滚动帧字节相同。与其记录不可靠的 `scroll_effective=false`，改为**加高窗口（1200×1100）+ `PrintWindow` 完整渲染**直接截取折叠线以下内容（更新卡片 + 容量卡片实测可见），最小窗口尺寸则用 1000×640 断言。设置页 `Settings(tall)` 记录 `height=1100`，`content_coverage=0.6069`。

### 3.3 验收矩阵（每格含页面就绪信标 + 内容覆盖 + 文件存在 + 元数据完整断言）

| 场景 | 主题 | 页面 | 就绪信标 | 内容覆盖 | 结果 |
| --- | --- | --- | --- | --- | --- |
| 任务首页 | Light/Dark | Tasks | ✅ | 0.64 / — | PASS |
| 任务首页（最小窗口 1000×640） | Light/Dark | Tasks(min) | ✅ | — | PASS |
| 设置页（更新卡片 + 容量卡片，加高窗口） | Light/Dark | Settings(tall) | ✅ | 0.6069 / — | PASS |
| 恢复中心 | Light/Dark | Restore | ✅ | — | PASS |
| 设备页 | Light/Dark | Device | ✅ | — | PASS |
| 空任务态 | Light/Dark | empty-Tasks | ✅ | — | PASS |
| 长任务名裁剪（中文/英文混排，省略号） | Light/Dark | Tasks | ✅ | — | PASS |
| 纯视觉模式隔离断言（无凭据/无拨号/受保护断言存在） | — | — | — | — | PASS（`--ui-isolation-tests` 7/7） |

窗口标题全程为 `飞牛同步增强版 v0.6.0 [隔离测试会话]`——版本号来自程序集（上一轮修复），隔离标识可见。

## 4. 测试集合与计数（不同集合，禁止相加）

| 集合 | 命令 | 结果 | 说明 |
| --- | --- | --- | --- |
| 完整性回归 | `--integrity-regressions` | exit=0 | 上两轮全部既有行为无回归 |
| P0 专项 | `--p0-tests` | **79 PASS / 0 FAIL** | 含本轮新增交接状态机用例 |
| 更新交接专项（真实进程） | `--handoff-process-tests` | **11 PASS / 0 FAIL** | 真实进程监督、绝不按时间释放租约等 |
| UI 隔离专项 | `--ui-isolation-tests` | **3 PASS / 0 FAIL**（带 `--ui-result` 产物审计后 **7 PASS / 0 FAIL**） | 纯视觉模式无凭据、实例互斥 |
| 默认全套 | （默认） | **333 PASS / 0 FAIL / 3 SKIP** | 3 SKIP 为待验证的远端接口，非本轮范围 |
| 真实交接 e2e | `handoff-e2e-acceptance.ps1` | **33 PASS / 0 FAIL** | 第 2 节 |
| UI 视觉验收 | `ui-visual-acceptance.ps1` | **49 PASS / 0 FAIL** | 第 3 节 |

## 5. 生产配置隔离方法（验收窗口内）

本轮在**有真实生产配置的机器**上执行真实验收，隔离流程：

1. 快照用户数据目录（logs 记路径、`*.lock` 记锁、其余文件 SHA256）——快照先于隔离拍摄；
2. 隔离（备份→移走）：生产 `%APPDATA%\FNSyncPlus\settings.json`、`%LOCALAPPDATA%\FNSyncPlus\versioned-beta\{settings.json, credentials.dat}`；
3. 全部还原/删除操作由 `$script:quarantinePerformed` 标志门控——脚本若在隔离前早死，**不会误删任何真实文件**；
4. finally 按字节还原 + SHA256 校验；走查对比 `changed/leftovers/missing` 并写入结果 JSON（本轮三项全空）。

## 6. CI 与交付

- 新增两个 CI 步骤已接入 `.github/workflows/build.yml`（pull_request 覆盖 `workbuddy/**`）：
  `Run Update Handoff Process Tests`、`Run UI Isolation Tests`（CI 环境无 `--ui-result` 参数时仅运行 3 项纯隔离用例，不依赖截图/显示器，CI 安全）。
- 提交分 7 批（见 PR #4 提交历史）：
  1. 无人值守交接修复（状态机 + 协调器接口 + 看门狗 + UpdateService + 两个新测试套件）
  2. 调度器与 WinUI 关闭协调接线（UpdateScheduler/AppServices/SettingsViewModel/MainWindow/UiVisualSignal）
  3. UI 进程隔离 + 纯 UI 模式验收脚本（ui-visual-acceptance.ps1/UiIsolationTestSuite/AppSettings）
  4. `App.xaml.cs` 纯视觉模式入口
  5. UiProbe 度量修复（ContentCoverage + PrintWindow 完整渲染）
  6. TasksPage.xaml 长任务名修复
  7. e2e 验收脚本 + CI 接线 + 本报告
- PR #4 推送与 CI 实际运行结果：
  - 代码提交（`02368d3…8c6446a`）触发 run **43155829841**：**success（7m28s，build-windows 全部步骤通过）**，含本轮新增的
    `Run Update Handoff Process Tests`（11 项）与 `Run UI Isolation Tests`（3 项 CI 安全用例）两个步骤，凭据扫描步骤通过。

## 7. 剩余 BLOCKED（单机无法构成证据）

| 项 | 状态 | 原因 |
| --- | --- | --- |
| 125% / 150% / 200% DPI 缩放验收 | BLOCKED | 本机仅 100%（96 DPI），WinUI 坐标换算未在不同缩放下实测 |
| 真实双账号场景 | BLOCKED | 仅有单账号 NAS 环境 |
| 公网更新服务器 + 完整 HTTPS 单链路 | BLOCKED | 无公网可达的更新服务器；本轮验证的是"签名验证 + 指针切换 + journal"机制本身 |
| 代码签名证书 | BLOCKED | 无证书；更新器签名链用的是自生成 ed25519（机制等价，信任链不等价） |

## 8. 定位重申

本轮完成后，`workbuddy/enterprise-release-readiness` 分支具备：真实可复现的无人值守更新安全交接、零误杀验收工具链、零凭据泄漏的 UI 纯视觉验收。**仍不合并 `main`、不创建正式 Release、不部署生产、不触发任何生产环境自动更新**；未增加新备份业务功能，未开发历史自动删除。当前定位：**内网试点候选版**。
