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

        [ObservableProperty]
        private bool _isCertConfirmationRequired = false;

        [ObservableProperty]
        private string _untrustedCertFingerprint = string.Empty;

        [ObservableProperty]
        private string _untrustedCertSubject = string.Empty;

        public bool IsConnected => _connectionService.IsConnected;
        public int? CurrentUid => _connectionService.CurrentUid;
        public string PersonalRootDisplay => _connectionService.PersonalRootDisplay;
        public bool TwoFactorRequired => _connectionService.TwoFactorRequired;

        public event Action? RequestRefreshConnectionPill;
        public event Action? RequestNavigateToAddFolder;

        public DeviceViewModel(SmbAuthService smbService, AppSettings settings, TaskManager taskManager, NasConnectionService connectionService)
        {
            _smbService = smbService;
            _settings = settings;
            _taskManager = taskManager;
            _connectionService = connectionService;

            _connectionService.PropertyChanged += (s, e) =>
            {
                Helpers.UIThreadHelper.RunOnUIThread(() =>
                {
                    if (e.PropertyName == nameof(NasConnectionService.IsConnected) ||
                        e.PropertyName == nameof(NasConnectionService.CurrentUid) ||
                        e.PropertyName == nameof(NasConnectionService.PersonalRootDisplay) ||
                        e.PropertyName == nameof(NasConnectionService.TwoFactorRequired) ||
                        e.PropertyName == nameof(NasConnectionService.CertificateConfirmationRequired) ||
                        e.PropertyName == nameof(NasConnectionService.State))
                    {
                        OnPropertyChanged(nameof(IsConnected));
                        OnPropertyChanged(nameof(CurrentUid));
                        OnPropertyChanged(nameof(PersonalRootDisplay));
                        OnPropertyChanged(nameof(TwoFactorRequired));
                    }
                });
            };

            NasAddress = settings.NasServer;
            Username = settings.Username;
            RememberPassword = settings.RememberCredentials;

            if (settings.RememberCredentials && (!string.IsNullOrEmpty(settings.NasServer) || !string.IsNullOrEmpty(settings.IsolatedCredentialPath)))
            {
                var creds = SmbAuthService.LoadCredentials(settings.IsolatedCredentialPath);
                if (creds.HasValue && !string.IsNullOrEmpty(creds.Value.Password))
                {
                    if (!string.IsNullOrEmpty(settings.IsolatedCredentialPath))
                    {
                        NasAddress = creds.Value.Server;
                        Username = creds.Value.Username;
                    }
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
            IsCertConfirmationRequired = false;
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

                if (!success && _connectionService.CertificateConfirmationRequired && _connectionService.UntrustedCertificateInfo != null)
                {
                    IsCertConfirmationRequired = true;
                    UntrustedCertFingerprint = _connectionService.UntrustedCertificateInfo.Thumbprint;
                    UntrustedCertSubject = $"{_connectionService.UntrustedCertificateInfo.Subject} (颁发者: {_connectionService.UntrustedCertificateInfo.Issuer}) 有效期至: {_connectionService.UntrustedCertificateInfo.NotAfter:yyyy-MM-dd}";
                }

                RequestRefreshConnectionPill?.Invoke();

                if (success && _connectionService.PendingWizardOnConnected)
                {
                    _connectionService.PendingWizardOnConnected = false;
                    RequestNavigateToAddFolder?.Invoke();
                }
            }
            catch (Exception ex)
            {
                IsSuccess = false;
                StatusMessage = $"连接异常 ({ex.GetType().Name}): {ex.Message}";
                App.LogCrashException(ex, "DeviceViewModel.LoginAsync");
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task TrustCertificateAndConnectAsync()
        {
            if (_connectionService.UntrustedCertificateInfo != null)
            {
                _connectionService.TrustCurrentCertificate();
                IsCertConfirmationRequired = false;
                await LoginAsync();
            }
        }

        [RelayCommand]
        public void CancelCertificateTrust()
        {
            _connectionService.ClearUntrustedCertificate();
            _connectionService.PendingWizardOnConnected = false;
            _connectionService.PendingOpenLocalFolderPicker = false;
            IsCertConfirmationRequired = false;
            StatusMessage = "已取消证书信任，连接中止。";
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

                if (success && _connectionService.PendingWizardOnConnected)
                {
                    _connectionService.PendingWizardOnConnected = false;
                    RequestNavigateToAddFolder?.Invoke();
                }
            }
            catch (Exception ex)
            {
                IsSuccess = false;
                StatusMessage = $"两步验证异常: {ex.Message}";
                App.LogCrashException(ex, "DeviceViewModel.SubmitTwoFactorCodeAsync");
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task DisconnectAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            try
            {
                _connectionService.PendingWizardOnConnected = false;
                _connectionService.PendingOpenLocalFolderPicker = false;
                await _connectionService.DisconnectAsync();
                IsSuccess = false;
                IsCertConfirmationRequired = false;
                StatusMessage = "已断开与 NAS 的连接。";
                RequestRefreshConnectionPill?.Invoke();
            }
            catch (Exception ex)
            {
                StatusMessage = $"断开异常: {ex.Message}";
                App.LogCrashException(ex, "DeviceViewModel.DisconnectAsync");
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
