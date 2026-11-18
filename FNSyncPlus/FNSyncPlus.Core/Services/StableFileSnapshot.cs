using System.Security.Cryptography;

namespace FNSyncPlus.Core.Services;

/// <summary>A private, verified disk snapshot of saved bytes shared by an editor.</summary>
public static class StableFileSnapshot
{
    public static bool IsOfficeOwnerFile(string name) => name.StartsWith("~$", StringComparison.Ordinal) &&
        new[] { ".xlsx", ".xls", ".xlsm", ".docx", ".doc", ".pptx", ".ppt" }
            .Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);

    public static async Task<FileStream> OpenAsync(string source, CancellationToken token)
    {
        string directory = Path.Combine(AppStorageRoot.TempRoot(), "FNSyncPlus-Snapshots");
        Directory.CreateDirectory(directory);
        var snapshot = new FileStream(Path.Combine(directory, Guid.NewGuid().ToString("N")),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
        try
        {
            var before = new FileInfo(source);
            long length = before.Length;
            DateTime modified = before.LastWriteTimeUtc;
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(snapshot, token).ConfigureAwait(false);
            await snapshot.FlushAsync(token).ConfigureAwait(false);
            snapshot.Position = 0;
            byte[] copiedHash = await SHA256.HashDataAsync(snapshot, token).ConfigureAwait(false);
            input.Position = 0;
            byte[] currentHash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
            before.Refresh();
            if (snapshot.Length != length || input.Length != length || !before.Exists ||
                before.Length != length || before.LastWriteTimeUtc != modified || !copiedHash.AsSpan().SequenceEqual(currentHash))
                throw new IOException("文件正在保存，请稍后重试。");
            snapshot.Position = 0;
            return snapshot;
        }
        catch { await snapshot.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
