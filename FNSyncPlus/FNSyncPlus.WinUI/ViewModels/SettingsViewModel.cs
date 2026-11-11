using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.WinUI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly AppSettings _settings;
        private readonly NasConnectionService _connectionService;
        private readonly UpdateService _updates;
        private readonly BackupCapacityAnalyzer _capacity = new();
        private CancellationTokenSource? _updateCts;

        public bool CanConfigureStartup => !_settings.IsVersionedBetaProfile && !_settings.IsInMemoryOnly && _settings.IsolatedSessionPath == null;

        [ObservableProperty]
        private bool _startWithWindows;

        [ObservableProperty]
        private bool _minimizeToTray;

        [ObservableProperty]
        private int _maxConcurrentSyncsIndex;

        [ObservableProperty]
        private string _statusNotice = string.Empty;

        // ---- 软件更新 ----

        [ObservableProperty]
        private string _currentVersionText = string.Empty;

        [ObservableProperty]
        private string _updateStatus = string.Empty;

        [ObservableProperty]
        private string _updateSupportMessage = string.Empty;

        [ObservableProperty]
        private string _updateFeedUrl = string.Empty;

        [ObservableProperty]
        private int _updateChannelIndex;

        [ObservableProperty]
        private int _updateModeIndex;

        [ObservableProperty]
        private bool _unattendedEnabled;

        [ObservableProperty]
        private bool _isUpdateBusy;

        [ObservableProperty]
        private double _downloadProgress;

        [ObservableProperty]
        private bool _showDownloadProgress;

        [ObservableProperty]
        private string _lastCheckText = string.Empty;

        [ObservableProperty]
        private bool _canInstallUpdate;

        [ObservableProperty]
        private string _installBlockedReason = string.Empty;

        // ---- 备份容量与保留策略（只读） ----

        public ObservableCollection<string> CapacityTasks { get; } = new();

        [ObservableProperty]
        private int _capacityTaskIndex = -1;

        [ObservableProperty]
        private string _capacitySummary = "选择一个备份任务后可查看 NAS 上的空间占用。";

        [ObservableProperty]
        private string _retentionSummary = string.Empty;

        [ObservableProperty]
        private double _retentionKeepVersions = 5;

        [ObservableProperty]
        private double _retentionKeepDays = 30;

        [ObservableProperty]
        private bool _isCapacityBusy;

        public bool CanCheckUpdate => _updates.CanCheck;

        public bool CanRunCapacity => CapacityTasks.Count > 0 && !IsCapacityBusy;

        /// <summary>Unattended install only makes sense for the unattended mode.</summary>
        public bool UnattendedSelectable => UpdateModeIndex == (int)AutoUpdateMode.UnattendedInstall;

        partial void OnUpdateModeIndexChanged(int value) => OnPropertyChanged(nameof(UnattendedSelectable));

        partial void OnIsCapacityBusyChanged(bool value) => OnPropertyChanged(nameof(CanRunCapacity));

        partial void OnCapacityTaskIndexChanged(int value) => OnPropertyChanged(nameof(CanRunCapacity));

        public SettingsViewModel(AppSettings settings, NasConnectionService connectionService, UpdateService updates)
        {
            _settings = settings;
            _connectionService = connectionService;
            _updates = updates;

            _startWithWindows = CanConfigureStartup && settings.StartWithWindows;
            _minimizeToTray = settings.MinimizeToTray;

            _maxConcurrentSyncsIndex = settings.MaxConcurrentSyncs switch
            {
                1 => 0,
                2 => 1,
                3 => 2,
                4 => 3,
                _ => 1
            };

            var update = _updates.Settings;
            _currentVersionText = $"当前版本 {UpdateService.CurrentVersion}（{_updates.CurrentChannelName} 通道）";
            _updateFeedUrl = update.ManifestUrl;
            _updateChannelIndex = string.Equals(update.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _updateModeIndex = update.AutoMode is >= 0 and <= 2 ? update.AutoMode : 0;
            _unattendedEnabled = update.UnattendedEnabled;
            _updateSupportMessage = _updates.SupportMessage;
            _installBlockedReason = _updates.CanInstall
                ? "安装将在备份任务安全结束后进行，程序会自动重启。"
                : _updates.SupportMessage;
            _canInstallUpdate = _updates.CanInstall;
            _lastCheckText = update.LastCheckUtc is { } last
                ? $"上次检查：{last.ToLocalTime():yyyy-MM-dd HH:mm}　结果：{update.LastResult}"
                : "尚未检查更新";
            _updateStatus = update.LastResult;

            foreach (var task in settings.Tasks)
                CapacityTasks.Add($"{task.TaskName} → {task.DestinationFolder}");
            if (CapacityTasks.Count > 0) CapacityTaskIndex = 0;
        }

        private SyncTask? SelectedCapacityTask =>
            CapacityTaskIndex >= 0 && CapacityTaskIndex < _settings.Tasks.Count ? _settings.Tasks[CapacityTaskIndex] : null;

        [RelayCommand]
        public async Task AnalyzeCapacityAsync()
        {
            var task = SelectedCapacityTask;
            if (task == null) { CapacitySummary = "请先选择一个备份任务。"; return; }
            var provider = _connectionService.CurrentStorageProvider;
            if (provider == null || !provider.IsAuthenticated) { CapacitySummary = "请先连接 NAS 后再统计容量。"; return; }
            IsCapacityBusy = true;
            CapacitySummary = "正在统计容量（只读，不会修改任何备份）…";
            try
            {
                var report = await _capacity.AnalyzeAsync(provider, task.DestinationFolder).ConfigureAwait(true);
                CapacitySummary = report.ToSummary();
            }
            catch (Exception ex)
            {
                CapacitySummary = $"统计失败：{ex.Message}";
            }
            finally { IsCapacityBusy = false; }
        }

        [RelayCommand]
        public async Task PreviewRetentionAsync()
        {
            var task = SelectedCapacityTask;
            if (task == null) { RetentionSummary = "请先选择一个备份任务。"; return; }
            var provider = _connectionService.CurrentStorageProvider;
            if (provider == null || !provider.IsAuthenticated) { RetentionSummary = "请先连接 NAS 后再预览保留策略。"; return; }
            IsCapacityBusy = true;
            RetentionSummary = "正在评估保留策略（只读预览）…";
            try
            {
                var policy = new RetentionPolicy
                {
                    KeepVersions = Math.Max(1, (int)RetentionKeepVersions),
                    KeepDays = Math.Max(0, (int)RetentionKeepDays)
                };
                var preview = await _capacity.PreviewRetentionAsync(provider, task.DestinationFolder, policy).ConfigureAwait(true);
                var text = new System.Text.StringBuilder();
                text.AppendLine(preview.Summary);
                foreach (var version in preview.Versions)
                    text.AppendLine($"  版本 #{version.Sequence} {version.CommittedAt.ToLocalTime():yyyy-MM-dd HH:mm} " +
                                    $"{CapacityReport.Format(version.ObjectBytes)} → {(version.Keep ? "保留" : "可清理")}（{version.Reason}）");
                if (preview.Blockers.Count > 0)
                    foreach (string blocker in preview.Blockers) text.AppendLine($"  阻止原因：{blocker}");
                RetentionSummary = text.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                RetentionSummary = $"评估失败：{ex.Message}";
            }
            finally { IsCapacityBusy = false; }
        }

        [RelayCommand]
        public void SaveSettings()
        {
            _settings.StartWithWindows = CanConfigureStartup && StartWithWindows;
            _settings.MinimizeToTray = MinimizeToTray;
            _settings.MaxConcurrentSyncs = MaxConcurrentSyncsIndex switch
            {
                0 => 1,
                1 => 2,
                2 => 3,
                3 => 4,
                _ => 2
            };
            _settings.Save();
            SaveUpdateSettings();
            StatusNotice = CanConfigureStartup ? "设置已保存成功。" : "设置已保存；此试用或测试配置暂不支持开机自动启动。";
        }

        [RelayCommand]
        public async Task CheckUpdateAsync()
        {
            if (!_updates.CanCheck)
            {
                UpdateStatus = _updates.SupportMessage;
                return;
            }
            if (IsUpdateBusy) return;
            SaveUpdateSettings();
            IsUpdateBusy = true;
            UpdateStatus = "正在检查更新…";
            try
            {
                var result = await _updates.CheckAsync().ConfigureAwait(true);
                UpdateStatus = result.HasUpdate
                    ? $"发现新版本 {result.Version}。{result.Notes}"
                    : result.Error.Length > 0 ? result.Error : "已是最新版本。";
                LastCheckText = $"上次检查：{DateTime.Now:yyyy-MM-dd HH:mm}";
            }
            catch (Exception ex)
            {
                UpdateStatus = $"检查更新失败：{ex.Message}";
            }
            finally
            {
                IsUpdateBusy = false;
            }
        }

        [RelayCommand]
        public async Task DownloadUpdateAsync()
        {
            if (!_updates.CanCheck) { UpdateStatus = _updates.SupportMessage; return; }
            if (IsUpdateBusy) return;
            IsUpdateBusy = true;
            ShowDownloadProgress = true;
            DownloadProgress = 0;
            UpdateStatus = "正在下载更新包…";
            _updateCts = new CancellationTokenSource();
            try
            {
                var check = await _updates.CheckAsync(_updateCts.Token).ConfigureAwait(true);
                if (!check.HasUpdate)
                {
                    UpdateStatus = check.Error.Length > 0 ? check.Error : "已是最新版本，无需下载。";
                    return;
                }
                var progress = new Progress<long>(bytes =>
                {
                    // The total size is unknown until the manifest is read; show MiB downloaded.
                    DownloadProgress = bytes / 1024d / 1024d;
                    UpdateStatus = $"正在下载更新包：{DownloadProgress:F1} MiB";
                });
                var result = await _updates.DownloadAsync(progress, _updateCts.Token).ConfigureAwait(true);
                UpdateStatus = result.Ok ? $"更新包已下载并校验通过：{Path.GetFileName(result.FilePath)}" : result.Error;
            }
            catch (OperationCanceledException)
            {
                UpdateStatus = "下载已取消。";
            }
            catch (Exception ex)
            {
                UpdateStatus = $"下载失败：{ex.Message}";
            }
            finally
            {
                IsUpdateBusy = false;
                ShowDownloadProgress = false;
                _updateCts?.Dispose();
                _updateCts = null;
            }
        }

        /// <summary>
        /// Installs the newest downloaded package. The running program must exit before its files can
        /// be replaced, so the updater is started first with our PID and it waits for a clean exit.
        /// A running backup is never killed: if it cannot finish in time the install is deferred.
        /// </summary>
        [RelayCommand]
        public async Task InstallUpdateAsync()
        {
            if (!_updates.CanInstall) { UpdateStatus = _updates.SupportMessage; return; }
            if (IsUpdateBusy) return;
            IsUpdateBusy = true;
            try
            {
                var check = await _updates.CheckAsync().ConfigureAwait(true);
                if (!check.HasUpdate)
                {
                    UpdateStatus = check.Error.Length > 0 ? check.Error : "没有可安装的新版本。";
                    return;
                }
                var download = await _updates.DownloadAsync(null).ConfigureAwait(true);
                if (!download.Ok) { UpdateStatus = download.Error; return; }
                string sha = FNSyncPlus.Core.Utils.HashHelper.ComputeFileSha256(download.FilePath);
                var (readiness, plan) = await _updates.PrepareInstallAsync(check.Version, download.FilePath, sha,
                    TimeSpan.FromMinutes(10)).ConfigureAwait(true);
                if (!plan.Ready)
                {
                    UpdateStatus = readiness.Reason;
                    return;
                }
                UpdateStatus = "正在启动更新器，程序即将退出并在更新后自动重启…";
                Process.Start(new ProcessStartInfo
                {
                    FileName = plan.Executable,
                    Arguments = plan.Arguments,
                    UseShellExecute = false,
                    WorkingDirectory = _updates.InstallRoot ?? AppContext.BaseDirectory
                });
                Microsoft.UI.Xaml.Application.Current.Exit();
            }
            catch (Exception ex)
            {
                UpdateStatus = $"安装失败：{ex.Message}";
            }
            finally
            {
                IsUpdateBusy = false;
            }
        }

        [RelayCommand]
        public void SkipVersion()
        {
            var settings = _updates.Settings;
            if (string.IsNullOrWhiteSpace(settings.LastResult)) return;
            settings.SkippedVersion = null;
            _updates.Save();
            UpdateStatus = "已取消跳过记录，下次检查将重新提示。";
        }

        private void SaveUpdateSettings()
        {
            var update = _updates.Settings;
            update.ManifestUrl = UpdateFeedUrl.Trim();
            update.Channel = UpdateChannelIndex == 1 ? "beta" : "stable";
            update.AutoMode = UpdateModeIndex;
            update.UnattendedEnabled = UnattendedEnabled && UpdateModeIndex == (int)AutoUpdateMode.UnattendedInstall;
            _updates.Save();
        }

        [RelayCommand]
        public async System.Threading.Tasks.Task ClearCredentials()
        {
            var result = await _connectionService.ForgetSavedCredentialsAsync();
            StatusNotice = result.Message;
        }
    }
}
