using System.Text;
using System.Security.Cryptography;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Experimental fail-closed lock. No lock directory is deleted or automatically stolen.
/// A crashed owner blocks future acquisitions until a separately designed recovery procedure.
/// This is not enabled in the sync engine and relies on the server's exclusive mkdir semantics.
/// </summary>
public sealed class ExperimentalRemoteLease
{
    private readonly INasStorageProvider _provider;
    private readonly string _directory;
    private readonly byte[] _owner;
    private bool _released;
    internal string DirectoryPath => _directory;
    internal string OwnerSha256 => Convert.ToHexString(SHA256.HashData(_owner)).ToLowerInvariant();

    public sealed record Inspection(string DirectoryPath, string OwnerSha256);

    private ExperimentalRemoteLease(INasStorageProvider provider, string directory, byte[] owner)
    {
        _provider = provider;
        _directory = directory;
        _owner = owner;
    }

    public static async Task<ExperimentalRemoteLease?> TryAcquireAsync(INasStorageProvider provider, string target,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider is not IExclusiveDirectoryProvider exclusive)
            throw new NotSupportedException("存储提供程序不支持可区分冲突的独占目录创建");
        if (!PathHelper.IsSafeRelativePath(target)) throw new ArgumentException("锁目标必须是安全的相对路径", nameof(target));
        string root = PathHelper.NormalizeRelPath(target);
        var existingRoot = await provider.GetItemAsync(root, token).ConfigureAwait(false);
        if (existingRoot == null || !existingRoot.IsDirectory)
            throw new DirectoryNotFoundException("备份目标不存在或不是目录，拒绝为其创建锁");
        string lockRoot = $"{root}/.fnsync/lock-generations";
        string path = root;
        foreach (string part in new[] { ".fnsync", "lock-generations" })
        {
            path = $"{path}/{part}";
            var made = await provider.CreateDirectoryAsync(path, token).ConfigureAwait(false);
            if (!made.Success) throw new IOException($"无法准备锁目录: {made.ErrorMessage}");
        }
        // Enumerate once, but verify every older generation sequentially: fnOS rejected
        // concurrent marker downloads under load during real two-connection testing.
        var directories = await ListGenerationsAsync(provider, lockRoot, token).ConfigureAwait(false);
        foreach (var directory in directories)
        {
            token.ThrowIfCancellationRequested();
            byte[]? previousOwner = await ReadMarkerAsync(provider, $"{directory}/owner", token).ConfigureAwait(false);
            byte[]? released = await ReadMarkerAsync(provider, $"{directory}/released", token).ConfigureAwait(false);
            if (previousOwner == null || released == null)
            {
                long sequence = long.Parse(directory[(directory.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                await RejectLaterGenerationsAsync(provider, lockRoot, sequence, token).ConfigureAwait(false);
                return null;
            }
            if (!IsValidOwner(previousOwner) || !previousOwner.AsSpan().SequenceEqual(released))
                throw new InvalidDataException("锁的持有者或释放记录无效，拒绝继续");
        }
        long next = (long)directories.Count + 1;
        string nextDirectory = $"{lockRoot}/{next.ToString("D8", System.Globalization.CultureInfo.InvariantCulture)}";
        var created = await exclusive.TryCreateDirectoryExclusiveAsync(nextDirectory, token).ConfigureAwait(false);
        if (created != ExclusiveDirectoryResult.Created) return null;
        // A listing that missed a later generation must not authorize a new owner.
        await RejectLaterGenerationsAsync(provider, lockRoot, next, token).ConfigureAwait(false);
        byte[] newOwner = Encoding.UTF8.GetBytes($"v1:{Guid.NewGuid():N}");
        await WriteAndVerifyMarkerAsync(provider, $"{nextDirectory}/owner", newOwner, token).ConfigureAwait(false);
        return new ExperimentalRemoteLease(provider, nextDirectory, newOwner);
    }

    private static async Task<List<string>> ListGenerationsAsync(INasStorageProvider provider, string lockRoot, CancellationToken token)
    {
        var sequences = new SortedSet<long>();
        foreach (var item in await provider.ListEntriesAsync(lockRoot, token).ConfigureAwait(false))
        {
            if (!item.IsDirectory || !long.TryParse(item.Name, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long sequence) || sequence < 1 ||
                item.Name != sequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) || !sequences.Add(sequence))
                throw new InvalidDataException("锁目录包含无效或重复的代次记录");
        }
        var result = new List<string>(sequences.Count);
        long expected = 1;
        foreach (long sequence in sequences)
        {
            if (sequence != expected) throw new InvalidDataException("锁记录存在缺口，拒绝继续");
            result.Add($"{lockRoot}/{sequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture)}");
            expected++;
        }
        return result;
    }

    public async Task ReleaseAsync(CancellationToken token = default)
    {
        if (_released) return;
        byte[]? owner = await ReadMarkerAsync(_provider, $"{_directory}/owner", token).ConfigureAwait(false);
        if (owner == null || !_owner.AsSpan().SequenceEqual(owner))
            throw new InvalidDataException("锁持有者记录不匹配，拒绝释放");
        byte[]? existing = await ReadMarkerAsync(_provider, $"{_directory}/released", token).ConfigureAwait(false);
        if (existing != null)
        {
            if (!_owner.AsSpan().SequenceEqual(existing)) throw new InvalidDataException("锁释放记录不匹配");
        }
        else
        {
            await WriteAndVerifyMarkerAsync(_provider, $"{_directory}/released", _owner, token).ConfigureAwait(false);
        }
        _released = true;
    }

    /// <summary>Inspects the first unreleased generation without creating or releasing any lock.</summary>
    public static async Task<Inspection?> InspectAsync(INasStorageProvider provider, string target, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!PathHelper.IsSafeRelativePath(target)) throw new ArgumentException("锁目标必须是安全的相对路径", nameof(target));
        string current = "";
        foreach (string part in $"{PathHelper.NormalizeRelPath(target)}/.fnsync/lock-generations".Split('/'))
        {
            current = current.Length == 0 ? part : $"{current}/{part}";
            var item = await provider.GetItemAsync(current, token).ConfigureAwait(false);
            if (item == null) return null;
            if (!item.IsDirectory) throw new InvalidDataException("锁目录路径不是文件夹");
        }
        for (long sequence = 1; sequence < long.MaxValue; sequence++)
        {
            token.ThrowIfCancellationRequested();
            string directory = $"{current}/{sequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture)}";
            var item = await provider.GetItemAsync(directory, token).ConfigureAwait(false);
            if (item == null)
            {
                await RejectLaterGenerationsAsync(provider, current, sequence, token).ConfigureAwait(false);
                return null;
            }
            if (!item.IsDirectory) throw new InvalidDataException("锁代次不是文件夹");
            byte[]? owner = await ReadMarkerAsync(provider, $"{directory}/owner", token).ConfigureAwait(false);
            if (owner == null || !IsValidOwner(owner)) throw new InvalidDataException("锁持有者记录缺失或无效");
            byte[]? released = await ReadMarkerAsync(provider, $"{directory}/released", token).ConfigureAwait(false);
            if (released == null)
            {
                await RejectLaterGenerationsAsync(provider, current, sequence, token).ConfigureAwait(false);
                return new Inspection(directory, Convert.ToHexString(SHA256.HashData(owner)).ToLowerInvariant());
            }
            if (!owner.AsSpan().SequenceEqual(released)) throw new InvalidDataException("锁释放记录不匹配");
        }
        throw new IOException("锁记录序号耗尽");
    }

