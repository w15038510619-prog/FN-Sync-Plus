using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
        /// 预览备份目录中的文件清单与原始文件名映射关系（无需开始恢复）
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
        /// 并逐一进行 SHA-256 完整性检验
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

            var report = new RestoreProgressReport();
            Log("INFO", $"开始恢复任务: NAS【{nasBackupFolder}】 -> 本地【{localDestinationFolder}】");

            string longNas = PathHelper.EnsureLongPathPrefix(nasBackupFolder);
            string longLocal = PathHelper.EnsureLongPathPrefix(localDestinationFolder);

            if (!Directory.Exists(longLocal))
            {
                Directory.CreateDirectory(longLocal);
            }

            // 1. 读取 NAS 上的清单文件
            Log("INFO", "正在读取 NAS 备份元数据清单...");
            var manifest = await _manifestManager.LoadManifestAsync(nasBackupFolder, cancellationToken).ConfigureAwait(false);

            if (manifest.Files.Count == 0)
            {
                Log("WARN", "在 NAS 备份目录中未发现清单文件或记录为空，将尝试直接按物理文件名恢复。");
            }

            // 建立 stored_rel -> ManifestFileEntry 的反向映射字典
            var storedToEntry = new Dictionary<string, ManifestFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in manifest.Files)
            {
                storedToEntry[PathHelper.NormalizeRelPath(f.StoredRel)] = f;
            }

            // 2. 扫描 NAS 备份目录中的物理文件
            var nasFiles = new List<FileInfo>();
            var nasDi = new DirectoryInfo(longNas);
            foreach (var fi in nasDi.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                string fn = fi.Name;
                if (fn.Equals(Manifest.CurrentManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                    fn.Equals(Manifest.LegacyManifestFileName, StringComparison.OrdinalIgnoreCase) ||
                    fn.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ||
                    fn.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    fn.Contains(".fn_part_"))
                {
                    continue;
                }
                nasFiles.Add(fi);
            }

            report.TotalFiles = nasFiles.Count;
            Log("INFO", $"NAS 上共扫描到 {report.TotalFiles} 个有效备份文件，开始执行还原...");

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

                // 确定目标本地文件相对路径（若清单中有记录则恢复原名，否则保留存储名）
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

                try
                {
                    if (!Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    // 复制并实时计算 SHA-256
                    string computedHash = "";
                    using (var src = new FileStream(nasFi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, useAsync: true))
                    using (var dest = new FileStream(targetLocalFull, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.WriteThrough | FileOptions.Asynchronous))
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

                    // 还原修改时间
                    if (expectedModTimeTicks > 0)
                    {
                        File.SetLastWriteTimeUtc(targetLocalFull, new DateTime(expectedModTimeTicks, DateTimeKind.Utc));
                    }
                    else
                    {
                        File.SetLastWriteTimeUtc(targetLocalFull, nasFi.LastWriteTimeUtc);
                    }

                    // 完整性校验
                    if (!string.IsNullOrEmpty(expectedSha256))
                    {
                        if (string.Equals(expectedSha256, computedHash, StringComparison.OrdinalIgnoreCase))
                        {
                            integrityOkCount++;
                        }
                        else
                        {
                            Log("WARN", $"[完整性警告] 文件 {targetRel} SHA-256 不一致！预期: {expectedSha256}, 实际: {computedHash}");
                        }
                    }

                    restoredCount++;
                    report.RestoredFiles = restoredCount;
                    report.LongNameRestoredFiles = longNameRestoredCount;
                    report.IntegrityVerifiedFiles = integrityOkCount;
                }
                catch (Exception ex)
                {
                    failedCount++;
                    report.FailedFiles = failedCount;
                    Log("ERROR", $"[恢复失败] {targetRel} : {ex.Message}");
                }

                ProgressChanged?.Invoke(report);
            }

            report.StatusMessage = $"恢复操作完成！已成功恢复: {restoredCount}, 长文件名还原: {longNameRestoredCount}, 完整性校验通过: {integrityOkCount}, 跳过已存在: {skippedCount}, 失败: {failedCount}";
            Log("INFO", report.StatusMessage);
            return report;
        }
    }
}
