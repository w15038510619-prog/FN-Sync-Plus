# 飞牛同步增强版 (FN Sync Plus) v0.4.0-beta

> **Windows 10/11 64位 原生 WinUI 3 多文件夹 NAS 自动备份、长文件名兼容与异机恢复系统**  
> 专为飞牛 NAS（fnOS）用户解决 Windows 超长中文文件名 `上传-文件名过长 (failed_code: 1018)` 痛点与多目录自动同步需求。  
> ⚠️ **说明**：当前版本为公开测试预览版（**v0.4.0-beta**），严禁称为正式版。请在生产环境使用前通过内置测试实验室进行完整性验证。

---

## 🌟 v0.4.0-beta 稳定性整改与核心升级亮点

### 1. 彻底修复添加文件夹向导 (独立全窗口响应式页面 `AddFolderPage`)
- **废弃固定尺寸弹窗**：彻底废除此前因固定尺寸导致严重裁切和按钮消失的 `ContentDialog` 弹窗。
- **现代化页面结构**：
  - **顶部**：返回任务列表按钮 + 标题 + 3 步进度指示器（1. 本地源目录、2. NAS 目标位置、3. 自动规划与确认）。
  - **中间**：自适应 `ScrollViewer` 响应式内容区域，支持 1366×768、1920×1080 及 125%/150% DPI 缩放，内容随窗口自由拉伸，绝无裁切。
  - **底部**：固定操作栏（取消返回、上一步、下一步/完成创建），始终停靠吸底可见，彻底杜绝双重「取消」按钮。
- **真实选区交互与防虚假数据**：
  - 严禁自动添加或创建任何未请求的默认文件夹（如 `WorkDocs`），列表初始完全为空。
  - 未选择任何待备份文件夹时，「下一步」按钮严格置灰，并提供清晰明确的引导说明。
  - 支持多选、防重复添加与单项快速移除。

### 2. 全面清除生产功能中的全部伪造目录与演示数据
- 彻底移除 `NasBrowserViewModel` 中在空共享或查询失败时伪造的假共享（如 `cenco (个人空间)`、`团队文件-cenco-public`）与假子目录（如 `WorkDocs`、`Photos_Backup`、`Project_Archive` 等）。
- 真实目录为空时明确展示「此目录为空 (暂无子文件夹)」，若发生网络或认证异常则准确展示底层错误信息。

### 3. 全新集中式 `NasConnectionService` 与设备表单优化
- **单例连接状态管理**：全局统一维护 `Disconnected`、`Connecting`、`Connected`、`ConnectionLost`、`AuthenticationFailed` 状态。
- **断开立即联动**：点击「断开当前连接」后，立即释放 Windows 网络会话，顶部连接药丸毫秒级变为未连接状态，绝不依据是否保存了 IP 误判。
- **固定表单宽度防跳变**：`DevicePage` 登录表单各输入项采用固定 360px 宽度结构，错误提示置于下方自动换行，连接/断开状态变化时输入框绝对保持恒定宽度。

### 4. 原创应用图标设计与全尺寸 ICO 资产
- 设计三套原创方案（A: 现代立体存储仓、B: 极简盾牌安全标、C: 飞牛 Fluent 存储魔方），精选方案 C 深度融合「文件夹 + 单向流 + NAS 存储」。
- 自动生成 16x16、24x24、32x32、48x48、64x64、128x128、256x256 完整复合 ICO 资产与 PNG 资源，确保 16x16 任务栏下清晰可辨。

### 5. 内置完整的「备份测试实验室」 (Test Lab)
- **安全沙箱环境**：数据存放于 `%LOCALAPPDATA%\FNSyncPlus\TestLab\Source`，严禁污染用户个人目录。
- **严格极限边界测试集**：
  - 254 字节极限边界文件名（保持原名）
  - 255 字节极限边界文件名（保持原名）
  - 256 字节超长文件名（自动映射为 `_FN_LONG_*`）
  - 300 字节超长文件名（自动映射为 `_FN_LONG_*`）
  - 特殊字符、Emoji、三层超长嵌套目录、独立空目录与跨目录同名文件。
