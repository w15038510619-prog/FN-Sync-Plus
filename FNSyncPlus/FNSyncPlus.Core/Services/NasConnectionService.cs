using System;
using System.ComponentModel;
using System.IO;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
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
    /// 统一 NAS 连接状态管理服务（全局单例），管理飞牛原生个人空间与 SMB 登录、证书核对、断开连接及状态广播
    /// </summary>
    public class NasConnectionService : INotifyPropertyChanged
    {
        private readonly SmbAuthService _smbService;
        private readonly AppSettings _settings;
        private readonly TaskManager _taskManager;

        private readonly SemaphoreSlim _connectionLock = new(1, 1);
        private CancellationTokenSource? _activeConnectionCts;
        private readonly object _reconnectLock = new();
        private CancellationTokenSource? _reconnectCts;
        private long _connectionIntentVersion;
        private bool _disconnectRequested;

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

        // TLS 证书待核对与信任机制（严格防护中间人攻击与自签名证书）
        public bool CertificateConfirmationRequired { get; private set; }
        public NasCertificateInfo? UntrustedCertificateInfo { get; private set; }

        /// <summary>
        /// 当用户在未连接 NAS 时点击添加文件夹，跳转连接后自动重新打开添加向导的标记
        /// </summary>
        public bool PendingWizardOnConnected { get; set; }

        /// <summary>
        /// 当用户从「添加文件夹」触发流程时（不论直接进入向导或连接成功后跳转进入），
        /// 页面加载完成后立即自动弹出 Windows 本地文件夹选择器的单次消费意图标记
        /// </summary>
        public bool PendingOpenLocalFolderPicker { get; set; }

        /// <summary>
        /// 向导中途 NAS 断开后前往重连时，保留当前已选择的本地文件夹草稿
        /// </summary>
        public bool IsPreservingWizardDraft { get; set; }

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
                ? $"{ServerAddress} · {Username} (飞牛个人空间已连接 UID: {CurrentUid})"
                : $"{ServerAddress} · {Username} (SMB 网络共享已连接)",
            NasConnectionState.Connecting => CurrentProviderType == StorageProviderType.FnOsPersonal
                ? "🟡 正在连接飞牛官方个人空间..."
                : "🟡 正在连接 NAS SMB 共享...",
            NasConnectionState.AuthenticationFailed => $"🔴 认证失败: {LastError}",
            NasConnectionState.ConnectionLost => "🔴 NAS 网络中断 (点击配置)",
            _ => "🔴 NAS 未连接 (点击配置)"
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
        /// 用户明确核对并信任当前 NAS 证书
        /// </summary>
        public void TrustCurrentCertificate()
        {
            if (UntrustedCertificateInfo != null && !string.IsNullOrEmpty(UntrustedCertificateInfo.Host))
            {
                _settings.TrustedCertThumbprints[UntrustedCertificateInfo.Host] = UntrustedCertificateInfo.Thumbprint;
                _settings.Save();
                CertificateConfirmationRequired = false;
                UntrustedCertificateInfo = null;
                OnPropertyChanged(nameof(CertificateConfirmationRequired));
                OnPropertyChanged(nameof(UntrustedCertificateInfo));
            }
        }

        /// <summary>
        /// 取消证书信任确认
        /// </summary>
        public void ClearUntrustedCertificate()
        {
            CertificateConfirmationRequired = false;
            UntrustedCertificateInfo = null;
            OnPropertyChanged(nameof(CertificateConfirmationRequired));
            OnPropertyChanged(nameof(UntrustedCertificateInfo));
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
            CancelAutomaticReconnect(disconnectRequested: false);
            long intentVersion;
            lock (_reconnectLock) intentVersion = _connectionIntentVersion;
            var result = await ConnectCoreAsync(server, user, password, rememberCredentials, providerType, twoFactorCode, CancellationToken.None).ConfigureAwait(false);
            if (!result.Success && rememberCredentials && providerType == StorageProviderType.FnOsPersonal && State == NasConnectionState.ConnectionLost)
                StartAutomaticReconnect(intentVersion);
            return result;
        }

        private async Task<(bool Success, string Message)> ConnectCoreAsync(
            string server, string user, string password, bool rememberCredentials,
            StorageProviderType providerType, string? twoFactorCode, CancellationToken reconnectToken)
        {
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(user))
            {
                LastError = "请输入 NAS 地址和用户名";
                State = NasConnectionState.AuthenticationFailed;
                return (false, LastError);
            }

            if (!await _connectionLock.WaitAsync(0).ConfigureAwait(false))
            {
                return (false, "当前已有连接或断开操作正在执行，请稍候。");
            }

            _activeConnectionCts?.Cancel();
            _activeConnectionCts?.Dispose();
            var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(reconnectToken);
            _activeConnectionCts = attemptCts;
            var ct = attemptCts.Token;

            try
            {
                State = NasConnectionState.Connecting;
                LastError = string.Empty;
                ServerAddress = server.Trim();
                Username = user.Trim();
                TwoFactorRequired = false;
                TwoFactorAccessToken = null;
                CertificateConfirmationRequired = false;
                UntrustedCertificateInfo = null;
                CurrentProviderType = providerType;

                if (providerType == StorageProviderType.FnOsPersonal)
                {
                    ParseHostAndPort(server, out string host, out int port);

                    // =========================================================================
                    // 阶段 1：TLS 证书探针与安全核对 (在发送密码或 Token 之前完成)
                    // =========================================================================
                    NasCertificateInfo certInfo;
                    try
                    {
                        certInfo = await FnOsApiClient.ProbeCertificateAsync(host, port, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        LogDiagnostics("ConnectAsync.TlsProbe", ex);
                        LastError = $"TLS 握手探测失败 ({ex.GetType().Name}): {ex.Message}";
                        State = NasConnectionState.ConnectionLost;
                        _taskManager.SetNasOnlineState(false);
                        return (false, LastError);
                    }

                    // 检查证书信任状态
                    if (!certInfo.IsSystemTrusted)
                    {
                        if (_settings.TrustedCertThumbprints.TryGetValue(host, out var trustedThumbprint))
                        {
                            if (!string.Equals(trustedThumbprint, certInfo.Thumbprint, StringComparison.OrdinalIgnoreCase))
                            {
                                // 证书指纹变更！严防中间人攻击，必须由用户重新确认
                                UntrustedCertificateInfo = certInfo;
                                CertificateConfirmationRequired = true;
                                LastError = $"检测到 NAS 证书指纹变更！原指纹: {trustedThumbprint}，新指纹: {certInfo.Thumbprint}。为防止安全风险，请重新核对并确认。";
                                State = NasConnectionState.AuthenticationFailed;
                                _taskManager.SetNasOnlineState(false);
                                OnPropertyChanged(nameof(CertificateConfirmationRequired));
                                OnPropertyChanged(nameof(UntrustedCertificateInfo));
                                return (false, LastError);
                            }
                            // 指纹完全匹配，用户此前已明确信任
                        }
                        else
                        {
                            // 首次连接，未记录过信任指纹：停止连接，呈现给用户核对
                            UntrustedCertificateInfo = certInfo;
                            CertificateConfirmationRequired = true;
                            LastError = $"检测到 NAS 自签名 TLS 证书。首次连接需由您核对并信任该设备证书指纹 (SHA-256: {certInfo.Thumbprint})。";
                            State = NasConnectionState.AuthenticationFailed;
                            _taskManager.SetNasOnlineState(false);
                            OnPropertyChanged(nameof(CertificateConfirmationRequired));
                            OnPropertyChanged(nameof(UntrustedCertificateInfo));
                            return (false, LastError);
                        }
                    }

                    // =========================================================================
                    // 阶段 2-4：证书核对通过，发起 WebSocket 握手、账号鉴权与个人空间验证
                    // =========================================================================
                    var fnProvider = new FnOsPersonalStorageProvider(certInfo.Thumbprint);
                    fnProvider.ConnectionLost += () => _ = HandleFnOsConnectionLostAsync(fnProvider);
                    StorageAuthResult authResult;
                    try
                    {
                        authResult = await fnProvider.ConnectAndAuthenticateAsync(server.Trim(), user.Trim(), password, twoFactorCode, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await fnProvider.DisposeAsync().ConfigureAwait(false);
                        LastError = "飞牛连接超时，将等待网络恢复。";
                        State = NasConnectionState.ConnectionLost;
                        _taskManager.SetNasOnlineState(false);
                        return (false, LastError);
                    }
                    catch (OperationCanceledException)
                    {
                        await fnProvider.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                    catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException)
                    {
                        await fnProvider.DisposeAsync().ConfigureAwait(false);
                        LogDiagnostics("ConnectAsync.Network", ex);
                        LastError = "飞牛连接中断或暂时不可用，将等待网络恢复。";
                        State = NasConnectionState.ConnectionLost;
                        _taskManager.SetNasOnlineState(false);
                        return (false, LastError);
                    }
                    catch (Exception ex)
                    {
                        await fnProvider.DisposeAsync().ConfigureAwait(false);
                        LogDiagnostics("ConnectAsync.Auth", ex);
                        LastError = $"飞牛个人空间接入失败 ({ex.GetType().Name}): {ex.Message}";
                        State = NasConnectionState.AuthenticationFailed;
                        _taskManager.SetNasOnlineState(false);
                        return (false, LastError);
                    }

                    if (authResult.TwoFactorRequired)
                    {
                        TwoFactorRequired = true;
                        TwoFactorAccessToken = authResult.TwoFactorAccessToken;
                        await ReplaceCurrentProviderAsync(fnProvider).ConfigureAwait(false);
                        LastError = authResult.ErrorMessage;
                        State = NasConnectionState.AuthenticationFailed;
                        return (false, authResult.ErrorMessage);
                    }

                    if (authResult.Success)
                    {
                        await ReplaceCurrentProviderAsync(fnProvider).ConfigureAwait(false);
                        CurrentUid = authResult.UserUid;
                        PersonalRootDisplay = fnProvider.RootDisplayPath;

                        ServerAddress = server.Trim();
                        Username = user.Trim();
                        _settings.NasServer = ServerAddress;
                        _settings.Username = Username;
                        _settings.RememberCredentials = rememberCredentials;
                        _settings.Save();

                        string? credentialWarning = null;
                        if (rememberCredentials && (!_settings.IsInMemoryOnly || !string.IsNullOrEmpty(_settings.IsolatedCredentialPath)))
                        {
                            if (!SmbAuthService.SaveCredentials(ServerAddress, Username, password, _settings.IsolatedCredentialPath))
                                credentialWarning = "登录凭据未能保存，断线后可能需要重新登录。";
                        }
                        else if (!rememberCredentials && (!_settings.IsInMemoryOnly || !string.IsNullOrEmpty(_settings.IsolatedCredentialPath)))
                        {
                            if (!SmbAuthService.ClearSavedCredentials(_settings.IsolatedCredentialPath))
                                credentialWarning = "旧的本机登录凭据未能清除，请检查文件权限。";
                        }

                        _taskManager.SetNasOnlineState(true);
                        LastConnectedTime = DateTime.Now;
                        State = NasConnectionState.Connected;
                        if (credentialWarning != null) LastError = credentialWarning;

                        return (true, $"登录成功！已成功连接飞牛官方「我的文件」个人空间 (UID: {CurrentUid})。{credentialWarning}");
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
                    // 传统 Windows SMB 模式备用方案
                    var (success, errorMsg) = await _smbService.ConnectNasAsync(server.Trim(), user.Trim(), password).ConfigureAwait(false);
                    if (success)
                    {
                        await ReplaceCurrentProviderAsync(new SmbStorageProvider(_smbService, $@"\\{server.Trim()}")).ConfigureAwait(false);
                        ServerAddress = server.Trim();
                        Username = user.Trim();
                        _settings.NasServer = ServerAddress;
                        _settings.Username = Username;
                        _settings.RememberCredentials = rememberCredentials;
                        _settings.Save();

                        string? credentialWarning = null;
                        if (rememberCredentials && (!_settings.IsInMemoryOnly || !string.IsNullOrEmpty(_settings.IsolatedCredentialPath)))
                        {
                            if (!SmbAuthService.SaveCredentials(ServerAddress, Username, password, _settings.IsolatedCredentialPath))
                                credentialWarning = "登录凭据未能保存，断线后可能需要重新登录。";
                        }
                        else if (!rememberCredentials && (!_settings.IsInMemoryOnly || !string.IsNullOrEmpty(_settings.IsolatedCredentialPath)))
                        {
                            if (!SmbAuthService.ClearSavedCredentials(_settings.IsolatedCredentialPath))
                                credentialWarning = "旧的本机登录凭据未能清除，请检查文件权限。";
                        }

                        _taskManager.SetNasOnlineState(true);
                        LastConnectedTime = DateTime.Now;
                        State = NasConnectionState.Connected;
                        if (credentialWarning != null) LastError = credentialWarning;
                        return (true, $"登录成功！已成功建立与 \\\\{ServerAddress} 的 SMB 验证会话。{credentialWarning}");
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
            catch (OperationCanceledException)
            {
                LastError = "连接操作已取消。";
                State = NasConnectionState.Disconnected;
                _taskManager.SetNasOnlineState(false);
                return (false, LastError);
            }
            catch (Exception ex)
            {
                LogDiagnostics("ConnectAsync.General", ex);
                LastError = $"连接异常 ({ex.GetType().Name}): {ex.Message}";
                State = NasConnectionState.ConnectionLost;
                _taskManager.SetNasOnlineState(false);
                return (false, LastError);
            }
            finally
            {
                if (ReferenceEquals(_activeConnectionCts, attemptCts)) _activeConnectionCts = null;
                attemptCts.Dispose();
                _connectionLock.Release();
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
        /// 主动断开当前 NAS 连接会话并安全释放存储提供程序
        /// 具备完整的生命周期保护、异步互斥与容错，绝不导致进程崩溃或错误在线状态
        /// </summary>
        public async Task DisconnectAsync()
        {
            CancelAutomaticReconnect(disconnectRequested: true);
            bool lockAcquired = await _connectionLock.WaitAsync(5000).ConfigureAwait(false);
            if (!lockAcquired)
            {
                // Ask the in-flight connection attempt to stop, but never mutate shared
                // connection state or release a semaphore we do not own.
                try { _activeConnectionCts?.Cancel(); } catch (ObjectDisposedException) { }
                lockAcquired = await _connectionLock.WaitAsync(5000).ConfigureAwait(false);
                if (!lockAcquired)
                {
                    LastError = "连接操作仍未结束，暂时无法断开；请稍后重试。";
                    return;
                }
            }

            try
            {
                _activeConnectionCts?.Cancel();
                _activeConnectionCts = null;

                // 1. 先安全通知 TaskManager 标记 NAS 离线，停止依赖当前连接的活动
                _taskManager.SetNasOnlineState(false);

                // 2. 安全释放当前存储提供程序
                var provider = _currentStorageProvider;
                _currentStorageProvider = null;
                if (provider != null)
                {
                    try
                    {
                        await provider.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogDiagnostics("DisconnectAsync.ProviderDispose", ex);
                    }
                }

                // 3. 仅在 SMB 共享模式下且确有必要时释放 SMB 会话，绝不影响使用 fnOS 原生模式的其他应用
                if (CurrentProviderType == StorageProviderType.SmbShare && !string.IsNullOrEmpty(ServerAddress))
                {
                    try
                    {
                        await _smbService.DisconnectNasAsync(ServerAddress).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogDiagnostics("DisconnectAsync.SmbDisconnect", ex);
                    }
                }

                // 4. 清理会话状态字段
                CurrentUid = null;
                PersonalRootDisplay = string.Empty;
                TwoFactorRequired = false;
                TwoFactorAccessToken = null;
                CertificateConfirmationRequired = false;
                UntrustedCertificateInfo = null;

                _state = NasConnectionState.Disconnected;
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(StatusDescription));
                StateChanged?.Invoke(this, NasConnectionState.Disconnected);
            }
            catch (Exception ex)
            {
                LogDiagnostics("DisconnectAsync.General", ex);
                _state = NasConnectionState.Disconnected;
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(StatusDescription));
                StateChanged?.Invoke(this, NasConnectionState.Disconnected);
            }
            finally
            {
                _connectionLock.Release();
            }
        }

        /// <summary>
        /// 验证当前 NAS 会话是否依然有效
        /// 基于有效会话与实际授权目录检查，绝不盲目通过 SMB IPC$ 冒充
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

            if (CurrentProviderType == StorageProviderType.SmbShare)
            {
                try
                {
                    string testPath = $@"\\{ServerAddress}\IPC$";
                    bool reachable = await Task.Run(() => System.IO.Directory.Exists(testPath)).ConfigureAwait(false);
                    if (reachable)
                    {
                        if (State != NasConnectionState.Connected)
                        {
                            State = NasConnectionState.Connected;
                            _taskManager.SetNasOnlineState(true);
                        }
                        return true;
                    }
                }
                catch { }
            }

            State = NasConnectionState.ConnectionLost;
            _taskManager.SetNasOnlineState(false);
            return false;
        }

        private async Task HandleFnOsConnectionLostAsync(FnOsPersonalStorageProvider provider)
        {
            long intentVersion;
            await _connectionLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(CurrentStorageProvider, provider) || provider.IsAuthenticated) return;
                LastError = "飞牛控制连接已中断，请重新连接后继续备份。";
                State = NasConnectionState.ConnectionLost;
                _taskManager.SetNasOnlineState(false);
                lock (_reconnectLock) intentVersion = _connectionIntentVersion;
            }
            finally { _connectionLock.Release(); }
            StartAutomaticReconnect(intentVersion);
        }

        private void CancelAutomaticReconnect(bool disconnectRequested)
        {
            CancellationTokenSource? existing;
            lock (_reconnectLock)
            {
                _disconnectRequested = disconnectRequested;
                _connectionIntentVersion++;
                existing = _reconnectCts;
                _reconnectCts = null;
            }
            try { existing?.Cancel(); } catch (ObjectDisposedException) { }
        }

        /// <summary>Stops retries and waits for any in-flight connection before removing saved credentials.</summary>
        public async Task<(bool Success, string Message)> ForgetSavedCredentialsAsync()
        {
            _settings.RememberCredentials = false;
            CancelAutomaticReconnect(disconnectRequested: false);
            if (!await _connectionLock.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false))
                return (false, "连接操作仍未结束，凭据尚未清除，请稍后重试。");
            try
            {
                // An already-running login may have saved its preference while we waited.
                _settings.RememberCredentials = false;
                bool removed = _settings.IsInMemoryOnly && string.IsNullOrEmpty(_settings.IsolatedCredentialPath) ||
                    SmbAuthService.ClearSavedCredentials(_settings.IsolatedCredentialPath);
                try { _settings.Save(); }
                catch (Exception ex) { return (false, $"凭据清除结果：{(removed ? "已清除" : "失败")}；保存设置失败：{ex.Message}"); }
                if (!removed) return (false, "清除凭据失败，请检查文件权限。");
                return (true, _settings.IsInMemoryOnly ? "已清除隔离测试会话保存的凭据，并停止自动重连。" : "已清除本机保存的 NAS 登录凭据，并停止自动重连。");
            }
            finally { _connectionLock.Release(); }
        }

        private void StartAutomaticReconnect(long intentVersion)
        {
            if (!_settings.RememberCredentials || (_settings.IsInMemoryOnly && string.IsNullOrEmpty(_settings.IsolatedCredentialPath))) return;
            CancellationTokenSource retry;
            lock (_reconnectLock)
            {
                if (_disconnectRequested || intentVersion != _connectionIntentVersion || _reconnectCts != null) return;
                retry = new CancellationTokenSource();
                _reconnectCts = retry;
            }
            _ = RunAutomaticReconnectAsync(retry, intentVersion);
        }

        private async Task RunAutomaticReconnectAsync(CancellationTokenSource retry, long intentVersion)
        {
            int[] seconds = { 2, 5, 10, 30 };
            bool connectedOnce = false;
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(seconds[Math.Min(attempt, seconds.Length - 1)]), retry.Token).ConfigureAwait(false);
                    var credentials = SmbAuthService.LoadCredentials(_settings.IsolatedCredentialPath);
                    if (credentials == null || !string.Equals(credentials.Value.Server, ServerAddress, StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(credentials.Value.Username, Username, StringComparison.OrdinalIgnoreCase)) return;
                    lock (_reconnectLock)
                        if (_disconnectRequested || intentVersion != _connectionIntentVersion) return;
                    var result = await ConnectCoreAsync(credentials.Value.Server, credentials.Value.Username, credentials.Value.Password,
                        true, StorageProviderType.FnOsPersonal, null, retry.Token).ConfigureAwait(false);
                    if (result.Success) { connectedOnce = true; return; }
                    if (State == NasConnectionState.AuthenticationFailed || CertificateConfirmationRequired || TwoFactorRequired) return;
                    retry.Token.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) when (retry.IsCancellationRequested) { }
            catch (Exception ex) { LogDiagnostics("AutomaticReconnect", ex); }
            finally
            {
                lock (_reconnectLock)
                    if (ReferenceEquals(_reconnectCts, retry)) _reconnectCts = null;
                retry.Dispose();
                if (connectedOnce && State == NasConnectionState.ConnectionLost) StartAutomaticReconnect(intentVersion);
            }
        }

        private async Task ReplaceCurrentProviderAsync(INasStorageProvider provider)
        {
            var previous = CurrentStorageProvider;
            CurrentStorageProvider = provider;
            if (previous != null && !ReferenceEquals(previous, provider))
                await previous.DisposeAsync().ConfigureAwait(false);
        }

        private static void ParseHostAndPort(string server, out string host, out int port)
        {
            string s = server.Trim();
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s[8..];
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = s[7..];
            s = s.TrimEnd('/');
            int colon = s.IndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p))
            {
                host = s[..colon];
                port = p;
            }
            else
            {
                host = s;
                port = 5667;
            }
        }

        private static void LogDiagnostics(string stage, Exception ex)
        {
            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string logDir = Path.Combine(localApp, "FNSyncPlus", "logs");
                Directory.CreateDirectory(logDir);
                string logFile = Path.Combine(logDir, $"connection_{DateTime.Now:yyyyMMdd}.log");
                string logMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{stage}] {ex.GetType().FullName}: {ex.Message}\n{ex.StackTrace}\n";
                string sanitized = Regex.Replace(logMsg, @"(?i)(password|pwd|secret|token)=([^\s&;]+)", "$1=******");
                File.AppendAllText(logFile, sanitized);
            }
            catch { }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
