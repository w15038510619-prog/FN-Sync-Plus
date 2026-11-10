using System.Text.Json;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Finds a uniquely linked, fully committed physical publication in an isolated backup.
/// A missing or ambiguous record fails closed. This is not a cross-device fencing protocol.
/// </summary>
public sealed class ExperimentalProjectionDiscovery
{
    public sealed record Result(ExperimentalPhysicalSnapshot.Result Plan, ExperimentalFirstProjection.Published Publication);
    private sealed record Entry(string Journal, ProjectionIntent Intent);

    public async Task<Result> DiscoverAsync(INasStorageProvider provider, string backupRoot, CancellationToken token = default)
        => await DiscoverCoreAsync(provider, backupRoot, false, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("无法唯一确定已发布目录");

    /// <summary>Only for a task durably marked as awaiting its first physical publication.</summary>
    public Task<Result?> DiscoverFirstPublicationAsync(INasStorageProvider provider, string backupRoot, CancellationToken token = default)
        => DiscoverCoreAsync(provider, backupRoot, true, token);

    private async Task<Result?> DiscoverCoreAsync(INasStorageProvider provider, string backupRoot, bool allowUnpublished, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!PathHelper.IsSafeRelativePath(backupRoot)) throw new ArgumentException("备份目录路径无效");
        string root = Canonical(provider, backupRoot);
        var lease = await ExperimentalRemoteLease.TryAcquireAsync(provider, root, token).ConfigureAwait(false)
            ?? throw new IOException("备份被其他写入者占用或存在未释放的锁");
        Exception? failure = null;
        try
        {
            return await DiscoverRecordsAsync(provider, root, allowUnpublished, token).ConfigureAwait(false);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            using var releaseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await lease.ReleaseAsync(releaseTimeout.Token).ConfigureAwait(false); }
            catch (Exception ex) { throw new IOException("查找发布记录后释放锁失败", failure == null ? ex : new AggregateException(failure, ex)); }
        }
    }

    internal async Task<Result> DiscoverUnderHeldLeaseAsync(INasStorageProvider provider, string backupRoot,
        ExperimentalRemoteLease.Inspection expected, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(expected);
        if (!PathHelper.IsSafeRelativePath(backupRoot)) throw new ArgumentException("备份目录路径无效");
        string root = Canonical(provider, backupRoot);
        async Task CheckLeaseAsync()
        {
            var actual = await ExperimentalRemoteLease.InspectAsync(provider, root, token).ConfigureAwait(false);
            if (actual == null || !Canonical(provider, actual.DirectoryPath).Equals(Canonical(provider, expected.DirectoryPath), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(actual.OwnerSha256, expected.OwnerSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("检查期间未释放锁发生变化");
        }
        await CheckLeaseAsync().ConfigureAwait(false);
        var result = await DiscoverRecordsAsync(provider, root, false, token, expected).ConfigureAwait(false)
            ?? throw new InvalidDataException("无法唯一确定已发布目录");
        var latest = await new VersionedManifestStore().LoadForWritingAsync(provider, root, token).ConfigureAwait(false);
        if (latest == null || latest.Id != result.Plan.VersionId)
            throw new InvalidDataException("最新清单与当前物理目录版本不一致，拒绝释放锁");
        await CheckLeaseAsync().ConfigureAwait(false);
        return result;
    }

    private static string Canonical(INasStorageProvider provider, string path) =>
        provider is FnOsPersonalStorageProvider fn ? fn.NormalizeRelativePath(path) : PathHelper.NormalizeRelPath(path);

    private async Task<Result?> DiscoverRecordsAsync(INasStorageProvider provider, string root, bool allowUnpublished, CancellationToken token,
        ExperimentalRemoteLease.Inspection? heldLease = null)
    {
            string Canonical(string path) => ExperimentalProjectionDiscovery.Canonical(provider, path);
            string projections = $"{root}/.fnsync/projections";
            var versions = new VersionedManifestStore();
            if (await versions.LoadForWritingAsync(provider, root, token).ConfigureAwait(false) == null)
                throw new InvalidDataException("备份没有已提交版本");
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var projectionsItem = await provider.GetItemAsync(projections, token).ConfigureAwait(false);
            if (projectionsItem != null && !projectionsItem.IsDirectory) throw new InvalidDataException("发布记录路径不是目录");
            var projectionItems = projectionsItem == null
                ? Array.Empty<NasStorageItem>()
                : (await provider.ListEntriesAsync(projections, token).ConfigureAwait(false)).ToArray();
            foreach (var item in projectionItems)
            {
                if (!item.Name.StartsWith("candidate-", StringComparison.Ordinal)) continue;
                if (!item.IsDirectory || !Guid.TryParseExact(item.Name["candidate-".Length..], "N", out _))
                    throw new InvalidDataException("候选目录名称无效");
                string candidate = $"{projections}/{item.Name}";
                foreach (var journalItem in await provider.ListEntriesAsync(candidate, token).ConfigureAwait(false))
                {
                    if (!journalItem.Name.StartsWith("publish-", StringComparison.Ordinal)) continue;
                    if (!journalItem.IsDirectory || !Guid.TryParseExact(journalItem.Name["publish-".Length..], "N", out _))
                        throw new InvalidDataException("发布记录目录名称无效");
                    string journal = $"{candidate}/{journalItem.Name}";
                    byte[] intentBytes = await ExperimentalProjectionRecoveryInspector.ReadBoundedAsync(provider, $"{journal}/intent.json", 32 * 1024 * 1024, token).ConfigureAwait(false);
                    var intent = JsonSerializer.Deserialize<ProjectionIntent>(intentBytes) ?? throw new InvalidDataException("发布意图无效");
                    if (intent.Schema != 1 || (intent.Kind != "first" && intent.Kind != "next") || intent.Next == null ||
                        !PathHelper.IsSafeRelativePath(intent.Next.CandidateRoot) ||
                        !Canonical(intent.Next.CandidateRoot).Equals($"{candidate}/current", StringComparison.OrdinalIgnoreCase) ||
                        !PathHelper.IsSafeRelativePath(intent.LeaseDirectory) || intent.OwnerSha256?.Length != 64 || !intent.OwnerSha256.All(Uri.IsHexDigit))
                        throw new InvalidDataException("发布意图不完整或不属于候选目录");
                    if (heldLease != null && Canonical(intent.LeaseDirectory).Equals(Canonical(heldLease.DirectoryPath), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("该锁关联物理发布记录，请使用发布恢复流程");
                    byte[] planBytes = JsonSerializer.SerializeToUtf8Bytes(intent.Next);
                    await ExperimentalNextProjection.VerifyRecordAsync(provider, $"{candidate}/receipt.json", planBytes, token).ConfigureAwait(false);
                    await ExperimentalNextProjection.VerifyRecordAsync(provider, $"{journal}/committed.json", planBytes, token).ConfigureAwait(false);
                    var version = await versions.LoadCommittedByIdForWritingAsync(provider, root, intent.Next.VersionId, token).ConfigureAwait(false)
                        ?? throw new InvalidDataException("发布版本不存在");
                    ExperimentalFirstProjection.ValidatePlan(intent.Next, version, provider.ProviderType);
                    if (!entries.TryAdd(journal, new Entry(journal, intent))) throw new InvalidDataException("发布记录重复");
                }
            }
            if (allowUnpublished && entries.Count == 0)
            {
                if (projectionItems.Any(item => !item.Name.StartsWith("candidate-", StringComparison.Ordinal)) ||
                    await provider.GetItemAsync($"{root}/current", token).ConfigureAwait(false) != null)
                    throw new InvalidDataException("存在无法确认的物理目录或发布记录，拒绝首次发布");
                return null;
            }
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int firstCount = 0;
            foreach (var entry in entries.Values)
            {
                var intent = entry.Intent;
                if (intent.Kind == "first")
                {
                    if (intent.Previous != null || intent.PreviousJournal != null || intent.Archive != null)
                        throw new InvalidDataException("首次发布包含意外的旧版本");
                    firstCount++;
                    continue;
                }
                if (intent.Previous == null || intent.PreviousJournal == null || intent.Archive == null ||
                    !PathHelper.IsSafeRelativePath(intent.PreviousJournal) || !PathHelper.IsSafeRelativePath(intent.Archive))
                    throw new InvalidDataException("增量发布记录不完整");
                string previousJournal = Canonical(intent.PreviousJournal), archive = Canonical(intent.Archive);
                string prefix = $"{projections}/archive-";
                if (!archive.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(archive[prefix.Length..], "N", out _) ||
                    !entries.TryGetValue(previousJournal, out var previous) || !referenced.Add(previousJournal) ||
                    !JsonSerializer.SerializeToUtf8Bytes(intent.Previous).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(previous.Intent.Next)) ||
                    intent.Previous.VersionId == intent.Next.VersionId)
                    throw new InvalidDataException("增量发布链无法唯一确认");
                await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{archive}/current", intent.Previous, token).ConfigureAwait(false);
            }
            var heads = entries.Values.Where(e => !referenced.Contains(e.Journal)).ToArray();
            if (firstCount != 1 || heads.Length != 1 || referenced.Count != entries.Count - 1)
                throw new InvalidDataException("无法唯一确定已发布目录");
            var head = heads[0];
            // Count all predecessors to reject a disconnected cycle.
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var cursor = head; ;)
            {
                if (!visited.Add(cursor.Journal)) throw new InvalidDataException("发布链存在循环");
                if (cursor.Intent.Kind == "first") break;
                cursor = entries[Canonical(cursor.Intent.PreviousJournal!)];
            }
            if (visited.Count != entries.Count) throw new InvalidDataException("发布链存在分支或孤立记录");
            await ExperimentalFirstProjection.VerifyTreeAsync(provider, $"{root}/current", head.Intent.Next, token).ConfigureAwait(false);
            return new Result(head.Intent.Next, new ExperimentalFirstProjection.Published(head.Intent.Next.VersionId, $"{root}/current", head.Journal));
    }
}
