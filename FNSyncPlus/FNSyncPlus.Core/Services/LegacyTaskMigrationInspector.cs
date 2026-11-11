using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

public enum LegacyMigrationClassification
{
    /// <summary>Can be migrated into a fresh, separate versioned backup directory.</summary>
    Supported,
    /// <summary>Old manifest is not v3/v4 or uses fields the new engine cannot prove.</summary>
    UnsupportedFormat,
    /// <summary>Old manifest JSON is damaged or contains unsafe/duplicate entries.</summary>
    CorruptMetadata,
    /// <summary>The backup target cannot be identified unambiguously (missing folder, conflicting roots).</summary>
    AmbiguousTarget,
    /// <summary>Local source path is missing, or source and target overlap.</summary>
    InvalidSource,
    /// <summary>Task account identity cannot be confirmed (UID unknown/mismatch, account name mismatch).</summary>
    AccountUnverified,
    /// <summary>Target already contains a versioned manifest; nothing to migrate.</summary>
    AlreadyVersioned,
    /// <summary>Another writer holds the lease, or an unclear lock/intent record exists.</summary>
    LockedByAnotherWriter
}

/// <summary>A read-only finding for one legacy task. Never enables or modifies the task.</summary>
public sealed record LegacyTaskInspection(
    SyncTask Task,
    LegacyMigrationClassification Classification,
    string Summary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Blockers,
    bool SourceExists,
    bool SourceTargetOverlap,
    string ManifestFormat,
    int FileCount,
    int HistoryCount,
    int DirectoryCount,
    long TotalBytes,
    bool TargetHasVersionedManifest,
    bool TargetHasLegacyManifest,
    bool HistoryProvable);

public sealed record LegacyTaskInventory(
    string? LegacySettingsPath,
    bool LegacySettingsReadable,
    string LegacySettingsError,
    IReadOnlyList<LegacyTaskInspection> Tasks,
    IReadOnlyList<string> OrphanFindings)
{
    public LegacyTaskInventory WithoutTask(string taskId) => this with
    {
        Tasks = Tasks.Where(t => !string.Equals(t.Task.TaskId, taskId, StringComparison.Ordinal)).ToList()
    };
}

public sealed record LegacyMigrationPreview(
    string TaskId,
    string TaskName,
    string SourceFolder,
    string LegacyTarget,
    string NewTarget,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Warnings,
    long VerifiedBytes,
    int FileCount,
    int HistoryCount,
    int DirectoryCount,
    bool CanExecute);

public sealed record LegacyMigrationExecution(
    string TaskId,
    string NewTarget,
    string ManifestId,
    long Sequence,
    DateTimeOffset CompletedAt,
    bool OldManifestUnchanged,
    bool OldFilesUnchanged);

public sealed record LegacyMigrationVerification(
    string TaskId,
    string NewTarget,
    string RestoreDirectory,
    int ExpectedFiles,
    int ExpectedHistory,
    int ExpectedDirectories,
    int RestoredFiles,
    int VerifiedFiles,
    int RestoredHistory,
    int VerifiedHistory,
    bool DirectoriesRestored,
    bool OldTargetUnchanged,
    bool Passed,
    IReadOnlyList<string> Failures);

/// <summary>
/// Interactive migration of legacy (pre-versioned) tasks and backups. Each step is explicit and
/// reversible by design: inspection is read-only, execution writes only to a fresh target, and the
/// migrated task is handed back disabled until the caller verifies the restore.
/// Nothing in this class deletes, renames or rewrites any legacy manifest or file.
/// </summary>
public sealed class LegacyTaskMigrationInspector
{
    private const int MaxManifestBytes = 64 * 1024 * 1024;

    private readonly VersionedManifestStore _versions;

    public LegacyTaskMigrationInspector(VersionedManifestStore? versions = null)
    {
        _versions = versions ?? new VersionedManifestStore();
    }

