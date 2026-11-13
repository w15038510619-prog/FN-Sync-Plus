using System.Text.Json;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Services;

/// <summary>Persisted, UI-friendly form of an <see cref="AmbiguousRenameException"/>.</summary>
public static class AmbiguousRenameDetails
{
    public sealed class Payload
    {
        [JsonPropertyName("groups")] public List<GroupDto> Groups { get; set; } = new();
        [JsonPropertyName("removals")] public List<FileDto> Removals { get; set; } = new();
        [JsonPropertyName("additions")] public List<FileDto> Additions { get; set; } = new();
        [JsonPropertyName("previous_preserved")] public bool PreviousPreserved { get; set; }
        [JsonPropertyName("previous_manifest_id")] public string? PreviousManifestId { get; set; }
    }

    public sealed class GroupDto
    {
        [JsonPropertyName("old_path")] public string OldPath { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
        [JsonPropertyName("candidates")] public List<string> Candidates { get; set; } = new();
        [JsonPropertyName("reason")] public string Reason { get; set; } = "";
    }

    public sealed class FileDto
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Serialize(AmbiguousRenameException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var payload = new Payload
        {
            PreviousPreserved = exception.PreviousBackupPreserved,
            PreviousManifestId = exception.PreviousManifestId
        };
        foreach (var group in exception.AmbiguousGroups)
            payload.Groups.Add(new GroupDto
            {
                OldPath = group.OldPath, Size = group.Size, Sha256 = group.Sha256,
                Candidates = group.CandidatePaths.ToList(), Reason = group.Reason
            });
        foreach (var removal in exception.UnmatchedRemovalDetails)
            payload.Removals.Add(new FileDto { Path = removal.Path, Size = removal.Size, Sha256 = removal.Sha256 });
        foreach (var addition in exception.UnmatchedAdditionDetails)
            payload.Additions.Add(new FileDto { Path = addition.Path, Size = addition.Size, Sha256 = addition.Sha256 });
        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Returns null when the stored JSON is unreadable; never throws for UI display.</summary>
    public static Payload? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<Payload>(json, Options); }
        catch (JsonException) { return null; }
    }
}
