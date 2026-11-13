using System.Text;
using FNSyncPlus.Core.Storage;

namespace FNSyncPlus.Core.Services;

public sealed record CapacitySuiteCase(string Name, bool Passed, string Detail);

public sealed record CapacitySuiteReport(IReadOnlyList<CapacitySuiteCase> Cases, int Passed, int Failed, TimeSpan Duration)
{
    public bool AllPassed => Failed == 0;

    public string ToSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"容量与保留策略测试：{Passed} PASS / {Failed} FAIL，用时 {Duration.TotalSeconds:F1}s");
        foreach (var item in Cases.Where(c => !c.Passed))
            builder.AppendLine($"  FAIL {item.Name} -> {item.Detail}");
        return builder.ToString();
    }
}

/// <summary>
/// Verifies capacity accounting and the retention preview. Every case builds its own isolated
/// versioned backup in a local directory; nothing here touches a real NAS or deletes user data.
/// </summary>
public sealed class CapacityTestSuite
{
    private readonly ExperimentalVersionedBackup _backup = new();
    private readonly BackupCapacityAnalyzer _analyzer = new();

    public async Task<CapacitySuiteReport> RunAsync(string workspace, CancellationToken token = default)
    {
        Directory.CreateDirectory(workspace);
        var cases = new List<CapacitySuiteCase>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var provider = new ExclusiveLocalProvider(workspace);

        async Task Case(string name, Func<Task<(bool Passed, string Detail)>> body)
        {
            try
            {
                var (passed, detail) = await body().ConfigureAwait(false);
                cases.Add(new CapacitySuiteCase(name, passed, detail));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { cases.Add(new CapacitySuiteCase(name, false, $"{ex.GetType().Name}: {ex.Message}")); }
        }

        async Task<(string target, string source)> PrepareAsync(string name, string content, CancellationToken t)
        {
            string target = name;
            string source = Path.Combine(workspace, name + "-src");
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "数据.txt"), content, Encoding.UTF8, t);
            await _backup.CreateNewAsync(provider, source, target, t);
            return (target, source);
        }

        await Case("首次备份后统计当前文件与物理对象", async () =>
        {
            var (target, _) = await PrepareAsync("cap-basic", "第一次备份内容", token);
            var report = await _analyzer.AnalyzeAsync(provider, target, token);
            bool ok = report.CurrentFileCount == 1 && report.CurrentLogicalBytes > 0 &&
                      report.PhysicalObjectCount >= 1 && report.PhysicalObjectBytes > 0 && report.VersionCount == 1 &&
                      report.Consistent && report.HistoryEntryCount == 0;
            string objectsPath = Path.Combine(workspace, target, ".fnsync", "objects");
            string diag = "";
            return (ok, report.ToSummary().Replace(Environment.NewLine, " | ") + " || " + diag);
        });

        await Case("修改文件后历史增加且版本数累加", async () =>
        {
            var (target, source) = await PrepareAsync("cap-history", "第一版内容", token);
            await File.WriteAllTextAsync(Path.Combine(source, "数据.txt"), "第二版内容", Encoding.UTF8, token);
            await _backup.RunAsync(provider, source, target, token);
            var report = await _analyzer.AnalyzeAsync(provider, target, token);
            bool ok = report.VersionCount == 2 && report.HistoryEntryCount == 1 &&
                      report.HistoryLogicalBytes > 0 && report.CurrentLogicalBytes > 0;
            return (ok, $"versions={report.VersionCount} history={report.HistoryEntryCount}");
        });

