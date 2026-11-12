using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Storage isolation suite (round 4, P0-1). Proves with REAL processes and REAL filesystem
/// evidence that a test environment rooted at FNSYNC_DATA_ROOT never touches production data:
///
///  * Every child process runs with FNSYNC_DATA_ROOT=&lt;isolated root&gt; and FNSYNC_STORAGE_AUDIT
///    recording every resolved application path; the parent asserts the audit contains ZERO
///    production paths and that files were really created only under the root.
///  * Production configuration trees are hashed before and after (read-only snapshots) and
///    must be byte-identical, including "still absent" when the machine has no production
///    config at all.
///  * A production-instance mutex held by the parent must not block or be taken by a test-root
///    instance, and a test process killed mid-run must leave production bytes untouched.
///
/// Nothing here moves, renames or deletes production files — the old "quarantine then restore"
/// pattern is gone for good.
/// </summary>
public sealed class StorageIsolationTestSuite
{
    private const string ChildScenarioArg = "--storage-isolation-child=";

    /// <summary>True when the current process is a spawned isolation child.</summary>
    public static bool IsChild(IReadOnlyList<string> args) =>
        args.Any(a => a.StartsWith(ChildScenarioArg, StringComparison.Ordinal));

    /// <summary>
    /// Entry point for a spawned child process. Returns the process exit code
    /// (0 = success, 1 = failure, 42 = rejection as expected).
    /// </summary>
    public static async Task<int> RunChildAsync(IReadOnlyList<string> args, CancellationToken token = default)
    {
        string scenario = args.First(a => a.StartsWith(ChildScenarioArg, StringComparison.Ordinal))[ChildScenarioArg.Length..];
        string resultPath = args.First(a => a.StartsWith("--child-result=", StringComparison.Ordinal))["--child-result=".Length..];
        var result = new Dictionary<string, object?>
        {
            ["scenario"] = scenario,
            ["pid"] = Environment.ProcessId,
        };
        int exitCode = 0;
        try
        {
            // NOTE: resolving TestRoot is itself the guard trigger for the "guard" scenario —
            // it must stay inside the try so the rejection is recorded instead of crashing.
            result["root"] = AppStorageRoot.TestRoot;
            switch (scenario)
            {
                case "basic":
                    RunChildBasic(result);
                    break;
                case "hold":
                    RunChildBasic(result);
                    await WriteResult(resultPath, result).ConfigureAwait(false);
                    result = null; // already written; the loop below is killed by the parent
                    for (int i = 0; i < 600; i++) await Task.Delay(1000, token).ConfigureAwait(false);
                    break;
                case "mutex":
                    RunChildMutex(result);
                    break;
                case "inherit":
                    RunChildInherit(result, resultPath);
                    break;
                case "inherit-report":
                    // Grandchild: nothing was set up for it explicitly — it must resolve the
                    // SAME root via the inherited environment variable, which is exactly how
                    // the updater and the relaunched application inherit the isolation.
                    result["resolved_root"] = AppStorageRoot.TestRoot;
                    result["resolved_roaming"] = AppStorageRoot.RoamingAppDataDir();
                    result["resolved_local"] = AppStorageRoot.LocalAppDataDir();
                    break;
                case "guard":
                    // The root passed in overlaps production; the very first resolution must
                    // throw. Reaching this line means the guard failed open.
                    _ = AppStorageRoot.RoamingAppDataDir();
                    result["guard_error"] = "未触发拒绝";
                    exitCode = 1;
                    break;
                default:
                    throw new InvalidOperationException("未知子进程场景: " + scenario);
            }
        }
        catch (InvalidOperationException ex) when (scenario == "guard")
        {
            result ??= new Dictionary<string, object?> { ["scenario"] = scenario };
            result["guard_rejected"] = ex.Message;
            exitCode = 42;
        }
        catch (Exception ex)
        {
            result ??= new Dictionary<string, object?> { ["scenario"] = scenario };
            result["error"] = ex.GetType().Name + ": " + ex.Message;
            exitCode = 1;
        }
        if (result != null) await WriteResult(resultPath, result).ConfigureAwait(false);
        return exitCode;
    }

