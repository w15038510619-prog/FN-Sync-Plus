using System;
using System.Threading;
using System.Threading.Tasks;

namespace FNSyncPlus.Core.Updates;

public sealed record UpdateSchedulerOptions(
    TimeSpan InitialDelay,
    TimeSpan Interval,
    TimeSpan MinBackoff,
    TimeSpan MaxBackoff)
{
    /// <summary>Defaults asked for by the operating requirements: check shortly after startup, then
    /// every six hours, backing off exponentially on failure.</summary>
    public static UpdateSchedulerOptions Default => new(
        InitialDelay: TimeSpan.FromSeconds(90),
        Interval: TimeSpan.FromHours(6),
        MinBackoff: TimeSpan.FromMinutes(1),
        MaxBackoff: TimeSpan.FromHours(1));

    public void Validate()
    {
        if (InitialDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(InitialDelay));
        if (Interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Interval));
        if (MinBackoff <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(MinBackoff));
        if (MaxBackoff < MinBackoff) throw new ArgumentOutOfRangeException(nameof(MaxBackoff));
    }
}

/// <summary>Launches the updater after a gated install session was granted. Separated so the
/// scheduling logic can be exercised without spawning a real process.</summary>
public interface IUpdateInstallLauncher
{
    bool TryStart(UpdateInstallSession session, out string error);
}

internal sealed class ProcessInstallLauncher : IUpdateInstallLauncher
{
    private readonly UpdateService _service;
    public ProcessInstallLauncher(UpdateService service) => _service = service;
    public bool TryStart(UpdateInstallSession session, out string error) =>
        _service.TryStartUpdater(session, null, out error);
}

public sealed record UpdateSchedulerEvent(string Kind, string Message, string Version);

/// <summary>
/// Background update checks. Runs one loop, so two checks can never overlap, and never lets a
/// network or disk problem escape: a failing update feed must not take the program down or delay a
/// backup.
///
/// Modes (from <see cref="UpdateSettings.AutoMode"/>)
///   CheckOnly          → check, notify, never download
///   DownloadAndNotify  → check, download, verify, notify; the user decides when to install
///   UnattendedInstall  → only when explicitly enabled: download, wait for a safe window, install
///
/// A handoff that did not actually replace the version (updater crashed, launch failed, rollback)
/// is remembered in <see cref="UpdateSettings.HandoffVersion"/>, so the next cycle does not loop on
/// the same broken release.
/// </summary>
public sealed class UpdateScheduler : IDisposable
{
    private readonly UpdateService _service;
    private readonly UpdateSchedulerOptions _options;
    private readonly Action<string>? _log;
    private readonly IUpdateInstallLauncher _launcher;
    private readonly IApplicationShutdownCoordinator? _shutdown;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _consecutiveFailures;
    private int _disposed;

    public UpdateScheduler(UpdateService service, UpdateSchedulerOptions? options = null,
        IUpdateInstallLauncher? launcher = null, Action<string>? log = null,
        IApplicationShutdownCoordinator? shutdown = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _options = options ?? UpdateSchedulerOptions.Default;
        _options.Validate();
        _launcher = launcher ?? new ProcessInstallLauncher(service);
        _log = log;
        _shutdown = shutdown;
    }

    /// <summary>Raised for UI refresh. Always invoked from a background thread.</summary>
    public event Action<UpdateSchedulerEvent>? Changed;

    /// <summary>Number of completed check cycles; used by tests and diagnostics.</summary>
    public int CompletedChecks { get; private set; }

    public int FailedChecks { get; private set; }