    private static async Task RejectLaterGenerationsAsync(INasStorageProvider provider, string lockRoot,
        long expectedSequence, CancellationToken token)
    {
        var seen = new HashSet<long>();
        foreach (var item in await provider.ListEntriesAsync(lockRoot, token).ConfigureAwait(false))
        {
            if (!item.IsDirectory || !long.TryParse(item.Name, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long sequence) || sequence < 1 ||
                item.Name != sequence.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) || !seen.Add(sequence))
                throw new InvalidDataException("锁目录包含无效或重复的代次记录");
            if (sequence > expectedSequence)
                throw new InvalidDataException("锁记录存在缺口或较晚代次，拒绝继续");
        }
    }

    // Only a recovery workflow with explicit confirmation that the owner has stopped may call this.
    // No fnOS fencing primitive is known: a live owner could otherwise still write concurrently.
    internal static async Task ReleaseStoppedOwnerAsync(INasStorageProvider provider, string target,
        Inspection expected, CancellationToken token = default)
    {
        var held = await InspectAsync(provider, target, token).ConfigureAwait(false);
        if (held == null || held.DirectoryPath != expected.DirectoryPath || held.OwnerSha256 != expected.OwnerSha256)
            throw new InvalidDataException("未释放锁已变化，拒绝恢复释放");
        byte[]? owner = await ReadMarkerAsync(provider, $"{held.DirectoryPath}/owner", token).ConfigureAwait(false);
        if (owner == null || !IsValidOwner(owner) ||
            !Convert.ToHexString(SHA256.HashData(owner)).Equals(held.OwnerSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("锁持有者记录已变化，拒绝恢复释放");
        byte[]? released = await ReadMarkerAsync(provider, $"{held.DirectoryPath}/released", token).ConfigureAwait(false);
        if (released != null) throw new InvalidDataException("锁释放状态发生变化");
        await WriteAndVerifyMarkerAsync(provider, $"{held.DirectoryPath}/released", owner, token).ConfigureAwait(false);
    }

    private static bool IsValidOwner(byte[] bytes)
    {
        if (bytes.Length != 35) return false;
        string value = Encoding.UTF8.GetString(bytes);
        return value.StartsWith("v1:", StringComparison.Ordinal) && Guid.TryParseExact(value[3..], "N", out _);
    }

    private static async Task<byte[]?> ReadMarkerAsync(INasStorageProvider provider, string path, CancellationToken token)
    {
        var info = await provider.GetItemAsync(path, token).ConfigureAwait(false);
        if (info == null) return null;
        if (info.IsDirectory || info.Size != 35) throw new InvalidDataException("锁记录大小无效");
        using var stream = await provider.OpenReadAsync(path, token).ConfigureAwait(false);
        byte[] bytes = new byte[36];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) break;
            offset += read;
        }
        if (offset != 35) throw new InvalidDataException("锁记录读取不完整");
        return bytes[..35];
    }

    private static async Task WriteAndVerifyMarkerAsync(INasStorageProvider provider, string path, byte[] bytes, CancellationToken token)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var result = await provider.UploadFileAsync(path, stream, bytes.Length, DateTime.UtcNow, false, null, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!result.Success) throw new IOException($"锁记录写入失败: {result.ErrorMessage}");
        var readback = await ReadMarkerAsync(provider, path, token).ConfigureAwait(false);
        if (readback == null || !bytes.AsSpan().SequenceEqual(readback)) throw new InvalidDataException("锁记录回读不一致");
    }
}
