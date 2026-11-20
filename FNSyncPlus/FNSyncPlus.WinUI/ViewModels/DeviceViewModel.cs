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
        [NotifyPropertyChangedFor(nameof(CanChangeRememberPassword))]
        private bool _isBusy = false;

        public bool CanChangeRememberPassword => !IsBusy;

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
        public bool ShowLoginForm => !IsConnected || EditingConnection;
        public bool ShowConnectedDashboard => IsConnected && !EditingConnection;
        public bool IsSwitchingAccount => IsConnected && EditingConnection;
        public bool ConnectionHealthy => IsConnected;
        public string ConnectedServerDisplay => !string.IsNullOrWhiteSpace(_connectionService.ServerAddress) ? _connectionService.ServerAddress : NasAddress;
        public string ConnectedUserDisplay => !string.IsNullOrWhiteSpace(_connectionService.Username) ? _connectionService.Username : Username;
        public string TlsSummaryDisplay => _settings.TrustedCertThumbprints.Count > 0
            ? $"已通过 TLS 加密连接（已信任 {_settings.TrustedCertThumbprints.Count} 个证书指纹）"
            : "已通过 TLS 加密连接";
        public string ConnectionMessage => IsConnected && !EditingConnection ? "" :
            _connectionService.State == NasConnectionState.Connecting ? "正在建立安全连接并校验设备证书…" :
            _connectionService.State == NasConnectionState.ConnectionLost ? "与 NAS 的网络连接已中断，请检查网络或重新连接。" :
            _connectionService.State == NasConnectionState.AuthenticationFailed ? (string.IsNullOrWhiteSpace(StatusMessage) ? "账号或密码验证失败，请核对后重试。" : StatusMessage) : StatusMessage;
        public bool ShowConnectionInfoBar => (!IsConnected || EditingConnection) && !string.IsNullOrWhiteSpace(ConnectionMessage);
        public Microsoft.UI.Xaml.Controls.InfoBarSeverity ConnectionInfoBarSeverity
        {
            get
            {
                if (IsBusy || _connectionService.State == NasConnectionState.Connecting)
                    return Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
                if (IsCertConfirmationRequired)
                    return Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
                if (_connectionService.State == NasConnectionState.AuthenticationFailed ||
                    _connectionService.State == NasConnectionState.ConnectionLost)
                    return Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
                if (StatusMessage.Contains("已断开") || StatusMessage.Contains("已取消"))
                    return Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
                if (StatusMessage.Contains("请输入"))
                    return Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
                if (StatusMessage.Contains("失败") || StatusMessage.Contains("异常") || StatusMessage.Contains("超时") || StatusMessage.Contains("无法"))
                    return Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
                return Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
            }
        }
        public string ConnectionInfoBarTitle
        {
            get
            {
                if (IsBusy || _connectionService.State == NasConnectionState.Connecting) return "正在连接";
                if (IsCertConfirmationRequired) return "待核对设备证书";
                if (_connectionService.State == NasConnectionState.AuthenticationFailed) return "认证失败";
                if (_connectionService.State == NasConnectionState.ConnectionLost) return "网络连接异常";
                if (StatusMessage.Contains("已断开") || StatusMessage.Contains("已取消")) return "连接状态";
                if (StatusMessage.Contains("请输入")) return "请完善登录信息";
                if (StatusMessage.Contains("失败") || StatusMessage.Contains("异常") || StatusMessage.Contains("超时") || StatusMessage.Contains("无法")) return "连接失败";
                return "提示";
            }
        }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowLoginForm))]
        [NotifyPropertyChangedFor(nameof(ShowConnectedDashboard))]
        [NotifyPropertyChangedFor(nameof(IsSwitchingAccount))]
        [NotifyPropertyChangedFor(nameof(ShowConnectionInfoBar))]
        [NotifyPropertyChangedFor(nameof(ConnectionMessage))]
        private bool _editingConnection;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowEmptyDiscovery))]
        private bool _isSearching;
        [ObservableProperty] private string _discoveryMessage = "";
        public bool HasDiscoveredDevices => DiscoveredDevices.Count > 0;
        public bool ShowEmptyDiscovery => !IsSearching && DiscoveredDevices.Count == 0;
        private int _searchGeneration;

        partial void OnStatusMessageChanged(string value)
        {
            OnPropertyChanged(nameof(ConnectionMessage));
            OnPropertyChanged(nameof(ShowConnectionInfoBar));
            OnPropertyChanged(nameof(ConnectionInfoBarSeverity));
            OnPropertyChanged(nameof(ConnectionInfoBarTitle));
        }

        partial void OnIsBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(ConnectionInfoBarSeverity));
            OnPropertyChanged(nameof(ConnectionInfoBarTitle));
        }

        partial void OnIsCertConfirmationRequiredChanged(bool value)
        {
            OnPropertyChanged(nameof(ConnectionInfoBarSeverity));
            OnPropertyChanged(nameof(ConnectionInfoBarTitle));
        }

        [RelayCommand] private void SwitchAccount() { EditingConnection = true; Password = ""; StatusMessage = ""; }
        [RelayCommand] private void CancelSwitchAccount() { EditingConnection = false; StatusMessage = ""; }
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

            DiscoveredDevices.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasDiscoveredDevices));
                OnPropertyChanged(nameof(ShowEmptyDiscovery));
            };

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
                        if (IsConnected) EditingConnection = false;
                        OnPropertyChanged(nameof(IsConnected));
                        OnPropertyChanged(nameof(CurrentUid));
                        OnPropertyChanged(nameof(PersonalRootDisplay));
                        OnPropertyChanged(nameof(ConnectedServerDisplay));
                        OnPropertyChanged(nameof(ConnectedUserDisplay));
                        OnPropertyChanged(nameof(TlsSummaryDisplay));
                        OnPropertyChanged(nameof(TwoFactorRequired));
                        OnPropertyChanged(nameof(ConnectionHealthy));
                        OnPropertyChanged(nameof(ConnectionMessage));
                        OnPropertyChanged(nameof(ShowLoginForm));
                        OnPropertyChanged(nameof(ShowConnectedDashboard));
                        OnPropertyChanged(nameof(IsSwitchingAccount));
                        OnPropertyChanged(nameof(ShowConnectionInfoBar));
                        OnPropertyChanged(nameof(ConnectionInfoBarSeverity));
                        OnPropertyChanged(nameof(ConnectionInfoBarTitle));
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
            int generation = System.Threading.Interlocked.Increment(ref _searchGeneration);
            if (Services.AppServices.IsUiVisualAcceptance)
            {
                Helpers.UIThreadHelper.RunOnUIThread(() =>
                {
                    DiscoveredDevices.Clear();
                    IsSearching = false;
                    DiscoveryMessage = "纯视觉验收模式（未执行局域网扫描）";
                });
                return;
            }
            Helpers.UIThreadHelper.RunOnUIThread(() =>
            {
                IsSearching = true;
                DiscoveryMessage = "正在搜索设备…";
            });
            try
            {
                var devices = await _discoveryService.DiscoverNasDevicesAsync(NasAddress.Trim());
                if (generation != _searchGeneration) return;
                Helpers.UIThreadHelper.RunOnUIThread(() =>
                {
                    if (generation != _searchGeneration) return;
                    DiscoveredDevices.Clear();
                    foreach (var d in devices)
                    {
                        DiscoveredDevices.Add(d);
                    }

                    DiscoveryMessage = $"找到 {DiscoveredDevices.Count} 台设备。";
                });
            }
            catch (Exception ex)
            {
                if (generation == _searchGeneration)
                {
                    Helpers.UIThreadHelper.RunOnUIThread(() => DiscoveryMessage = "设备搜索失败，可以直接填写 NAS 地址连接。");
                }
                App.LogCrashException(ex, "Device.Discovery");
            }
            finally
            {
                if (generation == _searchGeneration)
                {
                    Helpers.UIThreadHelper.RunOnUIThread(() => IsSearching = false);
                }
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
                var (success, msg) = await _connectionService.SubmitTwoFactorCodeAsync(TwoFactorCode, RememberPassword);
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
