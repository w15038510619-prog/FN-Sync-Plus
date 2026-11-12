using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Updates;

/// <summary>What the on-disk format marker actually looks like.</summary>
public enum FormatMarkerState
{
    /// <summary>No marker at all: this really is a first run.</summary>
    Missing,
    /// <summary>Parsed and structurally valid.</summary>
    Valid,
    /// <summary>Exists but cannot be parsed (truncated, empty, wrong shape).</summary>
    Corrupt,
    /// <summary>Exists but cannot be read (permissions, sharing violation, I/O error).</summary>
    Unreadable,
    /// <summary>Parsed but carries a value this build cannot interpret.</summary>
    Unsupported
}

public sealed record FormatMarkerInspection(FormatMarkerState State, int Format, string Detail)
{
    /// <summary>Only a genuinely absent marker may be created from scratch.</summary>
    public bool AllowsInitialization => State == FormatMarkerState.Missing;

    /// <summary>True when this build must stop instead of writing.</summary>
    public bool Blocks(int supportedFormat) => State switch
    {
        FormatMarkerState.Missing => false,
        FormatMarkerState.Valid => Format > supportedFormat,
        _ => true
    };
}

/// <summary>
/// Guards the boundary between program versions and the on-disk configuration/data format.
///
/// The previous implementation returned <c>null</c> for both "no marker yet" and "the marker is
/// unreadable", then treated null as format 0. A permission error or a half-written file therefore
/// let an older build proceed and write structures it did not understand — a fail-open path.
///
/// Now every state is distinguished. Only <see cref="FormatMarkerState.Missing"/> may initialize a
/// marker; corrupt or unreadable markers stop the program and are preserved for diagnosis. Writes
/// are temp-file + replace, serialized through an exclusive lock file, and read back for
/// confirmation.
/// </summary>
public sealed class DataFormatGuard
{
    private const int LockRetries = 25;
    private const int LockRetryDelayMs = 80;

    private sealed class Record
    {
        [JsonPropertyName("format")] public int Format { get; set; }
        [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
        [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    }

    private readonly string _path;
    private readonly int _supportedFormat;
    private readonly Action<string>? _log;

    public DataFormatGuard(string markerPath, int supportedFormat, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(markerPath)) throw new ArgumentException("格式标记路径不能为空", nameof(markerPath));
        if (supportedFormat < 1) throw new ArgumentOutOfRangeException(nameof(supportedFormat), "支持的格式版本必须为正数");
        _path = markerPath;
        _supportedFormat = supportedFormat;
        _log = log;
    }

    public string MarkerPath => _path;

    /// <summary>Path of the preserved copy taken when a marker was found corrupt, if any.</summary>
    public string? LastPreservedCopy { get; private set; }

    public int SupportedFormat => _supportedFormat;

    /// <summary>Read-only inspection. Never writes, never repairs, never deletes.</summary>
    public FormatMarkerInspection Probe()
    {
        string? text;
        try
        {
            if (!File.Exists(_path)) return new FormatMarkerInspection(FormatMarkerState.Missing, 0, "尚未创建格式标记");
            if (new FileInfo(_path).Length == 0)
                return new FormatMarkerInspection(FormatMarkerState.Corrupt, 0, "格式标记文件为空（可能写入中断）");
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new FormatMarkerInspection(FormatMarkerState.Unreadable, 0, $"无法读取格式标记：{ex.Message}");
        }

        Record? record;
        try { record = JsonSerializer.Deserialize<Record>(text); }
        catch (JsonException ex)
        {
            return new FormatMarkerInspection(FormatMarkerState.Corrupt, 0, $"格式标记不是有效 JSON：{ex.Message}");
        }
        if (record == null)
            return new FormatMarkerInspection(FormatMarkerState.Corrupt, 0, "格式标记内容为空");
        if (record.Format < 1)
            return new FormatMarkerInspection(FormatMarkerState.Unsupported, record.Format,
                $"格式标记中的格式号 {record.Format} 无法识别");
        return new FormatMarkerInspection(FormatMarkerState.Valid, record.Format, $"数据格式 {record.Format}");
    }

