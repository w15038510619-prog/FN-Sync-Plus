using System.Text;
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
        string lockRoot = $"{root}/.fnsync/lock-generations";
        string path = "";
        foreach (string part in lockRoot.Split('/'))
        {
            path = path.Length == 0 ? part : $"{path}/{part}";
            var made = await provider.CreateDirectoryAsync(path, token).ConfigureAwait(false);
            if (!made.Success) throw new IOException($"无法准备锁目录: {made.ErrorMessage}");
        }
        // Preserve and verify every older generation; the format's D8 is a minimum width,
        // not a maximum. Sequential verification is deliberately conservative but expensive.
        for (long sequence = 1; sequence < long.MaxValue; sequence++)
        {
            token.ThrowIfCancellationRequested();
            string directory = $"{lockRoot}/{sequence:D8}";
            var result = await exclusive.TryCreateDirectoryExclusiveAsync(directory, token).ConfigureAwait(false);
            if (result == ExclusiveDirectoryResult.Created)
            {
                byte[] owner = Encoding.UTF8.GetBytes($"v1:{Guid.NewGuid():N}");
                await WriteAndVerifyMarkerAsync(provider, $"{directory}/owner", owner, token).ConfigureAwait(false);
                return new ExperimentalRemoteLease(provider, directory, owner);
            }

            byte[]? previousOwner = await ReadMarkerAsync(provider, $"{directory}/owner", token).ConfigureAwait(false);
            byte[]? released = await ReadMarkerAsync(provider, $"{directory}/released", token).ConfigureAwait(false);
            if (previousOwner == null || released == null) return null;
            if (!IsValidOwner(previousOwner) || !previousOwner.AsSpan().SequenceEqual(released))
                throw new InvalidDataException("锁的持有者或释放记录无效，拒绝继续");
        }
        throw new IOException("锁记录序号耗尽，需安全维护后才能继续");
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
