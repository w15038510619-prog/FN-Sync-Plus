using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Storage.FnOs;

namespace FNSyncPlus.Acceptance;

public static partial class AcceptanceCli
{
    private static readonly string[] DualTests = ["AuthA", "AuthB", "DistinctUid", "WorkerStop", "TokenIsolation", "SameNameTasks", "RequestAudit"];
    private static readonly HashSet<string> ObservedOperations = new(StringComparer.Ordinal)
    {
        "auth.publicKey", "auth.login", "auth.twoFactor", "user.info", "file.ls", "file.mkdir",
        "file.checkUpload", "file.mv", "file.cp", "file.rename", "http.upload", "http.downloadToken", "http.download"
    };

    private sealed record Session(FnOsApiClient Client, FnOsPersonalStorageProvider Provider, int Uid, string Root) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }

    private static async Task<Session> LoginAsync(Uri server, string? pin, string label, CancellationToken token,
        Action<string>? observer = null)
    {
        if (Console.IsInputRedirected)
            throw new Blocked("An interactive console is required for masked NAS credentials.");
        var client = new FnOsApiClient(pin, observer ?? (_ => { }));
        var provider = new FnOsPersonalStorageProvider(client);
        try
        {
            Console.Error.Write($"Account {label} username: ");
            string username = Console.ReadLine()?.Trim() ?? "";
            if (username.Length == 0) throw new Blocked("A test account was not provided.");
            string password = ReadSecret($"Account {label} password (hidden): ");
            if (password.Length == 0) throw new Blocked("A test password was not provided.");
            var auth = await provider.ConnectAndAuthenticateAsync(server.AbsoluteUri, username, password, ct: token);
            password = string.Empty;
            if (auth.TwoFactorRequired && auth.TwoFactorAccessToken is { Length: > 0 } accessToken)
            {
                string code = ReadSecret($"Account {label} verification code (hidden): ");
                if (code.Length == 0) throw new Blocked("A verification code was not provided.");
                auth = await provider.VerifyTwoFactorCodeAsync(accessToken, code, username, token);
            }
            if (!auth.Success || auth.UserUid is not > 0 || auth.UserUid != client.CurrentUid ||
                client.CurrentVolumeId is not > 0 || string.IsNullOrEmpty(auth.PersonalRootPath) ||
                auth.PersonalRootPath != $"vol{client.CurrentVolumeId}/{auth.UserUid}")
                throw new Blocked("Authentication or server-confirmed personal-space identity could not be established.");
            return new(client, provider, auth.UserUid.Value, auth.PersonalRootPath);
        }
        catch
        {
            await provider.DisposeAsync();
            throw;
        }
    }

    private static string ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);
        char[] buffer = new char[1024];
        int length = 0;
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) return new string(buffer, 0, length);
                if (key.Key == ConsoleKey.Escape) throw new Blocked("Credential entry was canceled.");
                if (key.Key == ConsoleKey.Backspace && length > 0) buffer[--length] = '\0';
                if (char.IsControl(key.KeyChar)) continue;
                if (length == buffer.Length) throw new InvalidInput("Credential input is too long.");
                buffer[length++] = key.KeyChar;
            }
        }
        finally
        {
            Array.Clear(buffer);
            Console.Error.WriteLine();
        }
    }

    private static async Task DualAccountAsync(Options options, List<Result> results, CancellationToken token)
    {
        if (!options.Has("--authorize-nas") || Console.IsInputRedirected ||
            !options.Has("--server") || !options.Has("--root-a") || !options.Has("--root-b"))
        {
            foreach (var id in DualTests)
                results.Add(new(id, "RealNAS", "BLOCKED", null, "Two authorized NAS accounts, their test directories, and an interactive console are required."));
            return;
        }

        var server = Server(options);
        string rootA = SandboxPath(options.Require("--root-a"));
        string rootB = SandboxPath(options.Require("--root-b"));
        if (rootA.Contains('/') || rootB.Contains('/'))
            throw new InvalidInput("Each account must specify its own top-level Codex_FN_Test_ directory.");

        var traceA = new ConcurrentQueue<string>();
        var traceB = new ConcurrentQueue<string>();

        async Task<Session?> AuthenticateAsync(string label, ConcurrentQueue<string> trace)
        {
            try
            {
                var session = await LoginAsync(server, options.Get("--cert-sha256"), label, token, trace.Enqueue);
                results.Add(new($"Auth{label}", "RealNAS", "PASS", new { session.Uid }, "Authenticated and read the UID from server user.info."));
                return session;
            }
            catch (Exception error)
            {
                results.Add(Error($"Auth{label}", "RealNAS", error));
                return null;
            }
        }

        await using var a = await AuthenticateAsync("A", traceA);
        await using var b = await AuthenticateAsync("B", traceB);
        if (a == null || b == null)
        {
            foreach (var id in DualTests.Skip(2))
                results.Add(new(id, "RealNAS", "BLOCKED", null, "Both independent authenticated sessions are required."));
            return;
        }

        bool distinct = a.Uid != b.Uid && a.Root != b.Root;
        results.Add(new("DistinctUid", "RealNAS", distinct ? "PASS" : "FAIL",
            new { UidA = a.Uid, UidB = b.Uid }, distinct ? "The authenticated personal-space identities differ." : "These credentials resolve to the same NAS identity."));
        results.Add(new("WorkerStop", "RealNAS", "BLOCKED", null,
            "No deterministic active-worker checkpoint during a real account switch or disconnect is available in this runner."));
        if (!distinct)
        {
            foreach (var id in DualTests.Skip(4))
                results.Add(new(id, "RealNAS", "BLOCKED", null, "Distinct server UIDs are required."));
            return;
        }

        bool owned = false;
        try
        {
            var itemA = await a.Provider.GetItemAsync(rootA, token);
            var itemB = await b.Provider.GetItemAsync(rootB, token);
            owned = itemA is { IsDirectory: true } && itemB is { IsDirectory: true } &&
                itemA.OwnerUid == a.Uid && itemB.OwnerUid == b.Uid;
        }
        catch (Exception error)
        {
            results.Add(Error("SandboxOwnership", "RealNAS", error));
        }

        if (!owned)
        {
            foreach (var id in DualTests.Skip(4))
                results.Add(new(id, "RealNAS", "BLOCKED", null, "Both existing test directories must be confirmed as owned by the corresponding UID."));
            return;
        }

        try
        {
            string absoluteB = $"{b.Root}/{rootB}";
            var control = await b.Client.ListFilesAsync(absoluteB, token);
            if (!control.IsSuccess || control.Files is not { Count: > 0 })
                results.Add(new("TokenIsolation", "RealNAS", "BLOCKED", new { control.ErrNo },
                    "Account B must successfully list a nonempty sandbox. Put a harmless test marker in it and retry."));
            else
                results.Add(ClassifyProbe(await a.Client.ListFilesAsync(absoluteB, token)));
        }
        catch (Exception error)
        {
            results.Add(Error("TokenIsolation", "RealNAS", error));
        }

        if (!options.Has("--allow-test-writes"))
        {
            results.Add(new("SameNameTasks", "RealNAS", "BLOCKED", null, "Explicit permission to create new backup children in both test directories is required."));
            results.Add(Audit(traceA, traceB, false));
            return;
        }

        bool completed = false;
        try
        {
            string child = $"Acceptance_{Guid.NewGuid():N}";
            var taskA = await RunPersonalBackupAsync(a, server, rootA, child, token);
            var taskB = await RunPersonalBackupAsync(b, server, rootB, child, token);
            completed = true;
            bool isolated = taskA.Uid == a.Uid && taskB.Uid == b.Uid && taskA.TaskId != taskB.TaskId && taskA.FileSha256 != taskB.FileSha256;
            results.Add(new("SameNameTasks", "RealNAS", isolated ? "PASS" : "FAIL", new { TaskA = taskA, TaskB = taskB },
                isolated ? "Both scheduler tasks published and independently restored their own marker in their verified personal space." : "Task ownership or marker evidence did not match."));
        }
        catch (Exception error)
        {
            results.Add(Error("SameNameTasks", "RealNAS", error));
        }
        results.Add(Audit(traceA, traceB, completed));
    }

    private static Result ClassifyProbe(FnOsFileListResponse result)
    {
        string outcome = result.IsSuccess ? result.Files?.Count switch
        {
            > 0 => "Accessible",
            0 => "EmptyDirectory",
            _ => "IncompleteResponse"
        } : result.ErrNo switch
        {
            403 => "PermissionDenied",
            404 => "PathNotFound",
            401 => "AuthenticationRejected",
            _ => "UnknownReply"
        };
        string status = outcome == "PermissionDenied" ? "PASS" : outcome == "Accessible" ? "FAIL" : "BLOCKED";
        return new("TokenIsolation", "RealNAS", status,
            new { Outcome = outcome, ServerCode = result.ErrNo, ExactSameRequestPath = true }, outcome switch
            {
                "PermissionDenied" => "The server explicitly denied account A access to the same path account B listed.",
                "Accessible" => "Account A successfully listed content in account B's authorized sandbox.",
                "EmptyDirectory" => "The server returned an empty list, which cannot prove permission isolation.",
                "PathNotFound" => "The server reported a missing path, which cannot prove permission isolation.",
                "AuthenticationRejected" => "The server rejected the session rather than explicitly denying access to the directory.",
                _ => "The server response did not definitively establish a permission denial."
            });
    }

    private sealed record TaskEvidence(string TaskId, int Uid, string SnapshotId, string FileSha256);

    private static async Task<TaskEvidence> RunPersonalBackupAsync(Session session, Uri server, string sandbox, string child,
        CancellationToken token)
    {
        string target = $"{sandbox}/{child}";
        if (await session.Provider.GetItemAsync(target, token) != null)
            throw new Blocked("The intended test backup directory already exists.");
        string profile = Path.Combine(AppStorageRoot.TestSessionsRoot(), $"AcceptanceTask_{Guid.NewGuid():N}");
        string source = Path.Combine(profile, "source");
        Directory.CreateDirectory(source);
        byte[] contents = Encoding.UTF8.GetBytes($"FN Sync Plus synthetic marker {Guid.NewGuid():N}");
        string expected = Convert.ToHexString(SHA256.HashData(contents));
        await File.WriteAllBytesAsync(Path.Combine(source, "marker.txt"), contents, token);

        var settings = AppSettings.LoadIsolatedTestSession(Guid.NewGuid());
        settings.SetIsolatedTestBoundary(sandbox);
        using var manager = new TaskManager(settings, new SmbAuthService(), new LogService(Path.Combine(profile, "logs")),
            experimentalVersionedBackup: true) { StorageProviderSupplier = () => session.Provider };
        var task = new SyncTask
        {
            TaskName = "Acceptance same-name backup",
            SourceFolder = source,
            DestinationRoot = sandbox,
            DestinationFolder = target,
            ProviderType = StorageProviderType.FnOsPersonal,
            NasDeviceId = server.AbsoluteUri,
            NasUsername = session.Provider.CurrentUsername ?? "",
            NasAccountUid = session.Uid,
            AutoSyncEnabled = false,
            RealtimeWatchEnabled = false,
            ScheduleMode = TaskSyncScheduleMode.ManualOnly
        };

        var terminal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TaskStateChanged += state =>
        {
            if (state.TaskId == task.TaskId && state.State is TaskSyncState.Completed or TaskSyncState.Error)
                terminal.TrySetResult(state.State == TaskSyncState.Completed && state.LastSuccessTime != null);
        };
        try
        {
            manager.AddTask(task);
            manager.SetNasOnlineState(true);
            manager.TriggerSync(task.TaskId);
            if (!await terminal.Task.WaitAsync(TimeSpan.FromMinutes(5), token))
                throw new InvalidInput("The real backup worker reported failure.");
        }
        finally
        {
            if (!await manager.ShutdownAsync(TimeSpan.FromSeconds(30)))
                throw new Blocked("The test scheduler did not drain within the timeout.");
        }

        if (await session.Provider.GetItemAsync(target, token) is not { IsDirectory: true } directory || directory.OwnerUid != session.Uid)
            throw new InvalidInput("The new backup directory did not report the expected owner UID.");
        var backup = await ReadBackupAsync(session.Provider, target, token);
        var engine = new RestoreEngine(new ManifestManager());
        string restore = Path.Combine(profile, "restore");
        var report = await engine.RunRestorePackageAsync(session.Provider, backup.Package, restore,
            ConflictResolution.SkipExisting, token);
        if (report.RestoredFiles != 1 || report.IntegrityVerifiedFiles != 1 || report.FailedFiles != 0 ||
            !File.Exists(Path.Combine(restore, "marker.txt")) ||
            !(await File.ReadAllBytesAsync(Path.Combine(restore, "marker.txt"), token)).SequenceEqual(contents))
            throw new InvalidInput("The marker recovered from this account did not match its original bytes.");
        return new(task.TaskId, session.Uid, backup.SnapshotId, expected);
    }

    private static Result Audit(IEnumerable<string> traceA, IEnumerable<string> traceB, bool completed)
    {
        var entriesA = traceA.ToArray();
        var entriesB = traceB.ToArray();
        var entries = entriesA.Concat(entriesB).ToArray();
        bool deletion = entries.Any(method => method is "file.rm" or "file.delete" or "file.trash");
        int unclassified = entries.Count(operation => !ObservedOperations.Contains(operation));
        bool hasBackups = completed && entriesA.Contains("http.upload") && entriesB.Contains("http.upload");
        bool pass = hasBackups && unclassified == 0;
        return new("RequestAudit", "RealNAS", deletion ? "FAIL" : pass ? "PASS" : "BLOCKED", new
        {
            AccountA = Summarize(entriesA), AccountB = Summarize(entriesB), Unclassified = unclassified, DeletionObserved = deletion
        }, deletion ? "A destructive request was observed." : pass
            ? "Both completed backup sessions have outgoing operation traces and no deletion request was observed."
            : "Completed backup traces from both accounts and classification of every operation are required.");
    }

    private static object Summarize(string[] operations) => operations.Where(ObservedOperations.Contains)
        .GroupBy(operation => operation).ToDictionary(group => group.Key, group => group.Count());
}
