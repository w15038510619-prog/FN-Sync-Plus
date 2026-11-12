using System.Diagnostics;
using System.Text.Json;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.Updater;

/// <summary>
/// Stable, version-independent launcher. It lives in the install root next to <c>current.json</c>
/// and is never replaced by an update, so the update protocol keeps working even when a brand new
/// payload is broken.
///
/// Modes
///   (default)            launch the version pointed at by current.json and verify it becomes usable
///   --apply --release    install a signed release after the running app has exited, then launch
///   --rollback           switch the pointer back to the previous version and launch it
///   --status             print the current/previous version as JSON and exit
///
/// The updater is a separate process and therefore cannot rely on the UI having validated
/// anything. It verifies the signed manifest itself, derives the version/hash/size from that
/// signature, and refuses anything it cannot bind to the pinned public key. A command-line
/// <c>--sha256</c> is deliberately NOT accepted: a hash that the caller supplies proves nothing.
///
/// Exit codes: 0 launched (possibly after rollback), 2 usage error, 3 no usable version,
/// 4 the running app did not stop in time, 5 install failed, 6 release verification failed.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitNoVersion = 3;
    private const int ExitAppStillRunning = 4;
    private const int ExitInstallFailed = 5;
    private const int ExitReleaseRejected = 6;

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        string root = options.LayoutRoot ?? AppContext.BaseDirectory;
        var layout = new VersionedInstallLayout(root);
        var health = new LaunchHealthTracker(root);

        if (options.RejectedArguments)
        {
            layout.Journal("refused: --sha256/--version are not accepted, only a signed --release descriptor");
            Console.WriteLine("REFUSED 不再接受命令行 --sha256 / --version：它们不是可信来源。请传入 --release <release.json>。");
            return ExitUsage;
        }

        try
        {
            return options.Mode switch
            {
                "status" => PrintStatus(layout),
                "apply" => Apply(options, layout, health),
                "rollback" => Rollback(options, layout, health),
                "launch" => LaunchActive(layout, health, allowRollback: true),
                _ => Usage($"未知的操作：{options.Mode}")
            };
        }
        catch (Exception ex)
        {
            layout.Journal($"updater crashed: {ex.GetType().Name}: {ex.Message}");
            return ExitNoVersion;
        }
    }

    private static int PrintStatus(VersionedInstallLayout layout)
    {
        var payload = new
        {
            root = layout.Root,
            current = layout.ActiveVersion,
            previous = layout.PreviousVersion,
            journal = layout.JournalPath
        };
        Console.WriteLine(JsonSerializer.Serialize(payload));
        return layout.ActiveVersion == null ? ExitNoVersion : ExitOk;
    }

    private static int Apply(Options options, VersionedInstallLayout layout, LaunchHealthTracker health)
    {
        if (string.IsNullOrWhiteSpace(options.ReleasePath))
        {
            layout.Journal("apply refused: no --release descriptor");
            Console.WriteLine("REFUSED 独立更新器只接受签名发布描述（--release），不接受命令行哈希。");
            return ExitUsage;
        }

        // 1. Wait for the app that requested the update to stop on its own. Never kill it: an
        //    in-flight backup commit must be allowed to finish or fail safely.
        if (options.WaitProcessId is int pid && !WaitForExit(pid, options.WaitSeconds))
        {
            layout.Journal($"apply: app pid {pid} still running after {options.WaitSeconds}s, aborting");
            return ExitAppStillRunning;
        }

        var descriptor = ReleaseDescriptor.TryLoad(options.ReleasePath);
        if (descriptor == null)
        {
            layout.Journal("apply refused: release descriptor missing or malformed");
            Console.WriteLine("REFUSED 发布描述缺失或格式无效。");
            return ExitReleaseRejected;
        }

        var verifier = PinnedUpdateKey.TryCreateVerifier(out string keyError);
        if (verifier == null)
        {
            layout.Journal($"apply refused: no pinned update key ({keyError})");
            Console.WriteLine($"REFUSED {keyError}");
            return ExitReleaseRejected;
        }

        // 2. Re-verify the signed manifest. Everything below is derived from it, never from the
        //    caller's claims. ReleaseHandoff also refuses when the installed version is unknown,
        //    because "cannot prove it is an upgrade" must not silently allow a downgrade.
        var handoff = ReleaseHandoff.Verify(descriptor, verifier, CurrentVersion(layout), layout.Root, DateTimeOffset.UtcNow);
        if (!handoff.Ok)
        {
            layout.Journal($"apply refused: {handoff.Error}");
            Console.WriteLine($"REFUSED {handoff.Error}");
            return ExitReleaseRejected;
        }
        var release = handoff.Release!;
        layout.Journal($"apply {release.Version} from verified release (sha {release.ShortSha})");
        var installer = new UpdateInstaller(layout,
            token => VerifyLaunch(layout, health, release.Version, options.HealthTimeoutSeconds, token));
        var outcome = installer.ApplyAsync(release, options.WorkDirectory ?? Path.Combine(layout.Root, ".update-work"),
            CancellationToken.None).GetAwaiter().GetResult();

        if (!outcome.Ok)
        {
            layout.Journal($"apply {release.Version} failed: {outcome.Error}");
            Console.WriteLine($"APPLY-FAILED {outcome.Error}");
            // Put the user back into a working program instead of leaving them with nothing.
            if (options.NoLaunch) return ExitInstallFailed;
            string? active = layout.ActiveVersion;
            if (active == null) return ExitNoVersion;
            int relaunch = LaunchVersion(layout, health, active, allowRollback: false);
            return relaunch == ExitOk ? ExitInstallFailed : relaunch;
        }

        Console.WriteLine($"APPLIED {outcome.Version}");
        if (options.NoLaunch) return ExitOk;
        return LaunchActive(layout, health, allowRollback: true);
    }

    /// <summary>Version currently installed, used as the "must be newer" baseline. Unknown means the
    /// dowstream rule cannot be evaluated, which is treated as a refusal.</summary>
    private static Version? CurrentVersion(VersionedInstallLayout layout) =>
        Version.TryParse(layout.ActiveVersion, out var version) ? version : null;

    private static int Rollback(Options options, VersionedInstallLayout layout, LaunchHealthTracker health)
    {
        if (!layout.TryRestorePointer(out string reason))
        {
            layout.Journal($"rollback refused: {reason}");
            Console.WriteLine($"ROLLBACK-REFUSED {reason}");
            return ExitNoVersion;
        }
        layout.Journal("manual rollback completed");
        if (options.NoLaunch) return ExitOk;
        return LaunchActive(layout, health, allowRollback: false);
    }

    private static int LaunchActive(VersionedInstallLayout layout, LaunchHealthTracker health, bool allowRollback)
    {
        string? active = layout.ActiveVersion;
        if (active == null)
        {
            layout.Journal("launch failed: current.json is missing or points at a missing directory");
            return ExitNoVersion;
        }
        int result = LaunchVersion(layout, health, active, allowRollback);
        if (result == ExitOk) return ExitOk;

        // The new version did not come up. Fall back once, and only once.
        if (allowRollback && layout.PreviousVersion is { } previous)
        {
            layout.Journal($"launch of {active} failed, rolling back to {previous}");
            if (layout.TryRestorePointer(out string reason))
                return LaunchVersion(layout, health, previous, allowRollback: false);
            layout.Journal($"automatic rollback refused: {reason}");
            Console.WriteLine($"ROLLBACK-REFUSED {reason}");
        }
        return result;
    }

    private static int LaunchVersion(VersionedInstallLayout layout, LaunchHealthTracker health, string version, bool allowRollback)
    {
        string directory = layout.VersionDirectory(version);
        string exe = Path.Combine(directory, "FNSyncPlus.WinUI.exe");
        if (!File.Exists(exe))
        {
            layout.Journal($"launch {version}: missing {exe}");
            return ExitNoVersion;
        }
        health.Clear(version);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = directory,
            UseShellExecute = false
        });
        if (process == null)
        {
            layout.Journal($"launch {version}: Process.Start returned null");
            return ExitNoVersion;
        }
        health.RecordAttempt(version, process.Id);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, 25));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (health.IsHealthy(version))
            {
                layout.Journal($"launch {version} ok (pid {process.Id})");
                return ExitOk;
            }
            if (process.HasExited) break;
            Thread.Sleep(250);
        }
        var verdict = health.Evaluate(version, !process.HasExited);
        layout.Journal($"launch {version} not healthy: {verdict.Reason}");
        Console.WriteLine($"LAUNCH-UNHEALTHY {version}: {verdict.Reason}");
        return ExitNoVersion;
    }

    /// <summary>Callback for <see cref="VersionedInstallLayout.InstallAsync"/>: the new payload must
    /// actually start and report itself usable before the pointer switch is considered good.</summary>
    private static Task<bool> VerifyLaunch(VersionedInstallLayout layout, LaunchHealthTracker health, string version,
        int timeoutSeconds, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromResult(false);
        string directory = layout.VersionDirectory(version);
        string exe = Path.Combine(directory, "FNSyncPlus.WinUI.exe");
        if (!File.Exists(exe))
        {
            layout.Journal($"verify {version}: missing payload exe");
            return Task.FromResult(false);
        }
        health.Clear(version);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = exe, WorkingDirectory = directory, UseShellExecute = false,
            Arguments = "--update-health-check"
        });
        if (process == null) return Task.FromResult(false);
        health.RecordAttempt(version, process.Id);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, timeoutSeconds));
        while (DateTimeOffset.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (health.IsHealthy(version)) return Task.FromResult(true);
            if (process.HasExited) break;
            Thread.Sleep(250);
        }
        return Task.FromResult(health.Evaluate(version, !process.HasExited).Healthy);
    }

    private static bool WaitForExit(int pid, int seconds)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.WaitForExit(Math.Max(1, seconds) * 1000) && process.HasExited;
        }
        catch (ArgumentException) { return true; } // already gone
        catch (InvalidOperationException) { return true; }
    }

    private static int Usage(string message)
    {
        Console.WriteLine(message);
        Console.WriteLine("用法: FNSyncPlus.Updater [--layout <安装根目录>] [launch | status | rollback | " +
                          "apply --release <release.json> [--wait-pid <pid>] [--wait-seconds <s>] [--no-launch]]");
        return ExitUsage;
    }

    private sealed class Options
    {
        public string Mode { get; private set; } = "launch";
        public string? LayoutRoot { get; private set; }
        public string? ReleasePath { get; private set; }
        public string? WorkDirectory { get; private set; }
        public int? WaitProcessId { get; private set; }
        public int WaitSeconds { get; private set; } = 120;
        public int HealthTimeoutSeconds { get; private set; } = 25;
        public bool NoLaunch { get; private set; }
        public bool RejectedArguments { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--layout": options.LayoutRoot = Next(args, ref i); break;
                    case "--release": options.ReleasePath = Next(args, ref i); break;
                    case "--work-dir": options.WorkDirectory = Next(args, ref i); break;
                    case "--wait-pid":
                        if (int.TryParse(Next(args, ref i), out int pid)) options.WaitProcessId = pid;
                        break;
                    case "--wait-seconds":
                        if (int.TryParse(Next(args, ref i), out int wait)) options.WaitSeconds = wait;
                        break;
                    case "--health-timeout":
                        if (int.TryParse(Next(args, ref i), out int health)) options.HealthTimeoutSeconds = health;
                        break;
                    case "--no-launch": options.NoLaunch = true; break;
                    // A caller-supplied hash or version can never be trusted; keep parsing them only
                    // so a stale shortcut is rejected loudly instead of silently ignored.
                    case "--sha256": case "--version": options.RejectedArguments = true; Next(args, ref i); break;
                    case "status": case "--status": options.Mode = "status"; break;
                    case "apply": case "--apply": options.Mode = "apply"; break;
                    case "rollback": case "--rollback": options.Mode = "rollback"; break;
                    case "launch": case "--launch": options.Mode = "launch"; break;
                }
            }
            return options;
        }

        private static string? Next(string[] args, ref int index) => index + 1 < args.Length ? args[++index] : null;
    }
}
