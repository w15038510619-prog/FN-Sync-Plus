using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Real-process handoff lifecycle suite (P0-A, round 3). The first three cases were written
/// BEFORE the fix and documented the real defect (Dispose/Abort released a handed-off lease);
/// they stay as regression guards. The remaining cases exercise the state machine, the
/// journal+process watchdog against REAL child processes, and the scheduler's exit coordination.
/// </summary>
public sealed class UpdateHandoffLifecycleTestSuite
{
    public async Task<IReadOnlyList<UpdateSuiteCase>> RunAsync(string workspace, CancellationToken token = default)
    {
        Directory.CreateDirectory(workspace);
        var cases = new List<UpdateSuiteCase>();

        async Task Case(string name, Func<Task<(bool Passed, string Detail)>> body)
        {
            try
            {
                var (passed, detail) = await body().ConfigureAwait(false);
                cases.Add(new UpdateSuiteCase(name, passed, detail));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                cases.Add(new UpdateSuiteCase(name, false, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        TaskManager NewManager(string name)
        {
            string root = Path.Combine(workspace, name);
            Directory.CreateDirectory(root);
            return new TaskManager(AppSettings.CreateInMemory(), new SmbAuthService(),
                new LogService(Path.Combine(root, "logs")));
        }

        UpdateInstallSession NewGrantedSession(TaskManager manager, out MaintenanceLease lease)
        {
            if (!manager.TryEnterMaintenanceHold(out lease!, "确认测试"))
                throw new InvalidOperationException("无法获取维护租约，测试前置失败");
            return new UpdateInstallSession(
                new InstallReadiness(InstallDecision.Proceed, "备份已安全停止，可以安装"),
                new UpdateInstallPlan(true, "updater.exe", "--apply --release x", "就绪"),
                null, lease, null);
        }

        // ---- 确认用例（修复前 FAIL，已留作回归）----
        await Case("成功交接给更新器后 Dispose 不得释放维护租约", () =>
        {
            var manager = NewManager("dispose-after-handoff");
            using var session = NewGrantedSession(manager, out _);
            session.CommitHandOff(null);
            bool heldAtHandoff = manager.IsMaintenanceHoldActive && session.HoldsQueue;
            session.Dispose();
            bool heldAfterDispose = manager.IsMaintenanceHoldActive && session.HoldsQueue;
            return Task.FromResult((heldAtHandoff && heldAfterDispose,
                $"handoff 时持有={heldAtHandoff}，Dispose 后仍持有={heldAfterDispose}"));
        });

        await Case("成功交接后 Abort 不得释放维护租约", () =>
        {
            var manager = NewManager("abort-after-handoff");
            using var session = NewGrantedSession(manager, out _);
            session.CommitHandOff(null);
            bool refused = !session.Abort("迟到的取消");
            bool stillHeld = manager.IsMaintenanceHoldActive && session.HoldsQueue;
            return Task.FromResult((refused && stillHeld,
                $"Abort 被拒绝={refused}，维护租约仍被持有={stillHeld}"));
        });

        await Case("交接前中止会释放维护租约并恢复备份", () =>
        {
            var manager = NewManager("abort-before-handoff");
            using var session = NewGrantedSession(manager, out _);
            bool released = session.Abort("安装前取消");
            bool resumed = !manager.IsMaintenanceHoldActive && !session.HoldsQueue;
            return Task.FromResult((released && resumed, $"released={released} resumed={resumed}"));
        });

        // ---- 状态机转换 ----
        await Case("状态机沿 维护持有→更新器已启动→请求退出 推进且不回退", () =>
        {
            var manager = NewManager("state-machine");
            using var session = NewGrantedSession(manager, out _);
            bool startsAtHeld = session.State == InstallSessionState.MaintenanceHeld;
            session.CommitHandOff(null);
            bool updaterStarted = session.State == InstallSessionState.UpdaterStarted && session.IsHandedOff;
            bool shutdownMarked = session.MarkShutdownRequested();
            bool requested = session.State == InstallSessionState.ShutdownRequested;
            bool secondMark = session.MarkShutdownRequested();
            return Task.FromResult((startsAtHeld && updaterStarted && shutdownMarked && requested && !secondMark,
                $"held={startsAtHeld} started={updaterStarted} marked={shutdownMarked} requested={requested} dupMark={secondMark}"));
        });

        await Case("监督确认放弃是交接后唯一的释放路径", () =>
        {
            var manager = NewManager("supervised-release");
            using var session = NewGrantedSession(manager, out _);
            session.CommitHandOff(null);
            session.MarkShutdownRequested();
            bool released = session.ConfirmHandoffAbandoned("监督测试：更新器确认放弃");
            bool failedState = session.State == InstallSessionState.HandoffFailed;
            bool resumed = !manager.IsMaintenanceHoldActive && session.HoldReleased;
            bool duplicate = session.ConfirmHandoffAbandoned("重复确认必须无效");
            return Task.FromResult((released && failedState && resumed && !duplicate,
                $"released={released} state={failedState} resumed={resumed} duplicateRejected={duplicate}"));
        });

        await Case("并发确认放弃恰好释放一次租约", () =>
        {
            var manager = NewManager("race-release");
            using var session = NewGrantedSession(manager, out _);
            session.CommitHandOff(null);
            var results = new System.Collections.Concurrent.ConcurrentQueue<bool>();
            var gate = new ManualResetEventSlim(false);
            var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                gate.Wait();
                results.Enqueue(session.ConfirmHandoffAbandoned("并发确认"));
            })).ToArray();
            gate.Set();
            Task.WaitAll(workers);
            int releasedCount = results.Count(r => r);
            bool consistent = session.State == InstallSessionState.HandoffFailed && session.HoldReleased;
            return Task.FromResult((releasedCount == 1 && consistent,
                $"releasedCount={releasedCount}（必须恰好 1 次）state={session.State}"));
        });

        await Case("重复 Dispose 与 Abort 幂等，不产生重复释放", () =>
        {
            var manager = NewManager("idempotent");
            using var session = NewGrantedSession(manager, out _);
            bool first = session.Abort("第一次");
            bool second = session.Abort("第二次");
            session.Dispose();
            session.Dispose();
            return Task.FromResult((first && !second && !manager.IsMaintenanceHoldActive,
                $"first={first} second={second} holdActive={manager.IsMaintenanceHoldActive}"));
        });

        // ---- 真实进程级 watchdog ----
        // A REAL child process plays the updater, and a REAL journal file on disk carries the
        // updater's verdict — the liveness probe and journal evidence are genuine, not fakes.
        async Task<(string Reason, bool HoldActive, InstallSessionState State, string Logs)> SuperviseAsync(
            string name, string fakeUpdaterScript, string journalAfterExit, TimeSpan maxWait)
        {
            string dir = Path.Combine(workspace, name);
            Directory.CreateDirectory(dir);
            string journal = Path.Combine(dir, "update.log");
            File.WriteAllText(journal, "installed 0.1.0 (历史记录，在基线之前)\r\n", new UTF8Encoding(false));
            long baseline = new FileInfo(journal).Length;

            string script = Path.Combine(dir, "fake-updater.cmd");
            File.WriteAllText(script, fakeUpdaterScript, new UTF8Encoding(false));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })!;

            var manager = NewManager(name);
            if (!manager.TryEnterMaintenanceHold(out var lease, "watchdog 测试"))
                throw new InvalidOperationException("无法获取维护租约，测试前置失败");
            var session = new UpdateInstallSession(
                new InstallReadiness(InstallDecision.Proceed, "ok"),
                new UpdateInstallPlan(true, "x", "y", "ok"), null, lease, null);
            var verdict = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var supervisionLog = new System.Text.StringBuilder();
            session.CommitHandOff(new ProcessHandoffWatchdog(
                updaterStillRunning: () =>
                {
                    try { return !process.HasExited; }
                    catch { return false; }
                },
                journalTail: () => ReadTail(journal, baseline),
                onConfirmedAbandon: reason =>
                {
                    // Same wiring the production UpdateService uses: the supervised release goes
                    // through the session so the lease is released exactly once.
                    session.ConfirmHandoffAbandoned(reason);
                    verdict.TrySetResult(reason);
                },
                log: message => { lock (supervisionLog) supervisionLog.AppendLine(message); },
                initialDelay: TimeSpan.FromMilliseconds(250),
                pollInterval: TimeSpan.FromMilliseconds(120),
                unknownExitGrace: TimeSpan.FromMilliseconds(600),
                escalateAfter: TimeSpan.FromMinutes(30)));

            // Let the fake updater run briefly, then make its "exit" happen (the script itself
            // ends) and optionally write the journal line a real updater would have written.
            await Task.Delay(400, token).ConfigureAwait(false);
            if (journalAfterExit.Length > 0)
                File.AppendAllText(journal, journalAfterExit + "\r\n", new UTF8Encoding(false));

            var finished = await Task.WhenAny(verdict.Task, Task.Delay(maxWait, token)).ConfigureAwait(false)
                == verdict.Task;
            string reason = finished ? await verdict.Task.ConfigureAwait(false) : "";

            // Never leave fake updater children behind; they belong to this suite only.
            try { if (!process.HasExited) process.Kill(true); }
            catch { /* already gone or handle closed: nothing to clean up */ }

            return (reason, manager.IsMaintenanceHoldActive, session.State, supervisionLog.ToString());
        }

