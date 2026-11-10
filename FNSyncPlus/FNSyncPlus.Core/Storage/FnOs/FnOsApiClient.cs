using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FNSyncPlus.Core.Storage.FnOs
{
    /// <summary>
    /// NAS 设备的 TLS 证书元数据模型（用于用户核对与指纹锁定）
    /// </summary>
    public class NasCertificateInfo
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public string Thumbprint { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Issuer { get; set; } = string.Empty;
        public DateTime NotBefore { get; set; }
        public DateTime NotAfter { get; set; }
        public SslPolicyErrors SslPolicyErrors { get; set; }
        public bool IsSystemTrusted => SslPolicyErrors == SslPolicyErrors.None;
        public bool IsSelfSigned => string.Equals(Subject, Issuer, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 飞牛 fnOS 官方原生 API 客户端 (WebSocket RPC 控制面 + HTTP 数据面)
    /// 严禁绕过正常登录与权限隔离，严禁明文打印或记录敏感凭据，具备严格失败关闭保护
    /// </summary>
    public class FnOsApiClient : IAsyncDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly SocketsHttpHandler _httpHandler;
        private ClientWebSocket? _webSocket;
        private readonly SemaphoreSlim _wsLock = new(1, 1);
        private readonly Dictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
        private readonly Dictionary<string, TaskCompletionSource<string>> _pendingTaskRequests = new();
        private readonly Dictionary<string, List<FnOsFileEntry>> _pendingFileLists = new();
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveLoopTask;
        private CancellationTokenSource? _heartbeatCts;
        private Task? _heartbeatTask;
        private long _lastHeartbeatUtcTicks;

        private string? _host;
        private int _port;
        private bool _useSsl;
        private string? _pinnedCertSha256;

        private string? _rsaPublicKeyPem;
        private string? _sessionId;
        private byte[]? _aesKey;
        private byte[]? _iv;
        private byte[]? _hmacKeyBytes;

        public bool IsConnected => _webSocket?.State == WebSocketState.Open;
        private volatile bool _isAuthenticated;
        public bool IsAuthenticated { get => _isAuthenticated; private set => _isAuthenticated = value; }
        public DateTime? LastSuccessfulHeartbeatUtc
        {
            get { long ticks = Interlocked.Read(ref _lastHeartbeatUtcTicks); return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc); }
        }
        public event Action? ConnectionLost;
        public string? AuthToken { get; private set; }
        public string? LongToken { get; private set; }
        public string? AuthenticatedUser { get; private set; }
        public int? CurrentUid { get; private set; }
        public int? CurrentVolumeId { get; private set; }

        public void SetAuthenticatedForTesting(string user, int uid, int volId)
        {
            IsAuthenticated = true;
            AuthenticatedUser = user;
            CurrentUid = uid;
            CurrentVolumeId = volId;
        }

        public static Func<string, string, bool>? TrustedThumbprintValidator { get; set; }
        public string? LastSeenCertSha256 { get; private set; }

        public FnOsApiClient(string? pinnedCertSha256 = null)
        {
            _pinnedCertSha256 = pinnedCertSha256;
            _httpHandler = new SocketsHttpHandler
            {
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                    {
                        var x509 = cert as X509Certificate2 ?? (cert != null ? new X509Certificate2(cert) : null);
                        return ValidateServerCertificate(new HttpRequestMessage(), x509, chain, errors);
                    }
                },
                RequestHeaderEncodingSelector = (headerName, requestMessage) =>
                {
                    if (headerName.Equals("Trim-Path", StringComparison.OrdinalIgnoreCase))
                    {
                        return Encoding.UTF8;
                    }
                    return null;
                }
            };
            _httpClient = new HttpClient(_httpHandler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
        }

        /// <summary>
        /// 预检并探测 NAS 服务的真实 TLS 证书信息（在发送任何用户名、密码或 Token 之前执行）
        /// 严格提取真实证书的 SHA-256 指纹、使用者、颁发者与信任状态，严禁盲目信任或默认放行
        /// </summary>
        public static async Task<NasCertificateInfo> ProbeCertificateAsync(string host, int port, CancellationToken ct = default)
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var reg = ct.Register(() => { try { tcp.Close(); } catch { } });
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);

            NasCertificateInfo? info = null;
            using var sslStream = new SslStream(tcp.GetStream(), false, (sender, certificate, chain, errors) =>
            {
                if (certificate is X509Certificate2 cert)
                {
                    string thumb = cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256).ToUpperInvariant();
                    info = new NasCertificateInfo
                    {
                        Host = host,
                        Port = port,
                        Thumbprint = thumb,
                        Subject = cert.Subject,
                        Issuer = cert.Issuer,
                        NotBefore = cert.NotBefore,
                        NotAfter = cert.NotAfter,
                        SslPolicyErrors = errors
                    };
                }
                return true; // 仅探针探测：允许握手完成以捕获证书元数据
            });

            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = host
            };

            await sslStream.AuthenticateAsClientAsync(sslOptions, ct).ConfigureAwait(false);

            if (info == null)
            {
                throw new InvalidOperationException($"未能从 {host}:{port} 获取到有效的 TLS 证书。");
            }

            return info;
        }

        private bool ValidateServerCertificate(
            HttpRequestMessage request,
            X509Certificate2? certificate,
            X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            // 若系统信任链验证完全通过，直接允许
            if (sslPolicyErrors == SslPolicyErrors.None) return true;

            if (certificate == null) return false;

            // 提取服务端的真实 SHA-256 证书指纹
            string thumbprint = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256).ToUpperInvariant();
            LastSeenCertSha256 = thumbprint;

            // 1. 检查自签名或设备专属证书的 SHA-256 指纹强校验 (Certificate Pinning)
            if (!string.IsNullOrEmpty(_pinnedCertSha256))
            {
                return string.Equals(thumbprint, _pinnedCertSha256.Replace(":", "").ToUpperInvariant(), StringComparison.OrdinalIgnoreCase);
            }

            // 2. 检查由用户授权并记录在安全配置中的证书指纹白名单
            if (!string.IsNullOrEmpty(_host) && TrustedThumbprintValidator != null)
            {
                if (TrustedThumbprintValidator(_host, thumbprint))
                {
                    return true;
                }
            }

            // 严格安全策略：拒绝仅凭 Subject 包含 fnOS 字样就放行的逻辑，严防中间人攻击
            return false;
        }

        /// <summary>
        /// 建立 WebSocket 控制通道并完成 RSA 握手
        /// </summary>
        public async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct = default)
        {
            await StopHeartbeatAsync().ConfigureAwait(false);
            _receiveCts?.Cancel();
            if (_receiveLoopTask != null)
            {
                try { await _receiveLoopTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            _receiveCts?.Dispose();
            _webSocket?.Dispose();
            IsAuthenticated = false;
            AuthToken = null;
            LongToken = null;
            AuthenticatedUser = null;
            CurrentUid = null;
            CurrentVolumeId = null;
            _rsaPublicKeyPem = null;
            _sessionId = null;
            _aesKey = null;
            _iv = null;
            _hmacKeyBytes = null;
            Interlocked.Exchange(ref _lastHeartbeatUtcTicks, 0);
            _host = host;
            _port = port;
            _useSsl = useSsl;

            string scheme = useSsl ? "wss" : "ws";
            var uri = new Uri($"{scheme}://{host}:{port}/websocket?type=main");

            _webSocket = new ClientWebSocket();
            if (useSsl)
            {
                _webSocket.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) =>
                {
                    var x509 = cert as X509Certificate2 ?? (cert != null ? new X509Certificate2(cert) : null);
                    return ValidateServerCertificate(new HttpRequestMessage(), x509, chain, errors);
                };
            }

            await _webSocket.ConnectAsync(uri, ct);

            // 启动消息接收循环
            _receiveCts = new CancellationTokenSource();
            var activeSocket = _webSocket;
            var receiveToken = _receiveCts.Token;
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(activeSocket, receiveToken));

            // 发送首个握手请求获取 RSA 公钥 (util.crypto.getRSAPub)
            string getPubMsg = $"{{\"reqid\":\"{FnOsCryptoHelper.GenerateReqId()}\",\"req\":\"util.crypto.getRSAPub\"}}";
            await SendRawTextAsync(getPubMsg, ct);

            // 最多等待 8 秒获取握手
            using var handshakeTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, handshakeTimeoutCts.Token);

            while (!linked.IsCancellationRequested)
            {
                if (_rsaPublicKeyPem != null && _sessionId != null) break;
                await Task.Delay(50, linked.Token);
            }

            if (_rsaPublicKeyPem == null || _sessionId == null)
            {
                throw new InvalidOperationException("连接 fnOS 握手失败：未在预期时间内获取到 RSA 公钥与 Session ID。");
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var ms = new MemoryStream();

            try
            {
                while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            return;
                        }
                        ms.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    string text = Encoding.UTF8.GetString(ms.ToArray());
                    ProcessIncomingMessage(text);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception) { /* The caller receives an explicit connection-lost signal below. */ }
            finally { MarkControlChannelStopped(socket, ct.IsCancellationRequested); ms.Dispose(); }
        }

        private void MarkControlChannelStopped(ClientWebSocket socket, bool intentional)
        {
            List<TaskCompletionSource<string>> pending;
            bool wasAuthenticated;
            lock (_pendingRequests)
            {
                if (!ReferenceEquals(_webSocket, socket)) return;
                wasAuthenticated = IsAuthenticated;
                IsAuthenticated = false;
                pending = _pendingRequests.Values.Concat(_pendingTaskRequests.Values).ToList();
                _pendingRequests.Clear();
                _pendingTaskRequests.Clear();
                _pendingFileLists.Clear();
            }
            foreach (var request in pending)
                request.TrySetException(new IOException("飞牛控制连接已中断，已发送的远端操作结果可能不确定"));
            if (!intentional && wasAuthenticated) ConnectionLost?.Invoke();
        }

        private void StartHeartbeat()
        {
            if (_webSocket == null || !IsAuthenticated || (_heartbeatTask != null && !_heartbeatTask.IsCompleted)) return;
            _heartbeatCts = new CancellationTokenSource();
            var socket = _webSocket;
            var token = _heartbeatCts.Token;
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(socket, token));
        }

        private async Task HeartbeatLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            try
            {
                while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    if (!ReferenceEquals(_webSocket, socket) || !IsAuthenticated) return;
                    string response = await SendSignedRpcAsync("user.info", new Dictionary<string, object?>(), token).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(response);
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        (document.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String && result.GetString() == "fail") ||
                        (document.RootElement.TryGetProperty("errno", out var error) && error.ValueKind == JsonValueKind.Number && error.TryGetInt32(out int code) && code != 0))
                        throw new IOException("飞牛会话检查失败");
                    Interlocked.Exchange(ref _lastHeartbeatUtcTicks, DateTime.UtcNow.Ticks);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception)
            {
                if (ReferenceEquals(_webSocket, socket))
                {
                    try { socket.Abort(); } catch (ObjectDisposedException) { }
                }
            }
        }

        private async Task StopHeartbeatAsync()
        {
            _heartbeatCts?.Cancel();
            if (_heartbeatTask != null) await _heartbeatTask.ConfigureAwait(false);
            _heartbeatCts?.Dispose();
            _heartbeatCts = null;
            _heartbeatTask = null;
        }

        private void ProcessIncomingMessage(string rawMessage)
        {
            if (string.IsNullOrWhiteSpace(rawMessage)) return;

            // 处理初始握手包 (可能为纯 JSON)
            if (rawMessage.StartsWith("{") && rawMessage.Contains("\"pub\"") && rawMessage.Contains("\"si\""))
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawMessage);
                    if (doc.RootElement.TryGetProperty("pub", out var pubProp))
                        _rsaPublicKeyPem = pubProp.GetString();
                    if (doc.RootElement.TryGetProperty("si", out var siProp))
                        _sessionId = siProp.GetString();
                }
                catch { }
                return;
            }

            // 处理带 HMAC 签名前缀的消息 (前 44 字节为 Base64 签名)
            string jsonBody = rawMessage;
            if (rawMessage.Length > 44 && !rawMessage.StartsWith("{"))
            {
                jsonBody = rawMessage.Substring(44);
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonBody);
                if (doc.RootElement.TryGetProperty("reqid", out var reqIdProp))
                {
                    string? reqId = reqIdProp.GetString();
                    if (!string.IsNullOrEmpty(reqId))
                    {
                        TaskCompletionSource<string>? completed = null;
                        Exception? listError = null;
                        string response = jsonBody;
                        lock (_pendingRequests)
                        {
                            if (_pendingFileLists.TryGetValue(reqId, out var fileList) && _pendingRequests.TryGetValue(reqId, out var listCompletion))
                            {
                                try
                                {
                                    if (doc.RootElement.TryGetProperty("files", out var files))
                                    {
                                        if (files.ValueKind != JsonValueKind.Array) throw new InvalidDataException("飞牛目录列表批次无效");
                                        var batch = JsonSerializer.Deserialize<List<FnOsFileEntry>>(files.GetRawText())
                                            ?? throw new InvalidDataException("飞牛目录列表批次为空");
                                        if (batch.Any(item => item == null || string.IsNullOrWhiteSpace(item.Name)))
                                            throw new InvalidDataException("飞牛目录列表包含无效条目");
                                        fileList.AddRange(batch);
                                    }
                                    var frame = JsonSerializer.Deserialize<FnOsFileListResponse>(jsonBody)
                                        ?? throw new InvalidDataException("飞牛目录列表响应无效");
                                    if (frame.Result != null || (frame.ErrNo.HasValue && frame.ErrNo.Value != 0))
                                    {
                                        if (frame.IsSuccess && fileList.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fileList.Count)
                                            throw new InvalidDataException("飞牛目录列表包含重复条目，拒绝使用不确定的结果");
                                        frame.Files = fileList;
                                        response = JsonSerializer.Serialize(frame);
                                        _pendingFileLists.Remove(reqId);
                                        _pendingRequests.Remove(reqId);
                                        completed = listCompletion;
                                    }
                                }
                                catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
                                {
                                    _pendingFileLists.Remove(reqId);
                                    _pendingRequests.Remove(reqId);
                                    completed = listCompletion;
                                    listError = ex;
                                }
                            }
                            else if (_pendingTaskRequests.TryGetValue(reqId, out var taskCompletion))
                            {
                                bool terminalResult = doc.RootElement.TryGetProperty("result", out var result) &&
                                    result.ValueKind == JsonValueKind.String && result.GetString() is "succ" or "fail" or "partialCompleted" or "canceled";
                                bool terminalError = doc.RootElement.TryGetProperty("errno", out var error) &&
                                    error.ValueKind == JsonValueKind.Number && error.TryGetInt32(out int code) && code != 0;
                                if (terminalResult || terminalError)
                                {
                                    _pendingTaskRequests.Remove(reqId);
                                    completed = taskCompletion;
                                }
                            }
                            else if (_pendingRequests.TryGetValue(reqId, out var tcs))
                            {
                                _pendingRequests.Remove(reqId);
                                completed = tcs;
                            }
                        }
                        if (listError != null) completed?.TrySetException(listError);
                        else completed?.TrySetResult(response);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 登录鉴权 (RSA 加密 AES Key + AES 加密用户名密码凭据)
        /// 严格保护凭据安全，绝不在日志中明文记录密码
        /// </summary>
        public async Task<FnOsLoginResponse> LoginAsync(string username, string password, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_rsaPublicKeyPem) || string.IsNullOrEmpty(_sessionId))
            {
                throw new InvalidOperationException("尚未完成与 NAS 的安全握手，无法发起登录。");
            }

            // 1. 生成 256 位 AES 随机密钥与 IV
            _aesKey = FnOsCryptoHelper.GenerateRandomBytes(32);
            _iv = FnOsCryptoHelper.GenerateRandomBytes(16);

            // 2. RSA 公钥加密 AES 密钥
            byte[] encryptedAesKey = FnOsCryptoHelper.EncryptAesKeyWithRsa(_rsaPublicKeyPem, _aesKey);

            // 3. 构造登录荷载
            string reqId = FnOsCryptoHelper.GenerateReqId();
            var loginPayload = new Dictionary<string, object?>
            {
                ["reqid"] = reqId,
                ["user"] = username,
                ["password"] = password,
                ["stay"] = true,
                ["deviceType"] = "Windows-App",
                ["deviceName"] = "FN Sync Plus",
                ["did"] = FnOsCryptoHelper.GenerateDeviceId(),
                ["req"] = "user.login",
                ["si"] = _sessionId
            };

            string loginJson = JsonSerializer.Serialize(loginPayload);
            byte[] encryptedData = FnOsCryptoHelper.EncryptAesCbc(Encoding.UTF8.GetBytes(loginJson), _aesKey, _iv);

            // 4. 发送加密认证信封
            var envelope = new FnOsEncryptedAuthEnvelope
            {
                Req = "encrypted",
                IvBase64 = Convert.ToBase64String(_iv),
                RsaEncryptedKeyBase64 = Convert.ToBase64String(encryptedAesKey),
                AesEncryptedPayloadBase64 = Convert.ToBase64String(encryptedData)
            };

            string envelopeJson = JsonSerializer.Serialize(envelope);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingRequests)
            {
                _pendingRequests[reqId] = tcs;
            }

            string responseJson;
            try
            {
                await SendRawTextAsync(envelopeJson, ct).ConfigureAwait(false);
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                using var reg = linked.Token.Register(() => tcs.TrySetCanceled());
                responseJson = await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_pendingRequests)
                    if (_pendingRequests.TryGetValue(reqId, out var pending) && ReferenceEquals(pending, tcs)) _pendingRequests.Remove(reqId);
            }
            var loginResp = JsonSerializer.Deserialize<FnOsLoginResponse>(responseJson);

            if (loginResp?.Result == "succ" && !string.IsNullOrEmpty(loginResp.EncryptedSecretBase64))
            {
                // 解密 secret 字段获得 raw HMAC 签名密钥
                byte[] rawSecretCipher = Convert.FromBase64String(loginResp.EncryptedSecretBase64);
                _hmacKeyBytes = FnOsCryptoHelper.DecryptAesCbc(rawSecretCipher, _aesKey, _iv);

                lock (_pendingRequests)
                {
                    if (!IsConnected) throw new IOException("登录时飞牛控制连接已中断");
                    AuthToken = loginResp.Token;
                    LongToken = loginResp.LongToken;
                    AuthenticatedUser = username;
                    IsAuthenticated = true;
                }

                // 登录成功后立即通过 user.info 获取当前用户的权威 UID 与账户信息
                try
                {
                    string infoJson = await SendSignedRpcAsync("user.info", new Dictionary<string, object?>(), ct);
                    using var doc = JsonDocument.Parse(infoJson);
                    if (doc.RootElement.TryGetProperty("userInfo", out var uInfo) &&
                        uInfo.TryGetProperty("uid", out var uidProp))
                    {
                        CurrentUid = uidProp.GetInt32();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
                if (!IsAuthenticated || !IsConnected) throw new IOException("登录后飞牛控制连接已中断");
                StartHeartbeat();
            }

            return loginResp ?? new FnOsLoginResponse { Result = "failed", ErrMsg = "Empty response" };
        }

        /// <summary>
        /// 提交 TOTP 两步验证码完成二次鉴权
        /// </summary>
        public async Task<FnOsLoginResponse> LoginWithTwoFactorAsync(string accessToken, string code, string username, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(_sessionId) || _aesKey == null || _iv == null || string.IsNullOrEmpty(_rsaPublicKeyPem))
            {
                throw new InvalidOperationException("缺少基础会话加密上下文，无法进行两步验证。");
            }

            string reqId = FnOsCryptoHelper.GenerateReqId();
            var payload = new Dictionary<string, object?>
            {
                ["reqid"] = reqId,
                ["req"] = "user.twofaLogin",
                ["accessToken"] = accessToken,
                ["code"] = code,
                ["si"] = _sessionId,
                ["did"] = FnOsCryptoHelper.GenerateDeviceId(),
                ["stay"] = true
            };

            string payloadJson = JsonSerializer.Serialize(payload);
            byte[] encryptedData = FnOsCryptoHelper.EncryptAesCbc(Encoding.UTF8.GetBytes(payloadJson), _aesKey, _iv);
            byte[] encryptedAesKey = FnOsCryptoHelper.EncryptAesKeyWithRsa(_rsaPublicKeyPem, _aesKey);

            var envelope = new FnOsEncryptedAuthEnvelope
            {
                Req = "encrypted",
                IvBase64 = Convert.ToBase64String(_iv),
                RsaEncryptedKeyBase64 = Convert.ToBase64String(encryptedAesKey),
                AesEncryptedPayloadBase64 = Convert.ToBase64String(encryptedData)
            };

            string envelopeJson = JsonSerializer.Serialize(envelope);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingRequests)
            {
                _pendingRequests[reqId] = tcs;
            }

            string responseJson;
            try
            {
                await SendRawTextAsync(envelopeJson, ct).ConfigureAwait(false);
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                using var reg = linked.Token.Register(() => tcs.TrySetCanceled());
                responseJson = await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_pendingRequests)
                    if (_pendingRequests.TryGetValue(reqId, out var pending) && ReferenceEquals(pending, tcs)) _pendingRequests.Remove(reqId);
            }
            var loginResp = JsonSerializer.Deserialize<FnOsLoginResponse>(responseJson);

            if (loginResp?.Result == "succ" && !string.IsNullOrEmpty(loginResp.EncryptedSecretBase64))
            {
                byte[] rawSecretCipher = Convert.FromBase64String(loginResp.EncryptedSecretBase64);
                _hmacKeyBytes = FnOsCryptoHelper.DecryptAesCbc(rawSecretCipher, _aesKey, _iv);

                lock (_pendingRequests)
                {
                    if (!IsConnected) throw new IOException("登录时飞牛控制连接已中断");
                    AuthToken = loginResp.Token;
                    LongToken = loginResp.LongToken;
                    AuthenticatedUser = username;
                    IsAuthenticated = true;
                }

                try
                {
                    string infoJson = await SendSignedRpcAsync("user.info", new Dictionary<string, object?>(), ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(infoJson);
                    if (doc.RootElement.TryGetProperty("userInfo", out var uInfo) &&
                        uInfo.TryGetProperty("uid", out var uidProp))
                    {
                        CurrentUid = uidProp.GetInt32();
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
                if (!IsAuthenticated || !IsConnected) throw new IOException("登录后飞牛控制连接已中断");
                StartHeartbeat();
            }

            return loginResp ?? new FnOsLoginResponse { Result = "failed", ErrMsg = "Empty response" };
        }

        private async Task SendRawTextAsync(string text, CancellationToken ct)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket 未处于连接状态。");

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            await _wsLock.WaitAsync(ct);
            try
            {
                await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _wsLock.Release();
            }
        }

        /// <summary>
        /// 发送带 HMAC-SHA256 签名的 WebSocket RPC 请求并等待响应
        /// </summary>
        public Task<string> SendSignedRpcAsync(string method, Dictionary<string, object?> payload, CancellationToken ct = default)
            => SendSignedRpcCoreAsync(method, payload, false, TimeSpan.FromSeconds(15), ct);

        /// <summary>Waits for the terminal response of a long-running RPC; cancellation or timeout leaves the remote outcome unknown.</summary>
        public Task<string> SendSignedTaskRpcAsync(string method, Dictionary<string, object?> payload, TimeSpan timeout, CancellationToken ct = default)
            => SendSignedRpcCoreAsync(method, payload, true, timeout, ct);

        private async Task<string> SendSignedRpcCoreAsync(string method, Dictionary<string, object?> payload,
            bool waitForTerminalResult, TimeSpan timeout, CancellationToken ct, bool collectFileList = false)
        {
            if (!IsAuthenticated || _hmacKeyBytes == null)
                throw new InvalidOperationException("未鉴权或缺少 HMAC 签名密钥，无法发送 RPC。");
            if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

            string reqId = FnOsCryptoHelper.GenerateReqId();
            payload["req"] = method;
            payload["reqid"] = reqId;

            string jsonStr = JsonSerializer.Serialize(payload);
            string signedMessage = FnOsCryptoHelper.FormatSignedRpcMessage(_hmacKeyBytes, jsonStr);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingRequests)
            {
                (waitForTerminalResult ? _pendingTaskRequests : _pendingRequests).Add(reqId, tcs);
                if (collectFileList) _pendingFileLists.Add(reqId, new List<FnOsFileEntry>());
            }
            try
            {
                using var timeoutCts = new CancellationTokenSource(timeout);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
                using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));
                await SendRawTextAsync(signedMessage, linked.Token).ConfigureAwait(false);
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (_pendingRequests)
                {
                    var pending = waitForTerminalResult ? _pendingTaskRequests : _pendingRequests;
                    if (pending.TryGetValue(reqId, out var current) && ReferenceEquals(current, tcs)) pending.Remove(reqId);
                    if (collectFileList) _pendingFileLists.Remove(reqId);
                }
            }
        }

        /// <summary>
        /// 列出文件目录。当 subPath 为 null 或空时，飞牛系统自动定位至当前登录账号的「我的文件」根目录
        /// </summary>
        public async Task<FnOsFileListResponse> ListFilesAsync(string? subPath = null, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(subPath))
            {
                payload["path"] = subPath;
            }

            string responseJson;
            try
            {
                responseJson = await SendSignedRpcCoreAsync("file.ls", payload, false, TimeSpan.FromSeconds(60), ct, collectFileList: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("飞牛目录列表未在时限内完整返回，已停止读取。", ex);
            }
            var result = JsonSerializer.Deserialize<FnOsFileListResponse>(responseJson);

            // 若在根目录下，自动捕获当前系统 VolumeId 与 UID
            if (string.IsNullOrWhiteSpace(subPath) && result?.Files != null)
            {
                foreach (var f in result.Files)
                {
                    if (f.VolumeId.HasValue) CurrentVolumeId = f.VolumeId.Value;
                    if (f.Uid.HasValue) CurrentUid = f.Uid.Value;
                }
            }

            return result ?? new FnOsFileListResponse { Result = "failed" };
        }

        /// <summary>
        /// 在 NAS 上创建目录 (调用 file.mkdir)
        /// </summary>
        public async Task<FnOsGenericRpcResponse> CreateDirectoryAsync(string path, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Directory path cannot be empty", nameof(path));

            var payload = new Dictionary<string, object?>
            {
                ["path"] = path
            };

            string responseJson = await SendSignedRpcAsync("file.mkdir", payload, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FnOsGenericRpcResponse>(responseJson) 
                   ?? new FnOsGenericRpcResponse { Result = "failed" };
        }

        /// <summary>
        /// 预检文件上传状态并获取续传偏移 (调用 file.checkUpload)
        /// </summary>
        public async Task<FnOsCheckUploadResponse> CheckUploadAsync(string remotePath, long size, long mtimSeconds, CancellationToken ct = default)
        {
            string canonicalPath = remotePath.StartsWith("/") ? remotePath : "/" + remotePath;
            var payload = new Dictionary<string, object?>
            {
                ["path"] = canonicalPath,
                ["size"] = size,
                ["mtim"] = mtimSeconds
            };

            string responseJson = await SendSignedRpcAsync("file.checkUpload", payload, ct).ConfigureAwait(false);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            using var doc = JsonDocument.Parse(responseJson);
            var resp = new FnOsCheckUploadResponse
            {
                Result = doc.RootElement.TryGetProperty("result", out var rProp) ? rProp.GetString() : null,
                FromOffset = doc.RootElement.TryGetProperty("from", out var fProp) ? fProp.GetInt64() : 0,
            };
            if (doc.RootElement.TryGetProperty("errno", out var eProp))
            {
                resp.ErrNo = eProp.GetInt32();
            }
            if (doc.RootElement.TryGetProperty("errmsg", out var emProp))
            {
                resp.ErrMsg = emProp.GetString();
            }
            if (doc.RootElement.TryGetProperty("uploadName", out var uProp))
            {
                resp.UploadName = uProp.GetString();
                resp.ErrMsg ??= resp.UploadName;
            }

            return resp;
        }

        /// <summary>
        /// 上传文件二进制流 (调用 HTTP POST /upload 附带 Trim-* 头部与 HMAC-SHA256 签名)
        /// </summary>
        public async Task<bool> UploadFileAsync(
            string remotePath, 
            byte[] fileBytes, 
            DateTime lastWriteTimeUtc, 
            bool overwrite = true, 
            CancellationToken ct = default)
        {
            using var ms = new MemoryStream(fileBytes);
            return await UploadStreamAsync(remotePath, ms, fileBytes.Length, lastWriteTimeUtc, overwrite, null, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 真正的流式上传文件 (分块直接写入 HTTP 请求体，绝不将整个文件读取至内存，支持 100MB+ 大文件与进度报告)
        /// </summary>
        public async Task<bool> UploadStreamAsync(
            string remotePath,
            Stream sourceStream,
            long fileSize,
            DateTime lastWriteTimeUtc,
            bool overwrite = true,
            IProgress<long>? progress = null,
            CancellationToken ct = default)
        {
            var res = await UploadStreamDetailedAsync(remotePath, sourceStream, fileSize, lastWriteTimeUtc, overwrite, progress, ct).ConfigureAwait(false);
            return res.Success;
        }

        /// <summary>
        /// 真正的流式上传文件并返回结构化明细 (包含 HTTP 状态码、业务 result、errno 与 errmsg)
        /// </summary>
        public async Task<FnOsUploadResult> UploadStreamDetailedAsync(
            string remotePath,
            Stream sourceStream,
            long fileSize,
            DateTime lastWriteTimeUtc,
            bool overwrite = true,
            IProgress<long>? progress = null,
            CancellationToken ct = default)
        {
            if (!IsAuthenticated || _hmacKeyBytes == null || string.IsNullOrEmpty(AuthToken))
                throw new InvalidOperationException("未登录鉴权，无法上传文件。");

            string scheme = _useSsl ? "https" : "http";
            string uploadUrl = $"{scheme}://{_host}:{_port}/upload";

            // 1. 规范化路径 (确保存储路径为 /vol{v}/{uid}/{subpath} 格式)
            string canonicalPath = remotePath.StartsWith("/") ? remotePath : "/" + remotePath;

            // 2. 调用 checkUpload 预检获取从服务端分配的断点与 uploadName
            long mtim = (long)(lastWriteTimeUtc - DateTime.UnixEpoch).TotalSeconds;
            long fromOffset = 0;
            string uploadPath = canonicalPath;

            try
            {
                var checkRes = await CheckUploadAsync(canonicalPath, fileSize, mtim, ct).ConfigureAwait(false);
                if (checkRes.Result == "fail" || (checkRes.ErrNo.HasValue && checkRes.ErrNo.Value != 0))
                {
                    if (checkRes.ErrNo == 4104)
                    {
                        return FnOsUploadResult.Fail(499, "fail", 4104, "Path Is Too Long", $"checkUpload 预检失败：文件名超长 (errno: 4104, errmsg: {checkRes.ErrMsg ?? "Path Is Too Long"})");
                    }
                    return FnOsUploadResult.Fail(400, "fail", checkRes.ErrNo, checkRes.ErrMsg, $"checkUpload 预检返回失败: errno={checkRes.ErrNo}, msg={checkRes.ErrMsg}");
                }

                if (checkRes.FromOffset.HasValue) fromOffset = checkRes.FromOffset.Value;
                if (!string.IsNullOrEmpty(checkRes.UploadName))
                {
                    string parentDir = Path.GetDirectoryName(canonicalPath.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
                    uploadPath = string.IsNullOrEmpty(parentDir) ? checkRes.UploadName : $"{parentDir}/{checkRes.UploadName}";
                }
                else if (!string.IsNullOrEmpty(checkRes.ErrMsg) && checkRes.ErrMsg.Contains("~#"))
                {
                    string parentDir = Path.GetDirectoryName(canonicalPath.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
                    uploadPath = string.IsNullOrEmpty(parentDir) ? checkRes.ErrMsg : $"{parentDir}/{checkRes.ErrMsg}";
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                if (ex.Message.Contains("4104") || ex.Message.Contains("Path Is Too Long"))
                {
                    return FnOsUploadResult.Fail(499, "fail", 4104, "Path Is Too Long", $"checkUpload 预检异常: {ex.Message}");
                }
                // 其他非关键异常降级使用规范化路径
            }

            // 3. 计算 Trim-Sign: HMAC-SHA256(SecretKey, Trim-Path)
            string trimSign = FnOsCryptoHelper.ComputeTrimUploadSign(_hmacKeyBytes, uploadPath);

            // 4. 构建标准 multipart 表单 (避免带引号的 boundary 导致服务端解析失败)
            using var content = new MultipartFormDataContent();
            if (content.Headers.ContentType != null)
            {
                foreach (var param in content.Headers.ContentType.Parameters)
                {
                    if (param.Name == "boundary")
                    {
                        param.Value = param.Value?.Trim('\"');
                    }
                }
            }

            if (fromOffset > 0 && sourceStream.CanSeek)
            {
                sourceStream.Seek(fromOffset, SeekOrigin.Begin);
            }

            // 5. 真正的分块流式包装：使用 ProgressReportingStream 逐块通知进度，不占用额外大内存
            Stream payloadStream = progress != null
                ? new ProgressReportingStream(sourceStream, progress, fromOffset)
                : sourceStream;

            var streamContent = new StreamContent(payloadStream, bufferSize: 64 * 1024);
            streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(streamContent, "trim-upload-file", "upload");

            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl)
            {
                Content = content
            };

            // 严格拒绝 CR、LF、NULL 等控制字符，防止 HTTP 请求头注入
            if (uploadPath.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            {
                throw new ArgumentException("Trim-Path 包含非法控制字符，已被安全拒绝。", nameof(remotePath));
            }

            request.Headers.TryAddWithoutValidation("Trim-Path", uploadPath);
            request.Headers.Add("Trim-From", fromOffset.ToString());
            request.Headers.Add("Trim-Overwrite", overwrite ? "true" : "false");
            request.Headers.Add("Trim-Mtim", mtim.ToString());
            request.Headers.Add("Trim-Token", AuthToken);
            request.Headers.Add("Trim-Sign", trimSign);

            using var resp = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string errContent = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                int httpCode = (int)resp.StatusCode;
                int? errNo = null;
                string? errMsg = resp.ReasonPhrase;
                string? result = "fail";

                try
                {
                    using var errDoc = JsonDocument.Parse(errContent);
                    if (errDoc.RootElement.TryGetProperty("errno", out var ep)) errNo = ep.GetInt32();
                    if (errDoc.RootElement.TryGetProperty("errmsg", out var emp)) errMsg = emp.GetString();
                    if (errDoc.RootElement.TryGetProperty("result", out var rp)) result = rp.GetString();
                }
                catch { }

                if (httpCode == 499 || resp.ReasonPhrase?.Contains("Path Is Too Long") == true)
                {
                    errNo ??= 4104;
                    errMsg ??= "Path Is Too Long";
                }

                return FnOsUploadResult.Fail(httpCode, result, errNo, errMsg, $"HTTP {httpCode} {resp.ReasonPhrase}: {errContent}");
            }

            // 校验业务响应内容
            string respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(respBody))
            {
                try
                {
                    using var doc = JsonDocument.Parse(respBody);
                    string? resStr = doc.RootElement.TryGetProperty("result", out var resProp) ? resProp.GetString() : null;
                    int? errNo = doc.RootElement.TryGetProperty("errno", out var errNoProp) ? errNoProp.GetInt32() : null;
                    string? errMsg = doc.RootElement.TryGetProperty("errmsg", out var msgProp) ? msgProp.GetString() : null;

                    if (resStr == "fail" || (errNo.HasValue && errNo.Value != 0))
                    {
                        return FnOsUploadResult.Fail(200, resStr ?? "fail", errNo, errMsg, respBody);
                    }
                }
                catch { }
            }

            return FnOsUploadResult.Ok();
        }

        /// <summary>
        /// 下载文件获取原生二进制流 (调用 POST /multiple-download 获取 Token，紧随 GET 流式读取)
        /// </summary>
        public async Task<byte[]> DownloadFileAsync(string remotePath, CancellationToken ct = default)
        {
            using var stream = await DownloadStreamAsync(remotePath, ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct).ConfigureAwait(false);
            return ms.ToArray();
        }

        /// <summary>
        /// 真正的流式下载远端文件 (返回未加载至内存的原生 Stream，支持逐块消费 100MB+ 大文件)
        /// </summary>
        public async Task<Stream> DownloadStreamAsync(string remotePath, CancellationToken ct = default)
        {
            if (!IsAuthenticated || string.IsNullOrEmpty(AuthToken))
                throw new InvalidOperationException("未登录鉴权，无法下载文件。");

            string scheme = _useSsl ? "https" : "http";
            string baseUrl = $"{scheme}://{_host}:{_port}/multiple-download";

            // 1. 获取临时下载流令牌 (noZipWhenSingleFile: true 确保单文件直接输出未压缩原字节流)
            string normalizedRemotePath = remotePath.StartsWith("/") ? remotePath : "/" + remotePath;
            var dlReq = new FnOsMultipleDownloadRequest
            {
                Token = AuthToken,
                Paths = new List<string> { normalizedRemotePath },
                DownloadLimit = 2,
                NoZipWhenSingleFile = true,
                ZipFileName = Path.GetFileName(remotePath)
            };

            string jsonReq = JsonSerializer.Serialize(dlReq);
            using var postReq = new HttpRequestMessage(HttpMethod.Post, baseUrl)
            {
                Content = new StringContent(jsonReq, Encoding.UTF8, "application/json")
            };

            using var postResp = await _httpClient.SendAsync(postReq, ct).ConfigureAwait(false);
            postResp.EnsureSuccessStatusCode();
            string postRespJson = await postResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var dlTokenObj = JsonSerializer.Deserialize<FnOsMultipleDownloadResponse>(postRespJson);

            if (string.IsNullOrEmpty(dlTokenObj?.Token))
            {
                throw new InvalidOperationException("获取下载令牌失败");
            }

            // 2. GET 下载原生二进制数据流 (附带认证 Cookie 与 Token)
            string streamUrl = $"{baseUrl}?token={dlTokenObj.Token}";
            var getReq = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            getReq.Headers.Add("Cookie", $"fnos-token={AuthToken}");

            var streamResp = await _httpClient.SendAsync(getReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            streamResp.EnsureSuccessStatusCode();

            return await streamResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 删除指定文件或目录 (支持移入回收站)
        /// </summary>
        public async Task<FnOsGenericRpcResponse> DeleteFileOrDirAsync(string remotePath, bool moveToTrash = true, CancellationToken ct = default)
        {
            string canonicalPath = remotePath.StartsWith("/") ? remotePath : "/" + remotePath;
            var payload = new Dictionary<string, object?>
            {
                ["files"] = new[] { canonicalPath },
                ["moveToTrashbin"] = moveToTrash
            };
            string responseJson = await SendSignedRpcAsync("file.rm", payload, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<FnOsGenericRpcResponse>(responseJson)
                   ?? new FnOsGenericRpcResponse { Result = "failed" };
        }

        public async ValueTask DisposeAsync()
        {
            await StopHeartbeatAsync().ConfigureAwait(false);
            _receiveCts?.Cancel();
            if (_receiveLoopTask != null)
            {
                try { await _receiveLoopTask; } catch { }
            }

            if (_webSocket != null)
            {
                try
                {
                    if (_webSocket.State == WebSocketState.Open)
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token).ConfigureAwait(false);
                    }
                }
                catch { }
                try { _webSocket.Dispose(); } catch { }
                _webSocket = null;
            }

            _httpClient.Dispose();
            _httpHandler.Dispose();
            _wsLock.Dispose();
            _receiveCts?.Dispose();
        }
    }
}
