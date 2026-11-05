using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Models
{
    /// <summary>
    /// 备份清单元数据，保存在 NAS 目标备份根目录下
    /// 包含所有长文件名与常规文件映射、目录映射、时间戳与 SHA-256 校验和
    /// </summary>
    public class Manifest
    {
        public const string CurrentManifestFileName = "__FN_SYNC_MANIFEST__.json";
        public const string LegacyManifestFileName = "__FN_ORIGINAL_NAMES__.json";
        public const int CurrentVersion = 2;

        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("tool")]
        public string Tool { get; set; } = "FN Sync Plus (飞牛同步增强版)";

        [JsonPropertyName("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

        [JsonPropertyName("note")]
        public string Note { get; set; } = "飞牛同步元数据清单文件，请随备份保留，切勿删除，用于异机一键恢复原始文件名与目录结构。";

        [JsonPropertyName("source_root")]
        public string SourceRoot { get; set; } = string.Empty;

        [JsonPropertyName("target_root")]
        public string TargetRoot { get; set; } = string.Empty;

        [JsonPropertyName("files")]
        public List<ManifestFileEntry> Files { get; set; } = new();

        [JsonPropertyName("directories")]
        public List<ManifestDirEntry> Directories { get; set; } = new();
    }

    /// <summary>
    /// 文件映射项
    /// </summary>
    public class ManifestFileEntry
    {
        [JsonPropertyName("original_rel")]
        public string OriginalRel { get; set; } = string.Empty;

        [JsonPropertyName("stored_rel")]
        public string StoredRel { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("mtime_utc_ticks")]
        public long ModTimeUtcTicks { get; set; }

        [JsonPropertyName("mtime_iso")]
        public DateTimeOffset ModTimeIso { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        [JsonPropertyName("is_long_name")]
        public bool IsLongName { get; set; }
    }

    /// <summary>
    /// 目录映射项
    /// </summary>
    public class ManifestDirEntry
    {
        [JsonPropertyName("original_rel")]
        public string OriginalRel { get; set; } = string.Empty;

        [JsonPropertyName("stored_rel")]
        public string StoredRel { get; set; } = string.Empty;

        [JsonPropertyName("is_long_name")]
        public bool IsLongName { get; set; }
    }
}
