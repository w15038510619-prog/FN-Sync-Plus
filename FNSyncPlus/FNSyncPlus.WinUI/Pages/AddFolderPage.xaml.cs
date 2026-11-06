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
