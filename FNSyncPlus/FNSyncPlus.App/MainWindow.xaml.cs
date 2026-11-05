using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Utils;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;
using WpfApplication = System.Windows.Application;
using WpfColor = System.Windows.Media.Color;

namespace FNSyncPlus.App
{
    public partial class MainWindow : Window
    {
        private readonly AppSettings _settings;
        private readonly LongNameEngine _longNameEngine;
        private readonly ManifestManager _manifestManager;
        private readonly SmbAuthService _smbService;
        private readonly NasDiscoveryService _discoveryService;
        private readonly TaskManager _taskManager;
        private readonly RestoreEngine _restoreEngine;

        private WinForms.NotifyIcon? _notifyIcon;
        private bool _isExplicitExit = false;

        private List<DiscoveredBackupPackage> _discoveredPackages = new();

        public MainWindow()
        {
            InitializeComponent();

            _settings = AppSettings.Load();
            _longNameEngine = new LongNameEngine(maxUtf8Bytes: 255);
            _manifestManager = new ManifestManager();
            _smbService = new SmbAuthService();
            _discoveryService = new NasDiscoveryService();
            _restoreEngine = new RestoreEngine(_manifestManager);

            // 初始化多任务协调器
            _taskManager = new TaskManager(_settings, _longNameEngine, _manifestManager, _smbService);

            _taskManager.TaskListChanged += () => Dispatcher.InvokeAsync(RefreshTasksUi);
            _taskManager.TaskStateChanged += _ => Dispatcher.InvokeAsync(RefreshTasksUi);
            _taskManager.TaskProgressChanged += (_, _) => Dispatcher.InvokeAsync(RefreshTasksUi);

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeTrayIcon();
            LoadUiFromSettings();
            RefreshTasksUi();

            // 自动加载 DPAPI 本机加密凭据
            var creds = SmbAuthService.LoadCredentials();
            if (creds.HasValue)
            {
                TxtDeviceAddress.Text = creds.Value.Server;
                TxtDeviceUsername.Text = creds.Value.Username;
                TxtDevicePassword.Password = creds.Value.Password;
            }

            // 启动异步局域网设备发现
            _ = RefreshLanDevicesAsync();

            // 如果有凭据且指定了 NAS，后台自动尝试验证连接
            if (!string.IsNullOrEmpty(TxtDeviceAddress.Text) && !string.IsNullOrEmpty(TxtDeviceUsername.Text) && !string.IsNullOrEmpty(TxtDevicePassword.Password))
            {
                _ = AutoConnectNasOnStartupAsync();
            }
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new WinForms.NotifyIcon
            {
                Text = "飞牛同步增强版 (FN Sync Plus) v2.1",
                Visible = true,
                Icon = System.Drawing.SystemIcons.Application
            };

            var contextMenu = new WinForms.ContextMenuStrip();
            contextMenu.Items.Add("打开主界面", null, (s, e) => ShowWindowFromTray());
            contextMenu.Items.Add("全部立即备份", null, (s, e) => Dispatcher.InvokeAsync(() => _taskManager.TriggerSyncAll()));
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("完全退出", null, (s, e) => ExitApplication());

            _notifyIcon.ContextMenuStrip = contextMenu;
            _notifyIcon.DoubleClick += (s, e) => ShowWindowFromTray();
        }

        private void ShowWindowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void ExitApplication()
        {
            _isExplicitExit = true;
            _notifyIcon?.Dispose();
            _notifyIcon = null;
            _taskManager.Dispose();
            WpfApplication.Current.Shutdown();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExplicitExit && ChkGlobalMinimizeToTray.IsChecked == true)
            {
                e.Cancel = true;
                Hide();
                _notifyIcon?.ShowBalloonTip(1500, "飞牛同步增强版", "程序已最小化到系统托盘，后台持续守护多任务自动增量同步。", WinForms.ToolTipIcon.Info);
            }
            else
            {
                _taskManager.Dispose();
                _notifyIcon?.Dispose();
            }
        }

        #region Navigation Tabs

