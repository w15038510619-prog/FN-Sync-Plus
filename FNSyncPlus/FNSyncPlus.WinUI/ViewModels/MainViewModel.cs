using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.WinUI.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly AppSettings _settings;
        private readonly TaskManager _taskManager;
        private readonly SmbAuthService _smbService;

        [ObservableProperty]
        private string _appTitle = "飞牛同步增强版 v3.0";

        [ObservableProperty]
        private string _nasStatusText = "NAS 未连接 (点击登录)";

        [ObservableProperty]
        private bool _isNasConnected = false;

        [ObservableProperty]
        private string _pillBgColorHex = "#F1F5F9";

        [ObservableProperty]
        private string _pillTextColorHex = "#475569";

        [ObservableProperty]
        private string _selectedNavTag = "Tasks";

        [ObservableProperty]
        private bool _isInfoBarOpen = false;

        [ObservableProperty]
        private string _infoBarTitle = string.Empty;

        [ObservableProperty]
        private string _infoBarMessage = string.Empty;

        [ObservableProperty]
        private Microsoft.UI.Xaml.Controls.InfoBarSeverity _infoBarSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;

        public event Action<string>? RequestNavigate;
        public event Action? RequestToggleTheme;

        public MainViewModel(AppSettings settings, TaskManager taskManager, SmbAuthService smbService)
        {
            _settings = settings;
            _taskManager = taskManager;
            _smbService = smbService;

            UpdateNasConnectionStatus();
        }

        public void UpdateNasConnectionStatus()
        {
            string server = _settings.NasServer.Trim();
            string user = _settings.Username.Trim();

            if (!string.IsNullOrEmpty(server))
            {
                IsNasConnected = true;
                NasStatusText = $"{server} · {user} (SMB 已连接)";
                PillBgColorHex = "#DCFCE7"; // Light green
                PillTextColorHex = "#15803D"; // Deep green
            }
            else
            {
                IsNasConnected = false;
                NasStatusText = "NAS 未连接 (点击配置)";
                PillBgColorHex = "#FEF3C7"; // Light amber
                PillTextColorHex = "#B45309"; // Amber
            }
        }

        public void ShowNotification(string title, string message, Microsoft.UI.Xaml.Controls.InfoBarSeverity severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational)
        {
            InfoBarTitle = title;
            InfoBarMessage = message;
            InfoBarSeverity = severity;
            IsInfoBarOpen = true;
        }

        [RelayCommand]
        private void Navigate(string tag)
        {
            SelectedNavTag = tag;
            RequestNavigate?.Invoke(tag);
        }

        [RelayCommand]
        private void ToggleTheme()
        {
            RequestToggleTheme?.Invoke();
        }
    }
}