        await Case("未被引用的对象被报告但不会被删除", async () =>
        {
            var (target, _) = await PrepareAsync("cap-orphan", "孤立对象测试", token);
            string orphanDir = Path.Combine(workspace, target, ".fnsync", "objects", $"obj-{Guid.NewGuid():N}", "manual");
            Directory.CreateDirectory(orphanDir);
            string orphanFile = Path.Combine(orphanDir, "residue.bin");
            await File.WriteAllBytesAsync(orphanFile, new byte[4096], token);
            var report = await _analyzer.AnalyzeAsync(provider, target, token);
            bool reported = report.UnreferencedCount >= 1 && report.UnreferencedBytes >= 4096;
            var preview = await _analyzer.PreviewRetentionAsync(provider, target, new RetentionPolicy { KeepVersions = 1 }, token);
            bool preserved = File.Exists(orphanFile);
            return (reported && preserved, $"unreferenced={report.UnreferencedCount} preserved={preserved} preview={preview.Safe}");
        });

        await Case("空目标统计为零且不抛异常", async () =>
        {
            var report = await _analyzer.AnalyzeAsync(provider, "cap-empty", token);
            return (report.CurrentFileCount == 0 && report.VersionCount == 0 && report.PhysicalObjectCount == 0 &&
                    report.Notes.Count > 0, string.Join(" / ", report.Notes));
        });

