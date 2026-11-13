using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FNSyncPlus.Core.Storage;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Independent-machine recovery verification at the "isolated session + simulated remote
/// storage" environment level — the highest level executable on this machine, because Windows
/// Sandbox / Hyper-V optional features require elevation that is not available here (the real
/// independent-Windows-environment level stays BLOCKED and is tracked in the acceptance report).
///
/// A REAL versioned backup is produced by <see cref="ExperimentalVersionedBackup"/> against a
/// mock NAS (a plain directory behind <see cref="SmbStorageProvider"/>), then a brand-new
/// restore context — no machine-A configuration, no machine-A paths, the source directory even
/// renamed away — discovers and restores it exactly the way a freshly installed client on a new
/// computer would. Every restored byte is compared per-file with SHA-256, the remote storage is
/// hash-snapshotted to prove restores are read-only, and a corrupted object must be rejected by
/// the per-file integrity check with a recorded failure log entry.
/// </summary>
public sealed class IndependentRecoveryTestSuite
{
    public const string EnvironmentLevel =
        "LocalProcess + 模拟远端存储（目录级 SmbStorageProvider）；真实独立 Windows 环境（Sandbox/Hyper-V/第二台授权电脑）未达到，记录为 BLOCKED";

    public async Task<IReadOnlyList<UpdateSuiteCase>> RunAsync(string workspace, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(workspace)) throw new ArgumentException("测试工作目录不能为空", nameof(workspace));
        var cases = new List<UpdateSuiteCase>();
        var clock = Stopwatch.StartNew();

        async Task Case(string name, Func<Task<(bool Passed, string Detail)>> body)
        {
            try
            {
                var (passed, detail) = await body().ConfigureAwait(false);
                cases.Add(new UpdateSuiteCase(name, passed, detail));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                cases.Add(new UpdateSuiteCase(name, false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        // ---------------------------------------------------------------- shared fixture
        string machineA = Path.Combine(workspace, "machine-a");
        string source = Path.Combine(machineA, "src");
        string mockNas = Path.Combine(workspace, "mock-nas");
        string machineB = Path.Combine(workspace, "machine-b");
        const string target = "原机资料";
        string remoteTarget = Path.Combine(mockNas, target);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(mockNas);
        Directory.CreateDirectory(machineB);

        string longName = new string('长', 100) + ".txt";        // 304 UTF-8 bytes, over the 255 limit
        string longDir = new string('目', 90);
        var fixture = new Dictionary<string, byte[]>
        {
            ["日常报表.txt"] = Encoding.UTF8.GetBytes("普通中文内容"),
            ["Q4_report_最终版_v2.txt"] = Encoding.UTF8.GetBytes("mixed content"),
            ["📁项目⭐说明.txt"] = Encoding.UTF8.GetBytes("emoji unicode"),
            ["Ünïcødé_文件.bin"] = new byte[] { 1, 2, 3, 4, 5 },
            [longName] = Encoding.UTF8.GetBytes("超长中文文件名内容"),
            [longDir + "/子文件.txt"] = Encoding.UTF8.GetBytes("超长目录下的文件"),
            ["将被修改.txt"] = Encoding.UTF8.GetBytes("第一版内容"),
            ["冲突.txt"] = Encoding.UTF8.GetBytes("备份里的冲突文件内容"),
            ["证据.bin"] = RandomNumberGenerator.GetBytes(64 * 1024),
        };
        foreach (var (rel, bytes) in fixture)
        {
            string path = Path.Combine(source, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, token);
        }
        Directory.CreateDirectory(Path.Combine(source, "空目录A", "内层空目录", "更深空目录"));

        var backup = new ExperimentalVersionedBackup();
        var versions = new VersionedManifestStore();
        await using var provider = new LocalExclusiveProvider(mockNas);

        // Remote snapshot taken right after all backup operations; every restore must leave the
        // remote byte-identical (restores are strictly read-only).
        Dictionary<string, string> remoteSnapshot = new();

        static Dictionary<string, string> HashTree(string root)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(root)) return map;
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file);
                map[rel] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            }
            return map;
        }

