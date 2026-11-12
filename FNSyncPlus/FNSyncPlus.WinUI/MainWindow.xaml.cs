using System;
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
            _settings = AppServices.GetService<AppSettings>();
            _logService = AppServices.GetService<LogService>();
            InitializeComponent();

            // The version used to be a hard-coded string, so an upgraded install kept claiming the
            // old version. It now comes from the assembly that is actually running.
            string product = "飞牛同步增强版" + ProductVersionText();
            string title = AppServices.IsIsolatedTestSession
                ? (_settings.IsVersionedBetaTestProfile ? product + " [隔离测试会话·新版配置]" : product + " [隔离测试会话]")
                : AppServices.IsVersionedBetaSession ? product + " [新版备份试用]" : product + " [旧版引擎·仅供诊断]";
            WindowHelper.ConfigureWindow(this, title, 1200, 780);
            AppTitleText.Text = title;
            _logService.TaskLogWriteFailed += OnTaskLogWriteFailed;
            _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowHelper.GetHandle(this)));
            InitializeTrayIcon();
            _appWindow.Closing += OnAppWindowClosing;
            Closed += OnWindowClosed;
            WindowHelper.SetMicaBackdrop(this);
            SetTitleBar(AppTitleBarDragArea);
            ApplyStoredTheme();

            ViewModel.RequestNavigate += NavigateToTag;
            ViewModel.RequestToggleTheme += ToggleAppTheme;
            ViewModel.PropertyChanged += (s, e) =>
            {
                Helpers.UIThreadHelper.RunOnUIThread(() =>
                {
                    if (e.PropertyName == nameof(MainViewModel.IsNasConnected))
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

                var ka5 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Number5, Modifiers = Windows.System.VirtualKeyModifiers.Control };
                ka5.Invoked += Accelerator_TestLab_Invoked;
                root.KeyboardAccelerators.Add(ka5);

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
            else if (initialView == "RestorePreview" || initialView == "RestoreCompleted")
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
                    if (initialView == "RestoreCompleted")
                    {
                        await System.Threading.Tasks.Task.Delay(500);
                        if (restoreVm.SelectedPackage == null && restoreVm.DiscoveredPackages.Count > 0)
                        {
                            restoreVm.SelectedPackage = restoreVm.DiscoveredPackages[0];
                        }
                        await restoreVm.ExecuteRestoreAsync();
                    }
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
                    () => DispatcherQueue.TryEnqueue(async () => await RequestShutdownAsync()));
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

        public void NavigateToTag(string tag)
        {
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
        }

        /// <summary>Version shown in the title bar, taken from the running assembly plus its build suffix.</summary>
        private static string ProductVersionText()
        {
            try
            {
                var assembly = typeof(MainWindow).Assembly;
                var attributes = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
                string version = attributes.Length > 0
                    ? ((System.Reflection.AssemblyInformationalVersionAttribute)attributes[0]).InformationalVersion
                    : assembly.GetName().Version?.ToString(3) ?? string.Empty;
                int metadata = version.IndexOf('+');
                if (metadata > 0) version = version[..metadata];
                return string.IsNullOrWhiteSpace(version) ? string.Empty : " v" + version;
            }
            catch (Exception)
            {
                return string.Empty;
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
                if (Content is FrameworkElement root) root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
                IconThemeToggle.Glyph = dark ? "\uE706" : "\uE708";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"应用主题失败: {ex.Message}");
            }
        }

        private void ToggleAppTheme()
        {
            if (Content is FrameworkElement rootElement)
            {
                bool isDark = rootElement.ActualTheme == ElementTheme.Dark;
                rootElement.RequestedTheme = isDark ? ElementTheme.Light : ElementTheme.Dark;
                IconThemeToggle.Glyph = isDark ? "\uE706" : "\uE708"; // Sun / Moon
                try
                {
                    _settings.AppTheme = isDark ? "Light" : "Dark";
                    _settings.Save();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"保存主题失败: {ex.Message}");
                }
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
                DotNasStatus.Fill = ViewModel.IsNasConnected
                    ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 185, 129))
                    : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 148, 163, 184));
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

        private void Accelerator_TestLab_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            NavigateToTag("TestLab");
            if (NavView.MenuItems.Count >= 4)
            {
                NavView.SelectedItem = NavView.MenuItems[3];
            }
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
