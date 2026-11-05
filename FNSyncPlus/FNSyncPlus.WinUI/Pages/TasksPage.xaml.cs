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

        public TasksPage()
        {
            ViewModel = AppServices.GetService<TasksViewModel>();
            InitializeComponent();

            ViewModel.RequestAddFolderWizard += OpenAddFolderWizardDialog;
            ViewModel.RequestTaskDetails += OpenTaskDetailsDialog;
            ViewModel.RequestConfirmDelete += ConfirmDeleteTask;
        }

        private async void OpenAddFolderWizardDialog()
        {
            var dialog = new AddFolderDialog
            {
                XamlRoot = this.XamlRoot
            };

            await dialog.ShowAsync();
        }

        private async void OpenTaskDetailsDialog(SyncTaskItemViewModel taskItem)
        {
            var dialog = new TaskDetailsDialog(taskItem.Model)
            {
                XamlRoot = this.XamlRoot
            };

            await dialog.ShowAsync();
        }

        private async void ConfirmDeleteTask(SyncTaskItemViewModel taskItem)
        {
            var confirmDialog = new ContentDialog
            {
                Title = "确认删除任务配置",
                Content = $"确定要删除任务【{taskItem.TaskName}】吗？\n\n注意：此操作仅删除任务的自动化调度配置，绝对不会删除或修改您的本地源文件或 NAS 已有的备份文件。",
                PrimaryButtonText = "确认删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot
            };

            var res = await confirmDialog.ShowAsync();
            if (res == ContentDialogResult.Primary)
            {
                var tm = AppServices.GetService<FNSyncPlus.Core.Services.TaskManager>();
                tm.RemoveTask(taskItem.TaskId);
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
