using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Scheduler acceptance (P0-2). The previous build only had settings plus a manual button; these
/// cases prove background checks actually run, that they never overlap, that each mode does exactly
/// what it promises, and that a broken feed cannot break the program.
/// </summary>
public sealed class UpdateSchedulerTestSuite
{
    public async Task<IReadOnlyList<UpdateSuiteCase>> RunAsync(string workspace, CancellationToken token = default)
    {
        Directory.CreateDirectory(workspace);
        var cases = new List<UpdateSuiteCase>();

        var (privatePem, publicPem) = UpdateManifestSigner.CreateKeyPair();
        using var signingKey = ECDsa.Create();
        signingKey.ImportFromPem(privatePem);
        var verifier = UpdateManifestVerifier.FromPem(publicPem);

        byte[] package = BuildPackage(Path.Combine(workspace, "payload"));
        string packageHash = Convert.ToHexString(SHA256.HashData(package));

        using var server = new LoopbackHttpsServer(token);
        string baseUrl = server.BaseUrl;
        // Each context gets its own handler trusting only this run's loopback certificate.
        SchedulerContext.HandlerFactory = () => server.CreateHandler();

        UpdateManifest Manifest(Action<UpdateManifest>? customise = null)
        {
            var manifest = new UpdateManifest
            {
                Channel = "stable", Version = "0.5.0", MinVersion = "0.1.0", ReleasedAt = DateTimeOffset.UtcNow,
                Notes = "scheduler test", Url = baseUrl + "/pkg.zip", Sha256 = packageHash, Size = package.Length,
                Batch = 1, Audience = new List<string> { "*" }, MinCompatibleClient = "0.0.0"
            };
            customise?.Invoke(manifest);
            return manifest;
        }

        void Publish(UpdateManifest manifest)
        {
            server.Set("/manifest.json", Encoding.UTF8.GetBytes(UpdateManifestSigner.Sign(manifest, signingKey)));
            server.Set("/pkg.zip", package);
        }

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

        // ---- 模式 A ----
        await Case("启动后自动发现新版本并通知用户", async () =>
        {
            Publish(Manifest());
            server.ResetCounters();
            using var ctx = new SchedulerContext(workspace, "startup", verifier, baseUrl,
                new UpdateSchedulerOptions(TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(30),
                    TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)));
            ctx.Settings.AutoMode = (int)AutoUpdateMode.CheckOnly;
            ctx.Save();
            var detected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            ctx.Scheduler.Changed += e => { if (e.Kind == "available") detected.TrySetResult(e.Version); };
            ctx.Scheduler.Start();
            var completed = await Task.WhenAny(detected.Task, Task.Delay(5000, token)) == detected.Task;
            string version = completed ? await detected.Task : "";
            return (completed && version == "0.5.0", $"detected={completed} version={version} status={ctx.Scheduler.Status}");
        });

        await Case("模式 A 只检查不下载", async () =>
        {
            Publish(Manifest());
            server.ResetCounters();
            using var ctx = new SchedulerContext(workspace, "checkonly", verifier, baseUrl, FastOptions());
            ctx.Settings.AutoMode = (int)AutoUpdateMode.CheckOnly;
            ctx.Save();
            await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            int packageHits = server.Hits("/pkg.zip");
            // The important assertion is "no package was fetched". StagedVersion proves the
            // scheduler did not record a download either.
            return (packageHits == 0 && string.IsNullOrEmpty(ctx.Settings.StagedVersion),
                $"pkgHits={packageHits} staged={ctx.Settings.StagedVersion}");
        });

        // ---- 周期检查与并发 ----
        await Case("周期检查按间隔重复发生", async () =>
        {
            Publish(Manifest());
            server.ResetCounters();
            using var ctx = new SchedulerContext(workspace, "periodic", verifier, baseUrl,
                new UpdateSchedulerOptions(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(150),
                    TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(1)));
            ctx.Settings.AutoMode = (int)AutoUpdateMode.CheckOnly;
            ctx.Save();
            ctx.Scheduler.Start();
            await Task.Delay(900, token);
            await ctx.Scheduler.StopAsync(TimeSpan.FromSeconds(2));
            int checks = ctx.Scheduler.CompletedChecks;
            int manifestHits = server.Hits("/manifest.json");
            return (checks >= 3 && manifestHits >= 3, $"checks={checks} manifestHits={manifestHits}");
        });

