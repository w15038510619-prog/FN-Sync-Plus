using System.Text.Json;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Updates;

public sealed record LaunchHealth(bool Healthy, string Reason);

/// <summary>
/// Records launch attempts and "reached a working state" markers so a failed upgrade can be
/// detected instead of leaving the user with a program that never starts. Markers live under
/// <c>{root}/.health</c> and are keyed by version, so a still-running older client can never mark
/// a newer version healthy by accident.
/// </summary>
public sealed class LaunchHealthTracker
{
    private sealed class Marker
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    }

    public string Root { get; }
    private string HealthDirectory => Path.Combine(Root, ".health");

    public LaunchHealthTracker(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("安装根目录不能为空", nameof(root));
        Root = Path.GetFullPath(root);
    }

    private string AttemptPath(string version) => Path.Combine(HealthDirectory, $"{SafeName(version)}.attempt.json");
    private string HealthyPath(string version) => Path.Combine(HealthDirectory, $"{SafeName(version)}.healthy.json");

    public DateTimeOffset? RecordAttempt(string version, int pid)
    {
        try
        {
            Directory.CreateDirectory(HealthDirectory);
            var marker = new Marker { Version = version, Pid = pid, At = DateTimeOffset.UtcNow };
            File.WriteAllText(AttemptPath(version), JsonSerializer.Serialize(marker));
            return marker.At;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Called by the application once it is genuinely usable (window shown, settings loaded).</summary>
    public bool MarkHealthy(string version, int pid)
    {
        try
        {
            Directory.CreateDirectory(HealthDirectory);
            File.WriteAllText(HealthyPath(version), JsonSerializer.Serialize(new Marker
            {
                Version = version, Pid = pid, At = DateTimeOffset.UtcNow
            }));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool IsHealthy(string version) => ReadMarker(HealthyPath(version)) != null;

    /// <summary>
    /// Path of the startup report the application writes next to the marker. The report states what
    /// actually initialised (services, task list, data format) so the updater can reject a build that
    /// shows a window but cannot run backups.
    /// </summary>
    public string LaunchReportPath(string version) =>
        Path.Combine(HealthDirectory, $"report-{SafeName(version)}.json");

    /// <summary>Reads <c>usable</c> from the startup report. Null means no report was written.</summary>
    public bool? ReportedUsable(string version)
    {
        try
        {
            string path = LaunchReportPath(version);
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("usable", out var usable) &&
                (usable.ValueKind == JsonValueKind.True || usable.ValueKind == JsonValueKind.False))
                return usable.GetBoolean();
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decides whether the version launched by <see cref="RecordAttempt"/> became usable. A healthy
    /// marker older than the attempt does not count; the process must also still be alive or have
    /// written the marker before exiting. A startup report that says the build is not usable vetoes
    /// the marker, because launching successfully is not the same as being able to back up.
    /// </summary>
    public LaunchHealth Evaluate(string version, bool processStillRunning)
    {
        var attempt = ReadMarker(AttemptPath(version));
        if (attempt == null) return new LaunchHealth(false, "没有找到启动尝试记录，无法确认新版本启动成功");
        if (ReportedUsable(version) == false)
            return new LaunchHealth(false, "启动报告显示服务未就绪，未通过可用性检查");
        var healthy = ReadMarker(HealthyPath(version));
        if (healthy != null && healthy.At >= attempt.At) return new LaunchHealth(true, "新版本已进入可用状态");
        if (!processStillRunning) return new LaunchHealth(false, "新版本进程已退出且未报告可用状态");
        return new LaunchHealth(false, "新版本在限定时间内未报告可用状态");
    }

    public void Clear(string version)
    {
        TryDelete(AttemptPath(version));
        TryDelete(HealthyPath(version));
        // The report is per attempt too: a stale "usable" verdict must not decide the next launch.
        TryDelete(LaunchReportPath(version));
    }

    private static Marker? ReadMarker(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Marker>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string SafeName(string version) =>
        string.Concat(version.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_'));
}