    public string Status { get; private set; } = "尚未检查";

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>Starts the loop. Non-blocking: the first check is delayed, so NAS connection and the
    /// first backup are never held up by an update check.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _disposed, 0) == 1)
            throw new ObjectDisposedException(nameof(UpdateScheduler));
        if (_loop is { IsCompleted: false }) return;
        if (!_service.CanCheck)
        {
            Status = _service.SupportMessage;
            Report("disabled", Status, "");
            return;
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        var cts = _cts;
        if (cts == null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        var loop = _loop;
        if (loop != null)
        {
            try { await Task.WhenAny(loop, Task.Delay(timeout)).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cts.Dispose();
        _cts = null;
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        TimeSpan delay = _options.InitialDelay;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            var (succeeded, nextDelay) = await RunCycleSafelyAsync(token).ConfigureAwait(false);
            delay = succeeded ? _options.Interval : nextDelay;
        }
    }

    private async Task<(bool Succeeded, TimeSpan NextDelay)> RunCycleSafelyAsync(CancellationToken token)
    {
        try
        {
            return await RunCycleAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return (true, _options.Interval);
        }
        catch (Exception ex)
        {
            // A failed check must never crash the program or stop the loop permanently.
            _consecutiveFailures++;
            var backoff = Backoff();
            Status = $"更新检查异常：{ex.Message}";
            _log?.Invoke($"更新检查异常（已退避 {backoff.TotalSeconds:F0}s）：{ex}");
            Report("error", Status, "");
            return (false, backoff);
        }
    }

    /// <summary>Runs exactly one cycle. Public so the UI (and tests) can force a check.</summary>
    public async Task<UpdateSchedulerEvent> RunOnceAsync(CancellationToken token = default)
    {
        var (_, evt) = await RunCycleWithEventAsync(token).ConfigureAwait(false);
        return evt;
    }

    private async Task<(bool Succeeded, TimeSpan NextDelay)> RunCycleAsync(CancellationToken token)
    {
        var (succeeded, _) = await RunCycleWithEventAsync(token).ConfigureAwait(false);
        return (succeeded, succeeded ? _options.Interval : Backoff());
    }

    private async Task<(bool Succeeded, UpdateSchedulerEvent Event)> RunCycleWithEventAsync(CancellationToken token)
    {
        // Overlap guard: a second timer tick must never start a second check.
        if (!await _gate.WaitAsync(0, token).ConfigureAwait(false))
            return (true, new UpdateSchedulerEvent("skipped", "上一次更新检查仍在进行", ""));
        try
        {
            var settings = _service.Settings;
            if (!_service.CanCheck || string.IsNullOrWhiteSpace(settings.ManifestUrl))
            {
                Status = _service.CanCheck ? "未配置更新地址，跳过自动检查" : _service.SupportMessage;
                return (true, new UpdateSchedulerEvent("skipped", Status, ""));
            }

            var check = await _service.CheckAsync(token).ConfigureAwait(false);
            CompletedChecks++;
            if (!check.HasUpdate)
            {
                if (check.Error.Length > 0)
                {
                    // A configured-but-unreachable feed is not an exception: back off and stay quiet.
                    _consecutiveFailures++;
                    FailedChecks++;
                    Status = check.Error;
                    Report("error", Status, "");
                    return (false, new UpdateSchedulerEvent("error", check.Error, ""));
                }
                _consecutiveFailures = 0;
                Status = "已是最新版本";
                Report("uptodate", Status, "");
                return (true, new UpdateSchedulerEvent("uptodate", Status, ""));
            }

            _consecutiveFailures = 0;
            var release = check.Release!;
            int mode = settings.AutoMode;
            _log?.Invoke($"发现新版本 {check.Version}（模式 {ModeName(mode)}，通道 {_service.CurrentChannelName}）");

            if (mode == (int)AutoUpdateMode.CheckOnly)
            {
                settings.NotifiedVersion = check.Version;
                _service.Save();
                Status = $"发现新版本 {check.Version}，已提示用户（仅检查模式，不下载）";
                Report("available", Status, check.Version);
                return (true, new UpdateSchedulerEvent("available", Status, check.Version));
            }

            // Download and notify, or unattended install: both need a verified local package.
            var staged = await EnsureDownloadedAsync(release, token).ConfigureAwait(false);
            if (!staged.Ok || staged.Release == null)
            {
                FailedChecks++;
                Status = staged.Error;
                Report("error", Status, check.Version);
                return (false, new UpdateSchedulerEvent("error", staged.Error, check.Version));
            }

            if (mode != (int)AutoUpdateMode.UnattendedInstall)
            {
                settings.NotifiedVersion = check.Version;
                _service.Save();
                Status = $"新版本 {check.Version} 已下载并校验，等待用户确认安装";
                Report("ready", Status, check.Version);
                return (true, new UpdateSchedulerEvent("ready", Status, check.Version));
            }

            // Unattended install, but only when the operator enabled it explicitly.
            if (!settings.UnattendedEnabled || _service.CanInstall == false)
            {
                Status = $"新版本 {check.Version} 已下载；无人值守安装未启用，等待用户确认";
                Report("ready", Status, check.Version);
                return (true, new UpdateSchedulerEvent("ready", Status, check.Version));
            }

            // Never re-attempt a handoff that already failed once for this version.
            if (string.Equals(settings.HandoffVersion, check.Version, StringComparison.OrdinalIgnoreCase))
            {
                Status = $"版本 {check.Version} 上次安装未能完成，已改为提示用户手动安装";
                Report("ready", Status, check.Version);
                return (true, new UpdateSchedulerEvent("ready", Status, check.Version));
            }

            // Unattended installs must be able to end this process. Without a shutdown
            // coordinator the updater would only ever wait for our pid until it times out, so
            // refuse before taking the maintenance hold rather than guaranteeing that failure.
            if (_shutdown == null)
            {
                Status = "无人值守安装缺少应用退出协调器，已跳过（未启动更新器，未暂停备份）";
                _log?.Invoke(Status);
                Report("error", Status, check.Version);
                return (false, new UpdateSchedulerEvent("error", Status, check.Version));
            }

            var session = await _service.BeginInstallAsync(staged.Release, _options.MaxBackoff, token).ConfigureAwait(false);
            using (session)
            {
                if (!session.Plan.Ready)
                {
                    // Not an error: a running backup simply means "try again next cycle".
                    Status = $"新版本 {check.Version} 已就绪，等待备份安全结束：{session.Readiness.Reason}";
                    Report("deferred", Status, check.Version);
                    return (true, new UpdateSchedulerEvent("deferred", Status, check.Version));
                }

                settings.HandoffVersion = check.Version;
                _service.Save();
                if (!_launcher.TryStart(session, out string error))
                {
                    FailedChecks++;
                    Status = error;
                    Report("error", Status, check.Version);
                    return (false, new UpdateSchedulerEvent("error", error, check.Version));
                }

                // The session keeps the maintenance lease on purpose: the updater is now waiting
                // for this process to exit, and a new backup starting during the pointer swap is
                // exactly what the hold exists to prevent. Its watchdog supervises the release.
                // The one thing the handoff still needs is for us to actually leave: ask the
                // application lifecycle to exit through the coordinator, the same path a manual
                // install uses.
                if (!_shutdown.RequestShutdown($"正在安装 {check.Version}，程序将自动退出并在更新完成后重启"))
                {
                    // The request could not be delivered. The updater will wait for wait-seconds
                    // and then give up; the handoff watchdog confirms that and resumes backups.
                    // Report honestly instead of pretending an install is in progress.
                    FailedChecks++;
                    Status = "已启动更新器，但无法向主程序请求退出；更新器将放弃本次安装并恢复备份";
                    Report("error", Status, check.Version);
                    return (false, new UpdateSchedulerEvent("error", Status, check.Version));
                }
                session.MarkShutdownRequested();

                Status = $"正在安装 {check.Version}，程序将自动重启";
                Report("installing", Status, check.Version);
                return (true, new UpdateSchedulerEvent("installing", Status, check.Version));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(bool Ok, VerifiedRelease? Release, string Error)> EnsureDownloadedAsync(VerifiedRelease release,
        CancellationToken token)
    {
        if (_service.TryFindVerifiedPackage(release, out string existing))
            return (true, release with { LocalPackagePath = existing }, "");

        var download = await _service.DownloadAsync(release, null, token).ConfigureAwait(false);
        if (!download.Result.Ok) return (false, null, download.Result.Error);
        return (true, download.Release, "");
    }

    private TimeSpan Backoff()
    {
        double seconds = _options.MinBackoff.TotalSeconds * Math.Pow(2, Math.Min(_consecutiveFailures, 10));
        return TimeSpan.FromSeconds(Math.Min(seconds, _options.MaxBackoff.TotalSeconds));
    }

    private static string ModeName(int mode) => mode switch
    {
        (int)AutoUpdateMode.CheckOnly => "仅检查",
        (int)AutoUpdateMode.DownloadAndNotify => "自动下载并提醒",
        (int)AutoUpdateMode.UnattendedInstall => "无人值守安装",
        _ => $"未知({mode})"
    };

    private void Report(string kind, string message, string version)
    {
        Status = message;
        try { Changed?.Invoke(new UpdateSchedulerEvent(kind, message, version)); }
        catch (Exception ex) { _log?.Invoke($"更新状态通知处理异常：{ex.Message}"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            _cts?.Dispose();
            _cts = null;
        }
        _gate.Dispose();
    }
}
