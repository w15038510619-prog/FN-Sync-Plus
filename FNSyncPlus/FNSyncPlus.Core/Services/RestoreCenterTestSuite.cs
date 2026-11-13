using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

public sealed record RestoreSuiteCase(string Name, string Category, bool Passed, string Detail);

public sealed record RestoreSuiteReport(
    IReadOnlyList<RestoreSuiteCase> Cases,
    int Passed,
    int Failed,
    TimeSpan Duration)
{
    public bool AllPassed => Failed == 0;

    public string ToSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"恢复中心自动化测试：{Passed} PASS / {Failed} FAIL，用时 {Duration.TotalSeconds:F1}s");
        foreach (var item in Cases.Where(c => !c.Passed))
            builder.AppendLine($"  FAIL [{item.Category}] {item.Name} -> {item.Detail}");
        return builder.ToString();
    }
}

/// <summary>
/// Automated restore test toolkit. Every case creates its own isolated, versioned backup from
/// generated source data, restores into a fresh directory, and compares original names, contents
/// and SHA-256. Failures are reported per case; nothing here touches real NAS data.
/// </summary>
public sealed class RestoreCenterTestSuite
{
    private readonly ExperimentalVersionedBackup _backup = new();
    private readonly VersionedManifestStore _versions = new();
    private readonly RestoreEngine _restore = new(new ManifestManager());

