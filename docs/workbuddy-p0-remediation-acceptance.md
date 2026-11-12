# 自动更新 P0 整改与企业试点验收（2026-11-12）

分支：`workbuddy/enterprise-release-readiness`（PR #4，仍为 Draft，未合并 `main`，未创建正式 Release）
起点 HEAD：`22010c792268ce1c5578e994e9b8efcc7524694d`（`git fetch` 后与 `origin` 一致，无其他开发者新提交）
执行环境：单台 Windows 11 x64 实机；`dotnet 8.0.404`（`D:\FNSyncPlus_Toolchain\dotnet`）；Inno Setup 6（winget 安装于
`%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`）；显示器 1200×780 有效区域、系统缩放 100%（96 DPI）。

> **结论先说清楚：本轮把 4 个 P0 的真实缺陷改掉了并用可复现的证据证明；自动化测试与脚本化验收全部通过。
> 但这不等于"已适合生产部署"。** 异机恢复、真实双账号、125%/150%/200% 缩放、真实外网更新服务器仍为
> BLOCKED（单机无法构成证据），详见第 9 节。当前定位仍是**内网试点候选版**，不能作为唯一备份。

---

## 1. 本轮改动概览

| 编号 | 缺陷 | 关键文件 | 结论 |
| --- | --- | --- | --- |
| P0-1 | 维护状态泄漏，失败更新后自动备份永久停摆 | `Services/TaskManager.cs`、`Services/MaintenanceLease.cs`、`Updates/BackupActivityGate.cs`、`Updates/UpdateInstallSession.cs` | 已修复，11 项行为测试 |
| P0-2 | 只有"自动检查更新"设置项，没有真实后台调度 | `Updates/UpdateScheduler.cs`、`WinUI/App.xaml.cs`、`WinUI/Services/AppServices.cs` | 已实现，18 项调度测试 + 端到端验证 |
| P0-3 | 独立更新器接受命令行 `--sha256/--version`，签名链可被绕过 | `Updates/UpdateManifestVerifier.cs`、`Updates/VerifiedRelease.cs`、`Updates/ReleaseHandoff.cs`、`Updater/Program.cs` | 已修复，27 项攻击矩阵 + 真实 CLI 拒绝验证 |
| P0-4 | 数据格式标记读失败被当成"格式 0"，故障开放 | `Updates/DataFormatGuard.cs`、`Updates/VersionedInstallLayout.cs`、`Updates/AppBuildMetadata.cs` | 已修复，16 项格式测试 + 回滚兼容矩阵 |

附带修复（验收过程中暴露的真实产品缺陷，非重构）：

1. **主题设置从未被读取**：`AppTheme` 会写入配置，但启动时没有任何代码读取，用户选了深色主题，每次启动仍是浅色。
   已在 `MainWindow.ApplyStoredTheme()` 修复，并在切换时回写配置。
2. **标题栏版本号硬编码**：升级到 0.6.0 后标题仍显示 `v0.4.0-beta`。已改为读取正在运行程序集的
   `AssemblyInformationalVersion`（`MainWindow.ProductVersionText()`）。
3. **`current.json`/`previous.json` 记录别的机器的绝对路径**（安装包安装或整目录搬迁后），启动直接失败。
   已改为锚定到本机目录并回写（`VersionedInstallLayout.ReadPointer`），4 项迁移测试。
4. **测试污染仓库根目录**：维护/调度测试用相对路径 `dest-1`、`占用目录` 作为远端目标，实际落在进程工作目录。
   已改为绝对路径；历史残留已移出到 `.acceptance/residue/`。

---

## 2. 测试集合与计数（**不同集合，禁止相加**）

