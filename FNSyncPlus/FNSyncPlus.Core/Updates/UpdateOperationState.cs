using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Updates;

/// <summary>The explicit result of one install operation, as recorded by the updater.</summary>
public enum InstallOperationStatus
{
    /// <summary>No trustworthy evidence either way. Unknown must never be auto-resolved to a
    /// failure: backups stay paused and a human gets a diagnosable alert.</summary>
    Unknown = 0,

    /// <summary>The updater is waiting for the old application process to exit. Nothing in the
    /// install root has been modified by this operation yet.</summary>
    WaitingForAppExit = 1,

    /// <summary>The updater is modifying the install root (staging / pointer swap). An exited
    /// updater in this state means a potentially half-finished install: hold everything.</summary>
    Installing = 2,

    /// <summary>The pointer was switched to the new version and the launch health check passed.</summary>
    Installed = 3,

    /// <summary>The install or its launch verification failed and the pointer was restored to the
    /// previous version.</summary>
    RolledBack = 4,

    /// <summary>The updater explicitly gave up BEFORE touching the installation (e.g. the old app
    /// never exited, or verification refused the release). Safe to resume backups.</summary>
    Abandoned = 5,

    /// <summary>The operation failed before any install artifact could be created. The old
    /// installation is untouched, so backups may resume.</summary>
    FailedBeforeInstall = 6,
}

/// <summary>One durable record of an install operation.</summary>
public sealed record UpdateOperationState
{
    [JsonPropertyName("operation_id")] public string OperationId { get; init; } = "";
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; init; }
    [JsonPropertyName("updater_pid")] public int UpdaterPid { get; init; }
    [JsonPropertyName("updater_start_utc_ticks")] public long UpdaterStartUtcTicks { get; init; }
    [JsonPropertyName("updater_path")] public string? UpdaterPath { get; init; }
    [JsonPropertyName("status")] public InstallOperationStatus Status { get; init; }
    [JsonPropertyName("failure_reason")] public string FailureReason { get; init; } = "";
    [JsonPropertyName("previous_version")] public string PreviousVersion { get; init; } = "";
}

/// <summary>
/// Durable, atomic per-operation state store at <c>&lt;install root&gt;\.ops\&lt;operationId&gt;.json</c>.
/// Writes go temp-file + replace; reads treat a missing file as "no evidence" and a corrupt
/// file as Unknown (the bytes are preserved next to a .corrupt marker for diagnosis, never
/// silently dropped). After a crash or reboot the store answers: not started / in progress /
/// succeeded / failed / unknown — and unknown is never auto-resolved into "retry the install".
/// </summary>
public sealed class UpdateOperationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string DirectoryPath { get; }

    public UpdateOperationStateStore(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot)) throw new ArgumentException("安装根目录不能为空", nameof(installRoot));
        DirectoryPath = Path.Combine(Path.GetFullPath(installRoot), ".ops");
    }

    public string StatePath(string operationId) => Path.Combine(DirectoryPath, $"{operationId}.json");

    /// <summary>Creates or replaces the record for the operation, atomically.</summary>
    public void Save(UpdateOperationState state)
    {
        if (string.IsNullOrWhiteSpace(state.OperationId)) throw new ArgumentException("操作标识不能为空");
        Directory.CreateDirectory(DirectoryPath);
        string path = StatePath(state.OperationId);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(state with { UpdatedAt = DateTimeOffset.UtcNow }, JsonOptions));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>Transitions an existing record, creating it when absent (updater standalone runs).</summary>
    public void Transition(string operationId, InstallOperationStatus status, string failureReason,
        Func<UpdateOperationState, UpdateOperationState>? seed = null)
    {
        var current = Load(operationId) ?? (seed?.Invoke(new UpdateOperationState
        {
            OperationId = operationId,
            UpdaterPid = Environment.ProcessId,
            UpdaterStartUtcTicks = ProcessStartTicks(),
        }) ?? new UpdateOperationState
        {
            OperationId = operationId,
            UpdaterPid = Environment.ProcessId,
            UpdaterStartUtcTicks = ProcessStartTicks(),
        });
        Save(current with { Status = status, FailureReason = failureReason });
    }

    /// <summary>
    /// Reads the record, or null when no record exists. A corrupt record is preserved as
    /// <c>&lt;file&gt;.corrupt-&lt;timestamp&gt;</c> and reported as null-with-<paramref name="corrupt"/>
    /// so the caller treats the operation as Unknown (fail-closed), never as absent.
    /// </summary>
    public UpdateOperationState? Load(string operationId) => Load(operationId, out _);

    public UpdateOperationState? Load(string operationId, out bool corrupt)
    {
        corrupt = false;
        string path = StatePath(operationId);
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<UpdateOperationState>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            corrupt = true;
            try
            {
                string quarantine = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
                if (File.Exists(path) && !File.Exists(quarantine)) File.Copy(path, quarantine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    /// <summary>All recorded operations (corrupt files are skipped but reported).</summary>
    public IReadOnlyList<UpdateOperationState> LoadAll(out int corruptCount)
    {
        corruptCount = 0;
        var states = new List<UpdateOperationState>();
        if (!Directory.Exists(DirectoryPath)) return states;
        foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            string id = Path.GetFileNameWithoutExtension(file);
            var state = Load(id, out bool corrupt);
            if (corrupt) corruptCount++;
            if (state != null) states.Add(state);
        }
        return states;
    }

    private static long ProcessStartTicks()
    {
        try { return System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks; }
        catch { return 0; }
    }
}
