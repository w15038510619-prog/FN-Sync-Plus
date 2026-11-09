using System.Text.Json;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Experimental migration used only by isolated tests. Legacy data and its manifest are never modified.
/// </summary>
public sealed class LegacyBackupMigration
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly ImmutableContentStore _objects = new();
    private readonly VersionedManifestStore _versions = new();

    public async Task<VersionedManifestStore.Snapshot> MigrateAsync(
        INasStorageProvider provider, string target, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!PathHelper.IsSafeRelativePath(target)) throw new ArgumentException("备份目标必须是安全的相对路径", nameof(target));
        var existingTarget = await provider.GetItemAsync(PathHelper.NormalizeRelPath(target), token).ConfigureAwait(false);
        if (existingTarget == null || !existingTarget.IsDirectory) throw new InvalidDataException("旧备份目录不存在");
        string root = PathHelper.NormalizeRelPath(target);
        var existingMetadata = await provider.GetItemAsync($"{root}/.fnsync", token).ConfigureAwait(false);
        if (existingMetadata == null || !existingMetadata.IsDirectory) throw new InvalidDataException("旧备份没有元数据目录");
        var existingMain = await provider.GetItemAsync($"{root}/.fnsync/manifest.json", token).ConfigureAwait(false);
        if (existingMain == null || existingMain.IsDirectory) throw new InvalidDataException("旧主清单不存在，拒绝自动迁移");
        var lease = await ExperimentalRemoteLease.TryAcquireAsync(provider, target, token).ConfigureAwait(false)
            ?? throw new IOException("备份目标正被占用，或存在未释放的锁");
        bool commitMayHaveStarted = false;
        bool committed = false;
        Exception? primaryFailure = null;
        try
        {
            var result = await MigrateCoreAsync(provider, target, () => commitMayHaveStarted = true, token).ConfigureAwait(false);
            committed = true;
            return result;
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            if (commitMayHaveStarted && !committed)
                throw new IOException("版本化清单提交结果不确定，已保留锁；请先检查提交状态", ex);
            throw;
        }
        finally
        {
            // An interrupted commit can have succeeded remotely. Keep the lock for explicit
            // recovery instead of allowing another writer to assume the result was not committed.
            if (committed || !commitMayHaveStarted)
            {
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await lease.ReleaseAsync(releaseTimeout.Token).ConfigureAwait(false); }
                catch (Exception releaseError)
                {
                    string message = committed ? "迁移清单已提交，但释放锁失败；请先检查锁状态" : "迁移未提交，且释放锁失败；请先检查锁状态";
                    throw new IOException(message, primaryFailure == null ? releaseError : new AggregateException(primaryFailure, releaseError));
                }
            }
        }
    }

    private async Task<VersionedManifestStore.Snapshot> MigrateCoreAsync(
        INasStorageProvider provider, string target, Action beforeCommit, CancellationToken token)
    {
        string root = PathHelper.NormalizeRelPath(target);
        var targetItem = await provider.GetItemAsync(root, token).ConfigureAwait(false);
        if (targetItem == null || !targetItem.IsDirectory) throw new InvalidDataException("旧备份目录不存在");
        var metadata = await provider.GetItemAsync($"{root}/.fnsync", token).ConfigureAwait(false);
        if (metadata == null || !metadata.IsDirectory) throw new InvalidDataException("旧备份没有元数据目录");
        var main = await provider.GetItemAsync($"{root}/.fnsync/manifest.json", token).ConfigureAwait(false);
        if (main == null || main.IsDirectory) throw new InvalidDataException("旧主清单不存在，拒绝自动迁移");
        if (await _versions.LoadAsync(provider, root, token).ConfigureAwait(false) != null)
            throw new InvalidOperationException("目标已有版本化清单，拒绝重复迁移");

        byte[] originalBytes;
        using (var input = await provider.OpenReadAsync($"{root}/.fnsync/manifest.json", token).ConfigureAwait(false))
        using (var buffer = new MemoryStream())
        {
            await input.CopyToAsync(buffer, token).ConfigureAwait(false);
            originalBytes = buffer.ToArray();
        }
        Manifest manifest;
        try
        {
            using var json = JsonDocument.Parse(originalBytes);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.EnumerateObject().Any(p => p.Name.Equals("files", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array))
                throw new InvalidDataException("旧主清单缺少文件列表");
            if (!json.RootElement.EnumerateObject().Any(p => p.Name.Equals("version", StringComparison.OrdinalIgnoreCase) &&
                p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int version) && version is 3 or 4))
                throw new InvalidDataException("旧主清单缺少受支持的版本号");
            manifest = JsonSerializer.Deserialize<Manifest>(originalBytes, JsonOptions) ?? throw new InvalidDataException("旧主清单为空");
        }
        catch (JsonException ex) { throw new InvalidDataException("旧主清单损坏，拒绝自动迁移", ex); }
        if (manifest.Version is not (3 or 4) || manifest.Files == null || manifest.History == null || manifest.Directories == null)
            throw new InvalidDataException("旧主清单版本或字段不受支持");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in manifest.Files)
        {
            if (file == null || !PathHelper.IsSafeRelativePath(file.OriginalRel) || !PathHelper.IsSafeRelativePath(file.StoredRel) ||
                file.Size < 0 || file.Sha256 == null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit) ||
                !names.Add(PathHelper.NormalizeRelPath(file.OriginalRel)))
                throw new InvalidDataException("旧主清单包含不安全、重复或缺少有效哈希的文件");
        }
        foreach (var history in manifest.History)
            if (history == null || !PathHelper.IsSafeRelativePath(history.OriginalRel) || !PathHelper.IsSafeRelativePath(history.StoredRel) ||
                history.Size < 0 || history.Sha256 == null || history.Sha256.Length != 64 || !history.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("旧历史记录包含不安全的路径或缺少有效哈希");
        foreach (var directory in manifest.Directories)
            if (directory == null || !PathHelper.IsSafeRelativePath(directory.OriginalRel) || !PathHelper.IsSafeRelativePath(directory.StoredRel))
                throw new InvalidDataException("旧主清单包含不安全的目录路径");

        foreach (var file in manifest.Files)
        {
            var copy = await _objects.CopyExistingAsync(provider, root, file.StoredRel, file.Size, file.Sha256, token).ConfigureAwait(false);
            file.StoredRel = copy.StoredRel;
            file.StoredUtf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(copy.StoredRel));
        }
        foreach (var history in manifest.History)
        {
            var copy = await _objects.CopyExistingAsync(provider, root, history.StoredRel, history.Size, history.Sha256, token).ConfigureAwait(false);
            history.StoredRel = copy.StoredRel;
            history.StoredUtf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(copy.StoredRel));
        }
        token.ThrowIfCancellationRequested();
        using (var current = await provider.OpenReadAsync($"{root}/.fnsync/manifest.json", token).ConfigureAwait(false))
        using (var currentBytes = new MemoryStream())
        {
            await current.CopyToAsync(currentBytes, token).ConfigureAwait(false);
            if (!originalBytes.AsSpan().SequenceEqual(currentBytes.ToArray()))
                throw new IOException("迁移期间旧主清单发生变化，拒绝提交");
        }
        beforeCommit();
        return await _versions.SaveAsync(provider, root, manifest, null, token).ConfigureAwait(false);
    }
}
