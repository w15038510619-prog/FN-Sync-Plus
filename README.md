# 飞牛同步增强版（FN Sync Plus）v0.4.0-beta

面向 Windows x64 的飞牛 NAS 备份工具，目标是备份中文及超长文件名，并在恢复时保留原名和校验文件内容。

**当前分支仍在验收，不要用于正式文件。** 默认启动使用独立的新版版本化备份试验配置；旧任务配置保留供查看，不会自动迁移或运行。旧备份引擎仍有历史内容安全风险，仅保留显式诊断入口。通过隔离样本测试或构建出 ZIP，不代表软件已经可以安全替代现有备份。

## 当前验证情况

- 在用户授权的飞牛测试沙盒中，通过 WinUI 界面验证了文件备份、修改、改名、历史版本恢复、自动监听、一次定时检查和正常重启后的补扫。
- 验证了 UTF-8 长度为 300 字节的文件名可以恢复为原名，并对恢复内容进行 SHA-256 校验。超长名称在 NAS 上可能使用映射名，恢复原名需要通过软件的清单恢复流程。
- 通过隔离界面测试了复用已有目录：内容一致的文件在 NAS 内复制到独立备份，缺失或不同的文件从电脑上传。旧目录保留，但新副本及历史会占用额外空间，并产生校验所需的网络读取。新版默认入口仍属于试验，尚未作为正式版本交付。
- 新版试验配置中的镜像备份只允许手动运行，必须明确勾选授权；本地消失的受管理文件从当前目录移入历史，旧目录保留。已用隔离数据验证历史恢复，新版默认配置的镜像界面尚未实际验收。
- 已验证托盘关闭、恢复窗口，以及新版的单实例保护。较早的 WinUI 版本可能不参与该保护，升级前仍需正常退出旧程序。
- 暂无第二台 Windows 电脑的实际恢复结果；真实断网、NAS 断电及跨设备并发安全尚未完成验收。应用层模拟上传中断和专用测试进程中断不能替代这些测试。
- 自动更新引擎已通过 30 项隔离测试（本地回环 HTTPS）与真实二进制的端到端安装/回滚验证；但**没有正式代码签名证书、没有公司内网更新服务器**，因此未开启公司范围的无人值守更新。

完整证据、失败记录、测试范围和未完成事项见 [代码审计与验收记录](docs/codex-integrity-audit.md)。所有真实飞牛测试均应限定在用户明确授权的测试目录；不要删除用户现有 NAS 数据。

## 项目结构

- `FNSyncPlus/FNSyncPlus.WinUI`：WinUI 3 桌面界面。
- `FNSyncPlus/FNSyncPlus.Core`：备份、恢复、清单、调度、更新与存储提供程序。
- `FNSyncPlus/FNSyncPlus.Updater`：独立更新器兼稳定启动器（`--apply` / `--rollback` / `--status`）。
- `FNSyncPlus/FNSyncPlus.Tests`：隔离回归及其他测试。
- `FNSyncPlus/FNSyncPlus.App`：保留的旧版 WPF 界面。
- `tools/FNSyncPlus.UpdateTool`：离线生成签名密钥、签名与校验更新清单。

## 自动更新与安装

- 更新链路：签名清单 → HTTPS 下载 → SHA-256 校验 → 解压加固 → 版本目录 → 指针切换 → 启动验证 → 失败回滚。
- 备份进行中会推迟安装，不会中断正在提交的备份。
- 私钥只在发布机；客户端内置公钥，未内置则自动更新停用。
- 部署、升级、回滚见 [部署指南](docs/deployment-guide.md) 与 [更新与回滚](docs/update-and-rollback.md)。
- 带签名清单的发布包用 `FNSyncPlus\build-release.ps1 -SignManifest -SigningKeyPath … -ManifestBaseUrl …` 生成。

## 构建和测试

需要 Windows x64、.NET 8 SDK 及构建项目所需的 Windows SDK。运行隔离安全回归：

```powershell
dotnet run --project FNSyncPlus/FNSyncPlus.Tests/FNSyncPlus.Tests.csproj -c Release -- --integrity-regressions
```

生成 **WinUI x64 测试候选 ZIP**：

```powershell
.\FNSyncPlus\build-winui.ps1
```

脚本会先运行隔离安全回归，再生成自包含候选包，并检查关键文件哈希及是否意外包含配置或凭据。如果 SDK 不在 PATH 中，可通过 `-Dotnet` 指定 `dotnet.exe` 的完整路径。输出位于 `Release_WinUI` 下的唯一候选目录和 ZIP，不会自动发布。旧的 `FNSyncPlus/build.bat` 保留用于旧版 WPF，不能作为 WinUI 打包入口。

仅供开发诊断的 `--legacy-engine` 参数会选择旧引擎并可能运行旧任务；它存在已知的数据安全风险，不应在真实数据上使用。

真实 NAS 测试需要明确授权、隔离凭据和限定范围，不由上述构建脚本执行。不要把未执行的真实设备或异机测试计作通过。
