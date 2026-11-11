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
///   --apply              install a downloaded package after the running app has exited, then launch
///   --rollback           switch the pointer back to the previous version and launch it
///   --status             print the current/previous version as JSON and exit
///
/// Exit codes: 0 launched (possibly after rollback), 2 usage error, 3 no usable version,
/// 4 the running app did not stop in time, 5 install failed.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitNoVersion = 3;
    private const int ExitAppStillRunning = 4;
    private const int ExitInstallFailed = 5;

    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        string root = options.LayoutRoot ?? AppContext.BaseDirectory;
        var layout = new VersionedInstallLayout(root);
        var health = new LaunchHealthTracker(root);

        try
        {
            return options.Mode switch
            {
                "status" => PrintStatus(layout),
                "apply" => Apply(options, layout, health),
                "rollback" => Rollback(layout, health),
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
        if (string.IsNullOrWhiteSpace(options.Package) || string.IsNullOrWhiteSpace(options.Version) ||
            string.IsNullOrWhiteSpace(options.Sha256))
            return Usage("--apply 需要 --package、--version 和 --sha256");

        // 1. Wait for the app that requested the update to stop on its own. Never kill it: an
        //    in-flight backup commit must be allowed to finish or fail safely.
        if (options.WaitProcessId is int pid && !WaitForExit(pid, options.WaitSeconds))
        {
            layout.Journal($"apply {options.Version}: app pid {pid} still running after {options.WaitSeconds}s, aborting");
            return ExitAppStillRunning;
        }

        var installer = new UpdateInstaller(layout,
            token => VerifyLaunch(layout, health, options.Version!, options.HealthTimeoutSeconds, token));
        var outcome = installer.ApplyAsync(options.Package!, options.Version!, options.Sha256!,
            options.WorkDirectory ?? Path.Combine(layout.Root, ".update-work"), CancellationToken.None)
            .GetAwaiter().GetResult();

        if (!outcome.Ok)
        {
            layout.Journal($"apply {options.Version} failed: {outcome.Error}");
            Console.WriteLine($"APPLY-FAILED {outcome.Error}");
            // Put the user back into a working program instead of leaving them with nothing.
            if (options.NoLaunch) return ExitInstallFailed;
            int relaunch = LaunchVersion(layout, health, layout.ActiveVersion!, allowRollback: false);
            return relaunch == ExitOk ? ExitInstallFailed : relaunch;
        }

        Console.WriteLine($"APPLIED {outcome.Version}");
        if (options.NoLaunch) return ExitOk;
        return LaunchActive(layout, health, allowRollback: true);
    }

    private static int Rollback(VersionedInstallLayout layout, LaunchHealthTracker health)
    {
        if (!layout.RestorePointer())
        {
            layout.Journal("rollback requested but no previous version exists");
            return ExitNoVersion;
        }
        layout.Journal("manual rollback completed");
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
            if (layout.RestorePointer())
                return LaunchVersion(layout, health, previous, allowRollback: false);
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
        Console.WriteLine("用法: FNSyncPlus.Updater [--layout <安装根目录>] [launch | status | rollback | apply --package <zip> --version <v> --sha256 <hash> [--wait-pid <pid>] [--no-launch]]");
        return ExitUsage;
    }

    private sealed class Options
    {
        public string Mode { get; private set; } = "launch";
        public string? LayoutRoot { get; private set; }
        public string? Package { get; private set; }
        public string? Version { get; private set; }
        public string? Sha256 { get; private set; }
        public string? WorkDirectory { get; private set; }
        public int? WaitProcessId { get; private set; }
        public int WaitSeconds { get; private set; } = 120;
        public int HealthTimeoutSeconds { get; private set; } = 25;
        public bool NoLaunch { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--layout": options.LayoutRoot = Next(args, ref i); break;
                    case "--package": options.Package = Next(args, ref i); break;
                    case "--version": options.Version = Next(args, ref i); break;
                    case "--sha256": options.Sha256 = Next(args, ref i); break;
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
