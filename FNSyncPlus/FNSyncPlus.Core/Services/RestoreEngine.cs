using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services
{
    public enum ConflictResolution
    {
        SkipExisting,
        Overwrite,
        RenameWithTimestamp
    }

    public class RestoreFileResult
    {
        public string OriginalRel { get; set; } = string.Empty;
        public string StoredRel { get; set; } = string.Empty;
        public bool Success { get; set; }
        public bool Skipped { get; set; }
        public bool HashVerified { get; set; }
        public string? ErrorMessage { get; set; }
        public string? DetailedException { get; set; }
    }

    public class RestoreProgressReport
    {
        public int TotalFiles { get; set; }
        public int RestoredFiles { get; set; }
        public int SkippedFiles { get; set; }
        public int LongNameRestoredFiles { get; set; }
        public int IntegrityVerifiedFiles { get; set; }
        public int FailedFiles { get; set; }
        public int RestoredDirectories { get; set; }
        public string CurrentFile { get; set; } = string.Empty;
        public string StatusMessage { get; set; } = string.Empty;
        public List<RestoreFileResult> FileResults { get; } = new();
        public List<RestoreFileResult> FailedItems => FileResults.Where(r => !r.Success && !r.Skipped).ToList();
    }

    public class RestorePreviewItem
    {
        public string OriginalRelPath { get; set; } = string.Empty;
        public string StoredRelPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public bool IsLongName { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public DateTimeOffset ModTime { get; set; }
    }

    public class DiscoveredBackupPackage
    {
        public string PackageName { get; set; } = string.Empty;
        public string NasFolderUnc { get; set; } = string.Empty;
        public string OriginalSourceRoot { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public long TotalSizeBytes { get; set; }
        public int LongNameCount { get; set; }
        public DateTimeOffset LastUpdated { get; set; }
        public string? VersionId { get; set; }
        public bool HasDamagedVersionRecords { get; set; }

        public string FormattedSize
        {
            get
            {
                double mb = TotalSizeBytes / (1024.0 * 1024.0);
                if (mb > 1024) return $"{mb / 1024.0:F2} GB";
                return $"{mb:F1} MB";
            }
        }
    }

    public class RestoreEngine
    {
        private readonly ManifestManager _manifestManager;

        public event Action<RestoreProgressReport>? ProgressChanged;
        public event Action<string, string>? LogEmitted; // (level, message)

        public RestoreEngine(ManifestManager manifestManager)
        {
            _manifestManager = manifestManager;
        }

        /// <summary>Maps a manifest relative path onto the host separator (POSIX hosts use '/').</summary>
        private static string ToLocalSeparators(string relativePath) =>
            OperatingSystem.IsWindows()
                ? relativePath.Replace('/', '\\')
                : relativePath.Replace('\\', '/');

        private void Log(string level, string message)
        {
            LogEmitted?.Invoke(level, $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        }

        private static DiscoveredBackupPackage PackageFromManifest(string name, string path, Manifest manifest, string? versionId = null) => new()
        {
            PackageName = name,
            NasFolderUnc = path,
            OriginalSourceRoot = manifest.SourceRoot,
            FileCount = manifest.Files.Count,
            TotalSizeBytes = manifest.Files.Sum(f => f.Size),
            LongNameCount = manifest.Files.Count(f => f.IsLongName),
            LastUpdated = manifest.UpdatedAt,
            VersionId = versionId
        };

        private async Task<DiscoveredBackupPackage?> DiscoverProviderPackageAsync(INasStorageProvider provider,
            string path, string name, CancellationToken token)
        {
            if (!string.IsNullOrEmpty(path))
            {
                string metaPath = $"{path.TrimEnd('/', '\\')}/.fnsync";
                var metaDirectory = await provider.GetItemAsync(metaPath, token).ConfigureAwait(false);
                if (metaDirectory != null && !metaDirectory.IsDirectory) throw new InvalidDataException("备份元数据路径不是目录");
                var versionDirectory = metaDirectory == null ? null :
                    await provider.GetItemAsync($"{metaPath}/manifest-versions", token).ConfigureAwait(false);
                if (versionDirectory != null)
                {
                    if (!versionDirectory.IsDirectory) throw new InvalidDataException("版本化清单路径不是目录");
                    var inspection = await new VersionedManifestStore().InspectForRestoreAsync(provider, path, token).ConfigureAwait(false);
                    var snapshot = inspection.Snapshot ?? throw new InvalidDataException("存在版本化清单目录，但没有有效提交");
                    var package = PackageFromManifest(name, path, snapshot.Manifest, snapshot.Id);
                    package.HasDamagedVersionRecords = inspection.HasInvalidRecords;
                    return package;
                }
            }
            var manifest = await _manifestManager.LoadManifestAsync(provider, path, token).ConfigureAwait(false);
            return manifest.IsNew ? null : PackageFromManifest(name, path, manifest);
        }

        public async Task<Manifest> ReadPackageManifestAsync(INasStorageProvider provider,
            DiscoveredBackupPackage package, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(provider);
            ArgumentNullException.ThrowIfNull(package);
            if (package.VersionId == null)
                return await _manifestManager.LoadManifestAsync(provider, package.NasFolderUnc, cancellationToken).ConfigureAwait(false);
            var snapshot = await new VersionedManifestStore().LoadAsync(provider, package.NasFolderUnc, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("版本化清单不存在，请重新扫描备份");
            if (!string.Equals(snapshot.Id, package.VersionId, StringComparison.Ordinal))
                throw new IOException("备份版本已变化，请重新扫描后再恢复");
            return snapshot.Manifest;
        }

        /// <summary>
        /// 在 NAS 备份根目录下自动发现所有包含清单元数据的备份任务包（完全不依赖原电脑本地配置）
        /// 例如在 \\server\私人备份 下自动发现【工作资料】、【项目文件】、【照片备份】
        /// </summary>
        public async Task<List<DiscoveredBackupPackage>> DiscoverBackupPackagesAsync(string nasBackupRootUnc, CancellationToken cancellationToken = default,
            ICollection<string>? warnings = null)
        {
            var result = new List<DiscoveredBackupPackage>();
            if (string.IsNullOrWhiteSpace(nasBackupRootUnc) || !Directory.Exists(nasBackupRootUnc))
            {
                throw new DirectoryNotFoundException("备份扫描目录不存在或无法访问");
            }

            await Task.Run(async () =>
            {
                string longRoot = PathHelper.EnsureLongPathPrefix(nasBackupRootUnc);
                var rootDi = new DirectoryInfo(longRoot);

                // 1. 检查根目录本身是否就是一个备份包
                string rootMeta = Path.Combine(longRoot, Manifest.MetadataDirName, Manifest.IsolatedManifestFileName);
                string legacyRootMeta = Path.Combine(longRoot, Manifest.LegacyManifestFileName);
                try
                {
                    if (ManifestFilePresent(rootMeta) || ManifestFilePresent(legacyRootMeta))
                    {
                        var m = await _manifestManager.LoadManifestAsync(nasBackupRootUnc, cancellationToken);
                        if (!m.IsNew)
                        {
                            result.Add(new DiscoveredBackupPackage
                            {
                                PackageName = rootDi.Name,
                                NasFolderUnc = nasBackupRootUnc,
                                OriginalSourceRoot = m.SourceRoot,
                                FileCount = m.Files.Count,
                                TotalSizeBytes = m.Files.Sum(f => f.Size),
                                LongNameCount = m.Files.Count(f => f.IsLongName),
                                LastUpdated = m.UpdatedAt
                            });
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ReportDiscoveryIssue(warnings, "读取扫描根目录清单失败", ex); }

                // 2. 扫描根目录下的所有子文件夹
                try
                {
                    foreach (var sub in rootDi.EnumerateDirectories())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (sub.Name.Equals(Manifest.MetadataDirName, StringComparison.OrdinalIgnoreCase) ||
                            sub.Name.StartsWith("$") ||
                            sub.Name.Equals(".recycle", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string subUnc = PathHelper.RemoveLongPathPrefix(sub.FullName);
                        string subMeta = Path.Combine(sub.FullName, Manifest.MetadataDirName, Manifest.IsolatedManifestFileName);
                        string legacySubMeta = Path.Combine(sub.FullName, Manifest.LegacyManifestFileName);

                        try
                        {
                            if (ManifestFilePresent(subMeta) || ManifestFilePresent(legacySubMeta))
                            {
                                var m = await _manifestManager.LoadManifestAsync(subUnc, cancellationToken);
                                result.Add(new DiscoveredBackupPackage
                                {
                                    PackageName = sub.Name,
                                    NasFolderUnc = subUnc,
                                    OriginalSourceRoot = m.SourceRoot,
                                    FileCount = m.Files.Count,
                                    TotalSizeBytes = m.Files.Sum(f => f.Size),
                                    LongNameCount = m.Files.Count(f => f.IsLongName),
                                    LastUpdated = m.UpdatedAt
                                });
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { ReportDiscoveryIssue(warnings, $"读取备份目录失败: {sub.Name}", ex); }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { ReportDiscoveryIssue(warnings, "枚举备份目录失败", ex); }
            }, cancellationToken);

            return result;
        }

        /// <summary>
        /// 从 NAS 统一存储提供程序中发现包含清单元数据的备份任务包
        /// </summary>
        public async Task<List<DiscoveredBackupPackage>> DiscoverBackupPackagesAsync(
            INasStorageProvider storageProvider, 
            string nasBackupRootRel = "", 
            CancellationToken cancellationToken = default,
            ICollection<string>? warnings = null)
        {
            var result = new List<DiscoveredBackupPackage>();
            if (storageProvider == null || !storageProvider.IsAuthenticated) throw new InvalidOperationException("存储未连接，无法扫描备份");
            cancellationToken.ThrowIfCancellationRequested();

            if (storageProvider is SmbStorageProvider && (nasBackupRootRel.StartsWith(@"\\") || nasBackupRootRel.Contains(@":\")))
            {
                return await DiscoverBackupPackagesAsync(nasBackupRootRel, cancellationToken, warnings).ConfigureAwait(false);
            }

            try
            {
                string rootPath = nasBackupRootRel.TrimEnd('/', '\\');
                string rootName = string.IsNullOrEmpty(rootPath) || rootPath == "我的文件"
                    ? "个人空间根备份" : rootPath.Split('/', '\\').Last();
                var rootPackage = await DiscoverProviderPackageAsync(storageProvider, nasBackupRootRel,
                    rootName, cancellationToken).ConfigureAwait(false);
                if (rootPackage != null) result.Add(rootPackage);
                if (rootPackage?.HasDamagedVersionRecords == true)
                    ReportDiscoveryIssue(warnings, "版本化清单包含损坏记录，可能已回退到较早版本", new InvalidDataException("请核对备份版本"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ReportDiscoveryIssue(warnings, "读取扫描根目录清单失败", ex); }

            try
            {
                // 2. 枚举子目录；根目录自身损坏时仍尝试发现其他有效备份。
                var entries = await storageProvider.ListEntriesAsync(nasBackupRootRel, cancellationToken).ConfigureAwait(false);
                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.IsDirectory && !entry.Name.StartsWith(".") && !entry.Name.StartsWith("$"))
                    {
                        try
                        {
                            var package = await DiscoverProviderPackageAsync(storageProvider, entry.RelativePath,
                                entry.Name, cancellationToken).ConfigureAwait(false);
                            if (package != null) result.Add(package);
                            if (package?.HasDamagedVersionRecords == true)
                                ReportDiscoveryIssue(warnings, $"备份 {entry.Name} 的版本记录损坏，可能已回退", new InvalidDataException("请核对备份版本"));
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { ReportDiscoveryIssue(warnings, $"读取备份目录失败: {entry.Name}", ex); }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { ReportDiscoveryIssue(warnings, "扫描备份目录失败", ex); }

            return result;
        }

        private void ReportDiscoveryIssue(ICollection<string>? warnings, string context, Exception error)
        {
            string message = $"{context}: {error.Message}";
            Log("WARN", message);
            if (warnings == null) throw new IOException(message, error);
            warnings.Add(message);
        }

        private static bool ManifestFilePresent(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                    throw new InvalidDataException("清单路径是目录");
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }

        /// <summary>
        /// 预览备份目录中的文件清单与原始长文件名映射（无需开始恢复）
        /// </summary>
        public async Task<List<RestorePreviewItem>> PreviewBackupAsync(string targetNasFolder, CancellationToken cancellationToken = default)
        {
            var manifest = await _manifestManager.LoadManifestAsync(targetNasFolder, cancellationToken).ConfigureAwait(false);
            var list = new List<RestorePreviewItem>();

            foreach (var f in manifest.Files)
            {
                list.Add(new RestorePreviewItem
                {
                    OriginalRelPath = f.OriginalRel,
                    StoredRelPath = f.StoredRel,
                    Size = f.Size,
                    IsLongName = f.IsLongName,
                    Sha256 = f.Sha256,
                    ModTime = f.ModTimeIso
                });
            }

            return list;
        }

        /// <summary>
        /// 从 NAS 统一存储提供程序预览备份目录中的文件清单与原始长文件名映射
        /// </summary>
        public async Task<List<RestorePreviewItem>> PreviewBackupAsync(INasStorageProvider storageProvider, string targetNasFolderRel, CancellationToken cancellationToken = default)
        {
            if (storageProvider is SmbStorageProvider && (targetNasFolderRel.StartsWith(@"\\") || targetNasFolderRel.Contains(@":\")))
            {
                return await PreviewBackupAsync(targetNasFolderRel, cancellationToken).ConfigureAwait(false);
            }

            var manifest = await _manifestManager.LoadManifestAsync(storageProvider, targetNasFolderRel, cancellationToken).ConfigureAwait(false);
            var list = new List<RestorePreviewItem>();

            foreach (var f in manifest.Files)
            {
                list.Add(new RestorePreviewItem
                {
                    OriginalRelPath = f.OriginalRel,
                    StoredRelPath = f.StoredRel,
                    Size = f.Size,
                    IsLongName = f.IsLongName,
                    Sha256 = f.Sha256,
                    ModTime = f.ModTimeIso
                });
            }

            return list;
        }

        /// <summary>
        /// 执行异机一键还原：将 NAS 上的备份恢复到本地目录，自动将超长短别名还原为完整原始名称与目录结构
        /// 并逐一执行 SHA-256 完整性强校验。若校验不一致，自动删除损坏临时文件并记为失败，坚决杜绝损坏数据污染恢复环境。
        /// </summary>
        public async Task<RestoreProgressReport> RunRestoreAsync(
            string nasBackupFolder,
            string localDestinationFolder,
            ConflictResolution conflictResolution = ConflictResolution.SkipExisting,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(nasBackupFolder) || !Directory.Exists(nasBackupFolder))
            {
                throw new DirectoryNotFoundException($"NAS 备份目录不存在: {nasBackupFolder}");
            }
            if (string.IsNullOrWhiteSpace(localDestinationFolder))
            {
                throw new ArgumentException("本地恢复目标路径不能为空", nameof(localDestinationFolder));
            }

            if (PathHelper.ArePathsSameOrNested(nasBackupFolder, localDestinationFolder, out string nestReason))
            {
                throw new InvalidOperationException($"恢复路径安全校验失败: {nestReason}");
            }

            var report = new RestoreProgressReport();
            Log("INFO", $"开始恢复任务: NAS【{nasBackupFolder}】 -> 本地【{localDestinationFolder}】");

            string longNas = PathHelper.EnsureLongPathPrefix(nasBackupFolder);
            string longLocal = PathHelper.EnsureLongPathPrefix(localDestinationFolder);

            if (!Directory.Exists(longLocal))
            {
                Directory.CreateDirectory(longLocal);
            }

            // 1. 读取 NAS 上的清单文件
            Log("INFO", "正在读取 NAS 备份元数据清单 (.fnsync/manifest.json)...");
            var manifest = await _manifestManager.LoadManifestAsync(nasBackupFolder, cancellationToken).ConfigureAwait(false);

            if (manifest.Files == null || manifest.Directories == null ||
                manifest.Directories.Any(d => d == null || !PathHelper.IsSafeRelativePath(d.OriginalRel)))
                throw new InvalidDataException("清单包含无效或不安全的目录记录，拒绝恢复");

            if (manifest.Files.Count == 0) Log("WARN", "活动清单为空，没有文件需要恢复。");

            // 2. 先行恢复目录结构（包含空目录）
            int restoredDirsCount = 0;
            if (manifest.Directories != null && manifest.Directories.Count > 0)
            {
                Log("INFO", $"正在恢复 {manifest.Directories.Count} 个原始目录结构（包含空目录）...");
                foreach (var d in manifest.Directories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string dirTarget = Path.Combine(longLocal, ToLocalSeparators(d.OriginalRel));
                    bool existed = Directory.Exists(dirTarget);
                    try
                    {
                        EnsureSafeRestoreDirectory(longLocal, dirTarget);
                        if (!existed) restoredDirsCount++;
                    }
                    catch (Exception ex) { throw new IOException($"恢复目录失败: {d.OriginalRel}", ex); }
                }
                report.RestoredDirectories = restoredDirsCount;
            }

            // 建立 stored_rel -> ManifestFileEntry 的反向映射字典
            var storedToEntry = new Dictionary<string, ManifestFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files)
            {
                storedToEntry[PathHelper.NormalizeRelPath(f.StoredRel)] = f;
            }

            // 3. 只恢复活动清单中的文件，不能把遗留物理文件当作当前版本。
            var nasFiles = new List<FileInfo>();
            foreach (var file in manifest.Files)
            {
                if (!PathHelper.IsSafeRelativePath(file.StoredRel))
                    throw new InvalidDataException($"清单包含不安全的存储路径: {file.StoredRel}");
                nasFiles.Add(new FileInfo(Path.Combine(longNas, ToLocalSeparators(file.StoredRel))));
            }

            report.TotalFiles = nasFiles.Count;
            Log("INFO", $"NAS 上共扫描到 {report.TotalFiles} 个有效备份文件，开始执行完整性还原...");

            int restoredCount = 0;
            int skippedCount = 0;
            int longNameRestoredCount = 0;
            int integrityOkCount = 0;
            int failedCount = 0;

            for (int i = 0; i < nasFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var nasFi = nasFiles[i];
                string storedRel = PathHelper.NormalizeRelPath(Path.GetRelativePath(longNas, nasFi.FullName));
                report.CurrentFile = storedRel;

                // 确定目标本地文件相对路径
                string targetRel = storedRel;
                string expectedSha256 = "";
                long expectedModTimeTicks = 0;
                bool isLong = false;

                if (storedToEntry.TryGetValue(storedRel, out var entry))
                {
                    targetRel = entry.OriginalRel;
                    expectedSha256 = entry.Sha256;
                    expectedModTimeTicks = entry.ModTimeUtcTicks;
                    isLong = entry.IsLongName;
                }

                // 路径安全性检查（严格防止跨目录越界）
                if (!PathHelper.IsSafeRelativePath(targetRel))
                {
                    failedCount++;
                    report.FailedFiles = failedCount;
                    report.FileResults.Add(new RestoreFileResult { OriginalRel = targetRel, StoredRel = storedRel, Success = false, ErrorMessage = "不安全的清单路径" });
                    Log("ERROR", $"[安全拦截] 文件相对路径不安全（含路径遍历字符）: {targetRel}");
                    continue;
                }

                string targetLocalFull = Path.Combine(longLocal, ToLocalSeparators(targetRel));
                string targetDir = Path.GetDirectoryName(targetLocalFull)!;

                // 冲突处理
                if (File.Exists(targetLocalFull))
                {
                    if (conflictResolution == ConflictResolution.SkipExisting)
                    {
                        skippedCount++;
                        report.SkippedFiles = skippedCount;
                        report.FileResults.Add(new RestoreFileResult { OriginalRel = targetRel, StoredRel = storedRel, Skipped = true, Success = true });
                        ProgressChanged?.Invoke(report);
                        continue;
                    }
                    else if (conflictResolution == ConflictResolution.RenameWithTimestamp)
                    {
                        string dir = Path.GetDirectoryName(targetLocalFull)!;
                        string stem = Path.GetFileNameWithoutExtension(targetLocalFull);
                        string ext = Path.GetExtension(targetLocalFull);
                        string timestamped = $"{stem}_恢复_{Guid.NewGuid():N}{ext}";
                        targetLocalFull = Path.Combine(dir, timestamped);
                    }
                }

                // 使用固定短随机名暂存 (.fnsync_rst_{Guid:N}.tmp)，彻底避免长文件名追加后缀导致突破 255 字节上限
                string tempRestorePath = Path.Combine(targetDir, $".fnsync_rst_{Guid.NewGuid():N}.tmp");

                try
                {
                    EnsureSafeRestoreDirectory(longLocal, targetDir);
                    if (string.IsNullOrWhiteSpace(expectedSha256)) throw new IOException("清单缺少 SHA-256，无法验证恢复内容");

                    // 1. 先恢复到临时文件并实时计算 SHA-256
                    string computedHash = "";
                    using (var src = new FileStream(nasFi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
                    using (var dest = new FileStream(tempRestorePath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
                    using (var sha = SHA256.Create())
                    {
                        byte[] buf = new byte[128 * 1024];
                        int r;
                        while ((r = await src.ReadAsync(buf, 0, buf.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await dest.WriteAsync(buf.AsMemory(0, r), cancellationToken).ConfigureAwait(false);
                            sha.TransformBlock(buf, 0, r, null, 0);
                        }
                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        computedHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                        await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    // 2. SHA-256 完整性强校验
                    if (!string.IsNullOrEmpty(expectedSha256))
                    {
                        if (!string.Equals(expectedSha256, computedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            // 校验失败：坚决删除损坏的临时文件，绝不计入成功！
                            var mismatch = CleanupFailedRestoreTemp(tempRestorePath, new InvalidDataException("SHA-256 校验失败"));

                            failedCount++;
                            report.FailedFiles = failedCount;
                            report.FileResults.Add(new RestoreFileResult { OriginalRel = targetRel, StoredRel = storedRel, Success = false, ErrorMessage = mismatch.Message, DetailedException = mismatch.ToString() });
                            Log("ERROR", $"[恢复失败] {targetRel}: {mismatch.Message}");
                            ProgressChanged?.Invoke(report);
                            continue;
                        }
                    }

                    // 3. 还原修改时间
                    if (expectedModTimeTicks > 0)
                    {
                        File.SetLastWriteTimeUtc(tempRestorePath, new DateTime(expectedModTimeTicks, DateTimeKind.Utc));
                    }
                    else
                    {
                        File.SetLastWriteTimeUtc(tempRestorePath, nasFi.LastWriteTimeUtc);
                    }

                    // 4. 原子移动到最终目标位置
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(tempRestorePath, targetLocalFull, overwrite: conflictResolution == ConflictResolution.Overwrite);

                    restoredCount++;
                    integrityOkCount++;
                    if (isLong) longNameRestoredCount++;
                    report.RestoredFiles = restoredCount;
                    report.LongNameRestoredFiles = longNameRestoredCount;
                    report.IntegrityVerifiedFiles = integrityOkCount;
                    report.FileResults.Add(new RestoreFileResult { OriginalRel = targetRel, StoredRel = storedRel, Success = true, HashVerified = true });
                }
                catch (OperationCanceledException canceled)
                {
                    DeleteCanceledTemporaryFile(tempRestorePath, canceled);
                    throw;
                }
                catch (Exception ex)
                {
                    var reported = CleanupFailedRestoreTemp(tempRestorePath, ex);
                    failedCount++;
                    report.FailedFiles = failedCount;
                    report.FileResults.Add(new RestoreFileResult { OriginalRel = targetRel, StoredRel = storedRel, Success = false, ErrorMessage = reported.Message, DetailedException = reported.ToString() });
                    Log("ERROR", $"[恢复失败] {targetRel} : {reported.Message}");
                }

                ProgressChanged?.Invoke(report);
            }

            report.StatusMessage = BuildRestoreStatus(report);
            Log(failedCount > 0 ? "WARN" : "INFO", report.StatusMessage);
            return report;
        }

        /// <summary>
        /// 从 NAS 统一存储提供程序执行异机还原
        /// </summary>
        public async Task<RestoreProgressReport> RunRestoreAsync(
            INasStorageProvider storageProvider,
            string nasBackupFolderRel,
            string localDestinationFolder,
            ConflictResolution conflictResolution = ConflictResolution.SkipExisting,
            CancellationToken cancellationToken = default)
        {
            if (storageProvider == null) throw new ArgumentNullException(nameof(storageProvider));

            if (storageProvider is SmbStorageProvider && (nasBackupFolderRel.StartsWith(@"\\") || nasBackupFolderRel.Contains(@":\")))
            {
                return await RunRestoreAsync(nasBackupFolderRel, localDestinationFolder, conflictResolution, cancellationToken).ConfigureAwait(false);
            }

            var manifest = await _manifestManager.LoadManifestAsync(storageProvider, nasBackupFolderRel, cancellationToken).ConfigureAwait(false);
            return await RunRestoreManifestAsync(storageProvider, nasBackupFolderRel, localDestinationFolder,
                manifest, conflictResolution, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Explicit experimental restore for an already versioned backup; not used by the UI.</summary>
        public async Task<RestoreProgressReport> RunRestoreVersionedAsync(
            INasStorageProvider storageProvider,
            string nasBackupFolderRel,
            string localDestinationFolder,
            ConflictResolution conflictResolution = ConflictResolution.SkipExisting,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(storageProvider);
            var snapshot = await new VersionedManifestStore().LoadAsync(storageProvider, nasBackupFolderRel, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("未找到有效的版本化清单");
            return await RunRestoreManifestAsync(storageProvider, nasBackupFolderRel, localDestinationFolder,
                snapshot.Manifest, conflictResolution, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RestoreProgressReport> RunRestorePackageAsync(INasStorageProvider provider,
            DiscoveredBackupPackage package, string destination,
            ConflictResolution conflictResolution = ConflictResolution.SkipExisting,
            CancellationToken cancellationToken = default)
        {
            var manifest = await ReadPackageManifestAsync(provider, package, cancellationToken).ConfigureAwait(false);
            if (manifest.IsNew) throw new InvalidDataException("备份清单已不存在，请重新扫描");
            return await RunRestoreManifestAsync(provider, package.NasFolderUnc, destination,
                manifest, conflictResolution, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RestoreProgressReport> RetryRestorePackageAsync(INasStorageProvider provider,
            DiscoveredBackupPackage package, string destination, IEnumerable<string> originalPaths,
            ConflictResolution conflictResolution = ConflictResolution.SkipExisting,
            CancellationToken cancellationToken = default)
        {
            var requested = new HashSet<string>(originalPaths, StringComparer.OrdinalIgnoreCase);
            var manifest = await ReadPackageManifestAsync(provider, package, cancellationToken).ConfigureAwait(false);
            if (manifest.IsNew) throw new InvalidDataException("备份清单已不存在，请重新扫描");
            var selected = manifest.Files.Where(f => requested.Contains(f.OriginalRel)).ToList();
            if (requested.Count == 0 || selected.Count != requested.Count)
                throw new InvalidDataException("重试文件与当前备份清单不一致，请重新扫描");
            manifest.Files = selected;
            return await RunRestoreFilteredAsync(provider, package.NasFolderUnc, destination,
                manifest, conflictResolution, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RestoreProgressReport> RestorePackageHistoryAsync(INasStorageProvider provider,
            DiscoveredBackupPackage package, string destination, ManifestHistoryEntry requested,
            ConflictResolution conflictResolution = ConflictResolution.SkipExisting,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(requested);
            var manifest = await ReadPackageManifestAsync(provider, package, cancellationToken).ConfigureAwait(false);
            if (manifest.IsNew || manifest.History == null) throw new InvalidDataException("历史清单不可用，请重新扫描");
            var matches = manifest.History.Where(h => h != null &&
                string.Equals(h.OriginalRel, requested.OriginalRel, StringComparison.Ordinal) &&
                string.Equals(h.StoredRel, requested.StoredRel, StringComparison.Ordinal) &&
                string.Equals(h.Sha256, requested.Sha256, StringComparison.OrdinalIgnoreCase) &&
                h.Size == requested.Size && h.ArchivedAt == requested.ArchivedAt).ToList();
            if (matches.Count != 1) throw new InvalidDataException("历史记录已变化或无法唯一确认，请重新扫描");
            return await RunRestoreHistoryFileDetailedAsync(provider, package.NasFolderUnc, destination,
                matches[0], cancellationToken, conflictResolution).ConfigureAwait(false);
        }

        private async Task<RestoreProgressReport> RunRestoreManifestAsync(
            INasStorageProvider storageProvider,
            string nasBackupFolderRel,
            string localDestinationFolder,
            Manifest manifest,
            ConflictResolution conflictResolution,
            CancellationToken cancellationToken)
        {
            var report = new RestoreProgressReport();

            if (manifest.Files == null || manifest.Directories == null ||
                manifest.Directories.Any(dir => dir == null || !PathHelper.IsSafeRelativePath(dir.OriginalRel)))
                throw new InvalidDataException("清单包含无效或不安全的目录记录，拒绝恢复");

            report.TotalFiles = manifest.Files.Count;
            Log("INFO", $"开始从 NAS 恢复任务: 备份源【{nasBackupFolderRel}】 -> 本地【{localDestinationFolder}】，共 {report.TotalFiles} 个文件");

            string longLocal = PathHelper.EnsureLongPathPrefix(localDestinationFolder);
            if (!Directory.Exists(longLocal))
            {
                Directory.CreateDirectory(longLocal);
            }

            // 1. 恢复空目录
            foreach (var dir in manifest.Directories)
            {
                string dirRel = PathHelper.NormalizeRelPath(dir.OriginalRel);
                if (PathHelper.IsSafeRelativePath(dir.OriginalRel))
                {
                    string targetDir = Path.Combine(longLocal, ToLocalSeparators(dirRel));
                    if (!Directory.Exists(targetDir))
                    {
                        EnsureSafeRestoreDirectory(longLocal, targetDir);
                        report.RestoredDirectories++;
                    }
                }
            }

            // 2. 恢复文件并流式校验 SHA-256
            for (int i = 0; i < manifest.Files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileEntry = manifest.Files[i];
                report.CurrentFile = fileEntry.OriginalRel;

                string targetRel = PathHelper.NormalizeRelPath(fileEntry.OriginalRel);
                if (!PathHelper.IsSafeRelativePath(fileEntry.OriginalRel) || !PathHelper.IsSafeRelativePath(fileEntry.StoredRel))
                {
                    report.FailedFiles++;
                    report.FileResults.Add(new RestoreFileResult
                    {
                        OriginalRel = targetRel,
                        StoredRel = fileEntry.StoredRel,
                        Success = false,
                        ErrorMessage = $"[安全拦截] 文件相对路径不安全: {targetRel}"
                    });
                    Log("ERROR", $"[安全拦截] 文件相对路径不安全: {targetRel}");
                    continue;
                }

                string targetLocalFull = Path.Combine(longLocal, ToLocalSeparators(targetRel));
                string targetDir = Path.GetDirectoryName(targetLocalFull)!;

                if (File.Exists(targetLocalFull))
                {
                    if (conflictResolution == ConflictResolution.SkipExisting)
                    {
                        report.SkippedFiles++;
                        report.FileResults.Add(new RestoreFileResult
                        {
                            OriginalRel = targetRel,
                            StoredRel = fileEntry.StoredRel,
                            Skipped = true,
                            Success = true
                        });
                        ProgressChanged?.Invoke(report);
                        continue;
                    }
                    else if (conflictResolution == ConflictResolution.RenameWithTimestamp)
                    {
                        string stem = Path.GetFileNameWithoutExtension(targetLocalFull);
                        string ext = Path.GetExtension(targetLocalFull);
                        targetLocalFull = Path.Combine(targetDir, $"{stem}_恢复_{Guid.NewGuid():N}{ext}");
                    }
                }

                string tempRestorePath = Path.Combine(targetDir, $".fnsync_rst_{Guid.NewGuid():N}.tmp");
                try
                {
                    EnsureSafeRestoreDirectory(longLocal, targetDir);
                    if (string.IsNullOrWhiteSpace(fileEntry.Sha256)) throw new IOException("清单缺少 SHA-256，无法验证恢复内容");
                    string remoteRel = string.IsNullOrEmpty(nasBackupFolderRel)
                        ? fileEntry.StoredRel
                        : $"{nasBackupFolderRel.TrimEnd('/', '\\')}/{fileEntry.StoredRel}";

                    string computedHash = "";
                    using (var src = await storageProvider.OpenReadAsync(remoteRel, cancellationToken).ConfigureAwait(false))
                    using (var dest = new FileStream(tempRestorePath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
                    using (var sha = SHA256.Create())
                    {
                        byte[] buf = new byte[128 * 1024];
                        int r;
                        while ((r = await src.ReadAsync(buf, 0, buf.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await dest.WriteAsync(buf.AsMemory(0, r), cancellationToken).ConfigureAwait(false);
                            sha.TransformBlock(buf, 0, r, null, 0);
                        }
                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        computedHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                        await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    // 强比对 SHA-256
                    if (!string.IsNullOrEmpty(fileEntry.Sha256) && !string.Equals(fileEntry.Sha256, computedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        var mismatch = CleanupFailedRestoreTemp(tempRestorePath, new InvalidDataException("SHA-256 校验失败"));
                        report.FailedFiles++;
                        report.FileResults.Add(new RestoreFileResult
                        {
                            OriginalRel = targetRel,
                            StoredRel = fileEntry.StoredRel,
                            Success = false,
                            ErrorMessage = mismatch.Message,
                            DetailedException = mismatch.ToString()
                        });
                        Log("ERROR", $"[SHA-256 校验失败] 恢复文件与原始数据哈希不一致！文件: {targetRel}");
                        ProgressChanged?.Invoke(report);
                        continue;
                    }
                    else
                    {
                        report.IntegrityVerifiedFiles++;
                    }

                    if (fileEntry.ModTimeUtcTicks > 0)
                    {
                        File.SetLastWriteTimeUtc(tempRestorePath, new DateTime(fileEntry.ModTimeUtcTicks, DateTimeKind.Utc));
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(tempRestorePath, targetLocalFull, overwrite: conflictResolution == ConflictResolution.Overwrite);
                    report.RestoredFiles++;
                    if (fileEntry.IsLongName) report.LongNameRestoredFiles++;

                    report.FileResults.Add(new RestoreFileResult
                    {
                        OriginalRel = targetRel,
                        StoredRel = fileEntry.StoredRel,
                        Success = true,
                        HashVerified = true
                    });
                }
                catch (OperationCanceledException canceled)
                {
                    DeleteCanceledTemporaryFile(tempRestorePath, canceled);
                    throw;
                }
                catch (Exception ex)
                {
                    var reported = CleanupFailedRestoreTemp(tempRestorePath, ex);
                    report.FailedFiles++;
                    report.FileResults.Add(new RestoreFileResult
                    {
                        OriginalRel = targetRel,
                        StoredRel = fileEntry.StoredRel,
                        Success = false,
                        ErrorMessage = reported.Message,
                        DetailedException = reported.ToString()
                    });
                    Log("ERROR", $"[恢复失败] {targetRel} : {reported.Message}");
                }

                ProgressChanged?.Invoke(report);
            }

            report.StatusMessage = BuildRestoreStatus(report);
            Log(report.FailedFiles > 0 ? "WARN" : "INFO", report.StatusMessage);
            return report;
        }

        /// <summary>
        /// 重新恢复之前失败的特定文件列表
        /// </summary>
        public async Task<RestoreProgressReport> RunRestoreSelectedFilesAsync(
            INasStorageProvider storageProvider,
            string nasBackupFolderRel,
            string localDestinationFolder,
            IEnumerable<string> originalRelsToRestore,
            ConflictResolution conflictResolution = ConflictResolution.SkipExisting,
            CancellationToken cancellationToken = default)
        {
            var targetSet = new HashSet<string>(originalRelsToRestore, StringComparer.OrdinalIgnoreCase);
            var manifest = await _manifestManager.LoadManifestAsync(storageProvider, nasBackupFolderRel, cancellationToken).ConfigureAwait(false);
            manifest.Files = manifest.Files.Where(f => targetSet.Contains(f.OriginalRel)).ToList();

            return await RunRestoreFilteredAsync(storageProvider, nasBackupFolderRel, localDestinationFolder, manifest, conflictResolution, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// 单独恢复一个特定的历史版本文件
        /// </summary>
        public async Task<bool> RunRestoreHistoryFileAsync(
            INasStorageProvider storageProvider,
            string nasBackupFolderRel,
            string localDestinationFolder,
            ManifestHistoryEntry historyEntry,
            CancellationToken cancellationToken = default,
            ConflictResolution conflictResolution = ConflictResolution.SkipExisting)
        {
            if (historyEntry == null) return false;
            var result = await RunRestoreHistoryFileDetailedAsync(storageProvider, nasBackupFolderRel, localDestinationFolder,
                historyEntry, cancellationToken, conflictResolution).ConfigureAwait(false);
            return result.RestoredFiles == 1;
        }

        /// <summary>Returns the complete result so a skipped history file is distinct from a failure.</summary>
        public async Task<RestoreProgressReport> RunRestoreHistoryFileDetailedAsync(
            INasStorageProvider storageProvider,
            string nasBackupFolderRel,
            string localDestinationFolder,
            ManifestHistoryEntry historyEntry,
            CancellationToken cancellationToken = default,
            ConflictResolution conflictResolution = ConflictResolution.SkipExisting)
        {
            ArgumentNullException.ThrowIfNull(historyEntry);
            var manifest = new Manifest { Files = new() { new ManifestFileEntry {
                OriginalRel = historyEntry.OriginalRel, StoredRel = historyEntry.StoredRel,
                Sha256 = historyEntry.Sha256, Size = historyEntry.Size
            } } };
            return await RunRestoreFilteredAsync(storageProvider, nasBackupFolderRel, localDestinationFolder,
                manifest, conflictResolution, cancellationToken).ConfigureAwait(false);
        }

        private async Task<RestoreProgressReport> RunRestoreFilteredAsync(
            INasStorageProvider storageProvider,
            string nasBackupFolderRel,
            string localDestinationFolder,
            Manifest manifest,
            ConflictResolution conflictResolution,
            CancellationToken cancellationToken)
        {
            var report = new RestoreProgressReport { TotalFiles = manifest.Files.Count };
            string longLocal = PathHelper.EnsureLongPathPrefix(localDestinationFolder);

            foreach (var fileEntry in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                report.CurrentFile = fileEntry.OriginalRel;
                if (!PathHelper.IsSafeRelativePath(fileEntry.OriginalRel) || !PathHelper.IsSafeRelativePath(fileEntry.StoredRel))
                {
                    report.FailedFiles++;
                    report.FileResults.Add(new RestoreFileResult { OriginalRel = fileEntry.OriginalRel, StoredRel = fileEntry.StoredRel, Success = false, ErrorMessage = "不安全的清单路径" });
                    continue;
                }
                string targetRel = PathHelper.NormalizeRelPath(fileEntry.OriginalRel);
                string targetLocalFull = Path.Combine(longLocal, ToLocalSeparators(targetRel));
                string targetDir = Path.GetDirectoryName(targetLocalFull)!;
                string tempRestorePath = Path.Combine(targetDir, $".fnsync_rst_retry_{Guid.NewGuid():N}.tmp");
                try
                {
                    EnsureSafeRestoreDirectory(longLocal, targetDir);
                    if (File.Exists(targetLocalFull))
                    {
                        if (conflictResolution == ConflictResolution.SkipExisting)
                        {
                            report.SkippedFiles++;
                            report.FileResults.Add(new RestoreFileResult { OriginalRel = targetRel, StoredRel = fileEntry.StoredRel, Skipped = true, Success = true });
                            continue;
                        }
                        if (conflictResolution == ConflictResolution.RenameWithTimestamp)
                            targetLocalFull = Path.Combine(targetDir, $"{Path.GetFileNameWithoutExtension(targetLocalFull)}_恢复_{Guid.NewGuid():N}{Path.GetExtension(targetLocalFull)}");
                    }
                    if (string.IsNullOrWhiteSpace(fileEntry.Sha256)) throw new IOException("清单缺少 SHA-256，无法验证恢复内容");
                    string remoteRel = string.IsNullOrEmpty(nasBackupFolderRel)
                        ? fileEntry.StoredRel
                        : $"{nasBackupFolderRel.TrimEnd('/', '\\')}/{fileEntry.StoredRel}";

                    string computedHash = "";
                    using (var src = await storageProvider.OpenReadAsync(remoteRel, cancellationToken).ConfigureAwait(false))
                    using (var dest = new FileStream(tempRestorePath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
                    using (var sha = SHA256.Create())
                    {
                        byte[] buf = new byte[128 * 1024];
                        int r;
                        while ((r = await src.ReadAsync(buf, 0, buf.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await dest.WriteAsync(buf.AsMemory(0, r), cancellationToken).ConfigureAwait(false);
                            sha.TransformBlock(buf, 0, r, null, 0);
                        }
                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        computedHash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                    }

                    if (!string.IsNullOrEmpty(fileEntry.Sha256) && !string.Equals(fileEntry.Sha256, computedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        var mismatch = CleanupFailedRestoreTemp(tempRestorePath, new InvalidDataException("SHA-256 校验失败"));
                        report.FailedFiles++;
                        report.FileResults.Add(new RestoreFileResult { OriginalRel = targetRel, StoredRel = fileEntry.StoredRel, Success = false, ErrorMessage = mismatch.Message, DetailedException = mismatch.ToString() });
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(tempRestorePath, targetLocalFull, overwrite: conflictResolution == ConflictResolution.Overwrite);
                    report.RestoredFiles++;
                    report.IntegrityVerifiedFiles++;
                    report.FileResults.Add(new RestoreFileResult { OriginalRel = targetRel, StoredRel = fileEntry.StoredRel, Success = true, HashVerified = true });
                }
                catch (OperationCanceledException canceled)
                {
                    DeleteCanceledTemporaryFile(tempRestorePath, canceled);
                    throw;
                }
                catch (Exception ex)
                {
                    var reported = CleanupFailedRestoreTemp(tempRestorePath, ex);
                    report.FailedFiles++;
                    report.FileResults.Add(new RestoreFileResult { OriginalRel = targetRel, StoredRel = fileEntry.StoredRel, Success = false, ErrorMessage = reported.Message, DetailedException = reported.ToString() });
                }

                ProgressChanged?.Invoke(report);
            }

            report.StatusMessage = BuildRestoreStatus(report);
            return report;
        }

        private static string BuildRestoreStatus(RestoreProgressReport report)
        {
            if (report.FailedFiles > 0)
                return $"{(report.RestoredFiles > 0 ? "恢复部分失败" : "恢复失败")}: 成功 {report.RestoredFiles}，跳过 {report.SkippedFiles}，失败 {report.FailedFiles}";
            if (report.SkippedFiles > 0)
                return $"{(report.RestoredFiles > 0 ? "恢复完成" : "已跳过现有文件")}: 成功 {report.RestoredFiles}，跳过 {report.SkippedFiles}";
            if (report.TotalFiles == 0 && report.RestoredDirectories == 0) return "没有需要恢复的文件或目录";
            return $"恢复完成: 文件 {report.RestoredFiles}，目录 {report.RestoredDirectories}，校验通过 {report.IntegrityVerifiedFiles}";
        }

        private static Exception CleanupFailedRestoreTemp(string path, Exception failure)
        {
            try { File.Delete(path); return failure; }
            catch (DirectoryNotFoundException) { return failure; }
            catch (Exception cleanupError)
            {
                return new AggregateException("恢复失败且无法清理临时文件", failure, cleanupError);
            }
        }

        private static void DeleteCanceledTemporaryFile(string path, OperationCanceledException canceled)
        {
            try { File.Delete(path); }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception cleanupError)
            {
                throw new IOException("恢复已取消，但无法清理临时文件", new AggregateException(canceled, cleanupError));
            }
        }

        private static void EnsureSafeRestoreDirectory(string root, string directory)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullDirectory = Path.GetFullPath(directory);
            if (!fullDirectory.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
                !fullDirectory.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("恢复路径越过目标目录");
            Directory.CreateDirectory(fullRoot);
            var current = new DirectoryInfo(fullRoot);
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("恢复目录不能是链接或联接点");
            string relative = Path.GetRelativePath(fullRoot, fullDirectory);
            foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (part == ".") continue;
                current = new DirectoryInfo(Path.Combine(current.FullName, part));
                if (!current.Exists) current.Create();
                current.Refresh();
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("恢复路径包含链接或联接点");
            }
        }
    }
}
