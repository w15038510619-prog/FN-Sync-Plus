using System.Text.Json;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>Read-only search for the one journal bound to the currently held lease.</summary>
public sealed class ExperimentalProjectionRecoveryLocator
{
    public async Task<ExperimentalProjectionRecoveryInspector.Inspection?> LocateAsync(
        INasStorageProvider provider, string backupRoot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!PathHelper.IsSafeRelativePath(backupRoot)) throw new ArgumentException("备份目录路径无效");
        string Canonical(string path) => provider is FnOsPersonalStorageProvider fn ? fn.NormalizeRelativePath(path) : PathHelper.NormalizeRelPath(path);
        string root = Canonical(backupRoot), projections = $"{root}/.fnsync/projections";
        var held = await ExperimentalRemoteLease.InspectAsync(provider, root, token).ConfigureAwait(false);
        if (held == null) return null;
        var projectionItem = await provider.GetItemAsync(projections, token).ConfigureAwait(false);
        if (projectionItem == null || !projectionItem.IsDirectory)
            throw new InvalidDataException("存在未释放锁，但找不到物理发布记录；请保留现场并人工检查");
        var matching = new List<string>();
        foreach (var item in await provider.ListEntriesAsync(projections, token).ConfigureAwait(false))
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
                byte[] bytes = await ExperimentalProjectionRecoveryInspector.ReadBoundedAsync(provider, $"{journal}/intent.json", 32 * 1024 * 1024, token).ConfigureAwait(false);
                var intent = JsonSerializer.Deserialize<ProjectionIntent>(bytes) ?? throw new InvalidDataException("发布意图无效");
                if (intent.Schema != 1 || !PathHelper.IsSafeRelativePath(intent.LeaseDirectory) ||
                    intent.OwnerSha256?.Length != 64 || !intent.OwnerSha256.All(Uri.IsHexDigit))
                    throw new InvalidDataException("发布记录缺少有效的锁绑定，不能推断恢复状态");
                if (Canonical(intent.LeaseDirectory).Equals(Canonical(held.DirectoryPath), StringComparison.OrdinalIgnoreCase) &&
                    intent.OwnerSha256.Equals(held.OwnerSha256, StringComparison.OrdinalIgnoreCase)) matching.Add(journal);
            }
        }
        if (matching.Count != 1) throw new InvalidDataException("未能唯一定位当前锁对应的发布记录；请保留现场并人工检查");
        return await new ExperimentalProjectionRecoveryInspector().InspectAsync(provider, root, matching[0], token).ConfigureAwait(false);
    }
}