    /// <summary>Reads a previous settings.json without modifying, migrating or saving it.</summary>
    public static AppSettings? ReadLegacySettingsReadOnly(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("旧配置路径不能为空", nameof(path));
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var settings = AppSettings.FromJson(reader.ReadToEnd());
            if (settings.IsInMemoryOnly) throw new InvalidDataException("旧配置内容无效");
            return settings;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or ArgumentException)
        { throw new InvalidDataException("无法安全读取旧程序配置，原文件未修改。", ex); }
    }

    /// <summary>
    /// Read-only inventory of legacy tasks. Tasks come from the previous settings file (or an
    /// already loaded settings object); <paramref name="currentAccount"/> supplies the UID/address the
    /// user is currently signed in with, used to confirm ownership rather than guess it.
    /// </summary>
    public async Task<LegacyTaskInventory> InspectAsync(AppSettings? legacySettings, string legacySettingsPath,
        StorageAuthResult currentAccount, INasStorageProvider provider, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var tasks = new List<LegacyTaskInspection>();
        var orphans = new List<string>();
        if (legacySettings == null)
            return new LegacyTaskInventory(legacySettingsPath, false,
                legacySettingsPath == null ? "未指定旧配置文件" : "未找到旧程序配置文件", tasks, orphans);
        foreach (var task in legacySettings.Tasks)
        {
            token.ThrowIfCancellationRequested();
            tasks.Add(await InspectTaskAsync(task, currentAccount, provider, token).ConfigureAwait(false));
        }
        if (!string.IsNullOrWhiteSpace(legacySettings.LegacySourceFolder) || !string.IsNullOrWhiteSpace(legacySettings.LegacyTargetShare))
            orphans.Add("旧配置仍包含 v2.0 单任务字段 (SourceFolder/TargetShare)；它不会自动迁移，请先确认来源和目标后再建立新版任务。");
        if (string.IsNullOrWhiteSpace(legacySettings.NasServer) || string.IsNullOrWhiteSpace(legacySettings.Username))
            orphans.Add("旧配置缺少 NAS 地址或账号名，无法确认任务归属。");
        return new LegacyTaskInventory(legacySettingsPath, true, "", tasks, orphans);
    }

    public async Task<LegacyTaskInspection> InspectTaskAsync(SyncTask task, StorageAuthResult currentAccount,
        INasStorageProvider provider, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var warnings = new List<string>();
        var blockers = new List<string>();

        bool sourceExists = false;
        string sourceError = "";
        try { sourceExists = !string.IsNullOrWhiteSpace(task.SourceFolder) && Directory.Exists(task.SourceFolder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sourceError = ex.Message; }
        if (!sourceExists) blockers.Add($"本地源目录不存在或不可访问: {task.SourceFolder}{(sourceError.Length > 0 ? $" ({sourceError})" : "")}");

        bool overlap = false;
        if (sourceExists && task.ProviderType == StorageProviderType.LocalFileSystem &&
            PathHelper.ArePathsSameOrNested(task.SourceFolder, task.DestinationFolder, out string overlapReason))
        {
            overlap = true;
            blockers.Add($"源与目标重叠，拒绝迁移：{overlapReason}");
        }

        // Account identity: UID must be known and equal; an old task without UID can only be
        // migrated after an explicit, read-only confirmation by the user (warning, not silence).
        if (currentAccount == null || !currentAccount.Success)
            blockers.Add("当前未完成账号登录，无法确认任务归属");
        else
        {
            if (currentAccount.UserUid == null) blockers.Add("当前账号 UID 未知，拒绝推导个人空间归属");
            else if (task.NasAccountUid is int recordedUid && recordedUid != currentAccount.UserUid)
                blockers.Add($"任务记录的账号 UID ({recordedUid}) 与当前账号 ({currentAccount.UserUid}) 不一致");
            else if (task.NasAccountUid == null)
                warnings.Add("旧任务没有记录账号 UID，只能按名称和设备地址保守匹配；迁移前需要用户明确确认该目标的归属。");
            if (!string.IsNullOrWhiteSpace(task.NasUsername) && !string.IsNullOrWhiteSpace(currentAccount.AuthenticatedUser) &&
                !string.Equals(task.NasUsername, currentAccount.AuthenticatedUser, StringComparison.Ordinal))
                blockers.Add($"任务账号名「{task.NasUsername}」与当前登录账号「{currentAccount.AuthenticatedUser}」不一致（大小写敏感）");
            if (task.ProviderType != StorageProviderType.FnOsPersonal)
                warnings.Add($"旧任务存储类型为 {task.ProviderType}，迁移不改变存储类型；新版正式使用只支持飞牛个人空间目标。");
        }

        string target = PathHelper.NormalizeRelPath(task.DestinationFolder);
        bool targetHasVersioned = false, targetHasLegacy = false;
        string format = "无";
        int files = 0, history = 0, dirs = 0;
        long bytes = 0;
        bool historyProvable = false;

        if (!PathHelper.IsSafeRelativePath(target))
            blockers.Add($"旧任务目标不是安全的相对路径: {task.DestinationFolder}");
        else
        {
            var versioned = await _versions.LoadAsync(provider, target, token).ConfigureAwait(false);
            targetHasVersioned = versioned != null;
            var metadata = await provider.GetItemAsync($"{target}/.fnsync", token).ConfigureAwait(false);
            if (metadata != null && !metadata.IsDirectory) blockers.Add("目标 .fnsync 元数据路径不是目录，拒绝自动迁移");
            var legacyItem = await provider.GetItemAsync($"{target}/.fnsync/manifest.json", token).ConfigureAwait(false);
            targetHasLegacy = legacyItem != null && !legacyItem.IsDirectory;

            if (targetHasVersioned)
            {
                files = versioned!.Manifest.Files.Count;
                history = versioned.Manifest.History.Count;
                dirs = versioned.Manifest.Directories.Count;
                bytes = versioned.Manifest.Files.Sum(f => f.Size);
                format = "版本化清单";
                historyProvable = true;
            }
            else if (targetHasLegacy)
            {
                var parsed = await ReadLegacyManifestAsync(provider, target, token).ConfigureAwait(false);
                format = parsed.Format;
                blockers.AddRange(parsed.Errors);
                warnings.AddRange(parsed.Warnings);
                files = parsed.Manifest?.Files.Count ?? 0;
                history = parsed.Manifest?.History.Count ?? 0;
                dirs = parsed.Manifest?.Directories.Count ?? 0;
                bytes = parsed.Manifest?.Files.Sum(f => f.Size) ?? 0;
                historyProvable = parsed.Errors.Count == 0 && (history == 0 || parsed.AllHistoryHashesValid);
                if (parsed.Errors.Count == 0 && history > 0 && !parsed.AllHistoryHashesValid)
                    blockers.Add("旧历史记录存在缺失或无效 SHA-256，无法证明历史内容完整，停止迁移");
            }
            else
            {
                blockers.Add("目标目录或其 .fnsync/manifest.json 不存在，目标不明确，拒绝迁移");
            }
        }

        bool locked = false;
        // Inspection is strictly read-only: it must not create lock generations or any other
        // metadata on the legacy target. A concurrent writer is detected by the atomic lock
        // acquisition inside ExecuteAsync, which fails safely before writing anything.

        var classification = Classify(blockers, targetHasVersioned, targetHasLegacy, locked);
        string summary = BuildSummary(task, classification, target, files, history, dirs, bytes);
        return new LegacyTaskInspection(task, classification, summary, warnings, blockers, sourceExists, overlap,
            format, files, history, dirs, bytes, targetHasVersioned, targetHasLegacy, historyProvable);
    }

    private static LegacyMigrationClassification Classify(IReadOnlyList<string> blockers, bool versioned, bool legacy, bool locked)
    {
        if (versioned) return LegacyMigrationClassification.AlreadyVersioned;
        if (locked) return LegacyMigrationClassification.LockedByAnotherWriter;
        if (blockers.Any(b => b.Contains("不受支持", StringComparison.Ordinal)))
            return LegacyMigrationClassification.UnsupportedFormat;
        if (blockers.Any(b => b.Contains("UID", StringComparison.Ordinal) || b.Contains("账号", StringComparison.Ordinal)))
            return LegacyMigrationClassification.AccountUnverified;
        if (blockers.Any(b => b.Contains("源目录") || b.Contains("重叠", StringComparison.Ordinal)))
            return LegacyMigrationClassification.InvalidSource;
        if (blockers.Any(b => b.Contains("损坏", StringComparison.Ordinal) || b.Contains("重复", StringComparison.Ordinal) ||
                              b.Contains("不安全", StringComparison.Ordinal) || b.Contains("SHA-256", StringComparison.Ordinal)))
            return LegacyMigrationClassification.CorruptMetadata;
        if (blockers.Count > 0) return LegacyMigrationClassification.AmbiguousTarget;
        return LegacyMigrationClassification.Supported;
    }

    private static string BuildSummary(SyncTask task, LegacyMigrationClassification classification,
        string target, int files, int history, int dirs, long bytes) => classification switch
    {
        LegacyMigrationClassification.Supported =>
            $"可迁移：{files} 个当前文件、{history} 条历史、{dirs} 个目录（{bytes} 字节），源 {task.SourceFolder} → 新目标（旧目标 {target} 只读保留）",
        LegacyMigrationClassification.AlreadyVersioned => $"目标已是版本化备份（{files} 文件 / {history} 历史），无需迁移",
        LegacyMigrationClassification.UnsupportedFormat => "旧清单版本或字段不受支持，不能自动迁移",
        LegacyMigrationClassification.CorruptMetadata => "旧清单损坏或含不安全条目，已停止",
        LegacyMigrationClassification.AmbiguousTarget => "目标不明确或不可访问，已停止",
        LegacyMigrationClassification.InvalidSource => "源目录缺失或与目标重叠，已停止",
        LegacyMigrationClassification.AccountUnverified => "账号身份未能确认（UID/名称不一致或未知），已停止",
        LegacyMigrationClassification.LockedByAnotherWriter => "目标被其他写入者占用或存在遗留锁，已停止",
        _ => "状态未知，已停止"
    };

    private sealed record LegacyParseResult(string Format, Manifest? Manifest, List<string> Errors, List<string> Warnings,
        bool AllHistoryHashesValid);

    private async Task<LegacyParseResult> ReadLegacyManifestAsync(INasStorageProvider provider, string target,
        CancellationToken token)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        byte[] bytes;
        using (var stream = await provider.OpenReadAsync($"{target}/.fnsync/manifest.json", token).ConfigureAwait(false))
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxManifestBytes) { errors.Add("旧清单超过 64 MiB 上限，拒绝读取"); return new("未知", null, errors, warnings, false); }
                buffer.Write(chunk, 0, read);
            }
            bytes = buffer.ToArray();
        }
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { errors.Add("旧清单根节点不是对象，清单损坏"); return new("未知", null, errors, warnings, false); }
            bool hasFiles = doc.RootElement.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array;
            if (!hasFiles) { errors.Add("旧清单缺少 files 数组，清单损坏"); return new("未知", null, errors, warnings, false); }
            if (!doc.RootElement.TryGetProperty("version", out var versionElement) || !versionElement.TryGetInt32(out int version))
            { errors.Add("旧清单缺少版本号，清单损坏"); return new("未知", null, errors, warnings, false); }
            string format = $"v{version}";
            if (version is not (3 or 4))
            { errors.Add($"旧清单版本 v{version} 不受支持（仅支持 v3/v4）"); return new(format, null, errors, warnings, false); }
            var manifest = JsonSerializer.Deserialize<Manifest>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest == null || manifest.Files == null || manifest.History == null || manifest.Directories == null)
            { errors.Add("旧清单字段不完整，清单损坏"); return new(format, null, errors, warnings, false); }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                if (file == null || !PathHelper.IsSafeRelativePath(file.OriginalRel) || !PathHelper.IsSafeRelativePath(file.StoredRel) ||
                    file.Size < 0 || !IsHash(file.Sha256) || !names.Add(PathHelper.NormalizeRelPath(file.OriginalRel)))
                { errors.Add("旧清单包含不安全、重复或缺少有效哈希的当前文件"); break; }
            }
            bool historyHashesValid = true;
            foreach (var entry in manifest.History)
            {
                if (entry == null || !PathHelper.IsSafeRelativePath(entry.OriginalRel) || !PathHelper.IsSafeRelativePath(entry.StoredRel) ||
                    entry.Size < 0 || !IsHash(entry.Sha256)) { historyHashesValid = false; }
            }
            foreach (var directory in manifest.Directories)
                if (directory == null || !PathHelper.IsSafeRelativePath(directory.OriginalRel) || !PathHelper.IsSafeRelativePath(directory.StoredRel))
                { errors.Add("旧清单包含不安全的目录路径"); break; }
            if (manifest.Files.Count == 0) warnings.Add("旧清单没有当前文件，只能迁移目录与历史记录");
            if (manifest.History.Count > 0)
                warnings.Add("历史文件将复制到新目标并逐项校验 SHA-256；旧目录保持只读，不会被改写");
            return new(format, manifest, errors, warnings, historyHashesValid);
        }
        catch (JsonException ex) { errors.Add($"旧清单 JSON 损坏：{ex.Message}"); return new("未知", null, errors, warnings, false); }
    }

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    /// <summary>
    /// Builds a migration preview. The new target must be a different, empty destination; the old
    /// target is never the migration output. Refuses if any blocker remains.
    /// </summary>
    public async Task<LegacyMigrationPreview> BuildPreviewAsync(LegacyTaskInspection inspection, string newTarget,
        INasStorageProvider provider, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(provider);
        var blockers = inspection.Blockers.ToList();
        var warnings = inspection.Warnings.ToList();
        string normalizedNew = PathHelper.NormalizeRelPath(newTarget);
        string normalizedOld = PathHelper.NormalizeRelPath(inspection.Task.DestinationFolder);
        if (!PathHelper.IsSafeRelativePath(normalizedNew)) blockers.Add("新备份目标必须是安全的相对路径");
        if (string.Equals(normalizedNew, normalizedOld, StringComparison.OrdinalIgnoreCase))
            blockers.Add("新备份目标不能与旧备份目标相同；旧目录只读保留，迁移必须写入独立的新目录");
        if (normalizedNew.StartsWith(normalizedOld + "/", StringComparison.OrdinalIgnoreCase) ||
            normalizedOld.StartsWith(normalizedNew + "/", StringComparison.OrdinalIgnoreCase))
            blockers.Add("新备份目标与旧备份目录互相嵌套，已阻止");
        if (blockers.Count == 0)
        {
            if (await provider.GetItemAsync(normalizedNew, token).ConfigureAwait(false) != null)
                blockers.Add("新备份目标已存在，拒绝覆盖；请选择一个空的新目录");
        }
        return new LegacyMigrationPreview(inspection.Task.TaskId, inspection.Task.TaskName, inspection.Task.SourceFolder,
            normalizedOld, normalizedNew, blockers, warnings, inspection.TotalBytes, inspection.FileCount,
            inspection.HistoryCount, inspection.DirectoryCount, blockers.Count == 0);
    }

    /// <summary>
    /// Executes a previewed migration. Writes a migration record first (for interrupted-run
    /// re-checking), then performs the verified copy through LegacyBackupMigration. The legacy
    /// manifest and files are compared before and after; any change fails the migration.
    /// </summary>
    public async Task<LegacyMigrationExecution> ExecuteAsync(LegacyTaskInspection inspection, LegacyMigrationPreview preview,
        INasStorageProvider provider, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(provider);
        if (!preview.CanExecute) throw new InvalidOperationException("迁移预览存在阻止项，拒绝执行");
        if (!string.Equals(preview.TaskId, inspection.Task.TaskId, StringComparison.Ordinal))
            throw new InvalidOperationException("预览与任务不一致");
        string oldTarget = preview.LegacyTarget;
        string newTarget = preview.NewTarget;
        if (string.Equals(oldTarget, newTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("拒绝在原目标上迁移");

        var before = await ReadLegacyManifestAsync(provider, oldTarget, token).ConfigureAwait(false);
        if (before.Errors.Count > 0) throw new InvalidDataException("旧清单在执行前复核时损坏，停止迁移");
        byte[] oldManifestBytes = await ReadAllAsync(provider, $"{oldTarget}/.fnsync/manifest.json", token).ConfigureAwait(false);
        string oldManifestHash = Convert.ToHexString(SHA256.HashData(oldManifestBytes));

        // 1. Write a durable intent record LOCALLY (never inside the legacy backup) so an interrupted
        //    run can be re-checked instead of guessed. The old backup stays byte-for-byte untouched.
        string intentDirectory = GetMigrationIntentDirectory();
        Directory.CreateDirectory(intentDirectory);
        string intentPath = Path.Combine(intentDirectory, $"intent-{inspection.Task.TaskId}-{Guid.NewGuid():N}.json");
        byte[] intent = JsonSerializer.SerializeToUtf8Bytes(new
        {
            task_id = inspection.Task.TaskId, old_target = oldTarget, new_target = newTarget,
            source = inspection.Task.SourceFolder, created_at = DateTimeOffset.UtcNow,
            old_manifest_sha256 = oldManifestHash, status = "started"
        });
        await File.WriteAllBytesAsync(intentPath, intent, token).ConfigureAwait(false);

        // 2. Copy every legacy object, verified, into a NEW target. The legacy backup is never
        //    rewritten; all new state lives under the new target only.
        string newRoot = PathHelper.NormalizeRelPath(newTarget);
        var manifest = new Manifest
        {
            SourceRoot = inspection.Task.SourceFolder,
            TargetRoot = newRoot,
            BackupPolicy = inspection.Task.Strategy.ToString(),
            Note = "由旧任务安全迁移创建的独立版本化备份"
        };
        await CreateTreeAsync(provider, $"{newRoot}/.fnsync/objects", token).ConfigureAwait(false);
        var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        async Task<string> CopyVerifiedAsync(string storedRel, long size, string sha256)
        {
            if (copied.TryGetValue(storedRel, out string? reused)) return reused;
            string sourcePath = $"{oldTarget}/{storedRel}";
            var sourceItem = await provider.GetItemAsync(sourcePath, token).ConfigureAwait(false);
            if (sourceItem == null || sourceItem.IsDirectory || sourceItem.Size != size)
                throw new InvalidDataException($"旧备份文件不存在或大小与清单不一致: {storedRel}");
            string objectRel = $".fnsync/objects/obj-{Guid.NewGuid():N}";
            string destinationPath = $"{newRoot}/{objectRel}";
            using (var input = await provider.OpenReadAsync(sourcePath, token).ConfigureAwait(false))
            {
                var uploaded = await provider.UploadFileAsync(destinationPath, input, size, sourceItem.LastModifiedUtc, false, null, token).ConfigureAwait(false);
                if (!uploaded.Success) throw new IOException($"迁移对象写入失败: {uploaded.ErrorMessage}");
            }
            token.ThrowIfCancellationRequested();
            if (!await provider.VerifyFileIntegrityAsync(destinationPath, sha256, size, token).ConfigureAwait(false))
                throw new InvalidDataException($"迁移对象回读 SHA-256 校验失败: {storedRel}");
            copied[storedRel] = objectRel;
            return objectRel;
        }
        foreach (var file in before.Manifest!.Files)
        {
            token.ThrowIfCancellationRequested();
            string objectRel = await CopyVerifiedAsync(file.StoredRel, file.Size, file.Sha256).ConfigureAwait(false);
            manifest.Files.Add(new ManifestFileEntry
            {
                OriginalRel = file.OriginalRel, StoredRel = objectRel, Size = file.Size,
                Sha256 = file.Sha256.ToLowerInvariant(), IsLongName = file.IsLongName, Utf8Bytes = file.Utf8Bytes,
                StoredUtf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(objectRel)), IsActive = true
            });
        }
        foreach (var history in before.Manifest.History)
        {
            token.ThrowIfCancellationRequested();
            string objectRel = await CopyVerifiedAsync(history.StoredRel, history.Size, history.Sha256).ConfigureAwait(false);
            manifest.History.Add(new ManifestHistoryEntry
            {
                OriginalRel = history.OriginalRel, StoredRel = objectRel, Size = history.Size,
                Sha256 = history.Sha256.ToLowerInvariant(), Reason = history.Reason, RenamedTo = history.RenamedTo,
                IsLongName = history.IsLongName, Utf8Bytes = history.Utf8Bytes, ArchivedAt = history.ArchivedAt
            });
        }
        foreach (var directory in before.Manifest.Directories)
            manifest.Directories.Add(new ManifestDirEntry
            {
                OriginalRel = directory.OriginalRel, StoredRel = directory.StoredRel,
                IsLongName = directory.IsLongName, Utf8Bytes = directory.Utf8Bytes
            });
        var snapshot = await _versions.SaveAsync(provider, newTarget, manifest, null, token).ConfigureAwait(false);

        // 3. Re-check that the legacy manifest and every legacy file still match the pre-migration view.
        var after = await ReadLegacyManifestAsync(provider, oldTarget, token).ConfigureAwait(false);
        var afterBytes = await ReadAllAsync(provider, $"{oldTarget}/.fnsync/manifest.json", token).ConfigureAwait(false);
        bool oldManifestUnchanged = after.Errors.Count == 0 &&
            string.Equals(Convert.ToHexString(SHA256.HashData(afterBytes)), oldManifestHash, StringComparison.OrdinalIgnoreCase);
        bool oldFilesUnchanged = true;
        foreach (var file in before.Manifest?.Files ?? new List<ManifestFileEntry>())
        {
            token.ThrowIfCancellationRequested();
            var item = await provider.GetItemAsync($"{oldTarget}/{file.StoredRel}", token).ConfigureAwait(false);
            if (item == null || item.IsDirectory || item.Size != file.Size ||
                !await provider.VerifyFileIntegrityAsync($"{oldTarget}/{file.StoredRel}", file.Sha256, file.Size, token).ConfigureAwait(false))
            { oldFilesUnchanged = false; break; }
        }
        foreach (var history in before.Manifest?.History ?? new List<ManifestHistoryEntry>())
        {
            token.ThrowIfCancellationRequested();
            var item = await provider.GetItemAsync($"{oldTarget}/{history.StoredRel}", token).ConfigureAwait(false);
            if (item == null || item.IsDirectory || item.Size != history.Size ||
                !await provider.VerifyFileIntegrityAsync($"{oldTarget}/{history.StoredRel}", history.Sha256, history.Size, token).ConfigureAwait(false))
            { oldFilesUnchanged = false; break; }
        }
        await UpdateIntentAsync(intentPath, new Dictionary<string, object?>
        {
            ["status"] = oldManifestUnchanged && oldFilesUnchanged ? "completed" : "old-data-changed",
            ["manifest_id"] = snapshot.Id, ["sequence"] = snapshot.Sequence, ["finished_at"] = DateTimeOffset.UtcNow
        }, token).ConfigureAwait(false);
        if (!oldManifestUnchanged || !oldFilesUnchanged)
            throw new IOException("迁移期间旧备份发生变化，新目标已保留但迁移标记为未完成；请检查后重试");

        return new LegacyMigrationExecution(inspection.Task.TaskId, newTarget, snapshot.Id, snapshot.Sequence,
            DateTimeOffset.UtcNow, oldManifestUnchanged, oldFilesUnchanged);
    }

    /// <summary>
    /// Restore-verifies the migrated target into an empty local directory: current files, every
    /// provable history entry and all mapped (empty) directories, each checked with SHA-256.
    /// The destination must not already contain user data.
    /// </summary>
    public async Task<LegacyMigrationVerification> VerifyAsync(LegacyTaskInspection inspection, string migratedTarget,
        string restoreDirectory, INasStorageProvider provider, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(restoreDirectory)) throw new ArgumentException("恢复验证目录不能为空", nameof(restoreDirectory));
        string restore = Path.GetFullPath(restoreDirectory);
        if (Directory.Exists(restore) && Directory.EnumerateFileSystemEntries(restore).Any())
            throw new IOException("恢复验证目录必须为空，拒绝覆盖用户已有文件");
        Directory.CreateDirectory(restore);
        var failures = new List<string>();
        var engine = new RestoreEngine(new ManifestManager());
        var report = await engine.RunRestoreVersionedAsync(provider, migratedTarget, restore, ConflictResolution.SkipExisting).ConfigureAwait(false);
        if (report.FailedFiles > 0) failures.Add($"{report.FailedFiles} 个文件恢复失败");
        if (report.RestoredFiles != report.IntegrityVerifiedFiles) failures.Add("恢复文件数量与逐项 SHA-256 校验数量不一致");

        var snapshot = await _versions.LoadAsync(provider, migratedTarget, token).ConfigureAwait(false);
        if (snapshot == null) throw new InvalidDataException("迁移后无法读取版本化清单");
        int restoredHistory = 0, verifiedHistory = 0;
        foreach (var entry in snapshot.Manifest.History)
        {
            token.ThrowIfCancellationRequested();
            string historyRestore = Path.Combine(restore, ".history-verify", Path.GetFileNameWithoutExtension(Path.GetRandomFileName()));
            bool ok = await engine.RunRestoreHistoryFileAsync(provider, migratedTarget, historyRestore, entry).ConfigureAwait(false);
            restoredHistory++;
            if (ok && await VerifyLocalFileAsync(Path.Combine(historyRestore, entry.OriginalRel), entry.Sha256, entry.Size))
                verifiedHistory++;
            else failures.Add($"历史文件校验失败: {entry.OriginalRel}");
        }
        bool directoriesRestored = true;
        foreach (var directory in snapshot.Manifest.Directories)
        {
            if (!Directory.Exists(Path.Combine(restore, directory.OriginalRel)))
            { directoriesRestored = false; failures.Add($"目录未恢复: {directory.OriginalRel}"); }
        }
        var oldAfter = await ReadLegacyManifestAsync(provider, inspection.Task.DestinationFolder, token).ConfigureAwait(false);
        bool oldTargetUnchanged = oldAfter.Errors.Count == 0 && inspection.TargetHasLegacyManifest;
        return new LegacyMigrationVerification(inspection.Task.TaskId, migratedTarget, restore,
            snapshot.Manifest.Files.Count, snapshot.Manifest.History.Count, snapshot.Manifest.Directories.Count,
            report.RestoredFiles, report.IntegrityVerifiedFiles, restoredHistory, verifiedHistory,
            directoriesRestored, oldTargetUnchanged, failures.Count == 0 && report.FailedFiles == 0, failures);
    }

    /// <summary>
    /// Final step: builds the new task configuration and returns it DISABLED. The caller enables it
    /// explicitly; nothing here starts synchronization or rewrites the legacy settings file.
    /// </summary>
    public SyncTask CreateMigratedTask(LegacyTaskInspection inspection, LegacyMigrationVerification verification,
        StorageAuthResult currentAccount)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(verification);
        ArgumentNullException.ThrowIfNull(currentAccount);
        if (!verification.Passed) throw new InvalidOperationException("恢复验证未通过，禁止启用迁移后的任务");
        if (currentAccount.UserUid == null) throw new InvalidOperationException("当前账号 UID 未知，拒绝创建迁移任务");
        var old = inspection.Task;
        return new SyncTask
        {
            TaskName = string.IsNullOrWhiteSpace(old.TaskName) ? "迁移的备份任务" : old.TaskName,
            SourceFolder = old.SourceFolder,
            DestinationFolder = verification.NewTarget,
            DestinationRoot = verification.NewTarget,
            NasDeviceId = old.NasDeviceId,
            NasUsername = old.NasUsername,
            NasAccountUid = currentAccount.UserUid,
            ProviderType = old.ProviderType,
            Mode = old.Mode,
            Strategy = old.Strategy,
            ScheduleMode = old.ScheduleMode,
            IntervalMinutes = old.IntervalMinutes,
            AutoSyncEnabled = old.AutoSyncEnabled,
            RealtimeWatchEnabled = old.RealtimeWatchEnabled,
            Enabled = false,
            LastSyncStatus = "迁移完成，恢复验证通过；确认后才启用"
        };
    }

    /// <summary>Re-checks an interrupted migration from its local intent records without guessing.</summary>
    public Task<IReadOnlyList<string>> RecheckInterruptedAsync(string oldTarget, CancellationToken token = default)
    {
        string normalized = PathHelper.NormalizeRelPath(oldTarget);
        if (!PathHelper.IsSafeRelativePath(normalized)) throw new ArgumentException("备份目标必须是安全的相对路径", nameof(oldTarget));
        var findings = new List<string>();
        string directory = GetMigrationIntentDirectory();
        if (!Directory.Exists(directory)) return Task.FromResult<IReadOnlyList<string>>(findings);
        foreach (string path in Directory.EnumerateFiles(directory, "intent-*.json"))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                if (!doc.RootElement.TryGetProperty("old_target", out var oldElement) ||
                    !string.Equals(oldElement.GetString(), normalized, StringComparison.OrdinalIgnoreCase)) continue;
                string status = doc.RootElement.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "unknown" : "unknown";
                string newTarget = doc.RootElement.TryGetProperty("new_target", out var newElement) ? newElement.GetString() ?? "" : "";
                findings.Add(status switch
                {
                    "completed" => $"已完成迁移（{Path.GetFileName(path)}）：{normalized} → {newTarget}",
                    "old-data-changed" => $"迁移期间旧数据发生变化（{Path.GetFileName(path)}）：新目标 {newTarget} 已保留，需要人工检查",
                    _ => $"迁移可能未完成（{Path.GetFileName(path)}）：{normalized} → {newTarget}；请先确认新目标是否完整并做恢复验证，不要直接重试"
                });
            }
            catch (JsonException) { findings.Add($"迁移意图记录损坏：{Path.GetFileName(path)}，无法判断该次迁移状态，请保留现场"); }
        }
        if (findings.Count == 0) findings.Add("没有该目标的迁移意图记录");
        return Task.FromResult<IReadOnlyList<string>>(findings);
    }

    private static async Task CreateTreeAsync(INasStorageProvider provider, string directory, CancellationToken token)
    {
        string current = "";
        foreach (string part in directory.Split('/'))
        {
            current = current.Length == 0 ? part : $"{current}/{part}";
            var created = await provider.CreateDirectoryAsync(current, token).ConfigureAwait(false);
            if (!created.Success) throw new IOException($"无法创建迁移目录: {created.ErrorMessage}");
        }
    }

    private static string GetMigrationIntentDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FNSyncPlus", "migration-journal");

    /// <summary>Merges fields into the existing intent record; never drops earlier evidence.</summary>
    private static async Task UpdateIntentAsync(string path, IReadOnlyDictionary<string, object?> fields, CancellationToken token)
    {
        var node = JsonNode.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false)) as JsonObject
            ?? throw new InvalidDataException("迁移意图记录损坏，拒绝覆盖");
        foreach (var pair in fields)
            node[pair.Key] = pair.Value switch
            {
                null => null,
                long value => JsonValue.Create(value),
                int value => JsonValue.Create(value),
                DateTimeOffset value => JsonValue.Create(value.ToString("O")),
                _ => JsonValue.Create(pair.Value.ToString())
            };
        string temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, node.ToJsonString(), token).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private static async Task<byte[]> ReadAllAsync(INasStorageProvider provider, string path, CancellationToken token)
    {
        using var stream = await provider.OpenReadAsync(path, token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, token).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static async Task<bool> VerifyLocalFileAsync(string path, string expectedSha256, long expectedSize)
    {
        try
        {
            string longPath = PathHelper.EnsureLongPathPrefix(path);
            if (!File.Exists(longPath) || new FileInfo(longPath).Length != expectedSize) return false;
            await using var stream = new FileStream(longPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
