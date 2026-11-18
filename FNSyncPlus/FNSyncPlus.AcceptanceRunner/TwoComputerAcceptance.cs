using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;

namespace FNSyncPlus.Acceptance;

public static partial class AcceptanceCli
{
    private sealed record WorkerWindow(string Phase, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, string SnapshotId);
    private sealed record DeviceEvidence(int Schema, Guid RunId, string Role, string BuildSha, string MachineKey,
        Guid ProfileId, string Server, int Uid, string PersonalRoot, string BackupPath, string TaskId,
        WorkerWindow[] Windows, Entry[] CurrentFiles, bool Verified, bool ClocksSynchronized);

    private static string MachineKey() => HashText((string?)Registry.GetValue(
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null)
        ?? throw new Blocked("The Windows machine identity could not be read."));

    private static void RequireDevice(DeviceEvidence evidence)
    {
        if (evidence.Schema != 1 || evidence.RunId == Guid.Empty || evidence.ProfileId == Guid.Empty ||
            evidence.Role is not ("A" or "B") || !IsSha256(evidence.MachineKey) || evidence.Uid <= 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(evidence.PersonalRoot ?? "", @"\Avol[1-9][0-9]*/[1-9][0-9]*\z") ||
            !evidence.PersonalRoot!.EndsWith($"/{evidence.Uid}", StringComparison.Ordinal) ||
            evidence.BackupPath != $"Codex_FN_Test_TwoPC_{evidence.RunId:N}/{evidence.Role}/backup" ||
            !evidence.Verified || evidence.Windows is not { Length: 2 } || evidence.CurrentFiles is not { Length: > 0 } ||
            !Guid.TryParseExact(evidence.TaskId, "N", out _) || evidence.BuildSha?.Length != 40)
            throw new InvalidInput("An incomplete or unsuccessful device evidence file cannot qualify a peer test.");
        ValidateEntries(evidence.CurrentFiles);
        foreach (string phase in new[] { "Initial", "Incremental" })
        {
            if (evidence.Windows.Count(w => w.Phase == phase) != 1 ||
                evidence.Windows.Any(w => w.FinishedUtc <= w.StartedUtc || string.IsNullOrEmpty(w.SnapshotId)))
                throw new InvalidInput("Worker timing or snapshot evidence is incomplete.");
        }
    }

