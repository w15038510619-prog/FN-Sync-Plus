# FN Sync Plus 0.4.4 交付与验收

2026-11-18。结论：**READY FOR PILOT**。产品版本和官方更新通道已为 0.4.4 Stable；此名称不代替投运条件的实际验收。真实个人文件、团队文件、WPS 保存后保持打开、安装升级及公网 HTTPS 更新已经执行。未具备的 Windows 代码签名及物理 DPI 条件见文末。

应用和安装包的源码为 `57e762e41af0a4b61d89dadbe85a601ccb7f8b4f`。后续 `11e42925a877b915ded2613b33278cbe2650882f` 仅增加发布附属文件的校验与原子发布能力，不改变交付程序。工作分支仍为 `workbuddy/enterprise-release-readiness`，PR #4 保持 Draft，目标仍为 `codex/fix-backup-integrity`，没有合并 main 或创建 GitHub 正式 Release。

## 实际修复

| 问题 | 修复位置与行为 | 结果及边界 |
| --- | --- | --- |
| 已连接仍显示红色故障 | `DeviceViewModel` 将连接状态与独立的搜索状态、异步代次分开；`NasConnectionService` 维持唯一连接来源 | 已连接显示绿色，后台搜索不覆盖登录状态；设备页真实运行检查通过 |
| 团队文件缺失、分类过多 | `NasConnectionService.Team.cs`、`SmbAuthService`、`NasBrowserViewModel` 只列出 NAS 公布且当前身份可读取的 `团队文件-<名称>` 根；不把所有可读 SMB 映射归类为团队文件 | 实际服务器只有 `cenco-public`，窗口已核对只剩“我的文件”及该团队入口 |
| 团队备份与恢复 | `TeamSmbStorageProvider` 支持独占创建、不可覆盖目录发布；`TaskManager` 和 `RestoreEngine` 统一完整 UNC 与共享内相对路径，校验设备、账号、UID | 真实普通任务入口首次及增量、当前及历史 SHA-256 恢复通过；单目标单写入者，未开发共享写入模式 |
| 打开的 Excel/WPS | `StableFileSnapshot` 共享读取、分块私有副本、前后源状态与二次哈希；`ImmutableContentStore` 上传同一副本；精确排除 Office owner 文件 | 真实 WPS 保存 V1、V2 后始终保持打开，两次备份及两个版本恢复哈希通过；没有安装 Excel，未将 WPS 证据称为 Excel 实测 |
| 恢复操作不直观 | `RestoreViewModel`、`RestorePage` 自动列出现有备份、浏览其他位置、最新/历史区分、目录导航、搜索、多选、日期筛选 | 默认新恢复目录、跳过同名；确认对话框绑定具体版本/选项/目标；内部对象地址折叠。最小窗口列表与恢复按钮实际测量可见 |
| UI-01 图标 | 沿用用户指定图标；保持 EXE、稳定启动器、窗口、托盘和安装快捷方式一致，显式 AppUserModelID | 安装快捷方式的目标、图标与身份检查通过，100% DPI 实际任务栏与窗口沿用已确认截图。既有固定快捷方式缓存仍需目标机器核对 |
| UI-02 旧版本号、托盘仍为 0.4.0-beta | `TrayIcon` 接收与窗口同一程序集版本来源；清除正式启动标题与崩溃日志的旧版本常量 | 实际 NotifyIcon 提示与窗口标题均为 0.4.4；普通界面不含测试导航 |
| UI-03 同版本被当成错误 | 沿用 0.4.3 的完整验签后同版本正常提示；`UpdateManifestVerifier` 修复 Stable 被误标为 Beta 的分类 | 重新检查当前版本不安装；较低版本、错误签名仍拒绝 |
| UI-04 长更新地址 | 更新源、代理和通道移至高级选项，保留整行查看/复制 | 新用户不用输入地址；100% DPI 页面通过 |
| UI-05 任务卡片与布局 | 保留独立按钮列、单行省略、完整 Tooltip；恢复页改为有界列表和固定动作区 | 深浅主题 1200×780、1000×640 通过。没有将窗口缩小当作真实高 DPI |
| UI-06 旧任务中文 | 沿用严格 UTF-8/BOM 解析及只读保护；非法内容明确拒绝 | 原始已损坏配置样本缺失，原例复现 BLOCKED；不猜测改写旧任务 |
| UI-07 长中文、历史名称 | 展示层解码，原始恢复路径独立保留；长名称换行、选择、复制和 Tooltip | 中文及转义样本实际渲染；未修改无损长名称映射算法 |
| UI-08 历史恢复误操作 | 当前和历史标签只出现对应恢复动作，执行前核对内容、版本、目标和同名处理 | 历史列表两行样本和页脚实际可见；当前/历史恢复专项通过 |
| 深色标题栏看不见 | `WindowHelper.ApplyCaptionTheme` 同步活动/非活动、普通/悬停/按下的系统按钮前景和背景；切换主题立即应用 | 深色活动白色、非活动 #B0B0B0；浅色黑色。真实截图和原生 AppWindow 颜色断言通过 |
| UPDATE-01 重复下载 | 沿用共用缓存入口，使用前重新验签并核对版本、大小、SHA-256；损坏才重下 | 更新缓存专项通过 |
| UPDATE-02 超时/代理 | 沿用连接、清单、下载独立超时、用户代理策略、异步流式进度及取消；不启用不完整的续传 | 33 项更新专项；不关闭 TLS，不偷偷绕过代理。公开 HTTPS 验收沿用用户保存的 NO_PROXY 策略 |
| UPDATE-03 可复现发布 | `Build-StableRelease.ps1`、`build-release.ps1`、`UpdateTool verify-key`；CentOS 发布器先 ZIP 后签名清单，附属文件不可覆盖 | 修复增量构建遗漏公钥，以及 PowerShell 加载 DLL 导致 ZIP 失败的问题；实际公网发布与独立验签通过 |
| UPDATE-04 无安装反馈 | 保留检查、缓存校验、下载、验证、等待任务、安全退出、重启及失败状态 | 维护租约、真实进程交接和健康检查通过 |
| DIAG-01 日志/终端 | UTF-8 读写、ASCII 示例路径，普通提示与技术日志分开，继承原日志轮转和退出保护 | 签名/TLS 拒绝及日志专项沿用；早期偶发 TLS 根因未新增可复现证据，不宣称已经定位 |

