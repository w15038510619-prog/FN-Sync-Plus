# 自动更新与回滚说明

## 1. 信任链

```
发布私钥（发布机，离线）
   └─ 签名 manifest.json（ECDSA P-256 / SHA-256，覆盖除 signature 外全部字段，按字段名排序规范序列化）
        └─ manifest.sha256 指定更新包哈希
             └─ 更新器二次校验包 SHA-256，再解压安装
客户端内置公钥（编译期嵌入 FNSyncPlus.Core）
```

任一步失败都会停止：签名错、包哈希不符、解压越界、缺少主程序、启动验证失败。

## 2. 清单字段与策略

| 字段 | 作用 |
|---|---|
| `channel` | `stable` / `beta`。Stable 客户端永不接收 beta 清单；Beta 可接收两者 |
| `version` / `min_version` | 必须严格大于当前版本，否则按降级拒绝 |
| `min_compatible_client` | 客户端过旧时要求手工安装，不做跨代自动升级 |
| `batch` / `audience` | 分批灰度：客户端 `ReleaseBatch` 小于清单 `batch` 时不升级；`audience` 不匹配也不升级 |
| `url` | 必须 HTTPS |
| `size` / `sha256` | 下载后逐字节校验，不符即删除 |

## 3. 安装时序（关键安全点）

1. 用户在「设置 → 软件更新」点击「安装并重启」。
2. `UpdateService.PrepareInstallAsync`：
   - 进入维护状态（`TaskManager.EnterMaintenanceHold`），**不再启动新的备份任务**；
   - 等待正在运行的备份自然结束，最多等待预算时间；
   - 复查是否存在结果不明确的提交或未释放的锁；有则**拒绝**安装；
   - 保存配置。
3. 启动 `FNSyncPlus.Updater.exe --apply ... --wait-pid <当前进程>`。
4. 主程序退出；更新器等待该 PID 真正退出（超时则放弃安装，不会强杀）。
5. 更新器解压到 `.update-work\extract-*`，安装到 `app-<新版本>\`，写 `current.json`，旧指针存入 `previous.json`，全部记入 `update.log`。
6. 启动验证：拉起新版本，等待它写 `.health\<版本>.healthy.json`（主窗口可用后写入）。超时或进程退出即判定失败。
7. 失败 → 指针回滚到 `previous.json` 并重新启动旧版本；**失败的版本目录保留**用于排查。
8. 成功 → 清理解压目录。

## 4. 回滚

自动回滚：启动验证失败时更新器自动切回上一版本。

手工回滚：

```powershell
& "$env:LOCALAPPDATA\Programs\FNSyncPlus\FN_Sync_Plus.exe" `
  --layout "$env:LOCALAPPDATA\Programs\FNSyncPlus" --rollback
```

**数据格式保护**：`DataFormatGuard` 在同机记录配置格式版本。若新版把格式升级到 3，而旧版只支持 2，旧版会拒绝启动并提示安装最新版本，而不是写坏配置。因此：

- 程序二进制可以回滚；
- 数据格式不能靠回滚“降级”；
- 需要真正降级时，必须先备份 `%LOCALAPPDATA%\FNSyncPlus`，由开发者提供迁移工具。

## 5. 更新日志

`<安装根目录>\update.log`，追加式，包含：applying / installed / launch 结果 / 回滚 / 拒绝原因。日志不含凭据与 NAS 路径。

## 6. 已知限制（必须在试点前知晓）

- **没有正式代码签名证书**：安装包与主程序未做 Authenticode 签名，SmartScreen 会提示。内部试点需按公司策略放行，不得对外分发。
- **没有真实内网更新服务器**：当前仅用本地回环 HTTPS 服务与一次性测试密钥验证，尚未在公司服务器上部署与验收（BLOCKED）。
- **无人值守安装默认关闭**：需在设置中显式选择「无人值守安装」并打开开关；且在正式签名与验收完成前不得在公司生产电脑启用。
- **安装器未在本环境编译**：`FNSyncPlus.iss` 已写好，但当前机器没有 Inno Setup，未产出 `setup.exe`（BLOCKED）。
