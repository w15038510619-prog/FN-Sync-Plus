using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;

namespace FNSyncPlus.Core.Services;

public sealed record CapacityLargeFile(string OriginalRel, long Size, string Sha256, bool IsHistory);

public sealed record CapacityReport(
    long CurrentLogicalBytes,
    long HistoryLogicalBytes,
    int CurrentFileCount,
    int HistoryEntryCount,
    long PhysicalObjectBytes,
    int PhysicalObjectCount,
    long UnreferencedBytes,
    int UnreferencedCount,
    int VersionCount,
    DateTimeOffset? LatestVersionAt,
    IReadOnlyList<CapacityLargeFile> LargestFiles,
    bool Consistent,
    IReadOnlyList<string> Notes)
{
    public long LogicalTotalBytes => CurrentLogicalBytes + HistoryLogicalBytes;

    public string ToSummary()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"当前文件：{CurrentFileCount} 个，{Format(CurrentLogicalBytes)}");
        text.AppendLine($"历史版本：{HistoryEntryCount} 条，{Format(HistoryLogicalBytes)}");
        text.AppendLine($"物理对象：{PhysicalObjectCount} 个，{Format(PhysicalObjectBytes)}");
        text.AppendLine($"未被任何版本引用：{UnreferencedCount} 个，{Format(UnreferencedBytes)}");
        text.AppendLine($"已提交版本数：{VersionCount}" + (LatestVersionAt is { } at ? $"，最近 {at.ToLocalTime():yyyy-MM-dd HH:mm}" : ""));
        if (Notes.Count > 0)
        {
            text.AppendLine("提示：");
            foreach (string note in Notes) text.AppendLine($"  - {note}");
        }
        return text.ToString().TrimEnd();
    }

    public static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:F2} GiB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:F1} MiB",
        >= 1024 => $"{bytes / 1024d:F1} KiB",
        _ => $"{bytes} B"
    };
}

public sealed record RetentionPolicy
{
    public int KeepVersions { get; init; } = 5;
    public int KeepDays { get; init; } = 30;
    public long? MaxHistoryBytes { get; init; }

    public RetentionPolicy Validate()
    {
        if (KeepVersions < 1) throw new ArgumentOutOfRangeException(nameof(KeepVersions), "至少保留一个版本");
        if (KeepDays < 0) throw new ArgumentOutOfRangeException(nameof(KeepDays));
        if (MaxHistoryBytes is < 0) throw new ArgumentOutOfRangeException(nameof(MaxHistoryBytes));
        return this;
    }
}

public sealed record RetentionVersion(long Sequence, string Id, DateTimeOffset CommittedAt, long ObjectBytes,
    bool Keep, string Reason);

public sealed record RetentionPreview(
    IReadOnlyList<RetentionVersion> Versions,
    long ReclaimableBytes,
    int ReclaimableObjects,
    bool Safe,
    string Summary,
    IReadOnlyList<string> Blockers);

/// <summary>
/// Read-only capacity accounting for one versioned backup target plus a retention *preview*.
///
/// Safety rules enforced here:
///  * nothing is deleted, moved or rewritten — the preview only reports what a future, separately
///    reviewed cleanup could reclaim;
///  * the newest committed version is never proposed for removal;
///  * an object is only considered reclaimable when no committed version references it;
///  * damaged or unreadable version records make the preview unsafe instead of guessing.
/// Automatic deletion stays disabled until reference integrity and restore safety are proven.
/// </summary>
public sealed class BackupCapacityAnalyzer
{
    private readonly VersionedManifestStore _versions;

    public BackupCapacityAnalyzer(VersionedManifestStore? versions = null) => _versions = versions ?? new VersionedManifestStore();

