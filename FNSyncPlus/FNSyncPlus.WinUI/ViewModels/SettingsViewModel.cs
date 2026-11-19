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
using FNSyncPlus.WinUI.Services;

namespace FNSyncPlus.WinUI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly AppSettings _settings;
        private readonly NasConnectionService _connectionService;
        private readonly UpdateService _updates;
        private readonly UpdateScheduler? _scheduler;
        private readonly BackupCapacityAnalyzer _capacity = new();
        private CancellationTokenSource? _updateCts;

        private readonly string? _startupLauncher;
        private bool _refreshingStartup;
        public bool CanConfigureStartup => _startupLauncher != null;

        partial void OnStartWithWindowsChanged(bool oldValue, bool newValue)
        {
            if (_refreshingStartup || _startupLauncher == null) return;
            try
            {
                AppSettings.SetStartupRegistration(_startupLauncher, newValue);
                _settings.StartWithWindows = newValue;
                _settings.Save();
                StatusNotice = newValue ? "下次登录 Windows 时会自动启动。" : "已关闭开机自动启动。";
            }
            catch (Exception ex)
            {
                App.LogCrashException(ex, "Settings.Startup");
                _refreshingStartup = true;
                try { StartWithWindows = AppSettings.IsStartupRegistered(_startupLauncher); }
                catch { StartWithWindows = oldValue; }
                finally { _refreshingStartup = false; }
                StatusNotice = "无法保存开机启动设置，请检查当前 Windows 用户的启动项权限。";
            }
        }

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

        public string ProductVersion => $"v{UpdateService.DisplayVersion}";

        [ObservableProperty] private string _updateProxyUrl = string.Empty;
        [ObservableProperty] private double _connectTimeoutSeconds;
        [ObservableProperty] private double _checkTimeoutSeconds;
        [ObservableProperty] private double _downloadTimeoutMinutes;
        [ObservableProperty] private bool _canCancelUpdate;

        public bool CanRunUpdate => CanCheckUpdate && !IsUpdateBusy;
        public bool CanStartInstall => CanInstallUpdate && !IsUpdateBusy && ReadyVersion.Length > 0;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStartInstall))]
        private string _readyVersion = "";
        [ObservableProperty] private string _updateButtonLabel = "更新";
        partial void OnIsUpdateBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanRunUpdate));
            OnPropertyChanged(nameof(CanStartInstall));
        }

        [ObservableProperty]
        private int _updateChannelIndex;

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

        private void OnSchedulerChanged(UpdateSchedulerEvent evt)
        {
            // Raised from a background thread; marshal before touching bound properties.
            Helpers.UIThreadHelper.RunOnUIThread(() =>
            {
                if (IsUpdateBusy) return;
                ReadyVersion = evt.Kind == "ready" ? evt.Version : "";
                UpdateButtonLabel = evt.Kind switch
                {
                    "ready" or "available" => "可更新",
                    "downloading" => "下载中",
                    "error" => "更新异常",
                    _ => "更新"
                };
                UpdateStatus = evt.Message;
            });
        }

        public bool CanRunCapacity => CapacityTasks.Count > 0 && !IsCapacityBusy;

        /// <summary>Unattended install only makes sense for the unattended mode.</summary>
        public bool CanChangeUpdatePolicy => !UpdateSettings.IsControlledOnlineBeta;
        partial void OnIsCapacityBusyChanged(bool value) => OnPropertyChanged(nameof(CanRunCapacity));

        partial void OnCapacityTaskIndexChanged(int value) => OnPropertyChanged(nameof(CanRunCapacity));

        public SettingsViewModel(AppSettings settings, NasConnectionService connectionService, UpdateService updates,
            UpdateScheduler? scheduler = null)
        {
            _settings = settings;
            _connectionService = connectionService;
            _updates = updates;
            _scheduler = scheduler;
            if (_scheduler != null)
            {
                _scheduler.Changed += OnSchedulerChanged;
            }

            _startupLauncher = settings.InstalledStartupLauncher(AppContext.BaseDirectory);
            try { _startWithWindows = _startupLauncher != null && AppSettings.IsStartupRegistered(_startupLauncher); }
            catch (Exception ex) { App.LogCrashException(ex, "Settings.ReadStartup"); }
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
            if (Version.TryParse(update.StagedVersion, out var staged) && staged > UpdateService.CurrentVersion)
            {
                ReadyVersion = update.StagedVersion!;
                UpdateButtonLabel = "可更新";
            }
            _currentVersionText = $"当前版本 {ProductVersion}";
            _updateFeedUrl = update.ManifestUrl;
            _updateProxyUrl = update.ProxyUrl ?? "";
            _connectTimeoutSeconds = Math.Clamp(update.ConnectTimeoutSeconds, 2, 120);
            _checkTimeoutSeconds = Math.Clamp(update.CheckTimeoutSeconds, 5, 300);
            _downloadTimeoutMinutes = Math.Clamp(update.DownloadTimeoutMinutes, 1, 480);
            _updateChannelIndex = string.Equals(update.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            // Pure visual acceptance only: show a SIMULATED update state so the settings page
            // screenshots cover the "new version ready" card without any network access. The
            // simulated state is clearly labelled and never triggers a real check or download.
            if (AppServices.IsUiVisualAcceptance)
            {
                UpdateStatus = "纯视觉验收模拟：发现新版本 9.9.9，已下载并校验（模拟数据，不联网，不会安装）";
            }
            _updateSupportMessage = _updates.SupportMessage;
            _canInstallUpdate = _updates.CanInstall;
            _lastCheckText = update.LastCheckUtc is { } last
                ? $"上次检查：{last.ToLocalTime():yyyy-MM-dd HH:mm}　结果：{update.LastResult}"
                : "尚未检查更新";
            if (!AppServices.IsUiVisualAcceptance) _updateStatus = update.LastResult;
            else UpdateFeedUrl = "https://update.example.internal/fnsync/releases/windows-x64/manifest.json";

        }

        public void RefreshCapacityTasks()
        {
            CapacityTaskIndex = -1;
            CapacityTasks.Clear();
            foreach (var task in _settings.Tasks)
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
                provider = await _connectionService.GetTaskStorageAsync(task, default) ?? throw new IOException("请先连接创建任务的账号。");
                var report = await _capacity.AnalyzeAsync(provider, TaskManager.BackupTarget(task)).ConfigureAwait(true);
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
                provider = await _connectionService.GetTaskStorageAsync(task, default) ?? throw new IOException("请先连接创建任务的账号。");
                var preview = await _capacity.PreviewRetentionAsync(provider, TaskManager.BackupTarget(task), policy).ConfigureAwait(true);
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
            if (!SaveUpdateSettings()) { StatusNotice = UpdateStatus; return; }
            StatusNotice = CanConfigureStartup ? "设置已保存成功。" : "设置已保存；此试用或测试配置暂不支持开机自动启动。";
        }

        [RelayCommand]
        public async Task DownloadUpdateAsync()
        {
            if (!_updates.CanCheck) { UpdateStatus = _updates.SupportMessage; return; }
            await PrepareUpdateAsync(null);
        }

        private static string NoUpdateMessage(UpdateCheckOutcome result) =>
            result.Error.Length > 0 ? result.Error : result.Notes.Length > 0 ? result.Notes : "没有可用更新。";

        [RelayCommand]
        private void CancelUpdate() { if (CanCancelUpdate) _updateCts?.Cancel(); }

        [RelayCommand]
        private void CopyUpdateSource()
        {
            if (string.IsNullOrWhiteSpace(UpdateFeedUrl)) return;
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(UpdateFeedUrl);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            UpdateStatus = "已复制更新源地址。";
        }

        /// <summary>
        /// Installs the newest downloaded package. The running program must exit before its files can
        /// be replaced, so the updater is started first with our PID and it waits for a clean exit.
        /// A running backup is never killed: if it cannot finish in time the install is deferred.
        /// </summary>
        public async Task PrepareUpdateAsync(Func<string, string, Task<bool>>? confirmInstall)
        {
            if (IsUpdateBusy) return;
            IsUpdateBusy = true;
            using var cancellation = new CancellationTokenSource();
            _updateCts = cancellation;
            CanCancelUpdate = true;
            var token = cancellation.Token;
            UpdateButtonLabel = "检查中";
            ReadyVersion = "";
            UpdateStatus = "正在获取并验证更新清单…";
            try
            {
                var check = await _updates.CheckAsync(token);
                if (!check.HasUpdate)
                {
                    UpdateStatus = NoUpdateMessage(check);
                    return;
                }
                var release = check.Release!;
                UpdateButtonLabel = "下载中";
                UpdateStatus = $"正在检查本地更新包 v{release.Version}…";
                DownloadProgress = 0;
                ShowDownloadProgress = true;
                var elapsed = Stopwatch.StartNew();
                var lastUpdate = TimeSpan.Zero;
                bool downloading = true;
                var progress = new Progress<long>(bytes =>
                {
                    if (!downloading || bytes < release.Size && elapsed.Elapsed - lastUpdate < TimeSpan.FromMilliseconds(200)) return;
                    lastUpdate = elapsed.Elapsed;
                    DownloadProgress = 100.0 * bytes / release.Size;
                    double rate = bytes / Math.Max(0.1, elapsed.Elapsed.TotalSeconds) / 1024;
                    UpdateStatus = bytes >= release.Size ? "下载完成，正在校验更新包…" :
                        $"正在下载 v{release.Version}　{DownloadProgress:F0}%　{bytes / 1048576.0:F1}/{release.Size / 1048576.0:F1} MiB　{rate:F0} KiB/s";
                });
                var (download, verified) = await _updates.DownloadAsync(release, progress, token);
                downloading = false;
                ShowDownloadProgress = false;
                if (!download.Ok || verified == null) { UpdateStatus = download.Error; return; }
                UpdateStatus = download.Reused ? "本地更新包已重新校验通过。" : "更新包已下载并通过校验。";
                ReadyVersion = release.Version;
                UpdateStatus = $"v{release.Version} 已下载并通过校验。{check.Notes}";
                if (confirmInstall == null || !_updates.CanInstall) return;
                if (!await confirmInstall(release.Version, check.Notes)) return;

                // The session either owns the maintenance lease or has already released it; there is
                // no path here that leaves backups paused without an install in progress.
                UpdateButtonLabel = "等待安装";
                UpdateStatus = "正在等待已排队的备份和正在进行的恢复完成…";
                using var session = await _updates.BeginInstallAsync(verified, TimeSpan.FromHours(24), token)
                    .ConfigureAwait(true);
                if (!session.Plan.Ready)
                {
                    UpdateStatus = session.Readiness.Reason;
                    return;
                }
                token.ThrowIfCancellationRequested();
                CanCancelUpdate = false;
                UpdateStatus = "正在启动更新器，程序即将退出并在更新后自动重启…";
                if (!_updates.TryStartUpdater(session, TimeSpan.FromMinutes(2), out string startError))
                {
                    UpdateStatus = startError;
                    return;
                }

                // Same exit path as the unattended scheduler: the coordinator marshals to the UI
                // thread and runs the safe-exit sequence (stop tasks, flush logs, real close —
                // never a hide-to-tray). The session keeps its lease; the watchdog guards it.
                var shutdown = AppServices.GetService<FNSyncPlus.Core.Updates.IApplicationShutdownCoordinator>();
                if (!shutdown.RequestShutdown("正在安装更新，程序将自动退出并在更新完成后重启…"))
                {
                    // Delivering the request failed. The updater will wait for its timeout and
                    // give up; the handoff watchdog confirms that and resumes backups. Tell the
                    // user instead of pretending the install is proceeding.
                    UpdateStatus = "已启动更新器，但无法请求程序退出；稍后更新器将放弃本次安装并恢复备份。";
                }
                else
                {
                    session.MarkShutdownRequested();
                }
            }
            catch (OperationCanceledException) { UpdateStatus = "更新操作已取消。"; }
            catch (Exception ex)
            {
                App.LogCrashException(ex, "Settings.PrepareUpdate");
                UpdateStatus = "更新操作失败，请查看更新日志后重试。";
            }
            finally
            {
                IsUpdateBusy = false;
                CanCancelUpdate = false;
                ShowDownloadProgress = false;
                _updateCts = null;
                UpdateButtonLabel = ReadyVersion.Length == 0 ? "更新" : "可更新";
            }
        }

        private bool SaveUpdateSettings()
        {
            if (!UpdateSettings.IsValidProxyUrl(UpdateProxyUrl.Trim()))
            {
                UpdateStatus = "请输入有效的 HTTP 或 HTTPS 代理地址，地址中不能包含登录信息。留空使用系统代理。";
                return false;
            }
            var update = _updates.Settings;
            update.ManifestUrl = UpdateFeedUrl.Trim();
            update.ProxyUrl = UpdateProxyUrl.Trim();
            update.ConnectTimeoutSeconds = double.IsFinite(ConnectTimeoutSeconds) ? (int)ConnectTimeoutSeconds : 15;
            update.CheckTimeoutSeconds = double.IsFinite(CheckTimeoutSeconds) ? (int)CheckTimeoutSeconds : 30;
            update.DownloadTimeoutMinutes = double.IsFinite(DownloadTimeoutMinutes) ? (int)DownloadTimeoutMinutes : 120;
            if (CanChangeUpdatePolicy)
            {
                update.Channel = UpdateChannelIndex == 1 ? "beta" : "stable";
            }
            _updates.Save();
            return true;
        }

        [RelayCommand]
        public async System.Threading.Tasks.Task ClearCredentials()
        {
            var result = await _connectionService.ForgetSavedCredentialsAsync();
            StatusNotice = result.Message;
        }
    }
}
