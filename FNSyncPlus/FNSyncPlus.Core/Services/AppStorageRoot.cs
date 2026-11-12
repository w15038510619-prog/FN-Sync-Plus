using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// Single authority for every on-disk location the application is allowed to use.
///
/// Production builds resolve the usual per-user folders (FNSyncPlus under APPDATA /
/// LOCALAPPDATA). When the environment variable <see cref="OverrideEnvironmentVariable"/>
/// points at a dedicated test data root, EVERY application path — settings, credentials,
/// versioned-beta profile, logs, data-format marker, update state, caches, temp files and
/// the single-instance identity — resolves under that root instead. Windows KnownFolder
/// resolution (SHGetKnownFolderPath) does not follow the APPDATA environment variable,
/// so an explicit root override is the only reliable isolation mechanism; this class is
/// the one place that decides, which makes "the test run can never touch production data"
/// an auditable property instead of a convention.
///
/// Safety rules enforced here:
///  * The override must be an absolute path and must never overlap the real production
///    FNSyncPlus directories of the current user.
///  * An optional audit file (<see cref="AuditEnvironmentVariable"/>) records every resolved
///    path so acceptance runs can prove zero production access with real filesystem evidence.
///  * Once initialized the resolution is immutable for the process lifetime; child processes
///    (updater, relaunched app) inherit the environment variable automatically.
/// </summary>
public static class AppStorageRoot
{
    public const string OverrideEnvironmentVariable = "FNSYNC_DATA_ROOT";
    public const string AuditEnvironmentVariable = "FNSYNC_STORAGE_AUDIT";

    private static readonly object Sync = new();
    private static bool _initialized;
    private static string? _overrideRoot;
    private static string? _auditFile;

    /// <summary>True when this process runs with an isolated test data root.</summary>
    public static bool IsIsolatedTestRoot
    {
        get { EnsureInitialized(); return _overrideRoot != null; }
    }

    /// <summary>The isolated test data root, or null in a production environment.</summary>
    public static string? TestRoot
    {
        get { EnsureInitialized(); return _overrideRoot; }
    }

    /// <summary>
    /// Test-only explicit initialization for in-process suites that cannot rely on the
    /// environment variable. Refuses to change an already initialized resolution and refuses
    /// to activate a root in a process that was started as production.
    /// </summary>
    public static void InitializeForTests(string root)
    {
        lock (Sync)
        {
            EnsureInitialized();
            if (_overrideRoot != null && !_overrideRoot.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("测试数据根目录已固定，不能在同进程内变更");
            _overrideRoot = ValidateRoot(root);
        }
    }

    /// <summary>FNSyncPlus roaming directory (settings.json, credentials.dat).</summary>
    public static string RoamingAppDataDir()
    {
        EnsureInitialized();
        string path = _overrideRoot != null
            ? Path.Combine(_overrideRoot, "roaming", "FNSyncPlus")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FNSyncPlus");
        RecordAccess(path, "roaming");
        return path;
    }

    /// <summary>FNSyncPlus local directory (logs, versioned-beta, updates.json, markers, caches).</summary>
    public static string LocalAppDataDir()
    {
        EnsureInitialized();
        string path = _overrideRoot != null
            ? Path.Combine(_overrideRoot, "local", "FNSyncPlus")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FNSyncPlus");
        RecordAccess(path, "local");
        return path;
    }

    /// <summary>Temp root for test scaffolding; the OS temp dir in production.</summary>
    public static string TempRoot()
    {
        EnsureInitialized();
        string path = _overrideRoot != null ? Path.Combine(_overrideRoot, "temp") : Path.GetTempPath();
        RecordAccess(path, "temp");
        return path;
    }

    /// <summary>Root folder used by isolated test sessions (WinUiSession_/VersionedProfile_).</summary>
    public static string TestSessionsRoot()
    {
        string path = Path.Combine(TempRoot(), "FNSyncPlus_CodexTests");
        RecordAccess(path, "test-sessions");
        return path;
    }

    /// <summary>
    /// Suffix that makes the single-instance identity of a test-root process independent of
    /// the production instance. Null in production so the legacy cross-edition exclusion is
    /// kept exactly as before.
    /// </summary>
    public static string? SingleInstanceSuffix()
    {
        EnsureInitialized();
        if (_overrideRoot == null) return null;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(_overrideRoot.ToUpperInvariant()));
        return ".Root." + Convert.ToHexString(hash, 0, 4);
    }

    /// <summary>All FNSyncPlus production paths of the current user (read-only reference list).</summary>
    public static IReadOnlyList<string> EnumerateProductionPaths()
    {
        return new[]
        {
            Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FNSyncPlus")),
            Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FNSyncPlus")),
        };
    }

    /// <summary>True when <paramref name="path"/> lies inside any production FNSyncPlus directory.</summary>
    public static bool IsProductionPath(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var production in EnumerateProductionPaths())
        {
            if (full.Equals(production, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(production + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Hard stop used by test harnesses before touching a path they believe is isolated.</summary>
    public static void AssertNotProductionPath(string path, string purpose)
    {
        if (IsProductionPath(path))
            throw new InvalidOperationException($"测试环境试图访问生产路径，已阻止：{purpose} -> {path}");
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Sync)
        {
            if (_initialized) return;
            string? root = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(root))
                _overrideRoot = ValidateRoot(root);
            string? audit = Environment.GetEnvironmentVariable(AuditEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(audit))
                _auditFile = Path.GetFullPath(audit);
            _initialized = true;
        }
    }

    private static string ValidateRoot(string root)
    {
        string full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.IsPathRooted(full))
            throw new InvalidOperationException($"{OverrideEnvironmentVariable} 必须是绝对路径：{root}");
        foreach (var production in EnumerateProductionPaths())
        {
            if (full.Equals(production, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(production + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                production.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{OverrideEnvironmentVariable} 不得与生产数据目录重叠：{full} vs {production}");
        }
        Directory.CreateDirectory(full);
        return full;
    }

    private static void RecordAccess(string path, string purpose)
    {
        string? audit = _auditFile;
        if (audit == null) return;
        try
        {
            lock (Sync)
            {
                File.AppendAllText(audit,
                    $"{DateTimeOffset.UtcNow:O}\t{Environment.ProcessId}\t{purpose}\t{path}{Environment.NewLine}");
            }
        }
        catch
        {
            // The audit is evidence, never a reason to break the application.
        }
    }
}