    /// <summary>
    /// Gate for "may this build touch local data?". Returns false with an explanation whenever the
    /// on-disk state cannot be established with certainty.
    /// </summary>
    public bool TryClaim(string appVersion, out string error)
    {
        error = "";
        if (!TryAcquireLock(out FileStream? lockFile, out error)) return false;
        using (lockFile)
        {
            var probe = Probe();
            switch (probe.State)
            {
                case FormatMarkerState.Missing:
                    // Genuine first run: create the marker.
                    return TryWrite(appVersion, _supportedFormat, out error);

                case FormatMarkerState.Valid:
                    if (probe.Format > _supportedFormat)
                    {
                        error = $"本机配置由更高版本（数据格式 {probe.Format}）写入，当前程序只支持格式 {_supportedFormat}，" +
                                "已停止运行以避免写坏配置；请安装最新版本。";
                        _log?.Invoke(error);
                        return false;
                    }
                    // Keep the recorded format: this build may read older data, but declaring a newer
                    // format would block a legitimate rollback for no benefit. It is only raised by
                    // TryUpgrade when a real migration writes new structures.
                    return TryWrite(appVersion, probe.Format, out error);

                case FormatMarkerState.Corrupt:
                case FormatMarkerState.Unsupported:
                    PreserveForDiagnosis(probe.State);
                    error = $"配置格式标记已损坏（{probe.Detail}），为防止写坏数据已停止运行。" +
                            $"损坏文件已保留：{LastPreservedCopy ?? _path}。请人工确认后处理，程序不会自动删除。";
                    _log?.Invoke(error);
                    return false;

                default:
                    error = $"无法读取配置格式标记（{probe.Detail}），已停止运行以避免写坏数据。";
                    _log?.Invoke(error);
                    return false;
            }
        }
    }

    /// <summary>
    /// Raises the marker to a newer format. Only called by a real migration that has already written
    /// data in the new format; never used to paper over an unreadable marker.
    /// </summary>
    public bool TryUpgrade(string appVersion, int newFormat, out string error)
    {
        error = "";
        if (newFormat < 1 || newFormat > _supportedFormat)
        {
            error = $"不能把数据格式升级到 {newFormat}（当前程序最高支持 {_supportedFormat}）";
            return false;
        }
        if (!TryAcquireLock(out FileStream? lockFile, out error)) return false;
        using (lockFile)
        {
            var probe = Probe();
            if (probe.State is FormatMarkerState.Corrupt or FormatMarkerState.Unreadable or FormatMarkerState.Unsupported)
            {
                error = $"升级格式标记前无法确认当前状态（{probe.Detail}），已停止以避免覆盖诊断信息。";
                PreserveForDiagnosis(probe.State);
                return false;
            }
            if (probe.State == FormatMarkerState.Valid && probe.Format > newFormat)
            {
                error = $"当前格式 {probe.Format} 已高于目标格式 {newFormat}，拒绝降级标记。";
                return false;
            }
            return TryWrite(appVersion, newFormat, out error);
        }
    }

    private bool TryAcquireLock(out FileStream? handle, out string error)
    {
        error = "";
        string lockPath = _path + ".lock";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            handle = null;
            error = $"无法创建配置目录：{ex.Message}";
            return false;
        }

        for (int attempt = 0; attempt < LockRetries; attempt++)
        {
            try
            {
                handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                return true;
            }
            catch (IOException)
            {
                Thread.Sleep(LockRetryDelayMs);
            }
            catch (UnauthorizedAccessException ex)
            {
                handle = null;
                error = $"没有写入配置目录的权限：{ex.Message}";
                return false;
            }
        }
        handle = null;
        error = "另一个程序正在写入配置格式标记，为避免并发写入已停止本次操作。";
        return false;
    }

    private bool TryWrite(string appVersion, int format, out string error)
    {
        error = "";
        string temp = $"{_path}.tmp-{Guid.NewGuid():N}";
        try
        {
            string json = JsonSerializer.Serialize(new Record
            {
                Format = format, AppVersion = appVersion, UpdatedAt = DateTimeOffset.UtcNow
            });
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream);
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);

            // Confirm what actually landed: a silently truncated write must not be reported as ok.
            var probe = Probe();
            if (probe.State != FormatMarkerState.Valid || probe.Format != format)
            {
                error = $"写入格式标记后校验失败（{probe.Detail}），已停止运行。";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"无法写入配置格式标记：{ex.Message}";
            return false;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Copies a damaged marker aside. The original is never deleted or rewritten here.</summary>
    private void PreserveForDiagnosis(FormatMarkerState state)
    {
        if (LastPreservedCopy != null) return;
        if (state == FormatMarkerState.Unreadable) return; // cannot read it, so cannot copy it
        try
        {
            string copy = $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Copy(_path, copy, overwrite: false);
            LastPreservedCopy = copy;
            _log?.Invoke($"已保留损坏的格式标记副本：{copy}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastPreservedCopy = null;
        }
    }
}
