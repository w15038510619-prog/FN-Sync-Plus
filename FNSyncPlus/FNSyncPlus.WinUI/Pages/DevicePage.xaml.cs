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
            InitializeComponent();
            ViewModel = AppServices.GetService<DeviceViewModel>();

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
            if (ViewModel.StatusMessage.Contains("失败") || ViewModel.StatusMessage.Contains("异常"))
            {
                return InfoBarSeverity.Error;
            }
            return InfoBarSeverity.Informational;
        }

        public string GetStatusTitle(bool isSuccess)
        {
            if (isSuccess) return "连接就绪";
            if (ViewModel.StatusMessage.Contains("失败") || ViewModel.StatusMessage.Contains("异常"))
            {
                return "连接故障";
            }
            return "状态就绪";
        }
    }
}
