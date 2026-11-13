using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.Core.Updates;

public enum InstallDecision { Proceed, Defer, Refuse }

public sealed record InstallReadiness(InstallDecision Decision, string Reason);

/// <summary>Result of trying to take the maintenance hold. <see cref="Lease"/> is non-null only when
/// <see cref="Granted"/> is true; callers must hand it to an <see cref="UpdateInstallSession"/>.</summary>
public sealed record GateAttempt(InstallReadiness Readiness, MaintenanceLease? Lease)
{
    public bool Granted => Readiness.Decision == InstallDecision.Proceed && Lease != null;
}

/// <summary>
/// Decides whether an update may stop the application right now. It never kills a running
/// backup: new work is blocked first, then the gate waits for the current task to finish, and
/// any unclear commit/lock state defers the installation instead of guessing.
///
/// Every path that does not end in a real handoff releases the maintenance lease. A leaked hold
/// would silently disable automatic backups while the program keeps running, so the release is
/// explicit on each failure branch and idempotent at the lease level.
/// </summary>
public sealed class BackupActivityGate
{
    private readonly Func<bool> _hasRunningWork;
    private readonly Func<bool> _hasRunningOrQueuedWork;
    private readonly Func<bool> _hasUnclearCommitOrLock;
    private readonly Func<MaintenanceLease?> _enterHold;
    private readonly Func<Task> _saveConfiguration;
    private readonly Action<string>? _log;

    public BackupActivityGate(
        Func<bool> hasRunningWork,
        Func<bool> hasRunningOrQueuedWork,
        Func<bool> hasUnclearCommitOrLock,
        Func<MaintenanceLease?> enterHold,
        Func<Task> saveConfiguration,
        Action<string>? log = null)
    {
        _hasRunningWork = hasRunningWork ?? throw new ArgumentNullException(nameof(hasRunningWork));
        _hasRunningOrQueuedWork = hasRunningOrQueuedWork ?? throw new ArgumentNullException(nameof(hasRunningOrQueuedWork));
        _hasUnclearCommitOrLock = hasUnclearCommitOrLock ?? throw new ArgumentNullException(nameof(hasUnclearCommitOrLock));
        _enterHold = enterHold ?? throw new ArgumentNullException(nameof(enterHold));
        _saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
        _log = log;
    }

    /// <summary>Non-blocking probe used for UI state. Never takes the hold.</summary>
    public InstallReadiness Inspect()
    {
        if (_hasUnclearCommitOrLock())
            return new InstallReadiness(InstallDecision.Refuse, "存在结果不明确的提交或未释放的锁，推迟更新并保留现场");
        if (_hasRunningOrQueuedWork())
            return new InstallReadiness(InstallDecision.Defer, "备份或恢复正在进行，等待安全结束");
        return new InstallReadiness(InstallDecision.Proceed, "当前没有进行中的任务");
    }

    /// <summary>
    /// Takes the hold, waits for running tasks to finish, re-checks unclear state, then saves
    /// configuration. On any failure the hold is released before returning, so the caller never
    /// has to guess whether backups are still paused.
    /// </summary>
    public async Task<GateAttempt> TryAcquireAsync(TimeSpan budget, CancellationToken token = default)
    {
        // Cheap pre-check: do not pause backups only to discover we must refuse immediately.
        if (_hasUnclearCommitOrLock())
            return Refuse("存在结果不明确的提交或未释放的锁，推迟更新并保留现场");
        if (token.IsCancellationRequested)
            return Defer("已取消更新，未暂停备份");

        MaintenanceLease? lease = null;
        try
        {
            lease = _enterHold();
            if (lease == null)
                return Refuse("另一个更新流程已持有维护状态，本次不重复接管");

            var clock = Stopwatch.StartNew();
            while (_hasRunningWork())
            {
                if (token.IsCancellationRequested)
                    return Defer("用户取消更新，已恢复启动备份任务", lease);
                if (clock.Elapsed > budget)
                    return Defer("等待任务安全结束超时，已推迟安装并恢复备份", lease);
                await Task.Delay(250, token).ConfigureAwait(false);
            }

            if (_hasUnclearCommitOrLock())
                return Refuse("任务结束后发现不明确的提交或锁，拒绝安装并恢复备份", lease);

            try
            {
                await _saveConfiguration().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Refuse($"保存配置失败，已取消安装并恢复备份：{ex.Message}", lease);
            }

            // Success: the caller now owns the lease. It must either hand off to the updater or
            // release the lease through its install session.
            _log?.Invoke("已获得更新维护租约，备份队列已暂停，等待交接");
            return new GateAttempt(new InstallReadiness(InstallDecision.Proceed, "任务已安全结束，配置已保存"), lease);
        }
        catch (OperationCanceledException)
        {
            return Defer("用户取消更新，已恢复启动备份任务", lease);
        }
        catch (Exception ex)
        {
            return Refuse($"更新准备过程异常，已恢复启动备份任务：{ex.Message}", lease);
        }
    }

    private GateAttempt Refuse(string reason, MaintenanceLease? lease = null)
    {
        ReleaseQuietly(lease);
        _log?.Invoke($"更新被拒绝：{reason}");
        return new GateAttempt(new InstallReadiness(InstallDecision.Refuse, reason), null);
    }

    private GateAttempt Defer(string reason, MaintenanceLease? lease = null)
    {
        ReleaseQuietly(lease);
        _log?.Invoke($"更新已推迟：{reason}");
        return new GateAttempt(new InstallReadiness(InstallDecision.Defer, reason), null);
    }

    private static void ReleaseQuietly(MaintenanceLease? lease)
    {
        try { lease?.Release(); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
    }
}
