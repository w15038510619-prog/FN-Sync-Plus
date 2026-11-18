# 部署指南（公司内部受控试点）

适用版本为 0.4.3 Windows x64 投运候选版。最终结论和安装包哈希见 `production-candidate-0.4.3.md`。每位用户使用自己的飞牛账号和独立备份目录。同一个备份目标只允许一个客户端写入。

## 1. 产物说明

| 产物 | 路径 | 说明 |
|---|---|---|
| 版本化安装目录 | `Release_WinUI\install\` | `app-<版本>\` + `FN_Sync_Plus.exe`（稳定启动器）+ `current.json` |
| 更新包 | `FN-Sync-Plus-<版本>-x64.zip` | 只有应用载荷，不含用户配置 |
| 更新清单 | `manifest.json` | 必须由发布私钥签名，客户端只接受签名正确的清单 |
| 安装程序 | `FN_Sync_Plus_<版本>_x64_setup.exe` | 由 `FNSyncPlus\installer\FNSyncPlus.iss` 编译，用户级安装 |

`FN_Sync_Plus.exe` 是**更新器兼启动器**，它读取 `current.json` 指向的版本目录并启动 `FNSyncPlus.WinUI.exe`。启动器本身永不随更新替换，因此新版本启动失败时还能回滚。

## 2. 构建发布包

```powershell
cd FNSyncPlus
# 1) 只跑安全回归并产出未签名候选（默认）
.\build-release.ps1 -Version 0.4.1 -Channel beta

# 2) 正式试点：签名清单（私钥在发布机上，绝不入库）
.\build-release.ps1 -Version 0.4.1 -Channel beta `
    -SignManifest `
    -SigningKeyPath D:\secure\fnsync-update-private.pem `
    -ManifestBaseUrl https://update.example.internal/fnsync
```

首次使用需生成密钥对（**只在发布机执行，私钥不入库、不进安装包**）：

```powershell
dotnet run --project ..\tools\FNSyncPlus.UpdateTool -- keygen `
    --private D:\secure\fnsync-update-private.pem `
    --public ..\FNSyncPlus\FNSyncPlus.Core\Updates\update-public.pem
```

把公钥放到 `FNSyncPlus.Core\Updates\update-public.pem` 后重新编译客户端，公钥会被嵌入程序集。**没有嵌入公钥的构建会主动停用自动更新**（fail-closed），不会退回到“不校验”。

## 3. 安装（普通同事）

1. 运行 `FN_Sync_Plus_<版本>_x64_setup.exe`。
2. 默认安装到 `%LOCALAPPDATA%\Programs\FNSyncPlus`，不需要管理员权限。
3. 自动创建开始菜单项；勾选时创建桌面快捷方式。
4. 首次启动后在「设置 → 软件更新」填内网更新地址，选择通道（试点用 Beta）。

升级前备份本人的 `%APPDATA%\FNSyncPlus` 和 `%LOCALAPPDATA%\FNSyncPlus`。配置备份含私人连接资料，应留在本人受控位置。从托盘选择“退出程序”并等安全退出后运行安装程序，沿用原安装目录。安装目录与用户配置分离。

## 4. 升级

- 程序在「设置 → 软件更新」中检查、下载并安装；安装由独立更新器在程序退出后执行。
- 备份任务运行中会**推迟**安装，不会中断正在提交的备份。
- 升级后只切换 `current.json` 指针，旧版本目录保留，可回滚。
- 手工升级使用完整安装程序，安装前核对交付目录中 `SHA256SUMS.txt` 的值：

```powershell
Get-FileHash .\FN-Sync-Plus-0.4.3-Windows-x64-Setup.exe -Algorithm SHA256
```

## 5. 卸载

- 通过「应用和功能」或开始菜单卸载项卸载。
- 卸载**不会**删除 `%LOCALAPPDATA%\FNSyncPlus` 中的配置和凭据，也**不会**触碰 NAS 上任何备份。
- 重新安装后可沿用保留的配置。排障期间保留完整配置目录。

## 备份与恢复

1. 在“NAS 设备”配置自己的飞牛地址，由本人登录，核对顶部连接状态。
2. 在“同步任务”添加本地文件夹，选择本人独立 NAS 目标目录。首次使用选择“仅手动同步”，核对路径后执行该任务的“同步”。检查任务状态、文件数、失败数及详情日志。
3. 完成恢复核验后，可按需要改为智能自动或定时同步。暂停只影响对应任务。网络或账号失效时先恢复连接，再检查任务状态。
4. 在“恢复文件”选择备份目录或其上一级目录，检索后选择任务。恢复最新提交内容时选“当前文件”，设置 `C:\Restore_Current`，点击“恢复当前备份”。
5. 恢复修改、重命名或删除前的单个文件时选“历史版本”，选中所需日期，设置 `C:\Restore_History`，点击“恢复所选历史版本”。
6. 在确认框核对版本、文件、备份位置、恢复目标和同名文件处理方式。默认跳过已存在文件。选择覆盖前应确认目标中的同名文件可以替换。
7. 完成后检查恢复数、SHA-256 校验通过数、跳过数及失败数。有失败时导出恢复报告，查看任务详情日志。当前恢复的重试要求原备份版本、原目标和原冲突处理方式一致。

支持中文路径。示例命令统一使用英文目录名，减少终端显示和复制问题。恢复报告为 UTF-8，Windows PowerShell 5.1 读取时使用 `Get-Content -Encoding UTF8`。长名称可悬停查看完整内容。

旧任务通过“查看旧任务”只读查看，继续使用新版备份时新建任务并选择独立目标。移除任务前核对确认框。

## 更新网络选项与诊断

已下载包在重新验签、核对版本和 SHA-256 后复用。下载界面显示大小、百分比、速度和处理阶段，准备阶段可取消。相同版本检查显示最新状态。

代理留空使用系统代理，也可填写本人选择的 HTTP(S) 代理。连接建立、清单检查和整包下载分别配置超时，默认下载上限 120 分钟。下载失败可重新下载。只有完整大小和 SHA-256 校验通过的文件才作为缓存使用。

更新日志在安装目录的 `update.log`，操作状态和健康报告在 `.ops` 和 `.health`。提交状态不明、锁未安全处理或备份无法安全结束时，先检查日志并解决原因。

## 数据保护与回滚

升级保留旧 `app-<版本>` 目录与 `previous.json`。健康检查失败时自动恢复上一版本。人工回滚前从托盘安全退出程序，再执行：

```powershell
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\FNSyncPlus'
& (Join-Path $installRoot 'FN_Sync_Plus.exe') --layout $installRoot --rollback
```

数据格式保护可能拒绝启动无法读取新配置的旧版本，此时保留完整配置和诊断记录，由维护人员处理迁移。不要清理 NAS 的 current、历史目录、清单、对象、发布记录或锁记录。排障时保留旧程序、previous.json 和用户配置。

此候选包尚无 Windows 发布者代码签名。更新清单 ECDSA 签名用于核验更新来源，两类签名分别验收。

## 6. 部署前检查清单

- [ ] 安全回归全绿（`--integrity-regressions`，0 FAIL）
- [ ] `manifest.json` 已签名，且 `verify` 通过
- [ ] 更新包 SHA-256 与清单一致
- [ ] 安装目录不含 `credentials.dat` / `settings.json` / `updates.json`
- [ ] 试点范围（受众/批次）已确认，未覆盖正式全员
- [ ] 回滚演练已完成（见 `update-and-rollback.md`）
