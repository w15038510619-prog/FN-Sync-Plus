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
