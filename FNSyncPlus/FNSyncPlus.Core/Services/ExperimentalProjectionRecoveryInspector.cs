using System.Text.Json;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Read-only inspection of an interrupted projection. This cannot determine whether its
/// lock owner is still running, release a lock, move files, or safely resume publication.
/// </summary>
public sealed class ExperimentalProjectionRecoveryInspector
{
    public enum Stage { FirstPrepared, FirstMoved, BeforeArchive, OldArchived, NewMoved, Committed }
    public sealed record Inspection(Stage Stage, string VersionId, string JournalDirectory,
        string LockDirectory, string OwnerSha256, string? ArchiveDirectory);

    public async Task<Inspection> InspectAsync(INasStorageProvider provider, string backupRoot, string journalDirectory,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!PathHelper.IsSafeRelativePath(backupRoot) || !PathHelper.IsSafeRelativePath(journalDirectory))
            throw new ArgumentException("备份或发布记录路径无效");
        string Canonical(string path) => provider is FnOsPersonalStorageProvider fn ? fn.NormalizeRelativePath(path) : PathHelper.NormalizeRelPath(path);
        string root = Canonical(backupRoot), journal = Canonical(journalDirectory);
        string projectionPrefix = $"{root}/.fnsync/projections/";
        if (!PathHelper.IsSafeRelativePath(root) || !journal.StartsWith(projectionPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("发布记录不属于该备份");
        string[] parts = journal[projectionPrefix.Length..].Split('/');
        if (parts.Length != 2 || !parts[0].StartsWith("candidate-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(parts[0]["candidate-".Length..], "N", out _) ||
            !parts[1].StartsWith("publish-", StringComparison.Ordinal) || !Guid.TryParseExact(parts[1]["publish-".Length..], "N", out _))
            throw new ArgumentException("发布记录目录无效");
        string candidate = $"{projectionPrefix}{parts[0]}";
        byte[] intentBytes = await ReadBoundedAsync(provider, $"{journal}/intent.json", 32 * 1024 * 1024, token).ConfigureAwait(false);
        var intent = JsonSerializer.Deserialize<ProjectionIntent>(intentBytes) ?? throw new InvalidDataException("发布意图无效");
        if (intent.Schema != 1 || (intent.Kind != "first" && intent.Kind != "next") || intent.Next == null ||
            !PathHelper.IsSafeRelativePath(intent.LeaseDirectory) || intent.OwnerSha256?.Length != 64 || !intent.OwnerSha256.All(Uri.IsHexDigit) ||
            !PathHelper.IsSafeRelativePath(intent.Next.CandidateRoot) ||
            !Canonical(intent.Next.CandidateRoot).Equals($"{candidate}/current", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("发布意图缺少有效的锁绑定或候选目录");
        var held = await ExperimentalRemoteLease.InspectAsync(provider, root, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("没有可关联的未释放锁");
        if (!Canonical(intent.LeaseDirectory).Equals(Canonical(held.DirectoryPath), StringComparison.OrdinalIgnoreCase) ||
            !intent.OwnerSha256.Equals(held.OwnerSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("发布意图与当前未释放锁不匹配");
        var versions = new VersionedManifestStore();
        var latest = await versions.LoadForWritingAsync(provider, root, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("备份没有已提交版本");
        ExperimentalFirstProjection.ValidatePlan(intent.Next, latest, provider.ProviderType);
        byte[] nextBytes = JsonSerializer.SerializeToUtf8Bytes(intent.Next);
        await ExperimentalNextProjection.VerifyRecordAsync(provider, $"{candidate}/receipt.json", nextBytes, token).ConfigureAwait(false);

        ExperimentalPhysicalSnapshot.Result? old = intent.Previous;
        string? archive = null;
        if (intent.Kind == "next")
        {
            if (old == null || !PathHelper.IsSafeRelativePath(old.CandidateRoot) || intent.Archive == null ||
                !PathHelper.IsSafeRelativePath(intent.Archive) || intent.PreviousJournal == null ||
                !PathHelper.IsSafeRelativePath(intent.PreviousJournal)) throw new InvalidDataException("增量发布意图不完整");
            archive = Canonical(intent.Archive!);
            string archivePrefix = $"{projectionPrefix}archive-";
            if (!archive.StartsWith(archivePrefix, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(archive[archivePrefix.Length..], "N", out _)) throw new InvalidDataException("归档目录无效");
            string oldTree = Canonical(old.CandidateRoot);
            string oldPrefix = $"{projectionPrefix}candidate-";
            if (!oldTree.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase) || !oldTree.EndsWith("/current", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(oldTree[oldPrefix.Length..^"/current".Length], "N", out _)) throw new InvalidDataException("旧候选目录无效");
            string oldCandidate = oldTree[..^"/current".Length];
            string oldJournal = Canonical(intent.PreviousJournal!);
            string oldJournalPrefix = $"{oldCandidate}/publish-";
            if (!oldJournal.StartsWith(oldJournalPrefix, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(oldJournal[oldJournalPrefix.Length..], "N", out _)) throw new InvalidDataException("旧发布记录无效");
            var oldVersion = await versions.LoadCommittedByIdForWritingAsync(provider, root, old.VersionId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("旧版本不存在");
            if (oldVersion.Id == latest.Id) throw new InvalidDataException("新旧版本相同");
            ExperimentalFirstProjection.ValidatePlan(old, oldVersion, provider.ProviderType);
            byte[] oldBytes = JsonSerializer.SerializeToUtf8Bytes(old);
            await ExperimentalNextProjection.VerifyRecordAsync(provider, $"{oldCandidate}/receipt.json", oldBytes, token).ConfigureAwait(false);
            await ExperimentalNextProjection.VerifyRecordAsync(provider, $"{oldJournal}/committed.json", oldBytes, token).ConfigureAwait(false);
        }
        else if (old != null || intent.Archive != null || intent.PreviousJournal != null)
            throw new InvalidDataException("首次发布意图含意外的旧目录信息");

        async Task<bool> IsDirectoryAsync(string path)
        {
            var item = await provider.GetItemAsync(path, token).ConfigureAwait(false);
            if (item == null) return false;
            if (!item.IsDirectory) throw new InvalidDataException("发布路径存在同名文件");
            return true;
        }
        bool currentExists = await IsDirectoryAsync($"{root}/current").ConfigureAwait(false);
        bool candidateExists = await IsDirectoryAsync($"{candidate}/current").ConfigureAwait(false);
        bool archiveExists = archive != null && await IsDirectoryAsync($"{archive}/current").ConfigureAwait(false);
        Stage stage;
        if (intent.Kind == "first" && candidateExists && !currentExists)
        {
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{candidate}/current", intent.Next, token).ConfigureAwait(false);
            stage = Stage.FirstPrepared;
        }
        else if (intent.Kind == "first" && !candidateExists && currentExists)
        {
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{root}/current", intent.Next, token).ConfigureAwait(false);
            stage = Stage.FirstMoved;
        }
        else if (intent.Kind == "next" && candidateExists && currentExists && !archiveExists)
        {
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{root}/current", old!, token).ConfigureAwait(false);
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{candidate}/current", intent.Next, token).ConfigureAwait(false);
            stage = Stage.BeforeArchive;
        }
        else if (intent.Kind == "next" && candidateExists && !currentExists && archiveExists)
        {
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{archive}/current", old!, token).ConfigureAwait(false);
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{candidate}/current", intent.Next, token).ConfigureAwait(false);
            stage = Stage.OldArchived;
        }
        else if (intent.Kind == "next" && !candidateExists && currentExists && archiveExists)
        {
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{archive}/current", old!, token).ConfigureAwait(false);
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{root}/current", intent.Next, token).ConfigureAwait(false);
            stage = Stage.NewMoved;
        }
        else throw new InvalidDataException("当前、归档和候选目录状态无法唯一确定，拒绝推断");
        var committed = await provider.GetItemAsync($"{journal}/committed.json", token).ConfigureAwait(false);
        if (committed != null)
        {
            if (stage is not (Stage.FirstMoved or Stage.NewMoved)) throw new InvalidDataException("完成记录与目录状态不一致");
            await ExperimentalNextProjection.VerifyRecordAsync(provider, $"{journal}/committed.json", nextBytes, token).ConfigureAwait(false);
            stage = Stage.Committed;
        }
        return new Inspection(stage, intent.Next.VersionId, journal, held.DirectoryPath, held.OwnerSha256, archive);
    }

    internal static async Task<byte[]> ReadBoundedAsync(INasStorageProvider provider, string path, int maxBytes, CancellationToken token)
    {
        var item = await provider.GetItemAsync(path, token).ConfigureAwait(false);
        if (item == null || item.IsDirectory || item.Size < 1 || item.Size > maxBytes) throw new InvalidDataException("发布意图不存在或大小无效");
        byte[] bytes = new byte[(int)item.Size];
        using var stream = await provider.OpenReadAsync(path, token).ConfigureAwait(false);
        int offset = 0;
        while (offset < bytes.Length)
        {
            int count = await stream.ReadAsync(bytes.AsMemory(offset), token).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("发布意图读取不完整");
            offset += count;
        }
        byte[] extra = new byte[1];
        if (await stream.ReadAsync(extra.AsMemory(), token).ConfigureAwait(false) != 0) throw new InvalidDataException("发布意图长度不符");
        return bytes;
    }
}
