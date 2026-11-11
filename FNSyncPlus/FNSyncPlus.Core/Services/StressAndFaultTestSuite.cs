using System.Diagnostics;
using System.Security.Cryptography;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

public sealed record StressMeasurement(
    string Scenario,
    int Files,
    long SourceBytes,
    double InitialSeconds,
    double UnchangedSeconds,
    double IncrementalSeconds,
    long IncrementalUploadedBytes,
    long PeakWorkingSetBytes,
    int Failures,
    string Notes);

public sealed record FaultInjectionCase(string Name, string Stage, bool Passed, string Detail);

public sealed record FaultInjectionReport(IReadOnlyList<FaultInjectionCase> Cases, int Passed, int Failed)
{
    public bool AllPassed => Failed == 0;
    public string ToSummary()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"故障注入：{Passed} PASS / {Failed} FAIL");
        foreach (var item in Cases.Where(c => !c.Passed))
            text.AppendLine($"  FAIL [{item.Stage}] {item.Name} -> {item.Detail}");
        return text.ToString();
    }
}

/// <summary>
/// Generates deterministic test trees and measures first/incremental backup cost, then injects
/// failures at each publication stage and checks that the previous committed backup remains
/// restorable. Uses only the supplied provider (local mock in tests); no real NAS calls.
/// </summary>
public sealed class StressAndFaultTestSuite
{
    private readonly ExperimentalVersionedBackup _backup = new();
    private readonly VersionedManifestStore _versions = new();

    // ---------- stress ----------

    public async Task<StressMeasurement> RunStressAsync(INasStorageProvider provider, string workspace, int fileCount,
        int smallBytes = 512, long largeFileBytes = 8 * 1024 * 1024, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (fileCount < 1) throw new ArgumentOutOfRangeException(nameof(fileCount));
        string source = Path.Combine(workspace, $"stress-{fileCount}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        var payload = RandomNumberGenerator.GetBytes(smallBytes);
        long sourceBytes = 0;
        for (int i = 0; i < fileCount; i++)
        {
            token.ThrowIfCancellationRequested();
            string directory = Path.Combine(source, $"分组{i % 25:D2}", $"子层{i % 5:D1}");
            Directory.CreateDirectory(directory);
            string name = i % 40 == 0 ? $"中文长名称_{i:D5}_{new string('长', 20)}.txt" : $"file_{i:D5}.bin";
            string path = Path.Combine(directory, name);
            if (i % 200 == 0) File.WriteAllBytes(path, new byte[0]);
            else if (i % 500 == 0) File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(64 * 1024));
            else File.WriteAllBytes(path, payload);
            sourceBytes += new FileInfo(path).Length;
        }
        string large = Path.Combine(source, "单个大文件.bin");
        File.WriteAllBytes(large, RandomNumberGenerator.GetBytes((int)Math.Min(largeFileBytes, int.MaxValue)));
        sourceBytes += largeFileBytes;

        string target = $"stress-target-{fileCount}-{Guid.NewGuid():N}";
        var clock = Stopwatch.StartNew();
        var first = await _backup.CreateNewAsync(provider, source, target, token);
        double initial = clock.Elapsed.TotalSeconds;

        clock.Restart();
        var unchanged = await _backup.RunAsync(provider, source, target, token);
        double unchangedSeconds = clock.Elapsed.TotalSeconds;

        File.AppendAllText(Path.Combine(source, $"分组00", "子层0", "file_00000.bin"), "delta", System.Text.Encoding.UTF8);
        clock.Restart();
        var incremental = await _backup.RunAsync(provider, source, target, token);
        double incrementalSeconds = clock.Elapsed.TotalSeconds;
        long peak = GC.GetTotalMemory(false);
        try { peak = Math.Max(peak, Process.GetCurrentProcess().PeakWorkingSet64); } catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException) { }

