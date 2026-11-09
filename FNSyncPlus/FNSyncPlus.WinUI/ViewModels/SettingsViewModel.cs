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
        public async System.Threading.Tasks.Task ClearCredentials()
        {
            var result = await _connectionService.ForgetSavedCredentialsAsync();
            StatusNotice = result.Message;
        }
    }
}
