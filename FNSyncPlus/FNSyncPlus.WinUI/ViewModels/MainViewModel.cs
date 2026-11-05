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
        private readonly NasConnectionService _connectionService;

        [ObservableProperty]
        private string _appTitle = "飞牛同步增强版 v0.4.0-beta";

        [ObservableProperty]
        private string _nasStatusText = "NAS 未连接 (点击配置)";

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

        public MainViewModel(AppSettings settings, TaskManager taskManager, SmbAuthService smbService, NasConnectionService connectionService)
        {
            _settings = settings;
            _taskManager = taskManager;
            _smbService = smbService;
            _connectionService = connectionService;

            _connectionService.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NasConnectionService.State) ||
                    e.PropertyName == nameof(NasConnectionService.ServerAddress))
                {
                    UpdateNasConnectionStatus();
                }
            };

            UpdateNasConnectionStatus();
        }

        public void UpdateNasConnectionStatus()
        {
            switch (_connectionService.State)
            {
                case NasConnectionState.Connected:
                    IsNasConnected = true;
                    NasStatusText = $"{_connectionService.ServerAddress} · {_connectionService.Username} (SMB 已连接)";
                    PillBgColorHex = "#DCFCE7";
                    PillTextColorHex = "#15803D";
                    break;
                case NasConnectionState.Connecting:
                    IsNasConnected = false;
                    NasStatusText = "🟡 正在连接 NAS SMB...";
                    PillBgColorHex = "#FEF3C7";
                    PillTextColorHex = "#B45309";
                    break;
                case NasConnectionState.AuthenticationFailed:
                    IsNasConnected = false;
                    NasStatusText = "▲ 认证失败 (点击配置)";
                    PillBgColorHex = "#FEE2E2";
                    PillTextColorHex = "#B91C1C";
                    break;
                case NasConnectionState.ConnectionLost:
                    IsNasConnected = false;
                    NasStatusText = "▲ NAS 会话断开 (点击配置)";
                    PillBgColorHex = "#FEE2E2";
                    PillTextColorHex = "#B91C1C";
                    break;
                case NasConnectionState.Disconnected:
                default:
                    IsNasConnected = false;
                    NasStatusText = "🔴 NAS 未连接 (点击配置)";
                    PillBgColorHex = "#F1F5F9";
                    PillTextColorHex = "#64748B";
                    break;
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