    private static async Task<DeviceEvidence> ReadDeviceAsync(string path, CancellationToken token)
    {
        path = LocalPath(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidInput("Device evidence is too large.");
        var evidence = JsonSerializer.Deserialize<DeviceEvidence>(await File.ReadAllBytesAsync(path, token), Json)
            ?? throw new InvalidInput("Device evidence is missing.");
        RequireDevice(evidence);
        return evidence;
    }

    private static bool Overlaps(WorkerWindow left, WorkerWindow right) =>
        left.StartedUtc < right.FinishedUtc && right.StartedUtc < left.FinishedUtc;

    private static async Task TwoComputerRunAsync(Options options, List<Result> results, CancellationToken token)
    {
        if (!options.Has("--authorize-nas") || !options.Has("--allow-test-writes") || Console.IsInputRedirected)
            throw new Blocked("The operator must authorize new test writes and log in interactively on this computer.");
        string role = options.Require("--role");
        if (role is not ("A" or "B") || !Guid.TryParseExact(options.Require("--run-id"), "D", out Guid runId) || runId == Guid.Empty)
            throw new InvalidInput("Use role A or B and the same nonempty run GUID on both computers.");
        if (!DateTimeOffset.TryParse(options.Require("--start-at"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var start) || start <= DateTimeOffset.UtcNow || start > DateTimeOffset.UtcNow.AddMinutes(20))
            throw new InvalidInput("Choose a common UTC start within the next 20 minutes, allowing time for both logins.");
        string workspace = LocalPath(options.Require("--workspace"));
        if (Directory.Exists(workspace) || File.Exists(workspace)) throw new InvalidInput("Use a new local workspace.");
        Directory.CreateDirectory(workspace);
        string source = Path.Combine(AppStorageRoot.TestSessionsRoot(), $"TwoPC_{runId:N}_{role}", "source");
        Directory.CreateDirectory(source);
        var server = Server(options);
        string sandbox = $"Codex_FN_Test_TwoPC_{runId:N}";
        string target = $"{sandbox}/{role}/backup";
        await WriteNewJsonAsync(Path.Combine(workspace, "cleanup-inventory.json"), new
        {
            LocalWorkspace = workspace, LocalSource = source, IsolatedProfileRoot = AppStorageRoot.TestRoot, RelativePersonalBackupPath = target,
            Retained = true, Action = "Retain test files and history, including after failure. No automatic remote deletion."
        }, token);
        var trace = new ConcurrentQueue<string>();
        await using var session = await LoginAsync(server, options.Get("--cert-sha256"), role, token, trace.Enqueue);
        if (await session.Provider.GetItemAsync(target, token) != null)
            throw new Blocked("This test target already exists. Preserve its data and use a new session GUID.");
        byte[] original = Encoding.UTF8.GetBytes($"FN Sync Plus two-PC {runId:D} role {role} V1\n");
        string marker = Path.Combine(source, "marker.txt");
        await File.WriteAllBytesAsync(marker, original, token);
        // The random payload makes cross-device data substitution detectable and keeps workers active.
        await File.WriteAllBytesAsync(Path.Combine(source, "payload.bin"), RandomNumberGenerator.GetBytes(16 * 1024 * 1024), token);
        await File.WriteAllTextAsync(Path.Combine(source, "中文样本.txt"), $"两台电脑独立验收 {role} {runId:D}", new UTF8Encoding(false), token);
        var initial = await ReadLocalFilesAsync(source, token);
        var profileId = Guid.NewGuid();
        var settings = AppSettings.LoadIsolatedTestSession(profileId);
        settings.SetIsolatedTestBoundary(sandbox);
        using var manager = new TaskManager(settings, new SmbAuthService(), new LogService(Path.Combine(workspace, "logs")),
            experimentalVersionedBackup: true) { StorageProviderSupplier = () => session.Provider };
        var task = new SyncTask
        {
            TaskName = "Two-PC acceptance", SourceFolder = source, DestinationRoot = sandbox, DestinationFolder = target,
            ProviderType = StorageProviderType.FnOsPersonal, NasDeviceId = server.AbsoluteUri,
            NasUsername = session.Provider.CurrentUsername!, NasAccountUid = session.Uid,
            AutoSyncEnabled = false, RealtimeWatchEnabled = false, ScheduleMode = TaskSyncScheduleMode.ManualOnly
        };
        manager.AddTask(task);
        manager.SetNasOnlineState(true);
        var windows = new List<WorkerWindow>();
        try
        {
            await WaitUntilAsync(start, token);
            windows.Add(await RunWorkerAsync(manager, task, session.Provider, "Initial", token));
            AddResult(results, "OwnInitial", true, new { task.TaskId, session.Uid, target }, "The real backup worker completed in this account's new test target.");
            // Fixed offset permits the other operator to start the incremental pass at the same time.
            await WaitUntilAsync(start.AddMinutes(3), token);
            await File.WriteAllTextAsync(marker, $"FN Sync Plus two-PC {runId:D} role {role} V2\n", new UTF8Encoding(false), token);
            windows.Add(await RunWorkerAsync(manager, task, session.Provider, "Incremental", token));
            AddResult(results, "OwnIncremental", true, windows.ToArray(), "Both actual worker windows and committed snapshot identities are recorded.");
            await EnsureBackupIdleAsync(session.Provider, target, token);
            var backup = await ReadBackupAsync(session.Provider, target, token);
            var ownDirectory = await session.Provider.GetItemAsync(target, token);
            results.Add(new("OwnOwnership", "RealNAS", ownDirectory?.OwnerUid == null ? "BLOCKED" : ownDirectory.OwnerUid == session.Uid ? "PASS" : "FAIL",
                new { ExpectedUid = session.Uid, ActualUid = ownDirectory?.OwnerUid }, "The NAS must report ownership of this exact backup directory."));
            var expected = await ReadLocalFilesAsync(source, token);
            var restore = new RestoreEngine(new ManifestManager());
            string currentPath = Path.Combine(workspace, "restore-current");
            var current = await restore.RunRestorePackageAsync(session.Provider, backup.Package, currentPath, ConflictResolution.SkipExisting, token);
            bool currentOk = current.FailedFiles == 0 && current.IntegrityVerifiedFiles == expected.Length &&
                expected.SequenceEqual(await ReadLocalFilesAsync(currentPath, token));
            AddResult(results, "OwnCurrentHashes", currentOk, new { Expected = expected, Restored = await ReadLocalFilesAsync(currentPath, token) },
                "Current paths, byte sizes and SHA-256 must match this device's synthetic source.");
            var historical = backup.Manifest.History.SingleOrDefault(h => h.OriginalRel == "marker.txt" && h.Sha256 == initial.Single(e => e.Path == "marker.txt").Sha256);
            bool historyOk = false;
            if (historical != null)
            {
                string destination = Path.Combine(workspace, "restore-history");
                var history = await restore.RestorePackageHistoryAsync(session.Provider, backup.Package, destination, historical, cancellationToken: token);
                historyOk = history.FailedFiles == 0 && history.IntegrityVerifiedFiles == 1 &&
                    (await File.ReadAllBytesAsync(Path.Combine(destination, "marker.txt"), token)).SequenceEqual(original);
            }
            AddResult(results, "OwnHistoryHash", historyOk, new { OriginalSha256 = initial.Single(e => e.Path == "marker.txt").Sha256 },
                "The archived marker must restore the original V1 bytes for this account.");
            await TestIdleGuardsAsync(manager, task, session, trace, results, token);
            var after = await ReadBackupAsync(session.Provider, target, token);
            AddResult(results, "GuardSnapshotUnchanged", after.SnapshotId == backup.SnapshotId, new { Before = backup.SnapshotId, After = after.SnapshotId },
                "Pause and offline guards must leave the committed backup unchanged.");
            var evidence = new DeviceEvidence(1, runId, role, Build.Split('+').Last(), MachineKey(), profileId, server.AbsoluteUri,
                session.Uid, session.Root, target, task.TaskId, windows.ToArray(), expected,
                currentOk && historyOk && ownDirectory?.OwnerUid == session.Uid && !results.Any(r => r.Status == "FAIL"),
                options.Has("--clocks-synchronized"));
            await WriteNewJsonAsync(Path.Combine(workspace, "device-evidence.json"), evidence, token);
            await WriteNewJsonAsync(Path.Combine(workspace, "source-files.json"), new { Initial = initial, Current = expected }, token);
            results.Add(new("DeviceEvidence", "RealNAS", evidence.Verified ? "PASS" : results.Any(r => r.Status == "FAIL") ? "FAIL" : "BLOCKED", evidence,
                "Only this new synthetic test task was run. These results do not claim installed-UI account switching."));
            results.Add(new("LiveSwitchAndNetworkDrop", "RealNAS", "BLOCKED", null,
                "Active installed-UI account switching, token expiry and physical network interruption require a separate operator checkpoint."));
            await session.Provider.DisposeAsync();
            int beforeLogoutProbe = trace.Count;
            bool rejected = false;
            try { await session.Provider.CreateDirectoryAsync($"{target}/unauthenticated-probe", token); }
            catch (InvalidOperationException) { rejected = true; }
            AddResult(results, "LoggedOutWriteBlocked", rejected && trace.Count == beforeLogoutProbe, null,
                "After disposing this runner's authenticated session, the provider rejects a write before sending any request.");
        }
        finally
        {
            if (!await manager.ShutdownAsync(TimeSpan.FromSeconds(30)))
                results.Add(new("WorkerDrain", "RealNAS", "FAIL", null, "The isolated worker did not drain within 30 seconds. Retain all evidence and locks."));
        }
        results.Add(AuditOne(trace));
    }

    private static async Task WaitUntilAsync(DateTimeOffset target, CancellationToken token)
    {
        var delay = target - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            Console.Error.WriteLine($"Waiting for scheduled UTC checkpoint {target:O}.");
            await Task.Delay(delay, token);
        }
    }

    private static void AddResult(List<Result> results, string id, bool passed, object? evidence, string reason) =>
        results.Add(new(id, "RealNAS", passed ? "PASS" : "FAIL", evidence, reason));

    private static Result AuditOne(IEnumerable<string> trace)
    {
        string[] entries = trace.ToArray();
        bool destructive = entries.Any(operation => operation is "file.rm" or "file.del" or "file.delete" or "file.trash");
        int unknown = entries.Count(operation => !ObservedOperations.Contains(operation));
        return new("RequestAudit", "RealNAS", destructive ? "FAIL" : unknown > 0 ? "BLOCKED" : "PASS",
            new { Operations = Summarize(entries), Unclassified = unknown, Destructive = destructive },
            "The actual outgoing request trace is classified. No remote delete operation is permitted in this test.");
    }

    // user.info is the authenticated control-channel heartbeat, independent of task execution.
    private static int StorageRequestCount(IEnumerable<string> trace) => trace.Count(operation => operation != "user.info");

    private static async Task<Entry[]> ReadLocalFilesAsync(string root, CancellationToken token)
    {
        var entries = new List<Entry>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            EnsureNoReparse(file);
            await using var stream = File.OpenRead(file);
            var hash = await ReadHashAsync(stream, token);
            entries.Add(new(Path.GetRelativePath(root, file).Replace('\\', '/'), false, hash.Size, hash.Hash));
        }
        return Sort(entries);
    }

