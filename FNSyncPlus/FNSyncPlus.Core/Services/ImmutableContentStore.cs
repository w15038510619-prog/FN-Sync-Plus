using System.Security.Cryptography;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Experimental preparation of immutable content. Not yet used by the sync engine.
/// Prepared objects are intentionally retained on failure; no cleanup policy is assumed.
/// </summary>
public sealed class ImmutableContentStore
{
    public sealed record PreparedContent(string StoredRel, long Size, string Sha256, DateTime ModifiedUtc);

    /// <summary>Copies a verified legacy remote file to a fresh object without changing the original.</summary>
    public async Task<PreparedContent> CopyExistingAsync(INasStorageProvider provider, string target, string storedRel,
        long expectedSize, string expectedSha256, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!PathHelper.IsSafeRelativePath(target) || !PathHelper.IsSafeRelativePath(storedRel))
            throw new ArgumentException("备份目标和旧文件路径必须是安全的相对路径");
        if (expectedSize < 0 || expectedSha256 == null || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("旧文件缺少有效大小或 SHA-256，无法安全迁移");

        string root = PathHelper.NormalizeRelPath(target);
        string oldPath = $"{root}/{PathHelper.NormalizeRelPath(storedRel)}";
        var oldItem = await provider.GetItemAsync(oldPath, token).ConfigureAwait(false);
        if (oldItem == null || oldItem.IsDirectory || oldItem.Size != expectedSize)
            throw new InvalidDataException("旧备份文件不存在或大小与清单不一致，拒绝迁移");

        string newRel = $".fnsync/objects/obj-{Guid.NewGuid():N}";
        await EnsureObjectDirectoryAsync(provider, root, token).ConfigureAwait(false);
        string newPath = $"{root}/{newRel}";
        using var oldStream = await provider.OpenReadAsync(oldPath, token).ConfigureAwait(false);
        var uploaded = await provider.UploadFileAsync(newPath, oldStream, expectedSize, oldItem.LastModifiedUtc, false, null, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!uploaded.Success) throw new IOException($"旧备份对象复制失败: {uploaded.ErrorMessage}");
        var copied = await provider.GetItemAsync(newPath, token).ConfigureAwait(false);
        if (copied == null || copied.IsDirectory || copied.Size != expectedSize)
            throw new InvalidDataException("旧备份对象复制后大小不符");
        bool verified = await provider.VerifyFileIntegrityAsync(newPath, expectedSha256, expectedSize, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!verified) throw new InvalidDataException("旧备份对象复制后 SHA-256 不符");
        return new PreparedContent(newRel, expectedSize, expectedSha256.ToLowerInvariant(), oldItem.LastModifiedUtc);
    }

    private static async Task EnsureObjectDirectoryAsync(INasStorageProvider provider, string root, CancellationToken token)
    {
        string current = "";
        foreach (string part in $"{root}/.fnsync/objects".Split('/'))
        {
            current = current.Length == 0 ? part : $"{current}/{part}";
            var created = await provider.CreateDirectoryAsync(current, token).ConfigureAwait(false);
            if (!created.Success) throw new IOException($"无法创建内容对象目录: {created.ErrorMessage}");
        }
    }

    public async Task<PreparedContent> PrepareAsync(INasStorageProvider provider, string target, string sourceFile,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!PathHelper.IsSafeRelativePath(target)) throw new ArgumentException("备份目标必须是安全的相对路径", nameof(target));
        if (string.IsNullOrWhiteSpace(sourceFile)) throw new ArgumentException("源文件路径不能为空", nameof(sourceFile));

        // Keep this handle open through hashing and upload. On Windows FileShare.Read disallows
        // simultaneous writers and deletion; if a writer is already active, open fails instead.
        string longSource = PathHelper.EnsureLongPathPrefix(sourceFile);
        await using var input = new FileStream(longSource, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long size = input.Length;
        DateTime modified = File.GetLastWriteTimeUtc(longSource);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false)).ToLowerInvariant();
        input.Position = 0;

        string storedRel = $".fnsync/objects/obj-{Guid.NewGuid():N}";
        string root = PathHelper.NormalizeRelPath(target);
        await EnsureObjectDirectoryAsync(provider, root, token).ConfigureAwait(false);

        string objectPath = $"{root}/{storedRel}";
        var uploaded = await provider.UploadFileAsync(objectPath, input, size, modified, false, null, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!uploaded.Success) throw new IOException($"内容对象上传失败: {uploaded.ErrorMessage}");
        bool verified = await provider.VerifyFileIntegrityAsync(objectPath, hash, size, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!verified) throw new InvalidDataException("内容对象回读校验失败");
        return new PreparedContent(storedRel, size, hash, modified);
    }
}
