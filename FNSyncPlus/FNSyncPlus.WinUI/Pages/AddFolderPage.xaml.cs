using System;
using FNSyncPlus.WinUI.Helpers;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FNSyncPlus.WinUI.Pages
{
    public sealed partial class AddFolderPage : Page
    {
        public AddFolderWizardViewModel ViewModel { get; }

        public AddFolderPage()
        {
            ViewModel = AppServices.GetService<AddFolderWizardViewModel>();
            InitializeComponent();
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

        private async void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is CategoryItem category)
            {
                await ViewModel.NasBrowser.SelectCategoryAsync(category);
            }
        }

        private async void BtnApplyCustomPersonalRoot_Click(object sender, RoutedEventArgs e)
        {
            string path = TxtCustomPersonalRoot.Text.Trim();
            if (string.IsNullOrWhiteSpace(path)) return;

            bool ok = await ViewModel.NasBrowser.ApplyCustomPersonalPathAsync(path);
            if (ok)
            {
                TxtCustomPersonalRoot.Text = string.Empty;
            }
        }

        private async void BtnRefreshPersonalDetection_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.NasBrowser.InitializeAsync();
        }

        public Visibility GetExplorerVisibility(bool isGuidance) => isGuidance ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GetGuidanceVisibility(bool isGuidance) => isGuidance ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GetOfficialInfoVisibility(string? text) => !string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;

        private async void BreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
        {
            if (args.Item is BreadcrumbItem item)
            {
                await ViewModel.NasBrowser.NavigateToFolderAsync(item.Path);
            }
        }

        private async void FolderList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            if (ViewModel.NasBrowser.SelectedFolder != null)
            {
                await ViewModel.NasBrowser.NavigateToFolderAsync(ViewModel.NasBrowser.SelectedFolder.FullUncPath);
            }
        }

        private async void BtnRefreshFolder_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.NasBrowser.RefreshCurrentFolderAsync();
        }

        private async void BtnNavigateUp_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.NasBrowser.NavigateUpAsync();
        }

        private void BtnInlineCancel_Click(object sender, RoutedEventArgs e)
        {
            TxtInlineNewFolderName.Text = string.Empty;
            ViewModel.NasBrowser.LastErrorMessage = string.Empty;
            InlineNewFolderFlyout.Hide();
        }

        private async void BtnInlineCreateFolder_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtInlineNewFolderName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;

            bool success = await ViewModel.NasBrowser.CreateSubFolderAsync(name);
            if (success)
            {
                TxtInlineNewFolderName.Text = string.Empty;
                ViewModel.NasBrowser.LastErrorMessage = string.Empty;
                InlineNewFolderFlyout.Hide();
            }
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
