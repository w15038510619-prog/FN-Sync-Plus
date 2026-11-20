using System;
using System.ComponentModel;
using System.Diagnostics;

namespace FNSyncPlus.Core.Updates;

/// <summary>Three-state process verdict: a supervisor may NEVER treat "cannot tell" as "gone".</summary>
public enum ProcessProbeState
{
    /// <summary>Confirmed still running (PID alive AND start time matches the captured identity).</summary>
    Running,
    /// <summary>Confirmed gone: no such PID, or the PID was reused by a DIFFERENT process
    /// (start time mismatch), which also proves the original process is gone.</summary>
    Exited,
    /// <summary>Cannot confirm either way (query threw, access denied, platform limitation).
    /// Unknown is NOT Exited: no safety decision may be derived from it.</summary>
    Unknown,
}

/// <summary>
/// Identity of the real updater process for one handoff operation. A PID alone is not an
/// identity — PIDs are recycled — so the probe also pins the process start time and, when the
/// OS lets us read it, the executable path. The operation ID ties every piece of evidence
/// (state file, journal lines, version pointer) to THIS install attempt only.
/// </summary>
public sealed record UpdaterProcessIdentity(
    int ProcessId,
    DateTime StartTimeUtc,
    string? ExecutablePath,
    string OperationId)
{
    /// <summary>Captures the identity right after Process.Start, while the handle is fresh.</summary>
    public static UpdaterProcessIdentity Capture(Process process, string operationId)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (string.IsNullOrWhiteSpace(operationId)) throw new ArgumentException("操作标识不能为空", nameof(operationId));
        DateTime started = process.StartTime.ToUniversalTime();
        string? path = null;
        try { path = process.MainModule?.FileName; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            // The path is corroborating evidence, not required: PID + start time already reject
            // reuse. A denied read must not fail the capture.
        }
        return new UpdaterProcessIdentity(process.Id, started, path, operationId);
    }

    /// <summary>
    /// Probes the process table for THIS identity. PID reuse resolves to Exited (the process we
    /// started is provably gone); any query failure resolves to Unknown, never to Exited.
    /// </summary>
    public ProcessProbeState Probe(out string detail)
    {
        Process process;
        try { process = Process.GetProcessById(ProcessId); }
        catch (ArgumentException)
        {
            detail = $"pid {ProcessId} 不存在（已确认退出）";
            return ProcessProbeState.Exited;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            detail = $"pid {ProcessId} 查询失败（{ex.GetType().Name}），状态未知";
            return ProcessProbeState.Unknown;
        }

        using (process)
        {
            bool exited;
            try { exited = process.HasExited; }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                detail = $"pid {ProcessId} 存活状态读取失败（{ex.GetType().Name}），状态未知";
                return ProcessProbeState.Unknown;
            }
            if (exited)
            {
                detail = $"pid {ProcessId} 已退出（op {OperationId}）";
                return ProcessProbeState.Exited;
            }

            DateTime started;
            try { started = process.StartTime.ToUniversalTime(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
            {
                detail = $"pid {ProcessId} 启动时间读取失败（{ex.GetType().Name}），无法确认身份";
                return ProcessProbeState.Unknown;
            }
            if (started != StartTimeUtc)
            {
                // Same PID, different start time: our updater is gone and the slot was reused.
                detail = $"pid {ProcessId} 已被其他进程复用（启动时间不符），原更新器已确认退出";
                return ProcessProbeState.Exited;
            }

            if (ExecutablePath != null)
            {
                string? currentPath = null;
                try { currentPath = process.MainModule?.FileName; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
                {
                    detail = $"pid {ProcessId} 路径校验被拒绝（{ex.GetType().Name}），状态未知";
                    return ProcessProbeState.Unknown;
                }
                if (currentPath != null &&
                    !string.Equals(currentPath, ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    detail = $"pid {ProcessId} 路径与交接时不符（{currentPath}），按未知处理";
                    return ProcessProbeState.Unknown;
                }
            }

            detail = $"pid {ProcessId} 仍在运行（op {OperationId}）";
            return ProcessProbeState.Running;
        }
    }
}
