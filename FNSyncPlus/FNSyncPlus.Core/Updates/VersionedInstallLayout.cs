using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.Core.Updates;

public sealed record InstallOutcome(bool Ok, string VersionInstalled, string Error, bool RolledBack, string TargetDirectory);

/// <summary>
/// Versioned install layout:
///   {root}/app-{version}/        immutable app payload
///   {root}/current.json          points at the active version
///   {root}/previous.json         last known-good version for rollback
///   {root}/update.log            append-only update journal
/// The stable launcher (FNSyncPlus.Updater.exe in the install root) reads current.json.
/// Installation is: extract to a temp dir, verify the expected hash/marker file, swap current.json
/// atomically, then verify the new version's launch marker. Failure restores previous.json.
/// </summary>
public sealed class VersionedInstallLayout
{
    private sealed class Pointer
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("directory")] public string Directory { get; set; } = "";
        [JsonPropertyName("installed_at")] public DateTimeOffset InstalledAt { get; set; }
        [JsonPropertyName("payload_sha256")] public string PayloadSha256 { get; set; } = "";

        /// <summary>Data format the installed binaries can read. 0 means the payload did not
        /// declare it, which makes a rollback to it unsafe in a production install.</summary>
        [JsonPropertyName("supported_data_format")] public int SupportedDataFormat { get; set; }

        /// <summary>Install operation that produced this pointer, or empty for pre-existing
        /// installs. Ties the pointer to exactly one handoff for post-incident diagnosis.</summary>
        [JsonPropertyName("operation_id")] public string OperationId { get; set; } = "";
    }

    public string Root { get; }

    /// <summary>Local data-format marker. Injected by tests; defaults to the per-user location.</summary>
    public string DataFormatMarkerPath { get; }

    public VersionedInstallLayout(string root, string? dataFormatMarkerPath = null)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("安装根目录不能为空", nameof(root));
        Root = Path.GetFullPath(root);
        DataFormatMarkerPath = dataFormatMarkerPath ?? Path.Combine(
            AppStorageRoot.LocalAppDataDir(), "data-format.json");
    }

    /// <summary>
    /// A running app lives in <c>{root}/app-{version}</c>. Returns the install root when the layout
    /// around <paramref name="appDirectory"/> looks like a managed installation, otherwise null so
    /// an unpacked ZIP run never tries to take part in the update protocol.
    /// </summary>
    public static string? TryFindRootFromAppDirectory(string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDirectory)) return null;
        string full = Path.GetFullPath(appDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent)) return null;
        if (!Path.GetFileName(full).StartsWith("app-", StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(Path.Combine(parent, "current.json")) ? parent : null;
    }

    private string CurrentPath => Path.Combine(Root, "current.json");
    private string PreviousPath => Path.Combine(Root, "previous.json");
    public string JournalPath => Path.Combine(Root, "update.log");
    public string VersionDirectory(string version) => Path.Combine(Root, $"app-{version}");

    public string? ActiveVersion => ReadPointer(CurrentPath)?.Version;
    public string? PreviousVersion => ReadPointer(PreviousPath)?.Version;

    private Pointer? ReadPointer(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(path));
            if (pointer == null || string.IsNullOrWhiteSpace(pointer.Version)) return null;
            if (Directory.Exists(pointer.Directory)) return pointer;

            // The pointer records an absolute directory, which is only valid on the machine that
            // produced it. An installer, a ZIP extracted elsewhere or a changed drive letter all
            // leave a pointer that points at a path this machine does not have. Re-anchor it to
            // this root instead of reporting "no version": reporting null used to make the launcher
            // refuse to start anything even though app-<version> was right next to it.
            string reanchored = VersionDirectory(pointer.Version);
            if (!Directory.Exists(reanchored)) return null;
            string stale = pointer.Directory;
            pointer.Directory = reanchored;
            try
            {
                WritePointerAtomic(path, pointer);
                Journal($"{Path.GetFileName(path)} re-anchored from {stale} to {reanchored}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read-only install root: keep working, just do not persist the repair.
                Journal($"pointer re-anchor could not be saved: {ex.Message}");
            }
            return pointer;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Installs a verified payload directory into a new version directory. The payload directory is
    /// copied (not moved) so a failed marker check leaves the downloaded package intact.
    /// <paramref name="launchVerification"/> runs after the pointer swap and must confirm the new
    /// version starts; when it fails, the previous pointer is restored.
    /// </summary>
    public async Task<InstallOutcome> InstallAsync(string version, string payloadDirectory, Func<CancellationToken, Task<bool>> launchVerification,
        CancellationToken token = default, string? operationId = null)
    {
        if (!Version.TryParse(version, out _)) return new InstallOutcome(false, "", "版本号无效", false, "");
        if (!Directory.Exists(payloadDirectory)) return new InstallOutcome(false, "", "更新载荷目录不存在", false, "");
        string target = VersionDirectory(version);
        if (Directory.Exists(target)) return new InstallOutcome(false, "", "该版本目录已存在，拒绝覆盖", false, target);
        Directory.CreateDirectory(Root);
        string staging = Path.Combine(Root, $".staging-{Guid.NewGuid():N}");
        try
        {
            CopyDirectory(payloadDirectory, staging);
            token.ThrowIfCancellationRequested();
            string payloadHash = await HashDirectoryAsync(staging, token).ConfigureAwait(false);
            if (Directory.Exists(target)) throw new IOException("版本目录在准备期间出现，拒绝覆盖");
            Directory.Move(staging, target);
            var metadata = AppBuildMetadata.TryRead(staging) ?? AppBuildMetadata.TryRead(payloadDirectory);
            var pointer = new Pointer
            {
                Version = version, Directory = target, InstalledAt = DateTimeOffset.UtcNow, PayloadSha256 = payloadHash,
                SupportedDataFormat = metadata?.SupportedDataFormat ?? 0, OperationId = operationId ?? ""
            };
            var previous = ReadPointer(CurrentPath);
            WritePointerAtomic(PreviousPath, previous);
            WritePointerAtomic(CurrentPath, pointer);
            Journal($"installed {version} (payload {payloadHash[..12]}), previous={previous?.Version ?? "none"}");
            bool launched;
            try { launched = await launchVerification(token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Journal($"launch verification threw: {ex.GetType().Name}: {ex.Message}");
                launched = false;
            }
            if (launched)
                return new InstallOutcome(true, version, "", false, target);

            // Roll back the pointer only; keep the failed payload directory for diagnosis.
            Journal($"launch verification failed for {version}, rolling back pointer");
            RestorePointer();
            return new InstallOutcome(false, version, "新版本启动验证失败，已回滚到上一版本", true, target);
        }
        catch (OperationCanceledException)
        {
            if (Directory.Exists(staging)) TryDeleteDirectory(staging);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (Directory.Exists(staging)) TryDeleteDirectory(staging);
            return new InstallOutcome(false, "", $"安装失败：{ex.Message}", false, target);
        }
    }

    /// <summary>
    /// Restores the previous pointer after checking that the target version can read the data that
    /// is already on disk. Binaries roll back, data formats do not: if the on-disk format is newer
    /// than the previous build understands, or cannot be determined, the pointer stays put and the
    /// caller is told why.
    /// </summary>
    public bool RestorePointer() => TryRestorePointer(out _);

    public bool TryRestorePointer(out string reason)
    {
        var previous = ReadPointer(PreviousPath);
        if (previous == null)
        {
            reason = "没有可回滚的上一版本";
            return false;
        }
        if (!CanRollbackTo(previous, out reason)) return false;
        WritePointerAtomic(CurrentPath, previous);
        Journal($"rolled back to {previous.Version}");
        reason = $"已回滚到 {previous.Version}";
        return true;
    }

    /// <summary>
    /// True when <paramref name="target"/> may read the current on-disk data. A marker that is
    /// missing counts as format 0 (first run), which any version can handle.
    /// </summary>
    public bool CanRollbackToVersion(string version, out string reason)
    {
        string directory = VersionDirectory(version);
        int supported = AppBuildMetadata.TryRead(directory)?.SupportedDataFormat ?? 0;
        return CanRollbackTo(new Pointer { Version = version, Directory = directory, SupportedDataFormat = supported }, out reason);
    }

    private bool CanRollbackTo(Pointer previous, out string reason)
    {
        var marker = new DataFormatGuard(DataFormatMarkerPath, int.MaxValue).Probe();
        if (marker.State is FormatMarkerState.Corrupt or FormatMarkerState.Unreadable or FormatMarkerState.Unsupported)
        {
            reason = $"无法确认本机数据格式（{marker.Detail}），已拒绝回滚并保留当前版本";
            Journal($"rollback refused: marker {marker.State} ({marker.Detail})");
            return false;
        }
        if (marker.State == FormatMarkerState.Valid && previous.SupportedDataFormat < 1)
        {
            reason = $"上一版本 {previous.Version} 未声明可读取的数据格式，无法确认回滚安全，已保留当前版本";
            Journal($"rollback refused: {previous.Version} has no supported_data_format, on-disk format {marker.Format}");
            return false;
        }
        if (marker.State == FormatMarkerState.Valid && previous.SupportedDataFormat < marker.Format)
        {
            reason = $"本机数据格式为 {marker.Format}，上一版本 {previous.Version} 只能读到 {previous.SupportedDataFormat}，" +
                     "回滚可能写坏数据，已保留当前版本";
            Journal($"rollback refused: on-disk format {marker.Format} > {previous.Version} supported {previous.SupportedDataFormat}");
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// Checks whether an older app may safely read data written by the current version. Application
    /// binaries can roll back; data formats cannot. When the format marker is newer than the app
    /// understands, a rollback must be blocked rather than risking a downgrade write.
    /// </summary>
    public static bool CanDowngrade(int currentDataFormat, int appSupportedFormat) => appSupportedFormat >= currentDataFormat;

    public void Journal(string message)
    {
        try { File.AppendAllText(JournalPath, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void WritePointerAtomic(string path, Pointer? pointer)
    {
        if (pointer == null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(pointer));
        File.Move(temp, path, overwrite: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, target, overwrite: false);
        }
    }

    private static async Task<string> HashDirectoryAsync(string directory, CancellationToken token)
    {
        using var sha = SHA256.Create();
        var files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => (Rel: Path.GetRelativePath(directory, f).Replace('\\', '/'), Full: f))
            .OrderBy(f => f.Rel, StringComparer.Ordinal)
            .ToList();
        await using var composite = new MemoryStream();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            byte[] name = System.Text.Encoding.UTF8.GetBytes(file.Rel + "\0");
            await composite.WriteAsync(name, token).ConfigureAwait(false);
            await using (var stream = File.OpenRead(file.Full))
                await stream.CopyToAsync(composite, token).ConfigureAwait(false);
        }
        composite.Position = 0;
        return Convert.ToHexString(await sha.ComputeHashAsync(composite, token).ConfigureAwait(false));
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
