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

    /// <summary>
    /// 任务级自动化调度模式
    /// </summary>
    public enum TaskSyncScheduleMode
    {
        SmartAuto = 0,    // 模式 A: 智能自动备份 (默认：实时监听 + 稳定沉降 + 定时补偿检查 + 离线待命)
        RealtimeOnly = 1, // 模式 B: 仅实时监听 (实时监听文件变动并自动备份，不启用周期轮询)
        PeriodicOnly = 2, // 模式 C: 仅定时同步 (按固定周期检查同步，如 5/15/30/60 分钟)
        ManualOnly = 3    // 模式 D: 仅手动同步 (仅在用户手动点击时执行同步)
    }

    /// <summary>
    /// 备份策略（重命名、删除与历史版本行为）
    /// </summary>
    public enum BackupStrategy
    {
        Smart = 0,   // 智能备份（推荐）：当前有效备份树跟随重命名，旧文件名与历史版本保存于备份清单，本地删除默认软归档
        Archive = 1, // 归档备份：本地文件重命名或删除后，NAS 旧文件保留在原位置，新旧文件名并存
        Mirror = 2   // 镜像备份：NAS 当前有效文件树跟随本地新增、修改、重命名，删除需受控归档回收
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

        // Explicit user pause is persistent; transient runtime states are not.
        [JsonPropertyName("is_paused")]
        public bool IsUserPaused { get; set; }

        [JsonPropertyName("auto_sync_enabled")]
        public bool AutoSyncEnabled { get; set; } = true;

        [JsonPropertyName("realtime_watch_enabled")]
        public bool RealtimeWatchEnabled { get; set; } = true;

        [JsonPropertyName("interval_minutes")]
        public int IntervalMinutes { get; set; } = 5;

        [JsonPropertyName("schedule_mode")]
        public TaskSyncScheduleMode ScheduleMode { get; set; } = TaskSyncScheduleMode.SmartAuto;

        [JsonPropertyName("backup_strategy")]
        public BackupStrategy Strategy { get; set; } = BackupStrategy.Smart;

        [JsonPropertyName("mirror_deletion_enabled")]
        public bool MirrorDeletionEnabled { get; set; }

        [JsonPropertyName("settling_delay_seconds")]
        public int SettlingDelaySeconds { get; set; } = 3;

        [JsonPropertyName("last_success_time")]
        public DateTime? LastSuccessTime { get; set; }

        [JsonPropertyName("last_failure_time")]
        public DateTime? LastFailureTime { get; set; }

        [JsonIgnore]
        public DateTime? NextScheduledTime { get; set; }

        [JsonIgnore]
        public string? LastDetectedChange { get; set; }

        [JsonIgnore]
        public int QueuedChangesCount { get; set; }

        [JsonIgnore]
        public bool IsWatching { get; set; }

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

        [JsonIgnore]
        public bool IsExperimentalBackup { get; internal set; }

        // Persisted independently of runtime flags so a versioned task cannot fall back to the legacy engine.
        [JsonPropertyName("backup_storage_format")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonInclude]
        public string? BackupStorageFormat { get; internal set; }

        /// <summary>Only for an explicitly selected source in an isolated experiment.</summary>
        [JsonPropertyName("experimental_existing_root")]
        public string? ExperimentalExistingRoot { get; set; }

        [JsonPropertyName("experimental_pending_initialization")]
        [JsonInclude]
        public bool ExperimentalPendingInitialization { get; internal set; }

        [JsonPropertyName("experimental_first_publication_pending")]
        [JsonInclude]
        public bool ExperimentalFirstPublicationPending { get; internal set; }

        [JsonPropertyName("experimental_initialization_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        [JsonInclude]
        public string? ExperimentalInitializationId { get; internal set; }

        [JsonIgnore]
        public bool RequiresNewExperimentalBackup { get; internal set; }

        /// <summary>
        /// 自动化状态友好摘要（展示调度模式、监听状态、下次检查时间与排队情况）
        /// 例如：“实时监听中 · 每 5 分钟检查 · 下次 14:00 · 待上传 1 个文件”
        /// </summary>
        [JsonIgnore]
        public string ScheduleSummary
        {
            get
            {
                if (!Enabled) return "已禁用";
                if (State == TaskSyncState.Paused) return "已暂停自动化监控";
                if (State == TaskSyncState.OfflineWaiting) return "NAS 离线 · 等待连接恢复";

                string modeDesc = ScheduleMode switch
                {
                    TaskSyncScheduleMode.SmartAuto => $"智能自动 (每 {IntervalMinutes} 分钟补偿)",
                    TaskSyncScheduleMode.RealtimeOnly => "仅实时监听中",
                    TaskSyncScheduleMode.PeriodicOnly => $"定时同步 (每 {IntervalMinutes} 分钟)",
                    TaskSyncScheduleMode.ManualOnly => "仅手动同步",
                    _ => "自动同步"
                };

                var parts = new System.Collections.Generic.List<string>();

                if (IsWatching && (ScheduleMode == TaskSyncScheduleMode.SmartAuto || ScheduleMode == TaskSyncScheduleMode.RealtimeOnly))
                {
                    parts.Add("实时监听中");
                }
                parts.Add(modeDesc);

                if (NextScheduledTime.HasValue && ScheduleMode != TaskSyncScheduleMode.ManualOnly && ScheduleMode != TaskSyncScheduleMode.RealtimeOnly)
                {
                    parts.Add($"下次 {NextScheduledTime.Value:HH:mm}");
                }

                if (QueuedChangesCount > 0)
                {
                    parts.Add($"待同步 {QueuedChangesCount} 项");
                }
                else if (!string.IsNullOrEmpty(LastDetectedChange))
                {
                    parts.Add($"变动: {LastDetectedChange}");
                }

                if (LastSuccessTime.HasValue)
                {
                    parts.Add($"上次成功 {LastSuccessTime.Value:HH:mm}");
                }

                return string.Join(" · ", parts);
            }
        }

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
            TaskSyncState.Completed => IsExperimentalBackup ? LastSyncStatus : "已就绪 (已最新)",
            TaskSyncState.Error => !string.IsNullOrEmpty(LastSyncStatus) && LastSyncStatus.Contains("失败")
                ? LastSyncStatus
                : (string.IsNullOrEmpty(LastError) ? (string.IsNullOrEmpty(LastSyncStatus) ? "同步出错" : LastSyncStatus) : $"出错: {LastError}"),
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
