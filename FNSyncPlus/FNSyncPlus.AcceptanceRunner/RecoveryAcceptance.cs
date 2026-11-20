using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Acceptance;

public static partial class AcceptanceCli
{
    private sealed record Entry(string Path, bool Directory, long Bytes, string? Sha256);
    private sealed record Baseline(int Schema, Guid AcceptanceId, string ProviderType, string SourceKey,
        string SnapshotId, Entry[] Entries);
    private sealed record Backup(DiscoveredBackupPackage Package, Manifest Manifest, string SnapshotId);

    private static async Task RestoreAcceptanceAsync(Options options, bool before, string? scratch,
        List<Result> results, CancellationToken token)
    {
        string mode = options.Require("--mode");
        if (mode == "LocalFixture" && !before && options.Count == 1)
        {
            string source = Path.Combine(scratch!, "fixture");
            await CreateLocalBackupAsync(source, token);
            await using var provider = new SmbStorageProvider(rootUncPath: source);
            var backup = await ReadBackupAsync(provider, "", token);
            results.Add(await TestConflictAsync(provider, backup, scratch!, "LocalFixture", "LocalFileSystem", "local-fixture", token));
            return;
        }
        if (mode != "RealNasBackup")
            throw new InvalidInput("Use LocalFixture with no remote options, or RealNasBackup with an authorized source.");

        if (!Guid.TryParseExact(options.Require("--acceptance-id"), "D", out Guid id) || id == Guid.Empty)
            throw new InvalidInput("Supply a non-empty acceptance GUID in standard hyphenated form.");
        string baselinePath = LocalPath(options.Require("--baseline"));
        Baseline? expected = null;
        if (!before)
        {
            if (!File.Exists(baselinePath))
            {
                results.Add(new("RemoteSnapshot", "RealNAS", "BLOCKED", null, "A pre-restore SHA-256 baseline is required."));
                return;
            }
            EnsureNoReparse(baselinePath);
            expected = JsonSerializer.Deserialize<Baseline>(await File.ReadAllBytesAsync(baselinePath, token), Json);
            if (expected?.Schema != 1 || expected.AcceptanceId != id || !IsSha256(expected.SourceKey) ||
                string.IsNullOrEmpty(expected.SnapshotId) || expected.Entries == null)
                throw new InvalidInput("The baseline is invalid or belongs to a different acceptance run.");
            ValidateEntries(expected.Entries);
        }

        if (!options.Has("--authorize-nas") || !options.Has("--source-quiescent"))
            throw new Blocked("Explicit NAS authorization and confirmation that backup writers are paused are required.");

        string providerType = options.Require("--provider");
        string sourceInput = options.Require("--source");
        if (providerType == "SmbShare")
        {
            if (options.Has("--server") || options.Has("--cert-sha256"))
                throw new InvalidInput("SMB uses the current Windows share session and does not accept HTTPS options.");
            string unc = SmbSource(sourceInput);
            EnsureNoReparse(unc);
            if (!Directory.Exists(unc)) throw new Blocked("The authorized SMB backup directory is missing or inaccessible.");
            var uncRoot = Path.GetPathRoot(unc)!;
            string relativeBackup = Relative(unc[uncRoot.Length..].TrimStart('\\'));
            await using var provider = new SmbStorageProvider(rootUncPath: uncRoot);
            await RunRealRestoreAsync(provider, relativeBackup, unc, HashText(unc.ToUpperInvariant()), providerType,
                baselinePath, id, before, expected, scratch, results, token);
        }
        else if (providerType == "FnOsPersonal")
        {
            string path = SandboxPath(sourceInput);
            var server = Server(options);
            await using var session = await LoginAsync(server, options.Get("--cert-sha256"), "NAS", token);
            await RunRealRestoreAsync(session.Provider, path, null, HashText($"{server.AbsoluteUri}|{session.Root}|{path}"), providerType,
                baselinePath, id, before, expected, scratch, results, token);
        }
        else throw new InvalidInput("Use SmbShare or FnOsPersonal for a real NAS backup.");
    }

