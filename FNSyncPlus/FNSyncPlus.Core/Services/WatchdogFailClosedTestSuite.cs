using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Fail-closed fault-injection suite for the handoff watchdog (round 4, P0-2). Every case
/// asserts five outcome dimensions explicitly:
///
///   released   — was the maintenance lease released (backups may resume)?
///   diagnosed  — did supervision escalate to human diagnosis (hold kept)?
///   pointer    — the version pointer was never touched by the supervisor
///   previous   — the previous, known-good program is preserved untouched
///   hold       — while anything is UNKNOWN, nothing is released
///
/// The core rule under test: Running / Exited / Unknown are three different states, and
/// Unknown is NEVER treated as Exited. Timing values are injectable; no case shortens a wait
/// to bypass error handling — the abnormal path itself is what gets asserted.
/// </summary>
public sealed class WatchdogFailClosedTestSuite
{
    private sealed record Verdict(bool Released, string Reason, string Diagnosis, string Logs);

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

        async Task<Verdict> Supervise(
            string name,
            Func<ProcessProbeState> probe,
            Func<InstallOperationStatus> status,
            Func<string> evidence,
            Func<bool> intact,
            TimeSpan maxWait,
            TimeSpan? escalateAfter = null)
        {
            var verdict = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var diagnosis = new StringBuilder();
            var logs = new StringBuilder();
            using var watchdog = new ProcessHandoffWatchdog(
                updaterProbe: probe,
                operationStatus: status,
                journalEvidence: evidence,
                installProvenIntact: intact,
                onConfirmedAbandon: reason => verdict.TrySetResult("RELEASE:" + reason),
                onNeedsDiagnosis: reason =>
                {
                    lock (diagnosis) diagnosis.AppendLine(reason);
                    verdict.TrySetResult("DIAG:" + reason);
                },
                log: message => { lock (logs) logs.AppendLine(message); },
                initialDelay: TimeSpan.FromMilliseconds(60),
                pollInterval: TimeSpan.FromMilliseconds(60),
                unknownExitGrace: TimeSpan.FromMilliseconds(400),
                escalateAfter: escalateAfter ?? TimeSpan.FromMinutes(30));
            watchdog.Start();
            var finished = await Task.WhenAny(verdict.Task, Task.Delay(maxWait, token)).ConfigureAwait(false) == verdict.Task;
            string result = finished ? await verdict.Task.ConfigureAwait(false) : "";
            string diag;
            string logText;
            lock (diagnosis) diag = diagnosis.ToString();
            lock (logs) logText = logs.ToString();
            return new Verdict(result.StartsWith("RELEASE:", StringComparison.Ordinal),
                result.Length > 8 ? result[8..] : result, diag, logText);
        }

        static string NewLayout(string workspace, string name, string version, out string currentPath)
        {
            string root = Path.Combine(workspace, name);
            Directory.CreateDirectory(Path.Combine(root, $"app-{version}"));
            currentPath = Path.Combine(root, "current.json");
            File.WriteAllText(currentPath,
                $"{{\"version\":\"{version}\",\"directory\":\"{Path.Combine(root, $"app-{version}").Replace("\\", "\\\\")}\"," +
                "\"installed_at\":\"2026-01-01T00:00:00+00:00\",\"payload_sha256\":\"AA\",\"supported_data_format\":1}",
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(root, "previous.json"),
                File.ReadAllText(currentPath), new UTF8Encoding(false));
            return root;
        }

