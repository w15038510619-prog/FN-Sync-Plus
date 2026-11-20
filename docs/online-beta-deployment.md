# FN Sync Plus 首次在线 Beta 验收

本次从 `0.4.1` 基础安装版升级到 `0.4.2` 在线版。两版共用同一把 Beta 更新公钥，默认仅检查更新。下载和安装分别由使用者确认，无人值守安装关闭。

## 发布电脑

离线使用 `tools/release/Build-OnlineBeta.ps1`。将本次 Beta 私钥存放在发布电脑上受限的目录，路径须在仓库和输出目录之外。以后发布到同一个 Beta 通道继续使用这把密钥。

```powershell
$keys = Join-Path $env:LOCALAPPDATA 'FNSyncPlusReleaseSigning\Beta'
$inno = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
.\tools\release\Build-OnlineBeta.ps1 -Dotnet 'D:\dotnet\dotnet.exe' `
    -InnoCompiler $inno -OutputDirectory 'D:\FN-Beta\Release-0.4.2' `
    -PublicKey (Join-Path $keys 'update-public.pem') `
    -PrivateKey (Join-Path $keys 'update-private.pem')
```

`delivery` 下包含 `FN-Sync-Plus-0.4.1-Beta-Setup.exe`、`FN-Sync-Plus-0.4.2-x64.zip`、已签名的 `manifest.json`、公钥和 `SHA256SUMS.txt`。`build-provenance.json` 记录构建源码提交。发布时只传在线 ZIP、已签名清单和 CentOS 发布脚本，首次按需传公钥。如果清单时间或包内容需要更新，重新签名并保留签名后的原始字节。

## CentOS 发布

发布前确认 CentOS 的 SSH 主机指纹，发布电脑通过 OpenSSH 公钥或 SSH agent 身份认证接入。服务器需要 `python3`、`openssl` 和现有的 `/data/fnsync-updates/beta/`。发布脚本会把暂存文件、公钥和旧清单保存在同一文件系统上的 `/data/fnsync-updates/.beta-staging/`。这两个目录可复用现有 Nginx 配置。

先执行预检。`-PlanOnly` 校验本地压缩包、验签并显示发布范围，无网络操作。公钥指纹从受控签名目录核对。

```powershell
$fingerprint = (Get-FileHash (Join-Path $keys 'update-public.pem') -Algorithm SHA256).Hash
$publish = @{
    ReleaseDirectory = 'D:\FN-Beta\Release-0.4.2\delivery'
    SshTarget = 'root@192.168.8.18'
    SshPort = 22
    Dotnet = 'D:\dotnet\dotnet.exe'
    PublicKeySha256 = $fingerprint
    ProvisionTrust = $true
}
.\tools\release\Publish-OnlineBeta.ps1 @publish -PlanOnly
```

取得本次发布授权后，执行同一命令并移除 `-PlanOnly`。后续发布省略 `ProvisionTrust`。CentOS 先确认 ZIP 的大小及 SHA-256，再把 ZIP 原子发布到正式文件名。Windows 通过正常 TLS 下载并比对 ZIP 后才上传 `manifest.json`。CentOS 使用已固定的公钥独立验签，复核版本、URL、兼容版本、ZIP 大小和哈希，再原子替换清单。最后 Windows GET 清单并验签。

同版本 ZIP 已存在且内容相同时可以重试。已存在的发布文件内容不同、签名错误或清单版本冲突时停止。若在清单激活前中断，原清单仍提供服务。如果激活后才遇到网络错误，先确认正式清单和 ZIP 的实际哈希。旧清单副本在私有暂存目录，原有版本的 ZIP 可用于诊断。客户端启动验证失败的回滚由更新器在本机执行。

## 两台电脑操作顺序

每人使用自己的 Windows 和 fnOS 账号及独立备份目录。使用已有的测试样本验证，不在此次验收中修改正式备份内容。

1. A、B 分别等备份任务结束，再从托盘退出 FN Sync Plus。把 `%APPDATA%\FNSyncPlus` 与 `%LOCALAPPDATA%\FNSyncPlus`（存在时）复制到本人电脑上的本地备份目录。两台电脑各自保管这份配置备份。
2. 在 A、B 分别核对安装包 SHA-256，运行 `FN-Sync-Plus-0.4.1-Beta-Setup.exe`，沿用已有安装路径。通过原有快捷方式打开，核对版本、账号、原任务和测试恢复功能。
3. 在设置中填入 [Beta 更新清单](https://dev.sk-fj.com:8081/fnsync/beta/manifest.json)，核对 Beta 通道、仅检查更新、无人值守关闭。
4. A、B 各自点一次「检查更新」，记录发现的 `0.4.2` 和检查时间。B 根据提示手动下载并确认安装。安装期间等程序自行退出、更新和重新启动。
5. B 核对版本 `0.4.2`、NAS 连接和任务配置。把测试文件的当前版本和一份历史版本分别恢复到 `FN-B-CurrentRestore` 和 `FN-B-HistoryRestore`，核对恢复报告的 SHA-256。再次检查更新，确认已经是当前版本。A 的验证记录保留在 `0.4.1`，如要做第二次实际升级可在 B 完成后同样操作。
6. 记录每台电脑的 Windows 版本、应用版本、检查结果和恢复结果。升级失败时，保存安装目录的 `update.log`、对应 `.ops` 操作记录和 `.health` 报告，然后确认软件重新打开时显示的版本。对外提供证据时移除设备名、账号和本地路径等隐私信息。

如果无法访问该服务器或测试电脑，在验收记录中标记 BLOCKED。使用 `tools/acceptance/https-update-acceptance.ps1` 可在 Windows 上独立验证真实更新进程和自动回滚。该工具使用短期测试密钥和本地 HTTPS，故障注入只在这套测试环境运行。
