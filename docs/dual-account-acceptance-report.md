# 飞牛 fnOS 双账号安全隔离与权限边界验收报告

- **测试执行时间**: 2026-11-13 10:37:39
- **执行宿主环境**: DESKTOP-25RBAJ5
- **测试模式**: 沙盒隔离算法回归 + 物理状态诚实留痕模式
- **NAS 访问终端**: （未配置）
- **账号 A 用户名**: （未提供）
- **账号 B 用户名**: （未提供）

---

## 一、双账号隔离验收矩阵

| 序号 | 验收场景 | 验收状态 | 证据与详细诊断说明 |
| :---: | :--- | :---: | :--- |
| 1 | 沙盒逻辑仿真：多账号隔离验收全套回归 (13/13) | <span style='color:green'>**PASS**</span> | AccountIsolationTestSuite 13 项全部通过（覆盖 UID 判定、大小写敏感、沙盒隔离、跨会话不可见） |
| 2 | 物理环境：账号 A 真实认证与个人空间解析 | <span style='color:goldenrod'>**BLOCKED**</span> | 现场未提供第 2 个物理真实授权账号与专用测试 NAS 节点；已就绪真实 API 客户端与执行入口 |
| 3 | 物理环境：账号 B 真实认证与个人空间解析 | <span style='color:goldenrod'>**BLOCKED**</span> | 现场未提供第 2 个物理真实授权账号与专用测试 NAS 节点；已就绪真实 API 客户端与执行入口 |
| 4 | 物理环境：校验两个账号 UID 权威独立性 (UID_A != UID_B) | <span style='color:goldenrod'>**BLOCKED**</span> | 待现场接入两套独立真实授权账号通过实际响应校验 |
| 5 | 物理环境：账号切换真实触发客户端状态变化与旧任务关断 | <span style='color:goldenrod'>**BLOCKED**</span> | 待现场双账号执行真实客户端热切换验证 |
| 6 | 物理环境：Token 作用域锁定与跨账号越权拦截 | <span style='color:goldenrod'>**BLOCKED**</span> | 待现场验证物理 Token 跨租户调用被 NAS 服务端与客户端双向拦截 |
| 7 | 物理环境：同名任务远端路径严格物理隔离 | <span style='color:goldenrod'>**BLOCKED**</span> | 待现场实测账号 A 与账号 B 创建同名任务（如'工作资料'）时的物理路径隔离 |
| 8 | 物理环境：严禁对其他账号数据执行探测性破坏或删除 | <span style='color:goldenrod'>**BLOCKED**</span> | 待现场审计日志复核确认无越权写或删除操作 |
---

## 二、验收结论与企业安全合规声明

1. **逻辑与算法隔离（PASS）**：
   在自动化测试套件中，AccountIsolationTestSuite（13/13 PASS）完整验证了：
   - 任务记录的 UID 与当前已登录账号不一致时强制归类为账号不明并拒绝操作；
   - 未登录或 UID 未知时严格拒绝推导个人空间归属；
   - 账号名大小写不同严格区分不同账号；
   - 跨隔离会话的配置互不可见；
   - 切换账号后旧任务的命名空间与凭据完全失效。

2. **物理环境状态合规声明（BLOCKED）**：
   受限于受控构建与本地单机测试环境，现场未连接真实的第二个物理授权 fnOS 账号。依据企业级发布安全规范，物理实体多账号联调状态严格保持为 **BLOCKED**，严禁以伪造实测数据替代真实物理验证。

3. **测试人员双账号现场实测操作步骤**：
   - 步骤 1：准备两台 Windows PC 或在单机上准备两个独立授权的 fnOS 账号凭据；
   - 步骤 2：以管理员权限打开 PowerShell，运行命令：
     `powershell
     .\tools\acceptance\Test-DualAccountAcceptance.ps1 -Interactive
     `
   - 步骤 3：根据终端提示依次输入 NAS 地址、账号 A 用户名、密码（密文输入）、账号 B 用户名、密码（密文输入）；
   - 步骤 4：脚本将调用正式 FnOsApiClient 与 FnOsPersonalStorageProvider 执行实体联调，并更新本报告。