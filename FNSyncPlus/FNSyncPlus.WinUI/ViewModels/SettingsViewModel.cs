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
            if (!_settings.IsInMemoryOnly || !string.IsNullOrEmpty(_settings.IsolatedCredentialPath))
                SmbAuthService.ClearSavedCredentials(_settings.IsolatedCredentialPath);
            StatusNotice = _settings.IsInMemoryOnly ? "已清除隔离测试会话保存的凭据。" : "已清除本机保存的 NAS 登录凭据。";
        }
    }
}
