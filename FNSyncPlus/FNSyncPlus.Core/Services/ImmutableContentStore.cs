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

    public async Task<PreparedContent> PrepareAsync(INasStorageProvider provider, string target, string sourceFile,
        CancellationToken token = default)
    {
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
        string objectDirectory = $"{root}/.fnsync/objects";
        string current = "";
        foreach (string part in objectDirectory.Split('/'))
        {
            current = current.Length == 0 ? part : $"{current}/{part}";
            var created = await provider.CreateDirectoryAsync(current, token).ConfigureAwait(false);
            if (!created.Success) throw new IOException($"无法创建内容对象目录: {created.ErrorMessage}");
        }

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
