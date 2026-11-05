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
    /// <summary>
    /// 清单管理服务：负责清单的加载、跨版本兼容与原子可靠写入
    /// </summary>
    public class ManifestManager
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        private readonly SemaphoreSlim _fileLock = new(1, 1);

        /// <summary>
        /// 从目标目录读取清单（若新版本清单不存在，自动兼容旧版 __FN_ORIGINAL_NAMES__.json）
        /// </summary>
        public async Task<Manifest> LoadManifestAsync(string targetRootDirectory, CancellationToken cancellationToken = default)
        {
            await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string targetDir = PathHelper.EnsureLongPathPrefix(targetRootDirectory);
                string currentPath = Path.Combine(targetDir, Manifest.CurrentManifestFileName);
                string legacyPath = Path.Combine(targetDir, Manifest.LegacyManifestFileName);

                if (File.Exists(currentPath))
                {
                    string json = await File.ReadAllTextAsync(currentPath, cancellationToken).ConfigureAwait(false);
                    var manifest = JsonSerializer.Deserialize<Manifest>(json, JsonOptions);
                    if (manifest != null) return manifest;
                }

                if (File.Exists(legacyPath))
                {
                    // 兼容旧版清单结构
                    string legacyJson = await File.ReadAllTextAsync(legacyPath, cancellationToken).ConfigureAwait(false);
                    return ParseLegacyManifest(legacyJson);
                }

                return new Manifest
                {
                    Version = Manifest.CurrentVersion,
                    TargetRoot = PathHelper.RemoveLongPathPrefix(targetRootDirectory),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            finally
            {
                _fileLock.Release();
            }
        }

        /// <summary>
        /// 原子写入清单到 NAS 目标目录（写临时文件 -> 强制落盘 -> 原子覆盖），确保写入完整性
        /// </summary>
        public async Task SaveManifestAtomicAsync(string targetRootDirectory, Manifest manifest, CancellationToken cancellationToken = default)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));

            await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string targetDir = PathHelper.EnsureLongPathPrefix(targetRootDirectory);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                manifest.UpdatedAt = DateTimeOffset.UtcNow;
                string currentPath = Path.Combine(targetDir, Manifest.CurrentManifestFileName);
                string tmpPath = Path.Combine(targetDir, Manifest.CurrentManifestFileName + ".tmp");
                string bakPath = Path.Combine(targetDir, Manifest.CurrentManifestFileName + ".bak");

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
            finally
            {
                _fileLock.Release();
            }
        }

        private static Manifest ParseLegacyManifest(string legacyJson)
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
