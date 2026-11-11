using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;
using FNSyncPlus.WinUI.Services;

namespace FNSyncPlus.WinUI.ViewModels
{
    public class LongNameMappingDisplayItem
    {
        public string OriginalRel { get; set; } = string.Empty;
        public string StoredRel { get; set; } = string.Empty;
        public string StorageText => $"备份位置：{StoredRel}";
        public int Utf8Bytes { get; set; }
        public string Utf8BytesFormatted => $"{Utf8Bytes} B";
        public string Sha256 { get; set; } = string.Empty;
        public string HashText => $"SHA-256：{Sha256}";
        public string EntryType { get; set; } = "文件"; // 文件 或 目录
        public bool IsOverLimit => Utf8Bytes > 255;
    }

    public class TaskLogDisplayItem
    {
        public string TimeFormatted { get; set; } = string.Empty;
        public string Level { get; set; } = "INFO";
        public string Stage { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string FullFormattedText { get; set; } = string.Empty;

        public string LevelColorHex => Level switch
        {
            "ERROR" => "#DC2626",
            "WARN" => "#D97706",
            "DEBUG" => "#64748B",
            _ => "#15803D"
        };

        public string LevelBgHex => Level switch
        {
            "ERROR" => "#FEE2E2",
            "WARN" => "#FEF3C7",
            "DEBUG" => "#F1F5F9",
            _ => "#DCFCE7"
        };
    }

    public partial class TaskDetailsViewModel : ObservableObject
    {
        private readonly TaskManager _taskManager;
        private readonly ManifestManager _manifestManager;
        private readonly NasConnectionService _connectionService;
        private readonly LogService _logService;

        public SyncTask? TaskModel { get; private set; }

        [ObservableProperty]
        private string _taskName = string.Empty;

        [ObservableProperty]
        private string _sourceFolder = string.Empty;

        [ObservableProperty]
        private string _destinationFolder = string.Empty;

        [ObservableProperty]
        private string _storageTypeDisplay = "飞牛官方个人空间";

        [ObservableProperty]
        private int _scannedFiles;

        [ObservableProperty]
        private int _uploadedFiles;

        [ObservableProperty]
        private int _unchangedFiles;

        [ObservableProperty]
        private int _longNameFiles;

        [ObservableProperty]
        private int _failedFiles;

        [ObservableProperty]
        private string _transferredBytesFormatted = "0 B";

        [ObservableProperty]
        private string _transferredSummaryText = "上次统计传输: 0 B";

        [ObservableProperty]
        private string _scannedFilesText = "扫描: 0";

        [ObservableProperty]
        private string _uploadedFilesText = "上传: 0";

        [ObservableProperty]
        private string _unchangedFilesText = "跳过未改: 0";

        [ObservableProperty]
        private string _longNameFilesText = "超长项: 0";

        [ObservableProperty]
        private string _failedFilesText = "失败: 0";

        [ObservableProperty]
        private string _manifestStatusNotice = string.Empty;

        [ObservableProperty]
        private bool _hasLongNameMappings;

        [ObservableProperty]
        private bool _hasManifestResult;

        [ObservableProperty]
        private bool _hasLogs;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _logSearchQuery = string.Empty;

        [ObservableProperty]
        private int _selectedLogLevelIndex = 0; // 0=全部, 1=仅错误, 2=警告与错误

        [ObservableProperty]
        private string _statusFeedbackMessage = string.Empty;

        public ObservableCollection<LongNameMappingDisplayItem> LongNameMappings { get; } = new();
        public ObservableCollection<TaskLogDisplayItem> FilteredLogs { get; } = new();
        private readonly List<TaskLogDisplayItem> _allLoadedLogs = new();
        private readonly List<TaskLogDisplayItem> _recentLoadedLogs = new();
        private CancellationTokenSource? _logSearchCancellation;
        private bool _showingArchiveSearch;

        public TaskDetailsViewModel(
            TaskManager taskManager, 
            ManifestManager manifestManager,
            NasConnectionService connectionService,
            LogService logService)
        {
            _taskManager = taskManager;
            _manifestManager = manifestManager;
            _connectionService = connectionService;
            _logService = logService;
        }

