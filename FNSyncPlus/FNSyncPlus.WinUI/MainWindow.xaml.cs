using System;
using FNSyncPlus.WinUI.Dialogs;
using FNSyncPlus.WinUI.Helpers;
using FNSyncPlus.WinUI.Pages;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI
{
    public sealed partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }

        public MainWindow(string initialView = "Tasks", int wizardStep = 0)
        {
            ViewModel = AppServices.GetService<MainViewModel>();
            InitializeComponent();

            WindowHelper.ConfigureWindow(this, "飞牛同步增强版 (FN Sync Plus) v3.0", 1200, 780);
            WindowHelper.SetMicaBackdrop(this);
            SetTitleBar(AppTitleBar);

            ViewModel.RequestNavigate += NavigateToTag;
            ViewModel.RequestToggleTheme += ToggleAppTheme;
            ViewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsNasConnected))
                {
                    UpdateNasDot();
                }
            };
            UpdateNasDot();

            if (Content is UIElement root)
            {
                var ka1 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Number1, Modifiers = Windows.System.VirtualKeyModifiers.Control };
                ka1.Invoked += Accelerator_Tasks_Invoked;
                root.KeyboardAccelerators.Add(ka1);

                var ka2 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Number2, Modifiers = Windows.System.VirtualKeyModifiers.Control };
                ka2.Invoked += Accelerator_Restore_Invoked;
                root.KeyboardAccelerators.Add(ka2);

                var ka3 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Number3, Modifiers = Windows.System.VirtualKeyModifiers.Control };
                ka3.Invoked += Accelerator_Device_Invoked;
                root.KeyboardAccelerators.Add(ka3);

                var ka4 = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.Number4, Modifiers = Windows.System.VirtualKeyModifiers.Control };
                ka4.Invoked += Accelerator_Settings_Invoked;
                root.KeyboardAccelerators.Add(ka4);

                var kaN = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.N, Modifiers = Windows.System.VirtualKeyModifiers.Control };
                kaN.Invoked += Accelerator_AddWizard_Invoked;
                root.KeyboardAccelerators.Add(kaN);
            }

            // 导航至指定页面
            int tagIndex = initialView switch
            {
                "Restore" => 1,
                "Device" => 2,
                "Settings" => -1,
                _ => 0
            };

            if (tagIndex >= 0 && tagIndex < NavView.MenuItems.Count)
            {
                NavView.SelectedItem = NavView.MenuItems[tagIndex];
            }
            else if (tagIndex == -1 && NavView.FooterMenuItems.Count > 0)
            {
                NavView.SelectedItem = NavView.FooterMenuItems[0];
            }

            NavigateToTag(initialView);

            if (wizardStep > 0)
            {
                bool shown = false;
                this.Activated += async (s, e) =>
                {
                    if (shown) return;
                    shown = true;
                    await System.Threading.Tasks.Task.Delay(500);
                    var wizardVm = AppServices.GetService<AddFolderWizardViewModel>();
                    if (wizardStep >= 2)
                    {
                        await wizardVm.NextStepAsync();
                    }
                    if (wizardStep >= 3)
                    {
                        await wizardVm.NextStepAsync();
                    }

                    var dialog = new AddFolderDialog(wizardVm)
                    {
                        XamlRoot = this.Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                };
            }
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItemContainer is NavigationViewItem item && item.Tag is string tag)
            {
                NavigateToTag(tag);
            }
        }

        public void NavigateToTag(string tag)
        {
            Type targetPageType = tag switch
            {
                "Tasks" => typeof(TasksPage),
                "Restore" => typeof(RestorePage),
                "Device" => typeof(DevicePage),
                "Settings" => typeof(SettingsPage),
                _ => typeof(TasksPage)
            };

            if (ContentFrame.CurrentSourcePageType != targetPageType)
            {
                ContentFrame.Navigate(targetPageType);
            }
        }

        private void ToggleAppTheme()
        {
            if (Content is FrameworkElement rootElement)
            {
                bool isDark = rootElement.ActualTheme == ElementTheme.Dark;
                rootElement.RequestedTheme = isDark ? ElementTheme.Light : ElementTheme.Dark;
                IconThemeToggle.Glyph = isDark ? "\uE706" : "\uE708"; // Sun / Moon
            }
        }

        private void BorderNasPill_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            NavigateToTag("Device");
            if (NavView.MenuItems.Count >= 3)
            {
                NavView.SelectedItem = NavView.MenuItems[2];
            }
        }

        private void BtnThemeToggle_Click(object sender, RoutedEventArgs e)
        {
            ToggleAppTheme();
        }

        private void UpdateNasDot()
        {
            DotNasStatus.Fill = ViewModel.IsNasConnected
                ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 185, 129))
                : new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 245, 158, 11));
        }

        private void Accelerator_Tasks_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            NavigateToTag("Tasks");
            NavView.SelectedItem = NavView.MenuItems[0];
            args.Handled = true;
        }

        private void Accelerator_Restore_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            NavigateToTag("Restore");
            NavView.SelectedItem = NavView.MenuItems[1];
            args.Handled = true;
        }

        private void Accelerator_Device_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            NavigateToTag("Device");
            NavView.SelectedItem = NavView.MenuItems[2];
            args.Handled = true;
        }

        private void Accelerator_Settings_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            NavigateToTag("Settings");
            NavView.SelectedItem = NavView.FooterMenuItems[0];
            args.Handled = true;
        }

        private void Accelerator_AddWizard_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            NavigateToTag("Tasks");
            var tasksVm = AppServices.GetService<TasksViewModel>();
            tasksVm.AddFolderCommand.Execute(null);
            args.Handled = true;
        }
    }
}