        await Case("更新器放弃等待后监督机制释放维护租约（真实进程）", async () =>
        {
            var (reason, holdActive, state, _) = await SuperviseAsync("wd-abandon",
                "@echo off\r\nping -n 2 127.0.0.1 > nul\r\n",
                "apply: app pid 4242 still running after 180s, aborting",
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            bool pass = reason.Contains("still running after") && !holdActive &&
                        state == InstallSessionState.HandoffFailed;
            return (pass, $"reason={reason} holdActive={holdActive} state={state}");
        });

        await Case("更新器仍在运行时绝不按时间释放租约（真实进程）", async () =>
        {
            var (reason, holdActive, _, logs) = await SuperviseAsync("wd-alive",
                "@echo off\r\nping -n 60 127.0.0.1 > nul\r\n", "",
                TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            // The watchdog must stay silent while the updater lives and keep the hold.
            return (reason.Length == 0 && holdActive,
                $"watchdog 不应确认（reason=\"{reason}\"）holdActive={holdActive}（2 秒后仍须为 True）logs={logs.Trim()}");
        });

        await Case("更新器完成安装但主进程未退出时保持维护暂停（真实进程）", async () =>
        {
            var (reason, holdActive, state, logs) = await SuperviseAsync("wd-zombie",
                "@echo off\r\nping -n 2 127.0.0.1 > nul\r\n",
                "apply 0.6.0 from verified release (sha abc)\r\ninstalled 0.6.0 (payload deadbeef)",
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            // No abandon may be confirmed here: the callback must never fire, the hold stays,
            // and the anomaly is visible in the supervision log.
            bool pass = reason.Length == 0 && holdActive && state == InstallSessionState.UpdaterStarted &&
                        logs.Contains("保持维护暂停");
            return (pass, $"reason=\"{reason}\" holdActive={holdActive} state={state} logs={logs.Trim()}");
        });

        await Case("更新器异常退出无日志时宽限后恢复备份（真实进程）", async () =>
        {
            var (reason, holdActive, state, _) = await SuperviseAsync("wd-vanish",
                "@echo off\r\nping -n 2 127.0.0.1 > nul\r\n", "",
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            bool pass = reason.Contains("异常退出") && !holdActive && state == InstallSessionState.HandoffFailed;
            return (pass, $"reason={reason} holdActive={holdActive} state={state}");
        });

        return cases;
    }

    private static string ReadTail(string journalPath, long baseline)
    {
        using var stream = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length <= baseline) return "";
        stream.Seek(baseline, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
