using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;
using FNSyncPlus.WinUI.Helpers;

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

        [ObservableProperty]
        private bool _isLongDirectory;

        [ObservableProperty]
        private int _utf8Bytes;
    }

    public partial class AddFolderWizardViewModel : ObservableObject
    {
        private readonly TaskManager _taskManager;
        private readonly SmbAuthService _smbService;
        private readonly AppSettings _settings;
        private readonly FnOsIntegrationService _fnOsService;
        private readonly NasConnectionService _connectionService;
        private readonly LongNameEngine _longNameEngine;
        private readonly SemaphoreSlim _recomputeLock = new(1, 1);

        public NasBrowserViewModel NasBrowser { get; }

        public ObservableCollection<string> SelectedLocalFolders { get; } = new();
        public ObservableCollection<PlannedTaskItem> PlannedTasks { get; } = new();

        [ObservableProperty]
        private int _currentStep = 1; // 1 = Local Folders, 2 = Auto-Plan Preview & Confirmation

        [ObservableProperty]
        private string _stepIndicatorText = "步骤 1 / 2：选择本地待备份文件夹";

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

        public bool ShowExperimentalReuse => _taskManager.UseExperimentalVersionedBackup;

        [ObservableProperty]
        private string _experimentalExistingRoot = string.Empty;

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

        [ObservableProperty]
        private string? _duplicateWarningMessage;

        [ObservableProperty]
        private bool _hasDuplicateConflict;

        [ObservableProperty]
        private SyncTask? _existingConflictTask;

        [ObservableProperty]
        private bool _isRecomputing;

        [ObservableProperty]
        private bool _isNasDisconnectedWarning = false;

        private string _lastConnectedUsername = string.Empty;
        private string _lastConnectedServer = string.Empty;

        public AddFolderWizardViewModel(
            TaskManager taskManager, 
            SmbAuthService smbService, 
            AppSettings settings, 
            NasBrowserViewModel nasBrowser, 
            FnOsIntegrationService fnOsService,
            NasConnectionService connectionService,
            LongNameEngine? longNameEngine = null)
        {
            _taskManager = taskManager;
            _smbService = smbService;
            _settings = settings;
            NasBrowser = nasBrowser;
            _fnOsService = fnOsService;
            _connectionService = connectionService;
            _longNameEngine = longNameEngine ?? new LongNameEngine();

            NasBrowser.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NasBrowser.CurrentUncPath) || 
                    e.PropertyName == nameof(NasBrowser.HasWritePermission) ||
                    e.PropertyName == nameof(NasBrowser.IsPersonalSpaceGuidanceActive))
                {
                    UpdateStepState();
                }
            };

            _connectionService.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NasConnectionService.IsConnected) ||
                    e.PropertyName == nameof(NasConnectionService.State))
                {
                    UIThreadHelper.RunOnUIThread(() =>
                    {
                        bool isConn = _connectionService.IsConnected &&
                                      _connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnP &&
                                      fnP.IsAuthenticated;
                        IsNasDisconnectedWarning = !isConn;
                        UpdateStepState();
                    });
                }
            };

            ResetSession();
        }

        /// <summary>
        /// 重置向导会话状态为全新会话，清空临时选择与规划结果，步骤回到第 1 步
        /// </summary>
        public void ResetSession()
        {
            UIThreadHelper.RunOnUIThread(() =>
            {
                SelectedLocalFolders.Clear();
                PlannedTasks.Clear();
                CurrentStep = 1;
                StepIndicatorText = "步骤 1 / 2：选择本地待备份文件夹";
                DuplicateWarningMessage = null;
                ExistingConflictTask = null;
                HasDuplicateConflict = false;
                ExperimentalExistingRoot = string.Empty;
                StatusMessage = "请添加需要自动备份至飞牛 NAS 的本地文件夹。";

                bool isConn = _connectionService.IsConnected &&
                              _connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnP &&
                              fnP.IsAuthenticated;
                IsNasDisconnectedWarning = !isConn;
                _lastConnectedUsername = _connectionService.Username;
                _lastConnectedServer = _connectionService.ServerAddress;

                InitializeDefaultNasRoot();
                UpdateStepState();
            });
        }

        /// <summary>
        /// 当中途 NAS 断开并重新连接成功后恢复向导会话（保留用户已选本地文件夹草稿）
        /// </summary>
        public void RestoreSessionOnReconnect()
        {
            UIThreadHelper.RunOnUIThread(async () =>
            {
                IsNasDisconnectedWarning = false;
                string currentUser = _connectionService.Username;
                string currentServer = _connectionService.ServerAddress;

                if (!string.IsNullOrEmpty(_lastConnectedUsername) &&
                    (!string.Equals(_lastConnectedUsername, currentUser, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(_lastConnectedServer, currentServer, StringComparison.OrdinalIgnoreCase)))
                {
                    ExperimentalExistingRoot = string.Empty;
                    InitializeDefaultNasRoot();
                }
                else
                {
                    InitializeDefaultNasRoot();
                    if (SelectedLocalFolders.Count > 0)
                    {
                        await RecomputePlannedTasksAsync();
                    }
                }

                _lastConnectedUsername = currentUser;
                _lastConnectedServer = currentServer;
                UpdateStepState();
            });
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
                CanCreate = SelectedLocalFolders.Count > 0;
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
                CanCreate = SelectedLocalFolders.Count > 0;
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
                SecurityBadgeBg = status.BadgeBgHex;
                SecurityBadgeColor = status.BadgeColorHex;
                IsTeamShareWarning = status.IsTeamShare;
                CanCreate = false;
            }

            UpdateStepState();
        }

        public async Task EvaluateNasRootSecurityAsync()
        {
            if (string.IsNullOrWhiteSpace(NasDestinationRoot))
            {
                SecurityTitle = "未选定 NAS 目录";
                SecurityDescription = "请先选择 NAS 备份目标位置。";
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
                CanCreate = SelectedLocalFolders.Count > 0;
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

        /// <summary>
        /// 异步安全添加本地文件夹，严格执行路径有效性检验、Windows 长路径解析与已有任务防重复拦截
        /// </summary>
        public async Task<bool> AddLocalFolderAsync(string folderPath)
        {
            DuplicateWarningMessage = null;
            HasDuplicateConflict = false;
            ExistingConflictTask = null;

            if (string.IsNullOrWhiteSpace(folderPath)) return false;

            // 1. 安全规范化路径，防崩溃解析
            if (!PathHelper.TryNormalizeFullPath(folderPath, out string normalized, out string? normErr))
            {
                StatusMessage = $"无法添加此文件夹：{normErr}";
                return false;
            }

            if (!Directory.Exists(normalized))
            {
                StatusMessage = $"本地文件夹不存在或无法访问: {normalized}";
                return false;
            }

            // 2. 检查是否与已有任务冲突（严禁根据单纯末级名称判断，必须按完整规范路径）
            var existingTask = _taskManager.Tasks.FirstOrDefault(t => 
                string.Equals(PathHelper.NormalizeLocalPath(t.SourceFolder), normalized, StringComparison.OrdinalIgnoreCase));
            
            if (existingTask != null)
            {
                DuplicateWarningMessage = $"该文件夹已添加至备份任务「{existingTask.TaskName}」，目标为 {existingTask.DestinationFolder}，无需重复添加！";
                ExistingConflictTask = existingTask;
                HasDuplicateConflict = true;
                StatusMessage = DuplicateWarningMessage;
                return false;
            }

            // 3. 检查当前会话内重复
            if (SelectedLocalFolders.Any(f => string.Equals(PathHelper.NormalizeLocalPath(f), normalized, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = $"该文件夹已在待添加列表中。";
                return false;
            }

            // 4. UI 线程安全添加到会话
            UIThreadHelper.RunOnUIThread(() =>
            {
                SelectedLocalFolders.Add(normalized);
            });

            await RecomputePlannedTasksAsync();
            UpdateStepState();
            return true;
        }

        public void AddLocalFolder(string folderPath)
        {
            _ = AddLocalFolderAsync(folderPath);
        }

        /// <summary>
        /// 移除已选本地文件夹
        /// </summary>
        public async Task RemoveLocalFolderAsync(string folderPath)
        {
            DuplicateWarningMessage = null;
            HasDuplicateConflict = false;
            ExistingConflictTask = null;

            string normalized = PathHelper.NormalizeLocalPath(folderPath);
            var item = SelectedLocalFolders.FirstOrDefault(f => 
                string.Equals(PathHelper.NormalizeLocalPath(f), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f, folderPath, StringComparison.OrdinalIgnoreCase));

            if (item != null)
            {
                UIThreadHelper.RunOnUIThread(() =>
                {
                    SelectedLocalFolders.Remove(item);
                });
                await RecomputePlannedTasksAsync();
                UpdateStepState();
            }
        }

        public void RemoveLocalFolder(string folderPath)
        {
            _ = RemoveLocalFolderAsync(folderPath);
        }

        public async Task NextStepAsync()
        {
            if (CurrentStep == 1)
            {
                if (SelectedLocalFolders.Count == 0) return;
                CurrentStep = 2;
                StepIndicatorText = "步骤 2 / 2：自动规划预览与确认";
                await EvaluateNasRootSecurityAsync();
                await RecomputePlannedTasksAsync();
            }
            UpdateStepState();
        }

        public void PreviousStep()
        {
            if (CurrentStep == 2)
            {
                CurrentStep = 1;
                StepIndicatorText = "步骤 1 / 2：选择本地待备份文件夹";
            }
            UpdateStepState();
        }

        public async Task SetCustomNasDestinationRootAsync(string newRoot)
        {
            if (string.IsNullOrWhiteSpace(newRoot)) return;
            NasDestinationRoot = newRoot.Trim();
            await EvaluateNasRootSecurityAsync();
            await RecomputePlannedTasksAsync();
            UpdateStepState();
        }

        private void UpdateStepState()
        {
            IsStep1 = CurrentStep == 1;
            IsStep2 = CurrentStep == 2;
            IsStep3 = false;

            CanGoBack = CurrentStep > 1;
            CanGoNext = CurrentStep == 1 && SelectedLocalFolders.Count > 0;
            CanCreate = CurrentStep == 2 && PlannedTasks.Count > 0 && !string.IsNullOrWhiteSpace(NasDestinationRoot);

            if (IsNasDisconnectedWarning)
            {
                CanGoNext = false;
                CanCreate = false;
            }

            if (CurrentStep == 1)
            {
                if (!HasDuplicateConflict)
                {
                    StatusMessage = SelectedLocalFolders.Count == 0 
                        ? "请先添加至少一个本地待备份文件夹以继续下一步。" 
                        : $"已选择 {SelectedLocalFolders.Count} 个待备份文件夹。点击【下一步】将自动为您规划 NAS 备份目录。";
                }
            }
            else if (CurrentStep == 2)
            {
                StatusMessage = $"已选定 NAS 存储位置: {NasDestinationRoot}，点击【确认创建并开始备份】即可启动。";
            }
        }

        /// <summary>
        /// 重新计算规划任务，受信号量锁保护，严防并发调用相互覆盖或破坏 ObservableCollection
        /// 全面支持超长中文目录名转换（使用统一 LongNameEngine，防止 NAS 255 字节报错）
        /// </summary>
        public async Task RecomputePlannedTasksAsync()
        {
            await _recomputeLock.WaitAsync();
            IsRecomputing = true;

            try
            {
                var newPlannedList = new List<PlannedTaskItem>();

                if (SelectedLocalFolders.Count == 0 || string.IsNullOrWhiteSpace(NasDestinationRoot))
                {
                    UIThreadHelper.RunOnUIThread(() =>
                    {
                        PlannedTasks.Clear();
                        CanCreate = false;
                        UpdateStepState();
                    });
                    return;
                }

                // 1. 飞牛官方「我的文件」同名规划通道
                if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider)
                {
                    var allocatedFn = new List<string>();

                    foreach (var localPath in SelectedLocalFolders)
                    {
                        string desiredName = Path.GetFileName(localPath.TrimEnd('/', '\\'));
                        if (string.IsNullOrEmpty(desiredName)) desiredName = "备份数据";

                        int utf8Bytes = PathHelper.GetUtf8ByteCount(desiredName);
                        bool isLongDirectory = utf8Bytes > 255;
                        string safeTargetSubFolder = desiredName;

                        // 超长目录名由长文件名引擎自动转换为合规短别名 (如 _FN_LONG_...)
                        if (isLongDirectory)
                        {
                            var (storedAlias, _) = _longNameEngine.TranslateDirPath(desiredName);
                            safeTargetSubFolder = storedAlias;
                        }

                        string uniqueName = safeTargetSubFolder;
                        int counter = 2;

                        // 批次内同名去重
                        while (allocatedFn.Contains(uniqueName, StringComparer.OrdinalIgnoreCase))
                        {
                            uniqueName = $"{safeTargetSubFolder} ({counter++})";
                        }

                        // 检查已有任务冲突去重（如果已有不同源文件夹的任务占用了同名目标，自动加序号避免冲突）
                        var existingWithSameDest = _taskManager.Tasks.FirstOrDefault(t => 
                            string.Equals(Path.GetFileName(t.DestinationFolder.TrimEnd('/', '\\')), uniqueName, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(PathHelper.NormalizeLocalPath(t.SourceFolder), PathHelper.NormalizeLocalPath(localPath), StringComparison.OrdinalIgnoreCase));
                        
                        while (existingWithSameDest != null)
                        {
                            uniqueName = $"{safeTargetSubFolder} ({counter++})";
                            existingWithSameDest = _taskManager.Tasks.FirstOrDefault(t => 
                                string.Equals(Path.GetFileName(t.DestinationFolder.TrimEnd('/', '\\')), uniqueName, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(PathHelper.NormalizeLocalPath(t.SourceFolder), PathHelper.NormalizeLocalPath(localPath), StringComparison.OrdinalIgnoreCase));
                        }

                        allocatedFn.Add(uniqueName);

                        string rootPrefix = string.IsNullOrEmpty(NasDestinationRoot) || NasDestinationRoot == "我的文件"
                            ? "我的文件"
                            : NasDestinationRoot.TrimEnd('/', '\\');

                        string fullTarget = $"{rootPrefix}/{uniqueName}";

                        bool isExistingConfig = _taskManager.Tasks.Any(t =>
                            string.Equals(PathHelper.NormalizeLocalPath(t.SourceFolder), PathHelper.NormalizeLocalPath(localPath), StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(t.DestinationFolder.TrimEnd('/', '\\'), fullTarget.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase));

                        string statusText = isLongDirectory 
                            ? $"✔ 超长目录映射别名 ({utf8Bytes} B -> {uniqueName})"
                            : (isExistingConfig ? "✔ 复用已有备份配置" : (uniqueName != safeTargetSubFolder ? "▲ 批次同名去重规划" : "✔ 自动规划同名备份"));

                        newPlannedList.Add(new PlannedTaskItem
                        {
                            SourcePath = localPath,
                            SourceFolderName = desiredName, // 保持用户真实的完整文件夹名 (即使 300 字节)
                            TargetSubFolder = uniqueName,   // 存储端合规子目录名 (<= 255 字节)
                            FullTargetUnc = fullTarget,
                            FinalTargetUnc = fullTarget,
                            StatusText = statusText,
                            IsConflict = uniqueName != safeTargetSubFolder,
                            IsLongDirectory = isLongDirectory,
                            Utf8Bytes = utf8Bytes
                        });
                    }
                }
                // 2. 传统 SMB 共享规划通道
                else
                {
                    var allocated = new List<string>();

                    foreach (var localPath in SelectedLocalFolders)
                    {
                        string desiredName = Path.GetFileName(localPath.TrimEnd('\\'));
                        if (string.IsNullOrEmpty(desiredName)) desiredName = "备份数据";

                        int utf8Bytes = PathHelper.GetUtf8ByteCount(desiredName);
                        bool isLongDirectory = utf8Bytes > 255;
                        string safeDesiredName = desiredName;

                        if (isLongDirectory)
                        {
                            var (storedAlias, _) = _longNameEngine.TranslateDirPath(desiredName);
                            safeDesiredName = storedAlias;
                        }

                        var plan = await _smbService.PlanTargetSubFolderAsync(NasDestinationRoot, safeDesiredName, localPath, allocated);
                        allocated.Add(plan.FinalSubFolder);

                        string fullTarget = Path.Combine(NasDestinationRoot, plan.FinalSubFolder);

                        string statusText = isLongDirectory
                            ? $"✔ 超长目录映射别名 ({utf8Bytes} B -> {plan.FinalSubFolder})"
                            : (plan.HasForeignConflict ? "▲ 批次同名去重规划" : (plan.IsExisting ? "✔ 复用现有目录" : "✔ 自动规划新建"));

                        newPlannedList.Add(new PlannedTaskItem
                        {
                            SourcePath = localPath,
                            SourceFolderName = desiredName,
                            TargetSubFolder = plan.FinalSubFolder,
                            FullTargetUnc = fullTarget,
                            FinalTargetUnc = fullTarget,
                            StatusText = statusText,
                            IsConflict = plan.HasForeignConflict,
                            IsLongDirectory = isLongDirectory,
                            Utf8Bytes = utf8Bytes
                        });
                    }
                }

                // UI 线程批量刷新
                UIThreadHelper.RunOnUIThread(() =>
                {
                    PlannedTasks.Clear();
                    foreach (var item in newPlannedList)
                    {
                        PlannedTasks.Add(item);
                    }
                    CanCreate = PlannedTasks.Count > 0 && !string.IsNullOrWhiteSpace(NasDestinationRoot);
                    UpdateStepState();
                });
            }
            finally
            {
                IsRecomputing = false;
                _recomputeLock.Release();
            }
        }

        /// <summary>
        /// 创建并启动全部已规划的同步任务
        /// </summary>
        public async Task<int> CreateAllTasksAsync()
        {
            await Task.Yield();
            if (!CanCreate || PlannedTasks.Count == 0) return 0;
            if (!string.IsNullOrWhiteSpace(ExperimentalExistingRoot) && (!ShowExperimentalReuse || PlannedTasks.Count != 1))
            {
                SecurityTitle = "复用已有目录仅支持一个隔离测试任务";
                SecurityDescription = "请仅选择一个本地文件夹，或清除已有目录后重试。";
                return 0;
            }

            // 运行时连接底线守卫校验：必须处于 Connected 且存储提供程序为已鉴权的 FnOsPersonalStorageProvider
            if (_connectionService.State != NasConnectionState.Connected)
            {
                StatusMessage = "NAS 尚未连接，无法创建任务。请先连接飞牛官方个人空间。";
                foreach (var item in PlannedTasks)
                {
                    item.IsFailed = true;
                    item.StatusText = "❌ NAS 未连接";
                    item.ErrorMessage = StatusMessage;
                }
                IsNasDisconnectedWarning = true;
                UpdateStepState();
                return 0;
            }

            int createdCount = 0;

            // 1. 飞牛官方「我的文件」原生任务创建
            if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider)
            {
                if (!fnProvider.IsAuthenticated)
                {
                    StatusMessage = "飞牛个人空间鉴权已失效，请重新连接。";
                    return 0;
                }

                // 检查授权用户名与配置是否匹配
                if (!string.IsNullOrEmpty(fnProvider.CurrentUsername) &&
                    !string.IsNullOrEmpty(_settings.Username) &&
                    !string.Equals(fnProvider.CurrentUsername, _settings.Username, StringComparison.OrdinalIgnoreCase))
                {
                    StatusMessage = $"当前登录账号 ({fnProvider.CurrentUsername}) 与配置账号 ({_settings.Username}) 不一致，请重新连接。";
                    return 0;
                }
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

                    try
                    {
                        var created = _taskManager.UseExperimentalVersionedBackup
                            ? (await fnProvider.GetItemAsync(destSub) == null ? ExclusiveDirectoryResult.Created : ExclusiveDirectoryResult.AlreadyExists)
                            : await fnProvider.TryCreateDirectoryExclusiveAsync(destSub);
                        if (created != ExclusiveDirectoryResult.Created)
                        {
                            item.IsFailed = true;
                            item.StatusText = "❌ 目标目录已存在";
                            item.ErrorMessage = "飞牛上已有同名目录，未建立任务。请选择其他备份位置，避免覆盖已有文件。";
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        item.IsFailed = true;
                        item.StatusText = "❌ 创建个人空间目录失败";
                        item.ErrorMessage = ex.Message;
                        continue;
                    }

                    item.FinalTargetUnc = item.FullTargetUnc;
                    item.StatusText = _taskManager.UseExperimentalVersionedBackup ? "✔ 隔离试验任务已建立，目标将在同步时创建" : "✔ 目标就绪，任务已建立";
                    item.IsFailed = false;

                    var task = new SyncTask
                    {
                        TaskId = Guid.NewGuid().ToString("N"),
                        TaskName = $"{item.SourceFolderName} 备份",
                        SourceFolder = item.SourcePath,
                        DestinationRoot = NasDestinationRoot,
                        DestinationFolder = item.FullTargetUnc,
                        ExperimentalExistingRoot = ShowExperimentalReuse && !string.IsNullOrWhiteSpace(ExperimentalExistingRoot)
                            ? ExperimentalExistingRoot : null,
                        ProviderType = StorageProviderType.FnOsPersonal,
                        NasUsername = _settings.Username,
                        NasDeviceId = _settings.NasServer,
                        Mode = SyncMode.OneWayBackup,
                        Enabled = true,
                        AutoSyncEnabled = true,
                        RealtimeWatchEnabled = true,
                        IntervalMinutes = 5
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

                int fnFailedCount = PlannedTasks.Count - createdCount;
                StatusMessage = fnFailedCount == 0
                    ? $"{createdCount} 个个人空间同步任务已建立并启动。"
                    : $"已建立 {createdCount} 个任务，{fnFailedCount} 个未建立；请查看各项原因。";
                
                // 全部创建成功后，重置会话，杜绝残留旧文件夹记录
                if (createdCount == PlannedTasks.Count)
                {
                    ResetSession();
                }
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
                var (ensureOk, ensuredPath, ensureMsg) = await _fnOsService.EnsureSameNameBackupFolderAsync(
                    NasDestinationRoot, 
                    item.SourcePath, 
                    item.TargetSubFolder
                );

                if (!ensureOk)
                {
                    item.IsFailed = true;
                    item.StatusText = $"❌ 创建目标失败: {ensureMsg}";
                    item.ErrorMessage = ensureMsg;
                    failedCount++;
                    continue;
                }

                item.FinalTargetUnc = ensuredPath;
                item.StatusText = "✔ 目标就绪，任务已建立";
                item.IsFailed = false;

                var task = new SyncTask
                {
                    TaskId = Guid.NewGuid().ToString("N"),
                    TaskName = $"{item.SourceFolderName} 备份",
                    SourceFolder = item.SourcePath,
                    DestinationRoot = NasDestinationRoot,
                    DestinationFolder = ensuredPath,
                    ProviderType = StorageProviderType.SmbShare,
                    NasUsername = _settings.Username,
                    NasDeviceId = _settings.NasServer,
                    Mode = SyncMode.OneWayBackup,
                    Enabled = true,
                    AutoSyncEnabled = true,
                    RealtimeWatchEnabled = true,
                    IntervalMinutes = 5
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

            StatusMessage = failedCount > 0 
                ? $"已成功创建 {createdCount} 个任务，{failedCount} 个任务失败。" 
                : $"全部 {createdCount} 个同步任务已成功建立并启动！";

            if (createdCount > 0 && failedCount == 0)
            {
                ResetSession();
            }

            return createdCount;
        }
    }
}
