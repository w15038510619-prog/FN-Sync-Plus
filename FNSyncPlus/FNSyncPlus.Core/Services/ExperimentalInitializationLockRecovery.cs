using System.Security.Cryptography;
using System.Text.Json;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>Releases a stopped initial writer only before any manifest or physical publication exists.</summary>
public sealed class ExperimentalInitializationLockRecovery
{
    public sealed record Inspection(string BackupRoot, string LockDirectory, string OwnerSha256, string InitializationSha256);

    public async Task<Inspection?> InspectAsync(INasStorageProvider provider, SyncTask task, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(task);
        if (!task.ExperimentalPendingInitialization || task.ExperimentalFirstPublicationPending ||
            !Guid.TryParseExact(task.ExperimentalInitializationId, "N", out var id) || id == Guid.Empty ||
            string.IsNullOrWhiteSpace(task.SourceFolder) || !PathHelper.IsSafeRelativePath(task.DestinationFolder))
            throw new InvalidDataException("任务没有有效的首次备份归属信息，拒绝释放锁");
        string root = provider is FnOsPersonalStorageProvider fn ? fn.NormalizeRelativePath(task.DestinationFolder) : PathHelper.NormalizeRelPath(task.DestinationFolder);
        string? existing = string.IsNullOrWhiteSpace(task.ExperimentalExistingRoot) ? null : task.ExperimentalExistingRoot;
        if (existing != null) existing = ExperimentalVersionedBackup.ValidateReuseLocations(provider, root, existing).ExistingRoot;
        byte[] expected = ExperimentalVersionedBackup.InitializationOwnership(task.SourceFolder, root, existing, task.Strategy, task.ExperimentalInitializationId!);
        var held = await ExperimentalRemoteLease.InspectAsync(provider, root, token).ConfigureAwait(false);
        if (held == null) return null;
        byte[] recorded = await ExperimentalProjectionRecoveryInspector.ReadBoundedAsync(provider,
            $"{root}/.fnsync-init-{task.ExperimentalInitializationId}.json", 512 * 1024, token).ConfigureAwait(false);
        if (!expected.AsSpan().SequenceEqual(recorded)) throw new InvalidDataException("首次备份归属记录不匹配，拒绝释放锁");
        if (await provider.GetItemAsync($"{root}/current", token).ConfigureAwait(false) != null ||
            await provider.GetItemAsync($"{root}/.fnsync/projections", token).ConfigureAwait(false) != null ||
            await new VersionedManifestStore().LoadForWritingAsync(provider, root, token).ConfigureAwait(false) != null)
            throw new InvalidDataException("首次备份已有提交或发布记录，不能按未提交状态释放锁");
        return new Inspection(root, held.DirectoryPath, held.OwnerSha256, Convert.ToHexString(SHA256.HashData(recorded)).ToLowerInvariant());
    }

    public async Task ReleaseAfterOwnerStoppedAsync(INasStorageProvider provider, SyncTask task, Inspection expected,
        string acknowledgement, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (acknowledgement != ExperimentalProjectionRecovery.RequiredAcknowledgement)
            throw new InvalidOperationException("必须明确确认原同步程序及其他写入设备均已停止");
        if (provider is not IExclusiveDirectoryProvider exclusive) throw new NotSupportedException("存储不支持独占创建恢复记录");
        var actual = await InspectAsync(provider, task, token).ConfigureAwait(false);
        if (actual == null || actual != expected) throw new InvalidDataException("检查后锁或首次备份状态发生变化，请重新检查");
        string recordDir = $"{actual.LockDirectory}/recovery-{Guid.NewGuid():N}";
        if (await exclusive.TryCreateDirectoryExclusiveAsync(recordDir, token).ConfigureAwait(false) != ExclusiveDirectoryResult.Created)
            throw new IOException("恢复记录目录冲突，保留锁");
        byte[] confirmation = JsonSerializer.SerializeToUtf8Bytes(new { actual.BackupRoot, actual.LockDirectory, actual.OwnerSha256,
            actual.InitializationSha256, Acknowledgement = acknowledgement, ConfirmedAt = DateTimeOffset.UtcNow });
        await ExperimentalFirstProjection.WriteRecordAsync(provider, $"{recordDir}/confirmation.json", confirmation, token).ConfigureAwait(false);
        if (await InspectAsync(provider, task, token).ConfigureAwait(false) != expected)
            throw new InvalidDataException("恢复开始时锁或首次备份状态发生变化，保留锁");
        await ExperimentalRemoteLease.ReleaseStoppedOwnerAsync(provider, actual.BackupRoot,
            new ExperimentalRemoteLease.Inspection(actual.LockDirectory, actual.OwnerSha256), token).ConfigureAwait(false);
    }
}