        await Case("多次触发不会并发执行检查", async () =>
        {
            Publish(Manifest());
            server.ResetCounters();
            using var ctx = new SchedulerContext(workspace, "no-overlap", verifier, baseUrl, FastOptions());
            var runs = Enumerable.Range(0, 6).Select(_ => ctx.Scheduler.RunOnceAsync(token));
            await Task.WhenAll(runs).ConfigureAwait(false);
            return (server.MaxConcurrentRequests <= 1 && ctx.Scheduler.CompletedChecks >= 1,
                $"maxConcurrent={server.MaxConcurrentRequests} checks={ctx.Scheduler.CompletedChecks}");
        });

        // ---- 模式 B ----
        await Case("模式 B 自动下载并校验后等待用户确认安装", async () =>
        {
            Publish(Manifest());
            server.ResetCounters();
            using var ctx = new SchedulerContext(workspace, "download-notify", verifier, baseUrl, FastOptions());
            ctx.Settings.AutoMode = (int)AutoUpdateMode.DownloadAndNotify;
            ctx.Save();
            var evt = await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            string expected = Path.Combine(ctx.Service.DownloadDirectory, "FN-Sync-Plus-0.5.0-x64.zip");
            bool downloaded = File.Exists(expected) && ctx.Settings.StagedVersion == "0.5.0";
            return (evt.Kind == "ready" && evt.Version == "0.5.0" && downloaded && ctx.Launcher.Calls == 0 &&
                    server.Hits("/pkg.zip") >= 1,
                $"kind={evt.Kind} downloaded={downloaded} launches={ctx.Launcher.Calls} staged={ctx.Settings.StagedVersion}");
        });

        await Case("同一版本已下载时不会重复下载", async () =>
        {
            Publish(Manifest());
            using var ctx = new SchedulerContext(workspace, "dedupe", verifier, baseUrl, FastOptions());
            ctx.Settings.AutoMode = (int)AutoUpdateMode.DownloadAndNotify;
            ctx.Save();
            await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            server.ResetCounters();
            await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            return (server.Hits("/pkg.zip") == 0, $"second-run pkgHits={server.Hits("/pkg.zip")}");
        });

        // ---- 模式 C ----
        await Case("模式 C 在备份空闲时完成受控安装交接", async () =>
        {
            Publish(Manifest());
            using var ctx = new SchedulerContext(workspace, "unattended", verifier, baseUrl, FastOptions(),
                managedLayout: true);
            ctx.Settings.AutoMode = (int)AutoUpdateMode.UnattendedInstall;
            ctx.Settings.UnattendedEnabled = true;
            ctx.Save();
            var evt = await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            bool installed = evt.Kind == "installing" && ctx.Launcher.Calls == 1;
            bool heldDuringHandoff = ctx.Launcher.HoldSeenAtHandoff;
            bool releasedAfter = !ctx.Manager.IsMaintenanceHoldActive;
            return (installed && heldDuringHandoff && releasedAfter,
                $"kind={evt.Kind} calls={ctx.Launcher.Calls} held={heldDuringHandoff} released={releasedAfter} {evt.Message}");
        });

