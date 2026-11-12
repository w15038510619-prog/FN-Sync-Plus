using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Regression tests for the maintenance-hold lifecycle (P0-1).
///
/// These assert on real scheduling behaviour, not on a boolean: a hold that is never released used
/// to leave automatic backups silently paused while the program kept running, so every case here
/// checks that a real queued task actually runs (or actually stays queued) afterwards.
/// </summary>
public sealed class MaintenanceLifecycleTestSuite
{
    public async Task<IReadOnlyList<UpdateSuiteCase>> RunAsync(string workspace, CancellationToken token = default)
    {
        Directory.CreateDirectory(workspace);
        var cases = new List<UpdateSuiteCase>();
        var (_, publicPem) = UpdateManifestSigner.CreateKeyPair();
        var testVerifier = UpdateManifestVerifier.FromPem(publicPem);

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

        await Case("首次进入维护状态获得租约，重复进入被拒绝且不抢占", async () =>
        {
            using var harness = new ManagerHarness(Path.Combine(workspace, "hold-once"));
            bool first = harness.Manager.TryEnterMaintenanceHold(out MaintenanceLease lease, "测试一");
            bool second = harness.Manager.TryEnterMaintenanceHold(out MaintenanceLease? second2, "测试二");
            bool active = harness.Manager.IsMaintenanceHoldActive;
            lease.Release();
            bool releasedAfter = !harness.Manager.IsMaintenanceHoldActive;
            bool secondIsNull = second2 == null;
            await Task.CompletedTask;
            return (first && !second && secondIsNull && active && releasedAfter,
                $"first={first} second={second} secondLeaseNull={secondIsNull} active={active} released={releasedAfter}");
        });

        await Case("租约释放幂等，且过期的租约不能解除他人的维护状态", async () =>
        {
            using var harness = new ManagerHarness(Path.Combine(workspace, "hold-idempotent"));
            harness.Manager.TryEnterMaintenanceHold(out MaintenanceLease first, "第一");
            bool firstRelease = first.Release();
            bool secondRelease = first.Release();
            // A different owner takes a new hold; the stale lease must not clear it.
            harness.Manager.TryEnterMaintenanceHold(out MaintenanceLease current, "第二");
            bool staleRelease = first.Release();
            bool stillHeld = harness.Manager.IsMaintenanceHoldActive;
            current.Release();
            await Task.CompletedTask;
            return (firstRelease && !secondRelease && !staleRelease && stillHeld,
                $"first={firstRelease} duplicate={secondRelease} stale={staleRelease} stillHeld={stillHeld}");
        });

        await Case("备份运行期间先阻断新任务，任务结束后才授予租约", async () =>
        {
            using var harness = new ManagerHarness(Path.Combine(workspace, "hold-wait"));
            var running = harness.StartLongTask();
            var gate = harness.CreateGate();
            var attempt = gate.TryAcquireAsync(TimeSpan.FromSeconds(10), token);
            await Task.Delay(300, token);
            bool blockedWhileRunning = harness.Manager.IsMaintenanceHoldActive && !attempt.IsCompleted;
            var second = harness.StartQueuedTask();
            await Task.Delay(300, token);
            bool secondStayedQueued = second.State is TaskSyncState.Queued or TaskSyncState.Paused &&
                                      !File.Exists(harness.DestinationFile("second.txt"));
            harness.ReleaseLongTask();
            var result = await attempt.ConfigureAwait(false);
            bool granted = result.Granted;
            result.Lease?.Release();
            await harness.WaitForCompletionAsync(second, TimeSpan.FromSeconds(10));
            return (blockedWhileRunning && secondStayedQueued && granted,
                $"blocked={blockedWhileRunning} queued={secondStayedQueued}/{second.State} granted={granted}");
        });

        await Case("等待备份结束超时后租约被释放，队列恢复调度", async () =>
        {
            using var harness = new ManagerHarness(Path.Combine(workspace, "hold-timeout"));
            var running = harness.StartLongTask();
            var gate = harness.CreateGate();
            var attempt = await gate.TryAcquireAsync(TimeSpan.FromMilliseconds(300), token).ConfigureAwait(false);
            bool deferred = attempt.Readiness.Decision == InstallDecision.Defer && attempt.Lease == null;
            bool released = !harness.Manager.IsMaintenanceHoldActive;
            harness.ReleaseLongTask();
            var after = harness.StartQueuedTask();
            bool recovered = await harness.WaitForCompletionAsync(after, TimeSpan.FromSeconds(10));
            return (deferred && released && recovered,
                $"deferred={deferred} released={released} recovered={recovered}/{after.State} " +
                $"runningWork={harness.Manager.HasRunningWork} queued={harness.Manager.HasRunningOrQueuedWork} " +
                $"longState={running.State} {attempt.Readiness.Reason}");
        });

        await Case("用户取消后租约释放且队列恢复", async () =>
        {
            using var harness = new ManagerHarness(Path.Combine(workspace, "hold-cancel"));
            var running = harness.StartLongTask();
            using var cts = new CancellationTokenSource();
            var gate = harness.CreateGate();
            var attempt = gate.TryAcquireAsync(TimeSpan.FromMinutes(5), cts.Token);
            await Task.Delay(250, token);
            cts.Cancel();
            var result = await attempt.ConfigureAwait(false);
            bool released = !harness.Manager.IsMaintenanceHoldActive && result.Lease == null;
            harness.ReleaseLongTask();
            var after = harness.StartQueuedTask();
            bool recovered = await harness.WaitForCompletionAsync(after, TimeSpan.FromSeconds(10));
            return (released && recovered, $"released={released} recovered={recovered} {result.Readiness.Reason}");
        });

        await Case("保存配置失败时取消安装并释放租约", async () =>
        {
            using var harness = new ManagerHarness(Path.Combine(workspace, "hold-savefail"));
            var gate = new BackupActivityGate(() => false, () => false, () => false,
                () => harness.Manager.TryEnterMaintenanceHold(out MaintenanceLease l, "测试") ? l : null,
                () => throw new IOException("磁盘已满"));
            var attempt = await gate.TryAcquireAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            bool refused = attempt.Readiness.Decision == InstallDecision.Refuse && attempt.Lease == null;
            bool released = !harness.Manager.IsMaintenanceHoldActive;
            var after = harness.StartQueuedTask();
            bool recovered = await harness.WaitForCompletionAsync(after, TimeSpan.FromSeconds(10));
            return (refused && released && recovered,
                $"refused={refused} released={released} recovered={recovered} {attempt.Readiness.Reason}");
        });

        await Case("发现不明确的锁时不取维护状态", async () =>
        {
            using var harness = new ManagerHarness(Path.Combine(workspace, "hold-unclear"));
            bool entered = false;
            var gate = new BackupActivityGate(() => false, () => false, () => true,
                () => { entered = true; return harness.Manager.TryEnterMaintenanceHold(out MaintenanceLease l, "测试") ? l : null; },
                () => Task.CompletedTask);
            var attempt = await gate.TryAcquireAsync(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            await Task.CompletedTask;
            return (!attempt.Granted && !entered && !harness.Manager.IsMaintenanceHoldActive,
                $"granted={attempt.Granted} entered={entered} {attempt.Readiness.Reason}");
        });

        await Case("维护状态期间新任务保持排队，不会启动写入", async () =>
        {
            using var harness = new ManagerHarness(Path.Combine(workspace, "hold-queue"));
            harness.Manager.TryEnterMaintenanceHold(out MaintenanceLease lease, "测试");
            var queued = harness.StartQueuedTask();
            await Task.Delay(700, token);
            bool notStarted = !File.Exists(harness.DestinationFile("second.txt")) &&
                              queued.State != TaskSyncState.Completed;
            lease.Release();
            bool ran = await harness.WaitForCompletionAsync(queued, TimeSpan.FromSeconds(15));
            return (notStarted && ran, $"notStarted={notStarted} state={queued.State} ran={ran}");
        });

        await Case("交接完成前不释放租约，主程序未退出时看门狗恢复备份", async () =>
        {
            using var harness = new ManagerHarness(Path.Combine(workspace, "hold-watchdog"));
            harness.Manager.TryEnterMaintenanceHold(out MaintenanceLease lease, "测试");
            var session = new UpdateInstallSession(new InstallReadiness(InstallDecision.Proceed, "ok"),
                new UpdateInstallPlan(true, "x", "y", "ok"), null, lease, null);
            session.CommitHandOff(TimeSpan.FromMilliseconds(300));
            bool heldDuringHandoff = session.HoldsQueue && harness.Manager.IsMaintenanceHoldActive;
            await Task.Delay(900, token);
            bool releasedByWatchdog = !harness.Manager.IsMaintenanceHoldActive && session.HoldReleased;
            var after = harness.StartQueuedTask();
            bool recovered = await harness.WaitForCompletionAsync(after, TimeSpan.FromSeconds(10));
            return (heldDuringHandoff && releasedByWatchdog && recovered,
                $"held={heldDuringHandoff} watchdog={releasedByWatchdog} recovered={recovered}");
        });

        await Case("更新器文件缺失时安装会话不持有租约，备份继续", async () =>
        {
            using var harness = new ManagerHarness(Path.Combine(workspace, "hold-noupdater"));
            string root = harness.CreateManagedLayout(out string appDir, createUpdater: false);
            var service = harness.CreateService(appDir, testVerifier);
            var release = harness.ReleaseFor(root, "0.5.0");
            using var session = await service.BeginInstallAsync(release, TimeSpan.FromSeconds(3), token)
                .ConfigureAwait(false);
            bool refused = !session.Plan.Ready && !session.HoldsQueue && session.HoldReleased;
            bool notHolding = !harness.Manager.IsMaintenanceHoldActive;
            var after = harness.StartQueuedTask();
            bool recovered = await harness.WaitForCompletionAsync(after, TimeSpan.FromSeconds(10));
            return (refused && notHolding && recovered,
                $"refused={refused} notHolding={notHolding} recovered={recovered} {session.Readiness.Reason}");
        });

        await Case("更新器无法启动时会话中止并恢复备份", async () =>
        {
            using var harness = new ManagerHarness(Path.Combine(workspace, "hold-startfail"));
            string root = harness.CreateManagedLayout(out string appDir, updaterIsBroken: true);
            var service = harness.CreateService(appDir, testVerifier);
            var release = harness.ReleaseFor(root, "0.5.0");
            using var session = await service.BeginInstallAsync(release, TimeSpan.FromSeconds(3), token)
                .ConfigureAwait(false);
            bool prepared = session.Plan.Ready && session.HoldsQueue;
            bool started = service.TryStartUpdater(session, TimeSpan.FromSeconds(5), out string error);
            bool aborted = !started && session.IsAborted && !harness.Manager.IsMaintenanceHoldActive;
            var after = harness.StartQueuedTask();
            bool recovered = await harness.WaitForCompletionAsync(after, TimeSpan.FromSeconds(10));
            return (prepared && aborted && recovered,
                $"prepared={prepared} started={started} aborted={aborted} recovered={recovered} {error}");
        });

        return cases;
    }

    /// <summary>Real TaskManager over a local mock NAS, plus helpers to queue and observe work.</summary>
    private sealed class ManagerHarness : IDisposable
    {
        private readonly string _root;
        private readonly TaskCompletionSource _releaseLong = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly LogService _log;
        private int _counter;

        public ManagerHarness(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "source");
            string target = Path.Combine(root, "target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "seed.txt"), "seed");

            Source = source;
            Target = target;
            Settings = AppSettings.CreateInMemory();
            _log = new LogService(Path.Combine(root, "logs"));
            Provider = new SlowLocalProvider(target);
            Manager = new TaskManager(Settings, new SmbAuthService(), _log)
            {
                StorageProviderSupplier = () => Provider
            };
        }

        public string Source { get; }
        public string Target { get; }
        public AppSettings Settings { get; }
        public TaskManager Manager { get; }
        public SlowLocalProvider Provider { get; }

        public string DestinationFile(string name) => Path.Combine(Target, name);

        private SyncTask NewTask(string name, bool blockOnGate = false)
        {
            var task = new SyncTask
            {
                TaskName = name,
                SourceFolder = Source,
                // Rooted remote-style path: a bare relative name was resolved against the process
                // working directory and littered the repository root, while a local absolute path is
                // rejected as an invalid remote folder.
                DestinationFolder = $"/pilot/dest-{Interlocked.Increment(ref _counter)}",
                ProviderType = StorageProviderType.FnOsPersonal,
                ScheduleMode = TaskSyncScheduleMode.ManualOnly
            };
            Settings.Tasks.Add(task);
            if (blockOnGate) Provider.Gate = _releaseLong.Task;
            return task;
        }

        public SyncTask StartLongTask()
        {
            var task = NewTask("长时间任务" + _counter, blockOnGate: true);
            Manager.TriggerSync(task.TaskId);
            WaitUntil(() => task.IsRunning || task.State == TaskSyncState.Syncing, TimeSpan.FromSeconds(5));
            return task;
        }

        public void ReleaseLongTask()
        {
            Provider.Gate = null;
            _releaseLong.TrySetResult();
        }

        public SyncTask StartQueuedTask()
        {
            var task = NewTask("排队任务");
            Manager.TriggerSync(task.TaskId);
            return task;
        }

        public async Task<bool> WaitForCompletionAsync(SyncTask task, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                if (task.State is TaskSyncState.Completed or TaskSyncState.Error) return task.State == TaskSyncState.Completed;
                await Task.Delay(100).ConfigureAwait(false);
            }
            return task.State == TaskSyncState.Completed;
        }

        public BackupActivityGate CreateGate() => new(
            hasRunningWork: () => Manager.HasRunningWork,
            hasRunningOrQueuedWork: () => Manager.HasRunningOrQueuedWork,
            hasUnclearCommitOrLock: () => Manager.HasUnclearCommitOrLock,
            enterHold: () => Manager.TryEnterMaintenanceHold(out MaintenanceLease lease, "测试") ? lease : null,
            saveConfiguration: () => Task.CompletedTask);

        /// <summary>Builds a managed install layout with a dummy payload and (optionally) a broken updater.</summary>
        public string CreateManagedLayout(out string appDirectory, bool createUpdater = true, bool updaterIsBroken = false)
        {
            string root = Path.Combine(_root, "install-" + Guid.NewGuid().ToString("N")[..8]);
            string app = Path.Combine(root, "app-0.5.0");
            string payload = Path.Combine(root, "payload-0.5.0");
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(payload, "FNSyncPlus.WinUI.exe"), "payload");
            var layout = new VersionedInstallLayout(root);
            var installed = layout.InstallAsync("0.5.0", payload, _ => Task.FromResult(true), CancellationToken.None)
                .GetAwaiter().GetResult();
            if (!installed.Ok) throw new InvalidOperationException("无法准备安装目录：" + installed.Error);
            // A text file cannot be executed, so "broken" exercises Process.Start failing.
            if (createUpdater)
                File.WriteAllText(Path.Combine(root, "FNSyncPlus.Updater.exe"), "not a real exe");
            appDirectory = app;
            return root;
        }