        private void TabNav_Checked(object sender, RoutedEventArgs e)
        {
            if (ViewTasks == null) return;

            ViewTasks.Visibility = Visibility.Collapsed;
            ViewRestore.Visibility = Visibility.Collapsed;
            ViewDevice.Visibility = Visibility.Collapsed;
            ViewSettings.Visibility = Visibility.Collapsed;

            TabNavTasks.Foreground = (Brush)FindResource("TextMutedBrush");
            TabNavRestore.Foreground = (Brush)FindResource("TextMutedBrush");
            TabNavDevice.Foreground = (Brush)FindResource("TextMutedBrush");
            TabNavSettings.Foreground = (Brush)FindResource("TextMutedBrush");

            if (TabNavTasks.IsChecked == true)
            {
                ViewTasks.Visibility = Visibility.Visible;
                TabNavTasks.Foreground = (Brush)FindResource("TextMainBrush");
            }
            else if (TabNavRestore.IsChecked == true)
            {
                ViewRestore.Visibility = Visibility.Visible;
                TabNavRestore.Foreground = (Brush)FindResource("TextMainBrush");
                // 默认填充 NAS 备份根目录
                if (string.IsNullOrEmpty(TxtRestoreScanRoot.Text))
                {
                    TxtRestoreScanRoot.Text = _settings.PreferredBackupRoot;
                }
            }
            else if (TabNavDevice.IsChecked == true)
            {
                ViewDevice.Visibility = Visibility.Visible;
                TabNavDevice.Foreground = (Brush)FindResource("TextMainBrush");
            }
            else if (TabNavSettings.IsChecked == true)
            {
                ViewSettings.Visibility = Visibility.Visible;
                TabNavSettings.Foreground = (Brush)FindResource("TextMainBrush");
            }
        }

