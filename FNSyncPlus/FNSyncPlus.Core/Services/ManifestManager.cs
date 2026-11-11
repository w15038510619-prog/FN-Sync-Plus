using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
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
        public static string GetLocalCachePath(string targetFolder)
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string safeDir = PathHelper.RemoveLongPathPrefix(targetFolder).Trim().Replace('/', '_').Replace('\\', '_').Replace(':', '_');
            return Path.Combine(localApp, "FNSyncPlus", "manifest_cache", safeDir, "manifest.json");
        }

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

        private static void RejectVersionedMetadata(string metadataDirectory)
        {
            foreach (string name in new[] { "manifest-versions", "lock-generations" })
            {
                string path = Path.Combine(metadataDirectory, name);
                try { _ = File.GetAttributes(path); }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                throw new InvalidDataException("备份存在版本化迁移或锁记录，旧版清单入口拒绝读写；请使用受支持的恢复流程");
            }
        }

        private static async Task RejectVersionedMetadataAsync(INasStorageProvider provider, string metadataDirectory, CancellationToken token)
        {
            foreach (string name in new[] { "manifest-versions", "lock-generations" })
                if (await provider.GetItemAsync($"{metadataDirectory}/{name}", token).ConfigureAwait(false) != null)
                    throw new InvalidDataException("备份存在版本化迁移或锁记录，旧版清单入口拒绝读写；请使用受支持的恢复流程");
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
                RejectVersionedMetadata(metaDir);

                // 1. 优先从隔离元数据目录 .fnsync/manifest.json 读取
                if (File.Exists(isolatedPath) || File.Exists(backupPath))
                {
                    Exception? lastError = null;
                    foreach (string candidate in new[] { isolatedPath, backupPath })
                    {
                        if (!File.Exists(candidate)) continue;
                        try
                        {
                            string json = await File.ReadAllTextAsync(candidate, cancellationToken).ConfigureAwait(false);
                            var manifest = ParseManifest(Encoding.UTF8.GetBytes(json));
                            return manifest;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { lastError = ex; }
                    }
                    throw new InvalidDataException("已有清单及其备份均无法读取，拒绝创建空清单", lastError);
                }

                // 2. 兼容并自动迁移 v1/v2 位于备份根目录下的 __FN_SYNC_MANIFEST__.json
                string legacyRootPath = Path.Combine(longTarget, Manifest.LegacyManifestFileName);
                if (File.Exists(legacyRootPath))
                {
                    try
                    {
                        string legacyJson = await File.ReadAllTextAsync(legacyRootPath, cancellationToken).ConfigureAwait(false);
                        var manifest = ParseManifest(Encoding.UTF8.GetBytes(legacyJson));
                        if (manifest != null)
                        {
                            manifest.Note = "已读取旧版本根目录清单";
                            manifest.Version = Manifest.CurrentVersion;
                            return manifest;
                        }
                        throw new InvalidDataException("旧版清单为空");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { throw new InvalidDataException("无法读取旧版清单，拒绝创建空清单", ex); }
                }

                // 3. 兼容旧版 Go 清单 __FN_ORIGINAL_NAMES__.json
                string legacyGoPath = Path.Combine(longTarget, Manifest.LegacyGoManifestFileName);
                if (File.Exists(legacyGoPath))
                {
                    try
                    {
                        string legacyJson = await File.ReadAllTextAsync(legacyGoPath, cancellationToken).ConfigureAwait(false);
                        var manifest = ParseLegacyGoManifest(legacyJson);
                        return manifest;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { throw new InvalidDataException("无法读取旧版 Go 清单，拒绝创建空清单", ex); }
                }

                // 4. 全新备份，返回初始清单对象
                return new Manifest
                {
                    Version = Manifest.CurrentVersion,
                    IsNew = true,
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

        /// <summary>
        /// 从 NAS 统一存储提供程序读取清单 (.fnsync/manifest.json)
        /// </summary>
        public async Task<Manifest> LoadManifestAsync(INasStorageProvider storageProvider, string targetFolder, CancellationToken cancellationToken = default)
        {
            if (storageProvider == null) throw new ArgumentNullException(nameof(storageProvider));

            // 若为本地/UNC SMB 共享且不以相对路径模式运行，优先复用原生文件系统逻辑
            if (storageProvider is SmbStorageProvider && (targetFolder.StartsWith(@"\\") || targetFolder.Contains(@":\")))
            {
                return await LoadManifestAsync(targetFolder, cancellationToken).ConfigureAwait(false);
            }

            await _memoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string cleanFolder = targetFolder.Trim().TrimEnd('/', '\\').Replace('\\', '/');
                string manifestRelPath = string.IsNullOrEmpty(cleanFolder) ? ".fnsync/manifest.json" : $"{cleanFolder}/.fnsync/manifest.json";
                string metaDir = string.IsNullOrEmpty(cleanFolder) ? ".fnsync" : $"{cleanFolder}/.fnsync";
                if (!await storageProvider.ExistsAsync(metaDir, cancellationToken).ConfigureAwait(false))
                    return new Manifest { Version = Manifest.CurrentVersion, IsNew = true, TargetRoot = targetFolder, UpdatedAt = DateTimeOffset.UtcNow };
                await RejectVersionedMetadataAsync(storageProvider, metaDir, cancellationToken).ConfigureAwait(false);

                string bakRelPath = manifestRelPath + ".bak";
                bool hasMain = await storageProvider.ExistsAsync(manifestRelPath, cancellationToken).ConfigureAwait(false);
                bool hasBackup = await storageProvider.ExistsAsync(bakRelPath, cancellationToken).ConfigureAwait(false);
                if (hasMain || hasBackup)
                {
                    Exception? lastError = null;
                    foreach (string candidate in new[] { manifestRelPath, bakRelPath })
                    {
                        if (candidate == manifestRelPath ? !hasMain : !hasBackup) continue;
                        try
                        {
                            using var stream = await storageProvider.OpenReadAsync(candidate, cancellationToken).ConfigureAwait(false);
                            using var buffer = new MemoryStream();
                            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                            var manifest = ParseManifest(buffer.ToArray());
                            return manifest;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) { lastError = ex; }
                    }
                    throw new InvalidDataException("已有远端清单及其备份均无法读取，拒绝创建空清单", lastError);
                }

                // 远端尚未建立清单，初始化并返回全新清单
                return new Manifest
                {
                    Version = Manifest.CurrentVersion,
                    IsNew = true,
                    TargetRoot = targetFolder,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            finally
            {
                _memoryLock.Release();
            }
        }

        /// <summary>
        /// 将清单通过存储提供程序持久化至远端 .fnsync/manifest.json (流式上传并保留 .bak 副本)
        /// </summary>
        public async Task SaveManifestAtomicAsync(INasStorageProvider storageProvider, string targetFolder, Manifest manifest, CancellationToken cancellationToken = default)
        {
            if (storageProvider == null) throw new ArgumentNullException(nameof(storageProvider));
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            if (storageProvider is SmbStorageProvider && (targetFolder.StartsWith(@"\\") || targetFolder.Contains(@":\")))
            {
                await SaveManifestAtomicAsync(targetFolder, manifest, cancellationToken).ConfigureAwait(false);
                return;
            }

            await _memoryLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                manifest.UpdatedAt = DateTimeOffset.UtcNow;
                string cleanFolder = targetFolder.Trim().TrimEnd('/', '\\').Replace('\\', '/');
                string metaDir = string.IsNullOrEmpty(cleanFolder) ? ".fnsync" : $"{cleanFolder}/.fnsync";
                var directoryResult = await storageProvider.CreateDirectoryAsync(metaDir, cancellationToken).ConfigureAwait(false);
                if (!directoryResult.Success) throw new IOException($"无法创建清单目录: {directoryResult.ErrorMessage}");
                await RejectVersionedMetadataAsync(storageProvider, metaDir, cancellationToken).ConfigureAwait(false);

                byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

                string manifestPath = $"{metaDir}/manifest.json";
                string bakPath = manifestPath + ".bak";
                // 候选文件使用唯一名称，写入后重新读取验证；旧主清单此时仍未改变。
                string candidatePath = $"{metaDir}/manifest-{Guid.NewGuid():N}.candidate";
                await UploadAndVerifyAsync(storageProvider, candidatePath, manifestBytes, false, cancellationToken).ConfigureAwait(false);

                if (await storageProvider.ExistsAsync(manifestPath, cancellationToken).ConfigureAwait(false))
                {
                    byte[] previous;
                    using (var old = await storageProvider.OpenReadAsync(manifestPath, cancellationToken).ConfigureAwait(false))
                    using (var buffer = new MemoryStream())
                    {
                        await old.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                        previous = buffer.ToArray();
                    }
                    // 不允许以损坏的主清单覆盖可能唯一有效的备份。
                    try
                    {
                        _ = ParseManifest(previous);
                    }
                    catch (JsonException ex) { throw new InvalidDataException("旧主清单损坏，拒绝覆盖备份", ex); }
                    await UploadAndVerifyAsync(storageProvider, bakPath, previous, true, cancellationToken).ConfigureAwait(false);
                }
                await UploadAndVerifyAsync(storageProvider, manifestPath, manifestBytes, true, cancellationToken).ConfigureAwait(false);

            }
            finally
            {
                _memoryLock.Release();
            }
        }

        private static async Task UploadAndVerifyAsync(INasStorageProvider provider, string path, byte[] bytes, bool overwrite, CancellationToken token)
        {
            using var source = new MemoryStream(bytes, writable: false);
            var result = await provider.UploadFileAsync(path, source, bytes.Length, DateTime.UtcNow, overwrite, null, token).ConfigureAwait(false);
            if (!result.Success) throw new IOException($"远端清单写入失败: {result.ErrorMessage}");
            using var readback = await provider.OpenReadAsync(path, token).ConfigureAwait(false);
            byte[] actual = await SHA256.HashDataAsync(readback, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(actual, SHA256.HashData(bytes)))
                throw new InvalidDataException($"远端清单回读校验失败: {path}");
        }

        private static Manifest ParseManifest(byte[] bytes)
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.EnumerateObject().Any(p =>
                    p.Name.Equals("files", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array))
                throw new InvalidDataException("清单缺少有效的 files 数组");
            var manifest = JsonSerializer.Deserialize<Manifest>(bytes, JsonOptions);
            return manifest?.Files != null ? manifest : throw new InvalidDataException("清单文件列表无效");
        }

        private async Task SaveManifestAtomicInternalAsync(string targetRootDirectory, Manifest manifest, CancellationToken cancellationToken)
        {
            string metaDir = GetMetadataDirectory(targetRootDirectory);
            if (!Directory.Exists(metaDir))
            {
                Directory.CreateDirectory(metaDir);
            }

            RejectVersionedMetadata(metaDir);

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
                _ = ParseManifest(await File.ReadAllBytesAsync(currentPath, cancellationToken).ConfigureAwait(false));
                File.Copy(currentPath, bakPath, overwrite: true);
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
            catch (Exception ex) { throw new InvalidDataException("旧版 Go 清单格式无效", ex); }

            return manifest;
        }
    }
}
