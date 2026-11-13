# 飞牛 fnOS 双账号安全隔离与权限边界验收报告

- **测试执行时间**: 2026-11-13 09:25:31
- **执行宿主环境**: DESKTOP-25RBAJ5
- **测试模式**: 模拟沙盒隔离回归 + 物理状态诚实留痕模式
- **NAS 访问终端**: （未配置）
- **账号 A 用户名**: （未提供）
- **账号 B 用户名**: （未提供）

---

## 一、双账号隔离验收矩阵

| 序号 | 验收场景 | 验收状态 | 证据与详细诊断说明 |
| :---: | :--- | :---: | :--- |
| 1 | 沙盒逻辑仿真：多账号隔离验收全套回归 (13/13) | <span style='color:green'>**PASS**</span> | AccountIsolationTestSuite 13 项全部通过（覆盖 UID 判定、大小写敏感、沙盒隔离、跨会话不可见） |
| 2 | 物理环境：账号 A 独立创建与恢复个人空间备份 | <span style='color:goldenrod'>**BLOCKED**</span> | 现场未提供第 2 个物理真实授权账号与专用测试 NAS 节点 |
| 3 | 物理环境：账号 B 独立创建与恢复个人空间备份 | <span style='color:goldenrod'>**BLOCKED**</span> | 现场未提供第 2 个物理真实授权账号与专用测试 NAS 节点 |
| 4 | 物理环境：账号切换后旧账号后台同步任务立即中止 | <span style='color:goldenrod'>**BLOCKED**</span> | 待现场提供双账号进行物理热切换验证 |
| 5 | 物理环境：UID 不匹配时立即安全中止（Fail-closed） | <span style='color:goldenrod'>**BLOCKED**</span> | 待现场接入两个不同物理 UID 账号 |
| 6 | 物理环境：旧账号凭据严禁跨账号执行新任务 | <span style='color:goldenrod'>**BLOCKED**</span> | 待现场验证物理 Token 域隔离 |
| 7 | 物理环境：账号 A 与账号 B 个人空间目录边界互不混淆 | <span style='color:goldenrod'>**BLOCKED**</span> | 待真实个人空间文件列表比对 |
| 8 | 物理环境：同名任务（同名子目录）目标隔离不串用 | <span style='color:goldenrod'>**BLOCKED**</span> | 待现场多账号同名任务验证 |
| 9 | 物理环境：越权或无权限操作明确报错拦截 | <span style='color:goldenrod'>**BLOCKED**</span> | 待真实越权调用接口拦截测试 |
| 10 | 物理环境：严禁对其他账号数据执行探测性破坏 | <span style='color:goldenrod'>**BLOCKED**</span> | 待现场审计日志复核 |
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