        // 1. Environment level declaration + physical separation of the simulated remote.
        await Case("环境等级声明：独立会话+模拟远端存储（真实独立 Windows 环境 BLOCKED）", () =>
        {
            bool separated = !Path.GetFullPath(mockNas).StartsWith(Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase) &&
                             !Path.GetFullPath(source).StartsWith(Path.GetFullPath(mockNas), StringComparison.OrdinalIgnoreCase) &&
                             Directory.Exists(mockNas);
            return Task.FromResult((separated, $"level={EnvironmentLevel}；mockNas 与 source 物理分离={separated}"));
        });

        // 2. Machine A commits a full first version with the real engine.
        await Case("原机真实引擎提交首批完整版本备份", async () =>
        {
            var created = await backup.CreateNewAsync(provider, source, target, token).ConfigureAwait(false);
            bool ok = created.Snapshot.Sequence == 1 && created.UploadedFiles == fixture.Count &&
                      created.Snapshot.Manifest.Files.Count == fixture.Count;
            return (ok, $"seq={created.Snapshot.Sequence} uploaded={created.UploadedFiles} files={created.Snapshot.Manifest.Files.Count}");
        });

        // 3. An incremental run after a modification produces a restorable history entry.
        await Case("修改文件并增量提交，产生可独立恢复的历史版本", async () =>
        {
            await File.WriteAllTextAsync(Path.Combine(source, "将被修改.txt"), "第二版内容", token);
            var after = await backup.RunAsync(provider, source, target, token).ConfigureAwait(false);
            bool hasHistory = after.Snapshot.Manifest.History.Any(h =>
                h.OriginalRel == "将被修改.txt" && h.Reason == "superseded");
            remoteSnapshot = HashTree(mockNas);
            return (hasHistory && remoteSnapshot.Count > 0,
                $"historyEntries={after.Snapshot.Manifest.History.Count} remoteFiles={remoteSnapshot.Count}");
        });

        // 4. A brand-new context discovers the backup with zero machine-A configuration.
        await Case("独立环境零配置发现原机备份（不读原机任何本地配置）", async () =>
        {
            var warnings = new List<string>();
            var freshRestore = new RestoreEngine(new ManifestManager());
            var found = await freshRestore.DiscoverBackupPackagesAsync(mockNas, token, warnings).ConfigureAwait(false);
            var hit = found.SingleOrDefault(p => p.PackageName == target);
            bool ok = hit != null && hit.FileCount == fixture.Count;
            return (ok, $"found={found.Count} package={hit?.PackageName} files={hit?.FileCount} warnings={warnings.Count}");
        });

        // 5. The restore must not depend on machine-A at all: rename the source away first.
        string restoreCurrent = Path.Combine(machineB, "restore-current");
        RestoreProgressReport? reportCurrent = null;
        await Case("原机目录改名（模拟原机不在场）后恢复当前版本仍成功", async () =>
        {
            string offline = machineA + ".offline";
            Directory.Move(machineA, offline);
            try
            {
                var freshRestore = new RestoreEngine(new ManifestManager());
                reportCurrent = await freshRestore.RunRestoreVersionedAsync(provider, target, restoreCurrent,
                    ConflictResolution.SkipExisting, token).ConfigureAwait(false);
            }
            finally
            {
                Directory.Move(offline, machineA);
            }
            bool ok = reportCurrent.RestoredFiles == fixture.Count && reportCurrent.FailedFiles == 0;
            return (ok, $"restored={reportCurrent.RestoredFiles} failed={reportCurrent.FailedFiles} skipped={reportCurrent.SkippedFiles}");
        });