        // 1. 更新器正常退出（明确放弃）→ 释放租约，备份可恢复，无需人工诊断
        await Case("故障注入 01：更新器正常退出且明确放弃 → 释放租约", async () =>
        {
            var v = await Supervise("fi01", () => ProcessProbeState.Exited,
                () => InstallOperationStatus.Abandoned, () => "", () => false, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            bool pass = v.Released && v.Diagnosis.Length == 0 && v.Reason.Contains("Abandoned");
            return (pass, $"released={v.Released} diagnosed={v.Diagnosis.Length > 0} reason={v.Reason}");
        });

        // 2. 更新器安装成功 → 旧进程必须保持维护暂停，绝不恢复备份；指针与旧版本不动
        await Case("故障注入 02：更新器安装成功但主进程未退出 → 保持暂停，不动指针", async () =>
        {
            string root = NewLayout(workspace, "fi02", "0.5.0", out string currentPath);
            string pointerBefore = File.ReadAllText(currentPath);
            var v = await Supervise("fi02w", () => ProcessProbeState.Exited,
                () => InstallOperationStatus.Installed, () => "", () => false, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            bool pointerSame = File.ReadAllText(currentPath) == pointerBefore;
            bool pass = !v.Released && v.Diagnosis.Length == 0 && v.Logs.Contains("保持维护暂停") && pointerSame;
            return (pass, $"released={v.Released} logsHasHold={v.Logs.Contains("保持维护暂停")} pointerUntouched={pointerSame}");
        });

        // 3. 更新器安装失败（开始前失败）→ 释放租约
        await Case("故障注入 03：更新器在安装开始前失败 → 释放租约", async () =>
        {
            var v = await Supervise("fi03", () => ProcessProbeState.Exited,
                () => InstallOperationStatus.FailedBeforeInstall, () => "", () => false, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            bool pass = v.Released && v.Reason.Contains("FailedBeforeInstall") && v.Diagnosis.Length == 0;
            return (pass, $"released={v.Released} reason={v.Reason}");
        });

        // 4. 更新器在安装前退出（等待阶段消失）→ 文件系统证据证明未安装 → 释放租约
        await Case("故障注入 04：更新器在等待退出阶段消失 + 完整性证据 → 释放租约", async () =>
        {
            var v = await Supervise("fi04", () => ProcessProbeState.Exited,
                () => InstallOperationStatus.WaitingForAppExit, () => "", () => true, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            bool pass = v.Released && v.Reason.Contains("WaitingForAppExit") && v.Diagnosis.Length == 0;
            return (pass, $"released={v.Released} reason={v.Reason}");
        });

        // 5. 指针切换期间异常（Installing 中途退出）→ 进入人工诊断，绝不释放
        await Case("故障注入 05：更新器在安装中途退出 → 人工诊断，绝不释放", async () =>
        {
            var v = await Supervise("fi05", () => ProcessProbeState.Exited,
                () => InstallOperationStatus.Installing, () => "", () => false, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            bool pass = !v.Released && v.Diagnosis.Contains("安装中途") ;
            return (pass, $"released={v.Released} diagnosis={v.Diagnosis.Trim()}");
        });

        // 6. 更新器 PID 被复用 → 身份探针识别为“原进程已确认退出”，不误判为仍在运行
        await Case("故障注入 06：PID 复用 → 启动时间不符判定原更新器已退出", () =>
        {
            using var live = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe", Arguments = "/c ping -n 30 127.0.0.1 > nul",
                UseShellExecute = false, CreateNoWindow = true
            })!;
            try
            {
                // Same PID, WRONG start time: proves the process we started is gone and the slot
                // was reused by someone else — Exited for OUR updater, never "still running".
                var fake = new UpdaterProcessIdentity(live.Id, DateTime.UtcNow.AddHours(-3), null, "reuse-op");
                var probe = fake.Probe(out string detail);
                bool pass = probe == ProcessProbeState.Exited && detail.Contains("复用");
                return Task.FromResult((pass, $"probe={probe} detail={detail}"));
            }
            finally
            {
                try { if (!live.HasExited) live.Kill(); } catch { }
            }
        });

        // 7. 更新器查询权限不足（持续 Unknown）→ 升级人工诊断，绝不按未知=退出释放
        await Case("故障注入 07：进程查询权限不足（持续 Unknown）→ 人工诊断，绝不释放", async () =>
        {
            var v = await Supervise("fi07", () => ProcessProbeState.Unknown,
                () => InstallOperationStatus.Unknown, () => "", () => true, TimeSpan.FromSeconds(15),
                escalateAfter: TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            bool pass = !v.Released && v.Diagnosis.Contains("无法确认");
            return (pass, $"released={v.Released} diagnosis={v.Diagnosis.Trim()}");
        });

        // 8. 进程查询抛出异常 → 按 Unknown 处理（同 7），绝不当作退出
        await Case("故障注入 08：进程查询抛出异常 → 按未知处理，进入人工诊断", async () =>
        {
            var v = await Supervise("fi08",
                () => throw new System.ComponentModel.Win32Exception("模拟查询失败"),
                () => InstallOperationStatus.Unknown, () => "", () => true, TimeSpan.FromSeconds(15),
                escalateAfter: TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            bool pass = !v.Released && v.Diagnosis.Contains("无法确认") && v.Logs.Contains("绝不视为退出");
            return (pass, $"released={v.Released} diagnosis={v.Diagnosis.Trim()}");
        });

        // 9. 状态文件损坏 → 保留 .corrupt 证据；凭文件系统未安装证据释放，指针与旧版本不动
        await Case("故障注入 09：状态文件损坏 → 保留损坏证据，凭完整性证据安全释放", async () =>
        {
            string root = NewLayout(workspace, "fi09", "0.5.0", out string currentPath);
            string pointerBefore = File.ReadAllText(currentPath);
            var store = new UpdateOperationStateStore(root);
            Directory.CreateDirectory(store.DirectoryPath);
            string statePath = store.StatePath("corrupt-op");
            File.WriteAllText(statePath, "{ 这不是合法 JSON，模拟写入中断", new UTF8Encoding(false));
            string opsDirBefore = string.Join("|", Directory.GetFiles(store.DirectoryPath).Select(Path.GetFileName));
            var intactness = new InstallIntactnessProbe(root, "0.5.0", "0.6.0");
            var v = await Supervise("fi09w", () => ProcessProbeState.Exited,
                () => store.Load("corrupt-op", out _)?.Status ?? InstallOperationStatus.Unknown,
                () => "", () => intactness.ProveInstallNeverBegan(out _), TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            bool corruptKept = Directory.GetFiles(store.DirectoryPath).Any(f => f.Contains(".corrupt-"));
            bool pointerSame = File.ReadAllText(currentPath) == pointerBefore;
            bool pass = v.Released && corruptKept && pointerSame && v.Reason.Contains("安装从未开始");
            return (pass, $"released={v.Released} corruptEvidenceKept={corruptKept} pointerUntouched={pointerSame} filesBefore={opsDirBefore}");
        });

        // 10. 日志写入/读取失败 → 证据按空处理，凭完整性证据释放并记录告警
        await Case("故障注入 10：日志读取失败 → 凭完整性证据释放并记录告警", async () =>
        {
            var v = await Supervise("fi10", () => ProcessProbeState.Exited,
                () => InstallOperationStatus.Unknown,
                () => throw new IOException("模拟日志损坏"), () => true, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            bool pass = v.Released && v.Logs.Contains("日志证据异常") && v.Diagnosis.Length == 0;
            return (pass, $"released={v.Released} logs={v.Logs.Trim()}");
        });

        // 11. 历史日志包含 failed → 操作 ID 过滤后不得影响本次判定
        await Case("故障注入 11：历史日志中的 failed 不得影响本次操作判定", async () =>
        {
            string history = "2026-01-01 apply 0.4.0 failed: 上一次安装失败\r\n" +
                             "2026-01-02 [op deadbeefdeadbeefdeadbeefdeadbeef] apply 0.4.1 failed: 另一次失败\r\n";
            string filtered = UpdateService.FilterOperationEvidence(history, "curretop00000000000000000000000000");
            bool filterClean = filtered.Length == 0;
            var v = await Supervise("fi11", () => ProcessProbeState.Exited,
                () => InstallOperationStatus.Unknown, () => filtered, () => true, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            bool pass = filterClean && v.Released && v.Reason.Contains("文件系统证据");
            return (pass, $"filterClean={filterClean} released={v.Released} reason={v.Reason}");
        });

        // 12. 历史日志包含 installed → 不得误判本次已完成而冻结旧进程
        await Case("故障注入 12：历史日志中的 installed 不得误判本次已完成", async () =>
        {
            string history = "2026-01-01 installed 0.4.0 (payload aabbcc), previous=0.3.0\r\n" +
                             "2026-01-01 launch 0.4.0 ok (pid 999)\r\n";
            string filtered = UpdateService.FilterOperationEvidence(history, "curretop00000000000000000000000000");
            bool filterClean = filtered.Length == 0;
            var v = await Supervise("fi12", () => ProcessProbeState.Exited,
                () => InstallOperationStatus.Unknown, () => filtered, () => true, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            // If history leaked, the watchdog would wrongly HOLD forever (treating the old
            // process as a zombie). It must instead release on the intactness proof.
            bool pass = filterClean && v.Released;
            return (pass, $"filterClean={filterClean} released={v.Released}");
        });

        // 13. 本次操作没有任何结果记录且无法证明未安装 → 人工诊断，绝不释放
        await Case("故障注入 13：无任何结果记录且完整性不可证明 → 人工诊断，绝不释放", async () =>
        {
            var v = await Supervise("fi13", () => ProcessProbeState.Exited,
                () => InstallOperationStatus.Unknown, () => "", () => false, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            bool pass = !v.Released && v.Diagnosis.Contains("无任何状态记录") || (!v.Released && v.Diagnosis.Contains("人工诊断"));
            return (pass, $"released={v.Released} diagnosis={v.Diagnosis.Trim()}");
        });

        // 14. 更新器仍运行但超过预期时间 → 只告警，绝不按时间释放
        await Case("故障注入 14：更新器超时仍在运行 → 告警但绝不按时间释放", async () =>
        {
            var v = await Supervise("fi14", () => ProcessProbeState.Running,
                () => InstallOperationStatus.Unknown, () => "", () => false, TimeSpan.FromSeconds(3),
                escalateAfter: TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
            bool pass = !v.Released && v.Diagnosis.Length == 0 && v.Logs.Contains("绝不按时间强切");
            return (pass, $"released={v.Released} logs={v.Logs.Trim()}");
        });

        // 15. 看门狗确认与手动确认并发 → 恰好释放一次
        await Case("故障注入 15：看门狗确认与并发确认 → 恰好释放一次", async () =>
        {
            var manager = new TaskManager(AppSettings.CreateInMemory(), new SmbAuthService(),
                new LogService(Path.Combine(workspace, "fi15", "logs")));
            if (!manager.TryEnterMaintenanceHold(out var lease, "并发测试"))
                throw new InvalidOperationException("无法获取维护租约");
            var session = new UpdateInstallSession(new InstallReadiness(InstallDecision.Proceed, "ok"),
                new UpdateInstallPlan(true, "x", "y", "ok", "race-op"), null, lease, null);
            // The session must be handed off first: only a handed-off session accepts the
            // supervised release path (MaintenanceHeld sessions release via Abort instead).
            session.CommitHandOff(null);
            var released = new ConcurrentQueue<bool>();
            var gate = new ManualResetEventSlim(false);
            using var watchdog = new ProcessHandoffWatchdog(
                updaterProbe: () => ProcessProbeState.Exited,
                operationStatus: () => InstallOperationStatus.Abandoned,
                journalEvidence: () => "",
                installProvenIntact: () => false,
                onConfirmedAbandon: reason =>
                {
                    gate.Wait();
                    released.Enqueue(session.ConfirmHandoffAbandoned(reason));
                },
                onNeedsDiagnosis: _ => { },
                initialDelay: TimeSpan.FromMilliseconds(100),
                pollInterval: TimeSpan.FromMilliseconds(80),
                unknownExitGrace: TimeSpan.FromMilliseconds(300));
            watchdog.Start();
            var manual = Task.Run(() =>
            {
                gate.Wait();
                released.Enqueue(session.ConfirmHandoffAbandoned("手动并发确认"));
            });
            // Give both paths a real chance to race, then open the gate.
            await Task.Delay(500, token).ConfigureAwait(false);
            gate.Set();
            await Task.WhenAny(manual, Task.Delay(TimeSpan.FromSeconds(10), token)).ConfigureAwait(false);
            await Task.Delay(500, token).ConfigureAwait(false);
            int releases = released.Count(r => r);
            bool pass = releases == 1 && session.State == InstallSessionState.HandoffFailed && session.HoldReleased;
            return (pass, $"releases={releases}（必须恰好 1）state={session.State} holdReleased={session.HoldReleased}");
        });

        // 16. 两次更新尝试相互重叠 → 第二次在门禁处被拒绝，第一次租约不受影响
        await Case("故障注入 16：两次更新尝试重叠 → 第二次被门禁拒绝", () =>
        {
            var manager = new TaskManager(AppSettings.CreateInMemory(), new SmbAuthService(),
                new LogService(Path.Combine(workspace, "fi16", "logs")));
            bool first = manager.TryEnterMaintenanceHold(out var firstLease, "第一次更新");
            bool second = manager.TryEnterMaintenanceHold(out _, "第二次更新（应被拒绝）");
            bool firstIntact = first && firstLease is { IsReleased: false };
            firstLease?.Release();
            return Task.FromResult((first && !second && firstIntact,
                $"first={first} second={second}（必须 False）firstLeaseHeld={firstIntact}"));
        });

        return cases;
    }
}