        public UpdateService CreateService(string appDirectory, UpdateManifestVerifier verifier)
        {
            var settings = new UpdateSettings { ManifestUrl = "https://update.invalid/manifest.json", Channel = "stable" };
            return new UpdateService(settings, Path.Combine(_root, "updates.json"), appDirectory, Manager,
                verifierOverride: verifier);
        }

        /// <summary>A release whose package exists only so the pre-flight size/hash checks pass.</summary>
        public VerifiedRelease ReleaseFor(string installRoot, string version)
        {
            string package = Path.Combine(_root, $"pkg-{version}.zip");
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("package-" + version);
            File.WriteAllBytes(package, bytes);
            string hash = UpdatePackageExtractor.HashFile(package);
            return new VerifiedRelease("", version, UpdateChannel.Stable, hash, bytes.Length, "", package, "lab", 5);
        }

        private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                if (condition()) return;
                Thread.Sleep(50);
            }
        }

        public void Dispose()
        {
            ReleaseLongTask();
            try { Manager.ShutdownAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); } catch (Exception) { }
            Manager.Dispose();
            try { Provider.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception) { }
        }
    }
}

/// <summary>
/// Local mock NAS that can hold a sync open on demand. The test needs a task that is genuinely
/// running (so the gate has something to wait for) without depending on file size or timing, so
/// every call waits on a caller-controlled gate.
/// </summary>
internal sealed class SlowLocalProvider : INasStorageProvider
{
    private readonly SmbStorageProvider _inner;

