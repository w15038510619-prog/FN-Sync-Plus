using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services
{
    public class ManifestLockHandle : IDisposable
    {
        private FileStream? _lockStream;

        public ManifestLockHandle(FileStream stream)
        {
            _lockStream = stream;
        }

        public void Dispose()
        {
            if (_lockStream != null)
            {
                try
                {
                    _lockStream.Dispose();
                }
                catch { }
                _lockStream = null;
            }
        }
    }

    /// <summary>
    /// 清单管理服务：负责清单在 .fnsync/ 专用隔离目录下的加载、跨版本兼容迁移与原子可靠持久化
    /// </summary>
    public class ManifestManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        private readonly SemaphoreSlim _memoryLock = new(1, 1);

        /// <summary>
        /// 获取 NAS 目标目录下的元数据专用隔离目录路径 (.fnsync)
        /// </summary>
        public static string GetMetadataDirectory(string targetRootDirectory)
        {
            string longTarget = PathHelper.EnsureLongPathPrefix(targetRootDirectory);
            return Path.Combine(longTarget, Manifest.MetadataDirName);
        }

        /// <summary>
        /// 获取临时分块暂存目录路径 (.fnsync/staging)
        /// </summary>
        public static string GetStagingDirectory(string targetRootDirectory)
        {
            return Path.Combine(GetMetadataDirectory(targetRootDirectory), Manifest.StagingDirName);
        }

        /// <summary>
        /// 尝试对目标 NAS 目录获取独占同步锁，防止多任务或跨机器并发写入同一个目标目录
        /// </summary>
        public async Task<ManifestLockHandle?> TryAcquireTargetLockAsync(string targetRootDirectory)
        {
            string metaDir = GetMetadataDirectory(targetRootDirectory);
            if (!Directory.Exists(metaDir))
            {
                try { Directory.CreateDirectory(metaDir); } catch { }
            }

            string lockPath = Path.Combine(metaDir, Manifest.LockFileName);

            return await Task.Run(() =>
            {
                try
                {
                    var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    return new ManifestLockHandle(stream);
                }
                catch
                {
                    // 无法获取独占共享锁，说明已有其他同步任务在操作该目录
                    return null;
                }
            });
        }

        /// <summary>
        /// 从目标目录读取清单（自动从 .fnsync/manifest.json 加载；若不存在自动检查并迁移旧版本根目录清单）
        /// </summary>
        public async Task<Manifest> LoadManifestAsync(string targetRootDirectory, CancellationToken cancellationToken = default)
        {
            await _memoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string longTarget = PathHelper.EnsureLongPathPrefix(targetRootDirectory);
                string metaDir = Path.Combine(longTarget, Manifest.MetadataDirName);
                string isolatedPath = Path.Combine(metaDir, Manifest.IsolatedManifestFileName);
                string backupPath = Path.Combine(metaDir, Manifest.IsolatedManifestFileName + ".bak");

                // 1. 优先从隔离元数据目录 .fnsync/manifest.json 读取
                if (File.Exists(isolatedPath))
                {
                    try
                    {
                        string json = await File.ReadAllTextAsync(isolatedPath, cancellationToken).ConfigureAwait(false);
                        var manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions);
                        if (manifest != null && manifest.Files != null)
                        {
                            return manifest;
                        }
                    }
                    catch
                    {
                        // 主清单文件可能在异常断电时损坏，尝试读取备用清单 .bak
                        if (File.Exists(backupPath))
                        {
                            try
                            {
                                string bakJson = await File.ReadAllTextAsync(backupPath, cancellationToken).ConfigureAwait(false);
                                var bakManifest = JsonSerializer.Deserialize<Manifest>(bakJson, JsonOptions);
                                if (bakManifest != null) return bakManifest;
                            }
                            catch { }
                        }
                    }
                }

                // 2. 兼容并自动迁移 v1/v2 位于备份根目录下的 __FN_SYNC_MANIFEST__.json
                string legacyRootPath = Path.Combine(longTarget, Manifest.LegacyManifestFileName);
                if (File.Exists(legacyRootPath))
                {
                    try
                    {
                        string legacyJson = await File.ReadAllTextAsync(legacyRootPath, cancellationToken).ConfigureAwait(false);
                        var manifest = JsonSerializer.Deserialize<Manifest>(legacyJson, JsonOptions);
                        if (manifest != null)
                        {
                            manifest.Note = "已自动从旧版本根目录清单迁移到 .fnsync 专用隔离空间";
                            manifest.Version = Manifest.CurrentVersion;
                            // 迁移完成后尝试保存到新目录
                            _ = SaveManifestAtomicInternalAsync(targetRootDirectory, manifest, cancellationToken);
                            return manifest;
                        }
                    }
                    catch { }
                }

                // 3. 兼容旧版 Go 清单 __FN_ORIGINAL_NAMES__.json
                string legacyGoPath = Path.Combine(longTarget, Manifest.LegacyGoManifestFileName);
                if (File.Exists(legacyGoPath))
                {
                    try
                    {
                        string legacyJson = await File.ReadAllTextAsync(legacyGoPath, cancellationToken).ConfigureAwait(false);
                        var manifest = ParseLegacyGoManifest(legacyJson);
                        _ = SaveManifestAtomicInternalAsync(targetRootDirectory, manifest, cancellationToken);
                        return manifest;
                    }
                    catch { }
                }

                // 4. 全新备份，返回初始清单对象
                return new Manifest
                {
                    Version = Manifest.CurrentVersion,
                    TargetRoot = PathHelper.RemoveLongPathPrefix(targetRootDirectory),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            finally
            {
                _memoryLock.Release();
            }
        }

        /// <summary>
        /// 原子写入清单到 NAS 目标目录的 .fnsync/manifest.json（写入临时文件 -> 强制落盘 -> 原子覆盖）
        /// </summary>
        public async Task SaveManifestAtomicAsync(string targetRootDirectory, Manifest manifest, CancellationToken cancellationToken = default)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            await _memoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SaveManifestAtomicInternalAsync(targetRootDirectory, manifest, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _memoryLock.Release();
            }
        }

        private async Task SaveManifestAtomicInternalAsync(string targetRootDirectory, Manifest manifest, CancellationToken cancellationToken)
        {
            string metaDir = GetMetadataDirectory(targetRootDirectory);
            if (!Directory.Exists(metaDir))
            {
                Directory.CreateDirectory(metaDir);
            }

            manifest.UpdatedAt = DateTimeOffset.UtcNow;
            string currentPath = Path.Combine(metaDir, Manifest.IsolatedManifestFileName);
            string tmpPath = Path.Combine(metaDir, Manifest.IsolatedManifestFileName + ".tmp");
            string bakPath = Path.Combine(metaDir, Manifest.IsolatedManifestFileName + ".bak");

            byte[] utf8Bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

            // 1. 写入临时文件并强行落盘 Flush
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await fs.WriteAsync(utf8Bytes, cancellationToken).ConfigureAwait(false);
                await fs.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // 2. 备份原清单
            if (File.Exists(currentPath))
            {
                try
                {
                    File.Copy(currentPath, bakPath, overwrite: true);
                }
                catch
                {
                    // 备份失败不阻塞主流程
                }
            }

            // 3. 原子移动覆盖
            File.Move(tmpPath, currentPath, overwrite: true);
        }

        private static Manifest ParseLegacyGoManifest(string legacyJson)
        {
            var manifest = new Manifest
            {
                Version = Manifest.CurrentVersion,
                Note = "从旧版 Go 清单迁移转换"
            };

            try
            {
                using var doc = JsonDocument.Parse(legacyJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in filesEl.EnumerateArray())
                    {
                        string orig = item.TryGetProperty("original", out var o) ? o.GetString() ?? "" : "";
                        string stored = item.TryGetProperty("stored", out var s) ? s.GetString() ?? "" : "";
                        long size = item.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        long mtimeNs = item.TryGetProperty("mtime_ns", out var mt) ? mt.GetInt64() : 0;
                        string sha = item.TryGetProperty("sha256", out var sh) ? sh.GetString() ?? "" : "";

                        DateTimeOffset mtimeIso = mtimeNs > 0
                            ? DateTimeOffset.FromUnixTimeMilliseconds(mtimeNs / 1_000_000)
                            : DateTimeOffset.UtcNow;

                        manifest.Files.Add(new ManifestFileEntry
                        {
                            OriginalRel = PathHelper.NormalizeRelPath(orig),
                            StoredRel = PathHelper.NormalizeRelPath(stored),
                            Size = size,
                            ModTimeUtcTicks = mtimeIso.UtcTicks,
                            ModTimeIso = mtimeIso,
                            Sha256 = sha,
                            IsLongName = !string.Equals(orig, stored, StringComparison.Ordinal)
                        });
                    }
                }

                if (root.TryGetProperty("directories", out var dirsEl) && dirsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dirsEl.EnumerateArray())
                    {
                        string orig = item.TryGetProperty("original", out var o) ? o.GetString() ?? "" : "";
                        string stored = item.TryGetProperty("stored", out var s) ? s.GetString() ?? "" : "";

                        manifest.Directories.Add(new ManifestDirEntry
                        {
                            OriginalRel = PathHelper.NormalizeRelPath(orig),
                            StoredRel = PathHelper.NormalizeRelPath(stored),
                            IsLongName = !string.Equals(orig, stored, StringComparison.Ordinal)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                manifest.Note = $"解析旧清单出现警告: {ex.Message}";
            }

            return manifest;
        }
    }
}
