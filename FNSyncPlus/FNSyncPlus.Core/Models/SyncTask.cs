using System;
using System.Text.Json.Serialization;

namespace FNSyncPlus.Core.Models
{
    public enum SyncMode
    {
        OneWayBackup,    // 单向增量备份 (Windows -> NAS, 本地删除不删远端)
        MirrorBackup     // 镜像备份
    }

    public enum TaskSyncState
    {
        Idle,            // 空闲待命
        Queued,          // 排队等待同步
        Scanning,        // 正在扫描本地与远端
        Syncing,         // 正在上传同步
        Paused,          // 已暂停
        OfflineWaiting,  // NAS 离线等待
        Completed,       // 同步完成
        Error            // 同步异常
    }

    /// <summary>
    /// 同步任务模型：每个被保护的本地文件夹为一个独立任务，享有独立的配置、进度、日志与 NAS 存储空间
    /// </summary>
    public class SyncTask
    {
        [JsonPropertyName("task_id")]
        public string TaskId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("task_name")]
        public string TaskName { get; set; } = string.Empty;

        [JsonPropertyName("source_folder")]
        public string SourceFolder { get; set; } = string.Empty;

        [JsonPropertyName("nas_device_id")]
        public string NasDeviceId { get; set; } = string.Empty;

        [JsonPropertyName("destination_root")]
        public string DestinationRoot { get; set; } = string.Empty;

        [JsonPropertyName("destination_folder")]
        public string DestinationFolder { get; set; } = string.Empty;

        [JsonPropertyName("sync_mode")]
        public SyncMode Mode { get; set; } = SyncMode.OneWayBackup;

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        [JsonPropertyName("auto_sync_enabled")]
        public bool AutoSyncEnabled { get; set; } = true;

        [JsonPropertyName("realtime_watch_enabled")]
        public bool RealtimeWatchEnabled { get; set; } = true;

        [JsonPropertyName("interval_minutes")]
        public int IntervalMinutes { get; set; } = 5;

        [JsonPropertyName("last_sync_time")]
        public DateTime? LastSyncTime { get; set; }

        [JsonPropertyName("last_sync_status")]
        public string LastSyncStatus { get; set; } = "就绪";

        [JsonPropertyName("last_error")]
        public string? LastError { get; set; }

        [JsonPropertyName("task_manifest_id")]
        public string TaskManifestId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; } = DateTime.Now;

        // --- 统计与运行态字段（可持久化上次结果） ---

        [JsonPropertyName("last_scanned_files")]
        public int LastScannedFiles { get; set; }

        [JsonPropertyName("last_uploaded_files")]
        public int LastUploadedFiles { get; set; }

        [JsonPropertyName("last_unchanged_files")]
        public int LastUnchangedFiles { get; set; }

        [JsonPropertyName("last_long_name_files")]
        public int LastLongNameFiles { get; set; }

        [JsonPropertyName("last_failed_files")]
        public int LastFailedFiles { get; set; }

        [JsonPropertyName("last_transferred_bytes")]
        public long LastTransferredBytes { get; set; }

        // --- 纯内存运行态属性 ---

        [JsonIgnore]
        public TaskSyncState State { get; set; } = TaskSyncState.Idle;

        [JsonIgnore]
        public double CurrentProgress { get; set; }

        [JsonIgnore]
        public string CurrentFile { get; set; } = string.Empty;

        [JsonIgnore]
        public double CurrentSpeedBytesPerSec { get; set; }

        [JsonIgnore]
        public bool IsRunning => State == TaskSyncState.Scanning || State == TaskSyncState.Syncing;

        /// <summary>
        /// 状态描述友好文字
        /// </summary>
        [JsonIgnore]
        public string DisplayStatus => State switch
        {
            TaskSyncState.Idle => string.IsNullOrEmpty(LastSyncStatus) ? "空闲就绪" : LastSyncStatus,
            TaskSyncState.Queued => "排队等待中...",
            TaskSyncState.Scanning => "正在扫描本地变动...",
            TaskSyncState.Syncing => $"正在同步备份 ({CurrentProgress:F0}%)...",
            TaskSyncState.Paused => "已暂停",
            TaskSyncState.OfflineWaiting => "NAS离线等待恢复",
            TaskSyncState.Completed => "同步完成",
            TaskSyncState.Error => string.IsNullOrEmpty(LastError) ? "同步出错" : $"出错: {LastError}",
            _ => State.ToString()
        };
    }
}
