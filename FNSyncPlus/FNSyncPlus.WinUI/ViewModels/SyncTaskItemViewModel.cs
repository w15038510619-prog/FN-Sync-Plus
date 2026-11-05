using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.WinUI.ViewModels
{
    public partial class SyncTaskItemViewModel : ObservableObject
    {
        private readonly TaskManager _taskManager;

        public SyncTask Model { get; }

        public string TaskId => Model.TaskId;

        [ObservableProperty]
        private string _taskName;

        [ObservableProperty]
        private string _sourceFolder;

        [ObservableProperty]
        private string _destinationFolder;

        [ObservableProperty]
        private TaskSyncState _state;

        [ObservableProperty]
        private double _currentProgress;

        [ObservableProperty]
        private string _currentFile = string.Empty;

        [ObservableProperty]
        private string _currentSpeedFormatted = string.Empty;

        [ObservableProperty]
        private string _displayStatus = string.Empty;

        [ObservableProperty]
        private string _statusBadgeBgHex = "#F1F5F9";

        [ObservableProperty]
        private string _statusBadgeColorHex = "#475569";

        [ObservableProperty]
        private string _formattedLastSync = "--";

        [ObservableProperty]
        private bool _isSyncing;

        [ObservableProperty]
        private bool _isPaused;

        [ObservableProperty]
        private string _currentFileText = string.Empty;

        [ObservableProperty]
        private string _currentProgressText = "0%";

        public event Action<SyncTaskItemViewModel>? RequestViewDetails;
        public event Action<SyncTaskItemViewModel>? RequestDelete;

        public SyncTaskItemViewModel(SyncTask task, TaskManager taskManager)
        {
            Model = task;
            _taskManager = taskManager;

            _taskName = task.TaskName;
            _sourceFolder = task.SourceFolder;
            _destinationFolder = task.DestinationFolder;
            _state = task.State;
            _currentProgress = task.CurrentProgress;
            _currentFile = task.CurrentFile;

            UpdateVisualState();
        }

        public void UpdateVisualState()
        {
            State = Model.State;
            CurrentProgress = Model.CurrentProgress;
            CurrentFile = Model.CurrentFile;
            CurrentFileText = string.IsNullOrEmpty(Model.CurrentFile) ? string.Empty : $"传输: {Model.CurrentFile}";
            CurrentProgressText = $"{Model.CurrentProgress:F0}%";
            IsSyncing = Model.State == TaskSyncState.Syncing || Model.State == TaskSyncState.Scanning;
            IsPaused = Model.State == TaskSyncState.Paused;

            DisplayStatus = Model.DisplayStatus;
            StatusBadgeBgHex = Model.StateBadgeBgHex;
            StatusBadgeColorHex = Model.StateBadgeColorHex;

            if (Model.CurrentSpeedBytesPerSec > 0)
            {
                CurrentSpeedFormatted = $"{FormatSpeed(Model.CurrentSpeedBytesPerSec)} · ";
            }
            else
            {
                CurrentSpeedFormatted = string.Empty;
            }

            if (Model.LastSyncTime.HasValue)
            {
                var timeStr = Model.LastSyncTime.Value.ToString("yyyy-MM-dd HH:mm");
                FormattedLastSync = $"上次备份: {timeStr} · 扫描 {Model.LastScannedFiles} 个文件 · 转换 {Model.LastLongNameFiles} 项";
            }
            else
            {
                FormattedLastSync = "尚未进行初次同步";
            }
        }

        private static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec >= 1024 * 1024)
            {
                return $"{bytesPerSec / (1024 * 1024):F1} MB/s";
            }
            if (bytesPerSec >= 1024)
            {
                return $"{bytesPerSec / 1024:F0} KB/s";
            }
            return $"{bytesPerSec:F0} B/s";
        }

        [RelayCommand]
        private void RunSync()
        {
            _taskManager.TriggerSync(TaskId);
        }

        [RelayCommand]
        private void TogglePause()
        {
            if (IsPaused)
            {
                _taskManager.ResumeTask(TaskId);
            }
            else
            {
                _taskManager.PauseTask(TaskId);
            }
            UpdateVisualState();
        }

        [RelayCommand]
        private void ViewDetails()
        {
            RequestViewDetails?.Invoke(this);
        }

        [RelayCommand]
        private void DeleteTask()
        {
            RequestDelete?.Invoke(this);
        }
    }
}