    public async Task<CapacityReport> AnalyzeAsync(INasStorageProvider provider, string target,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var versions = await _versions.LoadAllCommittedAsync(provider, target, token).ConfigureAwait(false);
        var notes = new List<string>();
        var latest = versions.Count > 0 ? versions[^1] : null;
        long currentBytes = 0, historyBytes = 0;
        int currentCount = 0, historyCount = 0;
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var large = new List<CapacityLargeFile>();
        if (latest != null)
        {
            foreach (var file in latest.Manifest.Files)
            {
                currentBytes += file.Size;
                currentCount++;
                references.Add(Normalize(file.StoredRel));
                large.Add(new CapacityLargeFile(file.OriginalRel, file.Size, file.Sha256, false));
            }
            foreach (var entry in latest.Manifest.History)
            {
                historyBytes += entry.Size;
                historyCount++;
                references.Add(Normalize(entry.StoredRel));
                large.Add(new CapacityLargeFile(entry.OriginalRel, entry.Size, entry.Sha256, true));
            }
        }
        else notes.Add("目标没有可读取的版本化清单，容量统计仅包含物理对象。");

        // Every committed version's references count: an object is unreferenced only if no version uses it.
        foreach (var snapshot in versions)
        {
            foreach (var file in snapshot.Manifest.Files) references.Add(Normalize(file.StoredRel));
            foreach (var entry in snapshot.Manifest.History) references.Add(Normalize(entry.StoredRel));
        }

        var objects = await ListObjectsAsync(provider, target, token).ConfigureAwait(false);
        long physicalBytes = 0, unreferencedBytes = 0;
        int unreferenced = 0;
        foreach (var item in objects)
        {
            physicalBytes += item.Bytes;
            if (!references.Contains(item.StoredRel))
            {
                unreferenced++;
                unreferencedBytes += item.Bytes;
            }
        }
        bool consistent = await VerifyReferencesExistAsync(provider, target, references, notes, token).ConfigureAwait(false);
        if (unreferenced > 0)
            notes.Add($"有 {unreferenced} 个对象未被任何已提交版本引用（合计 {CapacityReport.Format(unreferencedBytes)}）。" +
                      "它们可能是失败备份的残留，程序不会自动删除，仅供人工核对。");
        if (versions.Count == 0 && objects.Count > 0)
            notes.Add("物理对象存在但没有有效的版本记录，请先在恢复中心核验，不要直接清理。");

        return new CapacityReport(currentBytes, historyBytes, currentCount, historyCount, physicalBytes, objects.Count,
            unreferencedBytes, unreferenced, versions.Count, latest?.Manifest.UpdatedAt,
            large.OrderByDescending(f => f.Size).Take(20).ToList(), consistent, notes);
    }

    /// <summary>
    /// Computes what a retention policy <i>would</i> remove. Returns Safe=false with blockers when
    /// the version history cannot be trusted; it never mutates the backup.
    /// </summary>
    public async Task<RetentionPreview> PreviewRetentionAsync(INasStorageProvider provider, string target,
        RetentionPolicy policy, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        IReadOnlyList<VersionedManifestStore.Snapshot> snapshots;
        try { snapshots = await _versions.LoadAllCommittedAsync(provider, target, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return new RetentionPreview(Array.Empty<RetentionVersion>(), 0, 0, false,
                "版本记录不完整，无法安全评估保留策略。", new[] { ex.Message });
        }
        var blockers = new List<string>();
        if (snapshots.Count == 0)
            return new RetentionPreview(Array.Empty<RetentionVersion>(), 0, 0, false,
                "没有已提交版本，无需清理。", blockers);

        var objects = await ListObjectsAsync(provider, target, token).ConfigureAwait(false);
        var objectsByOwner = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots)
        {
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in snapshot.Manifest.Files) owned.Add(Normalize(file.StoredRel));
            foreach (var entry in snapshot.Manifest.History) owned.Add(Normalize(entry.StoredRel));
            objectsByOwner[snapshot.Id] = owned.ToList();
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-policy.KeepDays);
        var ordered = snapshots.OrderByDescending(s => s.Sequence).ToList();
        var result = new List<RetentionVersion>();
        for (int index = 0; index < ordered.Count; index++)
        {
            var snapshot = ordered[index];
            long bytes = objects.Where(o => objectsByOwner[snapshot.Id].Contains(o.StoredRel, StringComparer.OrdinalIgnoreCase))
                .Sum(o => o.Bytes);
            bool newest = index == 0;
            bool withinCount = index < policy.KeepVersions;
            bool withinDays = snapshot.Manifest.UpdatedAt >= cutoff;
            bool keep = newest || withinCount || withinDays;
            string reason = newest ? "最新版本，永不删除"
                : withinCount ? $"保留最近 {policy.KeepVersions} 个版本"
                : withinDays ? $"在 {policy.KeepDays} 天保留期内"
                : "超出保留策略";
            result.Add(new RetentionVersion(snapshot.Sequence, snapshot.Id, snapshot.Manifest.UpdatedAt, bytes, keep, reason));
        }

        var removable = result.Where(v => !v.Keep).ToList();
        if (removable.Count == 0)
            return new RetentionPreview(result, 0, 0, true,
                "按当前策略没有可清理的版本。", blockers);

