using System.Security.Cryptography;
using System.Text.Json;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Prepares a separate, verified candidate tree from immutable objects. It does not publish,
/// move, replace or remove the current tree; publication and crash recovery are not implemented.
/// </summary>
public sealed class ExperimentalPhysicalSnapshot
{
    public sealed record FileMapping(string OriginalRel, string PhysicalRel, string ObjectRel, long Size, string Sha256);
    public sealed record DirectoryMapping(string OriginalRel, string PhysicalRel);
    public sealed record Result(string VersionId, string CandidateRoot, IReadOnlyList<FileMapping> Files,
        IReadOnlyList<DirectoryMapping> Directories);

    public async Task<Result> PrepareAsync(INasStorageProvider provider, string backupRoot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!PathHelper.IsSafeRelativePath(backupRoot)) throw new ArgumentException("备份目录路径无效", nameof(backupRoot));
        if (provider is not IExclusiveDirectoryProvider exclusive) throw new NotSupportedException("存储不支持独占创建候选目录");
        string root = PathHelper.NormalizeRelPath(backupRoot);
        var versions = new VersionedManifestStore();
        var snapshot = await versions.LoadForWritingAsync(provider, root, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("找不到已提交的版本化备份");
        var manifest = snapshot.Manifest;
        if (manifest.Files == null || manifest.Directories == null) throw new InvalidDataException("版本清单不完整");

        var originalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var originalDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in manifest.Directories)
            if (directory == null || !PathHelper.IsSafeRelativePath(directory.OriginalRel) ||
                !originalDirs.Add(PathHelper.NormalizeRelPath(directory.OriginalRel)))
                throw new InvalidDataException("目录清单包含无效或重复路径");
        foreach (var file in manifest.Files)
            if (file == null || !file.IsActive || !PathHelper.IsSafeRelativePath(file.OriginalRel) ||
                !PathHelper.IsSafeRelativePath(file.StoredRel) || !file.StoredRel.StartsWith(".fnsync/objects/obj-", StringComparison.Ordinal) ||
                file.Size < 0 || file.Sha256?.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) ||
                !originalFiles.Add(PathHelper.NormalizeRelPath(file.OriginalRel)))
                throw new InvalidDataException("文件清单包含无效路径、对象或校验值");
        foreach (string path in originalFiles.Concat(originalDirs))
        {
            if (originalFiles.Contains(path) && originalDirs.Contains(path)) throw new InvalidDataException("文件和目录重名");
            int slash = path.IndexOf('/');
            while (slash >= 0)
            {
                if (originalFiles.Contains(path[..slash])) throw new InvalidDataException("文件不能同时用作父目录");
                if (!originalDirs.Contains(path[..slash])) throw new InvalidDataException("目录清单缺少父目录");
                slash = path.IndexOf('/', slash + 1);
            }
        }

        var names = LongNameEngine.CreateForProviderType(provider.ProviderType);
        var directories = originalDirs.OrderBy(x => x.Count(c => c == '/')).ThenBy(x => x, StringComparer.Ordinal)
            .Select(x => new DirectoryMapping(x, names.TranslateDirPath(x).StoredRel)).ToList();
        var files = manifest.Files.OrderBy(x => x.OriginalRel, StringComparer.Ordinal)
            .Select(x => new FileMapping(PathHelper.NormalizeRelPath(x.OriginalRel), names.TranslateFilePath(x.OriginalRel).StoredRel,
                PathHelper.NormalizeRelPath(x.StoredRel), x.Size, x.Sha256.ToLowerInvariant())).ToList();
        var allPhysical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in directories.Select(d => d.PhysicalRel).Concat(files.Select(f => f.PhysicalRel)))
            if (!PathHelper.IsSafeRelativePath(path) || !allPhysical.Add(path)) throw new InvalidDataException("候选目录存在映射冲突");

        string projectionParent = $"{root}/.fnsync/projections";
        var preparedParent = await provider.CreateDirectoryAsync(projectionParent, token).ConfigureAwait(false);
        if (!preparedParent.Success) throw new IOException($"无法准备候选目录父目录: {preparedParent.ErrorMessage}");
        string candidate = $"{projectionParent}/candidate-{Guid.NewGuid():N}";
        if (await exclusive.TryCreateDirectoryExclusiveAsync(candidate, token).ConfigureAwait(false) != ExclusiveDirectoryResult.Created)
            throw new IOException("候选目录已存在，拒绝复用");
        string tree = $"{candidate}/tree";
        if (await exclusive.TryCreateDirectoryExclusiveAsync(tree, token).ConfigureAwait(false) != ExclusiveDirectoryResult.Created)
            throw new IOException("候选文件树已存在，拒绝复用");

        foreach (var directory in directories)
            await EnsureDirectoryAsync(provider, $"{tree}/{directory.PhysicalRel}", token).ConfigureAwait(false);
        foreach (var file in files)
        {
            int slash = file.PhysicalRel.LastIndexOf('/');
            if (slash >= 0) await EnsureDirectoryAsync(provider, $"{tree}/{file.PhysicalRel[..slash]}", token).ConfigureAwait(false);
            string target = $"{tree}/{file.PhysicalRel}";
            using var input = await provider.OpenReadAsync($"{root}/{file.ObjectRel}", token).ConfigureAwait(false);
            var upload = await provider.UploadFileAsync(target, input, file.Size, DateTime.UtcNow, false, null, token).ConfigureAwait(false);
            if (!upload.Success) throw new IOException($"候选文件写入失败: {upload.ErrorMessage}");
            if (!await provider.VerifyFileIntegrityAsync(target, file.Sha256, file.Size, token).ConfigureAwait(false))
                throw new InvalidDataException($"候选文件校验失败: {file.OriginalRel}");
        }
        // This detects changes during preparation, but is not a publication lock. A future
        // publisher must hold the writer lease and check the version again before any move.
        var latest = await versions.LoadForWritingAsync(provider, root, token).ConfigureAwait(false);
        if (latest?.Id != snapshot.Id) throw new IOException("准备候选目录期间备份版本已变化，拒绝使用该候选目录");

        var result = new Result(snapshot.Id, tree, files, directories);
        byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(result);
        using var receiptStream = new MemoryStream(receipt, writable: false);
        string receiptPath = $"{candidate}/receipt.json";
        var receiptUpload = await provider.UploadFileAsync(receiptPath, receiptStream, receipt.Length, DateTime.UtcNow, false, null, token).ConfigureAwait(false);
        string receiptHash = Convert.ToHexString(SHA256.HashData(receipt)).ToLowerInvariant();
        if (!receiptUpload.Success || !await provider.VerifyFileIntegrityAsync(receiptPath, receiptHash, receipt.Length, token).ConfigureAwait(false))
            throw new IOException("候选目录记录未通过回读校验");
        return result;
    }

    private static async Task EnsureDirectoryAsync(INasStorageProvider provider, string path, CancellationToken token)
    {
        var result = await provider.CreateDirectoryAsync(path, token).ConfigureAwait(false);
        if (!result.Success) throw new IOException($"无法创建候选子目录: {result.ErrorMessage}");
    }
}