## 配置、权限及数据保护

0.4.4 沿用 `%LOCALAPPDATA%\FNSyncPlus\versioned-beta`，没有删除或重建用户配置。安装验收复制现有的两条任务配置到私有测试根，禁止这些任务运行，真实启动旧/新载荷分别确认可读；文件逐字节哈希一致。DPAPI 使用合成夹具验证文件保留及可解密，没有把用户真实凭据复制到交付包。用户正在运行的 0.4.3 实例未被升级或结束。

账号切换和断开连接在任务写入时要求先暂停或结束；团队 SMB 冲突凭据安全拒绝，不强制断开其他 Windows 网络连接。权限由 fnOS/SMB 服务端决定，软件不拼接内部团队路径或扩大权限。用户确认本次所有可用账号均可读写 `cenco-public`，因此真实只读/无团队权限账号专项为 **SKIP（本环境无此权限角色）**，没有标记 PASS。自动化拒绝/隔离证据单独记录。

新增团队任务后，0.4.3 不支持这些任务，会安全拒绝配置。不要把安装健康检查期间的自动回滚，解释为新增团队任务后仍能无条件手工降级。保留旧版本、配置、`.ops`、`previous.json`、日志和 NAS 历史。

真实 NAS 测试均使用独立的 `Release044_<GUID>` 子目录。个人与团队测试目录已逐个按测试源和清单归属核对后清理；`cenco-public` 原有内容和 `tmp` 没有删除。程序不会自动清理历史对象。

## 实际测试结果

证据根：`D:\FN_Sync_Plus_RC_20261118`。各套件存在重叠断言，不相加为独立总数。

