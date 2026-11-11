using System;
using FNSyncPlus.WinUI.Helpers;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace FNSyncPlus.WinUI.Pages
{
    public sealed partial class AddFolderPage : Page
    {
        public AddFolderWizardViewModel ViewModel { get; }
        private bool _isPickingFolder = false;

        public AddFolderPage()
        {
            ViewModel = AppServices.GetService<AddFolderWizardViewModel>();
            InitializeComponent();
            this.Loaded += OnPageLoaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            CheckAndTriggerAutoFolderPicker();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var connectionService = AppServices.GetService<FNSyncPlus.Core.Services.NasConnectionService>();
            if (connectionService.IsPreservingWizardDraft)
            {
                connectionService.IsPreservingWizardDraft = false;
                ViewModel.RestoreSessionOnReconnect();
            }
            else
            {
                // 每次用户重新进入添加向导，必须创建全新的独立会话状态，清空临时选择，绝不残留上次记录
                ViewModel.ResetSession();
            }

            if (this.IsLoaded)
            {
                CheckAndTriggerAutoFolderPicker();
            }
        }

        /// <summary>
        /// 检查是否具备单次自动打开文件夹选择器的意图并安全触发
        /// </summary>
        public void CheckAndTriggerAutoFolderPicker()
        {
            var connectionService = AppServices.GetService<FNSyncPlus.Core.Services.NasConnectionService>();
            if (connectionService.PendingOpenLocalFolderPicker)
            {
                // 立即消费该单次意图，确保只弹出一次，用户取消或之后重载绝不再弹
                connectionService.PendingOpenLocalFolderPicker = false;
                _ = TriggerFolderPickerSafeAsync();
            }
        }

        private async Task TriggerFolderPickerSafeAsync()
        {
            if (_isPickingFolder) return;
            _isPickingFolder = true;

            try
            {
                IntPtr hwnd = App.MainWindowHandle;
                if (hwnd == IntPtr.Zero && App.MainWindowInstance != null)
                {
                    hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindowInstance);
                }

                var folder = await WindowHelper.PickFolderAsync(hwnd);
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    await ViewModel.AddLocalFolderAsync(folder);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AddFolderPage] 打开文件夹选择器异常: {ex.Message}");
                ViewModel.StatusMessage = $"打开文件夹选择器失败: {ex.Message}，您可以点击下方「选择本地文件夹」手动选择。";
            }
            finally
            {
                _isPickingFolder = false;
            }
        }

        private async void BtnAddLocalFolder_Click(object sender, RoutedEventArgs e)
        {
            await TriggerFolderPickerSafeAsync();
        }

        private void BtnReconnectNas_Click(object sender, RoutedEventArgs e)
        {
            var connectionService = AppServices.GetService<FNSyncPlus.Core.Services.NasConnectionService>();
            connectionService.PendingWizardOnConnected = true;
            connectionService.IsPreservingWizardDraft = true;
            if (App.MainWindowInstance != null)
            {
                App.MainWindowInstance.NavigateToTag("Device");
            }
            else if (Frame != null)
            {
                Frame.Navigate(typeof(DevicePage));
            }
        }

        private void BtnBackToTasks_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindowInstance != null)
            {
                App.MainWindowInstance.NavigateToTag("Tasks");
            }
            else if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void BtnViewExistingTasks_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindowInstance != null)
            {
                App.MainWindowInstance.NavigateToTag("Tasks");
            }
            else if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }


        private async void BtnRemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string folder)
            {
                await ViewModel.RemoveLocalFolderAsync(folder);
            }
        }

        private async void BtnChangeParentFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Dialogs.NasBrowserDialog
            {
                XamlRoot = this.XamlRoot
            };
            var res = await dialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                string sel = !string.IsNullOrWhiteSpace(dialog.ViewModel.ConfirmedSelectedPath)
                    ? dialog.ViewModel.ConfirmedSelectedPath
                    : dialog.ViewModel.CurrentUncPath;

                if (!string.IsNullOrWhiteSpace(sel))
                {
                    await ViewModel.SetCustomNasDestinationRootAsync(sel);
                }
            }
        }

        private async void BtnSelectExistingFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Dialogs.NasBrowserDialog
            {
                XamlRoot = this.XamlRoot,
                Title = AppServices.IsVersionedBetaSession ? "选择已有飞牛同步文件夹" : "选择已有飞牛同步文件夹（隔离试验）"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string selected = !string.IsNullOrWhiteSpace(dialog.ViewModel.ConfirmedSelectedPath)
                ? dialog.ViewModel.ConfirmedSelectedPath : dialog.ViewModel.CurrentUncPath;
            if (!string.IsNullOrWhiteSpace(selected)) ViewModel.ExperimentalExistingRoot = selected;
        }

        private void BtnClearExistingFolder_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ExperimentalExistingRoot = string.Empty;
        }

        private void BtnPreviousStep_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.PreviousStep();
        }

        private async void BtnNextStep_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.NextStepAsync();
        }

        private async void BtnCreateAndStart_Click(object sender, RoutedEventArgs e)
        {
            int created = await ViewModel.CreateAllTasksAsync();
            if (created > 0)
            {
                if (App.MainWindowInstance != null)
                {
                    App.MainWindowInstance.NavigateToTag("Tasks");
                }
                else if (Frame.CanGoBack)
                {
                    Frame.GoBack();
                }
            }
        }

        public Visibility GetEmptyStateVisibility(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GetListStateVisibility(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GetEmptyNoticeVisibility(int count) => count == 0 ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GetErrorNoticeVisibility(string? error) => !string.IsNullOrWhiteSpace(error) ? Visibility.Visible : Visibility.Collapsed;

        public InfoBarSeverity GetStep1NoticeSeverity(int count) => count > 0 ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;

        public Brush GetStepPillBg(int currentStep, int pillStep)
        {
            if (currentStep == pillStep)
            {
                return (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            }
            return (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        }

        public Brush GetStepPillFg(int currentStep, int pillStep)
        {
            if (currentStep == pillStep)
            {
                return new SolidColorBrush(Colors.White);
            }
            return (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        }

        public InfoBarSeverity GetSecuritySeverity(bool isTeamShareWarning)
        {
            if (isTeamShareWarning) return InfoBarSeverity.Warning;
            if (ViewModel.CanCreate) return InfoBarSeverity.Success;
            return InfoBarSeverity.Informational;
        }
    }
}
