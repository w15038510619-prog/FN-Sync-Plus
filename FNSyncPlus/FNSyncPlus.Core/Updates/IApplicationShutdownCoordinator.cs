namespace FNSyncPlus.Core.Updates;

/// <summary>
/// Lets the background update scheduler ask the application lifecycle for a safe exit, without
/// the scheduler (a worker thread) reaching into UI code. Implementations must:
///
/// <list type="bullet">
/// <item><description>marshal the request to the UI thread;</description></item>
/// <item><description>reuse the same safe-exit sequence as a manual exit (stop new work, wait for
/// running tasks, flush logs) so a handoff never bypasses shutdown hygiene;</description></item>
/// <item><description>NOT be intercepted by a hide-to-tray "close";</description></item>
/// <item><description>return false when the request could not be delivered (no window, dispatcher
/// gone) — the caller then treats the handoff as undeliverable.</description></item>
/// </list>
///
/// The manual "install now" button and the unattended scheduler must share this coordinator, so
/// there is exactly one way for the program to exit for an update.
/// </summary>
public interface IApplicationShutdownCoordinator
{
    /// <summary>Requests a safe exit. Returns true when the request was delivered, false otherwise.
    /// Delivery does not mean the process already exited — the updater waits for that.</summary>
    bool RequestShutdown(string reason);
}
