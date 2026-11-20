using System;

namespace FNSyncPlus.Core.Updates;

/// <summary>Guards a handed-off install session. The watchdog never releases the maintenance
/// lease by itself; it only reports a CONFIRMED safe-to-resume once the evidence proves the
/// updater is gone AND the installation is provably untouched — a fixed timer is not evidence,
/// and an unknown process state is not an exit.</summary>
public interface IUpdaterHandoffWatchdog : IDisposable
{
    /// <summary>Starts supervision. Must be idempotent and safe to call from any thread.</summary>
    void Start();
}

/// <summary>
/// Watches the real updater through three independent evidence channels and reaches one of two
/// outcomes: confirmed-safe-to-resume (lease released by the session) or hold (lease kept,
/// optionally escalated to human diagnosis).
///
/// Evidence channels, all scoped to THIS operation id:
/// <list type="bullet">
/// <item><description>Process identity probe (<see cref="ProcessProbeState"/>): PID + start time
/// (+ path when readable). Unknown is never treated as Exited.</description></item>
/// <item><description>Operation state file (<see cref="InstallOperationStatus"/>): the updater's
/// own durable, atomic record of what it did.</description></item>
/// <item><description>Install intactness probe (<see cref="InstallIntactnessProbe"/>): filesystem
/// proof that the installation was never modified, required before any "exited without a word"
/// verdict may release the lease.</description></item>
/// </list>
///
/// Journal lines are admissible only when tagged with the current operation id; historical
/// lines can never influence the verdict.
/// </summary>
public sealed class ProcessHandoffWatchdog : IUpdaterHandoffWatchdog
{
    private readonly Func<ProcessProbeState> _updaterProbe;
    private readonly Func<InstallOperationStatus> _operationStatus;
    private readonly Func<string> _journalEvidence;
    private readonly Func<bool> _installProvenIntact;
    private readonly Action<string> _onConfirmedAbandon;
    private readonly Action<string> _onNeedsDiagnosis;
    private readonly Action<string>? _log;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _unknownExitGrace;
    private readonly TimeSpan _escalateAfter;
    private readonly System.Threading.CancellationTokenSource _cts = new();
    private int _started;
    private int _finished;

