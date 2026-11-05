using System;
using FNSyncPlus.WinUI.Helpers;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI.Dialogs
{
    public sealed partial class AddFolderDialog : ContentDialog
    {
        public AddFolderWizardViewModel ViewModel { get; }

        public AddFolderDialog()
        {
            InitializeComponent();
            ViewModel = AppServices.GetService<AddFolderWizardViewModel>();
        }

        private async void BtnAddLocalFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = await WindowHelper.PickFolderAsync(App.MainWindowHandle);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                ViewModel.AddLocalFolder(folder);
            }
        }

        private void BtnRemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string folder)
            {
                ViewModel.RemoveLocalFolder(folder);
            }
        }

        private async void TxtNasRoot_TextChanged(object sender, TextChangedEventArgs e)
        {
            await ViewModel.EvaluateNasRootSecurityAsync();
        }

        private async void BtnBrowseNasRoot_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();

            var browser = new NasBrowserDialog
            {
                XamlRoot = this.XamlRoot
            };

            var res = await browser.ShowAsync();
            if (res == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(browser.ViewModel.ConfirmedSelectedPath))
            {
                ViewModel.NasDestinationRoot = browser.ViewModel.ConfirmedSelectedPath;
                await ViewModel.EvaluateNasRootSecurityAsync();
            }

            await this.ShowAsync();
        }

        private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (!ViewModel.CanCreate || ViewModel.PlannedTasks.Count == 0)
            {
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            try
            {
                int created = await ViewModel.CreateAllTasksAsync();
                if (created == 0)
                {
                    args.Cancel = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        }

        public InfoBarSeverity GetSecuritySeverity(bool isTeamShareWarning)
        {
            if (isTeamShareWarning) return InfoBarSeverity.Warning;
            if (ViewModel.CanCreate) return InfoBarSeverity.Success;
            return InfoBarSeverity.Informational;
        }
    }
}
