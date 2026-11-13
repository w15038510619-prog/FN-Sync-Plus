# 部署指南（公司内部受控试点）

> 适用版本：0.4.x 受控试点候选版。**未完成第 4 阶段验收前，不得把它作为重要资料或唯一备份。**

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

用户配置与凭据保存在 `%LOCALAPPDATA%\FNSyncPlus`，与程序目录分离。升级不会覆盖配置。

## 4. 升级

- 程序在「设置 → 软件更新」中检查、下载并安装；安装由独立更新器在程序退出后执行。
- 备份任务运行中会**推迟**安装，不会中断正在提交的备份。
- 升级后只切换 `current.json` 指针，旧版本目录保留，可回滚。
- 手工升级等价于把新包交给更新器：

```powershell
& "$env:LOCALAPPDATA\Programs\FNSyncPlus\FN_Sync_Plus.exe" --layout "$env:LOCALAPPDATA\Programs\FNSyncPlus" `
  --apply --package .\FN-Sync-Plus-0.4.1-x64.zip --version 0.4.1 --sha256 <清单中的SHA256>
```

## 5. 卸载

- 通过「应用和功能」或开始菜单卸载项卸载。
- 卸载**不会**删除 `%LOCALAPPDATA%\FNSyncPlus` 中的配置和凭据，也**不会**触碰 NAS 上任何备份。
- 需要彻底清除本机痕迹时，手动删除该目录。

## 6. 部署前检查清单

- [ ] 安全回归全绿（`--integrity-regressions`，0 FAIL）
- [ ] `manifest.json` 已签名，且 `verify` 通过
- [ ] 更新包 SHA-256 与清单一致
- [ ] 安装目录不含 `credentials.dat` / `settings.json` / `updates.json`
- [ ] 试点范围（受众/批次）已确认，未覆盖正式全员
- [ ] 回滚演练已完成（见 `update-and-rollback.md`）