| 测试 | 最终结果 | 证据 |
| --- | --- | --- |
| 核心完整性 | 554 PASS / 0 FAIL / 0 SKIP | `044-regressions/integrity-regressions-recheck.log`、`044-signed-final-build.log`；此后 Core 未变 |
| 默认功能 | 342 PASS / 0 FAIL / 3 SKIP | `044-regressions/default.log`；旧真实 NAS 入口与样本条件跳过，另做本轮真实专项 |
| P0 | 86 PASS / 0 FAIL | `044-regressions/p0-tests.log` |
| 更新引擎 | 33 PASS / 0 FAIL | `044-regressions/update-tests.log` |
| 账号隔离 | 13 PASS / 0 FAIL | `044-regressions/account-isolation-final.log`；单机隔离，不称作真实双账号 |
| 存储隔离 | 24 PASS / 0 FAIL | `044-regressions/storage-isolation-tests-recheck.log` |
| 签名链 | 11 PASS / 0 FAIL | `044-regressions/signature-audit-tests.log` |
| 独立恢复 | 15 PASS / 0 FAIL | `044-regressions/independent-recovery-tests.log`；本机独立进程 |
| 交接及看门狗 | 27 PASS / 0 FAIL | `044-regressions/handoff-process-tests.log` |
| UI 会话隔离 / 容量 | 3 / 10 PASS，均 0 FAIL | 对应 `ui-isolation-tests.log`、`capacity-tests.log` |
| 本轮快照、团队及 Stable 专项 | 12 PASS / 0 FAIL | `044-local-release-gates.log` |
| 真实个人 NAS 首次/增量/当前/历史 | PASS | `044-real-nas-3/result.json` 中个人项目全通过；同文件的早期团队夹具失败保留，见下文 |
| 真实团队 NAS、WPS、正常任务入口、完整 UNC 恢复及清理 | 10 PASS / 0 FAIL | `044-real-nas-6/result.json`；另 `044-readonly-fixture` 11 项通过后由 `cleanup.json` 证明清理 |
| 真实安装/升级/回滚/卸载/重装 | 22 PASS / 0 FAIL | `044-installer-acceptance/installer-result.json`；独立 AppId 和数据根，未覆盖用户安装身份 |
| 安装后的真实 WinUI | 89 PASS / 0 FAIL | `044-installer-acceptance/installed-ui/ui-acceptance-result.json`；100%/96 DPI，深浅主题、最小窗口和原生标题栏颜色 |
| Beta HTTPS 及故障回滚 | 43 PASS / 0 FAIL | `044-https-ci-beta/acceptance-result.json` |
| Stable HTTPS 及故障回滚 | 43 PASS / 0 FAIL | `044-https-ci-stable/acceptance-result.json` |
| 原子发布器 | 9 PASS / 0 FAIL | `044-publisher-tests.log`；篡改 ZIP/清单/安装器拒绝，先前清单保留 |
| 最终包审计 | PASS，805 条目，禁止项 0 | `044-package-audit.json`；内嵌密钥及签名通过，安装器 Authenticode=NotSigned |
| 实际服务器 Stable 发布 | PASS | `044-published-stable/publication-result.json`；完整重新下载、大小、哈希、独立签名及私有暂存隔离 |
| 实际服务器 0.4.3→0.4.4 升级 | 11 PASS / 0 FAIL | `044-public-update-stable/result.json`；真实 HTTPS、更新器、服务/任务/数据格式健康、旧版本与配置保留 |
| 旧 Beta 源迁移真实升级 | 11 PASS / 0 FAIL | `044-public-update-beta/result.json`；旧客户端无需改地址，发现、下载、交接及 0.4.4 健康启动 |

初次完整性测试 553 PASS/1 FAIL 为测试断言仍匹配旧的开发标签，修正后 554 通过。一次以 `dotnet DLL` 启动隔离套件无法正确拉起子进程，改为真实 apphost 后 24 通过。早期真实团队夹具试图覆盖 WPS 已占用的 owner 文件而失败，修正夹具后真实链路通过。增量构建遗漏嵌入公钥及 PS5 文件句柄阻止压缩均已复现、整改并重建，不使用失败产物交付。原始失败日志保留，未改写成 PASS。

完整双电脑并发按用户取消强制要求记为 SKIP；既有第二台真实异机恢复、0.4.1→0.4.2 在线升级和配置保留复用 `two-computer-acceptance-status-20261118.md` 的真实记录与用户确认，没有重复伪造新证据。

## 包、哈希及服务器

唯一最终交付目录：`D:\FN_Sync_Plus_044_Release_20261118\delivery`。其他含 Final、Delivery 等名字的中间目录不是本次发布包。

| 文件 | SHA-256 |
| --- | --- |
| `FN-Sync-Plus-0.4.4-Windows-x64-Setup.exe` | `f5dbdba60a0c2451b5990a29520aef6ef03a5e5780a7b991733a368385d3aa9b` |
| `FN-Sync-Plus-0.4.4-x64.zip` | `c2d41d084dec87589a630f4e32556db3bea82471d23d57853cfe1f941de8e57a` |
| Stable `manifest.json` | `f9c857766aaa63dfc727ee03c1d1afa53870f51a5605de69bfc526b6aa8608ba` |

应用 ZIP 大小 136224926 字节，只含应用载荷。安装器、验收程序、私钥、用户配置或凭据没有塞进更新 ZIP。签名私钥留在受控 Windows 发布机上、仓库及交付目录之外，不上传服务器、Git 或交付包。公钥内嵌客户端；ECDSA P-256 更新签名不等同 Windows Authenticode。

已核对的实际地址：