    private static async Task RunRealRestoreAsync(INasStorageProvider provider, string path, string? unc, string sourceKey,
        string providerType, string baselinePath, Guid id, bool before, Baseline? expected, string? scratch,
        List<Result> results, CancellationToken token)
    {
        string redactedSource = $"{providerType}/sandbox-{sourceKey[..12]}";
        if (expected != null && (expected.SourceKey != sourceKey || expected.ProviderType != providerType))
            throw new InvalidInput("The baseline belongs to a different NAS, account, or backup path.");

        if (await provider.GetItemAsync(path, token) is not { IsDirectory: true })
            throw new Blocked("The authorized NAS backup directory was not found.");
        // A full SMB scan checks for reparse points before Core opens a manifest or object.
        // fnOS uses authenticated personal-space API paths, not Windows filesystem paths.
        if (unc != null) _ = EnumerateSmb(unc, token);
        await EnsureBackupIdleAsync(provider, path, token);
        var backup = await ReadBackupAsync(provider, path, token);
        var evidence = new { SourceType = "RealNasBackup", SourcePath = redactedSource, ProviderType = providerType, backup.SnapshotId };
        results.Add(new("BackupSource", "RealNAS", "PASS", evidence, "The NAS manifest was read and its snapshot identified."));

        if (before)
        {
            var entries = await ScanAsync(provider, path, unc, token);
            await EnsureSameSnapshotAsync(provider, path, backup, token);
            var baseline = new Baseline(1, id, providerType, sourceKey, backup.SnapshotId, entries);
            await WriteNewJsonAsync(baselinePath, baseline, token);
            results.Add(new("RemoteBaseline", "RealNAS", "PASS", new { id, backup.SnapshotId, Entries = entries.Length }, "Pre-restore SHA-256 baseline saved."));
            return;
        }

        var immediatelyBefore = await ScanAsync(provider, path, unc, token);
        var preflight = Compare(expected!, backup.SnapshotId, immediatelyBefore, "BeforeRestore");
        if (preflight.Status != "PASS")
        {
            results.Add(preflight);
            results.Add(new("RestoreConflict", "RealNAS", "BLOCKED", evidence, "Source changed since baseline capture. Pause writers and start a fresh acceptance run."));
            return;
        }

        await EnsureSameSnapshotAsync(provider, path, backup, token);
        try
        {
            results.Add(await TestConflictAsync(provider, backup, scratch!, "RealNasBackup", providerType, redactedSource, token));
        }
        catch (Exception error)
        {
            results.Add(Error("RestoreConflict", "RealNAS", error));
        }

        // Even a failed restore must leave the remote source intact.
        try
        {
            var after = await ScanAsync(provider, path, unc, token);
            await EnsureBackupIdleAsync(provider, path, token);
            var current = await ReadBackupAsync(provider, path, token);
            results.Add(Compare(expected!, current.SnapshotId, after, "AfterRestore"));
        }
        catch (Exception error)
        {
            results.Add(Error("RemoteSnapshot", "RealNAS", error));
        }
    }

