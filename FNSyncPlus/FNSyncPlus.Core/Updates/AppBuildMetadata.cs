using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Updates;

/// <summary>
/// Describes what a payload can read, written next to the binaries at build time (and refreshed by
/// the app on a healthy start). Rollback needs this per version: binaries roll back, data formats
/// do not, so the updater has to know whether the target version understands the data that is
/// already on disk before it moves the pointer.
/// </summary>
public sealed record AppBuildMetadata(string Version, int SupportedDataFormat)
{
    public const string FileName = "build-meta.json";

    private sealed class Dto
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("supported_data_format")] public int SupportedDataFormat { get; set; }
    }

    public static AppBuildMetadata? TryRead(string directory)
    {
        try
        {
            string path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
            if (dto == null || dto.SupportedDataFormat < 1) return null;
            return new AppBuildMetadata(dto.Version, dto.SupportedDataFormat);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Dto { Version = Version, SupportedDataFormat = SupportedDataFormat }));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
}