- **双运行模式**：支持「本地隔离环境模拟测试」与「真实飞牛 NAS SMB 网络端到端测试（自动在 NAS 创建并清理 `FN_TEST_<guid>` 隔离沙箱）」。
- **全量无损验证**：自动执行全量备份、无变更增量（0 传输）、单文件变更增量、异机原名恢复，并对所有恢复文件执行 SHA-256 逐字节强比对。
- **报告一键导出**：支持导出 Markdown 检验报告与 JSON 机器可读数据。

### 6. 飞牛 fnOS 账号官方「我的文件」原生个人空间协议打通与 PoC 验收通过
- **彻底告别伪造私人 SMB 共享**：全面转向飞牛原生安全登录与个人空间 API，用户只需输入 NAS 飞牛账号密码，无需折腾复杂的 Windows 局域网 SMB 权限与文件夹导出。
- **真实 NAS 端到端 C# 协议闭环**：
  1. 支持 `util.crypto.getRSAPub` + RSA-PKCS1 + AES-256-CBC 协商加密与会话登录。
  2. 动态查询权威 UID 与真实个人空间基准（如 `vol1/1006`），无需猜测。
  3. `file.ls(path=null)` 原生直接列举当前用户的「我的文件」根目录。
  4. `file.mkdir` 在「我的文件」下建立专属测试与任务目录，飞牛 Web 文件管理器即时可见。
  5. `file.checkUpload` + HTTP `POST /upload`（HMAC-SHA256 签名）流式上传文件。
  6. HTTP `GET /multiple-download`（Cookie 认证）流式下载未压缩原字节。
  7. **SHA-256 强比对 100% 一致**，0 字节损坏，零误删用户既有文件。
- **统一存储抽象层 `INasStorageProvider`**：
  - **`FnOsPersonalStorageProvider`**：默认及首选提供程序，直连官方「我的文件」。
  - **`SmbStorageProvider`**：作为高级/兼容备选方案。
  - 详见技术验证报告：[`docs/fnos_personal_space_technical_verification.md`](docs/fnos_personal_space_technical_verification.md)。

---

## 一、交付文件清单

1. **`FNSyncPlus.WinUI`**：微软原生 WinUI 3 + Windows App SDK 现代桌面应用源码。
2. **`FNSyncPlus.Core`**：底层高性能同步引擎、长文件名映射、安全隔离清单、DPAPI 凭据安全存储与网络探测服务。
3. **`FNSyncPlus.Tests`**：核心自动化回归测试套件。
4. **`Release_WinUI\`**：已编译发布的 WinUI 3 独立绿色自包含运行目录，内含 `FNSyncPlus.WinUI.exe`。
5. **`FN_Sync_Plus_v0.4.0-beta_WinUI3_x64.zip`**：开箱即用独立分发包。
6. **`使用说明书.md`**：完整使用手册、架构设计与测试报告。
7. **`启动飞牛同步增强版_v0.4.0-beta.bat`**：一键快捷启动脚本。

---

## 二、自动化测试套件

系统内置自动化回归测试套件 `FNSyncPlus.Tests`，共涵盖 **38 个测试模块、265 项核心断言**：
- 单元测试：145 项全部通过（0 失败）
- 本地模拟备份与恢复：115 项全部通过（0 失败）
- GUI 自动化状态机流转：5 项全部通过（0 失败）
- 真实 NAS SMB 与个人空间线上测试：在未配置受控环境变量时严格执行安全跳过（2 项），杜绝在 CI 或非受控环境下操作用户真实数据。
- 飞牛官方「我的文件」原生协议独立 PoC (`--poc`)：在真实 NAS (`192.168.8.120:5667`) 上 100% 验收通过！

执行测试套件命令：
```powershell
# 运行全量回归测试套件
dotnet run --project FNSyncPlus/FNSyncPlus.Tests/FNSyncPlus.Tests.csproj

# 运行真实 NAS 飞牛官方「我的文件」端到端验收 PoC
dotnet run --project FNSyncPlus/FNSyncPlus.Tests/FNSyncPlus.Tests.csproj --poc
```