    private static void RunChildBasic(Dictionary<string, object?> result)
    {
        // Resolve the full authority surface; every path must be under the test root.
        string roaming = AppStorageRoot.RoamingAppDataDir();
        string local = AppStorageRoot.LocalAppDataDir();
        string temp = AppStorageRoot.TempRoot();
        string sessions = AppStorageRoot.TestSessionsRoot();
        result["resolved"] = new Dictionary<string, object?>
        {
            ["roaming"] = roaming, ["local"] = local, ["temp"] = temp, ["sessions"] = sessions,
        };

        // Real configuration write: an isolated session saves settings under the root only.
        var sessionId = Guid.NewGuid();
        var settings = AppSettings.LoadIsolatedTestSession(sessionId);
        settings.Tasks.Clear();
        settings.RememberCredentials = false;
        settings.Save();
        result["session_settings"] = settings.IsolatedSessionPath;
        result["session_tasks"] = settings.Tasks.Count;
        result["session_remember"] = settings.RememberCredentials;

        // Real credential write through the production code path (routed to the root).
        string credentialPath = Path.Combine(roaming, "credentials.dat");
        bool saved = SmbAuthService.SaveCredentials("192.0.2.10", "isolation-probe", "not-a-real-password", credentialPath);
        result["credential_saved"] = saved;
        result["credential_path"] = credentialPath;
        var loaded = SmbAuthService.LoadCredentials(credentialPath);
        result["credential_roundtrip"] = loaded.HasValue && loaded.Value.Username == "isolation-probe";

        // Real update-layout write: versioned pointers and the data-format marker stay under
        // the root as well (the default marker path follows the authority).
        string installRoot = Path.Combine(temp, "install");
        var layout = new VersionedInstallLayout(installRoot);
        result["layout_root"] = layout.Root;
        result["marker_path"] = layout.DataFormatMarkerPath;
        var guard = new DataFormatGuard(layout.DataFormatMarkerPath, 1);
        result["marker_claimed"] = guard.TryClaim("0.6.0-test", out _);

        // Auto-connect precondition: an isolated session carries no enabled task and no
        // remembered credential, so nothing can dial a NAS.
        result["no_enabled_tasks"] = settings.Tasks.Count(t => t.Enabled) == 0;
        result["production_access"] = new[]
            { roaming, local, temp, sessions, settings.IsolatedSessionPath!, credentialPath, layout.Root, layout.DataFormatMarkerPath }
            .Where(AppStorageRoot.IsProductionPath).ToArray();
    }

    /// <summary>
    /// Spawns a grandchild WITHOUT touching its environment block: ProcessStartInfo copies the
    /// parent environment by default, so the grandchild inherits FNSYNC_DATA_ROOT exactly the
    /// way the updater and the relaunched app inherit it from the real application.
    /// </summary>
    private static void RunChildInherit(Dictionary<string, object?> result, string resultPath)
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位测试运行器自身进程路径");
        string grandResultPath = resultPath + ".grand.json";
        var start = new ProcessStartInfo(exe, $"{ChildScenarioArg}inherit-report --child-result=\"{grandResultPath}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var grand = Process.Start(start) ?? throw new InvalidOperationException("无法启动孙进程");
        grand.StandardOutput.ReadToEnd();
        grand.StandardError.ReadToEnd();
        if (!grand.WaitForExit(30000))
        {
            try { grand.Kill(); } catch { }
            throw new TimeoutException("孙进程未在 30 秒内退出");
        }
        if (!File.Exists(grandResultPath)) throw new IOException("孙进程未写出结果文件");
        using var document = JsonDocument.Parse(File.ReadAllText(grandResultPath));
        string? grandRoot = document.RootElement.GetProperty("resolved_root").GetString();
        result["grand_root"] = grandRoot;
        result["inherit_matches"] = string.Equals(grandRoot, AppStorageRoot.TestRoot, StringComparison.OrdinalIgnoreCase);
        if (result["inherit_matches"] is not true)
            throw new InvalidOperationException("孙进程解析的测试数据根与父进程不一致");
    }

    private static void RunChildMutex(Dictionary<string, object?> result)
    {
        string name = SingleInstanceCoordinator.DesktopInstanceName + (AppStorageRoot.SingleInstanceSuffix() ?? "");
        using var coordinator = new SingleInstanceCoordinator(name);
        result["mutex_name"] = name;
        result["mutex_is_primary"] = coordinator.IsPrimary;
        result["production_mutex_name"] = SingleInstanceCoordinator.DesktopInstanceName;
    }

