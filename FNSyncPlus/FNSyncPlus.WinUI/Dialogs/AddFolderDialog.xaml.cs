using System;
using System.Linq;
using FNSyncPlus.WinUI.Helpers;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FNSyncPlus.WinUI.Dialogs
{
    public sealed partial class AddFolderDialog : ContentDialog
    {
        public AddFolderWizardViewModel ViewModel { get; }

        public AddFolderDialog(AddFolderWizardViewModel? vm = null)
        {
            ViewModel = vm ?? AppServices.GetService<AddFolderWizardViewModel>();
            InitializeComponent();

            var kaNext = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Right, Modifiers = Windows.System.VirtualKeyModifiers.Menu };
            kaNext.Invoked += Accelerator_Next_Invoked;
            this.KeyboardAccelerators.Add(kaNext);

            var kaBack = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Left, Modifiers = Windows.System.VirtualKeyModifiers.Menu };
            kaBack.Invoked += Accelerator_Back_Invoked;
            this.KeyboardAccelerators.Add(kaBack);
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
                await ViewModel.NasBrowser.NavigateToFolderAsync(category.UncPath);
            }
        }

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

        private async void BtnInlineCreateFolder_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtInlineNewFolderName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;

            bool success = await ViewModel.NasBrowser.CreateSubFolderAsync(name);
            if (success)
            {
                TxtInlineNewFolderName.Text = string.Empty;
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
                this.Hide();
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }

        private void Accelerator_Next_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (ViewModel.CanGoNext)
            {
                _ = ViewModel.NextStepAsync();
                args.Handled = true;
            }
        }

        private void Accelerator_Back_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            if (ViewModel.CanGoBack)
            {
                ViewModel.PreviousStep();
                args.Handled = true;
            }
        }

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