- [安装包](https://dev.sk-fj.com:8081/fnsync/stable/FN-Sync-Plus-0.4.4-Windows-x64-Setup.exe)
- [应用更新 ZIP](https://dev.sk-fj.com:8081/fnsync/stable/FN-Sync-Plus-0.4.4-x64.zip)
- [Stable 签名清单](https://dev.sk-fj.com:8081/fnsync/stable/manifest.json)
- [SHA-256](https://dev.sk-fj.com:8081/fnsync/stable/SHA256SUMS-0.4.4.txt)
- [版本说明](https://dev.sk-fj.com:8081/fnsync/stable/release-notes-0.4.4.md)、[回滚说明](https://dev.sk-fj.com:8081/fnsync/stable/rollback-0.4.4.md)、[构建来源](https://dev.sk-fj.com:8081/fnsync/stable/build-provenance-0.4.4.json)

发布没有重装或覆盖 Nginx。Stable 文件先上传 root/700 私有暂存，再校验并原子公开，HTTPS 完整下载相符后才原子启用签名清单。`.beta-staging` HTTP 不可读取；0.4.2 和 0.4.3 ZIP 的原哈希仍相同。服务器不保存签名私钥。

Stable 真实升级通过后，另行签署并启用官方 Beta 源的 0.4.4 迁移清单。Beta URL 下的 ZIP 与 Stable ZIP 哈希完全相同，清单按 Beta 身份独立签名，原 0.4.3 清单归档保留。`044-published-beta/publication-result.json` 记录 HTTPS、签名及旧包保护通过。0.4.3 用户点击检查更新即可发现新版，0.4.4 对精确匹配的官方 Beta 地址提示迁移到 Stable；自定义地址不变。

## 更新失败与 CI

[应用源码构建](https://github.com/w15038510619-prog/FN-Sync-Plus/actions/runs/44272724955)和[Beta/Stable HTTPS CI](https://github.com/w15038510619-prog/FN-Sync-Plus/actions/runs/44272724776)均成功。发布工具提交的[构建](https://github.com/w15038510619-prog/FN-Sync-Plus/actions/runs/44274514998)和[HTTPS CI](https://github.com/w15038510619-prog/FN-Sync-Plus/actions/runs/44274514883)也均成功。

Stable 故障用例将缺少 Core 的 0.4.5 测试载荷置于隔离回环服务，签名合法但启动失败。操作 `3fcce15e236447e88e5752b62b9bfc9d` 在 2026-11-18 09:10:52 UTC 回滚至 0.4.4，状态 RolledBack，故障目录保留，旧版 PID 10192 启动并健康。这个故障包从未上传正式服务器。

本次实际服务器升级操作 `b50c551f6aa9441b92cc619298ad7345`：旧进程 8848 安全退出，更新器 5836 安装 0.4.4，新版 PID 17796 于 17:26:58 本地时间健康。操作、指针和日志对应；原有两个客户端进程与生产配置未变。

Beta 迁移验证操作 `592ab5ca537a46238d03de990c44d87a`：旧进程 29296、更新器 38200、新版 3964，于 17:31:02 本地时间完成健康检查。同版本不重复提示另由上述 Stable/Beta CI 的真实进程用例验证，没有再要求操作人员重复执行。

本轮功能提交依次为 `1c59090`（团队与稳定副本）、`deee2aa`（连接、恢复及托盘）、`41d941e`（Stable 发布）、`e121a37`（嵌入公钥和通道）、`ae0b3c5`（无句柄泄漏验签及恢复布局）、`3b5d49a`（随安装器交付新说明）、`57e762e`（深色原生标题栏）、`11e4292`（附属文件原子发布）。当前文档和验收脚本的提交不改变最终应用二进制。

构建及日常操作见 [0.4.4 操作说明](stable-0.4.4-operations.md)。`Build-StableRelease.ps1` 默认完整回归；本次最终重打包复用已完成的 554 项完整性证据（Core 未再变动），只增加 UI 标题栏修复并重新运行安装后 89 项界面验收。

## 尚未满足的正式投运条件

- **BLOCKED：Windows 发布者代码签名。**没有可用证书，安装程序实际 NotSigned。提供证书后才可签署，不能用清单签名冒充。
- **BLOCKED：125%、150%、200% 物理 Windows DPI 及既有固定任务栏快捷方式升级后的视觉确认。**当前实际系统为 100%/96 DPI；只能由能操作该显示设置的现场环境补充，不需要重跑备份/恢复。
- **BLOCKED：原旧任务乱码样本复现。**合法中文/转义样本已验证，已损坏原文不能猜测修复；保留旧配置，出现时提供该原始样本定位。
- **SKIP：真实只读/无团队权限账号及完整双电脑测试。**按当前可用权限角色和用户明确调整的范围记录，未执行项目不算通过。

当前没有已知未解决的数据正确性或更新安全性 FAIL，可供限定用户、独立目标稳定试用。仍不宣称所有正式分发外部条件已满足，也不应将本程序作为唯一备份。
