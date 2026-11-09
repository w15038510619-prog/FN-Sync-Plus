using System.Security.Cryptography;
using System.Text.Json;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Isolated experiment for replacing one already verified physical tree. The old tree is
/// moved to a fresh archive before the candidate is moved into place. This is not atomic;
/// a failure after a move begins retains the lock and the journal for inspection.
/// </summary>
public sealed class ExperimentalNextProjection
{
    public sealed record Published(ExperimentalFirstProjection.Published Current, string ArchivedRoot);

    public async Task<Published> PublishAsync(INasStorageProvider provider, string backupRoot,
        ExperimentalPhysicalSnapshot.Result previous, ExperimentalFirstProjection.Published previousPublication,
        ExperimentalPhysicalSnapshot.Result prepared, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(previousPublication);
        ArgumentNullException.ThrowIfNull(prepared);
        if (!PathHelper.IsSafeRelativePath(backupRoot) || !PathHelper.IsSafeRelativePath(previous.CandidateRoot) ||
            !PathHelper.IsSafeRelativePath(prepared.CandidateRoot) || !PathHelper.IsSafeRelativePath(previousPublication.CurrentRoot) ||
            !PathHelper.IsSafeRelativePath(previousPublication.JournalDirectory)) throw new ArgumentException("物理发布路径无效");
        if (provider is not IExclusiveDirectoryProvider exclusive || provider is not IExperimentalCandidateDirectoryMover mover)
            throw new NotSupportedException("存储不支持隔离候选发布");
        string Canonical(string path) => provider is FnOsPersonalStorageProvider fn ? fn.NormalizeRelativePath(path) : PathHelper.NormalizeRelPath(path);
        string root = Canonical(backupRoot);
        string CandidateDirectory(string tree)
        {
            tree = Canonical(tree);
            string prefix = $"{root}/.fnsync/projections/candidate-";
            if (!PathHelper.IsSafeRelativePath(root) || !tree.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !tree.EndsWith("/current", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(tree[prefix.Length..^"/current".Length], "N", out _))
                throw new ArgumentException("候选目录不属于该备份");
            return tree[..^"/current".Length];
        }
        string previousCandidate = CandidateDirectory(previous.CandidateRoot);
        string nextCandidate = CandidateDirectory(prepared.CandidateRoot);
        string previousJournal = Canonical(previousPublication.JournalDirectory);
        string journalPrefix = $"{previousCandidate}/publish-";
        if (string.Equals(previousCandidate, nextCandidate, StringComparison.OrdinalIgnoreCase) ||
            !Canonical(previousPublication.CurrentRoot).Equals($"{root}/current", StringComparison.OrdinalIgnoreCase) ||
            previousPublication.VersionId != previous.VersionId ||
            !previousJournal.StartsWith(journalPrefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(previousJournal[journalPrefix.Length..], "N", out _))
            throw new ArgumentException("上一轮发布记录或新候选目录无效");

        byte[] previousBytes = JsonSerializer.SerializeToUtf8Bytes(previous);
        byte[] nextBytes = JsonSerializer.SerializeToUtf8Bytes(prepared);
        if (previousBytes.Length > 16 * 1024 * 1024 || nextBytes.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("候选记录过大");
        var oldPlan = JsonSerializer.Deserialize<ExperimentalPhysicalSnapshot.Result>(previousBytes) ?? throw new InvalidDataException("旧记录无效");
        var nextPlan = JsonSerializer.Deserialize<ExperimentalPhysicalSnapshot.Result>(nextBytes) ?? throw new InvalidDataException("新记录无效");
        var lease = await ExperimentalRemoteLease.TryAcquireAsync(provider, root, token).ConfigureAwait(false)
            ?? throw new IOException("备份被其他写入者占用或存在未释放的锁");
        bool moveStarted = false, committed = false;
        Exception? failure = null;
        try
        {
            var versions = new VersionedManifestStore();
            var latest = await versions.LoadForWritingAsync(provider, root, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("备份没有已提交版本");
            var oldVersion = await versions.LoadCommittedByIdForWritingAsync(provider, root, oldPlan.VersionId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("旧物理目录对应的已提交版本不存在");
            if (oldVersion.Id == latest.Id) throw new InvalidDataException("新旧物理目录对应同一版本，拒绝重复发布");
            ExperimentalFirstProjection.ValidatePlan(oldPlan, oldVersion, provider.ProviderType);
            ExperimentalFirstProjection.ValidatePlan(nextPlan, latest, provider.ProviderType);
            await VerifyRecordAsync(provider, $"{previousCandidate}/receipt.json", previousBytes, token).ConfigureAwait(false);
            await VerifyRecordAsync(provider, $"{previousJournal}/committed.json", previousBytes, token).ConfigureAwait(false);
            if (await provider.GetItemAsync($"{previousJournal}/intent.json", token).ConfigureAwait(false) == null)
                throw new InvalidDataException("上一轮发布意图记录不存在");
            await VerifyRecordAsync(provider, $"{nextCandidate}/receipt.json", nextBytes, token).ConfigureAwait(false);
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{root}/current", oldPlan, token).ConfigureAwait(false);
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{nextCandidate}/current", nextPlan, token).ConfigureAwait(false);

            string archive = $"{root}/.fnsync/projections/archive-{Guid.NewGuid():N}";
            if (await exclusive.TryCreateDirectoryExclusiveAsync(archive, token).ConfigureAwait(false) != ExclusiveDirectoryResult.Created)
                throw new IOException("归档目录冲突");
            string journal = $"{nextCandidate}/publish-{Guid.NewGuid():N}";
            if (await exclusive.TryCreateDirectoryExclusiveAsync(journal, token).ConfigureAwait(false) != ExclusiveDirectoryResult.Created)
                throw new IOException("发布记录目录冲突");
            byte[] intent = JsonSerializer.SerializeToUtf8Bytes(new { Previous = oldPlan, Next = nextPlan, PreviousJournal = previousJournal, Archive = archive });
            await ExperimentalFirstProjection.WriteRecordAsync(provider, $"{journal}/intent.json", intent, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            moveStarted = true;
            if (await mover.MoveCurrentToArchiveAsync(root, archive, token).ConfigureAwait(false) != VerifiedMoveOutcome.Moved)
                throw new IOException("旧当前目录未确认归档完成");
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{archive}/current", oldPlan, token).ConfigureAwait(false);
            if (await mover.MoveCandidateDirectoryAsync(root, nextCandidate, token).ConfigureAwait(false) != VerifiedMoveOutcome.Moved)
                throw new IOException("新候选目录未确认发布完成");
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{root}/current", nextPlan, token).ConfigureAwait(false);
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{archive}/current", oldPlan, token).ConfigureAwait(false);
            if ((await versions.LoadForWritingAsync(provider, root, token).ConfigureAwait(false))?.Id != nextPlan.VersionId)
                throw new IOException("发布期间备份版本发生变化");
            await ExperimentalFirstProjection.WriteRecordAsync(provider, $"{journal}/committed.json", nextBytes, token).ConfigureAwait(false);
            committed = true;
            return new Published(new ExperimentalFirstProjection.Published(nextPlan.VersionId, $"{root}/current", journal), $"{archive}/current");
        }
        catch (Exception ex)
        {
            failure = ex;
            if (moveStarted && !committed) throw new IOException("增量物理发布结果不确定，已保留锁和记录；请检查后处理", ex);
            throw;
        }
        finally
        {
            if (!moveStarted || committed)
            {
                using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await lease.ReleaseAsync(releaseTimeout.Token).ConfigureAwait(false); }
                catch (Exception ex) { throw new IOException("增量物理发布操作结束，但释放锁失败", failure == null ? ex : new AggregateException(failure, ex)); }
            }
        }
    }

    private static async Task VerifyRecordAsync(INasStorageProvider provider, string path, byte[] expected, CancellationToken token)
    {
        string hash = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
        bool valid = await provider.VerifyFileIntegrityAsync(path, hash, expected.Length, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!valid) throw new InvalidDataException("物理发布记录不存在或校验失败");
    }
}