    public ProcessHandoffWatchdog(
        Func<ProcessProbeState> updaterProbe,
        Func<InstallOperationStatus> operationStatus,
        Func<string> journalEvidence,
        Func<bool> installProvenIntact,
        Action<string> onConfirmedAbandon,
        Action<string> onNeedsDiagnosis,
        Action<string>? log = null,
        TimeSpan? initialDelay = null, TimeSpan? pollInterval = null,
        TimeSpan? unknownExitGrace = null, TimeSpan? escalateAfter = null)
    {
        _updaterProbe = updaterProbe ?? throw new ArgumentNullException(nameof(updaterProbe));
        _operationStatus = operationStatus ?? throw new ArgumentNullException(nameof(operationStatus));
        _journalEvidence = journalEvidence ?? throw new ArgumentNullException(nameof(journalEvidence));
        _installProvenIntact = installProvenIntact ?? throw new ArgumentNullException(nameof(installProvenIntact));
        _onConfirmedAbandon = onConfirmedAbandon ?? throw new ArgumentNullException(nameof(onConfirmedAbandon));
        _onNeedsDiagnosis = onNeedsDiagnosis ?? throw new ArgumentNullException(nameof(onNeedsDiagnosis));
        _log = log;
        _initialDelay = initialDelay ?? TimeSpan.FromMinutes(2);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);
        _unknownExitGrace = unknownExitGrace ?? TimeSpan.FromSeconds(15);
        _escalateAfter = escalateAfter ?? TimeSpan.FromMinutes(10);
    }

    public void Start()
    {
        if (System.Threading.Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = System.Threading.Tasks.Task.Run(() => SuperviseAsync(_cts.Token));
    }

    public void Dispose()
    {
        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
        _cts.Dispose();
    }

    private async System.Threading.Tasks.Task SuperviseAsync(System.Threading.CancellationToken token)
    {
        try { await System.Threading.Tasks.Task.Delay(_initialDelay, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        DateTime began = DateTime.UtcNow;
        DateTime? unknownSince = null;
        bool escalated = false;
        while (true)
        {
            ProcessProbeState probe = ProbeSafely();

            if (probe == ProcessProbeState.Running)
            {
                unknownSince = null;
                if (!escalated && DateTime.UtcNow - began >= _escalateAfter)
                {
                    escalated = true;
                    // Never release on a timer: log loudly so the operator knows, then keep holding.
                    _log?.Invoke($"更新器运行已超过 {_escalateAfter.TotalMinutes:F0} 分钟仍未完成交接；继续保持维护暂停，绝不按时间强切");
                }
            }
            else if (probe == ProcessProbeState.Unknown)
            {
                // "Cannot tell" is NOT "gone". Hold, keep supervising, escalate to diagnosis once
                // the state stays unknown for a prolonged period.
                unknownSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - unknownSince.Value >= _escalateAfter)
                {
                    if (System.Threading.Interlocked.Exchange(ref _finished, 1) == 0)
                        Diagnose("更新器进程状态长时间无法确认（查询失败或权限不足）；维护保持暂停，进入人工诊断，绝不按未知=退出释放租约");
                    return;
                }
            }
            else // Exited, confirmed by identity
            {
                unknownSince = null;
                if (DecideAfterConfirmedExit(token, out bool keepSupervising))
                    return;
                if (keepSupervising) { /* evidence landed mid-grace: fall through to the poll delay */ }
            }

            try { await System.Threading.Tasks.Task.Delay(_pollInterval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private ProcessProbeState ProbeSafely()
    {
        try { return _updaterProbe(); }
        catch (Exception ex)
        {
            _log?.Invoke($"更新器存活检查异常（按未知处理，绝不视为退出）：{ex.Message}");
            return ProcessProbeState.Unknown;
        }
    }

    private InstallOperationStatus StatusSafely()
    {
        try { return _operationStatus(); }
        catch (Exception ex)
        {
            _log?.Invoke($"读取更新操作状态异常（按未知处理）：{ex.Message}");
            return InstallOperationStatus.Unknown;
        }
    }

    private string EvidenceSafely()
    {
        try { return _journalEvidence() ?? ""; }
        catch (Exception ex)
        {
            _log?.Invoke($"读取本次操作日志证据异常：{ex.Message}");
            return "";
        }
    }

    private bool IntactSafely()
    {
        try { return _installProvenIntact(); }
        catch (Exception ex)
        {
            _log?.Invoke($"安装完整性证据读取异常（按不可证明处理）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The updater is confirmed gone. Returns true when supervision ends (either outcome),
    /// false when a grace re-check found the process alive again.
    /// </summary>
    private bool DecideAfterConfirmedExit(System.Threading.CancellationToken token, out bool keepSupervising)
    {
        keepSupervising = false;
        switch (EvaluateEvidence())
        {
            case Verdict.Release:
                Confirm(_lastReason);
                return true;
            case Verdict.Hold:
                return true;
            case Verdict.Diagnose:
                if (System.Threading.Interlocked.Exchange(ref _finished, 1) == 0)
                    Diagnose(_lastReason);
                return true;
        }

        // Inconclusive: the state file may lag the process exit. Wait the grace window, re-check.
        DateTime deadline = DateTime.UtcNow + _unknownExitGrace;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                System.Threading.Tasks.Task.Delay(
                    System.TimeSpan.FromMilliseconds(Math.Min(500, _pollInterval.TotalMilliseconds)), token)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { return true; }
            if (ProbeSafely() == ProcessProbeState.Running)
            {
                keepSupervising = true; // came back to life, or the first probe hiccuped
                return false;
            }
        }

        switch (EvaluateEvidence())
        {
            case Verdict.Release:
                Confirm(_lastReason);
                return true;
            case Verdict.Hold:
                return true;
            case Verdict.Diagnose:
                if (System.Threading.Interlocked.Exchange(ref _finished, 1) == 0)
                    Diagnose(_lastReason);
                return true;
        }

        // Still no durable evidence after the grace window. The only release path left is the
        // filesystem proof that the install never began; anything less goes to a human.
        if (IntactSafely())
        {
            Confirm("更新器已退出且无操作记录；文件系统证据（指针未切换、无目标目录、无暂存目录）证明安装从未开始，确认放弃本次安装");
            return true;
        }
        if (System.Threading.Interlocked.Exchange(ref _finished, 1) == 0)
            Diagnose("更新器已退出但本次操作无任何状态记录，且无法证明文件系统未被修改；维护保持暂停，进入人工诊断");
        return true;
    }

    private enum Verdict { Inconclusive, Release, Hold, Diagnose }

    private string _lastReason = "";

    private Verdict EvaluateEvidence()
    {
        switch (StatusSafely())
        {
            case InstallOperationStatus.Installed:
                _lastReason = "更新器已完成安装但本进程仍在运行：旧进程不得恢复备份，保持维护暂停";
                _log?.Invoke(_lastReason);
                return Verdict.Hold;
            case InstallOperationStatus.RolledBack:
                _lastReason = "更新器已回滚到上一版本但本进程仍在运行：保持维护暂停并记录异常";
                _log?.Invoke(_lastReason);
                return Verdict.Hold;
            case InstallOperationStatus.Installing:
                _lastReason = "更新器在安装中途退出（状态=Installing）：可能修改过半成品，保持维护暂停并进入人工诊断";
                return Verdict.Diagnose;
            case InstallOperationStatus.Abandoned:
                _lastReason = "更新器明确放弃且未修改安装（状态=Abandoned），确认放弃本次安装";
                return Verdict.Release;
            case InstallOperationStatus.FailedBeforeInstall:
                _lastReason = "更新器在安装开始前失败（状态=FailedBeforeInstall），安装确未开始，确认放弃本次安装";
                return Verdict.Release;
            case InstallOperationStatus.WaitingForAppExit:
                if (IntactSafely())
                {
                    _lastReason = "更新器在等待主程序退出阶段退出且安装未被修改（状态=WaitingForAppExit + 文件系统证据），确认放弃本次安装";
                    return Verdict.Release;
                }
                _lastReason = "更新器在等待退出阶段消失但无法证明安装未被修改，保持维护暂停并进入人工诊断";
                return Verdict.Diagnose;
        }

        // State file missing/unreadable: fall back to op-tagged journal evidence.
        string evidence = EvidenceSafely();
        if (ShowsCompletedWork(evidence))
        {
            _lastReason = $"本次操作日志显示安装已完成或已回滚，但本进程仍在运行：保持维护暂停（{FirstLine(evidence)}）";
            _log?.Invoke(_lastReason);
            return Verdict.Hold;
        }
        if (ShowsAbandon(evidence))
        {
            _lastReason = FirstMatching(evidence);
            return Verdict.Release;
        }
        return Verdict.Inconclusive;
    }

    private void Confirm(string reason)
    {
        if (System.Threading.Interlocked.Exchange(ref _finished, 1) == 1) return;
        try { _onConfirmedAbandon(reason); }
        catch (Exception ex) { _log?.Invoke($"交接放弃回调异常：{ex.Message}"); }
    }

    private void Diagnose(string reason)
    {
        try { _onNeedsDiagnosis(reason); }
        catch (Exception ex) { _log?.Invoke($"人工诊断回调异常：{ex.Message}"); }
    }

    /// <summary>Op-tagged journal lines meaning the updater acted on the installation.</summary>
    internal static bool ShowsCompletedWork(string evidence) =>
        evidence.Contains("installed ", StringComparison.Ordinal) ||
        (evidence.Contains("launch", StringComparison.Ordinal) && evidence.Contains(" ok", StringComparison.Ordinal)) ||
        evidence.Contains("rolled back to", StringComparison.Ordinal);

    /// <summary>Op-tagged journal lines meaning the updater gave up without touching the installation.</summary>
    internal static bool ShowsAbandon(string evidence) =>
        evidence.Contains("still running after", StringComparison.Ordinal) ||
        evidence.Contains("apply refused", StringComparison.OrdinalIgnoreCase) ||
        evidence.Contains("failed", StringComparison.Ordinal);

    internal static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "(无日志)";
        string line = text.Split('\r', '\n')[0];
        return line.Length > 160 ? line[..160] : line;
    }

    private static string FirstMatching(string evidence)
    {
        foreach (string line in evidence.Split('\r', '\n'))
        {
            if (line.Contains("still running after", StringComparison.Ordinal) ||
                line.Contains("apply refused", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                string s = line.Trim();
                return s.Length > 160 ? s[..160] : s;
            }
        }
        return "更新器已确认放弃本次安装";
    }
}