        public async Task LoadTaskAsync(SyncTask task)
        {
            if (task == null) return;
            IsLoading = true;
            TaskModel = task;
            TaskName = task.TaskName;
            SourceFolder = task.SourceFolder;
            DestinationFolder = task.DestinationFolder;
            StorageTypeDisplay = task.ProviderType == StorageProviderType.FnOsPersonal 
                ? "飞牛官方个人空间 (「我的文件」)" 
                : (task.ProviderType == StorageProviderType.SmbShare ? "Windows SMB 网络共享" : "本地沙盒测试");

            ScannedFiles = task.LastScannedFiles;
            UploadedFiles = task.LastUploadedFiles;
            UnchangedFiles = task.LastUnchangedFiles;
            LongNameFiles = task.LastLongNameFiles;
            FailedFiles = task.LastFailedFiles;
            TransferredBytesFormatted = task.IsExperimentalBackup
                ? (task.LastVersionedUploadedBytes.HasValue ? FormatBytes(task.LastVersionedUploadedBytes.Value) : "未记录")
                : FormatBytes(task.LastTransferredBytes);

            TransferredSummaryText = task.IsExperimentalBackup
                ? $"上次成功上传: {TransferredBytesFormatted}"
                : $"上次统计传输: {TransferredBytesFormatted}";
            ScannedFilesText = $"扫描: {ScannedFiles}";
            UploadedFilesText = $"上传: {UploadedFiles}";
            UnchangedFilesText = $"跳过未改: {UnchangedFiles}";
            LongNameFilesText = $"超长映射: {LongNameFiles}";
            FailedFilesText = $"失败: {FailedFiles}";

            // 1. 加载日志 (支持磁盘持久化与实时流)
            await LoadLogsAsync(task.TaskId);

            // 2. 加载清单元数据 (支持 fnOS 原生存储、SMB 与本地离线缓存)
            await LoadManifestDataAsync(task);

            IsLoading = false;
        }

        private async Task LoadLogsAsync(string taskId)
        {
            await Task.Yield();
            _logSearchCancellation?.Cancel();
            _showingArchiveSearch = false;
            _allLoadedLogs.Clear();

            var logs = _taskManager.GetTaskLogs(taskId);
            foreach (var logStr in logs)
            {
                string level = "INFO";
                if (logStr.Contains("[ERROR]") || logStr.Contains(" | ERROR")) level = "ERROR";
                else if (logStr.Contains("[WARN]") || logStr.Contains(" | WARN ")) level = "WARN";
                else if (logStr.Contains("[DEBUG]") || logStr.Contains(" | DEBUG")) level = "DEBUG";

                _allLoadedLogs.Add(new TaskLogDisplayItem
                {
                    FullFormattedText = logStr,
                    Level = level,
                    Message = logStr
                });
            }

            _recentLoadedLogs.Clear();
            _recentLoadedLogs.AddRange(_allLoadedLogs);

            bool hasErrors = _allLoadedLogs.Any(l => l.Level == "ERROR");
            SelectedLogLevelIndex = hasErrors ? 1 : 0; // 默认视图显示最新错误（若有错误）

            ApplyLogFilter();
            HasLogs = _allLoadedLogs.Count > 0;
        }

