using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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
        private readonly SyncEngine _syncEngine;
        private readonly RestoreEngine _restoreEngine;

        private WinForms.NotifyIcon? _notifyIcon;
        private readonly DispatcherTimer _syncTimer;
        private bool _isExplicitExit = false;

        // NAS 文件夹浏览器状态
        private string _nasBrowserCurrentUnc = "";
        private string _nasBrowserBaseShare = "";
        private readonly Stack<string> _nasBrowserHistory = new();

        public MainWindow()
        {
            InitializeComponent();

            _settings = AppSettings.Load();
            _longNameEngine = new LongNameEngine(maxUtf8Bytes: 255);
            _manifestManager = new ManifestManager();
            _smbService = new SmbAuthService();
            _discoveryService = new NasDiscoveryService();
            _syncEngine = new SyncEngine(_longNameEngine, _manifestManager, _smbService);
            _restoreEngine = new RestoreEngine(_manifestManager);

            // 增量同步引擎事件挂钩
            _syncEngine.ProgressChanged += OnSyncProgressChanged;
            _syncEngine.LogEmitted += OnLogEmitted;
            _syncEngine.StateChanged += OnSyncStateChanged;
            _syncEngine.RequestWatcherTriggeredSync += OnWatcherTriggeredSync;

            _restoreEngine.ProgressChanged += OnRestoreProgressChanged;
            _restoreEngine.LogEmitted += OnLogEmitted;

            // 定时同步器
            _syncTimer = new DispatcherTimer();
            _syncTimer.Tick += OnSyncTimerTick;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeTrayIcon();
            LoadUiFromSettings();

            // 自动加载 DPAPI 加密凭据
            var creds = SmbAuthService.LoadCredentials();
            if (creds.HasValue)
            {
                TxtNasAddress.Text = creds.Value.Server;
                TxtNasUsername.Text = creds.Value.Username;
                TxtNasPassword.Password = creds.Value.Password;
                AppendLog("INFO", $"已安全加载加密凭据: {creds.Value.Username}@{creds.Value.Server}");
            }

            // 启动异步局域网 NAS 发现
            _ = RefreshDiscoveryAsync();

            // 如果配置了自动同步，启动定时器和文件监控
            ApplyAutoSyncSettings();

            // 如果有凭据且指定了 NAS，尝试自动建立后台验证
            if (!string.IsNullOrEmpty(TxtNasAddress.Text) && !string.IsNullOrEmpty(TxtNasUsername.Text) && !string.IsNullOrEmpty(TxtNasPassword.Password))
            {
                _ = AutoConnectOnStartupAsync();
            }
        }

        private void InitializeTrayIcon()
        {
            _notifyIcon = new WinForms.NotifyIcon
            {
                Text = "飞牛同步增强版 (FN Sync Plus)",
                Visible = true,
                Icon = System.Drawing.SystemIcons.Application
            };

            var contextMenu = new WinForms.ContextMenuStrip();
            contextMenu.Items.Add("打开主界面", null, (s, e) => ShowWindowFromTray());
            contextMenu.Items.Add("立即执行同步", null, (s, e) => Dispatcher.InvokeAsync(() => BtnRunSyncNow_Click(this, new RoutedEventArgs())));
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
            WpfApplication.Current.Shutdown();
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExplicitExit && ChkMinimizeToTray.IsChecked == true)
            {
                e.Cancel = true;
                Hide();
                _notifyIcon?.ShowBalloonTip(1500, "飞牛同步增强版", "程序已最小化到系统托盘，后台持续保持自动增量同步。", WinForms.ToolTipIcon.Info);
            }
            else
            {
                _syncEngine.PauseOrCancel();
                _syncEngine.StopWatcher();
                _notifyIcon?.Dispose();
            }
        }

        #region Navigation Handling

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (TabDeviceView == null) return;

            TabDeviceView.Visibility = Visibility.Collapsed;
            TabTaskView.Visibility = Visibility.Collapsed;
            TabMonitorView.Visibility = Visibility.Collapsed;
            TabRestoreView.Visibility = Visibility.Collapsed;
            TabSettingsView.Visibility = Visibility.Collapsed;

            if (NavDevice.IsChecked == true) TabDeviceView.Visibility = Visibility.Visible;
            else if (NavTask.IsChecked == true) TabTaskView.Visibility = Visibility.Visible;
            else if (NavMonitor.IsChecked == true) TabMonitorView.Visibility = Visibility.Visible;
            else if (NavRestore.IsChecked == true) TabRestoreView.Visibility = Visibility.Visible;
            else if (NavSettings.IsChecked == true) TabSettingsView.Visibility = Visibility.Visible;
        }

        #endregion

        #region TAB 0: 设备连接与认证

        private async Task AutoConnectOnStartupAsync()
        {
            string server = TxtNasAddress.Text.Trim();
            string user = TxtNasUsername.Text.Trim();
            string pwd = TxtNasPassword.Password;

            AppendLog("INFO", $"正在后台自动验证 NAS 连接: {server} ...");
            var (ok, msg) = await _smbService.ConnectNasAsync(server, user, pwd);
            if (ok)
            {
                SetStatusConnected(server, user);
                AppendLog("INFO", $"NAS 连接验证成功: {server}");
            }
            else
            {
                SetStatusDisconnected($"连接未就绪: {msg}");
                AppendLog("WARN", $"NAS 自动连接提示: {msg}");
            }
        }

        private async void BtnConnectNas_Click(object sender, RoutedEventArgs e)
        {
            string server = TxtNasAddress.Text.Trim();
            string user = TxtNasUsername.Text.Trim();
            string pwd = TxtNasPassword.Password;

            if (string.IsNullOrEmpty(server))
            {
                ShowLoginResult(false, "请输入 NAS 的 IP 地址或主机名");
                return;
            }

            BtnConnectNas.IsEnabled = false;
            BtnConnectNas.Content = "⏳ 正在连接认证...";
            ShowLoginResult(true, "正在连接 NAS SMB 服务并进行身份验证，请稍候...", isPending: true);

            try
            {
                var (success, message) = await _smbService.ConnectNasAsync(server, user, pwd);
                if (success)
                {
                    ShowLoginResult(true, $"✅ {message}！已成功建立安全认证会话。您现在可以前往【同步任务】自由浏览并选择 NAS 文件夹。");
                    SetStatusConnected(server, user);

                    if (ChkRememberPassword.IsChecked == true)
                    {
                        SmbAuthService.SaveCredentials(server, user, pwd);
                    }
                    _settings.NasServer = server;
                    _settings.Username = user;
                    _settings.Save();
                }
                else
                {
                    ShowLoginResult(false, $"❌ 登录失败: {message}");
                    SetStatusDisconnected("登录失败");
                }
            }
            finally
            {
                BtnConnectNas.IsEnabled = true;
                BtnConnectNas.Content = "🔐 登录并连接 NAS";
            }
        }

        private void BtnDisconnectNas_Click(object sender, RoutedEventArgs e)
        {
            SetStatusDisconnected("未连接");
            ShowLoginResult(true, "已断开本地 NAS 会话连接。");
        }

        private async void BtnRefreshDiscovery_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDiscoveryAsync();
        }

        private async Task RefreshDiscoveryAsync()
        {
            TxtDiscoveryHint.Text = "正在扫描局域网中的 NAS 设备...";
            try
            {
                string known = TxtNasAddress.Text.Trim();
                var devices = await _discoveryService.DiscoverNasDevicesAsync(known);
                ListDiscoveredDevices.ItemsSource = devices;
                TxtDiscoveryHint.Text = $"扫描完成，共发现 {devices.Count} 台局域网设备。点击可快速填入。";
            }
            catch (Exception ex)
            {
                TxtDiscoveryHint.Text = $"扫描出现错误: {ex.Message}";
            }
        }

        private void ListDiscoveredDevices_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListDiscoveredDevices.SelectedItem is DiscoveredDevice dev)
            {
                TxtNasAddress.Text = dev.IpAddress;
                AppendLog("INFO", $"已选择发现的设备: {dev.Name} ({dev.IpAddress})");
            }
        }

        private void ShowLoginResult(bool success, string message, bool isPending = false)
        {
            BorderLoginResult.Visibility = Visibility.Visible;
            if (isPending)
            {
                BorderLoginResult.Background = new SolidColorBrush(WpfColor.FromRgb(241, 245, 249));
                BorderLoginResult.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(203, 213, 225));
                TxtLoginResult.Foreground = new SolidColorBrush(WpfColor.FromRgb(15, 23, 42));
            }
            else if (success)
            {
                BorderLoginResult.Background = new SolidColorBrush(WpfColor.FromRgb(240, 253, 244));
                BorderLoginResult.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(187, 247, 208));
                TxtLoginResult.Foreground = new SolidColorBrush(WpfColor.FromRgb(22, 101, 52));
            }
            else
            {
                BorderLoginResult.Background = new SolidColorBrush(WpfColor.FromRgb(254, 242, 242));
                BorderLoginResult.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(254, 202, 202));
                TxtLoginResult.Foreground = new SolidColorBrush(WpfColor.FromRgb(153, 27, 27));
            }
            TxtLoginResult.Text = message;
        }

        private void SetStatusConnected(string server, string user)
        {
            StatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(16, 185, 129)); // Green
            StatusText.Text = "NAS 已连接";
            StatusSubText.Text = $"{user} @ {server}";
        }

        private void SetStatusDisconnected(string reason)
        {
            StatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(245, 158, 11)); // Amber
            StatusText.Text = "NAS 未连接";
            StatusSubText.Text = reason;
        }

        #endregion

        #region TAB 1: 任务配置与 NAS 文件夹浏览器

        private void BtnBrowseLocal_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "选择要备份的电脑本地文件夹",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(TxtSourceFolder.Text) ? TxtSourceFolder.Text : @"D:\"
            };
            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                TxtSourceFolder.Text = dialog.SelectedPath;
            }
        }

        private async void BtnOpenNasBrowser_Click(object sender, RoutedEventArgs e)
        {
            string server = TxtNasAddress.Text.Trim();
            if (string.IsNullOrEmpty(server))
            {
                WpfMessageBox.Show("请先在【设备连接】中输入并登录 NAS", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                NavDevice.IsChecked = true;
                return;
            }

            ModalNasBrowser.Visibility = Visibility.Visible;
            _nasBrowserHistory.Clear();
            await LoadNasSharesIntoBrowserAsync(server);
        }

        private void BtnCloseNasBrowser_Click(object sender, RoutedEventArgs e)
        {
            ModalNasBrowser.Visibility = Visibility.Collapsed;
        }

        private async Task LoadNasSharesIntoBrowserAsync(string server)
        {
            _nasBrowserCurrentUnc = $@"\\{server}";
            _nasBrowserBaseShare = "";
            TxtNasBrowserCurrentPath.Text = _nasBrowserCurrentUnc;
            TxtNasBrowserSelectionHint.Text = "正在获取 NAS 共享列表...";

            try
            {
                var shares = await _smbService.EnumerateSharesAsync(server);
                var items = shares.Select(s => new NasFolderItem
                {
                    Name = s.Name,
                    FullUncPath = s.UncPath,
                    RelativeSubPath = ""
                }).ToList();

                ListNasBrowserItems.ItemsSource = items;
                TxtNasBrowserSelectionHint.Text = $"找到 {items.Count} 个可用共享。双击共享名进入浏览。";
            }
            catch (Exception ex)
            {
                TxtNasBrowserSelectionHint.Text = $"获取共享失败: {ex.Message}";
            }
        }

        private async void ListNasBrowserItems_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ListNasBrowserItems.SelectedItem is NasFolderItem selected)
            {
                _nasBrowserHistory.Push(_nasBrowserCurrentUnc);
                if (string.IsNullOrEmpty(_nasBrowserBaseShare))
                {
                    _nasBrowserBaseShare = selected.FullUncPath;
                }
                _nasBrowserCurrentUnc = selected.FullUncPath;
                TxtNasBrowserCurrentPath.Text = _nasBrowserCurrentUnc;
                await RefreshNasBrowserFolderAsync();
            }
        }

        private async void BtnNasBrowserUp_Click(object sender, RoutedEventArgs e)
        {
            if (_nasBrowserHistory.Count > 0)
            {
                _nasBrowserCurrentUnc = _nasBrowserHistory.Pop();
                if (_nasBrowserCurrentUnc.Count(c => c == '\\') <= 2)
                {
                    // 返回到了服务器根层级
                    await LoadNasSharesIntoBrowserAsync(TxtNasAddress.Text.Trim());
                    return;
                }
                TxtNasBrowserCurrentPath.Text = _nasBrowserCurrentUnc;
                await RefreshNasBrowserFolderAsync();
            }
            else
            {
                await LoadNasSharesIntoBrowserAsync(TxtNasAddress.Text.Trim());
            }
        }

        private async Task RefreshNasBrowserFolderAsync()
        {
            TxtNasBrowserSelectionHint.Text = "正在读取文件夹列表...";
            try
            {
                var subFolders = await _smbService.ListSubFoldersAsync(_nasBrowserCurrentUnc, _nasBrowserBaseShare);
                ListNasBrowserItems.ItemsSource = subFolders;
                TxtNasBrowserSelectionHint.Text = $"当前目录有 {subFolders.Count} 个子文件夹。可双击进入，或点击【选择当前文件夹】。";
            }
            catch (Exception ex)
            {
                TxtNasBrowserSelectionHint.Text = $"读取目录失败: {ex.Message}";
            }
        }

        private async void BtnNasBrowserNewFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_nasBrowserBaseShare))
            {
                WpfMessageBox.Show("请先双击进入具体的 NAS 共享目录后再新建子文件夹", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new InputFolderDialog { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                string folderName = dlg.FolderName;
                try
                {
                    var created = await _smbService.CreateSubFolderAsync(_nasBrowserCurrentUnc, folderName, _nasBrowserBaseShare);
                    AppendLog("INFO", $"在 NAS 上新建文件夹成功: {created.FullUncPath}");
                    await RefreshNasBrowserFolderAsync();
                }
                catch (Exception ex)
                {
                    WpfMessageBox.Show($"新建文件夹失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnSelectNasBrowserFolder_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_nasBrowserBaseShare))
            {
                if (ListNasBrowserItems.SelectedItem is NasFolderItem sel)
                {
                    TxtNasTargetUnc.Text = sel.FullUncPath;
                    ModalNasBrowser.Visibility = Visibility.Collapsed;
                    return;
                }
                WpfMessageBox.Show("请至少选择一个具体的 NAS 共享目录作为备份位置", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            TxtNasTargetUnc.Text = _nasBrowserCurrentUnc;
            ModalNasBrowser.Visibility = Visibility.Collapsed;
            AppendLog("INFO", $"已选择 NAS 备份目标目录: {TxtNasTargetUnc.Text}");
        }

        private void BtnSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            _settings.SourceFolder = TxtSourceFolder.Text.Trim();
            _settings.TargetShare = TxtNasTargetUnc.Text.Trim();
            _settings.RealtimeWatchEnabled = ChkRealtimeWatch.IsChecked == true;
            _settings.AutoSyncEnabled = ChkAutoInterval.IsChecked == true;
            _settings.SyncIntervalMinutes = GetIntervalMinutesFromComboBox();
            _settings.StartWithWindows = ChkStartWithWindows.IsChecked == true;
            _settings.MinimizeToTray = ChkMinimizeToTray.IsChecked == true;
            _settings.Save();

            ApplyAutoSyncSettings();
            WpfMessageBox.Show("设置已成功保存！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private int GetIntervalMinutesFromComboBox() => CmbIntervalMinutes.SelectedIndex switch
        {
            0 => 1,
            1 => 5,
            2 => 15,
            3 => 30,
            4 => 60,
            _ => 5
        };

        private void ApplyAutoSyncSettings()
        {
            // 实时监控
            if (_settings.RealtimeWatchEnabled && Directory.Exists(_settings.SourceFolder))
            {
                _syncEngine.StartWatcher(_settings.SourceFolder, _settings.TargetShare);
            }
            else
            {
                _syncEngine.StopWatcher();
            }

            // 定时轮询
            if (_settings.AutoSyncEnabled)
            {
                _syncTimer.Interval = TimeSpan.FromMinutes(_settings.SyncIntervalMinutes);
                _syncTimer.Start();
            }
            else
            {
                _syncTimer.Stop();
            }
        }

        #endregion

        #region TAB 2: 同步监控与运行

        private async void BtnRunSyncNow_Click(object sender, RoutedEventArgs e)
        {
            string src = TxtSourceFolder.Text.Trim();
            string dest = TxtNasTargetUnc.Text.Trim();

            if (!Directory.Exists(src))
            {
                WpfMessageBox.Show("本地源文件夹不存在，请检查配置", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                NavTask.IsChecked = true;
                return;
            }
            if (string.IsNullOrWhiteSpace(dest))
            {
                WpfMessageBox.Show("请先选择 NAS 备份目标目录", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                NavTask.IsChecked = true;
                return;
            }

            // 切换到监控视图
            NavMonitor.IsChecked = true;
            BtnPauseSync.Content = "⏸ 暂停/取消";

            try
            {
                await _syncEngine.RunSyncAsync(src, dest);
                ListFailedItems.ItemsSource = null;
                ListFailedItems.ItemsSource = _syncEngine.FailedItems;
            }
            catch (Exception ex)
            {
                AppendLog("ERROR", $"同步过程异常: {ex.Message}");
            }
        }

        private void BtnPauseSync_Click(object sender, RoutedEventArgs e)
        {
            _syncEngine.PauseOrCancel();
            BtnPauseSync.Content = "已暂停";
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtRealtimeLog.Clear();
        }

        private void OnSyncProgressChanged(SyncProgressReport rep)
        {
            Dispatcher.InvokeAsync(() =>
            {
                TxtMetricTotal.Text = rep.TotalFiles.ToString();
                TxtMetricUploaded.Text = rep.UploadedFiles.ToString();
                TxtMetricUnchanged.Text = rep.UnchangedFiles.ToString();
                TxtMetricLongNames.Text = rep.LongNameFiles.ToString();
                TxtMetricFailed.Text = rep.FailedFiles.ToString();

                TxtCurrentSyncStatus.Text = $"状态: {rep.State} ({rep.ScannedFiles}/{rep.TotalFiles})";
                TxtCurrentSyncFile.Text = string.IsNullOrEmpty(rep.CurrentFile) ? "就绪" : $"当前: {rep.CurrentFile}";

                if (rep.TotalFiles > 0)
                {
                    ProgressSync.Value = (double)rep.ScannedFiles / rep.TotalFiles * 100.0;
                }

                if (rep.SpeedBytesPerSec > 0)
                {
                    double mbPerSec = rep.SpeedBytesPerSec / (1024.0 * 1024.0);
                    TxtTransferSpeed.Text = $"{mbPerSec:F2} MB/s";
                }
                else
                {
                    TxtTransferSpeed.Text = "0 KB/s";
                }
            });
        }

        private void OnSyncStateChanged(SyncState state, string desc)
        {
            Dispatcher.InvokeAsync(() =>
            {
                TxtCurrentSyncStatus.Text = $"状态: {desc}";
            });
        }

        private void OnLogEmitted(string level, string msg)
        {
            Dispatcher.InvokeAsync(() =>
            {
                AppendLog(level, msg);
            });
        }

        private void AppendLog(string level, string msg)
        {
            if (TxtRealtimeLog.Text.Length > 200000)
            {
                TxtRealtimeLog.Text = TxtRealtimeLog.Text.Substring(50000);
            }
            TxtRealtimeLog.AppendText(msg + "\r\n");
            TxtRealtimeLog.ScrollToEnd();
        }

        private void OnWatcherTriggeredSync()
        {
            Dispatcher.InvokeAsync(() =>
            {
                AppendLog("INFO", "[文件变动实时检测] 正在自动触发增量同步...");
                BtnRunSyncNow_Click(this, new RoutedEventArgs());
            });
        }

        private void OnSyncTimerTick(object? sender, EventArgs e)
        {
            if (_syncEngine.CurrentState == SyncState.Idle || _syncEngine.CurrentState == SyncState.Completed)
            {
                AppendLog("INFO", "[定时自动轮询] 触发例行增量备份检查...");
                BtnRunSyncNow_Click(this, new RoutedEventArgs());
            }
        }

        #endregion

        #region TAB 3: 恢复中心 (异机恢复)

        private void BtnBrowseRestoreLocal_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "选择用于恢复文件的本地空文件夹",
                UseDescriptionForTitle = true
            };
            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                TxtRestoreLocalDest.Text = dialog.SelectedPath;
            }
        }

        private async void BtnPreviewRestore_Click(object sender, RoutedEventArgs e)
        {
            string nasPath = TxtRestoreNasPath.Text.Trim();
            if (string.IsNullOrEmpty(nasPath) || !Directory.Exists(nasPath))
            {
                WpfMessageBox.Show("请指定有效的 NAS 备份目录（如 \\\\192.168.8.120\\FN_Sync_Test）", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                TxtRestoreStatus.Text = "正在读取 NAS 备份元数据清单...";
                var items = await _restoreEngine.PreviewBackupAsync(nasPath);
                GridRestorePreview.ItemsSource = items;
                TxtRestoreStatus.Text = $"清单读取成功！共包含 {items.Count} 个文件记录，其中 {items.Count(i => i.IsLongName)} 个超长文件名映射。点击【开始一键完整恢复】可无损还原。";
            }
            catch (Exception ex)
            {
                TxtRestoreStatus.Text = $"读取清单失败: {ex.Message}";
                WpfMessageBox.Show($"读取备份元数据出现错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnStartRestore_Click(object sender, RoutedEventArgs e)
        {
            string nas = TxtRestoreNasPath.Text.Trim();
            string local = TxtRestoreLocalDest.Text.Trim();

            if (!Directory.Exists(nas))
            {
                WpfMessageBox.Show("NAS 备份目录无法访问", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(local))
            {
                WpfMessageBox.Show("请选择本地恢复目标文件夹", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var policy = CmbConflictPolicy.SelectedIndex switch
            {
                1 => ConflictResolution.Overwrite,
                2 => ConflictResolution.RenameWithTimestamp,
                _ => ConflictResolution.SkipExisting
            };

            BtnStartRestore.IsEnabled = false;
            BtnStartRestore.Content = "⏳ 正在恢复中...";

            try
            {
                var report = await _restoreEngine.RunRestoreAsync(nas, local, policy);
                WpfMessageBox.Show(report.StatusMessage, "恢复完成报告", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"恢复操作中断: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnStartRestore.IsEnabled = true;
                BtnStartRestore.Content = "🚀 开始一键完整恢复";
            }
        }

        private void OnRestoreProgressChanged(RestoreProgressReport rep)
        {
            Dispatcher.InvokeAsync(() =>
            {
                TxtRestoreStatus.Text = $"正在恢复 ({rep.RestoredFiles}/{rep.TotalFiles}): {rep.CurrentFile}";
                if (rep.TotalFiles > 0)
                {
                    ProgressRestore.Value = (double)rep.RestoredFiles / rep.TotalFiles * 100.0;
                }
            });
        }

        #endregion

        #region TAB 4: 设置与重试

        private void BtnRetryFailed_Click(object sender, RoutedEventArgs e)
        {
            if (_syncEngine.FailedItems.Count == 0)
            {
                WpfMessageBox.Show("当前没有失败的文件项目", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AppendLog("INFO", "正在重试失败的文件项目...");
            BtnRunSyncNow_Click(this, new RoutedEventArgs());
        }

        private void BtnClearCredentials_Click(object sender, RoutedEventArgs e)
        {
            if (WpfMessageBox.Show("确认清除本机保存的 NAS 密码凭据吗？", "确认", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                SmbAuthService.ClearSavedCredentials();
                TxtNasPassword.Clear();
                WpfMessageBox.Show("已成功清除本机保存的凭据", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnExportLog_Click(object sender, RoutedEventArgs e)
        {
            using var sfd = new WinForms.SaveFileDialog
            {
                Filter = "文本日志文件 (*.txt)|*.txt",
                FileName = $"FN_Sync_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };
            if (sfd.ShowDialog() == WinForms.DialogResult.OK)
            {
                File.WriteAllText(sfd.FileName, TxtRealtimeLog.Text, System.Text.Encoding.UTF8);
                WpfMessageBox.Show("日志导出成功！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void LoadUiFromSettings()
        {
            TxtNasAddress.Text = _settings.NasServer;
            TxtNasUsername.Text = _settings.Username;
            ChkRememberPassword.IsChecked = _settings.RememberCredentials;
            TxtSourceFolder.Text = _settings.SourceFolder;
            TxtNasTargetUnc.Text = _settings.TargetShare;
            TxtRestoreNasPath.Text = _settings.TargetShare;

            ChkRealtimeWatch.IsChecked = _settings.RealtimeWatchEnabled;
            ChkAutoInterval.IsChecked = _settings.AutoSyncEnabled;
            ChkStartWithWindows.IsChecked = _settings.StartWithWindows;
            ChkMinimizeToTray.IsChecked = _settings.MinimizeToTray;

            CmbIntervalMinutes.SelectedIndex = _settings.SyncIntervalMinutes switch
            {
                1 => 0,
                5 => 1,
                15 => 2,
                30 => 3,
                60 => 4,
                _ => 1
            };
        }

        #endregion
    }
}
