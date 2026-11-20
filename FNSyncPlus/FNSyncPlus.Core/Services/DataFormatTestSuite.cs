using System.Text.Json;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Regression tests for the data-format guard (P0-4).
///
/// The original code collapsed "no marker" and "marker unreadable" into <c>null</c> and then
/// treated null as format 0, so a damaged or unreadable marker let an older build write data it did
/// not understand. Each case below asserts both the verdict and that nothing was silently repaired:
/// a damaged file must still exist afterwards.
/// </summary>
public sealed class DataFormatTestSuite
{
    public Task<IReadOnlyList<UpdateSuiteCase>> RunAsync(string workspace, CancellationToken token = default)
    {
        Directory.CreateDirectory(workspace);
        var cases = new List<UpdateSuiteCase>();

        void Case(string name, Func<(bool Passed, string Detail)> body)
        {
            try
            {
                var (passed, detail) = body();
                cases.Add(new UpdateSuiteCase(name, passed, detail));
            }
            catch (Exception ex)
            {
                cases.Add(new UpdateSuiteCase(name, false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        string Fresh(string name)
        {
            string directory = Path.Combine(workspace, name);
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "data-format.json");
        }

        Case("首次运行（没有标记）允许初始化并写入当前格式", () =>
        {
            string path = Fresh("fresh");
            var guard = new DataFormatGuard(path, supportedFormat: 1);
            var probe = guard.Probe();
            bool claimed = guard.TryClaim("0.4.0", out string error);
            var after = guard.Probe();
            return (probe.State == FormatMarkerState.Missing && probe.AllowsInitialization && claimed &&
                    after.State == FormatMarkerState.Valid && after.Format == 1, $"{probe.State} / {after.State} {error}");
        });

        Case("有效旧格式被接受且不擅自抬高格式号", () =>
        {
            string path = Fresh("older");
            File.WriteAllText(path, "{\"format\":1,\"app_version\":\"0.3.0\"}");
            var guard = new DataFormatGuard(path, supportedFormat: 2);
            bool claimed = guard.TryClaim("0.4.0", out string error);
            var after = guard.Probe();
            int recorded = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("format").GetInt32();
            // Raising the marker without a migration would block a legitimate rollback.
            return (claimed && after.Format == 1 && recorded == 1, $"claimed={claimed} recorded={recorded} {error}");
        });

        Case("有效且相同的格式被接受", () =>
        {
            string path = Fresh("equal");
            File.WriteAllText(path, "{\"format\":2,\"app_version\":\"0.4.0\"}");
            var guard = new DataFormatGuard(path, supportedFormat: 2);
            bool claimed = guard.TryClaim("0.4.0", out string error);
            return (claimed && guard.Probe().Format == 2, error);
        });

        Case("数据格式高于程序支持时拒绝且标记保持不变", () =>
        {
            string path = Fresh("higher");
            File.WriteAllText(path, "{\"format\":5,\"app_version\":\"0.9.0\"}");
            var guard = new DataFormatGuard(path, supportedFormat: 2);
            bool claimed = guard.TryClaim("0.4.0", out string error);
            bool untouched = File.ReadAllText(path).Contains("\"format\":5", StringComparison.Ordinal);
            return (!claimed && untouched && error.Contains("停止运行", StringComparison.Ordinal), error);
        });

        Case("损坏 JSON 被拒绝、保留诊断副本且原文件不被删除", () =>
        {
            string path = Fresh("corrupt");
            File.WriteAllText(path, "{\"format\":1,\"app_ver");
            var guard = new DataFormatGuard(path, supportedFormat: 1);
            var probe = guard.Probe();
            bool claimed = guard.TryClaim("0.4.0", out string error);
            bool originalKept = File.Exists(path) && File.ReadAllText(path) == "{\"format\":1,\"app_ver";
            bool copyKept = guard.LastPreservedCopy != null && File.Exists(guard.LastPreservedCopy);
            return (probe.State == FormatMarkerState.Corrupt && !claimed && originalKept && copyKept, error);
        });

        Case("空文件被视为写入中断而不是首次运行", () =>
        {
            string path = Fresh("empty");
            File.WriteAllText(path, "");
            var guard = new DataFormatGuard(path, supportedFormat: 1);
            var probe = guard.Probe();
            bool claimed = guard.TryClaim("0.4.0", out _);
            return (probe.State == FormatMarkerState.Corrupt && !claimed, probe.Detail);
        });

        Case("格式号缺失或非法的标记被拒绝", () =>
        {
            string path = Fresh("noformat");
            File.WriteAllText(path, "{\"app_version\":\"0.4.0\"}");
            var guard = new DataFormatGuard(path, supportedFormat: 1);
            var probe = guard.Probe();
            bool claimed = guard.TryClaim("0.4.0", out _);
            return (probe.State == FormatMarkerState.Unsupported && !claimed, probe.Detail);
        });

        Case("标记文件被其他进程独占时拒绝继续（不是当作缺失）", () =>
        {
            string path = Fresh("locked");
            File.WriteAllText(path, "{\"format\":1,\"app_version\":\"0.4.0\"}");
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var guard = new DataFormatGuard(path, supportedFormat: 1);
            var probe = guard.Probe();
            bool claimed = guard.TryClaim("0.4.0", out string error);
            return (probe.State == FormatMarkerState.Unreadable && !claimed, $"{probe.State} {error}");
        });

        Case("写入中断遗留的临时文件不影响判定", () =>
        {
            string path = Fresh("temp-leftover");
            File.WriteAllText(path + ".tmp-deadbeef", "{\"format\":");
            File.WriteAllText(path, "{\"format\":1,\"app_version\":\"0.4.0\"}");
            var guard = new DataFormatGuard(path, supportedFormat: 1);
            bool claimed = guard.TryClaim("0.4.0", out string error);
            return (claimed && guard.Probe().Format == 1, error);
        });

        Case("两个调用并发写入时串行化且最终状态有效", () =>
        {
            string path = Fresh("concurrent");
            var first = new DataFormatGuard(path, supportedFormat: 1);
            var second = new DataFormatGuard(path, supportedFormat: 1);
            var results = new bool[8];
            Parallel.For(0, 8, i =>
            {
                var guard = i % 2 == 0 ? first : second;
                results[i] = guard.TryClaim("0.4.0", out _);
            });
            var probe = new DataFormatGuard(path, supportedFormat: 1).Probe();
            // Every writer either succeeded or was explicitly refused; nothing may end up corrupt.
            return (probe.State == FormatMarkerState.Valid && probe.Format == 1 && results.Any(r => r),
                $"state={probe.State} format={probe.Format} ok={results.Count(r => r)}/8");
        });

        Case("目录不可写时拒绝而不是假装成功", () =>
        {
            string file = Path.Combine(workspace, "blocked");
            File.WriteAllText(file, "not a directory");
            string path = Path.Combine(file, "data-format.json");
            var guard = new DataFormatGuard(path, supportedFormat: 1);
            bool claimed = guard.TryClaim("0.4.0", out string error);
            return (!claimed && error.Length > 0, error);
        });

        Case("回滚目标未声明可读格式时拒绝回滚", () =>
        {
            string root = Path.Combine(workspace, "rollback-unknown");
            string marker = Path.Combine(workspace, "rollback-unknown-format.json");
            File.WriteAllText(marker, "{\"format\":1,\"app_version\":\"0.5.0\"}");
            var layout = SeedTwoVersions(root, marker);
            bool ok = layout.TryRestorePointer(out string reason);
            return (!ok && reason.Contains("未声明", StringComparison.Ordinal) && layout.ActiveVersion == "0.5.0", reason);
        });

        Case("标记已损坏时拒绝回滚并保留当前版本", () =>
        {
            string root = Path.Combine(workspace, "rollback-corrupt");
            string marker = Path.Combine(workspace, "rollback-corrupt-format.json");
            File.WriteAllText(marker, "{oops");
            var layout = SeedTwoVersions(root, marker, declareFormat: true);
            bool ok = layout.TryRestorePointer(out string reason);
            return (!ok && layout.ActiveVersion == "0.5.0" && reason.Contains("无法确认", StringComparison.Ordinal), reason);
        });

        Case("数据格式已升级到更高版本时拒绝回滚到旧程序", () =>
        {
            string root = Path.Combine(workspace, "rollback-upgraded");
            string marker = Path.Combine(workspace, "rollback-upgraded-format.json");
            File.WriteAllText(marker, "{\"format\":1,\"app_version\":\"0.4.0\"}");
            var layout = SeedTwoVersions(root, marker, declareFormat: true);
            var guard = new DataFormatGuard(marker, supportedFormat: 2);
            bool upgraded = guard.TryUpgrade("0.5.0", 2, out string upgradeError);
            bool ok = layout.TryRestorePointer(out string reason);
            return (upgraded && !ok && layout.ActiveVersion == "0.5.0" && reason.Contains("可能写坏", StringComparison.Ordinal),
                $"{upgradeError} / {reason}");
        });

        Case("数据格式相同时允许回滚", () =>
        {
            string root = Path.Combine(workspace, "rollback-ok");
            string marker = Path.Combine(workspace, "rollback-ok-format.json");
            File.WriteAllText(marker, "{\"format\":1,\"app_version\":\"0.4.0\"}");
            var layout = SeedTwoVersions(root, marker, declareFormat: true);
            bool ok = layout.TryRestorePointer(out string reason);
            return (ok && layout.ActiveVersion == "0.4.0", reason);
        });

        Case("升级格式标记时不会覆盖损坏文件", () =>
        {
            string path = Fresh("upgrade-corrupt");
            File.WriteAllText(path, "not json");
            var guard = new DataFormatGuard(path, supportedFormat: 2);
            bool upgraded = guard.TryUpgrade("0.5.0", 2, out string error);
            return (!upgraded && File.ReadAllText(path) == "not json" && guard.LastPreservedCopy != null, error);
        });

        Case("创建备份失败时立即终止且原文件不受影响", () =>
        {
            string path = Fresh("backup-fail");
            string originalContent = "{\"format\":1,\"app_version\":\"0.4.0\"}";
            File.WriteAllText(path, originalContent);
            var guard = new DataFormatGuard(path, supportedFormat: 2)
            {
                FaultInjectBackupCreation = _ => false
            };
            bool upgraded = guard.TryUpgrade("0.5.0", 2, out string error);
            bool untouched = File.ReadAllText(path) == originalContent;
            return (!upgraded && untouched && guard.LastTransactionState == WriteTransactionState.Prepared &&
                    error.Contains("无法创建配置格式标记事务备份"), $"upgraded={upgraded} state={guard.LastTransactionState} error={error}");
        });

        Case("替换操作失败时触发回滚并恢复原标记", () =>
        {
            string path = Fresh("replace-fail-rollback");
            string originalContent = "{\"format\":1,\"app_version\":\"0.4.0\"}";
            File.WriteAllText(path, originalContent);
            var guard = new DataFormatGuard(path, supportedFormat: 2)
            {
                FaultInjectReplaceOperation = _ => false
            };
            bool upgraded = guard.TryUpgrade("0.5.0", 2, out string error);
            var probe = guard.Probe();
            bool restored = File.ReadAllText(path) == originalContent;
            return (!upgraded && restored && probe.State == FormatMarkerState.Valid && probe.Format == 1 &&
                    guard.LastTransactionState == WriteTransactionState.RolledBack,
                    $"upgraded={upgraded} state={guard.LastTransactionState} error={error}");
        });

        Case("写入后回读验证失败时自动回滚恢复原标记", () =>
        {
            string path = Fresh("rollback-on-verify-fail");
            File.WriteAllText(path, "{\"format\":1,\"app_version\":\"0.4.0\"}");
            var guard = new DataFormatGuard(path, supportedFormat: 2)
            {
                FaultInjectPostWriteVerifier = _ => false // 模拟回读或完整性校验未通过
            };
            bool upgraded = guard.TryUpgrade("0.5.0", 2, out string error);
            var after = guard.Probe();
            string content = File.ReadAllText(path);
            bool rolledBack = !upgraded && after.State == FormatMarkerState.Valid && after.Format == 1 &&
                              content.Contains("\"format\":1") && error.Contains("恢复原") &&
                              guard.LastTransactionState == WriteTransactionState.RolledBack;
            return (rolledBack, $"upgraded={upgraded} formatAfter={after.Format} state={guard.LastTransactionState} error={error}");
        });

        Case("回滚失败时严格保留备份副本且进入RecoveryFailed", () =>
        {
            string path = Fresh("rollback-fail-preserve-backup");
            string originalContent = "{\"format\":1,\"app_version\":\"0.4.0\"}";
            File.WriteAllText(path, originalContent);
            var guard = new DataFormatGuard(path, supportedFormat: 2)
            {
                FaultInjectPostWriteVerifier = _ => false, // 触发回滚
                FaultInjectRollbackFailure = _ => false   // 模拟回滚失败
            };
            bool upgraded = guard.TryUpgrade("0.5.0", 2, out string error);
            bool isRecoveryFailed = guard.LastTransactionState == WriteTransactionState.RecoveryFailed;
            bool backupKept = guard.LastPreservedBackup != null && File.Exists(guard.LastPreservedBackup) &&
                              File.ReadAllText(guard.LastPreservedBackup) == originalContent;
            return (!upgraded && isRecoveryFailed && backupKept && error.Contains("RecoveryFailed") && error.Contains("严格保留"),
                $"upgraded={upgraded} state={guard.LastTransactionState} backupKept={backupKept} error={error}");
        });

        Case("回滚后文件虽为合法JSON但SHA256与原文件不符时判定RecoveryFailed并保留备份", () =>
        {
            string path = Fresh("rollback-sha-mismatch-preserve-backup");
            string originalContent = "{\"format\":1,\"app_version\":\"0.4.0\"}";
            File.WriteAllText(path, originalContent);
            var guard = new DataFormatGuard(path, supportedFormat: 2)
            {
                FaultInjectPostWriteVerifier = _ => false, // 触发回滚
                FaultInjectRollbackCorruption = _ => false  // 模拟回滚后文件虽是合法 JSON 但内容与原文件 SHA-256 不一致
            };
            bool upgraded = guard.TryUpgrade("0.5.0", 2, out string error);
            bool isRecoveryFailed = guard.LastTransactionState == WriteTransactionState.RecoveryFailed;
            bool backupKept = guard.LastPreservedBackup != null && File.Exists(guard.LastPreservedBackup) &&
                              File.ReadAllText(guard.LastPreservedBackup) == originalContent;
            return (!upgraded && isRecoveryFailed && backupKept && error.Contains("RecoveryFailed") && error.Contains("严格保留"),
                $"upgraded={upgraded} state={guard.LastTransactionState} backupKept={backupKept} error={error}");
        });

        Case("锁文件被短暂独占占用时通过重试安全获取", () =>
        {
            string path = Fresh("lock-transient-retry");
            File.WriteAllText(path, "{\"format\":1,\"app_version\":\"0.4.0\"}");
            string lockPath = path + ".lock";
            var blocker = new TaskCompletionSource<bool>();
            var lockReleased = new TaskCompletionSource<bool>();

            var backgroundLock = Task.Run(() =>
            {
                using var fs = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                blocker.SetResult(true);
                Thread.Sleep(150); // 持有锁 150 毫秒
                lockReleased.SetResult(true);
            });

            blocker.Task.Wait(2000);
            var guard = new DataFormatGuard(path, supportedFormat: 2);
            bool upgraded = guard.TryUpgrade("0.5.0", 2, out string error);
            backgroundLock.Wait(2000);
            var after = guard.Probe();
            return (upgraded && after.Format == 2, $"upgraded={upgraded} format={after.Format} error={error}");
        });

        Case("临时文件损坏或遗留不影响原标记完整性", () =>
        {
            string path = Fresh("temp-damaged-leftover");
            File.WriteAllText(path, "{\"format\":1,\"app_version\":\"0.4.0\"}");
            File.WriteAllText(path + ".tmp-bad", "corrupted partial write");
            File.WriteAllText(path + ".bak-bad", "corrupted backup");
            var guard = new DataFormatGuard(path, supportedFormat: 1);
            bool claimed = guard.TryClaim("0.4.1", out string error);
            var after = guard.Probe();
            return (claimed && after.State == FormatMarkerState.Valid && after.Format == 1,
                $"claimed={claimed} format={after.Format} error={error}");
        });

        return Task.FromResult<IReadOnlyList<UpdateSuiteCase>>(cases);
    }

    /// <summary>Installs 0.4.0 then 0.5.0 so a previous pointer exists for rollback tests.</summary>
    private static VersionedInstallLayout SeedTwoVersions(string root, string markerPath, bool declareFormat = false)
    {
        var layout = new VersionedInstallLayout(root, markerPath);
        foreach (string version in new[] { "0.4.0", "0.5.0" })
        {
            string payload = Path.Combine(Path.GetTempPath(), "fnsync-fmt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "FNSyncPlus.WinUI.exe"), "payload " + version);
            if (declareFormat)
                new AppBuildMetadata(version, 1).Save(payload);
            var outcome = layout.InstallAsync(version, payload, _ => Task.FromResult(true), CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!outcome.Ok) throw new InvalidOperationException("无法准备安装布局：" + outcome.Error);
        }
        return layout;
    }
}
