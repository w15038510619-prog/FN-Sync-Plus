using System;
using FNSyncPlus.WinUI.Dialogs;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI.Pages
{
    public sealed partial class TasksPage : Page
    {
        public TasksViewModel ViewModel { get; }
        private bool _isDeleting = false;
        private bool _isShowingDetails = false;
        private bool _isEditingSettings = false;

        public TasksPage()
        {
            ViewModel = AppServices.GetService<TasksViewModel>();
            InitializeComponent();

            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.RequestAddFolderWizard -= OpenAddFolderWizardDialog;
            ViewModel.RequestAddFolderWizard += OpenAddFolderWizardDialog;

            ViewModel.RequestTaskDetails -= OpenTaskDetailsDialog;
            ViewModel.RequestTaskDetails += OpenTaskDetailsDialog;

            ViewModel.RequestEditSettings -= OpenTaskSettingsDialog;
            ViewModel.RequestEditSettings += OpenTaskSettingsDialog;

            ViewModel.RequestConfirmDelete -= ConfirmDeleteTask;
            ViewModel.RequestConfirmDelete += ConfirmDeleteTask;
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.RequestAddFolderWizard -= OpenAddFolderWizardDialog;
            ViewModel.RequestTaskDetails -= OpenTaskDetailsDialog;
            ViewModel.RequestEditSettings -= OpenTaskSettingsDialog;
            ViewModel.RequestConfirmDelete -= ConfirmDeleteTask;
        }

        private async void OpenAddFolderWizardDialog()
        {
            var connectionService = AppServices.GetService<FNSyncPlus.Core.Services.NasConnectionService>();
            bool isConnected = connectionService.IsConnected &&
                               connectionService.CurrentStorageProvider is FNSyncPlus.Core.Storage.FnOsPersonalStorageProvider fnProvider &&
                               fnProvider.IsAuthenticated;

            if (isConnected)
            {
                // 已连接：直接进入添加向导，并标记自动弹出文件夹选择窗口
                connectionService.PendingOpenLocalFolderPicker = true;
                connectionService.IsPreservingWizardDraft = false;

                if (App.MainWindowInstance != null)
                {
                    App.MainWindowInstance.NavigateToTag("AddFolder");
                }
                else
                {
                    Frame.Navigate(typeof(AddFolderPage));
                }
            }
            else
            {
                // 尚未连接飞牛官方个人空间，弹出对话框引导用户
                var dialog = new ContentDialog
                {
                    Title = "请先连接飞牛 NAS",
                    Content = "当前尚未连接飞牛官方个人空间。请先登录你的 fnOS 账号，连接成功后即可自动备份到「我的文件」。",
                    PrimaryButtonText = "前往连接",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot ?? App.MainWindowInstance?.Content?.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    connectionService.PendingWizardOnConnected = true;
                    connectionService.PendingOpenLocalFolderPicker = true;
                    connectionService.IsPreservingWizardDraft = false;
                    if (App.MainWindowInstance != null)
                    {
                        App.MainWindowInstance.NavigateToTag("Device");
                    }
                    else
                    {
                        Frame.Navigate(typeof(DevicePage));
                    }
                }
                else
                {
                    connectionService.PendingWizardOnConnected = false;
                    connectionService.PendingOpenLocalFolderPicker = false;
                    connectionService.IsPreservingWizardDraft = false;
                }
            }
        }

        private async void OpenTaskDetailsDialog(SyncTaskItemViewModel taskItem)
        {
            if (_isShowingDetails || taskItem == null) return;
            _isShowingDetails = true;

            try
            {
                if (this.XamlRoot == null) return;

                var dialog = new TaskDetailsDialog(taskItem.Model)
                {
                    XamlRoot = this.XamlRoot
                };

                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TasksPage] 打开任务详情弹窗异常: {ex.Message}");
            }
            finally
            {
                _isShowingDetails = false;
            }
        }

        private async void OpenTaskSettingsDialog(SyncTaskItemViewModel taskItem)
        {
            if (_isEditingSettings || taskItem == null) return;
            _isEditingSettings = true;

            try
            {
                if (this.XamlRoot == null) return;

                var tm = AppServices.GetService<FNSyncPlus.Core.Services.TaskManager>();
                var dialog = new TaskSettingsDialog(taskItem.Model, tm)
                {
                    XamlRoot = this.XamlRoot
                };

                await dialog.ShowAsync();
                taskItem.UpdateVisualState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TasksPage] 打开任务设置弹窗异常: {ex.Message}");
            }
            finally
            {
                _isEditingSettings = false;
            }
        }

        private async void ConfirmDeleteTask(SyncTaskItemViewModel taskItem)
        {
            if (_isDeleting || taskItem == null) return;
            _isDeleting = true;

            try
            {
                if (this.XamlRoot == null) return;

                var confirmDialog = new ContentDialog
                {
                    Title = "确认仅移除任务配置",
                    Content = $"确定要在当前软件中移除任务【{taskItem.TaskName}】的配置吗？\n\n程序会先请求停止当前同步，再移除任务配置。此操作不会主动删除本地源文件或 NAS 备份文件。",
                    PrimaryButtonText = "确认移除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                var res = await confirmDialog.ShowAsync();
                if (res == ContentDialogResult.Primary)
                {
                    var tm = AppServices.GetService<FNSyncPlus.Core.Services.TaskManager>();
                    if (!await tm.RemoveTaskAsync(taskItem.TaskId))
                    {
                        var warning = new ContentDialog
                        {
                            Title = "任务配置未移除",
                            Content = taskItem.Model.LastError ?? "任务可能仍在停止或已被移除，请检查任务状态后重试。",
                            CloseButtonText = "知道了",
                            XamlRoot = this.XamlRoot
                        };
                        await warning.ShowAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TasksPage] 移除任务弹窗或处理异常: {ex.Message}");
                if (this.XamlRoot != null)
                {
                    var error = new ContentDialog
                    {
                        Title = "移除任务失败",
                        Content = $"任务配置未能移除：{ex.Message}",
                        CloseButtonText = "知道了",
                        XamlRoot = this.XamlRoot
                    };
                    await error.ShowAsync();
                }
            }
            finally
            {
                _isDeleting = false;
            }
        }

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenAddFolderWizardDialog();
        }

        private void BtnSyncAll_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SyncAllCommand.Execute(null);
        }
    }
}
