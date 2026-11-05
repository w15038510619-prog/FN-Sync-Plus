using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

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
    /// 统一的 NAS 连接状态管理服务（全局单例），管理 SMB 登录、心跳验证、断开连接及状态广播
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

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler<NasConnectionState>? StateChanged;

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
            NasConnectionState.Connected => $"{ServerAddress} · {Username} (SMB 已连接)",
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
        }

        /// <summary>
        /// 执行 NAS SMB 连接与身份认证
        /// </summary>
        public async Task<(bool Success, string Message)> ConnectAsync(string server, string user, string password, bool rememberCredentials)
        {
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(user))
            {
                LastError = "请输入 NAS 地址和用户名";
                State = NasConnectionState.AuthenticationFailed;
                return (false, LastError);
            }

            State = NasConnectionState.Connecting;
            LastError = string.Empty;

            try
            {
                var (success, errorMsg) = await _smbService.ConnectNasAsync(server.Trim(), user.Trim(), password);
                if (success)
                {
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
            catch (Exception ex)
            {
                LastError = ex.Message;
                State = NasConnectionState.ConnectionLost;
                _taskManager.SetNasOnlineState(false);
                return (false, $"连接异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 主动断开当前 NAS SMB 连接会话
        /// </summary>
        public async Task DisconnectAsync()
        {
            string serverToDisconnect = ServerAddress;
            _state = NasConnectionState.Disconnected;
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(StatusDescription));
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