        private void BorderNasPill_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            TabNavDevice.IsChecked = true;
        }

        #endregion

        #region TAB 1: 任务看板与多任务管理

        private void RefreshTasksUi()
        {
            var tasks = _taskManager.Tasks.ToList();

            TxtStatTotalTasks.Text = tasks.Count.ToString();
            TxtStatSyncingTasks.Text = tasks.Count(t => t.IsRunning).ToString();
            TxtStatIdleTasks.Text = tasks.Count(t => t.State == TaskSyncState.Idle || t.State == TaskSyncState.Completed).ToString();
            TxtStatFailedTasks.Text = tasks.Count(t => t.State == TaskSyncState.Error || t.LastFailedFiles > 0).ToString();

            var lastSync = tasks.Where(t => t.LastSyncTime.HasValue).OrderByDescending(t => t.LastSyncTime).FirstOrDefault();
            TxtStatLastSyncTime.Text = lastSync?.LastSyncTime.HasValue == true
                ? lastSync.LastSyncTime.Value.ToString("HH:mm:ss")
                : "--";

            BorderEmptyTasks.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            ItemsControlTasks.ItemsSource = null;
            ItemsControlTasks.ItemsSource = tasks;
        }

        private void BtnAddFolderWizard_Click(object sender, RoutedEventArgs e)
        {
            var wizard = new AddFolderWizardDialog(_settings, _smbService, _taskManager)
            {
                Owner = this
            };

            if (wizard.ShowDialog() == true)
            {
                RefreshTasksUi();
            }
        }

        private void BtnTaskRunSync_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string taskId)
            {
                _taskManager.TriggerSync(taskId);
            }
        }

        private void BtnTaskPause_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string taskId)
            {
                var task = _taskManager.Tasks.FirstOrDefault(t => t.TaskId == taskId);
                if (task != null)
                {
                    if (task.State == TaskSyncState.Paused)
                    {
                        _taskManager.ResumeTask(taskId);
                    }
                    else
                    {
                        _taskManager.PauseTask(taskId);
                    }
                }
            }
        }

        private void BtnTaskViewLog_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string taskId)
            {
                var task = _taskManager.Tasks.FirstOrDefault(t => t.TaskId == taskId);
                if (task != null)
                {
                    var dlg = new TaskLogDialog(task, _taskManager) { Owner = this };
                    dlg.Show();
                }
            }
        }

        private void BtnTaskDelete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string taskId)
            {
                var task = _taskManager.Tasks.FirstOrDefault(t => t.TaskId == taskId);
                if (task == null) return;

                var confirm = WpfMessageBox.Show(
                    $"确认从软件中移除同步任务【{task.TaskName}】吗？\n\n安全说明：此操作仅移除当前软件的任务调度，绝对不会删除您电脑本地的文件，也绝不会删除 NAS 中已备份的任何数据。",
                    "确认移除任务",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm == MessageBoxResult.Yes)
                {
                    _taskManager.RemoveTask(taskId);
                }
            }
        }

        private void BtnSyncAllTasks_Click(object sender, RoutedEventArgs e)
        {
            _taskManager.TriggerSyncAll();
        }

        private async void BtnPreAnalyzeAll_Click(object sender, RoutedEventArgs e)
        {
            if (_taskManager.Tasks.Count == 0)
            {
                WpfMessageBox.Show("当前没有同步任务，请先添加文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int totalFiles = 0;
            int newFiles = 0;
            int modifiedFiles = 0;
            int unchangedFiles = 0;
            int longNames = 0;
            long totalBytes = 0;

            var syncEngine = new SyncEngine(_longNameEngine, _manifestManager, _smbService);

            foreach (var task in _taskManager.Tasks)
            {
                try
                {
                    if (Directory.Exists(task.SourceFolder))
                    {
                        var res = await syncEngine.AnalyzeSyncAsync(task.SourceFolder, task.DestinationFolder);
                        totalFiles += res.TotalFiles;
                        newFiles += res.NewFiles;
                        modifiedFiles += res.ModifiedFiles;
                        unchangedFiles += res.UnchangedFiles;
                        longNames += res.LongNameFiles;
                        totalBytes += res.TotalSizeBytes;
                    }
                }
                catch { }
            }

            double mb = totalBytes / (1024.0 * 1024.0);
            string sizeStr = mb > 1024 ? $"{mb / 1024.0:F2} GB" : $"{mb:F1} MB";

            string msg = $"【全部任务变动预先分析结果】\n" +
                         $"• 本地受保护总文件数: {totalFiles} 个 ({sizeStr})\n" +
                         $"• 待上传新增文件: {newFiles} 个\n" +
                         $"• 待上传已修改文件: {modifiedFiles} 个\n" +
                         $"• 未修改文件 (将秒级跳过): {unchangedFiles} 个\n" +
                         $"• 触发长文件名兼容映射: {longNames} 个";

            WpfMessageBox.Show(msg, "全局变动预先分析报告", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        #endregion

        #region TAB 2: 多任务异机恢复中心

        private async void BtnScanRestorePackages_Click(object sender, RoutedEventArgs e)
        {
            string rootUnc = TxtRestoreScanRoot.Text.Trim();
            if (string.IsNullOrEmpty(rootUnc) || !Directory.Exists(rootUnc))
            {
                WpfMessageBox.Show("请指定有效的 NAS 备份根目录（如 \\\\192.168.8.120\\私人备份）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            TxtRestoreGlobalStatus.Text = "正在扫描 NAS 根目录下的备份包...";
            try
            {
                _discoveredPackages = await _restoreEngine.DiscoverBackupPackagesAsync(rootUnc);

                CmbDiscoveredPackages.ItemsSource = null;
                CmbDiscoveredPackages.ItemsSource = _discoveredPackages.Select(p => p.PackageName).ToList();

                if (_discoveredPackages.Count > 0)
                {
                    CmbDiscoveredPackages.SelectedIndex = 0;
                    TxtRestoreGlobalStatus.Text = $"扫描完成！发现 {_discoveredPackages.Count} 个可用备份任务包，选择即可直接预览并恢复。";
                }
                else
                {
                    TxtRestoreGlobalStatus.Text = "该目录下未发现包含 .fnsync 元数据清单的备份任务包。";
                }
            }
            catch (Exception ex)
            {
                TxtRestoreGlobalStatus.Text = $"扫描异常: {ex.Message}";
            }
        }

        private async void CmbDiscoveredPackages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbDiscoveredPackages.SelectedIndex >= 0 && CmbDiscoveredPackages.SelectedIndex < _discoveredPackages.Count)
            {
                var pkg = _discoveredPackages[CmbDiscoveredPackages.SelectedIndex];
                TxtDiscoveredPackageDetail.Text = $"{pkg.FileCount} 个文件 | {pkg.FormattedSize} | {pkg.LongNameCount} 个超长名 | 上次备份: {pkg.LastUpdated:yyyy-MM-dd HH:mm}";

                // 自动更新本地还原目标子目录
                TxtRestoreLocalTarget.Text = Path.Combine(@"D:\Restored_Files", pkg.PackageName);

                try
                {
                    var preview = await _restoreEngine.PreviewBackupAsync(pkg.NasFolderUnc);
                    GridRestoreFiles.ItemsSource = preview;
                }
                catch { }
            }
        }

        private void BtnBrowseRestoreLocal_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "选择用于存放恢复文件的本地文件夹",
                UseDescriptionForTitle = true
            };
            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                TxtRestoreLocalTarget.Text = dialog.SelectedPath;
            }
        }

        private async void BtnExecuteRestore_Click(object sender, RoutedEventArgs e)
        {
            if (CmbDiscoveredPackages.SelectedIndex < 0 || CmbDiscoveredPackages.SelectedIndex >= _discoveredPackages.Count)
            {
                WpfMessageBox.Show("请先扫描并选择一个要恢复的备份任务包", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var pkg = _discoveredPackages[CmbDiscoveredPackages.SelectedIndex];
            string localDest = TxtRestoreLocalTarget.Text.Trim();

            if (string.IsNullOrEmpty(localDest))
            {
                WpfMessageBox.Show("请指定本地恢复目标文件夹", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var policy = CmbRestoreConflictPolicy.SelectedIndex switch
            {
                1 => ConflictResolution.Overwrite,
                2 => ConflictResolution.RenameWithTimestamp,
                _ => ConflictResolution.SkipExisting
            };

            BtnExecuteRestore.IsEnabled = false;
            BtnExecuteRestore.Content = "⏳ 正在恢复中...";

            try
            {
                _restoreEngine.ProgressChanged += rep =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        TxtRestoreGlobalStatus.Text = $"正在恢复 ({rep.RestoredFiles}/{rep.TotalFiles}): {rep.CurrentFile}";
                        if (rep.TotalFiles > 0)
                        {
                            ProgressRestoreGlobal.Value = (double)rep.RestoredFiles / rep.TotalFiles * 100.0;
                        }
                    });
                };

                var report = await _restoreEngine.RunRestoreAsync(pkg.NasFolderUnc, localDest, policy);
                WpfMessageBox.Show(report.StatusMessage, "恢复完成报告", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtRestoreGlobalStatus.Text = report.StatusMessage;
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"恢复操作中断: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnExecuteRestore.IsEnabled = true;
                BtnExecuteRestore.Content = "🚀 开始一键完整还原 (带 SHA-256 校验)";
            }
        }

        #endregion

        #region TAB 3: NAS 设备与 SMB 认证

        private async Task AutoConnectNasOnStartupAsync()
        {
            string server = TxtDeviceAddress.Text.Trim();
            string user = TxtDeviceUsername.Text.Trim();
            string pwd = TxtDevicePassword.Password;

            var (ok, msg) = await _smbService.ConnectNasAsync(server, user, pwd);
            if (ok)
            {
                SetNasOnlineState(true, $"{user} @ {server}");
            }
            else
            {
                SetNasOnlineState(false, $"未就绪: {msg}");
            }
        }

        private async void BtnDeviceLogin_Click(object sender, RoutedEventArgs e)
        {
            string server = TxtDeviceAddress.Text.Trim();
            string user = TxtDeviceUsername.Text.Trim();
            string pwd = TxtDevicePassword.Password;

            if (string.IsNullOrEmpty(server))
            {
                ShowDeviceLoginResult(false, "请输入 NAS 的 IP 地址或主机名");
                return;
            }

            BtnDeviceLogin.IsEnabled = false;
            BtnDeviceLogin.Content = "⏳ 正在认证...";

            try
            {
                var (ok, msg) = await _smbService.ConnectNasAsync(server, user, pwd);
                if (ok)
                {
                    ShowDeviceLoginResult(true, $"✅ {msg}！已成功建立安全 SMB 会话。所有同步任务将自动复用该连接。");
                    SetNasOnlineState(true, $"{user} @ {server}");

                    if (ChkDeviceRemember.IsChecked == true)
                    {
                        SmbAuthService.SaveCredentials(server, user, pwd);
                    }
                    _settings.NasServer = server;
                    _settings.Username = user;
                    _settings.Save();
                }
                else
                {
                    ShowDeviceLoginResult(false, $"❌ 登录失败: {msg}");
                    SetNasOnlineState(false, "登录失败");
                }
            }
            finally
            {
                BtnDeviceLogin.IsEnabled = true;
                BtnDeviceLogin.Content = "🔐 登录并建立 SMB 认证";
            }
        }

        private void BtnDeviceDisconnect_Click(object sender, RoutedEventArgs e)
        {
            SetNasOnlineState(false, "已断开连接");
            ShowDeviceLoginResult(true, "已断开本地 NAS 会话连接。");
        }

        private async void BtnRefreshLanDevices_Click(object sender, RoutedEventArgs e)
        {
            await RefreshLanDevicesAsync();
        }

        private async Task RefreshLanDevicesAsync()
        {
            TxtDiscoveryStatus.Text = "正在扫描局域网中的飞牛 NAS 设备...";
            try
            {
                var devices = await _discoveryService.DiscoverNasDevicesAsync(TxtDeviceAddress.Text.Trim());
                ListDiscoveredNas.ItemsSource = devices;
                TxtDiscoveryStatus.Text = $"扫描完成，发现 {devices.Count} 台局域网设备。点击可快速填入。";
            }
            catch (Exception ex)
            {
                TxtDiscoveryStatus.Text = $"扫描异常: {ex.Message}";
            }
        }

        private void ListDiscoveredNas_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListDiscoveredNas.SelectedItem is DiscoveredDevice dev)
            {
                TxtDeviceAddress.Text = dev.IpAddress;
            }
        }

        private void ShowDeviceLoginResult(bool ok, string msg)
        {
            BorderDeviceLoginResult.Visibility = Visibility.Visible;
            if (ok)
            {
                BorderDeviceLoginResult.Background = new SolidColorBrush(Color.FromRgb(240, 253, 244));
                BorderDeviceLoginResult.BorderBrush = new SolidColorBrush(Color.FromRgb(187, 247, 208));
                TxtDeviceLoginResult.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
            }
            else
            {
                BorderDeviceLoginResult.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                BorderDeviceLoginResult.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                TxtDeviceLoginResult.Foreground = new SolidColorBrush(Color.FromRgb(153, 27, 27));
            }
            TxtDeviceLoginResult.Text = msg;
        }

        private void SetNasOnlineState(bool online, string detail)
        {
            if (online)
            {
                DotNasStatus.Fill = (Brush)FindResource("SuccessBrush");
                TxtNasPillText.Text = $"NAS 在线: {detail}";
                _taskManager.SetNasOnlineState(true);
            }
            else
            {
                DotNasStatus.Fill = (Brush)FindResource("WarningBrush");
                TxtNasPillText.Text = $"NAS 未连接: {detail}";
                _taskManager.SetNasOnlineState(false);
            }
        }

        #endregion

        #region TAB 4: 全局设置

        private void BtnClearSavedCredentials_Click(object sender, RoutedEventArgs e)
        {
            if (WpfMessageBox.Show("确认清除本机保存的 NAS 密码凭据吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                SmbAuthService.ClearSavedCredentials();
                TxtDevicePassword.Clear();
                WpfMessageBox.Show("已成功清除本机保存的凭据", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnSaveGlobalSettings_Click(object sender, RoutedEventArgs e)
        {
            _settings.StartWithWindows = ChkGlobalAutoStart.IsChecked == true;
            _settings.MinimizeToTray = ChkGlobalMinimizeToTray.IsChecked == true;
            _settings.MaxConcurrentSyncs = CmbMaxConcurrent.SelectedIndex switch
            {
                0 => 1,
                1 => 2,
                2 => 3,
                3 => 4,
                _ => 2
            };
            _settings.Save();
            WpfMessageBox.Show("全局设置已成功保存！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            bool isDark = _settings.AppTheme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
            _settings.AppTheme = isDark ? "Light" : "Dark";
            _settings.Save();
            ApplyAppTheme(_settings.AppTheme);
        }

        private void ApplyAppTheme(string theme)
        {
            bool isDark = theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
            BtnThemeToggle.Content = isDark ? "🌙" : "☀️";

            if (isDark)
            {
                Resources["AppBgBrush"] = new SolidColorBrush(Color.FromRgb(15, 23, 42));       // #0F172A
                Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromRgb(30, 41, 59));     // #1E293B
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(30, 41, 59));       // #1E293B
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(51, 65, 85));   // #334155
                Resources["TextMainBrush"] = new SolidColorBrush(Color.FromRgb(248, 250, 252));   // #F8FAFC
                Resources["TextMutedBrush"] = new SolidColorBrush(Color.FromRgb(148, 163, 184)); // #94A3B8
            }
            else
            {
                Resources["AppBgBrush"] = new SolidColorBrush(Color.FromRgb(243, 244, 246));     // #F3F4F6
                Resources["HeaderBgBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                Resources["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(255, 255, 255));
                Resources["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(229, 231, 235)); // #E5E7EB
                Resources["TextMainBrush"] = new SolidColorBrush(Color.FromRgb(15, 23, 42));      // #0F172A
                Resources["TextMutedBrush"] = new SolidColorBrush(Color.FromRgb(100, 116, 139));  // #64748B
            }
        }

        private void LoadUiFromSettings()
        {
            ChkGlobalAutoStart.IsChecked = _settings.StartWithWindows;
            ChkGlobalMinimizeToTray.IsChecked = _settings.MinimizeToTray;
            CmbMaxConcurrent.SelectedIndex = _settings.MaxConcurrentSyncs switch
            {
                1 => 0,
                2 => 1,
                3 => 2,
                4 => 3,
                _ => 1
            };
            ApplyAppTheme(_settings.AppTheme);
        }

        #endregion
    }
}
