using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services
{
    /// <summary>
    /// 长文件名与超长路径映射引擎
    /// 自动识别超长文件名与目录名（以 Linux 文件系统 UTF-8 255 字节为硬边界）
    /// 转换为稳定唯一短别名并记录对应关系，普通文件 100% 保持原名
    /// </summary>
    public class LongNameEngine
    {
        public const int DefaultMaxUtf8Bytes = 255;
        /// <summary>
        /// 飞牛 fnOS 个人空间 HTTP 上传接口实测安全阈值。
        /// Linux 内核单层文件名上限为 255 字节，服务端在处理直传时会附加约 4-5 字节的临时暂存后缀（如 .~#0 或 .part），
        /// 导致原始文件名 >= 252 字节时服务端中间临时文件名溢出 255 字节上限 (errno: 4104, Path Is Too Long)。
        /// 因此将原生上传 Provider 的安全直传阈值设为 251 字节，>= 252 字节自动安全映射物理短别名。
        /// </summary>
        public const int FnOsUploadMaxUtf8Bytes = 251;
        public const string LongNamePrefix = "_FN_LONG_";

        private readonly int _maxFileUtf8Bytes;
        private readonly int _maxDirUtf8Bytes;

        public int MaxFileUtf8Bytes => _maxFileUtf8Bytes;
        public int MaxDirUtf8Bytes => _maxDirUtf8Bytes;

        // Key: 小写 stored_rel, Value: original_rel (防止在同一目录下出现短名碰撞)
        private readonly ConcurrentDictionary<string, string> _occupiedStoredToOriginal = new(StringComparer.OrdinalIgnoreCase);
        // Key: original_rel, Value: stored_rel (已确定的映射项，确保增量同步稳定性)
        private readonly ConcurrentDictionary<string, string> _originalToStored = new(StringComparer.OrdinalIgnoreCase);

        public LongNameEngine(int maxUtf8Bytes = DefaultMaxUtf8Bytes)
            : this(maxFileUtf8Bytes: maxUtf8Bytes, maxDirUtf8Bytes: maxUtf8Bytes)
        {
        }

        public LongNameEngine(int maxFileUtf8Bytes, int maxDirUtf8Bytes)
        {
            _maxFileUtf8Bytes = maxFileUtf8Bytes > 0 ? maxFileUtf8Bytes : DefaultMaxUtf8Bytes;
            _maxDirUtf8Bytes = maxDirUtf8Bytes > 0 ? maxDirUtf8Bytes : DefaultMaxUtf8Bytes;
        }

        /// <summary>
        /// 根据 NAS 存储提供程序动态创建匹配的长文件名引擎
        /// </summary>
        public static LongNameEngine CreateForProvider(FNSyncPlus.Core.Storage.INasStorageProvider? provider)
        {
            if (provider is FNSyncPlus.Core.Storage.FnOsPersonalStorageProvider)
            {
                // fnOS 个人空间：文件直传上限 251 字节，目录保持 Linux 255 字节
                return new LongNameEngine(maxFileUtf8Bytes: FnOsUploadMaxUtf8Bytes, maxDirUtf8Bytes: DefaultMaxUtf8Bytes);
            }
            // SMB 共享或本地沙盒：文件与目录均保持 255 字节
            return new LongNameEngine(maxFileUtf8Bytes: DefaultMaxUtf8Bytes, maxDirUtf8Bytes: DefaultMaxUtf8Bytes);
        }

        /// <summary>
        /// 根据任务同步提供程序类型创建匹配的长文件名引擎
        /// </summary>
        public static LongNameEngine CreateForProviderType(FNSyncPlus.Core.Storage.StorageProviderType providerType)
        {
            if (providerType == FNSyncPlus.Core.Storage.StorageProviderType.FnOsPersonal)
            {
                return new LongNameEngine(maxFileUtf8Bytes: FnOsUploadMaxUtf8Bytes, maxDirUtf8Bytes: DefaultMaxUtf8Bytes);
            }
            return new LongNameEngine(maxFileUtf8Bytes: DefaultMaxUtf8Bytes, maxDirUtf8Bytes: DefaultMaxUtf8Bytes);
        }

        /// <summary>
        /// 从已有的 Manifest 中恢复映射字典，确保增量同步时已映射的文件短名保持绝对稳定
        /// </summary>
        public void LoadExistingManifest(Manifest manifest)
        {
            if (manifest == null) return;

            foreach (var d in manifest.Directories)
            {
                if (!string.IsNullOrEmpty(d.OriginalRel) && !string.IsNullOrEmpty(d.StoredRel))
                {
                    _originalToStored[d.OriginalRel] = d.StoredRel;
                    _occupiedStoredToOriginal[d.StoredRel] = d.OriginalRel;
                }
            }

            foreach (var f in manifest.Files)
            {
                if (!string.IsNullOrEmpty(f.OriginalRel) && !string.IsNullOrEmpty(f.StoredRel))
                {
                    _originalToStored[f.OriginalRel] = f.StoredRel;
                    _occupiedStoredToOriginal[f.StoredRel] = f.OriginalRel;
                }
            }
        }

        /// <summary>
        /// 判断单个文件名或目录名（单层）是否超长
        /// </summary>
        public bool IsComponentTooLong(string componentName, bool isDirectory = false)
        {
            if (string.IsNullOrEmpty(componentName)) return false;
            int maxLimit = isDirectory ? _maxDirUtf8Bytes : _maxFileUtf8Bytes;
            return PathHelper.GetUtf8ByteCount(componentName) > maxLimit;
        }

        /// <summary>
        /// 转换相对文件路径
        /// 返回 NAS 上保存的相对路径，以及整条路径中是否有超长组件
        /// </summary>
        public (string StoredRel, bool IsLongName) TranslateFilePath(string originalRel)
        {
            originalRel = PathHelper.NormalizeRelPath(originalRel);
            if (string.IsNullOrEmpty(originalRel)) return (string.Empty, false);

            // 如果此前已经映射过，直接返回已有映射，保证幂等性与绝对稳定
            if (_originalToStored.TryGetValue(originalRel, out var existing))
            {
                bool isLong = !string.Equals(originalRel, existing, StringComparison.Ordinal);
                return (existing, isLong);
            }

            string[] parts = originalRel.Split('/');
            List<string> storedParts = new();
            bool hasLongComponent = false;

            // 逐层处理父目录
            string currentOriginalDir = "";
            string currentStoredDir = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string dirComp = parts[i];
                currentOriginalDir = string.IsNullOrEmpty(currentOriginalDir) ? dirComp : currentOriginalDir + "/" + dirComp;

                var (storedDirComp, dirLong) = TranslateSingleComponent(dirComp, currentOriginalDir, currentStoredDir, isDirectory: true);
                if (dirLong) hasLongComponent = true;

                currentStoredDir = string.IsNullOrEmpty(currentStoredDir) ? storedDirComp : currentStoredDir + "/" + storedDirComp;
                storedParts.Add(storedDirComp);
            }

            // 处理文件名本体
            string fileComp = parts[^1];
            var (storedFileComp, fileLong) = TranslateSingleComponent(fileComp, originalRel, currentStoredDir, isDirectory: false);
            if (fileLong) hasLongComponent = true;
            storedParts.Add(storedFileComp);

            string finalStored = string.Join('/', storedParts);
            _originalToStored[originalRel] = finalStored;
            _occupiedStoredToOriginal[finalStored] = originalRel;

            return (finalStored, hasLongComponent);
        }

        /// <summary>
        /// 转换相对目录路径
        /// </summary>
        public (string StoredRel, bool IsLongName) TranslateDirPath(string originalRel)
        {
            originalRel = PathHelper.NormalizeRelPath(originalRel);
            if (string.IsNullOrEmpty(originalRel)) return (string.Empty, false);

            if (_originalToStored.TryGetValue(originalRel, out var existing))
            {
                bool isLong = !string.Equals(originalRel, existing, StringComparison.Ordinal);
                return (existing, isLong);
            }

            string[] parts = originalRel.Split('/');
            List<string> storedParts = new();
            bool hasLongComponent = false;

            string currentOriginalDir = "";
            string currentStoredDir = "";
            for (int i = 0; i < parts.Length; i++)
            {
                string dirComp = parts[i];
                currentOriginalDir = string.IsNullOrEmpty(currentOriginalDir) ? dirComp : currentOriginalDir + "/" + dirComp;

                var (storedDirComp, dirLong) = TranslateSingleComponent(dirComp, currentOriginalDir, currentStoredDir, isDirectory: true);
                if (dirLong) hasLongComponent = true;

                currentStoredDir = string.IsNullOrEmpty(currentStoredDir) ? storedDirComp : currentStoredDir + "/" + storedDirComp;
                storedParts.Add(storedDirComp);
            }

            string finalStored = string.Join('/', storedParts);
            _originalToStored[originalRel] = finalStored;
            _occupiedStoredToOriginal[finalStored] = originalRel;

            return (finalStored, hasLongComponent);
        }

        /// <summary>
        /// 转换单个组件（文件名或单个目录名）
        /// </summary>
        private (string StoredName, bool IsLong) TranslateSingleComponent(string componentName, string currentOriginalFullPath, string parentStoredDir, bool isDirectory)
        {
            int byteCount = PathHelper.GetUtf8ByteCount(componentName);
            int maxLimit = isDirectory ? _maxDirUtf8Bytes : _maxFileUtf8Bytes;

            // 如果不超长，且不是内部元数据与清单保留名称，优先尝试使用原始名称
            if (byteCount <= maxLimit &&
                !string.Equals(componentName, Manifest.MetadataDirName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(componentName, Manifest.CurrentManifestFileName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(componentName, Manifest.LegacyManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                string candidateStoredPath = string.IsNullOrEmpty(parentStoredDir) ? componentName : parentStoredDir + "/" + componentName;
                if (!_occupiedStoredToOriginal.TryGetValue(candidateStoredPath, out var occupiedBy) ||
                    string.Equals(occupiedBy, currentOriginalFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return (componentName, false);
                }
            }

            // 超长或遇到同级冲突，生成稳定唯一的短名称
            string ext = isDirectory ? "" : Path.GetExtension(componentName);
            // 扩展名若过长（> 25 字节）则不保留，防止扩展名自身导致溢出
            if (PathHelper.GetUtf8ByteCount(ext) > 25)
            {
                ext = "";
            }

            // 使用当前完整相对路径计算 SHA-256，确保唯一且稳定
            string fullHash = HashHelper.ComputeSha256String(currentOriginalFullPath);

            // 尝试 12 位、16 位、24 位短哈希，若发生同级冲突则尝试加后缀
            int[] lengths = { 12, 16, 24, 32 };
            foreach (int len in lengths)
            {
                string hashShort = fullHash.Substring(0, Math.Min(len, fullHash.Length));
                string candidate = $"{LongNamePrefix}{hashShort}{ext}";
                string candidateStoredPath = string.IsNullOrEmpty(parentStoredDir) ? candidate : parentStoredDir + "/" + candidate;

                if (!_occupiedStoredToOriginal.TryGetValue(candidateStoredPath, out var occupiedBy) ||
                    string.Equals(occupiedBy, currentOriginalFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return (candidate, true);
                }
            }

            // 极端碰撞保底：加数字后缀
            for (int counter = 2; counter <= 9999; counter++)
            {
                string hashShort = fullHash.Substring(0, 12);
                string candidate = $"{LongNamePrefix}{hashShort}_{counter}{ext}";
                string candidateStoredPath = string.IsNullOrEmpty(parentStoredDir) ? candidate : parentStoredDir + "/" + candidate;

                if (!_occupiedStoredToOriginal.TryGetValue(candidateStoredPath, out var occupiedBy) ||
                    string.Equals(occupiedBy, currentOriginalFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return (candidate, true);
                }
            }

            throw new InvalidOperationException($"无法为长文件分配唯一的短映射名称: {currentOriginalFullPath}");
        }

        public void Clear()
        {
            _occupiedStoredToOriginal.Clear();
            _originalToStored.Clear();
        }
    }
}
