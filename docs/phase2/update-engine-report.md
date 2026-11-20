# 第二阶段：企业自动更新 — 实现与验证记录

日期：2026-11-11　分支：`workbuddy/enterprise-release-readiness`

## 1. 交付内容

| 组件 | 文件 | 说明 |
|---|---|---|
| 更新器（稳定启动器） | `FNSyncPlus/FNSyncPlus.Updater` | 单文件自包含，读 `current.json` 启动、`--apply`、`--rollback`、`--status` |
| 清单验证 | `Updates/UpdateManifestVerifier.cs` | ECDSA P-256，规范 JSON 签名，通道/批次/受众/降级策略 |
| 清单签名 | `Updates/UpdateManifestSigner.cs` | 与验证端共用规范序列化 |
| 发布工具 | `tools/FNSyncPlus.UpdateTool` | `keygen` / `sign` / `verify` / `inspect` |
| 下载 | `Updates/UpdateDownloader.cs` | HTTPS Range 断点续传、超长/短包拒绝、哈希不符即删 |
| 安装编排 | `Updates/UpdateInstaller.cs` | 二次哈希、解压、版本目录、指针切换、日志 |
| 解压加固 | `Updates/UpdatePackageExtractor.cs` | 路径穿越/盘符/链接/条目数/体积上限 |
| 启动健康 | `Updates/LaunchHealthTracker.cs` | 按版本记录尝试与可用标记，过期标记不算数 |
| 数据格式守卫 | `Updates/DataFormatGuard.cs` | 旧程序拒绝写更高格式配置 |
| 备份活动闸门 | `Updates/BackupActivityGate.cs` | 阻新任务 → 等当前结束 → 复查不确定状态 → 保存配置 |
| 维护暂停 | `TaskManager.EnterMaintenanceHold` | 队列不再取新任务，运行中任务不被中断 |
| 设置界面 | `Pages/SettingsPage.xaml` | 更新地址、通道、方式、进度、检查/下载/安装 |
| 构建 | `FNSyncPlus/build-release.ps1` | 版本化安装目录 + 载荷 ZIP + 可选签名清单 |

## 2. 自动化验证（本地 Windows）

`dotnet run --project FNSyncPlus/FNSyncPlus.Tests -c Release -- --update-tests`
**30 PASS / 0 FAIL**，使用 127.0.0.1 回环 HTTPS 服务与一次性密钥，覆盖：

签名被接受、版本号被篡改、包哈希被篡改、缺签名、降级/重复安装、Stable 拒收 Beta、Beta 接受 Beta、
非 HTTPS、批次未覆盖、受众不匹配、客户端过旧、下载成功、包被篡改后删除临时文件、断点续传发送 Range、
超长响应中止、解压 `../`、解压盘符路径、缺主程序、正常安装切指针、启动验证失败回滚、
哈希不符拒绝安装、同版本目录拒绝覆盖、格式更高禁止降级、格式守卫保留标记、
备份中推迟安装、不确定状态拒绝安装、等待超时、过期健康标记不算数、未配置地址报错、检查发现新版本。

## 3. 端到端手工验证（真实二进制，非模拟）

在 `D:\FN_SyncPlus_ReleaseBuild` 下用真实发布产物完成：

1. `FN_Sync_Plus.exe --layout ... status` → `{"current":"0.4.0","previous":null}` PASS
2. 用 `app-0.4.0` 打包 `FN-Sync-Plus-0.4.1-x64.zip`（141,843,212 字节，SHA-256 `F74C888F…860EB`）
3. `--apply --version 0.4.1 --sha256 <hash> --no-launch` → `APPLIED 0.4.1`，exit 0
4. `current.json` → 0.4.1，`previous.json` → 0.4.0，两个版本目录并存
5. `update.log` 记录 `applying → installed → apply ok`
6. 启动验证通过：`.health\0.4.1.healthy.json` 由新版本自身写入

这说明「启动器 → 健康标记 → 指针切换 → 回滚信息」链路在真实进程上可用。

## 4. 未完成 / BLOCKED

| 项 | 状态 | 原因 |
|---|---|---|
| 正式代码签名证书 | BLOCKED | 无证书 |
| 公司内网更新服务器部署 | BLOCKED | 无服务器与授权 |
| 无人值守面向全公司开启 | BLOCKED | 未完成第 4 阶段验收 |
| `build-release.ps1` / `installer` 由本人执行 | BLOCKED | 本环境 PowerShell 工具被安全策略禁用、未安装 Inno Setup；**脚本已写，等价步骤已用 dotnet CLI 手工执行并验证** |
| 更新失败的 WinUI 界面目视确认 | BLOCKED | 无法在本会话安全地做 GUI 截图 |

## 5. 安全边界

- 私钥不入库、不入包；公钥缺失时更新整体停用；
- 安装永不强杀备份；不确定状态一律拒绝安装；
- 回滚受数据格式守卫约束，避免旧程序写坏新格式；
- 更新器是独立进程，主程序退出后才替换文件。
