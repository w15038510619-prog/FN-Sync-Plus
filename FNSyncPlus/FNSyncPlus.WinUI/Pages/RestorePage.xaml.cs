using System;
using System.Linq;
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
            Loaded += async (_, _) => { if (!AppServices.IsUiVisualAcceptance) await ViewModel.LoadKnownBackupsAsync(); };

            if (AppServices.IsUiVisualAcceptance)
                {
                bool history = Environment.GetCommandLineArgs().Contains("--restore-history-fixture");
                ViewModel.LoadVisualFixture(history);
                if (history) RestoreMode.SelectedItem = RestoreMode.Items[1];
            }
        }

        private void RestoreMode_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) =>
            ViewModel.IsHistoryMode = sender.Items.Count > 1 && sender.SelectedItem == sender.Items[1];

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: RestoreBrowserItem folder }) ViewModel.OpenFolder(folder);
        }

        private void Browser_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (ViewModel.IsHistoryMode && e.ClickedItem is RestoreBrowserItem { IsFolder: false } file)
                ViewModel.SetHistoryFile(file);
        }

        private void BrowserRow_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            for (var element = e.OriginalSource as DependencyObject; element != null && !ReferenceEquals(element, sender);
                element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
            {
                if (element is Button or CheckBox) return;
            }
            if (sender is FrameworkElement { DataContext: RestoreBrowserItem { IsFolder: true } folder })
            {
                ViewModel.OpenFolder(folder);
                e.Handled = true;
            }
        }

        private void Browser_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter &&
                e.OriginalSource is ListViewItem { Content: RestoreBrowserItem { IsFolder: true } folder })
            {
                ViewModel.OpenFolder(folder);
                e.Handled = true;
            }
        }

        private void BrowserCheck_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox { Tag: RestoreBrowserItem item } && !ViewModel.IsHistoryMode)
                ViewModel.ToggleSelection(item);
        }

        public System.Collections.Generic.Dictionary<string, object?> VisualLayoutMetrics()
        {
            var files = CurrentFileList;
            FrameworkElement action = ViewModel.IsHistoryMode ? HistoryRestoreAction : CurrentRestoreActions;
            double bottom = action.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)).Y + action.ActualHeight;
            return new() { ["restore_list_height"] = files.ActualHeight, ["restore_action_height"] = action.ActualHeight,
                ["restore_action_bottom"] = bottom, ["restore_page_height"] = ActualHeight };
        }

        private async void BeginRestore_Click(object sender, RoutedEventArgs e)
        {
            if (_confirming) return;
            bool historical = ReferenceEquals(sender, HistoryRestoreAction);
            bool retry = ReferenceEquals(sender, RetryRestoreButton);
            bool entire = ReferenceEquals(sender, RestoreEntireButton);
            bool CanStart() => historical ? ViewModel.CanRestoreHistory : retry ? ViewModel.CanRetryCurrentRestore :
                entire ? ViewModel.CanRestoreEntireBackup : ViewModel.CanRestoreSelection;
            if (!CanStart()) return;
            var package = ViewModel.SelectedPackage!;
            var history = ViewModel.SelectedHistory;
            var destination = ViewModel.LocalRestoreTarget;
            int policyIndex = ViewModel.ConflictPolicyIndex;
            var selectedFiles = ViewModel.SelectedFilePaths.ToHashSet(StringComparer.Ordinal);
            var selectedFolders = ViewModel.SelectedDirectoryPaths.ToHashSet(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(destination))
            {
                ViewModel.RestoreStatusText = "请选择本地目标文件夹。";
                return;
            }

            string version = historical ? $"{history!.DisplayPath}\n{history.ArchivedAt} · {history.Reason}" :
                $"当前备份 · {package.LastUpdated.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
            string scope = historical ? "" : entire ? $"整个备份（{ViewModel.PackageFiles.Count} 个文件及所有文件夹）" :
                retry ? $"失败的 {ViewModel.FailedFilesList.Count} 个文件" : ViewModel.SelectionSummary;
            string conflict = policyIndex switch
            {
                1 => "覆盖已存在的同名文件",
                2 => "给恢复的同名文件加时间戳，保留原文件",
                _ => "跳过已存在的同名文件"
            };
            var detail = new TextBlock
            {
                Text = $"{package.PackageName}\n{version}\n{scope}\n恢复到：{destination}\n同名文件：{conflict}",
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            _confirming = true;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = historical ? "确认恢复历史版本" : retry ? "确认重试当前备份" : entire ? "确认恢复整个备份" : "确认恢复所选",
                    Content = new ScrollViewer { Content = detail, MaxHeight = 340 },
                    PrimaryButtonText = "开始恢复", CloseButtonText = "返回", DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
                    package != ViewModel.SelectedPackage || history != ViewModel.SelectedHistory ||
                    policyIndex != ViewModel.ConflictPolicyIndex || destination != ViewModel.LocalRestoreTarget ||
                    !CanStart() || !selectedFiles.SetEquals(ViewModel.SelectedFilePaths) || !selectedFolders.SetEquals(ViewModel.SelectedDirectoryPaths)) return;
                if (historical) await ViewModel.RestoreHistoryAsync();
                else if (retry) await ViewModel.RetryFailedFilesAsync();
                else await ViewModel.ExecuteRestoreAsync(entire);
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
            dialog.ViewModel.AllowReadOnlySelection = true;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            string selected = !string.IsNullOrWhiteSpace(dialog.ViewModel.ConfirmedSelectedPath)
                ? dialog.ViewModel.ConfirmedSelectedPath : dialog.ViewModel.CurrentUncPath;
            if (!string.IsNullOrWhiteSpace(selected)) { ViewModel.NasScanRoot = selected; await ViewModel.ScanPackagesAsync(); }
        }

        private async void Details_Click(object sender, RoutedEventArgs e)
        {
            var text = string.Join("\n\n", System.Linq.Enumerable.Select(ViewModel.PackageFiles,
                file => $"{file.OriginalRelPath}\n存储位置：{file.StoredRelPath}\nSHA-256：{file.Sha256}"));
            await new ContentDialog { XamlRoot = XamlRoot, Title = "备份高级详情", CloseButtonText = "关闭",
                Content = new ScrollViewer { MaxHeight = 360, Content = new TextBlock { Text = text,
                    IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap } } }.ShowAsync();
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
