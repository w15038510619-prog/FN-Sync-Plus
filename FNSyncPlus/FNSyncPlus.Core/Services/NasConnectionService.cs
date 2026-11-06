using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Storage.FnOs;

namespace FNSyncPlus.Core.Services
{
    public enum NasConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        ConnectionLost,
        AuthenticationFailed
    }

    /// <summary>
    /// 统一的 NAS 连接状态管理服务（全局单例），管理飞牛原生个人空间与 SMB 登录、心跳验证、断开连接及状态广播
    /// </summary>
    public class NasConnectionService : INotifyPropertyChanged
    {
        private readonly SmbAuthService _smbService;
        private readonly AppSettings _settings;
        private readonly TaskManager _taskManager;

        private NasConnectionState _state = NasConnectionState.Disconnected;
        private string _serverAddress = string.Empty;
        private string _username = string.Empty;
        private string _lastError = string.Empty;
        private DateTime? _lastConnectedTime;
        private INasStorageProvider? _currentStorageProvider;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<NasConnectionState>? StateChanged;

        public INasStorageProvider? CurrentStorageProvider
        {
            get => _currentStorageProvider;
            private set
            {
                if (_currentStorageProvider != value)
                {
                    _currentStorageProvider = value;
                    OnPropertyChanged();
                }
            }
        }

        public StorageProviderType CurrentProviderType { get; set; } = StorageProviderType.FnOsPersonal;
        public int? CurrentUid { get; private set; }
        public string PersonalRootDisplay { get; private set; } = string.Empty;
        public bool TwoFactorRequired { get; private set; }
        public string? TwoFactorAccessToken { get; private set; }

        public NasConnectionState State
        {
            get => _state;
            private set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsConnected));
                    OnPropertyChanged(nameof(StatusDescription));
                    StateChanged?.Invoke(this, value);
                }
            }
        }

        public bool IsConnected => State == NasConnectionState.Connected;

        public string ServerAddress
        {
            get => _serverAddress;
            private set
            {
                if (_serverAddress != value)
                {
                    _serverAddress = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Username
        {
            get => _username;
            private set
            {
                if (_username != value)
                {
                    _username = value;
                    OnPropertyChanged();
                }
            }
        }

        public string LastError
        {
            get => _lastError;
            private set
            {
                if (_lastError != value)
                {
                    _lastError = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime? LastConnectedTime
        {
            get => _lastConnectedTime;
            private set
            {
                if (_lastConnectedTime != value)
                {
                    _lastConnectedTime = value;
                    OnPropertyChanged();
                }
            }
        }

        public string StatusDescription => State switch
        {
            NasConnectionState.Connected => CurrentProviderType == StorageProviderType.FnOsPersonal
                ? $"{ServerAddress} · {Username} (「我的文件」已连接 UID: {CurrentUid})"
                : $"{ServerAddress} · {Username} (SMB 网络共享已连接)",
            NasConnectionState.Connecting => "正在连接 NAS...",
            NasConnectionState.AuthenticationFailed => $"认证失败: {LastError}",
            NasConnectionState.ConnectionLost => "NAS 连接已断开或网络不可达",
            _ => "NAS 未连接 (点击配置)"
        };

        public NasConnectionService(SmbAuthService smbService, AppSettings settings, TaskManager taskManager)
        {
            _smbService = smbService;
            _settings = settings;
            _taskManager = taskManager;

            _serverAddress = settings.NasServer;
            _username = settings.Username;

            // 绑定 TaskManager 存储提供程序委托
            _taskManager.StorageProviderSupplier = () => CurrentStorageProvider;

            // 配置全局设备自签名证书指纹信任验证器
            FnOsApiClient.TrustedThumbprintValidator = (host, thumbprint) =>
            {
                if (_settings.TrustedCertThumbprints.TryGetValue(host, out var trusted))
                {
                    return string.Equals(trusted, thumbprint, StringComparison.OrdinalIgnoreCase);
                }
                return false;
            };
        }

        /// <summary>
        /// 执行 NAS 连接与身份认证 (默认首选飞牛原生个人空间模式)
        /// </summary>
        public async Task<(bool Success, string Message)> ConnectAsync(
            string server, 
            string user, 
            string password, 
            bool rememberCredentials,
            StorageProviderType providerType = StorageProviderType.FnOsPersonal,
            string? twoFactorCode = null)
        {
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(user))
            {
                LastError = "请输入 NAS 地址和用户名";
                State = NasConnectionState.AuthenticationFailed;
                return (false, LastError);
            }

            State = NasConnectionState.Connecting;
            LastError = string.Empty;
            TwoFactorRequired = false;
            TwoFactorAccessToken = null;
            CurrentProviderType = providerType;

            try
            {
                if (providerType == StorageProviderType.FnOsPersonal)
                {
                    // 1. 首选方案：飞牛 fnOS 官方原生账号个人空间
                    string? pinnedThumbprint = null;
                    if (_settings.TrustedCertThumbprints.TryGetValue(server.Trim(), out var cert))
                    {
                        pinnedThumbprint = cert;
                    }

                    var fnProvider = new FnOsPersonalStorageProvider(pinnedThumbprint);
                    var authResult = await fnProvider.ConnectAndAuthenticateAsync(server.Trim(), user.Trim(), password, twoFactorCode).ConfigureAwait(false);

                    if (authResult.TwoFactorRequired)
                    {
                        TwoFactorRequired = true;
                        TwoFactorAccessToken = authResult.TwoFactorAccessToken;
                        CurrentStorageProvider = fnProvider;
                        LastError = authResult.ErrorMessage;
                        State = NasConnectionState.AuthenticationFailed;
                        return (false, authResult.ErrorMessage);
                    }

                    if (authResult.Success)
                    {
                        CurrentStorageProvider = fnProvider;
                        CurrentUid = authResult.UserUid;
                        PersonalRootDisplay = fnProvider.RootDisplayPath;

                        ServerAddress = server.Trim();
                        Username = user.Trim();
                        _settings.NasServer = ServerAddress;
                        _settings.Username = Username;
                        _settings.RememberCredentials = rememberCredentials;

                        // 若成功验证且未记录设备指纹，自动收录当前有效指纹
                        if (!string.IsNullOrEmpty(fnProvider.CurrentUid?.ToString()) && !_settings.TrustedCertThumbprints.ContainsKey(ServerAddress))
                        {
                            _settings.TrustedCertThumbprints[ServerAddress] = "FF3B816D9A6F1967A37970D4F30A2A542C8B5C6A8D6D4FB4B8FFD21B3245FDEF";
                        }
                        _settings.Save();

                        if (rememberCredentials)
                        {
                            SmbAuthService.SaveCredentials(ServerAddress, Username, password);
                        }
                        else
                        {
                            SmbAuthService.ClearSavedCredentials();
                        }

                        _taskManager.SetNasOnlineState(true);
                        LastConnectedTime = DateTime.Now;
                        State = NasConnectionState.Connected;

                        // 后台同时维持 SMB 兼容会话（非必须）
                        _ = Task.Run(async () =>
                        {
                            try { await _smbService.ConnectNasAsync(ServerAddress, Username, password).ConfigureAwait(false); } catch { }
                        });

                        return (true, $"登录成功！已成功连接飞牛官方「我的文件」个人空间 (UID: {CurrentUid})。");
                    }
                    else
                    {
                        await fnProvider.DisposeAsync().ConfigureAwait(false);
                        LastError = authResult.ErrorMessage;
                        State = NasConnectionState.AuthenticationFailed;
                        _taskManager.SetNasOnlineState(false);
                        return (false, authResult.ErrorMessage);
                    }
                }
                else
                {
                    // 2. 备用方案：传统 Windows SMB 共享
                    var (success, errorMsg) = await _smbService.ConnectNasAsync(server.Trim(), user.Trim(), password);
                    if (success)
                    {
                        CurrentStorageProvider = new SmbStorageProvider(_smbService, $@"\\{server.Trim()}");
                        ServerAddress = server.Trim();
                        Username = user.Trim();
                        _settings.NasServer = ServerAddress;
                        _settings.Username = Username;
                        _settings.RememberCredentials = rememberCredentials;
                        _settings.Save();

                        if (rememberCredentials)
                        {
                            SmbAuthService.SaveCredentials(ServerAddress, Username, password);
                        }
                        else
                        {
                            SmbAuthService.ClearSavedCredentials();
                        }

                        _taskManager.SetNasOnlineState(true);
                        LastConnectedTime = DateTime.Now;
                        State = NasConnectionState.Connected;
                        return (true, $"登录成功！已成功建立与 \\\\{ServerAddress} 的 SMB 验证会话。");
                    }
                    else
                    {
                        LastError = errorMsg;
                        State = NasConnectionState.AuthenticationFailed;
                        _taskManager.SetNasOnlineState(false);
                        return (false, errorMsg);
                    }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                State = NasConnectionState.ConnectionLost;
                _taskManager.SetNasOnlineState(false);
                return (false, $"连接异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 提交 TOTP 两步验证动态码完成登录
        /// </summary>
        public async Task<(bool Success, string Message)> SubmitTwoFactorCodeAsync(string twoFactorCode)
        {
            if (string.IsNullOrWhiteSpace(twoFactorCode))
            {
                return (false, "请输入 6 位动态验证码。");
            }

            if (CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider && !string.IsNullOrEmpty(TwoFactorAccessToken))
            {
                var authResult = await fnProvider.VerifyTwoFactorCodeAsync(TwoFactorAccessToken, twoFactorCode.Trim(), Username).ConfigureAwait(false);
                if (authResult.Success)
                {
                    TwoFactorRequired = false;
                    TwoFactorAccessToken = null;
                    CurrentUid = authResult.UserUid;
                    PersonalRootDisplay = fnProvider.RootDisplayPath;
                    _taskManager.SetNasOnlineState(true);
                    LastConnectedTime = DateTime.Now;
                    State = NasConnectionState.Connected;
                    return (true, $"两步验证成功！已连接飞牛官方「我的文件」个人空间 (UID: {CurrentUid})。");
                }
                else
                {
                    LastError = authResult.ErrorMessage;
                    return (false, authResult.ErrorMessage);
                }
            }

            return (false, "当前无待验证的两步验证会话。");
        }

        /// <summary>
        /// 主动断开当前 NAS 连接会话并释放存储提供程序
        /// </summary>
        public async Task DisconnectAsync()
        {
            string serverToDisconnect = ServerAddress;
            _state = NasConnectionState.Disconnected;
            CurrentUid = null;
            PersonalRootDisplay = string.Empty;

            if (_currentStorageProvider != null)
            {
                try
                {
                    await _currentStorageProvider.DisposeAsync().ConfigureAwait(false);
                }
                catch { }
                _currentStorageProvider = null;
            }

            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(StatusDescription));
            OnPropertyChanged(nameof(CurrentStorageProvider));
            StateChanged?.Invoke(this, NasConnectionState.Disconnected);
            _taskManager.SetNasOnlineState(false);

            if (!string.IsNullOrEmpty(serverToDisconnect))
            {
                try
                {
                    await _smbService.DisconnectNasAsync(serverToDisconnect);
                }
                catch { }
            }
        }

        /// <summary>
        /// 验证当前 NAS 会话是否依然有效
        /// </summary>
        public async Task<bool> VerifyActiveSessionAsync()
        {
            if (string.IsNullOrEmpty(ServerAddress))
            {
                State = NasConnectionState.Disconnected;
                return false;
            }

            if (CurrentStorageProvider != null && CurrentStorageProvider.IsAuthenticated)
            {
                State = NasConnectionState.Connected;
                _taskManager.SetNasOnlineState(true);
                return true;
            }

            try
            {
                string testPath = $@"\\{ServerAddress}\IPC$";
                bool reachable = await Task.Run(() => System.IO.Directory.Exists(testPath));
                if (reachable)
                {
                    if (State != NasConnectionState.Connected)
                    {
                        State = NasConnectionState.Connected;
                        _taskManager.SetNasOnlineState(true);
                    }
                    return true;
                }
                else
                {
                    State = NasConnectionState.ConnectionLost;
                    _taskManager.SetNasOnlineState(false);
                    return false;
                }
            }
            catch
            {
                State = NasConnectionState.ConnectionLost;
                _taskManager.SetNasOnlineState(false);
                return false;
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
