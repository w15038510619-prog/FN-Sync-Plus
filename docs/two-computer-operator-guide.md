# 双电脑剩余验收操作

两位操作人员各自解压同一份 ZIP 到本机固定磁盘。每人只在自己的电脑登录本人 fnOS 测试账号。验收 EXE 自包含，Windows x64 上无需安装 .NET SDK、Git 或管理员权限。它运行独立的 Core 备份任务，生成自己的临时配置和日志。已安装软件中的任务、凭据、当前与历史恢复及在线升级，沿用此前验收记录。

## 两边同时运行

先在两台 Windows 设置中确认时间已同步。商定一个未来 5–10 分钟的 UTC 时间，并在两台电脑使用完全相同的时间参数。认证、双因素验证和源文件准备须在这个时间之前完成。示例中的 NAS 地址需要换成现场 HTTPS 地址。自签名证书须使用经本人核对的 `-CertificateSha256`，连接失败会记录 BLOCKED。

电脑 A 的 PowerShell 执行下列命令。时间示例必须替换为本次实际未来时间。

```powershell
Set-Location 'C:\FN-TwoPC\FN-Sync-Plus-TwoPC'
$start = '2026-11-18T06:00:00Z'
.\Test-ComputerA.ps1 -Phase Run -Server 'https://NAS_HOST:5667' -StartAtUtc $start -ClocksSynchronized
```

电脑 B 同时执行同样命令，将入口改为 `Test-ComputerB.ps1`。两边使用 ZIP 中相同的 `session.json`，无需共享 Windows 配置、凭据或本地数据库。

脚本先等待共同开始时间，完成首次备份。在共同开始时间加 3 分钟时修改一个测试文件并执行增量备份。网络慢时可能错过预定增量时间，最终按实际 Worker 的重叠时段判定。时间未确认同步或没有时段重叠时，并发结果为 BLOCKED。每个备份 Worker 超过 5 分钟会停止等待并保留现场。

每台电脑只创建 3 个合成源文件，包括 16 MiB 随机二进制文件、本人标记文件和一个中文文件。新增 NAS 目录为本人个人空间下 `Codex_FN_Test_TwoPC_<session-guid>/<A或B>/backup`。目录已存在时停止，避免覆盖上一轮数据。两边同名源文件内容不同，当前恢复、历史标记恢复会比对文件相对路径、大小及 SHA-256。

这些新样本检查用于证明两账号和两客户端的独立性，已有的完整恢复验收仍直接复用。

## 交换报告并检查权限

默认本地结果位于 `%USERPROFILE%\FN-TwoPC-<session-guid>-A` 或末尾为 `-B` 的目录。运行结束后，双方只交换 `workspace\device-evidence.json`，保存到本机另一个文件名。报告包含测试 UID、NAS 路径、机器标识哈希和测试文件哈希，密码、Token、验证码均不写入报告。

电脑 A 执行下列命令。电脑 B 使用 B 入口和 A 发来的证据文件。

```powershell
.\Test-ComputerA.ps1 -Phase Peer -Server 'https://NAS_HOST:5667' -PeerEvidence 'C:\FN-TwoPC\peer-B.json'
```

此步骤用本人账号重新登录，确认两台 Windows、两份配置标识和两个 NAS UID 各自独立，向 NAS 请求另一位操作人员刚验证为非空的同一路径。只有明确的权限拒绝返回码才算权限隔离 PASS。空列表、404、未知回复和网络失败均记 BLOCKED，不能解释成权限隔离成功。如果目录可读，记录 FAIL，并停止扩大测试范围，先检查账号是否为管理员或共享了目录。

脚本还会把对方真实 UID 交给独立测试调度器，要求它在发出 NAS 请求前拒绝该任务。这个检查的目标是本人测试目录中的新子目录，运行轨迹必须为零请求。对方目录的直接写入 ACL 探测记 SKIP，客户端 UID 拦截和服务端只读权限检查单独报告。

## 需要一台先行试点机补的界面检查

`Run` 会自动验证测试任务在暂停时忽略触发、调度器离线时不发请求、恢复手动连接后保持待命、测试会话结束后拒绝写入。上述检查使用真实 NAS 会话和独立的 Core 调度器。

安装版正在上传时的账号切换、真实连接失效及物理断网不能由这个工具代做。优先在电脑 A 的独立合成测试任务完成以下观察，并保留对应日志和恢复哈希报告。

1. 上传期间暂停任务，确认安全停止，然后恢复并核对测试标记内容。
2. 上传期间断开软件中的 NAS 连接，确认停止写入。检查上一份已提交备份能恢复，未知提交和遗留锁给出明确状态。
3. 取得本人切换账号授权后，登录另一测试账号，确认旧测试任务被拒绝执行。再登录原账号，按程序受控流程恢复或重试，确认当前标记及历史保留。
4. 如果确实观察到认证失效，记录重新认证提示和停止写入情况。没有这个条件时，TokenExpiry 记 BLOCKED。主动断开会话不能代替服务器 Token 失效。

不要对正式任务制造故障。出现未知提交或锁时保留现场，通过软件已有检查流程处理。两台电脑各自确认安装版使用本机用户目录，配置和凭据未由另一台复制。B 已有的恢复和升级操作不需重做。

检查结束后，用 `-Phase Observe` 逐项录入 PASS / FAIL / BLOCKED / SKIP。PASS 和 FAIL 都必须提供已有证据文件路径，脚本记录该文件的 SHA-256。这是有文件支撑的操作人员记录，报告会明确标注 OperatorAttestation。

```powershell
.\Test-ComputerA.ps1 -Phase Observe
```

## 汇总与数据保留

双方交换 `run.json`、`peer.json`、可选的 `operator-observations.json` 和对应 Markdown。放到两个独立目录，执行下列命令。

```powershell
.\Merge-TwoComputerReports.ps1 -ComputerA 'C:\FN-TwoPC\Results-A' -ComputerB 'C:\FN-TwoPC\Results-B' -OutputDirectory 'C:\FN-TwoPC\Summary-1'
```

输出 `two-computer-result.json` 和 `.md`。缺少文件记 BLOCKED，任何实际 FAIL 都会保留原因。退出码为 0 表示要求的已执行项目均通过，1 表示 FAIL，2 表示存在 BLOCKED。未执行真实测试时可运行 A、B 脚本的默认 `Status` 阶段，结果为 BLOCKED。

每台电脑的 `workspace\cleanup-inventory.json` 列出本地样本、保留的隔离配置根和 NAS 测试路径。`source-files.json` 保留两版文件明细。工具保留全部测试数据和 NAS 历史，未提供远端自动清理命令。验收结论确认后，可由账号本人按这份清单逐项处理本次合成目录。已有有效备份和用户配置不在这份清理清单内。

`device-evidence.json` 本身是操作人员生成的测试证据文件。汇总器校验身份、共同轮次和文件 SHA-256，未对报告进行数字签名。只收集本次两位操作人员提供的原始结果。