    public SlowLocalProvider(string root) => _inner = new SmbStorageProvider(rootUncPath: root);

    /// <summary>When set, list operations wait for it; used to keep a task "running".</summary>
    public Task? Gate { get; set; }

    public StorageProviderType ProviderType => StorageProviderType.FnOsPersonal;
    // A local directory needs no credentials; reporting false would push tasks into
    // OfflineWaiting and the "is a backup running" assertions would never see real work.
    public bool IsAuthenticated => true;
    public string RootDisplayPath => _inner.RootDisplayPath;

    public Task<StorageAuthResult> ConnectAndAuthenticateAsync(string server, string username, string password,
        string? twoFactorCode = null, CancellationToken ct = default) =>
        _inner.ConnectAndAuthenticateAsync(server, username, password, twoFactorCode, ct);

    public async Task<IReadOnlyList<NasStorageItem>> ListEntriesAsync(string? relativePath = null, CancellationToken ct = default)
    {
        if (Gate is { } gate) await gate.WaitAsync(ct).ConfigureAwait(false);
        return await _inner.ListEntriesAsync(relativePath, ct).ConfigureAwait(false);
    }

    private async Task WaitAsync(CancellationToken ct)
    {
        if (Gate is { } gate) await gate.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<StorageOperationResult> CreateDirectoryAsync(string relativePath, CancellationToken ct = default)
    {
        await WaitAsync(ct).ConfigureAwait(false);
        return await _inner.CreateDirectoryAsync(relativePath, ct).ConfigureAwait(false);
    }

    public async Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default)
    {
        await WaitAsync(ct).ConfigureAwait(false);
        return await _inner.ExistsAsync(relativePath, ct).ConfigureAwait(false);
    }

