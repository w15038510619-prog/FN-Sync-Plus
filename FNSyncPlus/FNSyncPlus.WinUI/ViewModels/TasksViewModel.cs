using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using Microsoft.UI.Dispatching;

namespace FNSyncPlus.WinUI.ViewModels
{
    public partial class TasksViewModel : ObservableObject
    {
        private readonly TaskManager _taskManager;
        private readonly AppSettings _settings;
        private readonly DispatcherQueue _dispatcherQueue;

        public ObservableCollection<SyncTaskItemViewModel> Tasks { get; } = new();

        [ObservableProperty]
        private int _totalTasksCount;

        [ObservableProperty]
        private string _totalTasksCountText = "0 个保护目录";

        [ObservableProperty]
        private int _syncingTasksCount;

        [ObservableProperty]
        private int _idleTasksCount;

        [ObservableProperty]
        private int _failedTasksCount;

        [ObservableProperty]
        private string _lastSyncSummary = "--";

        [ObservableProperty]
        private bool _hasTasks;

        [ObservableProperty]
        private bool _isEmpty;

        public event Action? RequestAddFolderWizard;
        public event Action<SyncTaskItemViewModel>? RequestTaskDetails;
        public event Action<SyncTaskItemViewModel>? RequestEditSettings;
        public event Action<SyncTaskItemViewModel>? RequestConfirmDelete;
        public event Action<SyncTaskItemViewModel>? RequestInspectInterruption;
        public event Action<string>? RequestOperationError;

        public TasksViewModel(TaskManager taskManager, AppSettings settings)
        {
            _taskManager = taskManager;
            _settings = settings;
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            _taskManager.TaskListChanged += OnTaskListChanged;
            _taskManager.TaskStateChanged += OnTaskStateChanged;
            _taskManager.TaskProgressChanged += OnTaskProgressChanged;

            ReloadTasks();
        }

        public void ReloadTasks()
        {
            Tasks.Clear();
            foreach (var task in _taskManager.Tasks)
            {
                var itemVm = new SyncTaskItemViewModel(task, _taskManager);
                itemVm.RequestViewDetails += t => RequestTaskDetails?.Invoke(t);
                itemVm.RequestEditSettings += t => RequestEditSettings?.Invoke(t);
                itemVm.RequestDelete += t => RequestConfirmDelete?.Invoke(t);
                itemVm.RequestInspectInterruption += t => RequestInspectInterruption?.Invoke(t);
                itemVm.RequestOperationError += message => RequestOperationError?.Invoke(message);
                Tasks.Add(itemVm);
            }

            UpdateMetrics();
        }

        private void OnTaskListChanged()
        {
            Helpers.UIThreadHelper.RunOnUIThread(() => ReloadTasks());
        }

        private void OnTaskStateChanged(SyncTask task)
        {
            Helpers.UIThreadHelper.RunOnUIThread(() =>
            {
                var vm = Tasks.FirstOrDefault(t => t.TaskId == task.TaskId);
                if (vm != null)
                {
                    vm.UpdateVisualState();
                }
                UpdateMetrics();
            });
        }

        private void OnTaskProgressChanged(SyncTask task, SyncProgressReport report)
        {
            Helpers.UIThreadHelper.RunOnUIThread(() =>
            {
                var vm = Tasks.FirstOrDefault(t => t.TaskId == task.TaskId);
                if (vm != null)
                {
                    vm.UpdateVisualState();
                }
            });
        }

        private void UpdateMetrics()
        {
            TotalTasksCount = Tasks.Count;
            TotalTasksCountText = $"{TotalTasksCount} 个保护目录";
            SyncingTasksCount = Tasks.Count(t => t.State == TaskSyncState.Syncing || t.State == TaskSyncState.Scanning);
            IdleTasksCount = Tasks.Count(t => t.State == TaskSyncState.Completed || t.State == TaskSyncState.Idle);
            FailedTasksCount = Tasks.Count(t => t.State == TaskSyncState.Error);

            HasTasks = TotalTasksCount > 0;
            IsEmpty = TotalTasksCount == 0;

            var lastTask = Tasks.Where(t => t.Model.LastSyncTime.HasValue)
                                .OrderByDescending(t => t.Model.LastSyncTime)
                                .FirstOrDefault();

            LastSyncSummary = lastTask != null && lastTask.Model.LastSyncTime.HasValue
                ? lastTask.Model.LastSyncTime.Value.ToString("yyyy-MM-dd HH:mm")
                : "--";
        }

        [RelayCommand]
        private void AddFolder()
        {
            RequestAddFolderWizard?.Invoke();
        }

        [RelayCommand]
        private void SyncAll()
        {
            _taskManager.TriggerSyncAll();
        }
    }
}