| 集合 | 命令 | 结果 | 日志 |
| --- | --- | --- | --- |
| P0 整改专项 | `--p0-tests` | **76 PASS / 0 FAIL** | `.acceptance/tests/p0.log` |
| 隔离完整性回归 | `--integrity-regressions` | **541 PASS / 0 FAIL / 0 SKIP** | `.acceptance/tests/integrity.log` |
| 常规离线套件 | （无参数） | **333 PASS / 0 FAIL / 3 SKIP** | `.acceptance/tests/offline.log` |
| 多账号隔离 | `--account-isolation` | **13 PASS / 0 FAIL** | 控制台 |
| 解决方案编译 | `dotnet build FNSyncPlus.sln -c Release -p:Platform=x64` | **0 警告 / 0 错误** | `.acceptance/tests/build.log` |
| 真实双版本升级回滚 | `tools/acceptance/ab-upgrade-acceptance.ps1` | **27 PASS / 0 FAIL** | `.acceptance/ab-20261111-175437/acceptance-result.json` |
| Inno Setup 安装/升级/卸载/重装 | `tools/acceptance/installer-acceptance.ps1` | **13 PASS / 0 FAIL** | `.acceptance/installer-20261111-173643/installer-result.json` |
| WinUI 外观（100% DPI，明/暗，最小窗口） | `tools/acceptance/ui-visual-acceptance.ps1` | **13 PASS / 0 FAIL**（含 4 张人工复核截图） | `.acceptance/ui-final/ui-acceptance-result.json` |
| GitHub Actions（PR #4，run 43142664651） | 凭据扫描 + 上述三类入口 | **success**：凭据扫描 202 个文件通过；完整性 **541/0/0**；账号隔离 **13/0**；P0 **76/0**；离线 **328/0/4 SKIP** | Actions 运行日志 |

四类入口互不包含：`--integrity-regressions` 不含 P0 专项用例；离线套件是另一批用例；三个 `.ps1` 是脚本化
端到端验收，调用真实构建产物与真实 `ISCC.exe`，不是单元测试。因此 **76 + 541 + 333 这类加法没有意义**。

---

## 3. P0-1 维护状态泄漏

**根因**：`TaskManager.WaitForSafeInstallAsync` 在任何"不能安装"的分支（`Defer`/`Refuse`/取消/超时）先前已调用
`_blockNewWork()`，但没有对应的解除；更新器随后等待主程序退出，进程一退，磁盘上的配置仍是"暂停"。表现为
失败一次更新后，所有自动备份永久不再启动，且 UI 上看不出原因。

**修法**（不是"在 finally 里无条件解除"——那会在更新器已接管、等主程序退出时把新备份放进来）：

- `MaintenanceLease`：带所有者 `Guid` 的租约令牌，`Release()` 幂等且只对**第一次**调用返回 `true`；
  非所有者释放无效并记录日志。
- `BackupActivityGate.TryAcquireAsync` 返回 `GateAttempt(Readiness, Lease?)`：只在 `Proceed` 时把租约交出去，
  取消/超时/状态不明/保存配置失败都自己释放。
- `UpdateInstallSession`：`Open → HandedOff → Aborted` 状态机。交接成功后**故意不释放**租约——备份由
  更新器接管，主程序退出由 watchdog（`CommitHandOff(abandonAfter)`）兜底。
- 同时修掉一个并发竞态：`ProcessQueueLoopAsync` 现在把"检查维护状态 + 出队 + 校验任务"放在同一个
  `_taskLifecycleLock` 内；若取到执行槽时已被维护占用，任务**重新入队**而不是被丢弃。

**证据**：`Services/MaintenanceLifecycleTestSuite.cs` 11 项用例用真实 `TaskManager` + 可控慢速 provider
断言**调度行为**（队列里的任务是否真的被执行/是否仍然滞留），不是断言一个布尔值。典型用例：

- 交接失败后租约被释放，后续任务实际恢复执行（而不是"`_maintenanceHold == false`"）；
- 交接成功后新任务不被启动，主程序退出后（abandon watchdog）恢复；
- 重复 `Release()` 只有第一次生效；
- 非所有者释放不生效；
- 维护期间入队的任务不会被静默丢弃。

**残留风险**：租约是进程内对象 + 磁盘配置双写。若进程在"取租约"与"写配置"之间被强杀，磁盘仍可能停在暂停态；
目前靠下次启动时的健康检查与 UI 手动恢复按钮兜底，**未做**跨进程租约（多实例同时操作安装目录）的强一致验证。

---

## 4. P0-2 真实后台自动更新调度

`Updates/UpdateScheduler.cs`：单循环、`SemaphoreSlim(1,1)` 防重入、启动延迟 90s、周期 6h、指数退避
（1min→1h，`MinBackoff × 2^n` 上限 `MaxBackoff`）、`HandoffVersion` 防"升级后重启又降级重装"死循环。

