using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.WinUI.ViewModels
{
    public class PlannedTaskItem
    {
        public string SourcePath { get; set; } = string.Empty;
        public string SourceFolderName { get; set; } = string.Empty;
        public string TargetSubFolder { get; set; } = string.Empty;
        public string FullTargetUnc { get; set; } = string.Empty;
        public string StatusText { get; set; } = string.Empty;
        public bool IsConflict { get; set; }
    }

    public partial class AddFolderWizardViewModel : ObservableObject
    {
        private readonly TaskManager _taskManager;
        private readonly SmbAuthService _smbService;
        private readonly AppSettings _settings;
        private readonly FnOsIntegrationService _fnOsService;

        public NasBrowserViewModel NasBrowser { get; }

        public ObservableCollection<string> SelectedLocalFolders { get; } = new();
        public ObservableCollection<PlannedTaskItem> PlannedTasks { get; } = new();

        [ObservableProperty]
        private int _currentStep = 1; // 1 = Local Folders, 2 = NAS Destination, 3 = Auto-Plan Preview

        [ObservableProperty]
        private string _stepIndicatorText = "步骤 1 / 3：选择本地备份源文件夹";

        [ObservableProperty]
        private bool _canGoBack = false;

        [ObservableProperty]
        private bool _canGoNext = false;

        [ObservableProperty]
        private bool _isStep1 = true;

        [ObservableProperty]
        private bool _isStep2 = false;

        [ObservableProperty]
        private bool _isStep3 = false;

        [ObservableProperty]
        private string _nasDestinationRoot = string.Empty;

        [ObservableProperty]
        private string _securityTitle = "正在评估安全状态...";

        [ObservableProperty]
        private string _securityDescription = string.Empty;

        [ObservableProperty]
        private string _securityBadgeBg = "#F1F5F9";

        [ObservableProperty]
        private string _securityBadgeColor = "#475569";

        [ObservableProperty]
        private bool _isTeamShareWarning = false;

        [ObservableProperty]
        private bool _canCreate = false;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        public AddFolderWizardViewModel(TaskManager taskManager, SmbAuthService smbService, AppSettings settings, NasBrowserViewModel nasBrowser, FnOsIntegrationService fnOsService)
        {
            _taskManager = taskManager;
            _smbService = smbService;
            _settings = settings;
            NasBrowser = nasBrowser;
            _fnOsService = fnOsService;

            NasBrowser.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NasBrowser.CurrentUncPath) || 
                    e.PropertyName == nameof(NasBrowser.HasWritePermission) ||
                    e.PropertyName == nameof(NasBrowser.IsPersonalSpaceGuidanceActive))
                {
                    UpdateStepState();
                }
            };

            InitializeDefaultNasRoot();
            UpdateStepState();
        }

        private async void InitializeDefaultNasRoot()
        {
            string server = _settings.NasServer.Trim();
            string user = _settings.Username.Trim();

            if (string.IsNullOrEmpty(server))
            {
                NasDestinationRoot = string.Empty;
                SecurityTitle = "尚未连接 NAS";
                SecurityDescription = "请先在【NAS 设备】页面完成登录认证。";
                CanCreate = false;
                UpdateStepState();
                return;
            }

            var status = await _fnOsService.EvaluatePersonalSpaceAsync(server, user, _settings.PreferredBackupRoot);
            if (status.HasPersonalSpaceTarget)
            {
                NasDestinationRoot = status.PersonalSpaceRootUnc;
                SecurityTitle = status.DisplayTitle;
                SecurityDescription = status.Explanation;
                SecurityBadgeBg = "#DCFCE7";
                SecurityBadgeColor = "#15803D";
                IsTeamShareWarning = false;
                CanCreate = true;
                if (!string.IsNullOrWhiteSpace(NasDestinationRoot))
                {
                    await RecomputePlannedTasksAsync();
                }
            }
            else
            {
                NasDestinationRoot = string.Empty;
                SecurityTitle = status.DisplayTitle;
                SecurityDescription = status.Explanation;
                SecurityBadgeBg = "#FEF3C7";
                SecurityBadgeColor = "#D97706";
                IsTeamShareWarning = false;
                CanCreate = false;
            }

            UpdateStepState();
        }

        public async Task EvaluateNasRootSecurityAsync()
        {
            if (string.IsNullOrWhiteSpace(NasDestinationRoot))
            {
                SecurityTitle = "尚未指定 NAS 备份根目录";
                SecurityDescription = "请选择要保存备份的 NAS 文件夹。";
                SecurityBadgeBg = "#F1F5F9";
                SecurityBadgeColor = "#64748B";
                IsTeamShareWarning = false;
                CanCreate = false;
                UpdateStepState();
                return;
            }

            var (canWrite, writeErr) = await _smbService.TestDirectoryWritePermissionAsync(NasDestinationRoot);
            var eval = SmbAuthService.EvaluateShareSecurity(NasDestinationRoot, _settings.Username, canWrite, writeErr);

            SecurityTitle = eval.Title;
            SecurityDescription = eval.Description;
            SecurityBadgeBg = eval.BadgeBgHex;
            SecurityBadgeColor = eval.BadgeColorHex;
            IsTeamShareWarning = eval.State == NasShareSecurityState.TeamShare;

            await RecomputePlannedTasksAsync();
            UpdateStepState();
        }

        public void AddLocalFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

            string normalized = Path.GetFullPath(folderPath).TrimEnd('\\');
            if (!SelectedLocalFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                SelectedLocalFolders.Add(normalized);
                _ = RecomputePlannedTasksAsync();
                UpdateStepState();
            }
        }

        public void RemoveLocalFolder(string folderPath)
        {
            SelectedLocalFolders.Remove(folderPath);
            _ = RecomputePlannedTasksAsync();
            UpdateStepState();
        }

        public async Task NextStepAsync()
        {
            if (CurrentStep == 1)
            {
                if (SelectedLocalFolders.Count == 0) return;
                CurrentStep = 2;
                StepIndicatorText = "步骤 2 / 3：选择 NAS 备份位置";
                if (!string.IsNullOrWhiteSpace(NasDestinationRoot))
                {
                    _ = NasBrowser.NavigateToFolderAsync(NasDestinationRoot);
                }
                else
                {
                    await NasBrowser.InitializeAsync();
                }
            }
            else if (CurrentStep == 2)
            {
                if (NasBrowser.IsPersonalSpaceGuidanceActive)
                {
                    StatusMessage = "当前尚未配置个人专属 SMB 共享，无法继续。请根据指引配置或在左侧选择其他有效共享。";
                    return;
                }

                NasBrowser.ConfirmSelection();
                string selected = !string.IsNullOrWhiteSpace(NasBrowser.ConfirmedSelectedPath)
                    ? NasBrowser.ConfirmedSelectedPath
                    : NasBrowser.CurrentUncPath;

                if (string.IsNullOrWhiteSpace(selected) || selected.Contains("FN_PERSONAL_SPACE_GUIDANCE"))
                {
                    StatusMessage = "请先选择有效的 NAS 共享目录后再继续。";
                    return;
                }

                var (server, share, baseShareUnc, isServerRoot) = PathHelper.ParseUncShare(selected);
                if (isServerRoot || string.IsNullOrEmpty(share))
                {
                    StatusMessage = "请先在下方列表中点击进入具体的 NAS 共享文件夹后再进入下一步。";
                    return;
                }

                if (!NasBrowser.HasWritePermission)
                {
                    StatusMessage = "所选目录没有写入权限，无法作为备份存储位置。";
                    return;
                }

                NasDestinationRoot = selected;
                await EvaluateNasRootSecurityAsync();
                await RecomputePlannedTasksAsync();

                CurrentStep = 3;
                StepIndicatorText = "步骤 3 / 3：自动规划预览与确认";
            }
            UpdateStepState();
        }

        public void PreviousStep()
        {
            if (CurrentStep == 3)
            {
                CurrentStep = 2;
                StepIndicatorText = "步骤 2 / 3：选择 NAS 备份位置";
            }
            else if (CurrentStep == 2)
            {
                CurrentStep = 1;
                StepIndicatorText = "步骤 1 / 3：选择本地备份源文件夹";
            }
            UpdateStepState();
        }

        private void UpdateStepState()
        {
            IsStep1 = CurrentStep == 1;
            IsStep2 = CurrentStep == 2;
            IsStep3 = CurrentStep == 3;

            CanGoBack = CurrentStep > 1;
            CanGoNext = CurrentStep switch
            {
                1 => SelectedLocalFolders.Count > 0,
                2 => !NasBrowser.IsPersonalSpaceGuidanceActive 
                     && !string.IsNullOrWhiteSpace(NasBrowser.CurrentUncPath) 
                     && !NasBrowser.IsServerRoot 
                     && NasBrowser.HasWritePermission,
                3 => CanCreate && PlannedTasks.Count > 0,
                _ => false
            };

            if (CurrentStep == 1)
            {
                StatusMessage = SelectedLocalFolders.Count == 0 
                    ? "请先添加至少一个本地待备份文件夹以继续下一步。" 
                    : $"已选择 {SelectedLocalFolders.Count} 个待备份文件夹。";
            }
            else if (CurrentStep == 2)
            {
                if (NasBrowser.IsServerRoot)
                {
                    StatusMessage = "当前处于 NAS 服务器根目录，请在下方点击进入具体的共享文件夹后继续。";
                }
                else if (!NasBrowser.HasWritePermission)
                {
                    StatusMessage = "当前所选目录只读或无写入权限，请选择其他具备写入权限的文件夹。";
                }
                else
                {
                    StatusMessage = $"已选定 NAS 存储位置: {NasBrowser.CurrentUncPath}";
                }
            }
        }

        public async Task RecomputePlannedTasksAsync()
        {
            PlannedTasks.Clear();

            if (SelectedLocalFolders.Count == 0 || string.IsNullOrWhiteSpace(NasDestinationRoot))
            {
                CanCreate = false;
                UpdateStepState();
                return;
            }

            var allocated = new System.Collections.Generic.List<string>();

            foreach (var localPath in SelectedLocalFolders)
            {
                string desiredName = Path.GetFileName(localPath);
                if (string.IsNullOrEmpty(desiredName)) desiredName = "备份数据";

                var plan = await _smbService.PlanTargetSubFolderAsync(NasDestinationRoot, desiredName, localPath, allocated);
                allocated.Add(plan.FinalSubFolder);

                string fullTarget = Path.Combine(NasDestinationRoot, plan.FinalSubFolder);

                PlannedTasks.Add(new PlannedTaskItem
                {
                    SourcePath = localPath,
                    SourceFolderName = desiredName,
                    TargetSubFolder = plan.FinalSubFolder,
                    FullTargetUnc = fullTarget,
                    StatusText = plan.HasForeignConflict ? "▲ 批次同名去重规划" : (plan.IsExisting ? "✔ 复用现有目录" : "✔ 自动规划新建"),
                    IsConflict = plan.HasForeignConflict
                });
            }

            CanCreate = PlannedTasks.Count > 0;
            UpdateStepState();
        }

        public async Task<int> CreateAllTasksAsync()
        {
            await Task.Yield();
            if (!CanCreate || PlannedTasks.Count == 0) return 0;

            int createdCount = 0;
            _settings.PreferredBackupRoot = NasDestinationRoot;
            _settings.Save();

            foreach (var item in PlannedTasks)
            {
                // 确保在 NAS 个人空间根目录下自动建立同名备份子目录
                var (ensureOk, ensuredPath, _) = await _fnOsService.EnsureSameNameBackupFolderAsync(NasDestinationRoot, item.SourcePath);
                string finalTarget = ensureOk ? ensuredPath : item.FullTargetUnc;

                var task = new SyncTask
                {
                    TaskId = Guid.NewGuid().ToString("N"),
                    TaskName = item.SourceFolderName,
                    SourceFolder = item.SourcePath,
                    DestinationRoot = NasDestinationRoot,
                    DestinationFolder = finalTarget,
                    Enabled = true,
                    AutoSyncEnabled = true,
                    RealtimeWatchEnabled = true,
                    IntervalMinutes = 5
                };

                _taskManager.AddTask(task);
                _taskManager.TriggerSync(task.TaskId);
                createdCount++;
            }

            return createdCount;
        }
    }
}