    private static async Task WriteResult(string path, IReadOnlyDictionary<string, object?> result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result,
            new JsonSerializerOptions { WriteIndented = true })).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- parent mode

    public async Task<IReadOnlyList<UpdateSuiteCase>> RunAsync(string workspace, CancellationToken token = default)
    {
        Directory.CreateDirectory(workspace);
        var cases = new List<UpdateSuiteCase>();
        var auditFiles = new List<string>();
        int childCounter = 0;

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

        var productionSnapshotBefore = SnapshotProduction();

        (Process proc, string auditPath) SpawnChild(string scenario, string root, bool inheritOnly = false)
        {
            string audit = Path.Combine(workspace, $"audit-{++childCounter}.log");
            auditFiles.Add(audit);
            string exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法定位测试运行器自身进程路径");
            var start = new ProcessStartInfo(exe,
                $"{ChildScenarioArg}{scenario} --child-result=\"{Path.Combine(workspace, $"child-{scenario}-{childCounter}.json")}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.EnvironmentVariables[AppStorageRoot.OverrideEnvironmentVariable] = root;
            start.EnvironmentVariables[AppStorageRoot.AuditEnvironmentVariable] = audit;
            var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离子进程");
            return (process, audit);
        }

        async Task<(int ExitCode, JsonDocument? Result)> RunChildToCompletion(string scenario, string root, int timeoutMs = 60000)
        {
            var (process, _) = SpawnChild(scenario, root);
            string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            cancel.CancelAfter(timeoutMs);
            try { await process.WaitForExitAsync(cancel.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                return (-1, null);
            }
            string resultFile = Path.Combine(workspace, $"child-{scenario}-{childCounter}.json");
            JsonDocument? document = null;
            if (File.Exists(resultFile))
                document = JsonDocument.Parse(await File.ReadAllTextAsync(resultFile, token).ConfigureAwait(false));
            if (document == null)
                return (process.ExitCode, null);
            return (process.ExitCode, document);
        }

        static bool AuditHasNoProduction(string auditPath)
        {
            if (!File.Exists(auditPath)) return false;
            foreach (var line in File.ReadLines(auditPath))
            {
                int tab = line.LastIndexOf('\t');
                string path = tab >= 0 ? line[(tab + 1)..] : line;
                if (path.Length > 0 && AppStorageRoot.IsProductionPath(path)) return false;
            }
            return true;
        }

        string root1 = Path.Combine(workspace, "testdata-main");
        string root2 = Path.Combine(workspace, "testdata-empty-env");

        // ---- 1. 正式配置存在时，测试独立执行（真实子进程 + 审计 + 磁盘证据）----
        JsonDocument? mainResult = null;
        var (basicExit, basicDoc) = await RunChildToCompletion("basic", root1).ConfigureAwait(false);
        mainResult = basicDoc;
        Case("正式配置存在时测试会话独立执行（真实进程）", () =>
        {
            bool productionExists = File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FNSyncPlus", "settings.json"));
            bool ok = basicExit == 0 && mainResult != null &&
                File.Exists(Path.Combine(root1, "roaming", "FNSyncPlus", "credentials.dat")) &&
                mainResult.RootElement.GetProperty("credential_saved").GetBoolean() &&
                mainResult.RootElement.GetProperty("credential_roundtrip").GetBoolean();
            return (ok, $"exit={basicExit} productionSettingsExisted={productionExists} root={root1}");
        });

        // ---- 2. 空环境下仍能执行（审计证明解析不依赖生产任何文件）----
        var (emptyExit, emptyDoc) = await RunChildToCompletion("basic", root2).ConfigureAwait(false);
        Case("正式配置不存在与否不影响测试执行（审计零生产访问）", () =>
        {
            var audits = Directory.GetFiles(workspace, "audit-*.log");
            bool allClean = audits.Length >= 2 && audits.All(AuditHasNoProduction);
            return (emptyExit == 0 && emptyDoc != null && allClean,
                $"exit={emptyExit} audits={audits.Length} allZeroProduction={allClean}");
        });

        // ---- 3. 模拟正式客户端运行（持有生产互斥锁）时测试不冲突也不改配置 ----
        string root3 = Path.Combine(workspace, "testdata-mutex");
        using (var productionInstance = new SingleInstanceCoordinator(SingleInstanceCoordinator.DesktopInstanceName))
        {
            var (mutexExit, mutexDoc) = await RunChildToCompletion("mutex", root3).ConfigureAwait(false);
            Case("正式客户端运行时测试实例不占用其互斥锁", () =>
            {
                if (mutexExit != 0 || mutexDoc == null) return (false, $"exit={mutexExit}");
                string childName = mutexDoc.RootElement.GetProperty("mutex_name").GetString() ?? "";
                bool primary = mutexDoc.RootElement.GetProperty("mutex_is_primary").GetBoolean();
                bool distinct = !string.Equals(childName, SingleInstanceCoordinator.DesktopInstanceName, StringComparison.Ordinal);
                return (primary && distinct && productionInstance.IsPrimary,
                    $"childPrimary={primary} distinctName={distinct} productionPrimary={productionInstance.IsPrimary}");
            });
        }

        // ---- 4. 测试版启动不读取生产凭据 ----
        Case("测试版不读取生产凭据（凭据文件只在测试根下）", () =>
        {
            if (mainResult == null) return (false, "缺少 basic 子进程结果");
            string credentialPath = mainResult.RootElement.GetProperty("credential_path").GetString() ?? "";
            bool underRoot = credentialPath.StartsWith(Path.GetFullPath(root1), StringComparison.OrdinalIgnoreCase);
            var audits = Directory.GetFiles(workspace, "audit-*.log");
            bool noProductionCredential = audits.All(a =>
                !File.ReadLines(a).Any(l => l.EndsWith("credentials.dat", StringComparison.OrdinalIgnoreCase) &&
                    AppStorageRoot.IsProductionPath(l[(l.LastIndexOf('\t') + 1)..])));
            return (underRoot && noProductionCredential,
                $"credential={credentialPath} underRoot={underRoot} auditClean={noProductionCredential}");
        });

        // ---- 5. 测试版不会自动连接真实 NAS ----
        Case("测试会话无启用任务且无凭据可自动连接", () =>
        {
            if (mainResult == null) return (false, "缺少 basic 子进程结果");
            bool noTasks = mainResult.RootElement.GetProperty("no_enabled_tasks").GetBoolean();
            bool noRemember = !mainResult.RootElement.GetProperty("session_remember").GetBoolean();
            int tasks = mainResult.RootElement.GetProperty("session_tasks").GetInt32();
            return (noTasks && noRemember && tasks == 0,
                $"tasks={tasks} noEnabled={noTasks} remember={!noRemember}");
        });

        // ---- 6+7. 测试进程被强制结束后生产配置原地存在且字节不变 ----
        string rootKill = Path.Combine(workspace, "testdata-kill");
        var (holdProcess, _) = SpawnChild("hold", rootKill);
        string holdResult = Path.Combine(workspace, $"child-hold-{childCounter}.json");
        bool markerSeen = false;
        for (int i = 0; i < 100 && !markerSeen; i++)
        {
            if (File.Exists(holdResult)) markerSeen = true;
            else await Task.Delay(150, token).ConfigureAwait(false);
        }
        if (markerSeen)
        {
            try { holdProcess.Kill(); } catch { }
            try { await holdProcess.WaitForExitAsync(token).ConfigureAwait(false); } catch { }
        }
        else
        {
            try { holdProcess.Kill(); } catch { }
        }
        Case("测试进程被强制结束后生产配置原地存在", () =>
        {
            var after = SnapshotProduction();
            bool unchanged = SnapshotEquals(productionSnapshotBefore, after);
            return (markerSeen && unchanged,
                $"childRan={markerSeen} productionBytesIdentical={unchanged}");
        });
        Case("强制结束后生产 APPDATA 与 LOCALAPPDATA 两棵目录树字节不变", () =>
        {
            var after = SnapshotProduction();
            return (SnapshotEquals(productionSnapshotBefore, after),
                $"filesBefore={productionSnapshotBefore.Count} filesAfter={after.Count}");
        });

        // ---- 8. 测试安装/更新不影响生产版本指针与格式标记 ----
        Case("测试更新布局不影响生产版本指针与数据格式标记", () =>
        {
            if (mainResult == null) return (false, "缺少 basic 子进程结果");
            string layoutRoot = mainResult.RootElement.GetProperty("layout_root").GetString() ?? "";
            string markerPath = mainResult.RootElement.GetProperty("marker_path").GetString() ?? "";
            bool markerUnderRoot = !AppStorageRoot.IsProductionPath(markerPath);
            bool layoutUnderRoot = !AppStorageRoot.IsProductionPath(layoutRoot);
            bool markerClaimed = mainResult.RootElement.GetProperty("marker_claimed").GetBoolean();
            string productionLocal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FNSyncPlus");
            bool noProductionPointers = !File.Exists(Path.Combine(productionLocal, "current.json")) &&
                !File.Exists(Path.Combine(productionLocal, "previous.json"));
            return (markerUnderRoot && layoutUnderRoot && markerClaimed && noProductionPointers,
                $"markerUnderRoot={markerUnderRoot} layoutUnderRoot={layoutUnderRoot} claimed={markerClaimed} noProdPointers={noProductionPointers}");
        });

        // ---- 9. 测试数据目录与生产目录互不重叠（词法 + 审计全集）----
        Case("测试数据根与生产目录互不重叠", () =>
        {
            var audits = Directory.GetFiles(workspace, "audit-*.log");
            var paths = audits.SelectMany(a => File.Exists(a)
                ? File.ReadLines(a).Select(l => l[(l.LastIndexOf('\t') + 1)..])
                : Array.Empty<string>()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool noneProduction = paths.All(p => !AppStorageRoot.IsProductionPath(p));
            bool rootsClean = !AppStorageRoot.IsProductionPath(root1) && !AppStorageRoot.IsProductionPath(root2);
            return (noneProduction && rootsClean && paths.Count > 0,
                $"auditedPaths={paths.Count} noneProduction={noneProduction} rootsClean={rootsClean}");
        });

        // ---- 防护：生产路径被拒绝作为测试根 ----
        var (guardExit, guardDoc) = await RunChildToCompletion("guard",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FNSyncPlus")).ConfigureAwait(false);
        Case("生产目录被拒绝用作测试数据根", () =>
        {
            return (guardExit == 42 && guardDoc != null &&
                    guardDoc.RootElement.TryGetProperty("guard_rejected", out _),
                $"exit={guardExit}（42=按预期拒绝）");
        });

        // ---- 环境变量继承（更新器/重启进程自动继承隔离）----
        string rootInherit = Path.Combine(workspace, "testdata-inherit");
        var (parentExit, _) = await RunChildToCompletion("inherit", rootInherit).ConfigureAwait(false);
        // inherit 场景本身就是“孙进程”语义：子进程不经显式设置即解析出同一根。
        Case("隔离数据根经环境变量自动继承（更新器与重启场景）", () =>
        {
            return (parentExit == 0, $"exit={parentExit}");
        });

        // ---- 10. 全部测试结束后生产配置 SHA-256 不变 ----
        Case("全部隔离测试结束后生产配置 SHA-256 保持不变", () =>
        {
            var after = SnapshotProduction();
            bool same = SnapshotEquals(productionSnapshotBefore, after);
            return (same,
                $"before={productionSnapshotBefore.Count} after={after.Count} identical={same}");
        });

        foreach (var doc in new[] { mainResult, emptyDoc, guardDoc })
            doc?.Dispose();
        return cases;
    }

    // ------------------------------------------------------------- production snapshot

    private static IReadOnlyDictionary<string, string> SnapshotProduction()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in AppStorageRoot.EnumerateProductionPaths())
        {
            if (!Directory.Exists(root)) { map[root + Path.DirectorySeparatorChar + "<absent>"] = "absent"; continue; }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    map[file] = Convert.ToHexString(SHA256.HashData(stream));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    map[file] = "unreadable:" + ex.GetType().Name;
                }
            }
        }
        return map;
    }

    private static bool SnapshotEquals(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
    {
        if (before.Count != after.Count) return false;
        foreach (var pair in before)
        {
            if (!after.TryGetValue(pair.Key, out var hash) || hash != pair.Value) return false;
        }
        return true;
    }
}
