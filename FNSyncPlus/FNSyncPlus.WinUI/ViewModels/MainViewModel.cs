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
        private string _appTitle = $"飞牛同步增强版 v{FNSyncPlus.Core.Updates.UpdateService.DisplayVersion}";

        [ObservableProperty]
        private string _nasStatusText = "NAS 未连接";

        [ObservableProperty]
        private string _nasStatusTooltip = "点击配置飞牛 NAS 设备连接";

        [ObservableProperty]
        private string _nasStatusTone = "Neutral";

        [ObservableProperty]
        private NasConnectionState _connectionState = NasConnectionState.Disconnected;

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
                Helpers.UIThreadHelper.RunOnUIThread(() =>
                {
                    if (e.PropertyName == nameof(NasConnectionService.State) ||
                        e.PropertyName == nameof(NasConnectionService.ServerAddress) ||
                        e.PropertyName == nameof(NasConnectionService.CurrentProviderType))
                    {
                        UpdateNasConnectionStatus();
                    }
                });
            };

            UpdateNasConnectionStatus();
        }

        public void UpdateNasConnectionStatus()
        {
            ConnectionState = _connectionService.State;
            switch (_connectionService.State)
            {
                case NasConnectionState.Connected:
                    IsNasConnected = true;
                    NasStatusTone = "Success";
                    NasStatusText = $"{_connectionService.ServerAddress} · {_connectionService.Username}";
                    NasStatusTooltip = _connectionService.CurrentProviderType == FNSyncPlus.Core.Storage.StorageProviderType.FnOsPersonal
                        ? $"已连接飞牛官方个人空间 ({_connectionService.ServerAddress} · {_connectionService.Username})，点击管理设备"
                        : $"已连接 NAS SMB 共享 ({_connectionService.ServerAddress} · {_connectionService.Username})，点击管理设备";
                    PillBgColorHex = "#DCFCE7";
                    PillTextColorHex = "#15803D";
                    break;
                case NasConnectionState.Connecting:
                    IsNasConnected = false;
                    NasStatusTone = "Warning";
                    NasStatusText = "正在连接 NAS…";
                    NasStatusTooltip = "正在建立飞牛 NAS 安全连接";
                    PillBgColorHex = "#FEF3C7";
                    PillTextColorHex = "#B45309";
                    break;
                case NasConnectionState.AuthenticationFailed:
                    IsNasConnected = false;
                    NasStatusTone = "Error";
                    NasStatusText = "认证失败 · 点击处理";
                    NasStatusTooltip = "飞牛 NAS 登录认证失败，点击重新登录";
                    PillBgColorHex = "#FEE2E2";
                    PillTextColorHex = "#B91C1C";
                    break;
                case NasConnectionState.ConnectionLost:
                    IsNasConnected = false;
                    NasStatusTone = "Error";
                    NasStatusText = "连接已断开 · 点击重连";
                    NasStatusTooltip = "与飞牛 NAS 的连接中断，点击检查网络或重连";
                    PillBgColorHex = "#FEE2E2";
                    PillTextColorHex = "#B91C1C";
                    break;
                case NasConnectionState.Disconnected:
                default:
                    IsNasConnected = false;
                    NasStatusTone = "Neutral";
                    NasStatusText = "NAS 未连接";
                    NasStatusTooltip = "尚未连接飞牛 NAS，点击前往配置";
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
