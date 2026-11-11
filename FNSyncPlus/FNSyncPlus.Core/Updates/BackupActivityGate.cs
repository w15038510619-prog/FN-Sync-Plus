namespace FNSyncPlus.Core.Updates;

public enum InstallDecision { Proceed, Defer, Refuse }

public sealed record InstallReadiness(InstallDecision Decision, string Reason);

/// <summary>
/// Decides whether an update may stop the application right now. It never kills a running
/// backup: new work is blocked first, then the gate waits for the current task to finish, and
/// any unclear commit/lock state defers the installation instead of guessing.
/// </summary>
public sealed class BackupActivityGate
{
    private readonly Func<bool> _hasRunningOrQueuedWork;
    private readonly Func<bool> _hasUnclearCommitOrLock;
    private readonly Func<bool> _blockNewWork;
    private readonly Func<bool> _newWorkBlocked;
    private readonly Func<Task> _saveConfiguration;

    public BackupActivityGate(Func<bool> hasRunningOrQueuedWork, Func<bool> hasUnclearCommitOrLock,
        Func<bool> blockNewWork, Func<bool> newWorkBlocked, Func<Task> saveConfiguration)
    {
        _hasRunningOrQueuedWork = hasRunningOrQueuedWork ?? throw new ArgumentNullException(nameof(hasRunningOrQueuedWork));
        _hasUnclearCommitOrLock = hasUnclearCommitOrLock ?? throw new ArgumentNullException(nameof(hasUnclearCommitOrLock));
        _blockNewWork = blockNewWork ?? throw new ArgumentNullException(nameof(blockNewWork));
        _newWorkBlocked = newWorkBlocked ?? throw new ArgumentNullException(nameof(newWorkBlocked));
        _saveConfiguration = saveConfiguration ?? throw new ArgumentNullException(nameof(saveConfiguration));
    }

    /// <summary>Non-blocking probe used for UI state.</summary>
    public InstallReadiness Inspect()
    {
        if (_hasUnclearCommitOrLock())
            return new InstallReadiness(InstallDecision.Refuse, "存在结果不明确的提交或未释放的锁，推迟更新并保留现场");
        if (_hasRunningOrQueuedWork())
            return new InstallReadiness(InstallDecision.Defer, "备份或恢复正在进行，等待安全结束");
        return new InstallReadiness(InstallDecision.Proceed, "当前没有进行中的任务");
    }

    /// <summary>
    /// Blocks new work, waits until running tasks finish, re-checks unclear state, then saves
    /// configuration. Returns Refuse when the wait exceeded the budget — the caller must not install.
    /// </summary>
    public async Task<InstallReadiness> WaitForSafeInstallAsync(TimeSpan budget, CancellationToken token = default)
    {
        _blockNewWork();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_hasRunningOrQueuedWork())
        {
            if (clock.Elapsed > budget)
                return new InstallReadiness(InstallDecision.Defer, "等待任务安全结束超时，已推迟安装");
            await Task.Delay(500, token).ConfigureAwait(false);
        }
        if (!_newWorkBlocked())
            return new InstallReadiness(InstallDecision.Refuse, "无法阻止新的备份任务，拒绝安装");
        if (_hasUnclearCommitOrLock())
            return new InstallReadiness(InstallDecision.Refuse, "任务结束后发现不明确的提交或锁，拒绝安装");
        await _saveConfiguration().ConfigureAwait(false);
        return new InstallReadiness(InstallDecision.Proceed, "任务已安全结束，配置已保存");
    }
}