    private static async Task<Backup> ReadBackupAsync(INasStorageProvider provider, string path, CancellationToken token)
    {
        var engine = new RestoreEngine(new ManifestManager());
        var package = await engine.DiscoverProviderPackageAsync(provider, path, "Acceptance", token)
            ?? throw new Blocked("No supported backup manifest exists at the supplied source.");
        if (package.HasDamagedVersionRecords) throw new InvalidDataException();
        Manifest manifest;
        if (package.VersionId != null)
            manifest = await engine.ReadPackageManifestAsync(provider, package, token);
        else
        {
            string primary = Join(path, ".fnsync/manifest.json");
            if (await provider.GetItemAsync(primary, token) is not { IsDirectory: false })
                throw new Blocked("The primary legacy manifest is missing.");
            await using var stream = await provider.OpenReadAsync(primary, token);
            using var bytes = new MemoryStream();
            await stream.CopyToAsync(bytes, token);
            manifest = ManifestManager.ParseManifest(bytes.ToArray());
        }
        if (manifest.IsNew || manifest.Version < 1 || manifest.Version > Manifest.CurrentVersion || manifest.Files == null || manifest.Directories == null)
            throw new InvalidDataException();
        foreach (var file in manifest.Files)
        {
            if (file == null || !IsSha256(file.Sha256) || file.Size < 0)
                throw new InvalidDataException();
            _ = Relative(file.OriginalRel);
            _ = Relative(file.StoredRel);
        }
        if (manifest.Files.Select(file => Relative(file.OriginalRel)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Files.Count)
            throw new InvalidDataException();
        string manifestHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest)));
        return new(package, manifest, package.VersionId == null ? $"legacy:{manifestHash}" : $"versioned:{package.VersionId}:{manifestHash}");
    }

    private static async Task EnsureSameSnapshotAsync(INasStorageProvider provider, string path, Backup expected, CancellationToken token)
    {
        await EnsureBackupIdleAsync(provider, path, token);
        if ((await ReadBackupAsync(provider, path, token)).SnapshotId != expected.SnapshotId)
            throw new Blocked("The backup snapshot changed during scanning. Pause writers and repeat.");
    }

    private static async Task EnsureBackupIdleAsync(INasStorageProvider provider, string path, CancellationToken token)
    {
        if (await ExperimentalRemoteLease.InspectAsync(provider, path, token) != null)
            throw new Blocked("A backup writer lease is held. Stop or safely recover the writer before continuing.");
    }

    private static async Task<Result> TestConflictAsync(INasStorageProvider provider, Backup backup, string scratch,
        string mode, string providerType, string source, CancellationToken token)
    {
        string level = mode == "RealNasBackup" ? "RealNAS" : "Local";
        if (backup.Manifest.Files.Count < 2)
            return new("RestoreConflict", level, "BLOCKED", null, "Use at least two backed-up files to verify both a skipped conflict and a successful restore.");

        var engine = new RestoreEngine(new ManifestManager());
        string target = Path.Combine(scratch, $"restore_{Guid.NewGuid():N}");
        var first = backup.Manifest.Files.OrderBy(entry => entry.OriginalRel, StringComparer.Ordinal).First();
        string sentinel = Under(target, first.OriginalRel);
        Directory.CreateDirectory(PathHelper.EnsureLongPathPrefix(Path.GetDirectoryName(sentinel)!));
        byte[] marker = Guid.NewGuid().ToByteArray();
        await File.WriteAllBytesAsync(PathHelper.EnsureLongPathPrefix(sentinel), marker, token);

        var report = await engine.RunRestorePackageAsync(provider, backup.Package, target, ConflictResolution.SkipExisting, token);
        bool unchanged = (await File.ReadAllBytesAsync(PathHelper.EnsureLongPathPrefix(sentinel), token)).SequenceEqual(marker);
        bool exactSkip = report.FileResults.Count(file => file.Skipped) == 1 &&
            report.FileResults.Any(file => file.Skipped && file.OriginalRel == first.OriginalRel);
        bool restoredExpectedSnapshot = true;
        foreach (var file in backup.Manifest.Files.Where(file => file != first))
        {
            string local = PathHelper.EnsureLongPathPrefix(Under(target, file.OriginalRel));
            if (!File.Exists(local))
            {
                restoredExpectedSnapshot = false;
                continue;
            }
            await using var stream = File.OpenRead(local);
            restoredExpectedSnapshot &= stream.Length == file.Size &&
                string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), file.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        bool success = unchanged && exactSkip && restoredExpectedSnapshot && report.SkippedFiles == 1 && report.FailedFiles == 0 &&
            report.RestoredFiles == backup.Manifest.Files.Count - 1 && report.IntegrityVerifiedFiles == report.RestoredFiles;
        return new("RestoreConflict", level, success ? "PASS" : "FAIL", new
        {
            SourceType = mode, SourcePath = source, ProviderType = providerType, backup.SnapshotId,
            RestoreTarget = $"isolated-temp/{Path.GetFileName(target)}", ConflictResolution = "SkipExisting",
            report.RestoredFiles, report.SkippedFiles, report.FailedFiles, report.IntegrityVerifiedFiles,
            ExistingFileUnchanged = unchanged, ExpectedFileSkipped = exactSkip, RestoredExpectedSnapshot = restoredExpectedSnapshot
        }, success ? "The conflict was skipped and all other files passed SHA-256 verification." : "Restore counts, SHA-256 verification, or the conflict result did not match.");
    }

    private static async Task<Entry[]> ScanAsync(INasStorageProvider provider, string root, string? unc, CancellationToken token)
    {
        if (unc != null)
        {
            var entries = EnumerateSmb(unc, token);
            for (int index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry.Directory) continue;
                string full = Under(unc, entry.Path);
                EnsureNoReparse(full);
                await using var stream = File.OpenRead(PathHelper.EnsureLongPathPrefix(full));
                if (stream.Length != entry.Bytes) throw new Blocked("A file changed while scanning the SMB backup.");
                entries[index] = entry with { Sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)) };
            }
            return Sort(entries);
        }

        var found = new List<Entry>();
        var pending = new Stack<string>();
        pending.Push("");
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.TryPop(out var relative))
        {
            token.ThrowIfCancellationRequested();
            string directory = Join(root, relative);
            foreach (var item in await provider.ListEntriesAsync(directory, token))
            {
                string name = Relative(item.Name);
                if (name.Contains('/')) throw new InvalidDataException();
                string child = Join(relative, name);
                if (!visited.Add(child)) throw new InvalidDataException();
                if (item.IsDirectory)
                {
                    found.Add(new(child, true, 0, null));
                    pending.Push(child);
                    continue;
                }

                await using var stream = await provider.OpenReadAsync(Join(root, child), token);
                var (size, hash) = await ReadHashAsync(stream, token);
                if (size != item.Size) throw new Blocked("A file changed while scanning the fnOS backup.");
                found.Add(new(child, false, size, hash));
            }
        }
        return Sort(found);
    }

    private static List<Entry> EnumerateSmb(string root, CancellationToken token)
    {
        var entries = new List<Entry>();
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out string? directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (string full in Directory.EnumerateFileSystemEntries(PathHelper.EnsureLongPathPrefix(directory)))
            {
                var attributes = File.GetAttributes(full);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidInput("The SMB backup contains a reparse point.");
                string ordinary = full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + full[8..] : full;
                string relative = Relative(Path.GetRelativePath(root, ordinary));
                bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                entries.Add(new(relative, isDirectory, isDirectory ? 0 : new FileInfo(full).Length, null));
                if (isDirectory) directories.Push(ordinary);
            }
        }
        return entries;
    }

    private static async Task<(long Size, string Hash)> ReadHashAsync(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            size += read;
        }
        return (size, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static Result Compare(Baseline before, string snapshotId, Entry[] after, string phase)
    {
        var left = before.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var right = after.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        // Paths are hashed in the report. The local baseline retains the paths for the authorized tester.
        var removed = left.Keys.Except(right.Keys, StringComparer.Ordinal).Select(HashText).ToArray();
        var added = right.Keys.Except(left.Keys, StringComparer.Ordinal).Select(HashText).ToArray();
        var changed = left.Keys.Intersect(right.Keys, StringComparer.Ordinal).Where(path => left[path] != right[path]).Select(HashText).ToArray();
        bool unchanged = before.SnapshotId == snapshotId && added.Length + removed.Length + changed.Length == 0;
        return new("RemoteSnapshot", "RealNAS", unchanged ? "PASS" : "FAIL",
            new { before.AcceptanceId, Phase = phase, BeforeSnapshotId = before.SnapshotId, AfterSnapshotId = snapshotId,
                BeforeEntries = left.Count, AfterEntries = right.Count, AddedPathSha256 = added, RemovedPathSha256 = removed, ChangedPathSha256 = changed },
            unchanged ? "The source snapshot and all file hashes and paths match." :
                "Source content or paths changed. This comparison cannot attribute the change to the restore; check for concurrent writers.");
    }

    private static Entry[] Sort(IEnumerable<Entry> entries) => entries.OrderBy(entry => entry.Path, StringComparer.Ordinal).ToArray();

    private static void ValidateEntries(Entry[] entries)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry == null || Relative(entry.Path) != entry.Path || !paths.Add(entry.Path) || entry.Bytes < 0 ||
                (entry.Directory ? entry.Bytes != 0 || entry.Sha256 != null : !IsSha256(entry.Sha256)))
                throw new InvalidDataException();
        }
    }

    private static async Task WriteNewJsonAsync<T>(string path, T evidence, CancellationToken token)
    {
        EnsureNoReparse(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(file, evidence, Json, token);
    }

    private static string Relative(string value)
    {
        if (!PathHelper.IsSafeRelativePath(value)) throw new InvalidInput("The path contains an unsafe segment.");
        string normalized = value.Replace('\\', '/');
        foreach (string part in normalized.Split('/'))
        {
            if (part.Length == 0 || part.Trim() != part || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                !PathHelper.IsValidDirectoryName(part, out _) && Encoding.UTF8.GetByteCount(part) <= 255)
                throw new InvalidInput("The path contains an unsafe segment.");
        }
        return normalized;
    }

    private static string SandboxPath(string path)
    {
        string relative = Relative(path.StartsWith("我的文件/", StringComparison.Ordinal) ? path["我的文件/".Length..] : path);
        if (!IsSandbox(relative.Split('/')[0]))
            throw new InvalidInput("Use a pre-authorized sandbox directory named Codex_FN_Test_<unique suffix>.");
        return relative;
    }

    private static bool IsSandbox(string segment) =>
        System.Text.RegularExpressions.Regex.IsMatch(segment, @"\ACodex_FN_Test_[A-Za-z0-9_-]+\z");

    private static string SmbSource(string value)
    {
        if (!value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith(@"\\?\") || value.StartsWith(@"\\.\"))
            throw new InvalidInput("Real SMB mode requires a standard UNC path, not a local directory or mapped drive.");
        string rel = Relative(value[2..]);
        string[] parts = rel.Split('/');
        if (parts.Length < 3 || !IsSandbox(parts[2]))
            throw new InvalidInput("Use a UNC share containing a pre-authorized Codex_FN_Test_ sandbox.");
        return @"\\" + rel.Replace('/', '\\');
    }

    private static string LocalPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || full.Length < 3 || !char.IsAsciiLetter(full[0]) || full[1] != ':' ||
            new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed)
            throw new InvalidInput("Use a fixed local drive for output and fixture files.");
        AppStorageRoot.AssertNotProductionPath(full, "Acceptance evidence");
        EnsureNoReparse(full);
        return full;
    }

    private static void EnsureNoReparse(string path)
    {
        for (string? current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(PathHelper.EnsureLongPathPrefix(current)).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidInput("Reparse points are unsupported for acceptance paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string Join(string parent, string child) =>
        parent.Length == 0 ? child : child.Length == 0 ? parent : $"{parent}/{child}";
    private static string Under(string root, string relative) => Path.Combine(root, Relative(relative).Replace('/', Path.DirectorySeparatorChar));
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task CreateLocalBackupAsync(string folder, CancellationToken token)
    {
        Directory.CreateDirectory(folder);
        var manifest = new Manifest { UpdatedAt = DateTimeOffset.UtcNow };
        foreach (var (name, content) in new[] { ("conflict.txt", "Fixture conflict source"), ("恢复文件.txt", "Fixture second file") })
        {
            byte[] data = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(Under(folder, name), data, token);
            manifest.Files.Add(new()
            {
                OriginalRel = name, StoredRel = name, Size = data.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(data)), ModTimeUtcTicks = DateTime.UtcNow.Ticks
            });
        }
        await new ManifestManager().SaveManifestAtomicAsync(folder, manifest, token);
    }
}