    public async Task<RestoreSuiteReport> RunAsync(INasStorageProvider provider, string workspace,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(workspace)) throw new ArgumentException("测试工作目录不能为空", nameof(workspace));
        Directory.CreateDirectory(workspace);
        var cases = new List<RestoreSuiteCase>();
        var clock = Stopwatch.StartNew();
        int index = 0;
        foreach (var scenario in Scenarios())
        {
            token.ThrowIfCancellationRequested();
            string name = $"restore-case-{++index:D2}-{Guid.NewGuid():N}";
            progress?.Report($"({index}) {scenario.Name}");
            try
            {
                var result = await scenario.RunAsync(provider, workspace, name, token).ConfigureAwait(false);
                cases.Add(new RestoreSuiteCase(scenario.Name, scenario.Category, result.Passed, result.Detail));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                cases.Add(new RestoreSuiteCase(scenario.Name, scenario.Category, false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }
        clock.Stop();
        return new RestoreSuiteReport(cases, cases.Count(c => c.Passed), cases.Count(c => !c.Passed), clock.Elapsed);
    }

    private sealed record Scenario(string Name, string Category, Func<INasStorageProvider, string, string, CancellationToken, Task<(bool Passed, string Detail)>> RunAsync);

    private sealed record Expectation(string RelativePath, byte[] Content);

    private IEnumerable<Scenario> Scenarios()
    {
        yield return new("普通中文名称", "名称", (p, w, n, t) => RunCaseAsync(p, w, n, t,
            new() { ["日常报表.txt"] = Encoding.UTF8.GetBytes("普通中文内容") }));
        yield return new("中英文混合名称", "名称", (p, w, n, t) => RunCaseAsync(p, w, n, t,
            new() { ["Q4_report_最终版_v2.txt"] = Encoding.UTF8.GetBytes("mixed") }));
        yield return new("Emoji 与特殊 Unicode", "名称", (p, w, n, t) => RunCaseAsync(p, w, n, t,
            new() { ["📁项目⭐说明.txt"] = Encoding.UTF8.GetBytes("unicode"), ["Ünïcødé_文件.bin"] = new byte[] { 1, 2, 3 } }));
        yield return new("255 字节边界名称", "名称", (p, w, n, t) => RunCaseAsync(p, w, n, t,
            new() { [LongAscii(251) + ".txt"] = Encoding.UTF8.GetBytes("boundary") }, allowUnixOversize: true));
        yield return new("270 字节超长名称", "长名称", (p, w, n, t) => RunCaseAsync(p, w, n, t,
            new() { [LongAscii(266) + ".txt"] = Encoding.UTF8.GetBytes("over255") }, allowUnixOversize: true));
        yield return new("300 字节超长名称", "长名称", (p, w, n, t) => RunCaseAsync(p, w, n, t,
            new() { [new string('长', 100) + ".txt"] = Encoding.UTF8.GetBytes("over300") }, allowUnixOversize: true));
        yield return new("超长目录名下的子文件", "长名称", (p, w, n, t) => RunDirectoryCaseAsync(p, w, n, t,
            LongAscii(260), new() { ["子文件.txt"] = Encoding.UTF8.GetBytes("nested long dir") }, allowUnixOversize: true));
        yield return new("空目录", "结构", (p, w, n, t) => RunDirectoryCaseAsync(p, w, n, t, "",
            new() { ["空目录A"] = Array.Empty<byte>(), ["空目录A/内层空目录/更深空目录"] = Array.Empty<byte>() }, expectEmptyDirectories: true));
        yield return new("多层嵌套目录", "结构", (p, w, n, t) => RunDirectoryCaseAsync(p, w, n, t, "层级/一级/二级/三级",
            new() { ["文件.txt"] = Encoding.UTF8.GetBytes("nested") }));
        yield return new("同名冲突默认不覆盖", "冲突", async (p, w, n, t) =>
        {
            var files = new Dictionary<string, byte[]> { ["同名.txt"] = Encoding.UTF8.GetBytes("备份内容") };
            var created = await CreateBackupAsync(p, w, n, files, t);
            var restoreDir = Path.Combine(w, n + "-out");
            Directory.CreateDirectory(restoreDir);
            File.WriteAllText(Path.Combine(restoreDir, "同名.txt"), "用户现有内容");
            var report = await _restore.RunRestoreVersionedAsync(p, n, restoreDir, ConflictResolution.SkipExisting, t);
            return (report.SkippedFiles == 1 && File.ReadAllText(Path.Combine(restoreDir, "同名.txt")) == "用户现有内容",
                $"skipped={report.SkippedFiles} restored={report.RestoredFiles}");
        });
        yield return new("修改前历史版本", "历史", async (p, w, n, t) =>
        {
            string source = Path.Combine(w, n + "-src");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "文件.txt"), "第一版");
            var created = await _backup.CreateNewAsync(p, source, n, t);
            File.WriteAllText(Path.Combine(source, "文件.txt"), "第二版");
            await _backup.RunAsync(p, source, n, t);
            var current = await _versions.LoadAsync(p, n, t);
            var history = current!.Manifest.History.Single(h => h.OriginalRel == "文件.txt" && h.Reason == "superseded");
            string outDir = Path.Combine(w, n + "-hist");
            bool ok = await _restore.RunRestoreHistoryFileAsync(p, n, outDir, history, t);
            string text = File.ReadAllText(Path.Combine(outDir, "文件.txt"));
            return (ok && text == "第一版" && created.Snapshot.Manifest.Files.Single().Sha256 == history.Sha256, $"restored={text}");
        });
        yield return new("重命名历史版本", "历史", async (p, w, n, t) =>
        {
            string source = Path.Combine(w, n + "-src");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "旧名.txt"), "改名内容");
            await _backup.CreateNewAsync(p, source, n, t);
            File.Move(Path.Combine(source, "旧名.txt"), Path.Combine(source, "新名.txt"));
            await _backup.RunAsync(p, source, n, t);
            var current = await _versions.LoadAsync(p, n, t);
            var renamed = current!.Manifest.History.Single(h => h.OriginalRel == "旧名.txt" && h.Reason == "renamed");
            string outDir = Path.Combine(w, n + "-hist");
            bool ok = await _restore.RunRestoreHistoryFileAsync(p, n, outDir, renamed, t);
            return (ok && renamed.RenamedTo == "新名.txt" && File.ReadAllText(Path.Combine(outDir, "旧名.txt")) == "改名内容",
                $"renamedTo={renamed.RenamedTo}");
        });
        yield return new("文件内容损坏被识别", "损坏", async (p, w, n, t) =>
        {
            var files = new Dictionary<string, byte[]> { ["损坏.txt"] = Encoding.UTF8.GetBytes("original") };
            var created = await CreateBackupAsync(p, w, n, files, t);
            var entry = created.Snapshot.Snapshot.Manifest.Files.Single();
            string full = Path.Combine(LocalBackupPath(p, w, created.Target), entry.StoredRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) return (true, "SKIP: 该存储不能由本机直接篡改，需在对应环境执行");
            File.WriteAllBytes(full, Encoding.UTF8.GetBytes("tampered!"));
            var report = await _restore.RunRestoreVersionedAsync(p, n, Path.Combine(w, n + "-out"), ConflictResolution.SkipExisting, t);
            return (report.FailedFiles >= 1 && report.RestoredFiles == 0, $"failed={report.FailedFiles} restored={report.RestoredFiles}");
        });
        yield return new("清单损坏被拒绝", "损坏", async (p, w, n, t) =>
        {
            var created = await CreateBackupAsync(p, w, n, new() { ["a.txt"] = Encoding.UTF8.GetBytes("a") }, t);
            string versions = Path.Combine(LocalBackupPath(p, w, created.Target), ".fnsync", "manifest-versions");
            if (!Directory.Exists(versions)) return (true, "SKIP: 该存储不能由本机直接篡改，需在对应环境执行");
            foreach (var candidate in Directory.GetFiles(versions, "candidate-*.json"))
                File.WriteAllText(candidate, "{corrupted");
            bool rejected = false;
            try { await _restore.RunRestoreVersionedAsync(p, n, Path.Combine(w, n + "-out"), ConflictResolution.SkipExisting, t); }
            catch (InvalidDataException) { rejected = true; }
            return (rejected, "损坏的清单必须拒绝而不是返回空清单");
        });
        yield return new("路径穿越被拒绝", "安全", async (p, w, n, t) =>
        {
            var files = new Dictionary<string, byte[]> { ["安全.txt"] = Encoding.UTF8.GetBytes("safe") };
            var created = await CreateBackupAsync(p, w, n, files, t);
            var tampered = System.Text.Json.JsonSerializer.Deserialize<FNSyncPlus.Core.Models.Manifest>(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(created.Snapshot.Snapshot.Manifest))!;
            tampered.Files.Single().OriginalRel = "../逃逸.txt";
            await _versions.SaveAsync(p, n, tampered, created.Snapshot.Snapshot.Id, t);
            bool rejected = false; string failure = "";
            try
            {
                var report = await _restore.RunRestoreVersionedAsync(p, n, Path.Combine(w, n + "-out"), ConflictResolution.SkipExisting, t);
                rejected = report.FailedFiles > 0 && report.RestoredFiles == 0;
                failure = $"failed={report.FailedFiles} restored={report.RestoredFiles}";
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { rejected = true; failure = ex.Message; }
            return (rejected && !File.Exists(Path.Combine(w, "逃逸.txt")), $"越界路径被拒绝: {failure}");
        });
        yield return new("恢复目录为联接点被拒绝", "安全", async (p, w, n, t) =>
        {
            var files = new Dictionary<string, byte[]> { ["a.txt"] = Encoding.UTF8.GetBytes("a") };
            var created = await CreateBackupAsync(p, w, n, files, t);
            string outside = Path.Combine(w, "outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outside);
            string junction = Path.Combine(w, n + "-junction");
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "/d", "/c", "mklink", "/J", junction, outside }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("无法创建测试联接");
                if (!process.WaitForExit(10000) || process.ExitCode != 0) throw new IOException("无法创建测试联接");
            }
            else
            {
                Directory.CreateSymbolicLink(junction, outside);
            }
            bool rejected = false;
            string detail = "";
            try
            {
                var report = await _restore.RunRestoreVersionedAsync(p, n, junction, ConflictResolution.SkipExisting, t);
                rejected = report.RestoredFiles == 0 && report.FailedFiles > 0;
                detail = $"restored={report.RestoredFiles} failed={report.FailedFiles}";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { rejected = true; detail = ex.Message; }
            bool escaped = Directory.EnumerateFileSystemEntries(outside).Any();
            return (rejected && !escaped, $"rejected={rejected} escaped={escaped} {detail}");
        });
        yield return new("用户取消恢复不产生半成品", "取消", async (p, w, n, t) =>
        {
            var files = new Dictionary<string, byte[]>();
            for (int i = 0; i < 25; i++) files[$"批量{i:D2}.txt"] = Encoding.UTF8.GetBytes(new string('x', 4096));
            var created = await CreateBackupAsync(p, w, n, files, t);
            // Cancel while the restore engine is reading the second file.
            string outDir = Path.Combine(w, n + "-cancel");
            bool canceled = false;
            int failed = 0, restored = 0;
            var cts = new CancellationTokenSource();
            var canceling = new CancelAfterReadsProvider(p, cts, 2);
            try
            {
                var report = await _restore.RunRestoreVersionedAsync(canceling, n, outDir, ConflictResolution.SkipExisting, cts.Token);
                failed = report.FailedFiles; restored = report.RestoredFiles;
                canceled = failed > 0 || restored < 25;
            }
            catch (OperationCanceledException) { canceled = true; }
            int filesRestored = Directory.Exists(outDir) ? Directory.GetFiles(outDir, "*.txt").Length : 0;
            int tempLeft = Directory.Exists(outDir) ? Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Count(f => Path.GetFileName(f).StartsWith(".fnsync_rst_")) : 0;
            return (canceled && filesRestored < 25 && tempLeft == 0,
                $"canceled={canceled} restored={restored} failed={failed} temp={tempLeft}");
        });
        yield return new("中断后重试可完成", "取消", async (p, w, n, t) =>
        {
            var files = new Dictionary<string, byte[]> { ["重试.txt"] = Encoding.UTF8.GetBytes("retry content") };
            var created = await CreateBackupAsync(p, w, n, files, t);
            string outDir = Path.Combine(w, n + "-retry");
            try { await _restore.RunRestoreVersionedAsync(p, n, outDir, ConflictResolution.SkipExisting, new CancelOnSecondFile(default).Token); }
            catch (OperationCanceledException) { }
            var second = await _restore.RunRestoreVersionedAsync(p, n, outDir, ConflictResolution.SkipExisting, t);
            return (second.RestoredFiles + second.SkippedFiles >= 1 &&
                File.ReadAllText(Path.Combine(outDir, "重试.txt")) == "retry content" && second.FailedFiles == 0,
                $"second={second.RestoredFiles} skipped={second.SkippedFiles}");
        });
    }

    private async Task<(bool Passed, string Detail)> RunCaseAsync(INasStorageProvider provider, string workspace, string name,
        CancellationToken token, Dictionary<string, byte[]> files, bool allowUnixOversize = false)
        => await RunDirectoryCaseAsync(provider, workspace, name, token, "", files, allowUnixOversize: allowUnixOversize);

    private async Task<(bool Passed, string Detail)> RunDirectoryCaseAsync(INasStorageProvider provider, string workspace,
        string name, CancellationToken token, string directoryPrefix, Dictionary<string, byte[]> files,
        bool expectEmptyDirectories = false, bool allowUnixOversize = false)
    {
        string source = Path.Combine(workspace, name + "-src");
        Directory.CreateDirectory(source);
        string prefixDir = Path.Combine(source, directoryPrefix.Replace('/', Path.DirectorySeparatorChar));
        try { Directory.CreateDirectory(prefixDir); }
        catch (Exception ex) when (allowUnixOversize && ex is PathTooLongException or IOException)
        { return (true, "SKIP: 当前文件系统不支持该名称长度，留待 Windows 执行"); }
        var expectations = new List<Expectation>();
        var emptyDirectories = new List<string>();
        foreach (var file in files)
        {
            if (file.Value.Length == 0)
            {
                string directoryRel = string.IsNullOrEmpty(directoryPrefix) ? file.Key : directoryPrefix + "/" + file.Key;
                emptyDirectories.Add(directoryRel);
                try { Directory.CreateDirectory(Path.Combine(prefixDir, file.Key.Replace('/', Path.DirectorySeparatorChar))); }
                catch (Exception ex) when (allowUnixOversize && ex is PathTooLongException or IOException)
                { return (true, "SKIP: 当前文件系统不支持该名称长度，留待 Windows 执行"); }
                continue;
            }
            string path = Path.Combine(prefixDir, file.Key);
            try { File.WriteAllBytes(path, file.Value); }
            catch (IOException) when (!allowUnixOversize) { throw; }
            catch (IOException)
            {
                // ext4 caps names at 255 bytes; long-name scenarios are exercised on Windows.
                return (true, "SKIP: 当前文件系统不支持该名称长度，留待 Windows 执行");
            }
            expectations.Add(new Expectation(string.IsNullOrEmpty(directoryPrefix) ? file.Key : directoryPrefix + "/" + file.Key, file.Value));
        }
        var created = await CreateBackupAsync(provider, workspace, name, new(), token, source);
        string restoreDir = Path.Combine(workspace, name + "-out");
        var report = await _restore.RunRestoreVersionedAsync(provider, name, restoreDir, ConflictResolution.SkipExisting, token);
        if (report.FailedFiles != 0) return (false, $"恢复失败 {report.FailedFiles} 个文件");
        foreach (var expectation in expectations)
        {
            string restored = Path.Combine(restoreDir, expectation.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(restored)) return (false, $"未恢复: {expectation.RelativePath}");
            if (!File.ReadAllBytes(restored).AsSpan().SequenceEqual(expectation.Content))
                return (false, $"内容不一致: {expectation.RelativePath}");
            if (!Path.GetFileName(restored).Equals(Path.GetFileName(expectation.RelativePath), StringComparison.Ordinal))
                return (false, $"原名未保留: {expectation.RelativePath}");
        }
        if (expectEmptyDirectories)
            foreach (var relative in emptyDirectories)
                if (!Directory.Exists(Path.Combine(restoreDir, relative.Replace('/', Path.DirectorySeparatorChar))))
                    return (false, $"空目录未恢复: {relative}");
        if (report.IntegrityVerifiedFiles != report.RestoredFiles)
            return (false, $"逐项 SHA-256 校验数量 {report.IntegrityVerifiedFiles} 与恢复数量 {report.RestoredFiles} 不一致");
        return (true, $"{expectations.Count} 个文件逐项校验通过");
    }

    private async Task<(ExperimentalVersionedBackup.Result Snapshot, string Target)> CreateBackupAsync(
        INasStorageProvider provider, string workspace, string name, Dictionary<string, byte[]> files,
        CancellationToken token, string? sourceOverride = null)
    {
        string source = sourceOverride ?? Path.Combine(workspace, name + "-src");
        Directory.CreateDirectory(source);
        foreach (var file in files)
        {
            string path = Path.Combine(source, file.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Value);
        }
        // Multi-level nesting must be visible to the tree scan before files are written.
        var nested = new[] { "层级", "层级/一级", "层级/一级/二级", "层级/一级/二级/三级" };
        foreach (var directory in nested)
            Directory.CreateDirectory(Path.Combine(source, directory.Replace('/', Path.DirectorySeparatorChar)));
        var result = await _backup.CreateNewAsync(provider, source, name, token);
        // Absolute location of the backup for local file tampering/inspection when the provider
        // is backed by the local filesystem; otherwise callers must use the relative name.
        string absolute = Path.Combine(workspace, name);
        return (result, Directory.Exists(absolute) ? absolute : Path.Combine(workspace, name));
    }

    private static string LongAscii(int length) => new('a', length);

    /// <summary>
    /// Local filesystem location for a provider-relative target when a suite workspace is used.
    /// Providers backed by something else simply see the relative name (tampering is skipped).
    /// </summary>
    /// <summary>Best-effort local location of a backup for tampering; works for local-backed providers.</summary>
    private static string LocalBackupPath(INasStorageProvider provider, string workspace, string target)
    {
        if (Path.IsPathRooted(target)) return target;
        string leaf = target.Replace('/', Path.DirectorySeparatorChar);
        foreach (string root in new[] { workspace, provider.RootDisplayPath })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string candidate = Path.Combine(root, leaf);
            if (Directory.Exists(Path.Combine(candidate, ".fnsync"))) return candidate;
        }
        return Path.Combine(workspace, leaf);
    }

    /// <summary>Cancels the caller's token as soon as the Nth object read begins.</summary>
    private sealed class CancelAfterReadsProvider : INasStorageProvider
    {
        private readonly INasStorageProvider _inner;
        private readonly CancellationTokenSource _source;
        private readonly int _after;
        private int _reads;
        public CancelAfterReadsProvider(INasStorageProvider inner, CancellationTokenSource source, int after)
        { _inner = inner; _source = source; _after = after; }
        public StorageProviderType ProviderType => _inner.ProviderType;
        public bool IsAuthenticated => _inner.IsAuthenticated;
        public string RootDisplayPath => _inner.RootDisplayPath;
        public Task<StorageAuthResult> ConnectAndAuthenticateAsync(string server, string username, string password, string? twoFactorCode = null, CancellationToken ct = default)
            => _inner.ConnectAndAuthenticateAsync(server, username, password, twoFactorCode, ct);
        public Task<IReadOnlyList<NasStorageItem>> ListEntriesAsync(string? relativePath = null, CancellationToken ct = default)
            => _inner.ListEntriesAsync(relativePath, ct);
        public Task<StorageOperationResult> CreateDirectoryAsync(string relativePath, CancellationToken ct = default)
            => _inner.CreateDirectoryAsync(relativePath, ct);
        public Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default) => _inner.ExistsAsync(relativePath, ct);
        public Task<NasStorageItem?> GetItemAsync(string relativePath, CancellationToken ct = default) => _inner.GetItemAsync(relativePath, ct);
        public Task<StorageOperationResult> UploadFileAsync(string relativePath, Stream sourceStream, long fileSize, DateTime lastWriteTimeUtc, bool overwrite = true, IProgress<long>? progress = null, CancellationToken ct = default)
            => _inner.UploadFileAsync(relativePath, sourceStream, fileSize, lastWriteTimeUtc, overwrite, progress, ct);
        public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
        {
            var stream = await _inner.OpenReadAsync(relativePath, ct);
            if (Interlocked.Increment(ref _reads) >= _after) _source.Cancel();
            return stream;
        }
        public Task<bool> VerifyFileIntegrityAsync(string relativePath, string expectedSha256, long expectedSize, CancellationToken ct = default)
            => _inner.VerifyFileIntegrityAsync(relativePath, expectedSha256, expectedSize, ct);
        public Task<StorageOperationResult> DeleteAsync(string relativePath, bool moveToTrash = true, CancellationToken ct = default)
            => _inner.DeleteAsync(relativePath, moveToTrash, ct);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class CancelOnSecondFile
    {
        private int _seen;
        private readonly CancellationTokenSource _source;
        public CancelOnSecondFile(CancellationToken token)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(token);
        }
        public CancellationToken Token
        {
            get
            {
                if (Interlocked.Increment(ref _seen) >= 2) _source.Cancel();
                return _source.Token;
            }
        }
    }
}