        await Case("保留策略保留最新版本并标记更旧版本", async () =>
        {
            var (target, source) = await PrepareAsync("cap-retain", "v1", token);
            for (int i = 2; i <= 4; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(source, "数据.txt"), "v" + i, Encoding.UTF8, token);
                await _backup.RunAsync(provider, source, target, token);
            }
            var preview = await _analyzer.PreviewRetentionAsync(provider, target,
                new RetentionPolicy { KeepVersions = 2, KeepDays = 0 }, token);
            bool newestKept = preview.Versions.First().Keep;
            int kept = preview.Versions.Count(v => v.Keep);
            // Old objects stay referenced through the current manifest's history, so removing old
            // version records alone must NOT be reported as freeing space.
            bool ok = preview.Safe && newestKept && kept >= 1 && preview.Versions.Count == 4 &&
                      preview.Versions.Count(v => !v.Keep) == 2 && preview.ReclaimableBytes == 0 &&
                      preview.Summary.Contains("历史", StringComparison.Ordinal);
            return (ok, $"{preview.Summary} | kept={kept} versions={preview.Versions.Count}");
        });

        await Case("保留期内的版本不会被标记为可清理", async () =>
        {
            var (target, source) = await PrepareAsync("cap-window", "v1", token);
            await File.WriteAllTextAsync(Path.Combine(source, "数据.txt"), "v2", Encoding.UTF8, token);
            await _backup.RunAsync(provider, source, target, token);
            var preview = await _analyzer.PreviewRetentionAsync(provider, target,
                new RetentionPolicy { KeepVersions = 1, KeepDays = 30 }, token);
            bool ok = preview.Safe && preview.Versions.All(v => v.Keep) && preview.ReclaimableBytes == 0 &&
                      preview.Summary.Contains("没有可清理", StringComparison.Ordinal);
            return (ok, preview.Summary);
        });

        await Case("历史占用未超过上限时预览不提出清理", async () =>
        {
            var (target, source) = await PrepareAsync("cap-limit", "v1", token);
            await File.WriteAllTextAsync(Path.Combine(source, "数据.txt"), "v2", Encoding.UTF8, token);
            await _backup.RunAsync(provider, source, target, token);
            var preview = await _analyzer.PreviewRetentionAsync(provider, target,
                new RetentionPolicy { KeepVersions = 1, KeepDays = 0, MaxHistoryBytes = 1024 * 1024 * 1024 }, token);
            return (preview.Safe && preview.ReclaimableBytes == 0, preview.Summary);
        });

        await Case("提交记录损坏时预览拒绝给出删除建议", async () =>
        {
            var (target, source) = await PrepareAsync("cap-corrupt", "v1", token);
            await File.WriteAllTextAsync(Path.Combine(source, "数据.txt"), "v2", Encoding.UTF8, token);
            await _backup.RunAsync(provider, source, target, token);
            string versionDir = Path.Combine(workspace, target, ".fnsync", "manifest-versions");
            string receipt = Directory.GetFiles(versionDir, "commit-*.json").OrderBy(f => f).First();
            File.WriteAllText(receipt, "{ this is not json");
            var preview = await _analyzer.PreviewRetentionAsync(provider, target, new RetentionPolicy { KeepVersions = 1 }, token);
            bool ok = !preview.Safe && preview.Blockers.Count > 0 && preview.ReclaimableBytes == 0;
            return (ok, preview.Summary);
        });

        await Case("预览是只读的：重复执行对象数量和内容不变", async () =>
        {
            var (target, source) = await PrepareAsync("cap-readonly", "v1", token);
            await File.WriteAllTextAsync(Path.Combine(source, "数据.txt"), "v2", Encoding.UTF8, token);
            await _backup.RunAsync(provider, source, target, token);
            string objectsRoot = Path.Combine(workspace, target, ".fnsync", "objects");
            int before = Directory.GetFiles(objectsRoot, "*", SearchOption.AllDirectories).Length;
            string fingerprint = string.Join(",", Directory.GetFiles(objectsRoot, "*", SearchOption.AllDirectories)
                .OrderBy(f => f).Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length));
            await _analyzer.PreviewRetentionAsync(provider, target, new RetentionPolicy { KeepVersions = 1, KeepDays = 0 }, token);
            await _analyzer.PreviewRetentionAsync(provider, target, new RetentionPolicy { KeepVersions = 1, KeepDays = 0 }, token);
            int after = Directory.GetFiles(objectsRoot, "*", SearchOption.AllDirectories).Length;
            string afterFingerprint = string.Join(",", Directory.GetFiles(objectsRoot, "*", SearchOption.AllDirectories)
                .OrderBy(f => f).Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length));
            return (before == after && fingerprint == afterFingerprint, $"before={before} after={after}");
        });

        await Case("大文件统计按大小排序并标注历史来源", async () =>
        {
            var (target, source) = await PrepareAsync("cap-large", new string('A', 2000), token);
            await File.WriteAllTextAsync(Path.Combine(source, "数据.txt"), new string('A', 3000), Encoding.UTF8, token);
            await File.WriteAllTextAsync(Path.Combine(source, "大文件.bin"), new string('B', 80000), Encoding.UTF8, token);
            await _backup.RunAsync(provider, source, target, token);
            var report = await _analyzer.AnalyzeAsync(provider, target, token);
            bool ok = report.LargestFiles.Count >= 2 && report.LargestFiles[0].Size >= 80000 &&
                      report.LargestFiles.Any(f => f.OriginalRel == "数据.txt" && f.IsHistory);
            return (ok, string.Join(",", report.LargestFiles.Select(f => $"{f.OriginalRel}:{f.Size}:{f.IsHistory}")));
        });

        return new CapacitySuiteReport(cases, cases.Count(c => c.Passed), cases.Count(c => !c.Passed), clock.Elapsed);
    }
}

/// <summary>
/// Local mock NAS with exclusive directory creation. The SMB provider intentionally does not claim
/// atomic exclusivity, so tests use this subclass instead of weakening the product provider.
/// </summary>
internal sealed class ExclusiveLocalProvider : SmbStorageProvider, IExclusiveDirectoryProvider
{
    private readonly string _root;
    public ExclusiveLocalProvider(string root) : base(rootUncPath: root) => _root = root;

    public Task<ExclusiveDirectoryResult> TryCreateDirectoryExclusiveAsync(string relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Utils.PathHelper.IsSafeRelativePath(relativePath)) throw new ArgumentException("不安全的测试路径", nameof(relativePath));
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(full)) return Task.FromResult(ExclusiveDirectoryResult.AlreadyExists);
        Directory.CreateDirectory(full);
        return Task.FromResult(ExclusiveDirectoryResult.Created);
    }
}
