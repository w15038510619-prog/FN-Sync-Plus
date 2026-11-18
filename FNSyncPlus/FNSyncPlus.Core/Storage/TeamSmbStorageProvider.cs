using System.ComponentModel;
using System.Runtime.InteropServices;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Storage;

/// <summary>Versioned backup capabilities over an authenticated SMB filesystem root.</summary>
public sealed class TeamSmbStorageProvider : SmbStorageProvider, IExclusiveDirectoryProvider, IExperimentalCandidateDirectoryMover
{
    private readonly string _root;
    public TeamSmbStorageProvider(string root, Services.SmbAuthService? service = null) : base(service, root)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
    }

    private string Resolve(string relative)
    {
        if (!PathHelper.IsSafeRelativePath(relative)) throw new ArgumentException("共享内路径无效。");
        string path = Path.GetFullPath(Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("共享路径超出目标范围。");
        for (string? current = path; current != null && current.Length >= _root.Length; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("共享路径包含链接，请选择实际文件夹。");
        return PathHelper.EnsureLongPathPrefix(path);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string name, IntPtr securityAttributes);

    public Task<ExclusiveDirectoryResult> TryCreateDirectoryExclusiveAsync(string relativePath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string path = Resolve(relativePath);
        if (CreateDirectoryW(path, IntPtr.Zero)) return Task.FromResult(ExclusiveDirectoryResult.Created);
        int error = Marshal.GetLastWin32Error();
        if (error == 183 && Directory.Exists(path)) return Task.FromResult(ExclusiveDirectoryResult.AlreadyExists);
        throw new IOException("无法独占创建备份目录。", new Win32Exception(error));
    }

    public Task<VerifiedMoveOutcome> MoveCandidateDirectoryAsync(string backupRoot, string candidateDirectory, CancellationToken ct = default)
    {
        string prefix = backupRoot + "/.fnsync/projections/candidate-";
        if (!PathHelper.IsSafeRelativePath(backupRoot) || !candidateDirectory.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(candidateDirectory[prefix.Length..], "N", out _)) throw new IOException("候选目录不属于此备份。");
        return MoveAsync(candidateDirectory + "/current", backupRoot + "/current", ct);
    }

    public Task<VerifiedMoveOutcome> MoveCurrentToArchiveAsync(string backupRoot, string archiveDirectory, CancellationToken ct = default)
    {
        string prefix = backupRoot + "/.fnsync/projections/archive-";
        if (!PathHelper.IsSafeRelativePath(backupRoot) || !archiveDirectory.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(archiveDirectory[prefix.Length..], "N", out _)) throw new IOException("历史目录不属于此备份。");
        return MoveAsync(backupRoot + "/current", archiveDirectory + "/current", ct);
    }

    private Task<VerifiedMoveOutcome> MoveAsync(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string from = Resolve(source), to = Resolve(destination);
        if (Directory.Exists(to) || File.Exists(to)) return Task.FromResult(VerifiedMoveOutcome.DestinationExists);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        try
        {
            Directory.Move(from, to);
            if (Directory.Exists(from) || !Directory.Exists(to)) throw new IOException("目录移动结果无法确认。");
            return Task.FromResult(VerifiedMoveOutcome.Moved);
        }
        catch (IOException ex) { throw new RemoteMoveUncertainException("共享目录发布结果待核实，请保留备份并检查连接。", ex); }
    }
}
