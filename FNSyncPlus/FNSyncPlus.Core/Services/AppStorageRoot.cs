using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
///  * The override must be a fully-qualified, unambiguous absolute path before normalization.
///  * Relative paths, drive-relative paths (e.g. C:foo), and relative navigations (.. or .) are rejected.
///  * Drive roots (e.g. C:\) and filesystem roots are strictly forbidden as test roots.
///  * The override must NEVER point to production APPDATA, LOCALAPPDATA, their parent directories
///    (e.g. AppData, UserProfile, Users) or any production FNSyncPlus directory.
///  * Reparse points, directory junctions, and symlinks are resolved and checked for physical target overlaps.
///  * Subdirectories under an existing test root are verified not to be reparse points pointing into production.
///  * Path casing differences and trailing slashes are strictly normalized.
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
            if (_overrideRoot != null && !_overrideRoot.Equals(NormalizePath(root), StringComparison.OrdinalIgnoreCase))
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

    /// <summary>Normalizes a path by expanding to full path and stripping trailing separators.</summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>All FNSyncPlus production paths of the current user (read-only reference list).</summary>
    public static IReadOnlyList<string> EnumerateProductionPaths()
    {
        return new[]
        {
            NormalizePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FNSyncPlus")),
            NormalizePath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FNSyncPlus")),
        };
    }

    /// <summary>
    /// All production roots and their parent directories that test roots are strictly forbidden from pointing to.
    /// Includes APPDATA, LOCALAPPDATA, AppData, UserProfile, and Users.
    /// </summary>
    public static IReadOnlyList<string> EnumerateForbiddenProductionRoots()
    {
        var list = new List<string>();
        foreach (var p in EnumerateProductionPaths())
            list.Add(p);

        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(roaming)) list.Add(NormalizePath(roaming));

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local)) list.Add(NormalizePath(local));

        if (!string.IsNullOrWhiteSpace(roaming))
        {
            var p = Directory.GetParent(NormalizePath(roaming));
            if (p != null) list.Add(NormalizePath(p.FullName));
        }
        if (!string.IsNullOrWhiteSpace(local))
        {
            var p = Directory.GetParent(NormalizePath(local));
            if (p != null) list.Add(NormalizePath(p.FullName));
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            list.Add(NormalizePath(userProfile));
            var p = Directory.GetParent(NormalizePath(userProfile));
            if (p != null) list.Add(NormalizePath(p.FullName));
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Resolves physical path by following reparse points (symlinks, junctions) for all existing components.
    /// Strictly fail-closed: unresolvable, cyclic, broken, or inaccessible reparse points throw InvalidOperationException.
    /// </summary>
    public static string ResolvePhysicalPath(string path)
    {
        string normalized = NormalizePath(path);
        try
        {
            string? root = Path.GetPathRoot(normalized);
            if (string.IsNullOrEmpty(root)) return normalized;

            string current = root;
            string relative = normalized.Substring(root.Length);
            string[] parts = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NormalizePath(root) };
            int hops = 0;
            const int maxHops = 64;

            foreach (string part in parts)
            {
                string next = Path.Combine(current, part);
                FileAttributes attrs = default;
                bool exists = false;
                try
                {
                    attrs = File.GetAttributes(next);
                    exists = true;
                }
                catch (FileNotFoundException) { exists = false; }
                catch (DirectoryNotFoundException) { exists = false; }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"无法读取路径属性（无法确认安全即视为不安全）：{next} ({ex.Message})", ex);
                }

                if (exists && attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    hops++;
                    if (hops > maxHops)
                    {
                        throw new InvalidOperationException(
                            $"检测到重解析点跳转次数超限或可能存在循环解析（无法确认安全即视为不安全）：{path}");
                    }

                    FileSystemInfo? target;
                    try
                    {
                        var di = new DirectoryInfo(next);
                        target = di.ResolveLinkTarget(returnFinalTarget: true);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"无法安全解析重解析点物理路径（无法确认安全即视为不安全）：{next} ({ex.Message})", ex);
                    }

                    if (target == null || !target.Exists)
                    {
                        throw new InvalidOperationException(
                            $"无法解析重解析点物理目标或目标不存在（无法确认安全即视为不安全）：{next}");
                    }

                    string targetNormalized = NormalizePath(target.FullName);
                    if (!visited.Add(targetNormalized))
                    {
                        throw new InvalidOperationException(
                            $"检测到重解析点循环引用（无法确认安全即视为不安全）：{next} -> {targetNormalized}");
                    }

                    current = targetNormalized;
                    continue;
                }
                current = next;
            }
            return NormalizePath(current);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"解析路径物理目标异常（无法确认安全即视为不安全）：{path} ({ex.Message})", ex);
        }
    }

    /// <summary>True when <paramref name="path"/> lies inside any production FNSyncPlus directory.</summary>
    public static bool IsProductionPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string full = NormalizePath(path);
            if (IsLexicalProductionPath(full)) return true;

            string physical = ResolvePhysicalPath(full);
            if (!string.Equals(physical, full, StringComparison.OrdinalIgnoreCase) && IsLexicalProductionPath(physical))
                return true;
        }
        catch
        {
            // 无法确认安全即视为不安全（fail-closed）：拦截潜在的危险路径
            return true;
        }
        return false;
    }

    private static bool IsLexicalProductionPath(string full)
    {
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

    /// <summary>
    /// Validates and prepares a candidate root directory for test data isolation.
    /// Strictly verifies path qualification, rejects relative navigation, checks against production roots
    /// and parent directories, inspects reparse points and junctions, and verifies subdirectories.
    /// </summary>
    public static string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"{OverrideEnvironmentVariable} 路径不能为空");

        string raw = root.Trim();

        // 1. 规范化前检查是否为完全限定路径（拒绝相对路径、根驱动器相关或不明确路径）
        if (!Path.IsPathFullyQualified(raw))
            throw new InvalidOperationException($"{OverrideEnvironmentVariable} 必须是完全限定的绝对路径（拒绝相对路径、驱动器相对或不明确路径）：{root}");

        // 2. 拒绝包含相对段（. 或 ..）的不明确路径
        if (raw.StartsWith(".") || raw.Contains(@"\..\") || raw.Contains(@"/../") ||
            raw.EndsWith(@"\..") || raw.EndsWith(@"/..") ||
            raw.Contains(@"\.\") || raw.Contains(@"/./") ||
            raw.EndsWith(@"\.") || raw.EndsWith(@"/."))
        {
            throw new InvalidOperationException($"{OverrideEnvironmentVariable} 包含相对导航段（. 或 ..），拒绝使用不明确的路径：{root}");
        }

        // 3. 规范化路径与末尾分隔符
        string full = NormalizePath(Path.GetFullPath(raw));

        // 4. 驱动器根目录或文件系统根目录检查（如 C:\、D:\）
        string? rootDir = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(rootDir) && NormalizePath(rootDir).Equals(full, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{OverrideEnvironmentVariable} 不能是驱动器根目录或文件系统根目录：{full}");
        }

        // 5. 校验生产目录及父目录重叠
        CheckPathOverlapAgainstForbiddenRoots(full, "词法路径");

        // 6. 检查符号链接、目录联接点及重解析点带来的真实物理路径重叠风险
        string physicalFull = ResolvePhysicalPath(full);
        if (!string.Equals(physicalFull, full, StringComparison.OrdinalIgnoreCase))
        {
            CheckPathOverlapAgainstForbiddenRoots(physicalFull, "重解析目标路径");
        }

        // 7. 确保已存在的隔离目录中没有子目录重解析点指向生产配置
        VerifyNoSubdirectoryReparsePointsToProduction(full);

        Directory.CreateDirectory(full);
        return full;
    }

    private static void CheckPathOverlapAgainstForbiddenRoots(string pathToCheck, string pathDescription)
    {
        var forbiddenRoots = EnumerateForbiddenProductionRoots();
        var productionPaths = EnumerateProductionPaths();

        foreach (var forbidden in forbiddenRoots)
        {
            if (pathToCheck.Equals(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{OverrideEnvironmentVariable} 禁止指向生产目录或其父目录（{pathDescription}）：{pathToCheck} vs {forbidden}");
            }
            if (forbidden.StartsWith(pathToCheck + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{OverrideEnvironmentVariable} 包含生产数据目录（作为其父目录被禁止，{pathDescription}）：{pathToCheck} vs {forbidden}");
            }
        }

        foreach (var production in productionPaths)
        {
            if (pathToCheck.StartsWith(production + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{OverrideEnvironmentVariable} 不得位于生产数据目录之内（{pathDescription}）：{pathToCheck} vs {production}");
            }
        }
    }

    private static void VerifyNoSubdirectoryReparsePointsToProduction(string root)
    {
        if (!Directory.Exists(root)) return;
        var forbidden = EnumerateForbiddenProductionRoots();
        var production = EnumerateProductionPaths();
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string currentNorm = NormalizePath(current.FullName);
            if (!visited.Add(currentNorm))
            {
                throw new InvalidOperationException(
                    $"{OverrideEnvironmentVariable} 隔离目录存在循环目录或循环链接（无法确认安全即视为不安全）：{currentNorm}");
            }

            FileSystemInfo[] subEntries;
            try
            {
                subEntries = current.GetFileSystemInfos();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{OverrideEnvironmentVariable} 无法枚举隔离目录中的项（无法确认安全即视为不安全）：{current.FullName} ({ex.Message})", ex);
            }

            foreach (var sub in subEntries)
            {
                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    FileSystemInfo? target;
                    try
                    {
                        target = sub.ResolveLinkTarget(returnFinalTarget: true);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"{OverrideEnvironmentVariable} 隔离目录中的重解析点解析异常（无法确认安全即视为不安全）：{sub.FullName} ({ex.Message})", ex);
                    }

                    if (target == null || !target.Exists)
                    {
                        throw new InvalidOperationException(
                            $"{OverrideEnvironmentVariable} 隔离目录中的重解析点目标不存在或无法解析（无法确认安全即视为不安全）：{sub.FullName}");
                    }

                    string targetFull = NormalizePath(target.FullName);
                    string rootNorm = NormalizePath(root);
                    if (targetFull.Equals(rootNorm, StringComparison.OrdinalIgnoreCase) ||
                        rootNorm.StartsWith(targetFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                        visited.Contains(targetFull))
                    {
                        throw new InvalidOperationException(
                            $"{OverrideEnvironmentVariable} 隔离目录中的重解析点指向自身或上级目录构成循环引用（无法确认安全即视为不安全）：{sub.FullName} -> {targetFull}");
                    }

                    if (IsForbiddenOrProductionTarget(targetFull, forbidden, production))
                    {
                        throw new InvalidOperationException(
                            $"{OverrideEnvironmentVariable} 隔离目录中的子目录重解析点指向生产路径，已阻止：{sub.FullName} -> {targetFull}");
                    }
                }
                else if (sub is DirectoryInfo subDir)
                {
                    stack.Push(subDir);
                }
            }
        }
    }

    private static bool IsForbiddenOrProductionTarget(string targetFull, IReadOnlyList<string> forbidden, IReadOnlyList<string> production)
    {
        foreach (var prod in production)
        {
            if (targetFull.Equals(prod, StringComparison.OrdinalIgnoreCase) ||
                targetFull.StartsWith(prod + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (var f in forbidden)
        {
            if (targetFull.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                f.StartsWith(targetFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