模式（与用户可见设置一致）：

| 模式 | 行为 | 默认 |
| --- | --- | --- |
| A 检查并提醒 | 6h 检查，提示用户 | 默认 |
| B 下载并提醒 | 6h 检查 + 静默下载 + 提醒安装 | 可选 |
| C 无人值守安装 | 检查 + 下载 + 安装 | **关闭**（需 `UnattendedEnabled` + `CanInstall` + `HandoffVersion` 全部满足） |

**证据**：`Services/UpdateSchedulerTestSuite.cs` 18 项，对 `LoopbackHttpsServer`（自签 TLS、支持 Range、
带命中计数与并发峰值计数）验证：模式 A 不下载（`pkgHits == 0 && StagedVersion` 为空）、模式 B 下载一次且
重复检查不重复下载、模式 C 关闭时不安装、退避序列、重启后设置与已下载版本仍有效、并发检查只跑一个、
取消立即生效、`HandoffVersion == 当前版本` 时不再触发安装。

**端到端**：A/B 验收里版本 0.5.0 通过 `--apply` 装到 0.6.0 后，`updates.json` 的 `HandoffVersion` 记录为 0.6.0，
`.health/report-0.6.0.json` 覆盖写入（见第 6 节）。settings 页新增 `AutoCheckStatus` 一行文本，显示调度器
实际状态（`SettingsPage.xaml` + `SettingsViewModel.OnSchedulerChanged`，经 `UIHelper.RunOnUIThread` 回主线程）。

**残留风险**：调度器只在主程序运行时存在，**没有** Windows 计划任务/服务形态，程序不开就不会检查更新（这是当前
产品定位，不是缺陷，但企业部署需要明确告知）。真实外网 CDN、限速、断点续传的大包场景未验证。

---

## 5. P0-3 更新器信任链

**修复前**：`--apply` 接受 `--package <path> --version <v> --sha256 <hash>`，哈希由调用方（UI）算完再传，
UI 自己又是用"下载得到的文件"算的哈希——这是循环论证；且版本号取自第一次清单请求、包来自第二次下载，
两次请求之间可被替换（TOCTOU）。签名验证实际上只在"正常 UI 路径"存在，独立更新器本身可被直接调用绕过。

**修复后**：

1. `UpdateManifestVerifier.VerifyRelease()` 一次校验，返回 `ReleaseVerification(Ok, Error, Manifest, Channel)`，
   空签名直接拒绝。
2. `VerifiedRelease` 是不可变记录：`ManifestJson / Version / Channel / Sha256 / Size / PackageUrl /
   LocalPackagePath / MachineGroup / ReleaseBatch`，全部**来自已验证的清单字节**。
3. UI 把 `VerifiedRelease` 序列化为 `ReleaseDescriptor` 落到 `<install>\.pending\release-<version>.json`，
   更新器只用 `--apply --release <json> --wait-pid <pid> --wait-seconds 180`。
4. 更新器 `ReleaseHandoff.Verify()` 重新用内嵌公钥验证清单字节，并逐项比对：版本、SHA-256、大小、包路径必须
   位于安装根内、不得指向 `release.json` 本身、文件实际大小与哈希。任何一项不符返回 `ExitReleaseRejected (6)`。
5. 命令行 `--sha256` / `--version` 仍然被解析，但**只为了拒绝**：`ExitUsage (2)`，提示"不再接受命令行
   `--sha256 / --version`，请传入 `--release <release.json>`"。

**证据**：`Services/ReleaseTrustTestSuite.cs` 27 项攻击矩阵（覆盖：无描述符、无验证器、未知已安装版本、
包在安装根外、包缺失、包指向 `release.json`、签名被改、版本/哈希/大小与清单不符、实际文件被替换、
重放旧版本、同版本重复装、通道不符等）。真实 CLI 验证见 A/B 验收第 15–19 项：

- `--sha256/--version` → `exit=2`
- 只有清单没有描述符 → `exit=2`
- 包被改一个字节 → `exit=6`
- 包放在安装目录之外 → `exit=6`
- 同版本重复安装 → `exit=6`