    private static async Task<WorkerWindow> RunWorkerAsync(TaskManager manager, SyncTask task, INasStorageProvider provider,
        string phase, CancellationToken token)
    {
        var completion = new TaskCompletionSource<TaskSyncState>(TaskCreationOptions.RunContinuationsAsynchronously);
        DateTimeOffset? started = null;
        void Changed(SyncTask state)
        {
            if (state.TaskId != task.TaskId) return;
            if (state.State is TaskSyncState.Scanning or TaskSyncState.Syncing) started ??= DateTimeOffset.UtcNow;
            if (state.State is TaskSyncState.Completed or TaskSyncState.Error or TaskSyncState.ConfigurationError or TaskSyncState.OfflineWaiting)
                completion.TrySetResult(state.State);
        }
        manager.TaskStateChanged += Changed;
        try
        {
            manager.TriggerSync(task.TaskId);
            var state = await completion.Task.WaitAsync(TimeSpan.FromMinutes(5), token);
            DateTimeOffset finished = DateTimeOffset.UtcNow;
            // TaskStateChanged precedes the worker's final bookkeeping. Shutdown safely drains it.
            while (manager.HasRunningWork)
                await Task.Delay(100, token);
            if (state != TaskSyncState.Completed || task.LastSuccessTime == null || started == null)
                throw new InvalidInput($"The isolated {phase} backup did not complete. See its retained task log.");
            await EnsureBackupIdleAsync(provider, task.DestinationFolder, token);
            return new(phase, started.Value, finished, (await ReadBackupAsync(provider, task.DestinationFolder, token)).SnapshotId);
        }
        finally { manager.TaskStateChanged -= Changed; }
    }

