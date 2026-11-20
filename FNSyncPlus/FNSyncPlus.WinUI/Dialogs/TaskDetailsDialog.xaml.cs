using System;
using FNSyncPlus.Core.Models;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace FNSyncPlus.WinUI.Dialogs
{
    public sealed partial class TaskDetailsDialog : ContentDialog
    {
        public TaskDetailsViewModel ViewModel { get; }
        private readonly SyncTask _task;

        public TaskDetailsDialog(SyncTask task)
        {
            _task = task ?? throw new ArgumentNullException(nameof(task));
            ViewModel = AppServices.GetService<TaskDetailsViewModel>();
            InitializeComponent();

            this.Loaded += async (s, e) =>
            {
                try
                {
                    await ViewModel.LoadTaskAsync(_task);
                }
                catch (Exception ex)
                {
                    ViewModel.StatusFeedbackMessage = $"加载失败: {ex.Message}";
                }
            };
        }

        private void BtnCopySourcePath_Click(object sender, RoutedEventArgs e)
        {
            CopyToClipboard(ViewModel.SourceFolder, "源路径已复制到剪贴板");
        }

        private void BtnConfirmSingleRenamePair_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is AmbiguousRenameGroupDisplay group)
                ViewModel.ConfirmRenamePair(group);
        }

        private void BtnCopyDestPath_Click(object sender, RoutedEventArgs e)
        {
            CopyToClipboard(ViewModel.DestinationFolder, "目标路径已复制到剪贴板");
        }

        private void BtnCopyMappingItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string text)
            {
                CopyToClipboard(text, "路径已复制");
            }
        }

        private void BtnCopySingleLog_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string logText)
            {
                CopyToClipboard(logText, "日志已复制");
            }
        }

        private async void BtnRefreshLogs_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.RefreshAllAsync();
        }

        private async void BtnSearchAllLogs_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.SearchAllLogsAsync();
        }

        private void BtnCopyErrors_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.CopyAllErrorsToClipboard();
        }

        private void BtnOpenLogDir_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenLogsDirectory();
        }

        private void BtnExportDiag_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.ExportDiagnosticZip();
        }

        private void CopyToClipboard(string text, string feedback)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                ViewModel.StatusFeedbackMessage = feedback;
            }
            catch
            {
                ViewModel.StatusFeedbackMessage = "剪贴板访问失败";
            }
        }

        public Visibility GetEmptyMappingsVisibility(bool hasMappings, bool hasManifest) => hasManifest && !hasMappings ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GetHasMappingsVisibility(bool hasMappings) => hasMappings ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GetEmptyLogsVisibility(bool hasLogs) => hasLogs ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GetHasLogsVisibility(bool hasLogs) => hasLogs ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GetPendingDifferencesVisibility(bool hasDifferences) => hasDifferences ? Visibility.Visible : Visibility.Collapsed;
    }
}
