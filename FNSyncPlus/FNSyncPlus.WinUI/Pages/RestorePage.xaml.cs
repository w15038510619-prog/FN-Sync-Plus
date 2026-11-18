using System;
using FNSyncPlus.WinUI.Helpers;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI.Pages
{
    public sealed partial class RestorePage : Page
    {
        public RestoreViewModel ViewModel { get; }
        private bool _confirming;

        public RestorePage()
        {
            ViewModel = AppServices.GetService<RestoreViewModel>();
            InitializeComponent();

            if (AppServices.IsUiVisualAcceptance &&
                Array.IndexOf(Environment.GetCommandLineArgs(), "--restore-history-fixture") >= 0)
            {
                ViewModel.HistoryFilesList.Add(new(new FNSyncPlus.Core.Models.ManifestHistoryEntry
                {
                    OriginalRel = "设计资料/季度报表.txt", ArchivedAt = DateTimeOffset.Now, Reason = "superseded", Size = 32
                }));
                ViewModel.HistoryFilesList.Add(new(new FNSyncPlus.Core.Models.ManifestHistoryEntry
                {
                    OriginalRel = @"项目资料/\u7248\u672c\u8bf4\u660e.txt", ArchivedAt = DateTimeOffset.Now.AddDays(-1),
                    Reason = "renamed", Size = 24
                }));
                RestoreTabs.SelectedIndex = 1;
            }
        }

        private void RestoreTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CurrentRestoreActions == null || sender is not Pivot pivot) return;
            CurrentRestoreActions.Visibility = pivot.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BeginRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_confirming || !ViewModel.CanConfigureRestore) return;
            bool historical = RestoreTabs.SelectedIndex == 1;
            bool retry = ReferenceEquals(sender, RetryRestoreButton);
            var package = ViewModel.SelectedPackage;
            var history = ViewModel.SelectedHistory;
            var destination = ViewModel.LocalRestoreTarget;
            int policyIndex = ViewModel.ConflictPolicyIndex;
            if (package == null || (historical && history == null) || string.IsNullOrWhiteSpace(destination))
            {
                ViewModel.RestoreStatusText = "请选择要恢复的版本和本地目标文件夹。";
                return;
            }

            string version = historical ? $"历史版本 · {history!.ArchivedAt}\n文件：{history.DisplayPath}" :
                $"当前备份 · {package.LastUpdated.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n快照：{package.VersionId ?? "传统备份清单"}";
            string conflict = policyIndex switch
            {
                1 => "覆盖已存在的同名文件",
                2 => "给恢复的同名文件加时间戳，保留原文件",
                _ => "跳过已存在的同名文件"
            };
            var detail = new TextBlock
            {
                Text = $"{version}\n备份位置：{package.NasFolderUnc}\n恢复到：{destination}\n同名文件：{conflict}",
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            _confirming = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = historical ? "确认恢复历史版本" : retry ? "确认重试当前备份" : "确认恢复当前备份",
                    Content = new ScrollViewer { Content = detail, MaxHeight = 340 },
                    PrimaryButtonText = "开始恢复", CloseButtonText = "返回", DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
                    package != ViewModel.SelectedPackage || history != ViewModel.SelectedHistory ||
                    policyIndex != ViewModel.ConflictPolicyIndex || destination != ViewModel.LocalRestoreTarget) return;
                if (historical) await ViewModel.RestoreHistoryAsync();
                else if (retry) await ViewModel.RetryFailedFilesAsync();
                else await ViewModel.ExecuteRestoreAsync();
            }
            catch (Exception ex)
            {
                App.LogCrashException(ex, "RestorePage.BeginRestore");
                ViewModel.RestoreStatusText = "无法启动恢复，请检查备份与目标位置后重试。";
            }
            finally { _confirming = false; }
        }

        private async void BtnBrowseNasRestore_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Dialogs.NasBrowserDialog { XamlRoot = XamlRoot, Title = "选择备份目录或其上一级目录" };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string selected = !string.IsNullOrWhiteSpace(dialog.ViewModel.ConfirmedSelectedPath)
                ? dialog.ViewModel.ConfirmedSelectedPath : dialog.ViewModel.CurrentUncPath;
            if (!string.IsNullOrWhiteSpace(selected)) ViewModel.NasScanRoot = selected;
        }

        private async void BtnBrowseLocalRestore_Click(object sender, RoutedEventArgs e)
        {
            var folder = await WindowHelper.PickFolderAsync(App.MainWindowHandle);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                ViewModel.LocalRestoreTarget = folder;
            }
        }
    }
}
