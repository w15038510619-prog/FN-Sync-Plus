using System;
using System.Text.Json.Serialization;
using FNSyncPlus.Core.Storage;

namespace FNSyncPlus.Core.Models
{
    public enum SyncMode
    {
        OneWayBackup,    // 单向增量备份 (Windows -> NAS, 本地删除不删远端)
        MirrorBackup     // 镜像备份
    }

    public enum TaskSyncState
    {
        Idle,               // 空闲待命
        Queued,             // 排队等待同步
        Scanning,           // 正在扫描本地与远端
        Syncing,            // 正在上传同步
        Paused,             // 已暂停
        OfflineWaiting,     // NAS 离线等待
        Completed,          // 同步完成
        Error,              // 同步异常
        ConfigurationError, // 配置异常 (如源与目标冲突、路径非法等)
        Removing            // 正在安全移除
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

        [JsonPropertyName("provider_type")]
        public StorageProviderType ProviderType { get; set; } = StorageProviderType.FnOsPersonal;

        [JsonPropertyName("nas_username")]
        public string NasUsername { get; set; } = string.Empty;

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

        [JsonIgnore]
        public bool IsPaused => State == TaskSyncState.Paused;

        /// <summary>
        /// 状态描述友好文字
        /// </summary>
        [JsonIgnore]
        public string DisplayStatus => State switch
        {
            TaskSyncState.Idle => string.IsNullOrEmpty(LastSyncStatus) ? "待命中" : LastSyncStatus,
            TaskSyncState.Queued => "排队等待调度...",
            TaskSyncState.Scanning => "正在比对扫描...",
            TaskSyncState.Syncing => $"正在同步备份 ({CurrentProgress:F0}%)...",
            TaskSyncState.Paused => "已暂停",
            TaskSyncState.OfflineWaiting => "NAS离线等待恢复",
            TaskSyncState.Completed => "已就绪 (已最新)",
            TaskSyncState.Error => string.IsNullOrEmpty(LastError) ? "同步出错" : $"出错: {LastError}",
            TaskSyncState.ConfigurationError => string.IsNullOrEmpty(LastError) ? "配置异常" : $"配置异常: {LastError}",
            TaskSyncState.Removing => "正在安全移除...",
            _ => State.ToString()
        };

        [JsonIgnore]
        public string StateBadgeColorHex => State switch
        {
            TaskSyncState.Completed => "#15803D", // Green
            TaskSyncState.Syncing => "#2563EB",   // Blue
            TaskSyncState.Scanning => "#0284C7",  // Cyan
            TaskSyncState.Queued => "#6366F1",    // Indigo
            TaskSyncState.Paused => "#D97706",    // Amber
            TaskSyncState.Error => "#DC2626",     // Red
            TaskSyncState.ConfigurationError => "#B91C1C", // Dark red
            TaskSyncState.Removing => "#64748B",  // Slate
            TaskSyncState.OfflineWaiting => "#9333EA", // Purple
            _ => "#475569"                         // Slate
        };

        [JsonIgnore]
        public string StateBadgeBgHex => State switch
        {
            TaskSyncState.Completed => "#DCFCE7",
            TaskSyncState.Syncing => "#DBEAFE",
            TaskSyncState.Scanning => "#E0F2FE",
            TaskSyncState.Queued => "#EEF2FF",
            TaskSyncState.Paused => "#FEF3C7",
            TaskSyncState.Error => "#FEE2E2",
            TaskSyncState.ConfigurationError => "#FEE2E2",
            TaskSyncState.Removing => "#F1F5F9",
            TaskSyncState.OfflineWaiting => "#F3E8FF",
            _ => "#F1F5F9"
        };

        /// <summary>
        /// 验证同步任务配置的合法性与边界隔离，杜绝自同步死循环、盘符越界或无效路径伪造成功
        /// </summary>
        public bool ValidateConfiguration(out string errorMessage)
        {
            if (string.IsNullOrWhiteSpace(SourceFolder))
            {
                errorMessage = "本地源文件夹路径不能为空";
                return false;
            }

            if (string.IsNullOrWhiteSpace(DestinationFolder))
            {
                errorMessage = "NAS 目标备份路径不能为空";
                return false;
            }

            // 检查本地源文件夹是否存在
            try
            {
                if (!System.IO.Directory.Exists(SourceFolder))
                {
                    errorMessage = $"本地源文件夹不存在或不可访问: {SourceFolder}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                errorMessage = $"校验本地源文件夹异常: {ex.Message}";
                return false;
            }

            // 规范化路径比对
            string normSource = SourceFolder.TrimEnd('\\', '/').Replace('/', '\\');
            string normDest = DestinationFolder.TrimEnd('\\', '/');

            // 1. 严格禁止源目录与目标目录相同（自同步死循环防护）
            if (string.Equals(normSource, normDest.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
            {
                errorMessage = "严重配置错误：源文件夹与目标备份路径完全相同，严禁自同步！";
                return false;
            }

            // 2. 根据存储类型校验目标路径规范
            if (ProviderType == StorageProviderType.FnOsPersonal)
            {
                // 飞牛个人空间目标路径必须是相对路径或以「我的文件」开头，绝对不能是 Windows 盘符路径 (如 D:\...) 或 Windows UNC 路径 (\\...)
                if ((normDest.Length >= 2 && normDest[1] == ':') || normDest.StartsWith(@"\\") || normDest.StartsWith("//"))
                {
                    errorMessage = $"配置错误：飞牛个人空间任务的目标路径不能是 Windows 本地或 UNC 共享路径 ({normDest})，必须指向官方「我的文件」";
                    return false;
                }
            }
            else if (ProviderType == StorageProviderType.SmbShare)
            {
                // SMB 共享目标路径必须是规范的 UNC 路径
                if (!normDest.StartsWith(@"\\") && !normDest.StartsWith("//"))
                {
                    errorMessage = $"配置错误：SMB 共享任务的目标路径必须为有效的 UNC 路径 ({normDest})";
                    return false;
                }

                // 嵌套关系拦截
                if (normDest.StartsWith(normSource + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = "安全拦截：目标备份目录不能位于源文件夹内部，否则会导致无限嵌套！";
                    return false;
                }
            }
            else if (ProviderType == StorageProviderType.LocalFileSystem)
            {
                // 本地/沙盒测试存储：拦截源与目标相互嵌套
                if (normDest.StartsWith(normSource + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = "安全拦截：目标备份目录不能位于源文件夹内部，否则会导致无限嵌套！";
                    return false;
                }
                if (normSource.StartsWith(normDest + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    errorMessage = "安全拦截：源文件夹不能位于备份目录内部，否则会导致无限嵌套！";
                    return false;
                }
            }

            errorMessage = string.Empty;
            return true;
        }
    }
}
