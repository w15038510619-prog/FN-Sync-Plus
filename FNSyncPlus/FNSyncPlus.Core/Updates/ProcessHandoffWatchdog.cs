using System;

namespace FNSyncPlus.Core.Updates;

/// <summary>
/// Guards a handed-off install session. The watchdog never releases the maintenance lease by
/// itself; it only reports a CONFIRMED abandon once the evidence proves the updater is gone —
/// a fixed timer is not evidence.
/// </summary>
public interface IUpdaterHandoffWatchdog : IDisposable
{
    /// <summary>Starts supervision. Must be idempotent and safe to call from any thread.</summary>
    void Start();
}

/// <summary>
/// Watches the real updater process and its journal. Releases the maintenance hold only when the
/// handoff is provably over:
///
/// <list type="bullet">
/// <item><description>Updater still running → NEVER release, no matter how long it takes. It may
/// legitimately spend minutes waiting for our exit or installing.</description></item>
/// <item><description>Updater exited and the journal records that it gave up waiting, refused the
/// release, or the install failed → confirmed abandon, backups resume.</description></item>
/// <item><description>Updater exited and the journal shows a completed install or rollback → the
/// old process must NOT resume backups; the hold stays and the anomaly is logged.</description>
/// </item>
/// <item><description>Updater exited without any journal evidence → wait a grace period for the
/// journal write to land, then treat as an abnormal abandon and resume backups.</description></item>
/// </list>
///
/// All timing values are injectable so the tests can run this against real processes quickly.
/// </summary>
public sealed class ProcessHandoffWatchdog : IUpdaterHandoffWatchdog
{
    private readonly Func<bool> _updaterStillRunning;
    private readonly Func<string> _journalTail;
    private readonly Action<string> _onConfirmedAbandon;
    private readonly Action<string>? _log;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _unknownExitGrace;
    private readonly TimeSpan _escalateAfter;
    private readonly System.Threading.CancellationTokenSource _cts = new();
    private int _started;
    private int _finished;

    public ProcessHandoffWatchdog(Func<bool> updaterStillRunning, Func<string> journalTail,
        Action<string> onConfirmedAbandon, Action<string>? log = null,
        TimeSpan? initialDelay = null, TimeSpan? pollInterval = null,
        TimeSpan? unknownExitGrace = null, TimeSpan? escalateAfter = null)
    {
        _updaterStillRunning = updaterStillRunning ?? throw new ArgumentNullException(nameof(updaterStillRunning));
        _journalTail = journalTail ?? throw new ArgumentNullException(nameof(journalTail));
        _onConfirmedAbandon = onConfirmedAbandon ?? throw new ArgumentNullException(nameof(onConfirmedAbandon));
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
        bool escalated = false;
        while (true)
        {
            bool alive;
            try { alive = _updaterStillRunning(); }
            catch (Exception ex) { alive = false; _log?.Invoke($"更新器存活检查异常：{ex.Message}"); }

            if (!alive)
            {
                string tail;
                try { tail = _journalTail() ?? ""; }
                catch (Exception ex) { tail = ""; _log?.Invoke($"读取更新日志异常：{ex.Message}"); }

                if (ShowsCompletedWork(tail))
                {
                    // The updater finished an install/rollback while this process is still alive:
                    // an old process must never resume backups against a swapped installation.
                    // Keep the hold — do NOT invoke the abandon callback, which would resume work.
                    if (System.Threading.Interlocked.Exchange(ref _finished, 1) == 0)
                        _log?.Invoke($"更新器已完成操作但本进程未退出，保持维护暂停：{FirstLine(tail)}");
                    return;
                }
                if (ShowsAbandon(tail))
                {
                    Confirm(FirstMatching(tail));
                    return;
                }
                // No evidence yet: the journal write may lag behind process exit. Give it a grace
                // window, then re-check. Only after the grace window with still no evidence do we
                // treat the exit as an abandon — an updater that vanished without a word cannot
                // be installing anything.
                DateTime deadline = DateTime.UtcNow + _unknownExitGrace;
                while (DateTime.UtcNow < deadline)
                {
                    try { await System.Threading.Tasks.Task.Delay(
                        System.TimeSpan.FromMilliseconds(Math.Min(500, _pollInterval.TotalMilliseconds)), token)
                        .ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    try { if (_updaterStillRunning()) break; } catch { }
                }
                bool aliveAgain;
                try { aliveAgain = _updaterStillRunning(); }
                catch { aliveAgain = false; }
                if (!aliveAgain)
                {
                    string rechecked;
                    try { rechecked = _journalTail() ?? ""; }
                    catch { rechecked = ""; }
                    if (ShowsCompletedWork(rechecked))
                    {
                        if (System.Threading.Interlocked.Exchange(ref _finished, 1) == 0)
                            _log?.Invoke($"更新器已完成操作但本进程未退出，保持维护暂停：{FirstLine(rechecked)}");
                        return;
                    }
                    Confirm(ShowsAbandon(rechecked) ? FirstMatching(rechecked)
                        : "更新器异常退出且未记录安装成功，已确认放弃本次安装");
                    return;
                }
                // It came back to life (or the liveness probe hiccuped): keep supervising.
            }
            else if (!escalated && DateTime.UtcNow - began >= _escalateAfter)
            {
                escalated = true;
                // Never release on a timer: log loudly so the operator knows, then keep holding.
                _log?.Invoke($"更新器运行已超过 {_escalateAfter.TotalMinutes:F0} 分钟仍未完成交接；继续保持维护暂停，绝不按时间强切");
            }

            try { await System.Threading.Tasks.Task.Delay(_pollInterval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Confirm(string reason)
    {
        if (System.Threading.Interlocked.Exchange(ref _finished, 1) == 1) return;
        try { _onConfirmedAbandon(reason); }
        catch (Exception ex) { _log?.Invoke($"交接放弃回调异常：{ex.Message}"); }
    }

    /// <summary>Journal lines that mean the updater acted on the installation.</summary>
    internal static bool ShowsCompletedWork(string tail) =>
        tail.Contains("installed ", StringComparison.Ordinal) ||
        (tail.Contains("launch", StringComparison.Ordinal) && tail.Contains(" ok", StringComparison.Ordinal)) ||
        tail.Contains("rolled back to", StringComparison.Ordinal);

    /// <summary>Journal lines that mean the updater gave up without touching the installation.</summary>
    internal static bool ShowsAbandon(string tail) =>
        tail.Contains("still running after", StringComparison.Ordinal) ||
        tail.Contains("apply refused", StringComparison.OrdinalIgnoreCase) ||
        tail.Contains("failed", StringComparison.Ordinal);

    internal static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "(无日志)";
        string line = text.Split('\r', '\n')[0];
        return line.Length > 160 ? line[..160] : line;
    }

    private static string FirstMatching(string tail)
    {
        foreach (string line in tail.Split('\r', '\n'))
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