        // 6. Every restored file matches the original per-file SHA-256.
        await Case("逐文件 SHA-256 完整性强校验全部一致", () =>
        {
            int compared = 0;
            foreach (var (rel, bytes) in fixture)
            {
                string restoredPath = Path.Combine(restoreCurrent, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(restoredPath)) return Task.FromResult((false, $"缺少恢复文件：{rel}"));
                byte[] expectedBytes = rel == "将被修改.txt" ? Encoding.UTF8.GetBytes("第二版内容") : bytes;
                string expected = Convert.ToHexString(SHA256.HashData(expectedBytes));
                string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(restoredPath)));
                if (expected != actual) return Task.FromResult((false, $"哈希不一致：{rel}"));
                compared++;
            }
            bool verified = reportCurrent != null && reportCurrent.IntegrityVerifiedFiles == fixture.Count;
            return Task.FromResult((compared == fixture.Count && verified,
                $"compared={compared} integrityVerified={reportCurrent?.IntegrityVerifiedFiles}"));
        });

        // 7. The 300-byte long Chinese file name survives the round trip.
        await Case("超长中文文件名（300 字节）完整恢复", () =>
        {
            string path = Path.Combine(restoreCurrent, longName);
            bool ok = File.Exists(path) && File.ReadAllText(path) == "超长中文文件名内容";
            bool flagged = reportCurrent != null && reportCurrent.LongNameRestoredFiles >= 1;
            return Task.FromResult((ok && flagged, $"exists={File.Exists(path)} longNameRestored={reportCurrent?.LongNameRestoredFiles}"));
        });

        // 8. The over-long directory name and its child file survive.
        await Case("超长目录名及其子文件完整恢复", () =>
        {
            string path = Path.Combine(restoreCurrent, longDir, "子文件.txt");
            bool ok = File.Exists(path) && File.ReadAllText(path) == "超长目录下的文件";
            return Task.FromResult((ok, $"exists={File.Exists(path)}"));
        });

        // 9. Empty directories — including nested empty ones — are restored.
        await Case("空目录（含嵌套空目录）完整恢复", () =>
        {
            string nested = Path.Combine(restoreCurrent, "空目录A", "内层空目录", "更深空目录");
            bool ok = Directory.Exists(nested) &&
                      Directory.GetFileSystemEntries(Path.Combine(restoreCurrent, "空目录A", "内层空目录", "更深空目录")).Length == 0;
            bool counted = reportCurrent != null && reportCurrent.RestoredDirectories >= 3;
            return Task.FromResult((ok && counted, $"nestedExists={Directory.Exists(nested)} restoredDirs={reportCurrent?.RestoredDirectories}"));
        });

        // 10. Mixed Unicode names (CJK, emoji, latin-extended) restore byte-identical.
        await Case("混合 Unicode 名称恢复且内容逐字节一致", () =>
        {
            foreach (string rel in new[] { "📁项目⭐说明.txt", "Ünïcødé_文件.bin", "Q4_report_最终版_v2.txt" })
            {
                string restoredPath = Path.Combine(restoreCurrent, rel);
                if (!File.Exists(restoredPath)) return Task.FromResult((false, $"缺少：{rel}"));
                if (!File.ReadAllBytes(restoredPath).SequenceEqual(fixture[rel]))
                    return Task.FromResult((false, $"内容不一致：{rel}"));
            }
            return Task.FromResult((true, "emoji/latin-extended/中英混合 3 类名称全部一致"));
        });

        // 11. The pre-modification content is restorable as an independent history version.
        await Case("恢复历史版本（修改前的第一版内容）", async () =>
        {
            var current = await versions.LoadAsync(provider, target, token).ConfigureAwait(false);
            var history = current!.Manifest.History.Single(h => h.OriginalRel == "将被修改.txt" && h.Reason == "superseded");
            string outDir = Path.Combine(machineB, "restore-history");
            var freshRestore = new RestoreEngine(new ManifestManager());
            bool ok = await freshRestore.RunRestoreHistoryFileAsync(provider, target, outDir, history, token).ConfigureAwait(false);
            string text = File.ReadAllText(Path.Combine(outDir, "将被修改.txt"));
            return (ok && text == "第一版内容", $"restored='{text}' expected='第一版内容'");
        });

        // 12. Restore never overwrites an existing user file by default.
        await Case("同名冲突默认不覆盖用户已有文件", async () =>
        {
            string outDir = Path.Combine(machineB, "restore-conflict");
            Directory.CreateDirectory(outDir);
            await File.WriteAllTextAsync(Path.Combine(outDir, "冲突.txt"), "用户自己的内容", token);
            var freshRestore = new RestoreEngine(new ManifestManager());
            var report = await freshRestore.RunRestoreVersionedAsync(provider, target, outDir,
                ConflictResolution.SkipExisting, token).ConfigureAwait(false);
            bool ok = report.SkippedFiles == 1 &&
                      File.ReadAllText(Path.Combine(outDir, "冲突.txt")) == "用户自己的内容" &&
                      File.Exists(Path.Combine(outDir, longName));
            return (ok, $"skipped={report.SkippedFiles} restored={report.RestoredFiles} 冲突.txt 保留用户内容");
        });

        // 13. A corrupted remote object is rejected by the per-file hash check and the failure
        //     is recorded with a detailed log entry instead of silently landing on disk.
        await Case("损坏对象被逐文件哈希拒绝且失败留痕（不污染环境）", async () =>
        {
            var current = await versions.LoadAsync(provider, target, token).ConfigureAwait(false);
            var entry = current!.Manifest.Files.Single(f => f.OriginalRel == "证据.bin");
            string storedPath = Path.Combine(remoteTarget, entry.StoredRel.Replace('/', Path.DirectorySeparatorChar));
            byte[] original = await File.ReadAllBytesAsync(storedPath, token);
            byte[] corrupted = (byte[])original.Clone();
            corrupted[0] ^= 0xFF;
            string outDir = Path.Combine(machineB, "restore-corrupt");
            RestoreProgressReport report;
            try
            {
                await File.WriteAllBytesAsync(storedPath, corrupted, token);
                var freshRestore = new RestoreEngine(new ManifestManager());
                report = await freshRestore.RunRestoreVersionedAsync(provider, target, outDir,
                    ConflictResolution.SkipExisting, token).ConfigureAwait(false);
            }
            finally
            {
                await File.WriteAllBytesAsync(storedPath, original, token);
            }
            var failed = report.FailedItems.SingleOrDefault(i => i.OriginalRel == "证据.bin");
            bool othersOk = report.RestoredFiles == fixture.Count - 1;
            bool logged = failed != null && !string.IsNullOrWhiteSpace(failed.ErrorMessage);
            bool noPollution = !File.Exists(Path.Combine(outDir, "证据.bin"));
            return (failed != null && othersOk && logged && noPollution,
                $"failedItems={report.FailedItems.Count} others={report.RestoredFiles} error={failed?.ErrorMessage}");
        });

        // 14. The whole restore campaign never modified the remote storage.
        await Case("远端存储全程只读（恢复前后远端 SHA-256 快照一致）", () =>
        {
            var after = HashTree(mockNas);
            if (after.Count != remoteSnapshot.Count)
                return Task.FromResult((false, $"远端文件数变化 {remoteSnapshot.Count} -> {after.Count}"));
            foreach (var (rel, hash) in remoteSnapshot)
            {
                if (!after.TryGetValue(rel, out string? now) || now != hash)
                    return Task.FromResult((false, $"远端文件被修改：{rel}"));
            }
            return Task.FromResult((true, $"remoteFiles={after.Count} 全部一致"));
        });

        clock.Stop();
        return cases;
    }

    /// <summary>
    /// Adds exclusive directory creation to the plain SMB provider, the same capability the FnOS
    /// personal-space provider has natively. Exclusive creation is what lets the first backup of a
    /// target refuse to mix with whatever already exists at that path.
    /// </summary>
    private sealed class LocalExclusiveProvider : SmbStorageProvider, IExclusiveDirectoryProvider
    {
        private static readonly object DirectoryLock = new();
        private readonly string _root;

        public LocalExclusiveProvider(string root)
            : base(smbService: null, rootUncPath: root)
        {
            _root = root;
        }

        public Task<ExclusiveDirectoryResult> TryCreateDirectoryExclusiveAsync(string relativePath, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!Utils.PathHelper.IsSafeRelativePath(relativePath))
                throw new ArgumentException("不安全的相对路径", nameof(relativePath));
            string directory = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            lock (DirectoryLock)
            {
                if (Directory.Exists(directory)) return Task.FromResult(ExclusiveDirectoryResult.AlreadyExists);
                Directory.CreateDirectory(directory);
                return Task.FromResult(ExclusiveDirectoryResult.Created);
            }
        }
    }
}