    private static async Task TestIdleGuardsAsync(TaskManager manager, SyncTask task, Session session,
        ConcurrentQueue<string> trace, List<Result> results, CancellationToken token)
    {
        int before = StorageRequestCount(trace);
        manager.PauseTask(task.TaskId);
        manager.TriggerSync(task.TaskId);
        await Task.Delay(500, token);
        AddResult(results, "PausedTriggerBlocked", task.State == TaskSyncState.Paused && StorageRequestCount(trace) == before, null,
            "A paused synthetic task must ignore a manual trigger without a storage request. Authentication heartbeats are excluded.");
        manager.ResumeTask(task.TaskId);
        manager.SetNasOnlineState(false);
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(SyncTask state) { if (state.TaskId == task.TaskId && state.State == TaskSyncState.OfflineWaiting) stopped.TrySetResult(true); }
        manager.TaskStateChanged += Changed;
        try
        {
            manager.TriggerSync(task.TaskId);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            while (manager.HasRunningWork) await Task.Delay(100, token);
            AddResult(results, "OfflineTriggerBlocked", StorageRequestCount(trace) == before, null,
                "The real scheduler's offline state prevented this test task from issuing NAS requests. The physical network remained connected.");
        }
        finally { manager.TaskStateChanged -= Changed; }
        manager.SetNasOnlineState(true);
        AddResult(results, "ManualReconnectIdle", !task.IsRunning && StorageRequestCount(trace) == before, new { session.Uid },
            "Reconnecting a manual-only synthetic task must leave it idle.");
    }

