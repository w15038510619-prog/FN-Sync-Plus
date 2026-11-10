using System.Text.Json;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Explicit recovery for a stopped writer when the latest committed snapshot and the
/// complete physical publication chain still agree. This does not provide fencing.
/// </summary>
public sealed class ExperimentalStableLockRecovery
{
    public sealed record Inspection(string BackupRoot, string LockDirectory, string OwnerSha256,
        string VersionId, string JournalDirectory);

    public async Task<Inspection?> InspectAsync(INasStorageProvider provider, string backupRoot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!PathHelper.IsSafeRelativePath(backupRoot)) throw new ArgumentException("备份目录路径无效");
        string root = provider is FnOsPersonalStorageProvider fn ? fn.NormalizeRelativePath(backupRoot) : PathHelper.NormalizeRelPath(backupRoot);
        var held = await ExperimentalRemoteLease.InspectAsync(provider, root, token).ConfigureAwait(false);
        if (held == null) return null;
        var publication = await new ExperimentalProjectionDiscovery().DiscoverUnderHeldLeaseAsync(provider, root, held, token).ConfigureAwait(false);
        return new Inspection(root, held.DirectoryPath, held.OwnerSha256, publication.Plan.VersionId, publication.Publication.JournalDirectory);
    }

    public async Task ReleaseAfterOwnerStoppedAsync(INasStorageProvider provider, string backupRoot,
        Inspection expected, string acknowledgement, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (acknowledgement != ExperimentalProjectionRecovery.RequiredAcknowledgement)
            throw new InvalidOperationException("必须明确确认原同步程序及其他写入设备均已停止");
        if (provider is not IExclusiveDirectoryProvider exclusive)
            throw new NotSupportedException("存储不支持独占创建恢复记录");
        var actual = await InspectAsync(provider, backupRoot, token).ConfigureAwait(false);
        if (actual == null || actual != expected) throw new InvalidDataException("检查后锁或已发布版本发生变化，请重新检查");

        string recordDir = $"{actual.LockDirectory}/recovery-{Guid.NewGuid():N}";
        if (await exclusive.TryCreateDirectoryExclusiveAsync(recordDir, token).ConfigureAwait(false) != ExclusiveDirectoryResult.Created)
            throw new IOException("恢复记录目录冲突，保留锁");
        byte[] confirmation = JsonSerializer.SerializeToUtf8Bytes(new { actual.BackupRoot, actual.LockDirectory,
            actual.OwnerSha256, actual.VersionId, actual.JournalDirectory, Acknowledgement = acknowledgement,
            ConfirmedAt = DateTimeOffset.UtcNow });
        await ExperimentalFirstProjection.WriteRecordAsync(provider, $"{recordDir}/confirmation.json", confirmation, token).ConfigureAwait(false);
        var rechecked = await InspectAsync(provider, backupRoot, token).ConfigureAwait(false);
        if (rechecked == null || rechecked != expected) throw new InvalidDataException("恢复开始时锁或已发布版本发生变化，保留锁");
        await ExperimentalRemoteLease.ReleaseStoppedOwnerAsync(provider, actual.BackupRoot,
            new ExperimentalRemoteLease.Inspection(actual.LockDirectory, actual.OwnerSha256), token).ConfigureAwait(false);
    }
}