        var retainedIds = result.Where(v => v.Keep).Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retainedReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots.Where(s => retainedIds.Contains(s.Id)))
            foreach (string reference in objectsByOwner[snapshot.Id]) retainedReferences.Add(reference);

        var removableReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots.Where(s => !retainedIds.Contains(s.Id)))
            foreach (string reference in objectsByOwner[snapshot.Id]) removableReferences.Add(reference);

        var reclaimable = removableReferences.Where(r => !retainedReferences.Contains(r)).ToList();
        long reclaimableBytes = objects.Where(o => reclaimable.Contains(o.StoredRel, StringComparer.OrdinalIgnoreCase))
            .Sum(o => o.Bytes);
        if (policy.MaxHistoryBytes is { } limit)
        {
            long historyBytes = objects.Where(o => retainedReferences.Contains(o.StoredRel, StringComparer.OrdinalIgnoreCase))
                .Sum(o => o.Bytes);
            if (historyBytes <= limit)
                return new RetentionPreview(result, 0, 0, true,
                    $"历史占用 {CapacityReport.Format(historyBytes)} 未超过上限 {CapacityReport.Format(limit)}，无需清理。", blockers);
        }
        if (!result.Any(v => v.Keep && v.Sequence == snapshots.Max(s => s.Sequence)))
            blockers.Add("策略意外选中了最新版本，已阻止。");

        bool safe = blockers.Count == 0;
        string summary;
        if (!safe)
            summary = "保留策略评估未通过安全检查，未生成任何删除建议。";
        else if (reclaimableBytes == 0)
            summary = $"可清理 {removable.Count} 个旧版本记录，但它们引用的历史对象仍被当前清单的历史记录引用，" +
                      "删除版本记录不会释放空间。历史内容清理功能尚未启用。这是只读预览，程序不会删除任何内容。";
        else
            summary = $"可清理 {removable.Count} 个旧版本，预计释放 {CapacityReport.Format(reclaimableBytes)}" +
                      $"（{reclaimable.Count} 个对象）。这是只读预览，程序不会自动删除任何内容。";
        return new RetentionPreview(result, reclaimableBytes, reclaimable.Count, safe, summary, blockers);
    }

    private sealed record ObjectEntry(string StoredRel, long Bytes, DateTime LastModifiedUtc);

    private static async Task<List<ObjectEntry>> ListObjectsAsync(INasStorageProvider provider, string target,
        CancellationToken token)
    {
        var result = new List<ObjectEntry>();
        string objectsRoot = $"{Normalize(target)}/.fnsync/objects";
        IReadOnlyList<NasStorageItem> entries;
        try { entries = await provider.ListEntriesAsync(objectsRoot, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return result; }
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            // Objects are normally a single content-addressed file (obj-<guid>); migrated backups may
            // store them as a directory tree instead, so both shapes are accounted for.
            if (entry.IsDirectory)
                result.AddRange(await ListFilesRecursiveAsync(provider, $"{objectsRoot}/{entry.Name}", token).ConfigureAwait(false));
            else
                result.Add(new ObjectEntry(Normalize(entry.RelativePath), entry.Size, entry.LastModifiedUtc));
        }
        return result;
    }

    private static async Task<List<ObjectEntry>> ListFilesRecursiveAsync(INasStorageProvider provider, string path,
        CancellationToken token)
    {
        var result = new List<ObjectEntry>();
        IReadOnlyList<NasStorageItem> entries;
        try { entries = await provider.ListEntriesAsync(path, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException) { return result; }
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
                result.AddRange(await ListFilesRecursiveAsync(provider, $"{path}/{entry.Name}", token).ConfigureAwait(false));
            else
                result.Add(new ObjectEntry(Normalize(entry.RelativePath), entry.Size, entry.LastModifiedUtc));
        }
        return result;
    }

    private static async Task<bool> VerifyReferencesExistAsync(INasStorageProvider provider, string target,
        HashSet<string> references, List<string> notes, CancellationToken token)
    {
        var missing = new List<string>();
        foreach (string reference in references)
        {
            token.ThrowIfCancellationRequested();
            string path = $"{Normalize(target)}/{reference}";
            bool exists;
            try { exists = await provider.ExistsAsync(path, token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException) { exists = false; }
            if (!exists) missing.Add(reference);
        }
        if (missing.Count == 0) return true;
        notes.Add($"有 {missing.Count} 个清单引用的对象在存储中缺失，容量统计与恢复可能不完整；请先在恢复中心核验。");
        return false;
    }

    private static string Normalize(string relative) => (relative ?? "").Replace('\\', '/').Trim('/');
}