**残留风险**：公钥通过 `EmbeddedResource` 编译进程序（`UpdatePublicKeyPath`），当前私钥为**本轮一次性生成**的
测试密钥，仅用于验收；正式发布必须换成受控的离线签名密钥并建立密钥轮换/吊销流程（未做）。更新器自身
（`FN_Sync_Plus.exe`）的代码签名仍未做，Windows SmartScreen 会拦。

---

## 6. P0-4 数据格式守卫

**修复前**：读取 `data-format.json` 失败（不存在/损坏/无权限/未来版本）一律返回 `null`，调用方把 `null` 当成
格式 0，于是**降级运行**并可能用旧代码写坏新格式数据。

**修复后**：

- `FormatMarkerState { Missing, Valid, Corrupt, Unreadable, Unsupported }` + `FormatMarkerInspection`
  （`AllowsInitialization`、`Blocks()`），`Probe()` 只读不写。
- `TryClaim` 只在 `Missing` 时初始化；已存在更低格式时**保持**不抬高；`Corrupt`/`Unsupported` 拒绝并
  `PreserveForDiagnosis`（保留为 `.corrupt-<时间戳>`，绝不删除）；`Unreadable` 拒绝。
- 写标记：临时文件 + `File.Replace`/`Move` + 回读确认；跨进程用 `<path>.lock`（`FileShare.None`，25 次 × 80ms）。
- 真实迁移走 `TryUpgrade(appVersion, newFormat, out error)`。
- 回滚兼容性看**目标版本声明的格式**（`build-meta.json` + 指针里的 `supported_data_format`），不是看当前二进制：
  `TryRestorePointer`/`CanRollbackToVersion` 在标记损坏、目标未声明格式、或 `previous < marker.Format` 时拒绝。

**证据**：`Services/DataFormatTestSuite.cs` 16 项（缺失/有效/损坏/不可读/更高版本/并发锁/写失败回读/
损坏文件保留/回滚矩阵）；`LayoutRelocationTestSuite` 4 项。真实 CLI：A/B 验收第 20–23 项——
格式 2 高于旧版本能力时回滚被拒（`exit=3`，且不改写任何数据），格式一致时回滚成功（`exit=0`，指针回到 0.5.0），
没有 `previous.json` 时拒绝回滚。

**残留风险**：`supported_data_format` 目前恒为 1，说明真实迁移路径**从未在生产数据上跑过**，只是代码与测试就绪。

---

## 7. 自动更新系统逐项结论

| 项 | 结论 | 依据 |
| --- | --- | --- |
| 启动后自动检查（延迟 90s） | **PASS** | `UpdateSchedulerTestSuite` + 真实启动 `App.StartBackgroundUpdateChecks` |
| 每 6 小时周期检查 | **PASS** | 调度用例（注入时间源/短间隔） |
| 指数退避与上限 | **PASS** | 调度用例序列断言 |
| 防重复下载/防重复检查 | **PASS** | `pkgHits`、`MaxConcurrentRequests` 计数 |
| 取消进行中的检查/下载 | **PASS** | 取消用例 |
| 模式 A/B/C 与 C 默认关闭 | **PASS** | 三模式用例 + `UnattendedEnabled` 双重条件 |
| 升级后不再重复安装同一版本 | **PASS** | `HandoffVersion` 用例 + A/B 实测 |
| 清单签名验证（内嵌公钥） | **PASS** | 27 项攻击矩阵 |
| 命令行哈希/版本注入 | **已封堵（PASS）** | `exit=2` |
| TOCTOU（版本与包不一致） | **已封堵（PASS）** | `VerifiedRelease` 单次来源 |
| 安装目录外/被篡改的包 | **已封堵（PASS）** | `exit=6` |
| 真实公网更新服务器 + CDN | **BLOCKED** | 无可用外网发布端点，本轮只在本机 HTTPS 回环与本地文件路径验证 |
| 更新器代码签名 / SmartScreen | **BLOCKED** | 无企业代码签名证书 |
| 灰度/分批（`ReleaseBatch`/`MachineGroup`） | **部分（SKIP）** | 字段与解析已就绪，未做真实分批下发验证 |
| 升级失败自动回滚到上一版本 | **PASS（脚本化）** | A/B 第 21–23 项；**未**在"新版本启动后崩溃"场景做无人值守自动回滚 |

