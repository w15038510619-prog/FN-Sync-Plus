using System;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI.Pages
{
    public sealed partial class DevicePage : Page
    {
        public DeviceViewModel ViewModel { get; }

        public DevicePage()
        {
            ViewModel = AppServices.GetService<DeviceViewModel>();
            InitializeComponent();

            this.Loaded += (s, e) =>
            {
                if (AppServices.IsUiVisualAcceptance && Array.IndexOf(Environment.GetCommandLineArgs(), "--device-connected-fixture") >= 0)
                {
                    // Rendering only: do not authenticate or change the service's connection state.
                    ConnectedCard.Visibility = Visibility.Visible;
                    AccountValue.Text = "visual-test-user";
                    UidValue.Text = "1001";
                    RootValue.Text = "飞牛官方个人空间 [我的文件] (vol1/1001)";
                }
                if (!string.IsNullOrEmpty(ViewModel.Password))
                {
                    TxtPassword.Password = ViewModel.Password;
                }
            };

            ViewModel.RequestNavigateToAddFolder += OnRequestNavigateToAddFolder;
            this.Unloaded += (s, e) =>
            {
                ViewModel.RequestNavigateToAddFolder -= OnRequestNavigateToAddFolder;
            };
        }

        public System.Collections.Generic.Dictionary<string, object?> VisualLayoutMetrics()
        {
            double Contrast(Microsoft.UI.Xaml.Media.Brush foreground)
            {
                if (foreground is not Microsoft.UI.Xaml.Media.SolidColorBrush fg || ConnectedCard.Background is not Microsoft.UI.Xaml.Media.SolidColorBrush bg) return 0;
                double Linear(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
                double Luma(double r, double g, double b) => 0.2126 * Linear(r / 255) + 0.7152 * Linear(g / 255) + 0.0722 * Linear(b / 255);
                double alpha = fg.Color.A / 255.0;
                double text = Luma(fg.Color.R * alpha + bg.Color.R * (1-alpha), fg.Color.G * alpha + bg.Color.G * (1-alpha), fg.Color.B * alpha + bg.Color.B * (1-alpha));
                double background = Luma(bg.Color.R, bg.Color.G, bg.Color.B);
                return (Math.Max(text, background) + 0.05) / (Math.Min(text, background) + 0.05);
            }
            return new() { ["connection_card_visible"] = ConnectedCard.Visibility == Visibility.Visible,
                ["connection_value_contrast"] = Math.Min(Contrast(AccountValue.Foreground), Math.Min(Contrast(UidValue.Foreground), Contrast(RootValue.Foreground))),
                ["connection_label_contrast"] = Contrast(AccountLabel.Foreground) };
        }

        private void OnRequestNavigateToAddFolder()
        {
            Helpers.UIThreadHelper.RunOnUIThread(() =>
            {
                if (App.MainWindowInstance != null)
                {
                    App.MainWindowInstance.NavigateToTag("AddFolder");
                }
                else if (Frame != null)
                {
                    Frame.Navigate(typeof(AddFolderPage));
                }
            });
        }

        private void TxtPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            ViewModel.Password = TxtPassword.Password;
        }

        public bool HasStatusMessage(string message)
        {
            return !string.IsNullOrWhiteSpace(message);
        }

        public InfoBarSeverity GetSeverity(bool isSuccess)
        {
            if (isSuccess) return InfoBarSeverity.Success;
            if (ViewModel.IsBusy) return InfoBarSeverity.Informational;
            if (ViewModel.IsCertConfirmationRequired) return InfoBarSeverity.Warning;
            return InfoBarSeverity.Error;
        }

        public string GetStatusTitle(bool isSuccess)
        {
            if (isSuccess) return "连接就绪";
            if (ViewModel.IsBusy) return "正在处理...";
            if (ViewModel.IsCertConfirmationRequired) return "待核对证书指纹";
            return "连接故障";
        }
    }
}
