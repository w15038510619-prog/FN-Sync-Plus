using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.WinUI.ViewModels
{
    public partial class SettingsViewModel : ObservableObject
    {
        private readonly AppSettings _settings;
        private readonly NasConnectionService _connectionService;

        public bool CanConfigureStartup => !_settings.IsVersionedBetaProfile && !_settings.IsInMemoryOnly && _settings.IsolatedSessionPath == null;

        [ObservableProperty]
        private bool _startWithWindows;

        [ObservableProperty]
        private bool _minimizeToTray;

        [ObservableProperty]
        private int _maxConcurrentSyncsIndex;

        [ObservableProperty]
        private string _statusNotice = string.Empty;

        public SettingsViewModel(AppSettings settings, NasConnectionService connectionService)
        {
            _settings = settings;
            _connectionService = connectionService;

            _startWithWindows = CanConfigureStartup && settings.StartWithWindows;
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
            _settings.StartWithWindows = CanConfigureStartup && StartWithWindows;
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
            StatusNotice = CanConfigureStartup ? "设置已保存成功。" : "设置已保存；此试用或测试配置暂不支持开机自动启动。";
        }

        [RelayCommand]
        public async System.Threading.Tasks.Task ClearCredentials()
        {
            var result = await _connectionService.ForgetSavedCredentialsAsync();
            StatusNotice = result.Message;
        }
    }
}