---

## 8. 安装与卸载验收（Inno Setup，真实 `ISCC.exe` 编译 + 真实静默安装）

`.acceptance/installer-20261111-173643/installer-result.json`，13/13 PASS：

1. 编译 0.5.0 安装包 → `setup-a\FN_Sync_Plus_0.5.0_x64_setup.exe`
2. 静默安装后 `status` 解析到 0.5.0
3. 安装目录内启动器可运行（指针已改写为本机路径——这是本轮修的第 3 个缺陷）
4. 编译 0.6.0 安装包
5. 覆盖安装升级到 0.6.0，`app-0.5.0` 保留
6. 升级写入 `previous.json` 指向 0.5.0
7. 安装后仍可 `--rollback` 回到 0.5.0
8. 卸载移除启动器
9. 卸载保留 `%LOCALAPPDATA%\FNSyncPlus` 用户配置
10. 卸载清除 HKCU 卸载项
11. 卸载后可重装同一版本
12. 收尾再次卸载

同时修正了 `.iss` 里的 Pascal 脚本：`StringChangeEx` 替换反斜杠、`LoadStringFromFile` 读 `build-meta.json`
的 `supported_data_format`、安装后校验版本目录与启动器存在（编译期错误已修完，`ISCC` 输出 0 error）。

**残留风险**：未做"安装时程序正在运行"的占用场景、未做非管理员/受限用户安装、未做中文路径安装目录
（`C:\程序\飞牛同步`）。

---

## 9. WinUI 外观验收（100% DPI）

脚本：`tools/acceptance/ui-visual-acceptance.ps1` + `tools/acceptance/UiProbe`（`PrintWindow` 抓窗口本身，
不依赖窗口是否在最前）。产物：`.acceptance/ui-final/*.png`，13/13 PASS。

| 场景 | 结果 | 关键数据 |
| --- | --- | --- |
| 浅色主题启动 | PASS | 1200×780，DPI 96（100%），平均亮度 236 |
| 深色主题启动 | PASS | 1200×780，平均亮度 47（修复前深色设置根本不生效） |
| 最小窗口 1000×640 | PASS | 布局完整，三行任务 + 全部按钮可见 |
| 长中文名（含日文/韩文/希腊字母） | PASS（有瑕疵） | 卡片内被硬裁切，见下 |
| 超长英文名（86 字符无空格） | PASS（有瑕疵） | 硬裁切并出现溢出标记 |
| 结束后无残留进程 | PASS | 0 |

人工复核发现（**不是**自动化能判定的，已逐张看图）：

1. **长任务名是硬裁切，没有省略号**。`家庭照片与四年工作文档完整备份（…测试・테스트・Ω≈ç√ 2026 年度归档归档归档）`
   在卡片右边缘被切断；最小宽度下更明显。建议改为 `TextTrimming="CharacterEllipsis"` + `ToolTip` 显示全名。
2. **超长英文名**同样硬裁且右侧出现红色溢出标记，观感差于中文。
3. 标题栏版本号现在随程序集变化（修复前是硬编码 `v0.4.0-beta`）。
4. 隔离测试会话仍会自动重连上次保存的账号，状态条显示 `192.168.8.120 · wangxiaoliang（fnOS 个人空间已连接）`。
   凭据文件位于 `%LOCALAPPDATA%\FNSyncPlus\test-session\credentials.dat`，与生产凭据文件分离；本次验收未创建
   任何任务同步、未写入 NAS。仍是需要向试点用户说明的行为。
5. `Ctrl+1` 浮层是 WinUI 的键盘加速键提示（窗口内注册了 Ctrl+1/Ctrl+2 导航），不是布局错误。

**BLOCKED**：125% / 150% / 200% 缩放需要修改系统缩放并注销重启，会中断用户当前桌面会话，本轮未执行；
200% 下的重排、字体截断、图标模糊都**没有证据**。异机、多显示器、高对比度主题同样 BLOCKED。

---

## 10. 多账号隔离

`--account-isolation`：**13 PASS / 0 FAIL**。两层防线分别验证：