        private async Task LoadManifestDataAsync(SyncTask task)
        {
            LongNameMappings.Clear();
            HasLongNameMappings = false;
            HasManifestResult = false;
            ManifestStatusNotice = "正在读取备份清单...";

            try
            {
                Manifest? manifest = null;
                bool invalidVersionRecords = false;

                // A. 飞牛官方个人空间模式
                if (task.ProviderType == StorageProviderType.FnOsPersonal)
                {
                    if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider && fnProvider.IsAuthenticated)
                    {
                        if (task.IsExperimentalBackup)
                        {
                            if ((!string.IsNullOrWhiteSpace(task.NasUsername) && !string.Equals(task.NasUsername, fnProvider.CurrentUsername, StringComparison.Ordinal)) ||
                                (task.NasAccountUid.HasValue && (!fnProvider.CurrentUid.HasValue || fnProvider.CurrentUid.Value != task.NasAccountUid.Value)) ||
                                (!string.IsNullOrWhiteSpace(task.NasDeviceId) && !fnProvider.MatchesServerAddress(task.NasDeviceId)) ||
                                (!AppServices.IsIsolatedTestSession && (string.IsNullOrWhiteSpace(task.NasUsername) || string.IsNullOrWhiteSpace(task.NasDeviceId))))
                                throw new InvalidOperationException("飞牛账号或设备与任务不匹配，无法确认备份位置。");
                            var inspected = await new VersionedManifestStore().InspectForRestoreAsync(fnProvider, task.DestinationFolder);
                            manifest = inspected.Snapshot?.Manifest;
                            invalidVersionRecords = inspected.HasInvalidRecords;
                        }
                        else manifest = await _manifestManager.LoadManifestAsync(fnProvider, task.DestinationFolder);
                    }
                    else
                    {
                        if (task.IsExperimentalBackup)
                        {
                            ManifestStatusNotice = "飞牛未连接，暂时无法读取新版备份清单。";
                            return;
                        }
                        // 离线读取本地缓存清单
                        string cacheFile = ManifestManager.GetLocalCachePath(task.DestinationFolder);
                        if (File.Exists(cacheFile))
                        {
                            string json = await File.ReadAllTextAsync(cacheFile, Encoding.UTF8);
                            manifest = JsonSerializer.Deserialize<Manifest>(json);
                            if (manifest != null)
                            {
                                manifest.IsFromCache = true;
                                manifest.CachedAt = File.GetLastWriteTime(cacheFile);
                            }
                        }
                    }
                }
                // B. SMB 共享模式
                else if (task.ProviderType == StorageProviderType.SmbShare)
                {
                    if (Directory.Exists(task.DestinationFolder))
                    {
                        manifest = await _manifestManager.LoadManifestAsync(task.DestinationFolder);
                    }
                }
                // C. 本地测试模式
                else
                {
                    if (Directory.Exists(task.DestinationFolder))
                    {
                        manifest = await _manifestManager.LoadManifestAsync(task.DestinationFolder);
                    }
                }

                if (manifest != null && !manifest.IsNew)
                {
                    var mappings = ManifestMappingPreview.Create(manifest, task.IsExperimentalBackup);
                    // 状态提示
                    if (task.IsExperimentalBackup)
                    {
                        ManifestStatusNotice = invalidVersionRecords
                            ? "发现异常提交，显示最后有效清单；文件位置为备份对象路径，未单独核验对象。"
                            : "已读取新版备份清单；文件位置为备份对象路径，未单独核验对象。";
                    }
                    else if (manifest.IsFromCache)
                    {
                        ManifestStatusNotice = $"离线模式：已展示最近一次本地缓存清单 (缓存时间: {manifest.CachedAt:yyyy-MM-dd HH:mm})";
                    }
                    else if (manifest.Files.Count > 0 || manifest.Directories.Count > 0)
                    {
                        ManifestStatusNotice = $"✔ 成功加载最新清单 (更新于: {manifest.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm})";
                    }
                    else
                    {
                        ManifestStatusNotice = "ℹ 远端尚未建立文件映射或当前任务所有文件均符合 NAS 原名规范";
                    }

                    foreach (var mapping in mappings)
                    {
                        LongNameMappings.Add(new LongNameMappingDisplayItem
                        {
                            OriginalRel = mapping.OriginalRel,
                            StoredRel = mapping.StorageDescription,
                            Utf8Bytes = mapping.NameUtf8Bytes,
                            Sha256 = mapping.Sha256,
                            EntryType = mapping.IsDirectory ? "目录" : "文件"
                        });
                    }
                    HasManifestResult = true;
                }
                else
                {
                    ManifestStatusNotice = "▲ 未能读取到元数据清单。若为新建立任务，将在首次同步完成后生成。";
                }
            }
            catch (Exception ex)
            {
                LongNameMappings.Clear();
                HasManifestResult = false;
                ManifestStatusNotice = $"读取清单异常: {ex.Message}";
            }

            HasLongNameMappings = LongNameMappings.Count > 0;
        }

        partial void OnLogSearchQueryChanged(string value)
        {
            if (_logSearchCancellation != null)
            {
                _logSearchCancellation.Cancel();
                StatusFeedbackMessage = "已取消上一次历史搜索。";
            }
            if (_showingArchiveSearch)
            {
                _allLoadedLogs.Clear();
                _allLoadedLogs.AddRange(_recentLoadedLogs);
                _showingArchiveSearch = false;
                HasLogs = _allLoadedLogs.Count > 0;
                StatusFeedbackMessage = "已返回最近日志；点击“搜索全部”检索历史。";
            }
            ApplyLogFilter();
        }

