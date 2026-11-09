using System.Security.Cryptography;
using System.Text.Json;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Isolated experiment for publishing the first physical tree only. It refuses an existing
/// current directory. A sent move with an uncertain outcome retains the writer lease.
/// Incremental publication and recovery of an abandoned lease are not implemented.
/// </summary>
public sealed class ExperimentalFirstProjection
{
    public sealed record Published(string VersionId, string CurrentRoot, string JournalDirectory);

    public async Task<Published> PublishAsync(INasStorageProvider provider, string backupRoot,
        ExperimentalPhysicalSnapshot.Result prepared, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!PathHelper.IsSafeRelativePath(backupRoot) || !PathHelper.IsSafeRelativePath(prepared.CandidateRoot))
            throw new ArgumentException("备份或候选目录路径无效");
        if (provider is not IExclusiveDirectoryProvider exclusive || provider is not IExperimentalCandidateDirectoryMover mover)
            throw new NotSupportedException("存储不支持隔离候选发布");
        string root = PathHelper.NormalizeRelPath(backupRoot);
        string candidateTree = PathHelper.NormalizeRelPath(prepared.CandidateRoot);
        if (provider is FnOsPersonalStorageProvider fnOs)
        {
            root = fnOs.NormalizeRelativePath(root);
            candidateTree = fnOs.NormalizeRelativePath(candidateTree);
        }
        string prefix = $"{root}/.fnsync/projections/candidate-";
        if (!PathHelper.IsSafeRelativePath(root) || !candidateTree.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !candidateTree.EndsWith("/current", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(candidateTree[prefix.Length..^"/current".Length], "N", out _))
            throw new ArgumentException("候选目录不属于该备份");
        string candidate = candidateTree[..^"/current".Length];
        byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(prepared);
        if (receipt.Length > 16 * 1024 * 1024) throw new InvalidDataException("候选记录过大");
        var stable = JsonSerializer.Deserialize<ExperimentalPhysicalSnapshot.Result>(receipt)
            ?? throw new InvalidDataException("候选记录无效");
        var lease = await ExperimentalRemoteLease.TryAcquireAsync(provider, root, token).ConfigureAwait(false)
            ?? throw new IOException("备份被其他写入者占用或存在未释放的锁");
        bool moveStarted = false, committed = false;
        Exception? failure = null;
        try
        {
            var versions = new VersionedManifestStore();
            var latest = await versions.LoadForWritingAsync(provider, root, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("备份没有已提交版本");
            if (latest.Id != stable.VersionId) throw new InvalidDataException("候选目录对应的备份版本已过期");
            var names = LongNameEngine.CreateForProviderType(provider.ProviderType);
            var expectedDirectories = latest.Manifest.Directories.Select(d => PathHelper.NormalizeRelPath(d.OriginalRel))
                .OrderBy(x => x.Count(c => c == '/')).ThenBy(x => x, StringComparer.Ordinal)
                .Select(x => new ExperimentalPhysicalSnapshot.DirectoryMapping(x, names.TranslateDirPath(x).StoredRel)).ToArray();
            var expectedFiles = latest.Manifest.Files.OrderBy(f => f.OriginalRel, StringComparer.Ordinal)
                .Select(f => new ExperimentalPhysicalSnapshot.FileMapping(PathHelper.NormalizeRelPath(f.OriginalRel),
                    names.TranslateFilePath(f.OriginalRel).StoredRel, PathHelper.NormalizeRelPath(f.StoredRel), f.Size, f.Sha256.ToLowerInvariant())).ToArray();
            if (!expectedDirectories.SequenceEqual(stable.Directories) || !expectedFiles.SequenceEqual(stable.Files))
                throw new InvalidDataException("候选映射与已提交版本不一致");
            string receiptPath = $"{candidate}/receipt.json";
            string receiptHash = Convert.ToHexString(SHA256.HashData(receipt)).ToLowerInvariant();
            if (!await provider.VerifyFileIntegrityAsync(receiptPath, receiptHash, receipt.Length, token).ConfigureAwait(false))
                throw new InvalidDataException("候选记录不存在或校验失败");
            token.ThrowIfCancellationRequested();
            if (await provider.GetItemAsync($"{root}/current", token).ConfigureAwait(false) != null)
                throw new IOException("当前目录已存在，首次发布拒绝覆盖");
            await VerifyTreeAsync(provider, candidateTree, stable, token).ConfigureAwait(false);
            string journal = $"{candidate}/publish-{Guid.NewGuid():N}";
            if (await exclusive.TryCreateDirectoryExclusiveAsync(journal, token).ConfigureAwait(false) != ExclusiveDirectoryResult.Created)
                throw new IOException("发布记录目录冲突");
            await WriteRecordAsync(provider, $"{journal}/intent.json", receipt, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            moveStarted = true;
            var moved = await mover.MoveCandidateDirectoryAsync(root, candidate, token).ConfigureAwait(false);
            if (moved != VerifiedMoveOutcome.Moved) throw new IOException("候选目录未确认移动完成");
            await VerifyTreeAsync(provider, $"{root}/current", stable, token).ConfigureAwait(false);
            if ((await versions.LoadForWritingAsync(provider, root, token).ConfigureAwait(false))?.Id != stable.VersionId)
                throw new IOException("发布期间备份版本发生变化");
            await WriteRecordAsync(provider, $"{journal}/committed.json", receipt, token).ConfigureAwait(false);
            committed = true;
            return new Published(stable.VersionId, $"{root}/current", journal);
        }
        catch (Exception ex)
        {
            failure = ex;
            if (moveStarted && !committed) throw new IOException("候选目录发布结果不确定，已保留锁和记录；请检查后处理", ex);
            throw;
        }
        finally
        {
            if (!moveStarted || committed)
            {
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await lease.ReleaseAsync(releaseTimeout.Token).ConfigureAwait(false); }
                catch (Exception ex) { throw new IOException("候选发布操作结束，但释放锁失败", failure == null ? ex : new AggregateException(failure, ex)); }
            }
        }
    }

    private static async Task WriteRecordAsync(INasStorageProvider provider, string path, byte[] content, CancellationToken token)
    {
        using var input = new MemoryStream(content, writable: false);
        var uploaded = await provider.UploadFileAsync(path, input, content.Length, DateTime.UtcNow, false, null, token).ConfigureAwait(false);
        string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        bool verified = uploaded.Success && await provider.VerifyFileIntegrityAsync(path, hash, content.Length, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!verified) throw new IOException("发布记录未通过回读校验");
    }

    private static async Task VerifyTreeAsync(INasStorageProvider provider, string tree,
        ExperimentalPhysicalSnapshot.Result plan, CancellationToken token)
    {
        var root = await provider.GetItemAsync(tree, token).ConfigureAwait(false);
        if (root == null || !root.IsDirectory) throw new DirectoryNotFoundException("待验证的文件树不存在");
        var files = plan.Files.ToDictionary(f => f.PhysicalRel, StringComparer.OrdinalIgnoreCase);
        var dirs = plan.Directories.Select(d => d.PhysicalRel).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(string.Empty);
        while (queue.Count > 0)
        {
            string parent = queue.Dequeue();
            string remote = parent.Length == 0 ? tree : $"{tree}/{parent}";
            foreach (var item in await provider.ListEntriesAsync(remote, token).ConfigureAwait(false))
            {
                if (item == null || !PathHelper.IsSafeRelativePath(item.Name) || item.Name.Contains('/') || item.Name.Contains('\\'))
                    throw new InvalidDataException("文件树含无效目录条目");
                string relative = parent.Length == 0 ? item.Name : $"{parent}/{item.Name}";
                if (item.IsDirectory)
                {
                    if (!dirs.Contains(relative) || !seenDirs.Add(relative)) throw new InvalidDataException("文件树含额外或重复目录");
                    queue.Enqueue(relative);
                }
                else
                {
                    if (!files.TryGetValue(relative, out var expected) || !seenFiles.Add(relative) || item.Size != expected.Size ||
                        !await provider.VerifyFileIntegrityAsync($"{tree}/{relative}", expected.Sha256, expected.Size, token).ConfigureAwait(false))
                        throw new InvalidDataException("文件树含额外、重复或校验失败的文件");
                    token.ThrowIfCancellationRequested();
                }
            }
        }
        if (seenFiles.Count != files.Count || seenDirs.Count != dirs.Count) throw new InvalidDataException("文件树缺少预期文件或目录");
    }
}
