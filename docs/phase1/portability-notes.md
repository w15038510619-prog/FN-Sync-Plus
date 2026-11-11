# 跨平台构建与验证说明（阶段 1）

目的：在没有 Windows 的开发机（Linux 沙盒）上编译并运行 Core 回归，把"能不能编译/跑通"与"Windows CI 结论"分开记录。

已完成的代码兼容处理（Windows 行为不变）：
1. `PathHelper.EnsureLongPathPrefix` 在非 Windows 上原样返回路径，不再把 POSIX 路径改写成 `\\?\` 形式。
2. `SmbStorageProvider.CombineUnc` 在 POSIX 上保留正斜杠，本地目录模拟存储才能被枚举。
3. `RestoreEngine` 使用 `ToLocalSeparators` 按宿主平台拼接相对路径（此前硬编码反斜杠，POSIX 上会把整条路径当成文件名）。
4. `SmbAuthService.IsCredentialProtectionAvailable`：没有 DPAPI 时拒绝以明文保存凭据，测试标记 SKIP。

测试套件中的平台门控：
- 单实例命名事件、联接点、长路径前缀、正斜杠路径：非 Windows 显示 `[SKIP]` 并带原因，不再崩溃。
- 300 字节文件名在 ext4 上不可创建，POSIX 主机使用短名，Windows CI 保留原名。

本地结果（Linux）：`--integrity-regressions` 运行的 Windows 专有部分被显式 SKIP；迁移、恢复、压力与故障套件另有独立探针，结果为 PASS。
最终结论以 GitHub Actions（windows-latest）为准；当前沙盒没有推送凭据，CI 尚未运行。
