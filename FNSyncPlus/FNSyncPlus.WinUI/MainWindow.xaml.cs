using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.WinUI.Dialogs;
using FNSyncPlus.WinUI.Helpers;
using FNSyncPlus.WinUI.Pages;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using FNSyncPlus.Tray;

namespace FNSyncPlus.WinUI
{
    public sealed partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }
        public SettingsViewModel Updates { get; }
        private readonly AppSettings _settings;
        private readonly AppWindow _appWindow;
        private readonly LogService _logService;
        private TrayIcon? _trayIcon;
        private bool _shutdownInProgress;
        private bool _shutdownComplete;
        private int _logWarningQueued;

        public MainWindow(string initialView = "Tasks", int wizardStep = 0)
        {
            ViewModel = AppServices.GetService<MainViewModel>();
            Updates = AppServices.GetService<SettingsViewModel>();
            _settings = AppServices.GetService<AppSettings>();
            _logService = AppServices.GetService<LogService>();
            InitializeComponent();

            // The version used to be a hard-coded string, so an upgraded install kept claiming the
            // old version. It now comes from the assembly that is actually running.
            string displayVer = FNSyncPlus.Core.Updates.UpdateService.DisplayVersion;
            string product = $"飞牛同步增强版 v{displayVer}";
            string title = AppServices.IsIsolatedTestSession
                ? (_settings.IsVersionedBetaTestProfile ? product + " [隔离测试会话·新版配置]" : product + " [隔离测试会话]")
                : AppServices.IsVersionedBetaSession ? product : product + " [旧版引擎·仅供诊断]";
            WindowHelper.ConfigureWindow(this, title, 1200, 780);
            AppTitleText.Text = "飞牛同步增强版";
            ToolTipService.SetToolTip(AppTitleText, title);
            AppVersionTag.Text = $"v{displayVer}";
            if (AppServices.IsIsolatedTestSession)
            {
                SessionTagText.Text = _settings.IsVersionedBetaTestProfile ? "隔离测试·新版" : "隔离测试";
                SessionTagPill.Visibility = Visibility.Visible;
            }
            else if (!AppServices.IsVersionedBetaSession)
            {
                SessionTagText.Text = "旧版诊断";
                SessionTagPill.Visibility = Visibility.Visible;
            }
            ViewModel.AppTitle = title;
            _logService.TaskLogWriteFailed += OnTaskLogWriteFailed;
            _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowHelper.GetHandle(this)));
            _appWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
            InitializeTrayIcon();
            _appWindow.Closing += OnAppWindowClosing;
            Closed += OnWindowClosed;
            SizeChanged += (_, _) => UpdateTitleBarPadding();
            WindowHelper.SetMicaBackdrop(this);
            SetTitleBar(AppTitleBarDragArea);
            UpdateTitleBarPadding();
            ApplyStoredTheme();

            ViewModel.RequestNavigate += NavigateToTag;
            ViewModel.RequestToggleTheme += ToggleAppTheme;
            Updates.ThemeChangedRequested += ApplyThemeExplicit;
            ViewModel.PropertyChanged += (s, e) =>
            {
                Helpers.UIThreadHelper.RunOnUIThread(() =>
                {
                    if (e.PropertyName == nameof(MainViewModel.IsNasConnected) ||
                        e.PropertyName == nameof(MainViewModel.ConnectionState))
                    {
                        UpdateNasDot();
                    }
                });
            };
            UpdateNasDot();

            if (Content is UIElement root)
            {
                var ka1 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Number1, Modifiers = Windows.System.VirtualKeyModifiers.Control };
                ka1.Invoked += Accelerator_Tasks_Invoked;
                root.KeyboardAccelerators.Add(ka1);

                var ka2 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Number2, Modifiers = Windows.System.VirtualKeyModifiers.Control };
                ka2.Invoked += Accelerator_Restore_Invoked;
                root.KeyboardAccelerators.Add(ka2);

                var ka3 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Number3, Modifiers = Windows.System.VirtualKeyModifiers.Control };
                ka3.Invoked += Accelerator_Device_Invoked;
                root.KeyboardAccelerators.Add(ka3);

                var ka4 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Number4, Modifiers = Windows.System.VirtualKeyModifiers.Control };
                ka4.Invoked += Accelerator_Settings_Invoked;
                root.KeyboardAccelerators.Add(ka4);


                var kaN = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.N, Modifiers = Windows.System.VirtualKeyModifiers.Control };
                kaN.Invoked += Accelerator_AddWizard_Invoked;
                root.KeyboardAccelerators.Add(kaN);
            }

            // 导航至指定页面
            int tagIndex = initialView switch
            {
                "Restore" => 1,
                "Device" => 2,
                "TestLab" => 3,
                "Settings" => -1,
                _ => 0
            };

            if (tagIndex >= 0 && tagIndex < NavView.MenuItems.Count)
            {
                NavView.SelectedItem = NavView.MenuItems[tagIndex];
            }
            else if (tagIndex == -1 && NavView.FooterMenuItems.Count > 0)
            {
                NavView.SelectedItem = NavView.FooterMenuItems[0];
            }

            if (initialView == "AddFolder" || initialView == "AddWizard" || wizardStep > 0)
            {
                NavigateToTag("AddFolder");
                if (wizardStep > 0)
                {
                    bool stepApplied = false;
                    this.Activated += async (s, e) =>
                    {
                        if (stepApplied) return;
                        stepApplied = true;
                        await System.Threading.Tasks.Task.Delay(300);
                        var wizardVm = AppServices.GetService<AddFolderWizardViewModel>();
                        if (AppServices.IsUiVisualAcceptance)
                        {
                            wizardVm.LoadVisualFixture(wizardStep);
                            return;
                        }
                        if (wizardVm.SelectedLocalFolders.Count == 0)
                        {
                            wizardVm.AddLocalFolder(@"D:\FNSync_GUI_Test_Folders\Project_Alpha");
                            wizardVm.AddLocalFolder(@"D:\FNSync_GUI_Test_Folders\Docs_2026");
                            wizardVm.AddLocalFolder(@"D:\FNSync_GUI_Test_Folders\CAD_Drawings");
                        }
                        if (wizardStep >= 2)
                        {
                            await wizardVm.NextStepAsync();
                        }
                        if (wizardStep >= 3)
                        {
                            await wizardVm.NextStepAsync();
                        }
                    };
                }
            }
            else if (initialView == "DeviceConnected")
            {
                NavigateToTag("Device");
                bool ran = false;
                this.Activated += async (s, e) =>
                {
                    if (ran) return;
                    ran = true;
                    await System.Threading.Tasks.Task.Delay(500);
                    var deviceVm = AppServices.GetService<DeviceViewModel>();
                    await deviceVm.LoginAsync();
                };
            }
            else if (initialView == "RestorePreview")
            {
                NavigateToTag("Restore");
                bool ran = false;
                this.Activated += async (s, e) =>
                {
                    if (ran) return;
                    ran = true;
                    await System.Threading.Tasks.Task.Delay(500);
                    var restoreVm = AppServices.GetService<RestoreViewModel>();
                    await restoreVm.ScanPackagesAsync();

                };
            }
            else if (initialView == "TestLabReport")
            {
                NavigateToTag("TestLab");
                bool ran = false;
                this.Activated += async (s, e) =>
                {
                    if (ran) return;
                    ran = true;
                    await System.Threading.Tasks.Task.Delay(400);
                    var labVm = AppServices.GetService<TestLabViewModel>();
                    await labVm.RunTestAsync();
                };
            }
            else
            {
                NavigateToTag(initialView);
            }
        }

        private void InitializeTrayIcon()
        {
            try
            {
                _trayIcon = new TrayIcon(
                    () => DispatcherQueue.TryEnqueue(ShowFromTray),
                    () => DispatcherQueue.TryEnqueue(async () => await RequestShutdownAsync()), ViewModel.AppTitle);
            }
            catch (Exception ex)
            {
                App.LogCrashException(ex, "MainWindow.InitializeTrayIcon");
                _trayIcon?.Dispose();
                _trayIcon = null;
            }
        }

        private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (_shutdownComplete) return;
            args.Cancel = true;
            if (_settings.MinimizeToTray && _trayIcon != null)
            {
                try { sender.Hide(); }
                catch (Exception ex) { App.LogCrashException(ex, "MainWindow.HideToTray"); }
                return;
            }
            _ = RequestShutdownAsync();
        }

        /// <summary>
        /// Safe-exit entry point for an update handoff. Runs the same sequence as the exit menu —
        /// stop new work, wait for running tasks, flush logs, then really close — and can never be
        /// intercepted by hide-to-tray, because it does not go through AppWindow.Closing.
        /// </summary>
        internal void BeginUpdateShutdown(string reason)
        {
            if (_shutdownComplete) return;
            try { AppTitleText.Text = reason; }
            catch (Exception ex) { App.LogCrashException(ex, "MainWindow.BeginUpdateShutdown"); }
            _ = RequestShutdownAsync();
        }

        private async Task RequestShutdownAsync()
        {
            if (_shutdownComplete) return;
            if (_shutdownInProgress) return;
            _shutdownInProgress = true;
            string priorTitle = AppTitleText.Text;
            try
            {
                ShowFromTray();
                AppTitleText.Text = "正在停止任务并处理日志，请稍候…";
                bool stopped = await AppServices.GetService<TaskManager>().ShutdownAsync(TimeSpan.FromSeconds(15));
                if (stopped)
                {
                    _shutdownComplete = true;
                    Close();
                    return;
                }
                var dialog = new ContentDialog
                {
                    Title = "任务或日志尚未处理完",
                    Content = "尚未确认任务停止或待写日志处理完成。请稍后重试退出；新的同步任务不会再启动。",
                    CloseButtonText = "知道了",
                    XamlRoot = Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (TaskLogPersistenceException ex)
            {
                AppTitleText.Text = "任务日志未保存，请检查磁盘空间和权限";
                var dialog = new ContentDialog
                {
                    Title = "有任务日志未保存",
                    Content = $"{ex.Message}\n\n日志暂时保留在本程序内存中。请检查磁盘空间和日志目录权限，然后再次选择退出以重试。若仍要退出，这些日志可能丢失。",
                    PrimaryButtonText = "稍后重试",
                    SecondaryButtonText = "仍要退出",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Secondary)
                {
                    _shutdownComplete = true;
                    Close();
                }
            }
            catch (Exception ex)
            {
                App.LogCrashException(ex, "MainWindow.Shutdown");
                AppTitleText.Text = "无法确认任务是否停止，请查看日志后重试退出";
            }
            finally
            {
                if (!_shutdownComplete && AppTitleText.Text == "正在停止任务并处理日志，请稍候…") AppTitleText.Text = priorTitle;
                _shutdownInProgress = false;
            }
        }

        public void ShowFromTray()
        {
            _appWindow.Show();
            if (_appWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
                presenter.Restore();
            Activate();
        }

        public void HideToTray()
        {
            if (_trayIcon != null) _appWindow.Hide();
        }

        private void OnTaskLogWriteFailed()
        {
            if (Interlocked.Exchange(ref _logWarningQueued, 1) != 0) return;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _logWarningQueued, 0);
                if (!_shutdownInProgress) AppTitleText.Text = "任务日志未保存，请检查磁盘空间和权限";
            })) Interlocked.Exchange(ref _logWarningQueued, 0);
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _logService.TaskLogWriteFailed -= OnTaskLogWriteFailed;
            Updates.ThemeChangedRequested -= ApplyThemeExplicit;
            try
            {
                _trayIcon?.Dispose();
                AppServices.GetService<TaskManager>().Dispose();
            }
            finally { ((App)Application.Current).ReleaseSingleInstance(); }
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
            {
                NavigateToTag(tag);
            }
        }

        private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
        {
            UpdateButton.Flyout.Hide();
            await Updates.PrepareUpdateAsync(async (version, notes) =>
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Content.XamlRoot, Title = $"安装 v{version} 并重启？",
                    Content = $"{notes}\n\n已排队的同步和正在恢复的文件会先完成。等待期间暂缓开始新任务，可在顶部「更新」中取消等待。",
                    PrimaryButtonText = "确认，任务结束后安装", CloseButtonText = "稍后再说",
                    DefaultButton = ContentDialogButton.Close
                };
                return await dialog.ShowAsync() == ContentDialogResult.Primary;
            });
        }

        private async void Help_Click(object sender, RoutedEventArgs e)
        {
            if (ContentFrame.Content is Page page) await PageHelp.For(page).ShowAsync();
        }

        public void NavigateToTag(string tag)
        {
            if (tag.StartsWith("TestLab", StringComparison.Ordinal) && !AppServices.IsIsolatedTestSession) tag = "Tasks";
            Type targetPageType = tag switch
            {
                "Tasks" => typeof(TasksPage),
                "Restore" => typeof(RestorePage),
                "Device" => typeof(DevicePage),
                "TestLab" => typeof(TestLabPage),
                "Settings" => typeof(SettingsPage),
                "AddFolder" => typeof(AddFolderPage),
                _ => typeof(TasksPage)
            };

            if (ContentFrame.CurrentSourcePageType != targetPageType)
            {
                ContentFrame.Navigate(targetPageType);
            }
            else if (ContentFrame.Content is AddFolderPage addPage)
            {
                addPage.CheckAndTriggerAutoFolderPicker();
            }

            // Visual acceptance only: report this page as ready once its content has actually
            // loaded and one layout pass has run, so the harness screenshots a rendered page
            // instead of a white frame or the previous view.
            if (AppServices.IsUiVisualAcceptance) HookVisualPageReady(tag);
        }

        private void HookVisualPageReady(string tag)
        {
            if (ContentFrame.Content is not FrameworkElement page) return;
            void Report() => DispatcherQueue.TryEnqueue(() => DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    var manager = AppServices.GetService<TaskManager>();
                    var metrics = page is Pages.RestorePage restore ? restore.VisualLayoutMetrics()
                        : page is Pages.DevicePage device ? device.VisualLayoutMetrics() : new Dictionary<string, object?>();
                    metrics["task_count"] = manager.Tasks.Count;
                    metrics["nas_online"] = manager.IsNasOnline;
                    metrics["tray_tooltip"] = _trayIcon?.DisplayText;
                    var caption = _appWindow.TitleBar.ButtonForegroundColor;
                    var inactive = _appWindow.TitleBar.ButtonInactiveForegroundColor;
                    metrics["caption_foreground"] = caption.HasValue ? $"{caption.Value.R:X2}{caption.Value.G:X2}{caption.Value.B:X2}" : "default";
                    metrics["caption_inactive_foreground"] = inactive.HasValue ? $"{inactive.Value.R:X2}{inactive.Value.G:X2}{inactive.Value.B:X2}" : "default";
                    UiVisualSignal.ReportPageReady(tag, metrics);
                    if (Array.IndexOf(Environment.GetCommandLineArgs(), "--show-help-fixture") >= 0 && page is Page helpPage)
                    {
                        _ = PageHelp.For(helpPage).ShowAsync();
                    }
                }
                catch (Exception ex) { App.LogCrashException(ex, "MainWindow.VisualPageReady"); }
            }));
            page.SizeChanged += (_, _) => Report();
            if (page.IsLoaded)
            {
                Report();
                return;
            }
            RoutedEventHandler? once = null;
            once = (s, e) =>
            {
                page.Loaded -= once!;
                Report();
            };
            page.Loaded += once;
        }

        private void UpdateTitleBarPadding()
        {
            try
            {
                double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
                double rightInset = _appWindow.TitleBar.RightInset > 0
                    ? _appWindow.TitleBar.RightInset / Math.Max(1.0, scale)
                    : 144.0;
                AppTitleBar.Padding = new Thickness(14, 0, Math.Max(144.0, rightInset + 8), 0);
            }
            catch
            {
                AppTitleBar.Padding = new Thickness(14, 0, 148, 0);
            }
        }

        /// <summary>
        /// Applies the theme the user saved last time. The setting used to be written but never read,
        /// so a dark-mode user got a light window on every launch until they toggled it again.
        /// </summary>
        private void ApplyStoredTheme()
        {
            try
            {
                bool dark = string.Equals(_settings.AppTheme, "Dark", StringComparison.OrdinalIgnoreCase);
                ApplyThemeVisuals(dark);
                Updates.SyncThemeFromShell(dark);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"应用主题失败: {ex.Message}");
            }
        }

        private void ApplyThemeVisuals(bool dark)
        {
            if (Content is FrameworkElement root) root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
            WindowHelper.ApplyCaptionTheme(this, dark);
            IconThemeToggle.Glyph = dark ? "\uE706" : "\uE708";
            ToolTipService.SetToolTip(BtnThemeToggle, dark ? "切换为浅色主题" : "切换为深色主题");
        }

        private void ApplyThemeExplicit(bool dark)
        {
            ApplyThemeVisuals(dark);
            try
            {
                _settings.AppTheme = dark ? "Dark" : "Light";
                _settings.Save();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"保存主题失败: {ex.Message}");
            }
        }

        private void ToggleAppTheme()
        {
            if (Content is FrameworkElement rootElement)
            {
                bool nextDark = rootElement.ActualTheme != ElementTheme.Dark;
                ApplyThemeExplicit(nextDark);
                Updates.SyncThemeFromShell(nextDark);
            }
        }

        private void BtnNasStatusPill_Click(object sender, RoutedEventArgs e)
        {
            NavigateToTag("Device");
            if (NavView.MenuItems.Count >= 3)
            {
                NavView.SelectedItem = NavView.MenuItems[2];
            }
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleAppTheme();
        }

        private void UpdateNasDot()
        {
            Helpers.UIThreadHelper.RunOnUIThread(() =>
            {
                var color = ViewModel.ConnectionState switch
                {
                    NasConnectionState.Connected => Windows.UI.Color.FromArgb(255, 16, 185, 129),
                    NasConnectionState.Connecting => Windows.UI.Color.FromArgb(255, 245, 158, 11),
                    NasConnectionState.AuthenticationFailed or NasConnectionState.ConnectionLost => Windows.UI.Color.FromArgb(255, 239, 68, 68),
                    _ => Windows.UI.Color.FromArgb(255, 148, 163, 184)
                };
                DotNasStatus.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
            });
        }

        private void Accelerator_Tasks_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            NavigateToTag("Tasks");
            NavView.SelectedItem = NavView.MenuItems[0];
            args.Handled = true;
        }

        private void Accelerator_Restore_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            NavigateToTag("Restore");
            NavView.SelectedItem = NavView.MenuItems[1];
            args.Handled = true;
        }

        private void Accelerator_Device_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            NavigateToTag("Device");
            NavView.SelectedItem = NavView.MenuItems[2];
            args.Handled = true;
        }

        private void Accelerator_Settings_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            NavigateToTag("Settings");
            NavView.SelectedItem = NavView.FooterMenuItems[0];
            args.Handled = true;
        }


        private async void Accelerator_AddWizard_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            var connectionService = Services.AppServices.GetService<FNSyncPlus.Core.Services.NasConnectionService>();
            bool isConnected = connectionService.IsConnected &&
                               connectionService.CurrentStorageProvider is FNSyncPlus.Core.Storage.FnOsPersonalStorageProvider fnProvider &&
                               fnProvider.IsAuthenticated;

            if (isConnected)
            {
                connectionService.PendingOpenLocalFolderPicker = true;
                connectionService.IsPreservingWizardDraft = false;
                NavigateToTag("AddFolder");
            }
            else
            {
                var dialog = new ContentDialog
                {
                    Title = "请先连接飞牛 NAS",
                    Content = "当前尚未连接飞牛官方个人空间。请先登录你的 fnOS 账号，连接成功后即可自动备份到「我的文件」。",
                    PrimaryButtonText = "前往连接",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.Content.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    connectionService.PendingWizardOnConnected = true;
                    connectionService.PendingOpenLocalFolderPicker = true;
                    connectionService.IsPreservingWizardDraft = false;
                    NavigateToTag("Device");
                }
                else
                {
                    connectionService.PendingWizardOnConnected = false;
                    connectionService.PendingOpenLocalFolderPicker = false;
                    connectionService.IsPreservingWizardDraft = false;
                }
            }
        }
    }
}
