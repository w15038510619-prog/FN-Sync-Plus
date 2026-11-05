using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.WinUI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly AppSettings _settings;

        [ObservableProperty]
        private bool _startWithWindows;

        [ObservableProperty]
        private bool _minimizeToTray;

        [ObservableProperty]
        private int _maxConcurrentSyncsIndex;

        [ObservableProperty]
        private string _statusNotice = string.Empty;

        public SettingsViewModel(AppSettings settings)
        {
            _settings = settings;

            _startWithWindows = settings.StartWithWindows;
            _minimizeToTray = settings.MinimizeToTray;

            _maxConcurrentSyncsIndex = settings.MaxConcurrentSyncs switch
            {
                1 => 0,
                2 => 1,
                3 => 2,
                4 => 3,
                _ => 1
            };
        }

        [RelayCommand]
        public void SaveSettings()
        {
            _settings.StartWithWindows = StartWithWindows;
            _settings.MinimizeToTray = MinimizeToTray;
            _settings.MaxConcurrentSyncs = MaxConcurrentSyncsIndex switch
            {
                0 => 1,
                1 => 2,
                2 => 3,
                3 => 4,
                _ => 2
            };
            _settings.Save();
            StatusNotice = "设置已保存成功。";
        }

        [RelayCommand]
        public void ClearCredentials()
        {
            SmbAuthService.ClearSavedCredentials();
            StatusNotice = "已成功清除本机使用 Windows DPAPI 加密保存的所有 NAS 登录密码。";
        }
    }
}