        await Case("备份运行期间模式 C 推迟安装并恢复备份", async () =>
        {
            Publish(Manifest());
            using var ctx = new SchedulerContext(workspace, "unattended-busy", verifier, baseUrl,
                new UpdateSchedulerOptions(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(30),
                    TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)), managedLayout: true);
            ctx.Settings.AutoMode = (int)AutoUpdateMode.UnattendedInstall;
            ctx.Settings.UnattendedEnabled = true;
            ctx.Save();
            var backup = ctx.StartBlockingBackup();
            var evt = await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            bool deferred = evt.Kind == "deferred" && ctx.Launcher.Calls == 0;
            bool released = !ctx.Manager.IsMaintenanceHoldActive;
            ctx.ReleaseBackup();
            bool finished = await ctx.WaitForStateAsync(backup, TaskSyncState.Completed, TimeSpan.FromSeconds(10));
            return (deferred && released && finished,
                $"kind={evt.Kind} launches={ctx.Launcher.Calls} released={released} backup={backup.State} {evt.Message}");
        });

        await Case("未启用无人值守时不会自动安装", async () =>
        {
            Publish(Manifest());
            using var ctx = new SchedulerContext(workspace, "unattended-off", verifier, baseUrl, FastOptions(),
                managedLayout: true);
            ctx.Settings.AutoMode = (int)AutoUpdateMode.UnattendedInstall;
            ctx.Settings.UnattendedEnabled = false;
            ctx.Save();
            var evt = await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            return (ctx.Launcher.Calls == 0 && evt.Kind == "ready", $"kind={evt.Kind} calls={ctx.Launcher.Calls}");
        });

        await Case("同一版本交接失败后不会反复重试安装", async () =>
        {
            Publish(Manifest());
            using var ctx = new SchedulerContext(workspace, "handoff-loop", verifier, baseUrl, FastOptions(),
                managedLayout: true);
            ctx.Settings.AutoMode = (int)AutoUpdateMode.UnattendedInstall;
            ctx.Settings.UnattendedEnabled = true;
            ctx.Settings.HandoffVersion = "0.5.0";
            ctx.Save();
            var evt = await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            return (ctx.Launcher.Calls == 0 && evt.Kind == "ready", $"kind={evt.Kind} calls={ctx.Launcher.Calls}");
        });

        // ---- 失败与恢复 ----
        await Case("更新源不可达时只记录失败并继续运行", async () =>
        {
            using var ctx = new SchedulerContext(workspace, "unreachable", verifier, "https://127.0.0.1:1", FastOptions());
            ctx.Settings.AutoMode = (int)AutoUpdateMode.CheckOnly;
            ctx.Save();
            var evt = await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            bool stillUsable = ctx.Manager.Tasks.Count == 0 && ctx.Scheduler.Status.Length > 0;
            return (evt.Kind == "error" && ctx.Scheduler.FailedChecks >= 1 && stillUsable, evt.Message);
        });

        await Case("取消后不再继续检查且不残留状态", async () =>
        {
            Publish(Manifest());
            using var ctx = new SchedulerContext(workspace, "cancel", verifier, baseUrl,
                new UpdateSchedulerOptions(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100),
                    TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1)));
            ctx.Settings.AutoMode = (int)AutoUpdateMode.CheckOnly;
            ctx.Save();
            ctx.Scheduler.Start();
            await Task.Delay(350, token);
            await ctx.Scheduler.StopAsync(TimeSpan.FromSeconds(2));
            int afterStop = ctx.Scheduler.CompletedChecks;
            await Task.Delay(400, token);
            bool stopped = ctx.Scheduler.CompletedChecks == afterStop && !ctx.Scheduler.IsRunning;
            bool noHold = !ctx.Manager.IsMaintenanceHoldActive;
            return (stopped && noHold, $"afterStop={afterStop} later={ctx.Scheduler.CompletedChecks} running={ctx.Scheduler.IsRunning}");
        });

        await Case("重启后更新设置与已下载版本仍然有效", () =>
        {
            Publish(Manifest());
            string settingsPath = Path.Combine(workspace, "restart", "updates.json");
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var settings = new UpdateSettings
            {
                ManifestUrl = baseUrl + "/manifest.json", Channel = "beta",
                AutoMode = (int)AutoUpdateMode.DownloadAndNotify, UnattendedEnabled = false,
                MachineGroup = "lab", ReleaseBatch = 3, StagedVersion = "0.5.0"
            };
            new UpdateService(settings, settingsPath, "", verifierOverride: verifier).Save();
            var reloaded = UpdateService.LoadSettings(settingsPath);
            return Task.FromResult((reloaded.Channel == "beta" && reloaded.AutoMode == (int)AutoUpdateMode.DownloadAndNotify &&
                    reloaded.StagedVersion == "0.5.0" && reloaded.ReleaseBatch == 3,
                $"channel={reloaded.Channel} mode={reloaded.AutoMode} staged={reloaded.StagedVersion}"));
        });

        await Case("Stable 客户端不会获取 Beta 版本", async () =>
        {
            Publish(Manifest(m => m.Channel = "beta"));
            server.ResetCounters();
            using var ctx = new SchedulerContext(workspace, "channel-split", verifier, baseUrl, FastOptions());
            ctx.Settings.Channel = "stable";
            ctx.Settings.AutoMode = (int)AutoUpdateMode.DownloadAndNotify;
            ctx.Save();
            var evt = await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            bool rejected = evt.Kind == "error" && evt.Message.Contains("通道", StringComparison.Ordinal);
            return (rejected && server.Hits("/pkg.zip") == 0,
                $"kind={evt.Kind} pkgHits={server.Hits("/pkg.zip")} {evt.Message}");
        });

        await Case("Beta 客户端可以获取 Beta 版本", async () =>
        {
            Publish(Manifest(m => m.Channel = "beta"));
            using var ctx = new SchedulerContext(workspace, "channel-beta", verifier, baseUrl, FastOptions());
            ctx.Settings.Channel = "beta";
            ctx.Settings.AutoMode = (int)AutoUpdateMode.CheckOnly;
            ctx.Save();
            var evt = await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            return (evt.Kind == "available" && evt.Version == "0.5.0", $"{evt.Kind} {evt.Message}");
        });

        await Case("未配置更新地址时保持静默且不影响运行", async () =>
        {
            using var ctx = new SchedulerContext(workspace, "no-url", verifier, "", FastOptions());
            var evt = await ctx.Scheduler.RunOnceAsync(token).ConfigureAwait(false);
            return (evt.Kind == "skipped" && !ctx.Manager.IsMaintenanceHoldActive, evt.Message);
        });

        return cases;
    }

    private static UpdateSchedulerOptions FastOptions() => new(
        InitialDelay: TimeSpan.FromMilliseconds(20),
        Interval: TimeSpan.FromSeconds(30),
        MinBackoff: TimeSpan.FromMilliseconds(100),
        MaxBackoff: TimeSpan.FromMilliseconds(500));

    private static byte[] BuildPackage(string source)
    {
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "FNSyncPlus.WinUI.exe"), "scheduler payload");
        File.WriteAllText(Path.Combine(source, "FNSyncPlus.Updater.exe"), "updater");
        string zip = source + "-" + Guid.NewGuid().ToString("N") + ".zip";
        ZipFile.CreateFromDirectory(source, zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        byte[] bytes = File.ReadAllBytes(zip);
        File.Delete(zip);
        return bytes;
    }

    /// <summary>Fake launcher: records the call and whether the hold was visible at handoff time.</summary>
    private sealed class RecordingLauncher : IUpdateInstallLauncher
    {
        private readonly Func<bool> _holdProbe;
        public RecordingLauncher(Func<bool> holdProbe) => _holdProbe = holdProbe;
        public int Calls { get; private set; }
        public bool HoldSeenAtHandoff { get; private set; }

        public bool TryStart(UpdateInstallSession session, out string error)
        {
            Calls++;
            HoldSeenAtHandoff = _holdProbe() && session.HoldsQueue;
            session.CommitHandOff(null);
            error = "";
            return true;
        }
    }

    private sealed class SchedulerContext : IDisposable
    {
        private readonly TaskManager _manager;
        private readonly AppSettings _appSettings;
        private readonly string _root;
        private SlowLocalProvider? _blockingProvider;
        private TaskCompletionSource? _blockingGate;

        /// <summary>Supplies a handler that trusts the suite's loopback certificate.</summary>
        internal static Func<HttpMessageHandler>? HandlerFactory;

        public SchedulerContext(string workspace, string name, UpdateManifestVerifier verifier, string baseUrl,
            UpdateSchedulerOptions options, bool managedLayout = false)
        {
            string root = Path.Combine(workspace, "sched-" + name);
            Directory.CreateDirectory(root);
            string settingsPath = Path.Combine(root, "updates.json");
            _root = root;
            _appSettings = AppSettings.CreateInMemory();
            Manager = new TaskManager(_appSettings, new SmbAuthService(),
                new LogService(Path.Combine(root, "logs")));
            _manager = Manager;

            string installRoot;
            if (managedLayout)
            {
                string install = Path.Combine(root, "install");
                string payload = Path.Combine(root, "payload-0.4.0");
                Directory.CreateDirectory(payload);
                File.WriteAllText(Path.Combine(payload, "FNSyncPlus.WinUI.exe"), "current payload");
                new AppBuildMetadata("0.4.0", 1).Save(payload);
                var layout = new VersionedInstallLayout(install, Path.Combine(root, "data-format.json"));
                layout.InstallAsync("0.4.0", payload, _ => Task.FromResult(true), CancellationToken.None)
                    .GetAwaiter().GetResult();
                // The updater binary lives in the install root; contents are irrelevant to the fake launcher.
                File.WriteAllText(Path.Combine(install, "FNSyncPlus.Updater.exe"), "stub");
                installRoot = Path.Combine(install, "app-0.4.0");
            }
            else
            {
                installRoot = root;
            }

            Settings = new UpdateSettings
            {
                ManifestUrl = string.IsNullOrWhiteSpace(baseUrl) ? "" : baseUrl + "/manifest.json",
                Channel = "stable", MachineGroup = "lab", ReleaseBatch = 5
            };
            Service = new UpdateService(Settings, settingsPath, installRoot, Manager, null, verifier,
                HandlerFactory?.Invoke());
            Launcher = new RecordingLauncher(() => Manager.IsMaintenanceHoldActive);
            Scheduler = new UpdateScheduler(Service, options, Launcher);
        }

        public UpdateSettings Settings { get; }
        public UpdateService Service { get; }
        public UpdateScheduler Scheduler { get; }
        public TaskManager Manager { get; }
        public RecordingLauncher Launcher { get; }

        public void Save() => Service.Save();

        /// <summary>Starts a real sync that stays running until <see cref="ReleaseBackup"/>.</summary>
        public SyncTask StartBlockingBackup()
        {
            string source = Path.Combine(_root, "backup-source");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "seed.txt"), "seed");
            string target = Path.Combine(_root, "backup-target");
            Directory.CreateDirectory(target);
            _blockingGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _blockingProvider = new SlowLocalProvider(target) { Gate = _blockingGate.Task };
            Manager.StorageProviderSupplier = () => _blockingProvider;
            var task = new SyncTask
            {
                TaskName = "占用中的备份", SourceFolder = source,
                // Rooted remote-style path so the sync never resolves against the working directory.
                DestinationFolder = "/pilot/占用目录",
                ProviderType = StorageProviderType.FnOsPersonal, ScheduleMode = TaskSyncScheduleMode.ManualOnly
            };
            _appSettings.Tasks.Add(task);
            Manager.TriggerSync(task.TaskId);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (Manager.HasRunningWork) break;
                Thread.Sleep(50);
            }
            return task;
        }

        public void ReleaseBackup()
        {
            if (_blockingProvider != null) _blockingProvider.Gate = null;
            _blockingGate?.TrySetResult();
        }

        public async Task<bool> WaitForStateAsync(SyncTask task, TaskSyncState state, TimeSpan timeout)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < timeout)
            {
                if (task.State == state) return true;
                await Task.Delay(100).ConfigureAwait(false);
            }
            return task.State == state;
        }

        public void Dispose()
        {
            try { Scheduler.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(); } catch (Exception) { }
            Scheduler.Dispose();
            try { _manager.ShutdownAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(); } catch (Exception) { }
            _manager.Dispose();
        }
    }
}
