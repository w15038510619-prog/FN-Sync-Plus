using System;
using System.Threading;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.Core.Updates;

/// <summary>Lifecycle of one update attempt, as observed by the main program.</summary>
public enum InstallSessionState
{
    /// <summary>Created; the install verdict has not been decided yet.</summary>
    Preparing = 0,

    /// <summary>The session owns the maintenance lease; no updater has been started.</summary>
    MaintenanceHeld = 1,

    /// <summary>The updater process is running and waiting for this process to exit.</summary>
    UpdaterStarted = 2,

    /// <summary>A safe exit was requested from the application lifecycle coordinator.</summary>
    ShutdownRequested = 3,

    /// <summary>Failed before the handoff; the lease was released and backups resumed.</summary>
    Aborted = 4,

    /// <summary>The handoff was supervised to a confirmed failure; the lease was released and
    /// backups resumed. Only <see cref="ConfirmHandoffAbandoned"/> may reach this state — never a
    /// timer, never a Dispose.</summary>
    HandoffFailed = 5,
}

/// <summary>
/// The lifecycle of one update attempt, from "may I pause backups" to "the updater owns the
/// installation" or "we are back to normal".
///
/// Legal transitions:
///   Preparing / MaintenanceHeld → Aborted            (AbortBeforeHandoff: lease released, backups resume)
///   MaintenanceHeld → UpdaterStarted                  (CommitHandoff: the updater is now waiting for us)
///   UpdaterStarted → ShutdownRequested                (MarkShutdownRequested: lifecycle coordinator asked)
///   UpdaterStarted / ShutdownRequested → HandoffFailed (ConfirmHandoffAbandon: the supervisor confirmed
///                                                      the updater gave up or died — the ONLY release path
///                                                      after a handoff)
///
/// The single rule that makes this safe: the maintenance lease is released in exactly one of two
/// places — <see cref="Abort"/> before the handoff, or <see cref="ConfirmHandoffAbandon"/> after the
/// supervised watchdog proved the updater is gone and gave up. <see cref="Dispose"/> only aborts
/// sessions that were never handed off; a handed-off session keeps its lease on purpose, because
/// releasing it while the updater waits for our exit would let a new backup start mid-swap.
/// </summary>
public sealed class UpdateInstallSession : IDisposable
{
    private readonly MaintenanceLease? _lease;
    private readonly Action<string>? _log;
    private IUpdaterHandoffWatchdog? _watchdog;
    private int _state;

    internal UpdateInstallSession(InstallReadiness readiness, UpdateInstallPlan plan, VerifiedRelease? release,
        MaintenanceLease? lease, Action<string>? log)
    {
        Readiness = readiness;
        Plan = plan;
        Release = release;
        _lease = lease;
        _log = log;
        // A session that never got a lease cannot hold the queue and is already finished.
        _state = lease == null ? (int)InstallSessionState.Aborted : (int)InstallSessionState.MaintenanceHeld;
    }

    public InstallReadiness Readiness { get; }
    public UpdateInstallPlan Plan { get; }
    public VerifiedRelease? Release { get; }

    /// <summary>PID of the updater process, once started. Recorded for diagnostics and for the
    /// process-level acceptance report (which must name both PIDs).</summary>
    public int? UpdaterProcessId { get; private set; }

    /// <summary>Unique ID of this install attempt. The updater's journal lines, the operation
    /// state file, the version pointer and the watchdog all share it, so evidence from older
    /// attempts can never be mistaken for this one.</summary>
    public string OperationId => Plan.OperationId;

    /// <summary>Full identity of the updater process (PID + start time + path), captured at start.</summary>
    public UpdaterProcessIdentity? UpdaterIdentity { get; private set; }

    internal void RecordUpdaterProcess(UpdaterProcessIdentity identity)
    {
        UpdaterIdentity = identity;
        UpdaterProcessId = identity.ProcessId;
    }

    public InstallSessionState State => (InstallSessionState)Volatile.Read(ref _state);

    /// <summary>True once the updater owns the installation (started, exit requested or beyond).</summary>
    public bool IsHandedOff => State is InstallSessionState.UpdaterStarted or InstallSessionState.ShutdownRequested;

    public bool IsAborted => State is InstallSessionState.Aborted;
    public bool IsOpen => State is InstallSessionState.Preparing or InstallSessionState.MaintenanceHeld;

    /// <summary>True when backups are no longer being held back by this session.</summary>
    public bool HoldReleased => _lease == null || _lease.IsReleased;

    /// <summary>True while this session is the owner of the queue hold.</summary>
    public bool HoldsQueue => _lease is { IsReleased: false };

