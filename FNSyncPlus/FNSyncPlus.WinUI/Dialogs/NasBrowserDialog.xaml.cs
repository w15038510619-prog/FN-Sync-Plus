using System;
using System.Linq;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI.Dialogs
{
    public sealed partial class NasBrowserDialog : ContentDialog
    {
        public NasBrowserViewModel ViewModel { get; }

        public NasBrowserDialog()
        {
            InitializeComponent();
            ViewModel = AppServices.GetService<NasBrowserViewModel>();
        }

        private async void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is CategoryItem category)
            {
                await ViewModel.NavigateToFolderAsync(category.UncPath, category.UncPath);
            }
        }

        private async void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
        {
            if (args.Item is BreadcrumbItem item)
            {
                await ViewModel.NavigateToFolderAsync(item.Path);
            }
        }

        private async void FolderList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            if (ViewModel.SelectedFolder != null)
            {
                await ViewModel.NavigateToFolderAsync(ViewModel.SelectedFolder.FullUncPath);
            }
        }

        private async void BtnRefreshFolder_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.RefreshCurrentFolderAsync();
        }

        private async void BtnNavigateUp_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.NavigateUpAsync();
        }

        private void BtnCancelNewFolder_Click(object sender, RoutedEventArgs e)
        {
            TxtNewFolderName.Text = string.Empty;
            ViewModel.LastErrorMessage = string.Empty;
            NewFolderFlyout.Hide();
        }

        private async void BtnConfirmCreateFolder_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtNewFolderName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;

            bool success = await ViewModel.CreateSubFolderAsync(name);
            if (success)
            {
                TxtNewFolderName.Text = string.Empty;
                ViewModel.LastErrorMessage = string.Empty;
                NewFolderFlyout.Hide();
            }
        }

        private void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            bool confirmed = ViewModel.ConfirmSelection();
            if (!confirmed)
            {
                args.Cancel = true;
            }
        }
    }
}
