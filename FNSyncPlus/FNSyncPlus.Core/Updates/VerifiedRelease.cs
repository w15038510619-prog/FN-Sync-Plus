using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Updates;

/// <summary>
/// An immutable release description that has already been verified against the pinned signing key.
///
/// The update flow used to pass loose values around (a version from one manifest fetch, a hash
/// recomputed from whatever file happened to be downloaded). That made the hash check circular and
/// let a second manifest fetch substitute a different package. Everything needed to install is now
/// bound to one signed payload: the manifest bytes that were actually signed, the version, the
/// size, the URL, and the local file that was verified against that size and hash.
/// </summary>
public sealed record VerifiedRelease(
    string ManifestJson,
    string Version,
    UpdateChannel Channel,
    string Sha256,
    long Size,
    string PackageUrl,
    string LocalPackagePath,
    string MachineGroup,
    int ReleaseBatch)
{
    public string ShortSha => Sha256.Length <= 12 ? Sha256 : Sha256[..12];

    /// <summary>
    /// Writes the descriptor the standalone updater re-verifies. Only fields that the updater can
    /// cross-check against the signature are persisted; nothing here is trusted on its own.
    /// </summary>
    public ReleaseDescriptor ToDescriptor() => new()
    {
        ManifestJson = ManifestJson,
        Version = Version,
        PackagePath = LocalPackagePath,
        PackageSha256 = Sha256,
        PackageSize = Size,
        MachineGroup = MachineGroup,
        ReleaseBatch = ReleaseBatch,
        RequestedChannelName = Channel == UpdateChannel.Beta ? "beta" : "stable"
    };
}

/// <summary>On-disk handoff between the app and the updater (written as <c>release.json</c>).</summary>
public sealed class ReleaseDescriptor
{
    [JsonPropertyName("manifest")] public string ManifestJson { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("package_path")] public string PackagePath { get; set; } = "";
    [JsonPropertyName("package_sha256")] public string PackageSha256 { get; set; } = "";
    [JsonPropertyName("package_size")] public long PackageSize { get; set; }
    [JsonPropertyName("machine_group")] public string MachineGroup { get; set; } = "";
    [JsonPropertyName("release_batch")] public int ReleaseBatch { get; set; }

    /// <summary>Channel the client was configured for. Not an authenticity claim — it only lets the
    /// updater re-apply the same channel rule the app already enforced.</summary>
    [JsonPropertyName("requested_channel")] public string RequestedChannelName { get; set; } = "stable";

    [JsonIgnore]
    public UpdateChannel RequestedChannel => string.Equals(RequestedChannelName, "beta", StringComparison.OrdinalIgnoreCase)
        ? UpdateChannel.Beta : UpdateChannel.Stable;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public void Save(string path)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temp = full + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
        // Replace last so a crash never leaves a truncated descriptor behind.
        if (File.Exists(full)) File.Replace(temp, full, null);
        else File.Move(temp, full);
    }

    public static ReleaseDescriptor? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var descriptor = JsonSerializer.Deserialize<ReleaseDescriptor>(File.ReadAllText(path));
            if (descriptor == null) return null;
            if (string.IsNullOrWhiteSpace(descriptor.ManifestJson) ||
                string.IsNullOrWhiteSpace(descriptor.Version) ||
                string.IsNullOrWhiteSpace(descriptor.PackagePath) ||
                descriptor.PackageSha256.Length != 64 ||
                descriptor.PackageSize <= 0) return null;
            return descriptor;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
