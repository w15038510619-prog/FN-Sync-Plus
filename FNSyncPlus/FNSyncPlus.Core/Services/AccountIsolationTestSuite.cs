using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Multi-account isolation acceptance (P1).
///
/// Two accounts on the same NAS must never be able to read or write each other's backup space. The
/// product enforces this in two independent layers, and both are covered here:
///   1. task ownership — a task records the UID/name of the account that created it, and migrating or
///      resuming it under a different account is refused instead of guessed;
///   2. session sandbox — an isolated session may only touch a parent folder it was explicitly granted,
///      so a task cannot point at another account's space.
///
/// A real two-account NAS run is not reproducible on a single test machine; those cases are reported
/// as BLOCKED in the acceptance document rather than simulated into a false PASS.
/// </summary>
public sealed class AccountIsolationTestSuite
{
    private const string SandboxRoot = "Codex_FN_Test_Ui";

    public async Task<IReadOnlyList<UpdateSuiteCase>> RunAsync(string workspace, CancellationToken token = default)
    {
        Directory.CreateDirectory(workspace);
        var cases = new List<UpdateSuiteCase>();

        async Task Case(string name, Func<Task<(bool Passed, string Detail)>> body)
        {
            try
            {
                var (passed, detail) = await body().ConfigureAwait(false);
                cases.Add(new UpdateSuiteCase(name, passed, detail));
            }
            catch (Exception ex)
            {
                cases.Add(new UpdateSuiteCase(name, false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        // A local SMB-backed provider only authenticates when its root actually exists on disk.
        string NasRoot(string name)
        {
            string root = Path.Combine(workspace, "nas", name);
            Directory.CreateDirectory(root);
            return root;
        }

        string NewSource(string name)
        {
            string source = Path.Combine(workspace, "sources", name);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "note.txt"), name);
            return source;
        }

        SyncTask NewTask(string source, int? uid, string? username, string target = SandboxRoot + "/task-1") => new()
        {
            TaskId = Guid.NewGuid().ToString("N"),
            TaskName = "账号隔离验收任务",
            SourceFolder = source,
            DestinationFolder = target,
            ProviderType = StorageProviderType.FnOsPersonal,
            NasAccountUid = uid,
            NasUsername = username ?? string.Empty,
            Enabled = false
        };

        StorageAuthResult Account(int? uid, string? name, bool success = true) => new()
        {
            Success = success,
            UserUid = uid,
            AuthenticatedUser = name,
            PersonalRootPath = "我的文件"
        };

        LegacyTaskMigrationInspector NewInspector() => new();

        // ---------------------------------------------------------------- task ownership
        await Case("任务记录的 UID 与当前账号不一致时归类为账号不明", async () =>
        {
            var inspector = NewInspector();
            var provider = new SlowLocalProvider(NasRoot("a"));
            var task = NewTask(NewSource("uid-mismatch"), 1000, "alice");
            var finding = await inspector.InspectTaskAsync(task, Account(2000, "alice"), provider);
            return (finding.Classification == LegacyMigrationClassification.AccountUnverified &&
                    finding.Blockers.Any(b => b.Contains("1000") && b.Contains("2000")),
                $"class={finding.Classification} blockers={string.Join('|', finding.Blockers)}");
        }).ConfigureAwait(false);

        await Case("UID 一致时不判定为账号不明", async () =>
        {
            var inspector = NewInspector();
            var provider = new SlowLocalProvider(NasRoot("a"));
            var task = NewTask(NewSource("uid-same"), 1000, "alice");
            var finding = await inspector.InspectTaskAsync(task, Account(1000, "alice"), provider);
            bool noAccountBlocker = !finding.Blockers.Any(b => b.Contains("UID") || b.Contains("账号"));
            return (finding.Classification != LegacyMigrationClassification.AccountUnverified && noAccountBlocker,
                $"class={finding.Classification} blockers={string.Join('|', finding.Blockers)}");
        }).ConfigureAwait(false);

        await Case("旧任务缺少 UID 时给出警告而不是静默放行", async () =>
        {
            var inspector = NewInspector();
            var provider = new SlowLocalProvider(NasRoot("a"));
            var task = NewTask(NewSource("uid-missing"), null, null);
            var finding = await inspector.InspectTaskAsync(task, Account(1000, "alice"), provider);
            return (finding.Warnings.Any(w => w.Contains("没有记录账号 UID")),
                $"warnings={string.Join('|', finding.Warnings)}");
        }).ConfigureAwait(false);

        await Case("未登录时拒绝确认任务归属", async () =>
        {
            var inspector = NewInspector();
            var provider = new SlowLocalProvider(NasRoot("a"));
            var task = NewTask(NewSource("not-signed-in"), 1000, "alice");
            var finding = await inspector.InspectTaskAsync(task, Account(null, null, success: false), provider);
            return (finding.Classification == LegacyMigrationClassification.AccountUnverified &&
                    finding.Blockers.Any(b => b.Contains("未完成账号登录")),
                $"class={finding.Classification} blockers={string.Join('|', finding.Blockers)}");
        }).ConfigureAwait(false);

        await Case("当前账号 UID 未知时拒绝推导个人空间归属", async () =>
        {
            var inspector = NewInspector();
            var provider = new SlowLocalProvider(NasRoot("a"));
            var task = NewTask(NewSource("uid-unknown"), 1000, "alice");
            var finding = await inspector.InspectTaskAsync(task, Account(null, "alice"), provider);
            return (finding.Blockers.Any(b => b.Contains("UID 未知")),
                $"blockers={string.Join('|', finding.Blockers)}");
        }).ConfigureAwait(false);

        await Case("账号名大小写不同也视为不同账号", async () =>
        {
            var inspector = NewInspector();
            var provider = new SlowLocalProvider(NasRoot("a"));
            var task = NewTask(NewSource("name-case"), 1000, "Alice");
            var finding = await inspector.InspectTaskAsync(task, Account(1000, "alice"), provider);
            return (finding.Classification == LegacyMigrationClassification.AccountUnverified &&
                    finding.Blockers.Any(b => b.Contains("账号名")),
                $"class={finding.Classification} blockers={string.Join('|', finding.Blockers)}");
        }).ConfigureAwait(false);

        await Case("同一份旧配置中分属两个账号的任务得到不同结论", async () =>
        {
            var inspector = NewInspector();
            var provider = new SlowLocalProvider(NasRoot("a"));
            var mine = NewTask(NewSource("pair-mine"), 1000, "alice", SandboxRoot + "/mine");
            var other = NewTask(NewSource("pair-other"), 2000, "bob", SandboxRoot + "/other");
            var settings = new AppSettings();
            settings.Tasks.Add(mine);
            settings.Tasks.Add(other);
            var inventory = await inspector.InspectAsync(settings, Path.Combine(workspace, "legacy-settings.json"),
                Account(1000, "alice"), provider);
            bool mineOk = inventory.Tasks[0].Classification != LegacyMigrationClassification.AccountUnverified;
            bool otherBlocked = inventory.Tasks[1].Classification == LegacyMigrationClassification.AccountUnverified;
            return (mineOk && otherBlocked,
                $"mine={inventory.Tasks[0].Classification} other={inventory.Tasks[1].Classification}");
        }).ConfigureAwait(false);

        // ---------------------------------------------------------------- session sandbox
        await Case("隔离会话拒绝沙盒之外的本地源目录", () =>
        {
            var settings = AppSettings.LoadIsolatedTestSession(Guid.NewGuid());
            settings.SetIsolatedTestBoundary(SandboxRoot);
            var task = NewTask(NewSource("outside-source"), 1000, "alice");
            try
            {
                settings.EnsureIsolatedTestTask(task);
                return Task.FromResult((false, "没有抛出异常，越界任务被接受"));
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult((true, ex.Message));
            }
        }).ConfigureAwait(false);

        await Case("隔离会话拒绝沙盒之外的目标目录", () =>
        {
            var settings = AppSettings.LoadIsolatedTestSession(Guid.NewGuid());
            settings.SetIsolatedTestBoundary(SandboxRoot);
            string sandboxSource = Path.Combine(Path.GetTempPath(), "FNSyncPlus_CodexTests", "iso-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandboxSource);
            var task = NewTask(sandboxSource, 1000, "alice", "其他账号的备份/secret");
            try
            {
                settings.EnsureIsolatedTestTask(task);
                return Task.FromResult((false, "没有抛出异常，跨账号目标被接受"));
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult((true, ex.Message));
            }
        }).ConfigureAwait(false);

        await Case("隔离会话拒绝用相对路径跳出沙盒", () =>
        {
            var settings = AppSettings.LoadIsolatedTestSession(Guid.NewGuid());
            settings.SetIsolatedTestBoundary(SandboxRoot);
            string sandboxSource = Path.Combine(Path.GetTempPath(), "FNSyncPlus_CodexTests", "iso-escape-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandboxSource);
            var task = NewTask(sandboxSource, 1000, "alice", SandboxRoot + "/../另一个账号空间");
            try
            {
                settings.EnsureIsolatedTestTask(task);
                return Task.FromResult((false, "没有抛出异常，路径穿越被接受"));
            }
            catch (InvalidOperationException ex)
            {
                return Task.FromResult((true, ex.Message));
            }
        }).ConfigureAwait(false);

        await Case("隔离会话接受沙盒内、同一账号的任务", () =>
        {
            var settings = AppSettings.LoadIsolatedTestSession(Guid.NewGuid());
            settings.SetIsolatedTestBoundary(SandboxRoot);
            string sandboxSource = Path.Combine(Path.GetTempPath(), "FNSyncPlus_CodexTests", "iso-ok-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandboxSource);
            var task = NewTask(sandboxSource, 1000, "alice");
            settings.EnsureIsolatedTestTask(task);
            return Task.FromResult((true, task.DestinationFolder));
        }).ConfigureAwait(false);

        await Case("两个隔离会话的配置互不可见", () =>
        {
            var firstId = Guid.NewGuid();
            var first = AppSettings.LoadIsolatedTestSession(firstId);
            first.Tasks.Add(NewTask(NewSource("session-a"), 1000, "alice"));
            first.Save();
            var reloaded = AppSettings.LoadIsolatedTestSession(firstId);
            var second = AppSettings.LoadIsolatedTestSession(Guid.NewGuid());
            return Task.FromResult((reloaded.Tasks.Count == 1 && second.Tasks.Count == 0,
                $"first={reloaded.Tasks.Count} second={second.Tasks.Count}"));
        }).ConfigureAwait(false);

        await Case("账号切换后旧任务的命名空间归属仍可核对", async () =>
        {
            // The recorded owner is the authoritative key: two tasks may share a display name and a
            // target folder name, but only the one whose UID matches the signed-in account is usable.
            var inspector = NewInspector();
            var provider = new SlowLocalProvider(NasRoot("b"));
            var alice = NewTask(NewSource("ns-alice"), 1000, "alice", SandboxRoot + "/shared");
            var bob = NewTask(NewSource("ns-bob"), 2000, "bob", SandboxRoot + "/shared");
            var asBob = Account(2000, "bob");
            var aliceFinding = await inspector.InspectTaskAsync(alice, asBob, provider);
            var bobFinding = await inspector.InspectTaskAsync(bob, asBob, provider);
            return (aliceFinding.Classification == LegacyMigrationClassification.AccountUnverified &&
                    bobFinding.Classification != LegacyMigrationClassification.AccountUnverified,
                $"alice={aliceFinding.Classification} bob={bobFinding.Classification}");
        }).ConfigureAwait(false);

        return cases;
    }
}
