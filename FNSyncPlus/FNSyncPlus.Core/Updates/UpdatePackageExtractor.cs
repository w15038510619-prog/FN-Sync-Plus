using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FNSyncPlus.Core.Updates;

public sealed record ExtractOutcome(bool Ok, string PayloadDirectory, string Error, int FileCount, long TotalBytes)
{
    public static ExtractOutcome Fail(string error) => new(false, "", error, 0, 0);
}

/// <summary>
/// Extracts a verified update package into a private staging directory. The archive is untrusted
/// input even after its SHA-256 matches the signed manifest, so every entry is path-checked:
/// rooted paths, drive letters, "..", absolute paths, link entries and oversized archives are
/// rejected instead of extracted. A single top-level folder is flattened so callers always get a
/// directory containing the application files.
/// </summary>
public static class UpdatePackageExtractor
{
    /// <summary>Hard cap on uncompressed payload size; a real package is roughly 200-400 MiB.</summary>
    public const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;
    public const int MaxEntries = 20000;

    public static async Task<ExtractOutcome> ExtractAsync(string packagePath, string destinationRoot,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            return ExtractOutcome.Fail("更新包不存在");
        if (string.IsNullOrWhiteSpace(destinationRoot))
            return ExtractOutcome.Fail("解压目标目录无效");
        string staging = Path.Combine(Path.GetFullPath(destinationRoot), $"extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = await Task.Run(() => ZipFile.OpenRead(packagePath), token).ConfigureAwait(false);
            if (archive.Entries.Count == 0) return ExtractOutcome.Fail("更新包为空");
            if (archive.Entries.Count > MaxEntries) return ExtractOutcome.Fail("更新包条目过多，拒绝解压");
            long total = 0;
            int files = 0;
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                string? relative = SafeRelativePath(entry.FullName);
                if (relative == null) return ExtractOutcome.Fail($"更新包包含不安全的路径：{entry.FullName}");
                if (relative.Length == 0) continue; // directory entry at root
                if (IsLink(entry)) return ExtractOutcome.Fail($"更新包包含链接条目，拒绝解压：{entry.FullName}");
                total += entry.Length;
                if (total > MaxUncompressedBytes) return ExtractOutcome.Fail("更新包解压后体积超出上限，拒绝解压");
                string target = Path.Combine(staging, relative);
                string fullTarget = Path.GetFullPath(target);
                if (!fullTarget.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return ExtractOutcome.Fail($"更新包路径逃逸出目标目录：{entry.FullName}");
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(fullTarget);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
                await using (var input = entry.Open())
                await using (var output = new FileStream(fullTarget, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                files++;
            }
            if (files == 0) return ExtractOutcome.Fail("更新包不包含任何文件");
            string payload = FlattenSingleRoot(staging);
            if (!File.Exists(Path.Combine(payload, "FNSyncPlus.WinUI.exe")))
                return ExtractOutcome.Fail("更新包缺少主程序 FNSyncPlus.WinUI.exe");
            return new ExtractOutcome(true, payload, "", files, total);
        }
        catch (OperationCanceledException)
        {
            TryDelete(staging);
            throw;
        }
        catch (InvalidDataException ex)
        {
            TryDelete(staging);
            return ExtractOutcome.Fail($"更新包不是有效的 ZIP：{ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(staging);
            return ExtractOutcome.Fail($"解压更新包失败：{ex.Message}");
        }
    }

    /// <summary>Normalizes a zip entry name, or returns null when it must be rejected.</summary>
    internal static string? SafeRelativePath(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;
        string name = entryName.Replace('\\', '/');
        if (name.StartsWith('/') || name.StartsWith("//")) return null;
        if (name.Length >= 2 && name[1] == ':') return null;   // drive-letter rooted
        if (name.Contains('\0')) return null;
        var parts = new List<string>();
        foreach (string part in name.Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") return null;
            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static bool IsLink(ZipArchiveEntry entry)
    {
        // Unix symlink: external attributes hold the file mode in the high 16 bits.
        int mode = (int)((entry.ExternalAttributes >> 16) & 0xFFFF);
        const int symlinkFlag = 0xA000;
        return (mode & 0xF000) == symlinkFlag;
    }

    /// <summary>If the archive has exactly one top-level directory, return it instead of the wrapper.</summary>
    private static string FlattenSingleRoot(string staging)
    {
        var directories = Directory.GetDirectories(staging);
        var files = Directory.GetFiles(staging);
        return directories.Length == 1 && files.Length == 0 ? directories[0] : staging;
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
