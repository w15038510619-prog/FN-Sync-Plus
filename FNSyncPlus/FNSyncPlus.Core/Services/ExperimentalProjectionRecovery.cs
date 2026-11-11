using System.Text.Json;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Isolated, explicitly confirmed recovery experiment. The storage protocol cannot prove
/// another device has stopped; a false acknowledgement can permit concurrent writers.
/// This is not connected to product tasks or UI.
/// </summary>
public sealed class ExperimentalProjectionRecovery
{
    public const string RequiredAcknowledgement = "已确认原同步程序及所有其他写入设备均已停止";
    public sealed record Recovered(string VersionId, string CurrentRoot, string JournalDirectory);

    public async Task<Recovered> RecoverAfterOwnerStoppedAsync(INasStorageProvider provider, string backupRoot,
        ExperimentalProjectionRecoveryInspector.Inspection expected, string acknowledgement, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(expected);
        if (acknowledgement != RequiredAcknowledgement)
            throw new InvalidOperationException("必须明确确认原同步程序及其他写入设备均已停止");
        if (!PathHelper.IsSafeRelativePath(backupRoot) || !PathHelper.IsSafeRelativePath(expected.JournalDirectory))
            throw new ArgumentException("恢复路径无效");
        if (provider is not IExclusiveDirectoryProvider exclusive || provider is not IExperimentalCandidateDirectoryMover mover)
            throw new NotSupportedException("存储不支持隔离恢复");
        string Canonical(string path) => provider is FnOsPersonalStorageProvider fn ? fn.NormalizeRelativePath(path) : PathHelper.NormalizeRelPath(path);
        string root = Canonical(backupRoot);
        var inspector = new ExperimentalProjectionRecoveryInspector();
        async Task<ExperimentalProjectionRecoveryInspector.Inspection> CheckAsync()
        {
            var actual = await inspector.InspectAsync(provider, root, expected.JournalDirectory, token).ConfigureAwait(false);
            if (actual.VersionId != expected.VersionId || actual.OwnerSha256 != expected.OwnerSha256 ||
                !Canonical(actual.LockDirectory).Equals(Canonical(expected.LockDirectory), StringComparison.OrdinalIgnoreCase) ||
                !Canonical(actual.JournalDirectory).Equals(Canonical(expected.JournalDirectory), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("恢复检查期间版本、记录或锁持有者发生变化");
            return actual;
        }
        var current = await CheckAsync().ConfigureAwait(false);
        if (current.Stage != expected.Stage) throw new InvalidDataException("恢复检查后目录状态已变化，请重新检查");
        string journal = Canonical(current.JournalDirectory);
        string recovery = $"{journal}/recovery";
        if (await exclusive.TryCreateDirectoryExclusiveAsync(recovery, token).ConfigureAwait(false) != ExclusiveDirectoryResult.Created)
            throw new IOException("已有恢复尝试记录，拒绝自动重试；请先检查");
        byte[] confirmation = JsonSerializer.SerializeToUtf8Bytes(new { current.VersionId, current.LockDirectory,
            current.OwnerSha256, current.Stage, Acknowledgement = acknowledgement, ConfirmedAt = DateTimeOffset.UtcNow });
        await ExperimentalFirstProjection.WriteRecordAsync(provider, $"{recovery}/confirmation.json", confirmation, token).ConfigureAwait(false);
        current = await CheckAsync().ConfigureAwait(false);
        if (current.Stage != expected.Stage) throw new InvalidDataException("恢复开始时目录状态已变化，拒绝继续");
        byte[] intentBytes = await ExperimentalProjectionRecoveryInspector.ReadBoundedAsync(provider,
            $"{journal}/intent.json", 32 * 1024 * 1024, token).ConfigureAwait(false);
        var intent = JsonSerializer.Deserialize<ProjectionIntent>(intentBytes) ?? throw new InvalidDataException("发布意图无效");
        string candidate = journal[..journal.LastIndexOf('/')];

        if (current.Stage == ExperimentalProjectionRecoveryInspector.Stage.BeforeArchive)
        {
            if (current.ArchiveDirectory == null ||
                await mover.MoveCurrentToArchiveAsync(root, current.ArchiveDirectory, token).ConfigureAwait(false) != VerifiedMoveOutcome.Moved)
                throw new IOException("恢复时旧目录未确认归档，保留锁和恢复记录");
            current = await CheckAsync().ConfigureAwait(false);
            if (current.Stage != ExperimentalProjectionRecoveryInspector.Stage.OldArchived)
                throw new InvalidDataException("恢复归档后的状态不一致，保留锁");
        }
        if (current.Stage is ExperimentalProjectionRecoveryInspector.Stage.FirstPrepared or ExperimentalProjectionRecoveryInspector.Stage.OldArchived)
        {
            var previousStage = current.Stage;
            if (await mover.MoveCandidateDirectoryAsync(root, candidate, token).ConfigureAwait(false) != VerifiedMoveOutcome.Moved)
                throw new IOException("恢复时新目录未确认移动，保留锁和恢复记录");
            current = await CheckAsync().ConfigureAwait(false);
            var expectedStage = previousStage == ExperimentalProjectionRecoveryInspector.Stage.FirstPrepared
                ? ExperimentalProjectionRecoveryInspector.Stage.FirstMoved : ExperimentalProjectionRecoveryInspector.Stage.NewMoved;
            if (current.Stage != expectedStage) throw new InvalidDataException("恢复移动后的状态不一致，保留锁");
        }
        if (current.Stage is ExperimentalProjectionRecoveryInspector.Stage.FirstMoved or ExperimentalProjectionRecoveryInspector.Stage.NewMoved)
        {
            byte[] next = JsonSerializer.SerializeToUtf8Bytes(intent.Next);
            await ExperimentalFirstProjection.WriteRecordAsync(provider, $"{journal}/committed.json", next, token).ConfigureAwait(false);
            current = await CheckAsync().ConfigureAwait(false);
        }
        if (current.Stage != ExperimentalProjectionRecoveryInspector.Stage.Committed)
            throw new InvalidDataException("恢复后未确认完整发布记录，保留锁");
        token.ThrowIfCancellationRequested();
        await ExperimentalRemoteLease.ReleaseStoppedOwnerAsync(provider, root,
            new ExperimentalRemoteLease.Inspection(current.LockDirectory, current.OwnerSha256), token).ConfigureAwait(false);
        return new Recovered(current.VersionId, $"{root}/current", journal);
    }
}
