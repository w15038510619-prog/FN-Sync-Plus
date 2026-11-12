using System;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.Core.Updates;

/// <summary>
/// The lifecycle of one update attempt, from "may I pause backups" to "the updater owns the
/// installation" or "we are back to normal".
///
/// States
///   Preparing → Holding → HandedOff   (normal path: the app now exits and the updater takes over)
///   Preparing → Aborted               (any failure: the lease is released, backups resume)
///
/// The single rule that makes this safe: the maintenance lease is released in exactly one of two
/// places — <see cref="Abort"/> when installation will not happen, or the abandon watchdog when a
/// handoff never actually ends the process. It is never released "just in case", because doing so
/// while the updater waits for the app to exit would let a new backup start mid-swap.
/// </summary>
public sealed class UpdateInstallSession : IDisposable
{
    private const int Open = 0;
    private const int HandedOff = 1;
    private const int Aborted = 2;

    private readonly MaintenanceLease? _lease;
    private readonly Action<string>? _log;
    private int _state;

    internal UpdateInstallSession(InstallReadiness readiness, UpdateInstallPlan plan, VerifiedRelease? release,
        MaintenanceLease? lease, Action<string>? log)
    {
        Readiness = readiness;
        Plan = plan;
        Release = release;
        _lease = lease;
        _log = log;
        if (lease == null && !plan.Ready) _state = Aborted;
    }

    public InstallReadiness Readiness { get; }
    public UpdateInstallPlan Plan { get; }
    public VerifiedRelease? Release { get; }

    public bool IsHandedOff => Volatile.Read(ref _state) == HandedOff;
    public bool IsAborted => Volatile.Read(ref _state) == Aborted;
    public bool IsOpen => Volatile.Read(ref _state) == Open;

    /// <summary>True when backups are no longer being held back by this session.</summary>
    public bool HoldReleased => _lease == null || _lease.IsReleased;

    /// <summary>True while this session is the owner of the queue hold.</summary>
    public bool HoldsQueue => _lease is { IsReleased: false };

    /// <summary>
    /// Marks the handoff to the updater. The lease stays held: the app is about to exit and a new
    /// backup starting during the pointer swap is exactly what the hold exists to prevent.
    /// </summary>
    /// <param name="abandonAfter">
    /// Safety net: if the process is still alive after this delay the handoff clearly did not
    /// happen (updater crashed, exit blocked), so the lease is released instead of leaving backups
    /// paused forever.
    /// </param>
    public void CommitHandOff(TimeSpan? abandonAfter = null)
    {
        if (Interlocked.CompareExchange(ref _state, HandedOff, Open) != Open) return;
        _log?.Invoke("已交接给更新器，保持维护状态等待主程序退出");
        if (abandonAfter is { } delay && _lease != null)
        {
            _ = Task.Run(async () =>
            {
                try { await Task.Delay(delay).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (IsHandedOff) Abort("交接后主程序未在预期时间内退出，已恢复备份任务");
            });
        }
    }

    /// <summary>
    /// Ends the attempt. Idempotent: calling it twice, or after a successful install, does not
    /// release somebody else's hold. Returns true when this call actually released the lease.
    /// </summary>
    public bool Abort(string reason)
    {
        int previous = Interlocked.CompareExchange(ref _state, Aborted, Open);
        if (previous == HandedOff)
        {
            // The process is still here even though we handed off: undo the state so a late exit
            // path cannot be mistaken for a completed install.
            Interlocked.Exchange(ref _state, Aborted);
        }
        else if (previous == Aborted)
        {
            return false; // already ended; do not log or release twice
        }
        bool released = _lease?.Release() ?? false;
        _log?.Invoke($"已取消更新（{reason}）{(released ? "，已恢复启动备份任务" : "，维护状态已由其他流程释放")}");
        return released;
    }

    public void Dispose() => Abort("安装会话结束");
}
