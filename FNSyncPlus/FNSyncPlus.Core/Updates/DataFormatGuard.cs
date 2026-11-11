using System.Text.Json;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Updates;

/// <summary>
/// Guards the boundary between program versions and the on-disk configuration/data format.
/// A newer app may upgrade the format marker; an older app that only understands an earlier
/// format must refuse to run instead of writing structures it does not understand.
/// </summary>
public sealed class DataFormatGuard
{
    private sealed class Record
    {
        [JsonPropertyName("format")] public int Format { get; set; }
        [JsonPropertyName("app_version")] public string AppVersion { get; set; } = "";
        [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    }

    private readonly string _path;
    private readonly int _supportedFormat;

    public DataFormatGuard(string markerPath, int supportedFormat)
    {
        if (string.IsNullOrWhiteSpace(markerPath)) throw new ArgumentException("格式标记路径不能为空", nameof(markerPath));
        _path = markerPath;
        _supportedFormat = supportedFormat;
    }

    public int? OnDiskFormat
    {
        get
        {
            try
            {
                if (!File.Exists(_path)) return null;
                return JsonSerializer.Deserialize<Record>(File.ReadAllText(_path))?.Format;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>
    /// Returns true when this build may proceed. When the marker is newer than this build it
    /// returns false with an explanation and leaves the marker untouched.
    /// </summary>
    public bool TryClaim(string appVersion, out string error)
    {
        error = "";
        int current = OnDiskFormat ?? 0;
        if (current > _supportedFormat)
        {
            error = $"本机配置由更高版本（数据格式 {current}）写入，当前程序只支持格式 {_supportedFormat}，" +
                    "已停止运行以避免写坏配置；请安装最新版本。";
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Record
            {
                Format = _supportedFormat, AppVersion = appVersion, UpdatedAt = DateTimeOffset.UtcNow
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"无法写入配置格式标记：{ex.Message}";
            return false;
        }
        return true;
    }
}