    /// <summary>
    /// Marks the handoff to the updater and attaches the supervision that guards the lease from
    /// here on. The lease stays held: the app is about to exit and a new backup starting during
    /// the pointer swap is exactly what the hold exists to prevent.
    /// </summary>
    public void CommitHandOff(IUpdaterHandoffWatchdog? watchdog)
    {
        if (Interlocked.CompareExchange(ref _state, (int)InstallSessionState.UpdaterStarted,
                (int)InstallSessionState.MaintenanceHeld) != (int)InstallSessionState.MaintenanceHeld)
            return;
        Interlocked.Exchange(ref _watchdog, watchdog)?.Dispose();
        _log?.Invoke("已交接给更新器，保持维护状态等待主程序退出");
        watchdog?.Start();
    }

    /// <summary>
    /// Records that the application lifecycle coordinator accepted the shutdown request. Legal
    /// only while the updater is running and waiting for this process.
    /// </summary>
    public bool MarkShutdownRequested()
    {
        int previous = Interlocked.CompareExchange(ref _state, (int)InstallSessionState.ShutdownRequested,
            (int)InstallSessionState.UpdaterStarted);
        if (previous == (int)InstallSessionState.UpdaterStarted)
        {
            _log?.Invoke("已请求主程序安全退出，等待进程结束");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Ends the attempt BEFORE the handoff: releases the lease so backups resume immediately.
    /// Refused after a successful handoff — once the updater is running, only the supervised
    /// watchdog may release the hold (see <see cref="ConfirmHandoffAbandoned"/>). Idempotent:
    /// a second call, or a call on a finished session, does nothing and returns false.
    /// Returns true when this call actually released the lease.
    /// </summary>
    public bool Abort(string reason)
    {
        int previous = Interlocked.CompareExchange(ref _state, (int)InstallSessionState.Aborted,
            (int)InstallSessionState.MaintenanceHeld);
        if (previous == (int)InstallSessionState.Preparing)
        {
            previous = Interlocked.CompareExchange(ref _state, (int)InstallSessionState.Aborted,
                (int)InstallSessionState.Preparing);
        }
        if (previous != (int)InstallSessionState.Preparing &&
            previous != (int)InstallSessionState.MaintenanceHeld)
        {
            if (IsHandedOff)
                _log?.Invoke($"拒绝中止已交接的安装（{reason}）：维护租约保持，由交接监督机制管理");
            return false; // already ended or handed off; nothing to release here
        }
        bool released = _lease?.Release() ?? true;
        Interlocked.Exchange(ref _watchdog, null)?.Dispose();
        _log?.Invoke($"已取消更新（{reason}）{(released ? "，已恢复启动备份任务" : "，维护状态已由其他流程释放")}");
        return released;
    }

    /// <summary>
    /// The supervised release path. Legal only after a handoff, and only when the watchdog has
    /// confirmed the updater is no longer running and gave up (journal evidence, or it vanished
    /// without any record after a grace period). This is the ONLY way a handed-off session gives
    /// the maintenance lease back.
    /// </summary>
    public bool ConfirmHandoffAbandoned(string reason)
    {
        int previous = Interlocked.CompareExchange(ref _state, (int)InstallSessionState.HandoffFailed,
            (int)InstallSessionState.UpdaterStarted);
        if (previous != (int)InstallSessionState.UpdaterStarted)
        {
            previous = Interlocked.CompareExchange(ref _state, (int)InstallSessionState.HandoffFailed,
                (int)InstallSessionState.ShutdownRequested);
        }
        if (previous != (int)InstallSessionState.UpdaterStarted &&
            previous != (int)InstallSessionState.ShutdownRequested)
            return false; // already finished (possibly by a concurrent release); no double release
        bool released = _lease?.Release() ?? true;
        Interlocked.Exchange(ref _watchdog, null)?.Dispose();
        _log?.Invoke($"交接监督确认更新器已放弃安装（{reason}），{(released ? "已恢复备份任务" : "维护状态已由其他流程释放")}");
        return released;
    }

    /// <summary>
    /// Aborts the session ONLY when it was never handed off. A handed-off session is left alone:
    /// its lease is guarded by the watchdog, and disposing it here (e.g. by a `using` block in the
    /// scheduler) must not resume backups while the updater is waiting for this process to exit.
    /// </summary>
    public void Dispose()
    {
        if (State is InstallSessionState.Aborted or InstallSessionState.HandoffFailed) return;
        if (IsHandedOff)
        {
            // Deliberately keep the lease: the updater owns the install now.
            return;
        }
        Abort("安装会话结束");
    }
}