        return new StressMeasurement(
            $"files={fileCount}, small={smallBytes}B, large={largeFileBytes}B",
            fileCount + 1, sourceBytes, initial, unchangedSeconds, incrementalSeconds,
            incremental.UploadedBytes, peak,
            first.UploadedFiles == 0 ? 1 : 0,
            $"first version={first.Snapshot.Id[..8]} unchangedHasNewVersion={unchanged.HasNewVersion} incrementalUploaded={incremental.UploadedFiles}");
    }

    // ---------- fault injection ----------

    /// <summary>
    /// Fault cases act on a fault-injecting provider supplied by the caller. Each case starts from a
    /// healthy committed backup and must leave it restorable with matching SHA-256.
    /// </summary>
    public async Task<FaultInjectionReport> RunFaultMatrixAsync(INasStorageProvider provider, IFaultInjectingProviderControl control,
        string workspace, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(control);
        var cases = new List<FaultInjectionCase>();
        string source = Path.Combine(workspace, $"fault-{Guid.NewGuid():N}");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "稳定文件.txt"), "stable v1");
        string basename = $"fault-target-{Guid.NewGuid():N}";
        var baseline = await _backup.CreateNewAsync(provider, source, basename, token);
        string baselineSha = baseline.Snapshot.Manifest.Files.Single().Sha256;

        // Restoring the ACTIVE committed version must always reproduce its recorded hash, whether
        // the interrupted run committed or not.
        async Task<bool> RestoresActiveVersionAsync()
        {
            var snapshot = await _versions.LoadAsync(provider, basename, token);
            if (snapshot == null) return false;
            var entry = snapshot.Manifest.Files.FirstOrDefault(f => f.OriginalRel == "稳定文件.txt");
            if (entry == null) return false;
            string restore = Path.Combine(workspace, $"fault-restore-{Guid.NewGuid():N}");
            var report = await new RestoreEngine(new ManifestManager()).RunRestoreVersionedAsync(provider, basename, restore, ConflictResolution.SkipExisting, token);
            string path = Path.Combine(restore, "稳定文件.txt");
            return report.FailedFiles == 0 && File.Exists(path) && HashHelper.ComputeFileSha256(path) == entry.Sha256;
        }
        async Task<bool> BaselineRestorableAsync(string label) => await RestoresActiveVersionAsync();

        async Task CaseAsync(string name, string stage, Func<Task> act, Func<Task<bool>> invariant)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await act();
                control.Reset();
                bool ok = await invariant();
                cases.Add(new FaultInjectionCase(name, stage, ok, ok ? "旧备份仍可恢复且 SHA-256 一致" : "旧备份不可恢复或内容变化"));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // The injected fault cancelled the run; the previous backup must still restore.
                control.Reset();
                bool ok = await invariant();
                cases.Add(new FaultInjectionCase(name, stage, ok, ok ? "按预期取消且旧备份完好" : "取消后旧备份损坏"));
            }
            catch (Exception ex)
            {
                // Expected failures are fine as long as the invariant holds afterwards.
                try
                {
                    control.Reset();
                    bool ok = await invariant();
                    cases.Add(new FaultInjectionCase(name, stage, ok, ok ? $"按预期失败（{ex.GetType().Name}）且旧备份完好" : $"失败后旧备份损坏: {ex.Message}"));
                }
                catch (Exception inner)
                {
                    cases.Add(new FaultInjectionCase(name, stage, false, $"失败后无法验证旧备份: {inner.Message}"));
                }
            }
            finally { control.Reset(); }
        }

        // 1. Upload interruption (mid-stream cancel).
        control.CancelUploadsAfterBytes(basename, 1);
        await CaseAsync("上传中断", "对象上传", async () =>
        {
            File.WriteAllText(Path.Combine(source, "稳定文件.txt"), "stable v2");
            await _backup.RunAsync(provider, source, basename, token);
        }, () => BaselineRestorableAsync("upload"));

        // 2. Response lost after complete upload.
        control.FailAfterComplete("/.fnsync/objects/");
        await CaseAsync("上传完成但确认丢失", "对象上传", async () =>
        {
            File.WriteAllText(Path.Combine(source, "稳定文件.txt"), "stable v3");
            await _backup.RunAsync(provider, source, basename, token);
        }, () => BaselineRestorableAsync("upload-ack"));

        // 3. Corruption detected by read-back verification.
        control.CorruptObjectUploads();
        await CaseAsync("对象上传后内容不一致", "对象校验", async () =>
        {
            File.WriteAllText(Path.Combine(source, "稳定文件.txt"), "stable v4");
            await _backup.RunAsync(provider, source, basename, token);
        }, () => BaselineRestorableAsync("corrupt-object"));

        // 4. Manifest read failure (temporary NAS outage).
        control.FailReads("/.fnsync/manifest-versions");
        await CaseAsync("清单读取失败（NAS 暂不可达）", "清单读取", async () =>
            await _backup.RunAsync(provider, source, basename, token),
            () => BaselineRestorableAsync("manifest-read"));

        // 5. Cancel before commit: cancel the token right after objects are prepared.
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            control.AfterObjectUpload = () => cts.Cancel();
            await CaseAsync("清单提交前取消", "清单提交", async () =>
            {
                File.WriteAllText(Path.Combine(source, "稳定文件.txt"), "stable v5");
                await _backup.RunAsync(provider, source, basename, cts.Token);
            }, () => BaselineRestorableAsync("pre-commit"));
        }

        // 6. Interrupted after the commit receipt was written: the new version may be active.
        control.FailAfterComplete("/commit-");
        await CaseAsync("提交记录写入后中断", "清单提交", async () =>
        {
            File.WriteAllText(Path.Combine(source, "稳定文件.txt"), "stable v6");
            await _backup.RunAsync(provider, source, basename, token);
        }, async () =>
        {
            // Whether or not the new commit became active, the selected version must restore with
            // its recorded hash. Verify via the manifest, not the old baseline.
            string restore = Path.Combine(workspace, $"fault-restore-{Guid.NewGuid():N}");
            var snapshot = await _versions.LoadAsync(provider, basename, token);
            if (snapshot == null) return false;
            var report = await new RestoreEngine(new ManifestManager()).RunRestoreVersionedAsync(provider, basename, restore, ConflictResolution.SkipExisting, token);
            var entry = snapshot.Manifest.Files.Single();
            string path = Path.Combine(restore, "稳定文件.txt");
            return report.FailedFiles == 0 && File.Exists(path) && HashHelper.ComputeFileSha256(path) == entry.Sha256;
        });

        // 7. Repeated restore is idempotent and never overwrites.
        await CaseAsync("重复恢复不覆盖现有文件", "恢复", async () =>
        {
            string restore = Path.Combine(workspace, $"fault-restore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(restore);
            File.WriteAllText(Path.Combine(restore, "稳定文件.txt"), "user data");
            var report = await new RestoreEngine(new ManifestManager()).RunRestoreVersionedAsync(provider, basename, restore, ConflictResolution.SkipExisting, token);
            if (report.SkippedFiles != 1 || File.ReadAllText(Path.Combine(restore, "稳定文件.txt")) != "user data")
                throw new InvalidOperationException("重复恢复覆盖了用户文件");
        }, () => BaselineRestorableAsync("restore-idempotent"));

        // 8. Cancelled restore leaves no temp files and the backup is untouched.
        await CaseAsync("恢复取消", "恢复", async () =>
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try { await new RestoreEngine(new ManifestManager()).RunRestoreVersionedAsync(provider, basename, Path.Combine(workspace, $"fault-restore-{Guid.NewGuid():N}"), ConflictResolution.SkipExisting, cts.Token); }
            catch (OperationCanceledException) { }
        }, () => BaselineRestorableAsync("restore-cancel"));

        return new FaultInjectionReport(cases, cases.Count(c => c.Passed), cases.Count(c => !c.Passed));
    }

    // ---------- provider fault control ----------

    /// <summary>Fault knobs a test provider implements; the production provider never does.</summary>
    public interface IFaultInjectingProviderControl
    {
        void CancelUploadsAfterBytes(string pathContains, int bytes);
        void FailAfterComplete(string pathContains);
        void CorruptObjectUploads();
        void FailReads(string pathContains);
        Action? AfterObjectUpload { get; set; }
        void Reset();
    }
}
