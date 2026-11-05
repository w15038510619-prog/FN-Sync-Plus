using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.WinUI.ViewModels
{
    public class LongNameMappingDisplayItem
    {
        public string OriginalRel { get; set; } = string.Empty;
        public string StoredRel { get; set; } = string.Empty;
        public int Utf8Bytes { get; set; }
        public string Utf8BytesFormatted => $"{Utf8Bytes} B";
        public string Sha256 { get; set; } = string.Empty;
    }

    public partial class TaskDetailsViewModel : ObservableObject
    {
        private readonly TaskManager _taskManager;
        private readonly ManifestManager _manifestManager;

        public SyncTask? TaskModel { get; private set; }

        [ObservableProperty]
        private string _taskName = string.Empty;

        [ObservableProperty]
        private string _sourceFolder = string.Empty;

        [ObservableProperty]
        private string _destinationFolder = string.Empty;

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
        private string _transferredSummaryText = "累计传输: 0 B";

        [ObservableProperty]
        private string _scannedFilesText = "扫描: 0";

        [ObservableProperty]
        private string _uploadedFilesText = "上传: 0";

        [ObservableProperty]
        private string _unchangedFilesText = "跳过未改: 0";

        [ObservableProperty]
        private string _longNameFilesText = "超长文件名: 0";

        [ObservableProperty]
        private string _failedFilesText = "失败: 0";

        public ObservableCollection<LongNameMappingDisplayItem> LongNameMappings { get; } = new();
        public ObservableCollection<string> TaskLogs { get; } = new();

        public TaskDetailsViewModel(TaskManager taskManager, ManifestManager manifestManager)
        {
            _taskManager = taskManager;
            _manifestManager = manifestManager;
        }

        public async Task LoadTaskAsync(SyncTask task)
        {
            TaskModel = task;
            TaskName = task.TaskName;
            SourceFolder = task.SourceFolder;
            DestinationFolder = task.DestinationFolder;

            ScannedFiles = task.LastScannedFiles;
            UploadedFiles = task.LastUploadedFiles;
            UnchangedFiles = task.LastUnchangedFiles;
            LongNameFiles = task.LastLongNameFiles;
            FailedFiles = task.LastFailedFiles;
            TransferredBytesFormatted = FormatBytes(task.LastTransferredBytes);

            TransferredSummaryText = $"累计传输: {TransferredBytesFormatted}";
            ScannedFilesText = $"扫描: {ScannedFiles}";
            UploadedFilesText = $"上传: {UploadedFiles}";
            UnchangedFilesText = $"跳过未改: {UnchangedFiles}";
            LongNameFilesText = $"超长文件名: {LongNameFiles}";
            FailedFilesText = $"失败: {FailedFiles}";

            // 加载日志
            TaskLogs.Clear();
            var logs = _taskManager.GetTaskLogs(task.TaskId);
            foreach (var l in logs)
            {
                TaskLogs.Add(l);
            }

            // 读取清单长文件名映射
            LongNameMappings.Clear();
            if (Directory.Exists(task.DestinationFolder))
            {
                try
                {
                    var manifest = await _manifestManager.LoadManifestAsync(task.DestinationFolder);
                    foreach (var f in manifest.Files)
                    {
                        if (f.IsLongName)
                        {
                            LongNameMappings.Add(new LongNameMappingDisplayItem
                            {
                                OriginalRel = f.OriginalRel,
                                StoredRel = f.StoredRel,
                                Utf8Bytes = System.Text.Encoding.UTF8.GetByteCount(f.OriginalRel),
                                Sha256 = f.Sha256
                            });
                        }
                    }
                }
                catch { }
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
            if (bytes >= 1024 * 1024)
                return $"{bytes / (1024.0 * 1024):F2} MB";
            if (bytes >= 1024)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }
    }
}
