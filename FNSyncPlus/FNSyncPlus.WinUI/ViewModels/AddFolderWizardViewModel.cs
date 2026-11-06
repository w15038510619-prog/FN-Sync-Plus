using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.WinUI.ViewModels
{
    public partial class PlannedTaskItem : ObservableObject
    {
        [ObservableProperty]
        private string _sourcePath = string.Empty;

        [ObservableProperty]
        private string _sourceFolderName = string.Empty;

        [ObservableProperty]
        private string _targetSubFolder = string.Empty;

        [ObservableProperty]
        private string _fullTargetUnc = string.Empty;

        [ObservableProperty]
        private string _finalTargetUnc = string.Empty;

        [ObservableProperty]
        private string _statusText = string.Empty;

        [ObservableProperty]
        private bool _isConflict;

        [ObservableProperty]
        private bool _isFailed;

        [ObservableProperty]
        private string _errorMessage = string.Empty;
    }

    public partial class AddFolderWizardViewModel : ObservableObject
    {
        private readonly TaskManager _taskManager;
        private readonly SmbAuthService _smbService;
        private readonly AppSettings _settings;
        private readonly FnOsIntegrationService _fnOsService;
        private readonly NasConnectionService _connectionService;

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

        public AddFolderWizardViewModel(
            TaskManager taskManager, 
            SmbAuthService smbService, 
            AppSettings settings, 
            NasBrowserViewModel nasBrowser, 
            FnOsIntegrationService fnOsService,
            NasConnectionService connectionService)
        {
            _taskManager = taskManager;
            _smbService = smbService;
            _settings = settings;
            NasBrowser = nasBrowser;
            _fnOsService = fnOsService;
            _connectionService = connectionService;

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
            // 优先支持飞牛官方「我的文件」原生个人空间
            if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider && fnProvider.IsAuthenticated)
            {
                NasDestinationRoot = "我的文件";
                SecurityTitle = "飞牛官方个人空间 [我的文件]";
                SecurityDescription = "已直连飞牛官方「我的文件」原生存储，将自动为选中的本地文件夹建立同名备份目录。";
                SecurityBadgeBg = "#DCFCE7";
                SecurityBadgeColor = "#15803D";
                IsTeamShareWarning = false;
                CanCreate = true;
                await RecomputePlannedTasksAsync();
                UpdateStepState();
                return;
            }

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

            if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider && 
                (NasDestinationRoot == "我的文件" || NasDestinationRoot.StartsWith("我的文件/") || !NasDestinationRoot.StartsWith(@"\\")))
            {
                SecurityTitle = "飞牛官方个人空间 [我的文件]";
                SecurityDescription = $"选定存储位置: {NasDestinationRoot}，具备专属读写权限与隔离保护。";
                SecurityBadgeBg = "#DCFCE7";
                SecurityBadgeColor = "#15803D";
                IsTeamShareWarning = false;
                CanCreate = true;
                await RecomputePlannedTasksAsync();
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
                if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider)
                {
                    NasBrowser.ConfirmSelection();
                    string sel = !string.IsNullOrWhiteSpace(NasBrowser.ConfirmedSelectedPath)
                        ? NasBrowser.ConfirmedSelectedPath
                        : NasBrowser.CurrentUncPath;

                    if (string.IsNullOrWhiteSpace(sel) || sel.Contains("FN_PERSONAL_SPACE_GUIDANCE"))
                    {
                        sel = "我的文件";
                    }

                    NasDestinationRoot = sel;
                    await EvaluateNasRootSecurityAsync();
                    await RecomputePlannedTasksAsync();

                    CurrentStep = 3;
                    StepIndicatorText = "步骤 3 / 3：自动规划预览与确认";
                    UpdateStepState();
                    return;
                }

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

            // 1. 飞牛原生「我的文件」同名规划通道
            if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider)
            {
                var allocatedFn = new System.Collections.Generic.List<string>();
                foreach (var localPath in SelectedLocalFolders)
                {
                    string desiredName = Path.GetFileName(localPath.TrimEnd('/', '\\'));
                    if (string.IsNullOrEmpty(desiredName)) desiredName = "备份数据";

                    string uniqueName = desiredName;
                    int counter = 1;
                    while (allocatedFn.Contains(uniqueName, StringComparer.OrdinalIgnoreCase))
                    {
                        uniqueName = $"{desiredName} ({counter++})";
                    }
                    allocatedFn.Add(uniqueName);

                    string rootPrefix = string.IsNullOrEmpty(NasDestinationRoot) || NasDestinationRoot == "我的文件"
                        ? "我的文件"
                        : NasDestinationRoot.TrimEnd('/', '\\');

                    string fullTarget = $"{rootPrefix}/{uniqueName}";

                    PlannedTasks.Add(new PlannedTaskItem
                    {
                        SourcePath = localPath,
                        SourceFolderName = desiredName,
                        TargetSubFolder = uniqueName,
                        FullTargetUnc = fullTarget,
                        FinalTargetUnc = fullTarget,
                        StatusText = uniqueName != desiredName ? "▲ 批次同名去重规划" : "✔ 自动规划同名备份",
                        IsConflict = uniqueName != desiredName
                    });
                }

                CanCreate = PlannedTasks.Count > 0;
                UpdateStepState();
                return;
            }

            // 2. 传统 SMB 共享规划通道
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
                    FinalTargetUnc = fullTarget,
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

            // 1. 飞牛官方「我的文件」原生任务创建
            if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider)
            {
                foreach (var item in PlannedTasks)
                {
                    string destSub = item.FullTargetUnc;
                    if (destSub.StartsWith("我的文件/"))
                    {
                        destSub = destSub.Substring("我的文件/".Length);
                    }
                    else if (destSub == "我的文件")
                    {
                        destSub = item.TargetSubFolder;
                    }

                    var mkRes = await fnProvider.CreateDirectoryAsync(destSub);
                    if (!mkRes.Success && mkRes.ErrorCode != 17)
                    {
                        item.IsFailed = true;
                        item.StatusText = "❌ 创建个人空间目录失败";
                        item.ErrorMessage = mkRes.ErrorMessage;
                        continue;
                    }

                    item.FinalTargetUnc = item.FullTargetUnc;
                    item.StatusText = "✔ 目标就绪，任务已建立";
                    item.IsFailed = false;

                    var task = new SyncTask
                    {
                        TaskId = Guid.NewGuid().ToString("N"),
                        TaskName = $"{item.SourceFolderName} 备份",
                        SourceFolder = item.SourcePath,
                        DestinationRoot = NasDestinationRoot,
                        DestinationFolder = item.FullTargetUnc,
                        NasDeviceId = _settings.NasServer,
                        Mode = SyncMode.OneWayBackup,
                        AutoSyncEnabled = true,
                        RealtimeWatchEnabled = true
                    };

                    _taskManager.AddTask(task);
                    _taskManager.TriggerSync(task.TaskId);
                    createdCount++;
                }

                if (createdCount > 0)
                {
                    _settings.PreferredBackupRoot = NasDestinationRoot;
                    _settings.Save();
                }

                StatusMessage = $"全部 {createdCount} 个个人空间同步任务已成功建立并启动！";
                return createdCount;
            }

            // 2. 传统 SMB 共享校验与创建通道
            var rootValidation = await _smbService.ValidateDirectoryAccessStateAsync(NasDestinationRoot, _settings.NasServer);
            if (!rootValidation.DoesShareExist || !rootValidation.IsDirectoryWritable)
            {
                StatusMessage = $"目标根共享无法访问或不可写: {rootValidation.DetailedMessage}";
                foreach (var item in PlannedTasks)
                {
                    item.IsFailed = true;
                    item.StatusText = "❌ 目标共享不可写";
                    item.ErrorMessage = rootValidation.DetailedMessage;
                }
                return 0;
            }

            int failedCount = 0;

            foreach (var item in PlannedTasks)
            {
                // 确保在 NAS 目标空间根目录下自动建立目标备份子目录（传入 item.TargetSubFolder 尊重批次同名去重规划）
                var (ensureOk, ensuredPath, ensureMsg) = await _fnOsService.EnsureSameNameBackupFolderAsync(
                    NasDestinationRoot, 
                    item.SourcePath, 
                    item.TargetSubFolder
                );

                if (!ensureOk)
                {
                    // 创建失败立即停止该任务，严禁使用失败目录作为同步目标启动同步！
                    item.IsFailed = true;
                    item.StatusText = $"❌ 创建目标失败: {ensureMsg}";
                    item.ErrorMessage = ensureMsg;
                    failedCount++;
                    continue;
                }

                // 创建成功，写入实际验证路径并更新状态
                item.FinalTargetUnc = ensuredPath;
                item.StatusText = "✔ 目标就绪，任务已建立";
                item.IsFailed = false;

                var task = new SyncTask
                {
                    TaskId = Guid.NewGuid().ToString("N"),
                    TaskName = item.SourceFolderName,
                    SourceFolder = item.SourcePath,
                    DestinationRoot = NasDestinationRoot,
                    DestinationFolder = ensuredPath,
                    Enabled = true,
                    AutoSyncEnabled = true,
                    RealtimeWatchEnabled = true,
                    IntervalMinutes = 5
                };

                _taskManager.AddTask(task);
                _taskManager.TriggerSync(task.TaskId);
                createdCount++;
            }

            // 只有在至少成功创建了任务时，才保存 PreferredBackupRoot，严禁提前保存无效根目录
            if (createdCount > 0)
            {
                _settings.PreferredBackupRoot = NasDestinationRoot;
                _settings.Save();
            }

            if (failedCount > 0)
            {
                StatusMessage = $"任务创建完毕：{createdCount} 个成功，{failedCount} 个失败。失败任务已中止未启动同步。";
            }
            else
            {
                StatusMessage = $"全部 {createdCount} 个同步任务已成功建立并启动！";
            }

            return createdCount;
        }
    }
}