- **任务归属**（`LegacyTaskMigrationInspector`）：UID 不一致 → `AccountUnverified` 且阻塞项写明双方 UID；
  旧任务缺 UID → 警告而非静默放行；未登录 → 拒绝；当前 UID 未知 → 拒绝推导个人空间；账号名大小写不同视为不同账号；
  同一份旧配置里分属两个账号的任务给出不同结论。
- **会话沙盒**（`AppSettings.EnsureIsolatedTestTask`）：源目录在沙盒外 → 拒绝；目标目录在沙盒外 → 拒绝；
  `..` 路径穿越 → 拒绝；沙盒内同账号任务 → 接受；两个隔离会话配置互不可见。

**BLOCKED**：真实 NAS 上两个账号的**端到端**写入隔离（A 账号的任务绝不能出现在 B 账号的个人空间）需要第二套
飞牛账号与真实设备，单机无法构造，本轮没有做，也不把单元测试当作已完成端到端验收。

---

## 11. 压力复测

`--stress-tests --files=3000`（本地 provider，非 NAS）：

```
文件数=3001  源大小=9.6MB
首次备份=22.5s  无变化=5.4s  增量=5.20s
增量上传=8B  峰值工作集=152MB  失败=0
```

无变化扫描 5.4s 对 3001 个文件属可接受区间；增量只上传 8 字节，说明内容寻址复用生效。**限制**：这是本地磁盘
provider，不代表真实 NAS 的网络与 API 延迟；8MB 单文件 × 3000 的规模未测。

---

## 12. 真实双版本升级与回滚（A 0.5.0 → B 0.6.0）

脚本：`tools/acceptance/ab-upgrade-acceptance.ps1`；产物：`.acceptance/ab-20261111-175437/`。
**27 项全部 PASS**，关键几条（其余见 `acceptance-result.json`）：

- 两个版本都用 `build-release.ps1` 真实 `dotnet publish` + 一次性 ECDSA 密钥签名，载荷分别声明
  `version/supported_data_format`（A=0.5.0/1，B=0.6.0/1）。
- 载荷内不含私钥、`credentials.dat`、`settings.json`。
- 独立更新器 `--apply` 装上 B：`APPLIED 0.6.0`，`previous.json` 记录 0.5.0，两个版本目录并存，
  **启动器字节未变**（`8FA5ED57BAD5`），保证下一次还能被修复。
- 新版本"可用"判定不是"窗口出现"：`.health/report-0.6.0.json` 记录 `services_initialized / task_manager_ok /
  nas_online / task_count / config_readable / format_compatible`，验收断言 `usable=True services=True tasks=True
  format_state=Valid`。
- 更新日志写入"来自已验证发布（sha 前缀）"与安装动作。
- 程序运行时 `--apply` → `exit=4`，**不杀进程**，指针不变。
- `current.json` 被写成垃圾 → `status`/`launch` 均 `exit=3`，不启动任何版本。
- 升级与回滚后，安装目录之外的用户数据（`%APPDATA%`、`%LOCALAPPDATA%`）逐文件哈希比对无变化。

---

## 13. 成熟度估计（基于本轮证据，非承诺）

| 维度 | 估计 | 依据 / 主要缺口 |
| --- | --- | --- |
| 核心备份能力 | **82%** | 备份/增量/复用/长文件名/恢复路径有大量回归与历史真机验证；缺异机恢复、NAS 断电、真实大文件规模 |
| 自动更新能力 | **78%** | P0-2/P0-3 已闭环，真实签名包端到端跑通；缺公网发布端点、代码签名、灰度、崩溃自动回滚 |
| 企业部署能力 | **55%** | 安装/升级/卸载/重装已实测；缺静默批量部署（GPO/SCCM）、端口/代理文档验证、集中策略 |
| 数据安全 | **80%** | 格式守卫、损坏保留、回滚兼容、账号绑定均有用例；缺真实双账号端到端、静态加密、密钥轮换 |
| 综合（可试点，不可生产） | **65%** | 全部自动化通过 + 三类脚本化端到端通过；关键人工项（异机、高 DPI、真实双账号）仍 BLOCKED |

不能因为 76 / 541 / 333 / 13 / 27 / 13 / 13 全部通过就认为可以替换用户的正式备份方案：这些数字证明的是
**已知缺陷不再复现**，不是"未知场景已覆盖"。

