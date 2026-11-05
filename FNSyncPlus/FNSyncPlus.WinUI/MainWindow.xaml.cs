using System;
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

        public MainWindow()
        {
            InitializeComponent();

            ViewModel = AppServices.GetService<MainViewModel>();

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

            // 默认导航至同步任务页面
            NavView.SelectedItem = NavView.MenuItems[0];
            NavigateToTag("Tasks");
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
    }
}
