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
                if (!string.IsNullOrEmpty(ViewModel.Password))
                {
                    TxtPassword.Password = ViewModel.Password;
                }
            };
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
