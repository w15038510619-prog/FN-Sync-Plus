using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

/// <summary>Creates a read-only long-name preview without confusing versioned objects with current-tree aliases.</summary>
public static class ManifestMappingPreview
{
    public sealed record Item(string OriginalRel, string StorageDescription, int NameUtf8Bytes, string Sha256, bool IsDirectory);

    public static IReadOnlyList<Item> Create(Manifest manifest, bool versioned)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Files == null || manifest.Directories == null) throw new InvalidDataException("清单缺少文件或目录列表");
        var result = new List<Item>();
        int NameBytes(string path) => PathHelper.GetUtf8ByteCount(Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)));
        foreach (var dir in manifest.Directories)
        {
            if (dir == null || !PathHelper.IsSafeRelativePath(dir.OriginalRel)) throw new InvalidDataException("清单目录路径无效");
            int bytes = NameBytes(dir.OriginalRel);
            if (versioned ? bytes > LongNameEngine.DefaultMaxUtf8Bytes : dir.IsLongName || dir.OriginalRel != dir.StoredRel)
                result.Add(new Item(dir.OriginalRel, versioned ? "目录名称保存在清单中" : dir.StoredRel, bytes, "-- (目录)", true));
        }
        foreach (var file in manifest.Files)
        {
            if (file == null || !PathHelper.IsSafeRelativePath(file.OriginalRel)) throw new InvalidDataException("清单文件路径无效");
            if (!file.IsActive) continue;
            int bytes = NameBytes(file.OriginalRel);
            if (versioned ? file.IsLongName || bytes > LongNameEngine.FnOsUploadMaxUtf8Bytes : file.IsLongName || file.OriginalRel != file.StoredRel)
            {
                if (!PathHelper.IsSafeRelativePath(file.StoredRel) ||
                    (versioned && (!file.StoredRel.StartsWith(".fnsync/objects/obj-", StringComparison.Ordinal) ||
                        file.Sha256?.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))))
                    throw new InvalidDataException("清单存储路径或校验值无效");
                result.Add(new Item(file.OriginalRel, file.StoredRel, bytes, file.Sha256, false));
            }
        }
        return result;
    }
}