    private static async Task TwoComputerPeerAsync(Options options, List<Result> results, CancellationToken token)
    {
        if (!options.Has("--authorize-nas") || Console.IsInputRedirected)
            throw new Blocked("This computer's account holder must authorize and perform an interactive login.");
        var own = await ReadDeviceAsync(options.Require("--own"), token);
        var peer = await ReadDeviceAsync(options.Require("--peer"), token);
        var server = Server(options);
        if (own.MachineKey != MachineKey() || own.Server != server.AbsoluteUri || peer.Server != own.Server ||
            own.RunId != peer.RunId || own.Role == peer.Role || own.BuildSha != peer.BuildSha)
            throw new InvalidInput("Evidence files must belong to this machine, opposite roles, one NAS, one run and one tool build.");
        AddResult(results, "DistinctMachinesAndProfiles", own.MachineKey != peer.MachineKey && own.ProfileId != peer.ProfileId && own.TaskId != peer.TaskId,
            new { MachineA = own.MachineKey, MachineB = peer.MachineKey, ProfileA = own.ProfileId, ProfileB = peer.ProfileId },
            "The two runners must use distinct Windows machine identities, test settings profiles and task IDs.");
        bool accounts = own.Uid != peer.Uid && own.PersonalRoot != peer.PersonalRoot;
        AddResult(results, "DistinctRealAccounts", accounts, new { OwnUid = own.Uid, PeerUid = peer.Uid }, "The two server-confirmed personal-space UIDs must differ.");
        foreach (var window in own.Windows)
        {
            var other = peer.Windows.Single(w => w.Phase == window.Phase);
            bool overlap = own.ClocksSynchronized && peer.ClocksSynchronized && Overlaps(window, other);
            results.Add(new($"Concurrent{window.Phase}", "RealNAS", overlap ? "PASS" : "BLOCKED",
                new { Own = window, Peer = other }, overlap ? "Actual worker intervals overlap, assuming both Windows clocks are synchronized."
                : "Both operators must confirm synchronized Windows clocks and the recorded worker intervals must overlap."));
        }
        if (!accounts) return;
        var trace = new ConcurrentQueue<string>();
        await using var session = await LoginAsync(server, options.Get("--cert-sha256"), own.Role, token, trace.Enqueue);
        if (session.Uid != own.Uid || session.Root != own.PersonalRoot)
            throw new InvalidInput("Login must use this computer's original test account.");
        await EnsureBackupIdleAsync(session.Provider, own.BackupPath, token);
        var before = await ReadBackupAsync(session.Provider, own.BackupPath, token);
        if (before.SnapshotId != own.Windows.Single(w => w.Phase == "Incremental").SnapshotId)
            throw new Blocked("The owned test snapshot changed after its device report. Stop test writers and inspect it.");
        string restored = Path.Combine(AppStorageRoot.TestRoot!, "after-both-devices");
        var engine = new RestoreEngine(new ManifestManager());
        var restore = await engine.RunRestorePackageAsync(session.Provider, before.Package, restored, cancellationToken: token);
        var restoredFiles = await ReadLocalFilesAsync(restored, token);
        AddResult(results, "OwnHashesAfterBothRun", restore.FailedFiles == 0 &&
            restore.IntegrityVerifiedFiles == own.CurrentFiles.Length && own.CurrentFiles.SequenceEqual(restoredFiles),
            new { Expected = own.CurrentFiles, Actual = restoredFiles },
            "After both devices finished, actual backup object bytes were recovered and compared against this machine's original file hashes.");
        var permission = ClassifyProbe(await session.Client.ListFilesAsync($"{peer.PersonalRoot}/{peer.BackupPath}", token));
        results.Add(permission with { TestId = "PeerReadPermission", Reason = permission.Reason + " The other device previously confirmed this exact directory as nonempty and owned." });
        await TestPeerUidGuardAsync(session, server, own, peer, trace, results, token);
        await EnsureBackupIdleAsync(session.Provider, own.BackupPath, token);
        var after = await ReadBackupAsync(session.Provider, own.BackupPath, token);
        AddResult(results, "OwnSnapshotAfterPeerRun", before.SnapshotId == after.SnapshotId, new { before.SnapshotId },
            "This account's committed snapshot remains unchanged after both runs and the read-only cross-account probe.");
        results.Add(new("PeerServerWritePermission", "RealNAS", "SKIP", null,
            "No write is attempted in the peer's personal space. The real server write-permission boundary remains untested; local UID guards have separate regression evidence."));
        results.Add(AuditOne(trace));
    }