---

## 14. 复现命令

```powershell
$env:PATH = "D:\FNSyncPlus_Toolchain\dotnet;$env:PATH"
cd D:\FN_Sync_Plus_WB

dotnet build FNSyncPlus\FNSyncPlus.sln -c Release -p:Platform=x64        # 0 警告 0 错误
dotnet run --project FNSyncPlus\FNSyncPlus.Tests -c Release -- --p0-tests              # 76 / 0
dotnet run --project FNSyncPlus\FNSyncPlus.Tests -c Release -- --integrity-regressions # 541 / 0 / 0
dotnet run --project FNSyncPlus\FNSyncPlus.Tests -c Release                            # 333 / 0 / 3
dotnet run --project FNSyncPlus\FNSyncPlus.Tests -c Release -- --account-isolation     # 13 / 0
dotnet run --project FNSyncPlus\FNSyncPlus.Tests -c Release -- --stress-tests --files=3000

# 端到端（会真实 publish 两个版本并签名，输出到 .acceptance\）
powershell -File tools\acceptance\ab-upgrade-acceptance.ps1 -DotnetPath D:\FNSyncPlus_Toolchain\dotnet\dotnet.exe
powershell -File tools\acceptance\installer-acceptance.ps1 -PayloadA <A载荷> -PayloadB <B载荷>
powershell -File tools\acceptance\ui-visual-acceptance.ps1 -Payload <app-x.y.z 目录>
```

`.acceptance/` 已在 `.gitignore` 中：验收产物（截图、日志、安装包、一次性密钥）**不进版本库**，
只在需要复核时本地保留。

---

## 15. 提交、推送与 CI

本轮按修复内容分 5 次提交，全部推送到 PR #4（`workbuddy/enterprise-release-readiness` →
`codex/fix-backup-integrity`），**未合并 `main`，未创建正式 Release，未改动生产清单**：

| 提交 | 内容 |
| --- | --- |
| `c63d9ab` | P0-1 / P0-3 / P0-4 与更新器信任链、数据格式守卫（含 4 个测试套件） |
| `6a0c158` | P0-2 后台调度、启动健康报告、主题与标题版本号修复 |
| `6e3342b` | `build-release.ps1`、`FNSyncPlus.iss`、三套端到端验收工具、CI 步骤 |
| `0a754d4` | 本文档与 `acceptance-status.md` 更新 |
| `cbf40d3` | CI 触发条件补齐（PR 基分支是 `codex/**`，原来根本不会跑检查） |

CI：run [43142664651](https://github.com/w15038510619-prog/FN-Sync-Plus/actions/runs/43142664651) 结论 **success**，
本地与 CI 的完整性回归数字一致（541 / 0 / 0）。离线套件 CI 为 328 / 0 / 4 SKIP，本机为 333 / 0 / 3 SKIP，
差异来自 CI runner 缺少本机才有的真实 NAS 环境，属已知差异，不是回归。

> 推送过程中 GitHub 的 `github.com:443` 通道多次被网络代理以 `CONNECT tunnel failed 502` 拒绝，重试后才成功；
> 如果复现时拉不到远程，先确认代理而不是仓库状态。

---

## 16. 仍然 BLOCKED 的清单（不要当成已完成）

| 项 | 原因 | 需要什么才能解除 |
| --- | --- | --- |
| 异机恢复 | 只有一台电脑 | 第二台 Windows 机器 + 同一 NAS |
| 125/150/200% 缩放外观 | 需改系统缩放并注销 | 可牺牲的桌面会话 / 虚拟机 |
| 真实双账号端到端隔离 | 只有一个飞牛账号 | 第二套 NAS 账号 |
| 公网更新服务器 / 灰度 | 无发布端点与 CDN | 正式域名 + 发布流水线 |
| 更新器与安装包代码签名 | 无企业证书 | OV/EV 代码签名证书 |
| 安装包在中文路径 / 非管理员安装 | 需独立机器验证 | 干净虚拟机 |
| 更新失败后无人值守自动回滚（新版本崩溃场景） | 需要真实崩溃注入与重启编排 | 专用测试机 |
