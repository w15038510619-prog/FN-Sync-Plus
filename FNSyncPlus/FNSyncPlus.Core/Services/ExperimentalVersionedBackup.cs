using System.Security.Cryptography;
using System.Text.Json;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Isolated incremental backup experiment for migrated versioned backups. It is not connected to
/// tasks or the UI. Mirror deletion and the physical active directory are not implemented.
/// </summary>
public sealed class ExperimentalVersionedBackup
{
    public sealed record Result(VersionedManifestStore.Snapshot Snapshot, int UploadedFiles, int UnchangedFiles, int RenamedFiles, int DeletedFiles);
    private sealed record SourceFile(string RelativePath, string FullPath, long Size, long ModifiedTicks, string Sha256);
    private sealed record SourceTree(List<string> Directories, List<(string RelativePath, string FullPath)> Files);
    private readonly VersionedManifestStore _versions = new();
    private readonly ImmutableContentStore _objects = new();

    public Task<Result> RunAsync(INasStorageProvider provider, string sourceDirectory, string target,
        CancellationToken token = default, BackupStrategy strategy = BackupStrategy.Smart)
        => RunCoreAsync(provider, sourceDirectory, target, false, token, strategy);

    /// <summary>Creates a brand-new destination and commits its first complete version; never adopts an existing directory.</summary>
    public async Task<Result> CreateNewAsync(INasStorageProvider provider, string sourceDirectory, string target,
        CancellationToken token = default, BackupStrategy strategy = BackupStrategy.Smart)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!PathHelper.IsSafeRelativePath(target)) throw new ArgumentException("备份目标必须是安全的相对路径", nameof(target));
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException("备份源目录不存在");
        if (strategy is not (BackupStrategy.Smart or BackupStrategy.Archive))
            throw new NotSupportedException("实验性同步尚不支持镜像删除");
        if (provider is not IExclusiveDirectoryProvider exclusive)
            throw new NotSupportedException("存储不支持独占创建新的备份目录");
        var preflight = Scan(PathHelper.EnsureLongPathPrefix(sourceDirectory), token);
        foreach (var file in preflight.Files)
            await HashSourceAsync(file.RelativePath, file.FullPath, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (await exclusive.TryCreateDirectoryExclusiveAsync(target, token).ConfigureAwait(false) != ExclusiveDirectoryResult.Created)
            throw new IOException("备份目标已存在，拒绝复用或覆盖");
        return await RunCoreAsync(provider, sourceDirectory, target, true, token, strategy).ConfigureAwait(false);
    }

    private async Task<Result> RunCoreAsync(INasStorageProvider provider, string sourceDirectory, string target,
        bool creating, CancellationToken token, BackupStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!PathHelper.IsSafeRelativePath(target)) throw new ArgumentException("备份目标必须是安全的相对路径", nameof(target));
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException("备份源目录不存在");
        if (strategy is not (BackupStrategy.Smart or BackupStrategy.Archive))
            throw new NotSupportedException("实验性同步尚不支持镜像删除");
        string sourceRoot = PathHelper.EnsureLongPathPrefix(sourceDirectory);
        var initial = await _versions.LoadForWritingAsync(provider, target, token).ConfigureAwait(false);
        if (creating && initial != null) throw new InvalidDataException("新目标已出现版本化清单，拒绝覆盖");
        if (!creating && initial == null) throw new InvalidDataException("目标没有版本化清单，必须先进行安全迁移");
        var lease = await ExperimentalRemoteLease.TryAcquireAsync(provider, target, token).ConfigureAwait(false)
            ?? throw new IOException("目标被其他写入者占用或存在未释放的锁");
        bool commitMayHaveStarted = false;
        bool committed = false;
        Exception? primaryFailure = null;
        try
        {
            var oldSnapshot = await _versions.LoadForWritingAsync(provider, target, token).ConfigureAwait(false);
            if (!string.Equals(initial?.Id, oldSnapshot?.Id, StringComparison.Ordinal))
                throw new IOException("获取锁期间备份版本发生变化，请重新扫描");
            var manifest = oldSnapshot == null ? new Manifest() :
                JsonSerializer.Deserialize<Manifest>(JsonSerializer.SerializeToUtf8Bytes(oldSnapshot.Manifest))
                    ?? throw new InvalidDataException("无法复制版本化清单");
            if (manifest.Files == null || manifest.Directories == null || manifest.History == null)
                throw new InvalidDataException("版本化清单字段不完整");
            if (!string.IsNullOrWhiteSpace(manifest.SourceRoot) &&
                !string.Equals(PathHelper.NormalizeLocalPath(manifest.SourceRoot), PathHelper.NormalizeLocalPath(sourceDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("当前源目录与备份记录不一致，拒绝同步");

            var previous = new Dictionary<string, ManifestFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in manifest.Files)
            {
                if (item == null || !PathHelper.IsSafeRelativePath(item.OriginalRel) || !IsObject(item.StoredRel) ||
                    item.Size < 0 || !IsHash(item.Sha256) || !previous.TryAdd(PathHelper.NormalizeRelPath(item.OriginalRel), item))
                    throw new InvalidDataException("活动清单包含不安全、重复或未迁移的文件");
            }
            foreach (var history in manifest.History)
                if (history == null || !PathHelper.IsSafeRelativePath(history.OriginalRel) || !IsObject(history.StoredRel) ||
                    history.Size < 0 || !IsHash(history.Sha256))
                    throw new InvalidDataException("历史清单包含不安全或未迁移的文件");
            var previousDirectories = new Dictionary<string, ManifestDirEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in manifest.Directories)
                if (directory == null || !PathHelper.IsSafeRelativePath(directory.OriginalRel) || !PathHelper.IsSafeRelativePath(directory.StoredRel) ||
                    !previousDirectories.TryAdd(PathHelper.NormalizeRelPath(directory.OriginalRel), directory))
                    throw new InvalidDataException("目录清单包含不安全或重复的路径");

            var verifiedObjects = new Dictionary<string, (long Size, string Hash)>(StringComparer.OrdinalIgnoreCase);
            async Task VerifyPreviousObjectAsync(string storedRel, long size, string hash)
            {
                if (verifiedObjects.TryGetValue(storedRel, out var verified))
                {
                    if (verified.Size != size || !string.Equals(verified.Hash, hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("同一备份对象存在冲突的校验信息");
                    return;
                }
                string path = $"{PathHelper.NormalizeRelPath(target)}/{storedRel}";
                var item = await provider.GetItemAsync(path, token).ConfigureAwait(false);
                if (item == null || item.IsDirectory || item.Size != size)
                    throw new InvalidDataException($"已有备份对象不存在或大小不符: {storedRel}");
                bool valid = await provider.VerifyFileIntegrityAsync(path, hash, size, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!valid) throw new InvalidDataException($"已有备份对象校验失败: {storedRel}");
                verifiedObjects[storedRel] = (size, hash);
            }
            foreach (var old in previous.Values) await VerifyPreviousObjectAsync(old.StoredRel, old.Size, old.Sha256).ConfigureAwait(false);
            foreach (var history in manifest.History) await VerifyPreviousObjectAsync(history.StoredRel, history.Size, history.Sha256).ConfigureAwait(false);

            SourceTree tree = Scan(sourceRoot, token);
            var currentNames = new HashSet<string>(tree.Files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase);
            var baselines = new Dictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in tree.Files)
                baselines[source.RelativePath] = await HashSourceAsync(source.RelativePath, source.FullPath, token).ConfigureAwait(false);

            var missing = previous.Values.Where(old => !currentNames.Contains(old.OriginalRel)).ToList();
            var additions = baselines.Values.Where(source => !previous.ContainsKey(source.RelativePath)).ToList();
            var renames = new Dictionary<string, ManifestFileEntry>(StringComparer.OrdinalIgnoreCase);
            var matchedOld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in missing.GroupBy(old => (old.Size, Hash: old.Sha256.ToUpperInvariant())))
            {
                var matches = additions.Where(source => source.Size == group.Key.Size &&
                    string.Equals(source.Sha256, group.Key.Hash, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count == 0) continue;
                int oldContentCount = previous.Values.Count(old => old.Size == group.Key.Size &&
                    string.Equals(old.Sha256, group.Key.Hash, StringComparison.OrdinalIgnoreCase));
                int currentContentCount = baselines.Values.Count(source => source.Size == group.Key.Size &&
                    string.Equals(source.Sha256, group.Key.Hash, StringComparison.OrdinalIgnoreCase));
                if (matches.Count != 1 || group.Count() != 1 || oldContentCount != 1 || currentContentCount != 1)
                    throw new InvalidDataException("多个同内容文件无法唯一确认重命名，原备份未改变");
                var old = group.Single();
                renames.Add(matches[0].RelativePath, old);
                matchedOld.Add(old.OriginalRel);
            }
            var deleted = missing.Where(old => !matchedOld.Contains(old.OriginalRel)).ToList();
            if (deleted.Count > 0 && additions.Any(source => !renames.ContainsKey(source.RelativePath)))
                throw new InvalidDataException("新增和消失的文件无法确认是否为重命名，原备份未改变");

            var preparedFiles = new List<SourceFile>();
            var nextFiles = new List<ManifestFileEntry>();
            int uploaded = 0, unchanged = 0, renamed = 0;
            void AddHistory(ManifestFileEntry old, string reason, string? renamedTo = null)
            {
                manifest.History.Add(new ManifestHistoryEntry {
                    OriginalRel = old.OriginalRel, StoredRel = old.StoredRel, Size = old.Size, Sha256 = old.Sha256,
                    Reason = reason, RenamedTo = renamedTo, IsLongName = old.IsLongName, Utf8Bytes = old.Utf8Bytes,
                    StoredUtf8Bytes = old.StoredUtf8Bytes
                });
            }
            foreach (var missingEntry in missing)
            {
                if (strategy == BackupStrategy.Archive) nextFiles.Add(missingEntry);
                else if (matchedOld.Contains(missingEntry.OriginalRel))
                    AddHistory(missingEntry, "renamed", renames.Single(kv => ReferenceEquals(kv.Value, missingEntry)).Key);
                else AddHistory(missingEntry, "deleted");
            }
            foreach (var source in tree.Files)
            {
                token.ThrowIfCancellationRequested();
                var baseline = baselines[source.RelativePath];
                previous.TryGetValue(source.RelativePath, out var old);
                if (renames.TryGetValue(source.RelativePath, out var renamedFrom))
                {
                    nextFiles.Add(new ManifestFileEntry {
                        OriginalRel = source.RelativePath, StoredRel = renamedFrom.StoredRel, Size = baseline.Size,
                        Sha256 = baseline.Sha256, ModTimeUtcTicks = baseline.ModifiedTicks,
                        ModTimeIso = new DateTimeOffset(baseline.ModifiedTicks, TimeSpan.Zero), IsActive = true,
                        PreviousRel = renamedFrom.OriginalRel,
                        IsLongName = PathHelper.GetUtf8ByteCount(Path.GetFileName(source.RelativePath)) > LongNameEngine.FnOsUploadMaxUtf8Bytes,
                        Utf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(source.RelativePath)),
                        StoredUtf8Bytes = renamedFrom.StoredUtf8Bytes
                    });
                    preparedFiles.Add(baseline);
                    renamed++;
                    continue;
                }
                bool caseOnlyRename = old != null && !string.Equals(old.OriginalRel, source.RelativePath, StringComparison.Ordinal);
                if (caseOnlyRename && strategy == BackupStrategy.Archive)
                    throw new InvalidDataException("归档策略不能同时表示仅大小写不同的文件名");
                if (old != null && old.Size == baseline.Size && string.Equals(old.Sha256, baseline.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (caseOnlyRename)
                    {
                        string oldName = old.OriginalRel;
                        AddHistory(old, "renamed", source.RelativePath);
                        old.OriginalRel = source.RelativePath;
                        old.PreviousRel = oldName;
                        old.IsLongName = PathHelper.GetUtf8ByteCount(Path.GetFileName(source.RelativePath)) > LongNameEngine.FnOsUploadMaxUtf8Bytes;
                        old.Utf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(source.RelativePath));
                        renamed++;
                    }
                    else unchanged++;
                    old.ModTimeUtcTicks = baseline.ModifiedTicks;
                    old.ModTimeIso = new DateTimeOffset(baseline.ModifiedTicks, TimeSpan.Zero);
                    nextFiles.Add(old);
                    preparedFiles.Add(baseline);
                    continue;
                }

                var content = await _objects.PrepareAsync(provider, target, source.FullPath, token).ConfigureAwait(false);
                if (content.Size != baseline.Size || !string.Equals(content.Sha256, baseline.Sha256, StringComparison.OrdinalIgnoreCase) ||
                    content.ModifiedUtc.Ticks != baseline.ModifiedTicks)
                    throw new IOException($"文件在扫描和上传之间发生变化: {source.RelativePath}");
                if (old != null)
                {
                    AddHistory(old, caseOnlyRename ? "renamed" : "superseded", caseOnlyRename ? source.RelativePath : null);
                    if (caseOnlyRename) renamed++;
                }
                nextFiles.Add(new ManifestFileEntry {
                    OriginalRel = source.RelativePath, StoredRel = content.StoredRel, Size = content.Size,
                    Sha256 = content.Sha256, ModTimeUtcTicks = content.ModifiedUtc.Ticks,
                    ModTimeIso = new DateTimeOffset(content.ModifiedUtc), IsActive = true,
                    IsLongName = PathHelper.GetUtf8ByteCount(Path.GetFileName(source.RelativePath)) > LongNameEngine.FnOsUploadMaxUtf8Bytes,
                    Utf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(source.RelativePath)),
                    StoredUtf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(content.StoredRel)),
                    PreviousRel = caseOnlyRename ? old?.OriginalRel : null
                });
                preparedFiles.Add(baseline);
                uploaded++;
            }

            var finalTree = Scan(sourceRoot, token);
            if (!tree.Directories.SequenceEqual(finalTree.Directories, StringComparer.OrdinalIgnoreCase) ||
                !tree.Files.Select(f => f.RelativePath).SequenceEqual(finalTree.Files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase))
                throw new IOException("备份期间源目录结构发生变化，未提交新清单");
            foreach (var expected in preparedFiles)
            {
                var actual = await HashSourceAsync(expected.RelativePath, expected.FullPath, token).ConfigureAwait(false);
                if (actual.Size != expected.Size || actual.ModifiedTicks != expected.ModifiedTicks || actual.Sha256 != expected.Sha256)
                    throw new IOException($"备份期间源文件发生变化，未提交新清单: {expected.RelativePath}");
            }
            manifest.Files = nextFiles;
            var nextDirectories = tree.Directories.ToDictionary(d => d, d => new ManifestDirEntry { OriginalRel = d, StoredRel = d }, StringComparer.OrdinalIgnoreCase);
            if (strategy == BackupStrategy.Archive)
                foreach (var entry in previousDirectories) nextDirectories.TryAdd(entry.Key, entry.Value);
            var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in nextFiles)
                if (!filePaths.Add(PathHelper.NormalizeRelPath(file.OriginalRel)))
                    throw new InvalidDataException("备份结果包含重复的文件路径，未提交新清单");
            foreach (string directory in nextDirectories.Keys)
                if (filePaths.Contains(directory))
                    throw new InvalidDataException("备份结果中的文件与目录同名，未提交新清单");
            foreach (string path in filePaths.Concat(nextDirectories.Keys))
            {
                int slash = path.IndexOf('/');
                while (slash >= 0)
                {
                    if (filePaths.Contains(path[..slash]))
                        throw new InvalidDataException("备份结果中的文件被用作目录，未提交新清单");
                    slash = path.IndexOf('/', slash + 1);
                }
            }
            manifest.Directories = nextDirectories.Values.ToList();
            manifest.SourceRoot = PathHelper.RemoveLongPathPrefix(sourceDirectory);
            manifest.TargetRoot = PathHelper.NormalizeRelPath(target);
            manifest.BackupPolicy = strategy.ToString();
            manifest.UpdatedAt = DateTimeOffset.UtcNow;
            token.ThrowIfCancellationRequested();
            commitMayHaveStarted = true;
            var snapshot = await _versions.SaveAsync(provider, target, manifest, oldSnapshot?.Id, token).ConfigureAwait(false);
            committed = true;
            return new Result(snapshot, uploaded, unchanged, renamed, strategy == BackupStrategy.Smart ? deleted.Count : 0);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            if (commitMayHaveStarted && !committed)
                throw new IOException("版本化同步提交结果不确定，已保留锁；请先检查提交状态", ex);
            throw;
        }
        finally
        {
            if (committed || !commitMayHaveStarted)
            {
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await lease.ReleaseAsync(releaseTimeout.Token).ConfigureAwait(false); }
                catch (Exception releaseError)
                {
                    string message = committed ? "同步清单已提交，但释放锁失败；请先检查锁状态" : "同步未提交，且释放锁失败；请先检查锁状态";
                    throw new IOException(message, primaryFailure == null ? releaseError : new AggregateException(primaryFailure, releaseError));
                }
            }
        }
    }

    private static bool IsHash(string? value) => value != null && value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsObject(string? value) => value != null && PathHelper.IsSafeRelativePath(value) &&
        value.StartsWith(".fnsync/objects/obj-", StringComparison.Ordinal) && !value[".fnsync/objects/".Length..].Contains('/');

    private static SourceTree Scan(string root, CancellationToken token)
    {
        var directories = new List<string>();
        var files = new List<(string RelativePath, string FullPath)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(DirectoryInfo directory)
        {
            token.ThrowIfCancellationRequested();
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("源目录包含链接或联接点，拒绝同步");
            foreach (var item in directory.EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException($"源目录包含链接或联接点: {item.Name}");
                string relative = PathHelper.NormalizeRelPath(Path.GetRelativePath(root, item.FullName));
                if (!PathHelper.IsSafeRelativePath(relative) || !names.Add(relative)) throw new InvalidDataException("源目录包含不安全或重复的路径");
                if (item is DirectoryInfo sub) { directories.Add(relative); Walk(sub); }
                else if (item is FileInfo) files.Add((relative, item.FullName));
                else throw new IOException($"无法识别源项目类型: {relative}");
            }
        }
        Walk(new DirectoryInfo(root));
        directories.Sort(StringComparer.OrdinalIgnoreCase);
        files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath));
        return new SourceTree(directories, files);
    }

    private static async Task<SourceFile> HashSourceAsync(string relative, string fullPath, CancellationToken token)
    {
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long size = stream.Length;
        long modified = File.GetLastWriteTimeUtc(fullPath).Ticks;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
        return new SourceFile(relative, fullPath, size, modified, hash);
    }
}
