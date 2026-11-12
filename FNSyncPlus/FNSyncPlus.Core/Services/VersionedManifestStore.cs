using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Experimental immutable manifest protocol. Restore discovery can read it; task writes remain isolated.
/// Its correctness depends on complete directory listings and read-after-write consistency.
/// </summary>
public sealed class VersionedManifestStore
{
    private const string VersionDirectory = ".fnsync/manifest-versions";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed class Receipt
    {
        public long Sequence { get; set; }
        public string Id { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    public sealed record Snapshot(Manifest Manifest, long Sequence, string Id);
    public sealed record RestoreInspection(Snapshot? Snapshot, bool HasInvalidRecords);

    private static string DirectoryPath(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return VersionDirectory;
        if (!PathHelper.IsSafeRelativePath(target)) throw new ArgumentException("版本化清单目标必须是安全的相对路径", nameof(target));
        return $"{PathHelper.NormalizeRelPath(target)}/{VersionDirectory}";
    }

    private static string ReceiptName(long sequence, string id) => $"commit-{sequence:D20}-{id}.json";
    private static string CandidateName(string id) => $"candidate-{id}.json";

    private static async Task<bool> DirectoryExistsAsync(INasStorageProvider provider, string directory, CancellationToken token)
    {
        string current = "";
        foreach (string part in directory.Split('/'))
        {
            current = current.Length == 0 ? part : $"{current}/{part}";
            var item = await provider.GetItemAsync(current, token).ConfigureAwait(false);
            if (item == null) return false;
            if (!item.IsDirectory) throw new IOException($"清单目录路径不是文件夹: {current}");
        }
        return true;
    }

    private static async Task CreateDirectoryTreeAsync(INasStorageProvider provider, string directory, CancellationToken token)
    {
        string current = "";
        foreach (string part in directory.Split('/'))
        {
            current = current.Length == 0 ? part : $"{current}/{part}";
            var result = await provider.CreateDirectoryAsync(current, token).ConfigureAwait(false);
            if (!result.Success) throw new IOException($"无法创建版本目录: {result.ErrorMessage}");
        }
    }

    private static bool TryParseReceiptName(string name, out long sequence, out string id)
    {
        sequence = 0;
        id = "";
        if (name.Length != 65 || !name.StartsWith("commit-", StringComparison.Ordinal) ||
            name[27] != '-' || !name.EndsWith(".json", StringComparison.Ordinal)) return false;
        if (!long.TryParse(name.AsSpan(7, 20), NumberStyles.None, CultureInfo.InvariantCulture, out sequence) || sequence < 1)
            return false;
        id = name.Substring(28, 32);
        return Guid.TryParseExact(id, "N", out _);
    }

    private static async Task<byte[]> ReadBytesAsync(INasStorageProvider provider, string path, CancellationToken token)
    {
        using var stream = await provider.OpenReadAsync(path, token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, token).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static Manifest ParseManifest(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.EnumerateObject().Any(p => p.Name.Equals("files", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array))
            throw new InvalidDataException("版本化清单缺少有效的 files 数组");
        return JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions) is { Files: not null } manifest
            ? manifest : throw new InvalidDataException("版本化清单无效");
    }

    private async Task<(Snapshot? snapshot, long maxSequence, bool hasReceipts, bool hasInvalidReceipts)> ScanAsync(
        INasStorageProvider provider, string target, CancellationToken token)
    {
        string directory = DirectoryPath(target);
        if (!await DirectoryExistsAsync(provider, directory, token).ConfigureAwait(false)) return (null, 0, false, false);
        var entries = await provider.ListEntriesAsync(directory, token).ConfigureAwait(false);
        var receipts = new List<(long sequence, string id, string name)>();
        bool malformedReceipts = false;
        foreach (var entry in entries)
            if (!entry.IsDirectory && entry.Name.StartsWith("commit-", StringComparison.Ordinal))
            {
                if (TryParseReceiptName(entry.Name, out long sequence, out string id)) receipts.Add((sequence, id, entry.Name));
                else malformedReceipts = true;
            }

        long maxSequence = receipts.Count == 0 ? 0 : receipts.Max(x => x.sequence);
        Snapshot? selected = null;
        bool hasInvalidReceipts = malformedReceipts;
        foreach (var group in receipts.OrderByDescending(x => x.sequence).GroupBy(x => x.sequence))
        {
            var valid = new List<Snapshot>();
            foreach (var item in group)
            {
                token.ThrowIfCancellationRequested();
                byte[] receiptBytes = await ReadBytesAsync(provider, $"{directory}/{item.name}", token).ConfigureAwait(false);
                Receipt? receipt;
                try { receipt = JsonSerializer.Deserialize<Receipt>(receiptBytes, JsonOptions); }
                catch (JsonException) { hasInvalidReceipts = true; continue; }
                if (receipt == null || receipt.Sequence != item.sequence || receipt.Id != item.id ||
                    receipt.Sha256 == null || receipt.Sha256.Length != 64 || !receipt.Sha256.All(Uri.IsHexDigit))
                { hasInvalidReceipts = true; continue; }
                string candidatePath = $"{directory}/{CandidateName(item.id)}";
                if (!await provider.ExistsAsync(candidatePath, token).ConfigureAwait(false)) { hasInvalidReceipts = true; continue; }
                byte[] bytes = await ReadBytesAsync(provider, candidatePath, token).ConfigureAwait(false);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), receipt.Sha256, StringComparison.OrdinalIgnoreCase))
                { hasInvalidReceipts = true; continue; }
                try { valid.Add(new Snapshot(ParseManifest(bytes), item.sequence, item.id)); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException) { hasInvalidReceipts = true; }
            }
            if (valid.Count > 1)
                throw new InvalidDataException($"同一清单版本存在多个有效提交 ({group.Key})，拒绝自动选择");
            if (valid.Count == 1 && selected == null) selected = valid[0];
        }
        return (selected, maxSequence, receipts.Count > 0 || malformedReceipts, hasInvalidReceipts);
    }

    public async Task<Snapshot?> LoadAsync(INasStorageProvider provider, string target, CancellationToken token = default)
    {
        var (snapshot, _, hasReceipts, _) = await ScanAsync(provider, target, token).ConfigureAwait(false);
        if (snapshot == null && hasReceipts) throw new InvalidDataException("版本化清单提交均无效，拒绝返回空清单");
        return snapshot;
    }

    /// <summary>
    /// Lists every committed version, oldest first, for read-only capacity and retention analysis.
    /// Damaged or ambiguous records make the whole listing unreliable, so this throws instead of
    /// returning a partial history that a caller might mistake for complete.
    /// </summary>
    public async Task<IReadOnlyList<Snapshot>> LoadAllCommittedAsync(INasStorageProvider provider, string target,
        CancellationToken token = default)
    {
        string directory = DirectoryPath(target);
        if (!await DirectoryExistsAsync(provider, directory, token).ConfigureAwait(false))
            return Array.Empty<Snapshot>();
        var entries = await provider.ListEntriesAsync(directory, token).ConfigureAwait(false);
        var receipts = new List<(long sequence, string id, string name)>();
        foreach (var entry in entries)
        {
            if (entry.IsDirectory || !entry.Name.StartsWith("commit-", StringComparison.Ordinal)) continue;
            if (!TryParseReceiptName(entry.Name, out long sequence, out string id))
                throw new InvalidDataException($"存在无法解析的提交记录：{entry.Name}");
            receipts.Add((sequence, id, entry.Name));
        }
        var snapshots = new List<Snapshot>();
        foreach (var item in receipts.OrderBy(x => x.sequence))
        {
            token.ThrowIfCancellationRequested();
            byte[] receiptBytes = await ReadBytesAsync(provider, $"{directory}/{item.name}", token).ConfigureAwait(false);
            Receipt? receipt;
            try { receipt = JsonSerializer.Deserialize<Receipt>(receiptBytes, JsonOptions); }
            catch (JsonException) { throw new InvalidDataException($"提交记录无效：{item.name}"); }
            if (receipt == null || receipt.Sequence != item.sequence || receipt.Id != item.id ||
                receipt.Sha256 is not { Length: 64 })
                throw new InvalidDataException($"提交记录内容与文件名不一致：{item.name}");
            string candidatePath = $"{directory}/{CandidateName(item.id)}";
            if (!await provider.ExistsAsync(candidatePath, token).ConfigureAwait(false))
                throw new InvalidDataException($"提交记录缺少对应清单：{item.name}");
            byte[] bytes = await ReadBytesAsync(provider, candidatePath, token).ConfigureAwait(false);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), receipt.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"提交清单校验失败：{item.name}");
            try { snapshots.Add(new Snapshot(ParseManifest(bytes), item.sequence, item.id)); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                throw new InvalidDataException($"提交清单内容无效：{item.name}");
            }
        }
        return snapshots;
    }

    public async Task<RestoreInspection> InspectForRestoreAsync(INasStorageProvider provider, string target, CancellationToken token = default)
    {
        var (snapshot, _, hasReceipts, hasInvalidRecords) = await ScanAsync(provider, target, token).ConfigureAwait(false);
        if (snapshot == null && hasReceipts) throw new InvalidDataException("版本化清单提交均无效，拒绝返回空清单");
        return new RestoreInspection(snapshot, hasInvalidRecords);
    }

    /// <summary>A readable fallback is not sufficient for writing when any commit record is invalid.</summary>
    public async Task<Snapshot?> LoadForWritingAsync(INasStorageProvider provider, string target, CancellationToken token = default)
    {
        var (snapshot, _, hasReceipts, hasInvalidReceipts) = await ScanAsync(provider, target, token).ConfigureAwait(false);
        if (hasInvalidReceipts || (snapshot == null && hasReceipts))
            throw new InvalidDataException("存在无效的清单提交，拒绝继续写入");
        return snapshot;
    }

    /// <summary>
    /// Reads one committed historical version after rejecting damaged or ambiguous records.
    /// A caller that will mutate the backup must hold the writer lease throughout validation and mutation.
    /// </summary>
    public async Task<Snapshot?> LoadCommittedByIdForWritingAsync(INasStorageProvider provider, string target,
        string id, CancellationToken token = default)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("版本标识无效", nameof(id));
        var (_, _, hasReceipts, invalidReceipts) = await ScanAsync(provider, target, token).ConfigureAwait(false);
        if (invalidReceipts) throw new InvalidDataException("存在无效的清单提交，拒绝读取用于写入的历史版本");
        if (!hasReceipts) return null;
        string directory = DirectoryPath(target);
        var entries = await provider.ListEntriesAsync(directory, token).ConfigureAwait(false);
        var matches = new List<(long Sequence, string Name)>();
        foreach (var item in entries)
            if (!item.IsDirectory && TryParseReceiptName(item.Name, out long sequence, out string receiptId) &&
                string.Equals(receiptId, id, StringComparison.Ordinal)) matches.Add((sequence, item.Name));
        if (matches.Count == 0) return null;
        if (matches.Count != 1) throw new InvalidDataException("同一版本标识对应多个提交，拒绝选择");
        byte[] receiptBytes = await ReadBytesAsync(provider, $"{directory}/{matches[0].Name}", token).ConfigureAwait(false);
        Receipt? receipt = JsonSerializer.Deserialize<Receipt>(receiptBytes, JsonOptions);
        if (receipt == null || receipt.Sequence != matches[0].Sequence || receipt.Id != id ||
            receipt.Sha256?.Length != 64 || !receipt.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("历史版本提交记录无效");
        byte[] bytes = await ReadBytesAsync(provider, $"{directory}/{CandidateName(id)}", token).ConfigureAwait(false);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), receipt.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("历史版本清单校验失败");
        return new Snapshot(ParseManifest(bytes), receipt.Sequence, id);
    }

    public async Task<Snapshot> SaveAsync(INasStorageProvider provider, string target, Manifest manifest,
        string? expectedPreviousId, CancellationToken token = default)
    {
        var (previous, maxSequence, hasReceipts, hasInvalidReceipts) = await ScanAsync(provider, target, token).ConfigureAwait(false);
        if (previous == null && hasReceipts) throw new InvalidDataException("版本化清单提交均无效，拒绝写入");
        if (hasInvalidReceipts)
            throw new InvalidDataException("存在无效的清单提交，拒绝继续写入");
        if (!string.Equals(previous?.Id, expectedPreviousId, StringComparison.Ordinal))
            throw new IOException("清单版本已变化，拒绝覆盖并发提交");
        if (maxSequence == long.MaxValue) throw new InvalidDataException("清单版本序号已耗尽");
        long sequence = maxSequence + 1;
        string id = Guid.NewGuid().ToString("N");
        string directory = DirectoryPath(target);
        await CreateDirectoryTreeAsync(provider, directory, token).ConfigureAwait(false);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        _ = ParseManifest(bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string candidatePath = $"{directory}/{CandidateName(id)}";
        await UploadAndVerifyAsync(provider, candidatePath, bytes, token).ConfigureAwait(false);
        var (beforeCommit, _, _, invalidBeforeCommit) = await ScanAsync(provider, target, token).ConfigureAwait(false);
        if (invalidBeforeCommit) throw new InvalidDataException("准备期间发现无效的清单提交，拒绝写入");
        if (!string.Equals(beforeCommit?.Id, expectedPreviousId, StringComparison.Ordinal))
            throw new IOException("清单版本在准备期间发生变化，拒绝提交");
        byte[] receipt = JsonSerializer.SerializeToUtf8Bytes(new Receipt { Sequence = sequence, Id = id, Sha256 = hash }, JsonOptions);
        await UploadAndVerifyAsync(provider, $"{directory}/{ReceiptName(sequence, id)}", receipt, token).ConfigureAwait(false);
        var selected = await LoadAsync(provider, target, token).ConfigureAwait(false);
        if (selected?.Id != id) throw new IOException("无法确认新清单已成为唯一活动版本");
        return selected;
    }

    private static async Task UploadAndVerifyAsync(INasStorageProvider provider, string path, byte[] bytes, CancellationToken token)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var result = await provider.UploadFileAsync(path, stream, bytes.Length, DateTime.UtcNow, false, null, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.Success) throw new IOException($"版本化清单写入失败: {result.ErrorMessage}");
        byte[] actual = await ReadBytesAsync(provider, path, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(actual)))
            throw new InvalidDataException("版本化清单回读校验失败");
    }
}