    private static async Task TestPeerUidGuardAsync(Session session, Uri server, DeviceEvidence own, DeviceEvidence peer,
        ConcurrentQueue<string> trace, List<Result> results, CancellationToken token)
    {
        string source = Path.Combine(AppStorageRoot.TestSessionsRoot(), "uid-guard-source");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "marker.txt"), "UID boundary check", token);
        string sandbox = own.BackupPath.Split('/')[0];
        var settings = AppSettings.LoadIsolatedTestSession(Guid.NewGuid());
        settings.SetIsolatedTestBoundary(sandbox);
        using var manager = new TaskManager(settings, new SmbAuthService(), new LogService(Path.Combine(source, "logs")),
            experimentalVersionedBackup: true) { StorageProviderSupplier = () => session.Provider };
        var task = new SyncTask
        {
            TaskName = "Foreign UID guard", SourceFolder = source, DestinationRoot = sandbox,
            DestinationFolder = $"{sandbox}/{own.Role}/uid-guard", NasAccountUid = peer.Uid,
            NasUsername = session.Provider.CurrentUsername!, NasDeviceId = server.AbsoluteUri,
            ProviderType = StorageProviderType.FnOsPersonal, ScheduleMode = TaskSyncScheduleMode.ManualOnly,
            AutoSyncEnabled = false, RealtimeWatchEnabled = false
        };
        manager.AddTask(task);
        manager.SetNasOnlineState(true);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TaskStateChanged += state =>
        {
            if (state.TaskId == task.TaskId && state.State is TaskSyncState.ConfigurationError or TaskSyncState.Completed or TaskSyncState.Error)
                completion.TrySetResult(state.State == TaskSyncState.ConfigurationError && state.LastSyncStatus == "NAS 账号不匹配");
        };
        int before = StorageRequestCount(trace);
        try
        {
            manager.TriggerSync(task.TaskId);
            bool refused = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            AddResult(results, "PeerTaskWriteBlocked", refused && StorageRequestCount(trace) == before,
                new { TaskUid = peer.Uid, ConnectedUid = session.Uid },
                "The real scheduler rejects the other account's UID before sending a storage request. Authentication heartbeats are excluded.");
        }
        finally
        {
            if (!await manager.ShutdownAsync(TimeSpan.FromSeconds(30))) throw new Blocked("The UID guard worker did not drain.");
        }
    }
}