    public async Task<NasStorageItem?> GetItemAsync(string relativePath, CancellationToken ct = default)
    {
        await WaitAsync(ct).ConfigureAwait(false);
        return await _inner.GetItemAsync(relativePath, ct).ConfigureAwait(false);
    }

    public async Task<StorageOperationResult> UploadFileAsync(string relativePath, Stream sourceStream, long fileSize,
        DateTime lastWriteTimeUtc, bool overwrite = true, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        await WaitAsync(ct).ConfigureAwait(false);
        return await _inner.UploadFileAsync(relativePath, sourceStream, fileSize, lastWriteTimeUtc, overwrite, progress, ct)
            .ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
    {
        await WaitAsync(ct).ConfigureAwait(false);
        return await _inner.OpenReadAsync(relativePath, ct).ConfigureAwait(false);
    }

    public Task<bool> VerifyFileIntegrityAsync(string relativePath, string expectedSha256, long expectedSize,
        CancellationToken ct = default) =>
        _inner.VerifyFileIntegrityAsync(relativePath, expectedSha256, expectedSize, ct);

    public Task<StorageOperationResult> DeleteAsync(string relativePath, bool moveToTrash = true, CancellationToken ct = default) =>
        _inner.DeleteAsync(relativePath, moveToTrash, ct);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