        public async Task SearchAllLogsAsync()
        {
            if (TaskModel == null) return;
            string query = LogSearchQuery.Trim();
            if (query.Length == 0) { StatusFeedbackMessage = "请输入关键字或批次号。"; return; }
            _logSearchCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            _logSearchCancellation = cancellation;
            string taskId = TaskModel.TaskId;
            StatusFeedbackMessage = "正在搜索该任务的已保存日志…";
            try
            {
                var logs = await Task.Run(() => _logService.SearchTaskLogs(taskId, query, cancellationToken: cancellation.Token), cancellation.Token);
                if (cancellation.IsCancellationRequested || !ReferenceEquals(_logSearchCancellation, cancellation)) return;
                _allLoadedLogs.Clear();
                foreach (var log in logs)
                    _allLoadedLogs.Add(new TaskLogDisplayItem { FullFormattedText = log.FormattedDisplay, Level = log.Level, Message = log.Message });
                _showingArchiveSearch = true;
                HasLogs = _allLoadedLogs.Count > 0;
                ApplyLogFilter();
                StatusFeedbackMessage = $"已搜索全部已保存日志，找到 {logs.Count} 条（最多显示最近 500 条）；结果受日志级别筛选影响。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (ReferenceEquals(_logSearchCancellation, cancellation)) StatusFeedbackMessage = $"搜索失败：{ex.Message}";
            }
            finally
            {
                if (ReferenceEquals(_logSearchCancellation, cancellation)) _logSearchCancellation = null;
                cancellation.Dispose();
            }
        }

        partial void OnSelectedLogLevelIndexChanged(int value)
        {
            ApplyLogFilter();
        }

        public void ApplyLogFilter()
        {
            FilteredLogs.Clear();
            string query = LogSearchQuery.Trim();

            foreach (var item in _allLoadedLogs)
            {
                // 等级过滤
                if (SelectedLogLevelIndex == 1 && item.Level != "ERROR") continue;
                if (SelectedLogLevelIndex == 2 && item.Level != "ERROR" && item.Level != "WARN") continue;

                // 关键词过滤
                if (!string.IsNullOrEmpty(query) && 
                    item.FullFormattedText.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                FilteredLogs.Add(item);
            }
        }

        [RelayCommand]
        public async Task RefreshAllAsync()
        {
            if (TaskModel != null)
            {
                await LoadTaskAsync(TaskModel);
                StatusFeedbackMessage = $"已于 {DateTime.Now:HH:mm:ss} 刷新最新数据与日志";
            }
        }

        [RelayCommand]
        public void OpenLogsDirectory()
        {
            if (TaskModel == null) return;
            string logsDir = _logService.GetTaskLogDirectory(TaskModel.TaskId);
            try
            {
                if (!Directory.Exists(logsDir))
                {
                    Directory.CreateDirectory(logsDir);
                }
                Process.Start(new ProcessStartInfo("explorer.exe", logsDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                StatusFeedbackMessage = $"打开目录失败: {ex.Message}";
            }
        }

        [RelayCommand]
        public void CopyAllErrorsToClipboard()
        {
            var errors = _allLoadedLogs.Where(l => l.Level == "ERROR").Select(l => l.FullFormattedText).ToList();
            if (errors.Count == 0)
            {
                StatusFeedbackMessage = "当前任务没有错误日志记录。";
                return;
            }

            string combined = string.Join(Environment.NewLine, errors);
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(combined);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                StatusFeedbackMessage = $"已复制 {errors.Count} 条错误日志到剪贴板！";
            }
            catch
            {
                StatusFeedbackMessage = "复制失败，请重试。";
            }
        }

        [RelayCommand]
        public void ExportDiagnosticZip()
        {
            if (TaskModel == null) return;
            string targetZip = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"FNSync_Diag_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.zip");

            try
            {
                _logService.ExportTaskDiagnosticZip(TaskModel.TaskId, targetZip);
                StatusFeedbackMessage = $"诊断包已导出至桌面: {Path.GetFileName(targetZip)}";
            }
            catch (Exception ex)
            {
                StatusFeedbackMessage = $"导出诊断包异常: {ex.Message}";
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024 * 1024):F2} GiB";
            if (bytes >= 1024 * 1024)
                return $"{bytes / (1024.0 * 1024):F2} MiB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F1} KiB";
            return $"{bytes} B";
        }
    }
}
