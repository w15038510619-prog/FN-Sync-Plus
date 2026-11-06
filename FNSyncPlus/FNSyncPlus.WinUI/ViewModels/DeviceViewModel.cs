using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.WinUI.ViewModels
{
    public partial class DeviceViewModel : ObservableObject
    {
        private readonly NasDiscoveryService _discoveryService = new();
        private readonly SmbAuthService _smbService;
        private readonly AppSettings _settings;
        private readonly TaskManager _taskManager;
        private readonly NasConnectionService _connectionService;

        public ObservableCollection<DiscoveredDevice> DiscoveredDevices { get; } = new();

        [ObservableProperty]
        private DiscoveredDevice? _selectedDevice;

        partial void OnSelectedDeviceChanged(DiscoveredDevice? value)
        {
            if (value != null && !string.IsNullOrWhiteSpace(value.IpAddress))
            {
                NasAddress = value.IpAddress;
            }
        }

        [ObservableProperty]
        private string _nasAddress = string.Empty;

        [ObservableProperty]
        private string _username = string.Empty;

        [ObservableProperty]
        private string _password = string.Empty;

        [ObservableProperty]
        private bool _rememberPassword = true;

        [ObservableProperty]
        private string _statusMessage = string.Empty;

        [ObservableProperty]
        private bool _isBusy = false;

        [ObservableProperty]
        private bool _isSuccess = false;

        [ObservableProperty]
        private string _twoFactorCode = string.Empty;

        public bool IsConnected => _connectionService.IsConnected;
        public int? CurrentUid => _connectionService.CurrentUid;
        public string PersonalRootDisplay => _connectionService.PersonalRootDisplay;
        public bool TwoFactorRequired => _connectionService.TwoFactorRequired;

        public event Action? RequestRefreshConnectionPill;

        public DeviceViewModel(SmbAuthService smbService, AppSettings settings, TaskManager taskManager, NasConnectionService connectionService)
        {
            _smbService = smbService;
            _settings = settings;
            _taskManager = taskManager;
            _connectionService = connectionService;

            _connectionService.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NasConnectionService.IsConnected) ||
                    e.PropertyName == nameof(NasConnectionService.CurrentUid) ||
                    e.PropertyName == nameof(NasConnectionService.PersonalRootDisplay) ||
                    e.PropertyName == nameof(NasConnectionService.TwoFactorRequired))
                {
                    OnPropertyChanged(nameof(IsConnected));
                    OnPropertyChanged(nameof(CurrentUid));
                    OnPropertyChanged(nameof(PersonalRootDisplay));
                    OnPropertyChanged(nameof(TwoFactorRequired));
                }
            };

            NasAddress = settings.NasServer;
            Username = settings.Username;
            RememberPassword = settings.RememberCredentials;

            if (settings.RememberCredentials && !string.IsNullOrEmpty(settings.NasServer))
            {
                var creds = SmbAuthService.LoadCredentials();
                if (creds.HasValue && !string.IsNullOrEmpty(creds.Value.Password))
                {
                    Password = creds.Value.Password;
                }
            }

            _ = SearchLanDevicesAsync();
        }

        [RelayCommand]
        public async Task SearchLanDevicesAsync()
        {
            IsBusy = true;
            StatusMessage = "正在探测局域网中的飞牛 NAS 设备...";
            try
            {
                var devices = await _discoveryService.DiscoverNasDevicesAsync(NasAddress.Trim());
                DiscoveredDevices.Clear();
                foreach (var d in devices)
                {
                    DiscoveredDevices.Add(d);
                }

                StatusMessage = $"搜索完成，找到 {DiscoveredDevices.Count} 台局域网设备。";
            }
            catch (Exception ex)
            {
                StatusMessage = $"搜索异常: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task LoginAsync()
        {
            if (string.IsNullOrWhiteSpace(NasAddress) || string.IsNullOrWhiteSpace(Username))
            {
                StatusMessage = "请输入 NAS 地址和用户名。";
                IsSuccess = false;
                return;
            }

            IsBusy = true;
            StatusMessage = "正在建立飞牛官方原生个人空间安全连接并验证 TLS 证书指纹...";
            IsSuccess = false;

            try
            {
                var (success, msg) = await _connectionService.ConnectAsync(
                    NasAddress.Trim(),
                    Username.Trim(),
                    Password,
                    RememberPassword);

                IsSuccess = success;
                StatusMessage = msg;
                RequestRefreshConnectionPill?.Invoke();
            }
            catch (Exception ex)
            {
                IsSuccess = false;
                StatusMessage = $"连接异常: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task SubmitTwoFactorCodeAsync()
        {
            if (string.IsNullOrWhiteSpace(TwoFactorCode))
            {
                StatusMessage = "请输入 6 位动态验证码。";
                return;
            }

            IsBusy = true;
            try
            {
                var (success, msg) = await _connectionService.SubmitTwoFactorCodeAsync(TwoFactorCode);
                IsSuccess = success;
                StatusMessage = msg;
                if (success)
                {
                    TwoFactorCode = string.Empty;
                }
                RequestRefreshConnectionPill?.Invoke();
            }
            catch (Exception ex)
            {
                IsSuccess = false;
                StatusMessage = $"两步验证异常: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task DisconnectAsync()
        {
            IsBusy = true;
            try
            {
                await _connectionService.DisconnectAsync();
                IsSuccess = false;
                StatusMessage = "已断开当前 NAS SMB 会话。";
                RequestRefreshConnectionPill?.Invoke();
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
