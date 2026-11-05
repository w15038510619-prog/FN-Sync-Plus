using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services
{
    public enum ConflictResolution
    {
        SkipExisting,
        Overwrite,
        RenameWithTimestamp
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

        private void Log(string level, string message)
        {
            LogEmitted?.Invoke(level, $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        }

        /// <summary>
        /// 在 NAS 备份根目录下自动发现所有包含清单元数据的备份任务包（完全不依赖原电脑本地配置）
        /// 例如在 \\server\私人备份 下自动发现【工作资料】、【项目文件】、【照片备份】
        /// </summary>
        public async Task<List<DiscoveredBackupPackage>> DiscoverBackupPackagesAsync(string nasBackupRootUnc, CancellationToken cancellationToken = default)
        {
            var result = new List<DiscoveredBackupPackage>();
            if (string.IsNullOrWhiteSpace(nasBackupRootUnc) || !Directory.Exists(nasBackupRootUnc))
            {
                return result;
            }

            await Task.Run(async () =>
            {
                string longRoot = PathHelper.EnsureLongPathPrefix(nasBackupRootUnc);
                var rootDi = new DirectoryInfo(longRoot);

                // 1. 检查根目录本身是否就是一个备份包
                string rootMeta = Path.Combine(longRoot, Manifest.MetadataDirName, Manifest.IsolatedManifestFileName);
                string legacyRootMeta = Path.Combine(longRoot, Manifest.LegacyManifestFileName);
                if (File.Exists(rootMeta) || File.Exists(legacyRootMeta))
                {
                    try
                    {
                        var m = await _manifestManager.LoadManifestAsync(nasBackupRootUnc, cancellationToken);
                        if (m.Files.Count > 0)
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
                    catch { }
                }

                // 2. 扫描根目录下的所有子文件夹
                try
                {
                    foreach (var sub in rootDi.EnumerateDirectories())
                    {
                        if (sub.Name.Equals(Manifest.MetadataDirName, StringComparison.OrdinalIgnoreCase) ||
                            sub.Name.StartsWith("$") ||
                            sub.Name.Equals(".recycle", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string subUnc = PathHelper.RemoveLongPathPrefix(sub.FullName);
                        string subMeta = Path.Combine(sub.FullName, Manifest.MetadataDirName, Manifest.IsolatedManifestFileName);
                        string legacySubMeta = Path.Combine(sub.FullName, Manifest.LegacyManifestFileName);

                        if (File.Exists(subMeta) || File.Exists(legacySubMeta))
                        {
                            try
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
                            catch { }
                        }
                    }
                }
                catch { }
            }, cancellationToken);

            return result;
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

            if (manifest.Files.Count == 0)
            {
                Log("WARN", "在 NAS 备份目录中未发现清单文件或记录为空，将尝试直接按物理文件名恢复。");
            }

            // 2. 先行恢复目录结构（包含空目录）
            int restoredDirsCount = 0;
            if (manifest.Directories != null && manifest.Directories.Count > 0)
            {
                Log("INFO", $"正在恢复 {manifest.Directories.Count} 个原始目录结构（包含空目录）...");
                foreach (var d in manifest.Directories)
                {
                    if (string.IsNullOrWhiteSpace(d.OriginalRel)) continue;
                    if (!PathHelper.IsSafeRelativePath(d.OriginalRel))
                    {
                        Log("WARN", $"[安全拦截] 忽略潜在目录遍历风险的清单目录: {d.OriginalRel}");
                        continue;
                    }

                    string dirTarget = Path.Combine(longLocal, d.OriginalRel.Replace('/', '\\'));
                    if (!Directory.Exists(dirTarget))
                    {
                        try
                        {
                            Directory.CreateDirectory(dirTarget);
                            restoredDirsCount++;
                        }
                        catch (Exception ex)
                        {
                            Log("WARN", $"创建目录失败: {d.OriginalRel} ({ex.Message})");
                        }
                    }
                }
                report.RestoredDirectories = restoredDirsCount;
            }

            // 建立 stored_rel -> ManifestFileEntry 的反向映射字典
            var storedToEntry = new Dictionary<string, ManifestFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files)
            {
                storedToEntry[PathHelper.NormalizeRelPath(f.StoredRel)] = f;
            }

            // 3. 扫描 NAS 备份目录中的物理文件，严格排除 .fnsync 专用目录及其他元数据文件
            var nasFiles = new List<FileInfo>();
            var nasDi = new DirectoryInfo(longNas);
            foreach (var fi in nasDi.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string relToNas = PathHelper.NormalizeRelPath(Path.GetRelativePath(longNas, fi.FullName));
                if (relToNas.StartsWith(Manifest.MetadataDirName + "/", StringComparison.OrdinalIgnoreCase) ||
                    relToNas.Equals(Manifest.MetadataDirName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string fn = fi.Name;
                if (fn.Equals(Manifest.LegacyManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                    fn.Equals(Manifest.LegacyGoManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                    fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                    fn.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    fn.Contains(".fn_part_") ||
                    fn.Contains(".restore_tmp_") ||
                    fn.StartsWith(".fnsync_rst_"))
                {
                    continue;
                }
                nasFiles.Add(fi);
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
                if (cancellationToken.IsCancellationRequested)
                {
                    Log("WARN", "恢复任务已被取消。");
                    break;
                }

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
                    Log("ERROR", $"[安全拦截] 文件相对路径不安全（含路径遍历字符）: {targetRel}");
                    continue;
                }

                if (isLong)
                {
                    longNameRestoredCount++;
                    Log("INFO", $"[还原长文件名] 存储名: {storedRel} -> 还原为原始长文件名: {targetRel}");
                }

                string targetLocalFull = Path.Combine(longLocal, targetRel.Replace('/', '\\'));
                string targetDir = Path.GetDirectoryName(targetLocalFull)!;

                // 冲突处理
                if (File.Exists(targetLocalFull))
                {
                    if (conflictResolution == ConflictResolution.SkipExisting)
                    {
                        skippedCount++;
                        report.SkippedFiles = skippedCount;
                        ProgressChanged?.Invoke(report);
                        continue;
                    }
                    else if (conflictResolution == ConflictResolution.RenameWithTimestamp)
                    {
                        string dir = Path.GetDirectoryName(targetLocalFull)!;
                        string stem = Path.GetFileNameWithoutExtension(targetLocalFull);
                        string ext = Path.GetExtension(targetLocalFull);
                        string timestamped = $"{stem}_恢复_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
                        targetLocalFull = Path.Combine(dir, timestamped);
                    }
                }

                // 使用固定短随机名暂存 (.fnsync_rst_{Guid:N}.tmp)，彻底避免长文件名追加后缀导致突破 255 字节上限
                string tempRestorePath = Path.Combine(targetDir, $".fnsync_rst_{Guid.NewGuid():N}.tmp");

                try
                {
                    if (!Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

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
                            if (File.Exists(tempRestorePath))
                            {
                                try { File.Delete(tempRestorePath); } catch { }
                            }

                            failedCount++;
                            report.FailedFiles = failedCount;
                            Log("ERROR", $"[SHA-256 校验失败] 恢复文件与原始数据哈希不一致！文件: {targetRel}\n  预期: {expectedSha256}\n  实际: {computedHash}。已自动删除损坏文件！");
                            ProgressChanged?.Invoke(report);
                            continue;
                        }
                        else
                        {
                            integrityOkCount++;
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
                    File.Move(tempRestorePath, targetLocalFull, overwrite: true);

                    restoredCount++;
                    report.RestoredFiles = restoredCount;
                    report.LongNameRestoredFiles = longNameRestoredCount;
                    report.IntegrityVerifiedFiles = integrityOkCount;
                }
                catch (Exception ex)
                {
                    // 发生异常时清理残留的临时文件
                    if (File.Exists(tempRestorePath))
                    {
                        try { File.Delete(tempRestorePath); } catch { }
                    }

                    failedCount++;
                    report.FailedFiles = failedCount;
                    Log("ERROR", $"[恢复失败] {targetRel} : {ex.Message}");
                }

                ProgressChanged?.Invoke(report);
            }

            report.StatusMessage = $"恢复操作完成！已成功恢复: {restoredCount}, 长文件名还原: {longNameRestoredCount}, 完整性校验通过: {integrityOkCount}, 空目录恢复: {restoredDirsCount}, 跳过已存在: {skippedCount}, 失败: {failedCount}";
            Log("INFO", report.StatusMessage);
            return report;
        }
    }
}
