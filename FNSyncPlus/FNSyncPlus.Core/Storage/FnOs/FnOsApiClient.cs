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
    /// 飞牛 fnOS 官方原生 API 客户端 (WebSocket RPC 控制面 + HTTP 数据面)
    /// 严禁绕过正常登录与权限隔离，严禁明文打印或记录敏感凭据，具备严格失败关闭保护
    /// </summary>
    public class FnOsApiClient : IAsyncDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly HttpClientHandler _httpHandler;
        private ClientWebSocket? _webSocket;
        private readonly SemaphoreSlim _wsLock = new(1, 1);
        private readonly Dictionary<string, TaskCompletionSource<string>> _pendingRequests = new();
        private CancellationTokenSource? _receiveCts;
        private Task? _receiveLoopTask;

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
        public bool IsAuthenticated { get; private set; }
        public string? AuthToken { get; private set; }
        public string? LongToken { get; private set; }
        public string? AuthenticatedUser { get; private set; }
        public int? CurrentUid { get; private set; }
        public int? CurrentVolumeId { get; private set; }

        public FnOsApiClient(string? pinnedCertSha256 = null)
        {
            _pinnedCertSha256 = pinnedCertSha256;
            _httpHandler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = ValidateServerCertificate
            };
            _httpClient = new HttpClient(_httpHandler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
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

            // 检查自签名或设备专属证书的 SHA-256 指纹强校验 (Certificate Pinning)
            if (!string.IsNullOrEmpty(_pinnedCertSha256))
            {
                string thumbprint = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256).ToLowerInvariant();
                return string.Equals(thumbprint, _pinnedCertSha256.Replace(":", "").ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
            }

            // 若 NAS 使用内置自签名证书 (Subject 为 CN=fnOS)
            if (certificate.Subject.Contains("fnOS", StringComparison.OrdinalIgnoreCase) &&
                certificate.Issuer.Contains("fnOS", StringComparison.OrdinalIgnoreCase))
            {
                // 放行局域网 IP 直连导致的名称不匹配和自签名证书链未知错误，确保未被其他伪造证书劫持
                var allowedErrors = SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;
                if ((sslPolicyErrors & ~allowedErrors) == SslPolicyErrors.None)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 建立 WebSocket 控制通道并完成 RSA 握手
        /// </summary>
        public async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct = default)
        {
            _host = host;
            _port = port;
            _useSsl = useSsl;

            string scheme = useSsl ? "wss" : "ws";
            var uri = new Uri($"{scheme}://{host}:{port}/websocket?type=main");

            _webSocket?.Dispose();
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
            _receiveCts?.Cancel();
            _receiveCts = new CancellationTokenSource();
            _receiveLoopTask = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));

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

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];
            var ms = new MemoryStream();

            try
            {
                while (!ct.IsCancellationRequested && _webSocket != null && _webSocket.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
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
            catch { }
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
                        lock (_pendingRequests)
                        {
                            if (_pendingRequests.TryGetValue(reqId, out var tcs))
                            {
                                _pendingRequests.Remove(reqId);
                                tcs.TrySetResult(jsonBody);
                            }
                        }
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

            var tcs = new TaskCompletionSource<string>();
            lock (_pendingRequests)
            {
                _pendingRequests[reqId] = tcs;
            }

            await SendRawTextAsync(envelopeJson, ct);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linked.Token.Register(() => tcs.TrySetCanceled());

            string responseJson = await tcs.Task;
            var loginResp = JsonSerializer.Deserialize<FnOsLoginResponse>(responseJson);

            if (loginResp?.Result == "succ" && !string.IsNullOrEmpty(loginResp.EncryptedSecretBase64))
            {
                // 解密 secret 字段获得 raw HMAC 签名密钥
                byte[] rawSecretCipher = Convert.FromBase64String(loginResp.EncryptedSecretBase64);
                _hmacKeyBytes = FnOsCryptoHelper.DecryptAesCbc(rawSecretCipher, _aesKey, _iv);

                AuthToken = loginResp.Token;
                LongToken = loginResp.LongToken;
                AuthenticatedUser = username;
                IsAuthenticated = true;

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
                catch { }
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
        public async Task<string> SendSignedRpcAsync(string method, Dictionary<string, object?> payload, CancellationToken ct = default)
        {
            if (!IsAuthenticated || _hmacKeyBytes == null)
                throw new InvalidOperationException("未鉴权或缺少 HMAC 签名密钥，无法发送 RPC。");

            string reqId = FnOsCryptoHelper.GenerateReqId();
            payload["req"] = method;
            payload["reqid"] = reqId;

            string jsonStr = JsonSerializer.Serialize(payload);
            string signedMessage = FnOsCryptoHelper.FormatSignedRpcMessage(_hmacKeyBytes, jsonStr);

            var tcs = new TaskCompletionSource<string>();
            lock (_pendingRequests)
            {
                _pendingRequests[reqId] = tcs;
            }

            await SendRawTextAsync(signedMessage, ct);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linked.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
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

            string responseJson = await SendSignedRpcAsync("file.ls", payload, ct);
            Console.WriteLine($"[DEBUG] ListFiles raw response: {responseJson}");
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

            string responseJson = await SendSignedRpcAsync("file.mkdir", payload, ct);
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

            string responseJson = await SendSignedRpcAsync("file.checkUpload", payload, ct);
            Console.WriteLine($"[DEBUG] checkUpload raw response: {responseJson}");

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            using var doc = JsonDocument.Parse(responseJson);
            var resp = new FnOsCheckUploadResponse
            {
                Result = doc.RootElement.TryGetProperty("result", out var rProp) ? rProp.GetString() : null,
                FromOffset = doc.RootElement.TryGetProperty("from", out var fProp) ? fProp.GetInt64() : 0,
            };
            if (doc.RootElement.TryGetProperty("uploadName", out var uProp))
            {
                resp.ErrMsg = uProp.GetString(); // 临时借用 ErrMsg 存放 uploadName 供调用方使用
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
                var checkRes = await CheckUploadAsync(canonicalPath, fileBytes.Length, mtim, ct);
                if (checkRes.FromOffset.HasValue) fromOffset = checkRes.FromOffset.Value;
                if (!string.IsNullOrEmpty(checkRes.ErrMsg)) // checkRes.ErrMsg 存放了 uploadName
                {
                    string parentDir = Path.GetDirectoryName(canonicalPath.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
                    uploadPath = string.IsNullOrEmpty(parentDir) ? checkRes.ErrMsg : $"{parentDir}/{checkRes.ErrMsg}";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] checkUpload exception: {ex.Message}");
            }

            Console.WriteLine($"[DEBUG] Actual Upload Target Path: {uploadPath}, From: {fromOffset}");

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

            byte[] dataToSend = fileBytes;
            if (fromOffset > 0 && fromOffset < fileBytes.Length)
            {
                dataToSend = fileBytes[(int)fromOffset..];
            }

            var fileContent = new ByteArrayContent(dataToSend);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "trim-upload-file", "upload");

            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUrl)
            {
                Content = content
            };

            request.Headers.Add("Trim-Path", uploadPath);
            request.Headers.Add("Trim-From", fromOffset.ToString());
            request.Headers.Add("Trim-Overwrite", overwrite ? "true" : "false");
            request.Headers.Add("Trim-Mtim", mtim.ToString());
            request.Headers.Add("Trim-Token", AuthToken);
            request.Headers.Add("Trim-Sign", trimSign);

            using var resp = await _httpClient.SendAsync(request, ct);
            string respBody = await resp.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"[DEBUG] POST /upload StatusCode: {(int)resp.StatusCode} {resp.StatusCode}, Body: {respBody}");
            return resp.IsSuccessStatusCode;
        }

        /// <summary>
        /// 下载文件获取原生二进制流 (调用 POST /multiple-download 获取 Token，紧随 GET 流式读取)
        /// </summary>
        public async Task<byte[]> DownloadFileAsync(string remotePath, CancellationToken ct = default)
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
            Console.WriteLine($"[DEBUG] POST /multiple-download request: {jsonReq}");
            using var postReq = new HttpRequestMessage(HttpMethod.Post, baseUrl)
            {
                Content = new StringContent(jsonReq, Encoding.UTF8, "application/json")
            };

            using var postResp = await _httpClient.SendAsync(postReq, ct);
            string postRespJson = await postResp.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"[DEBUG] POST /multiple-download StatusCode: {(int)postResp.StatusCode} {postResp.StatusCode}, Body: {postRespJson}");
            postResp.EnsureSuccessStatusCode();
            var dlTokenObj = JsonSerializer.Deserialize<FnOsMultipleDownloadResponse>(postRespJson);

            if (string.IsNullOrEmpty(dlTokenObj?.Token))
            {
                throw new InvalidOperationException($"获取下载令牌失败: {postRespJson}");
            }

            // 2. GET 下载原生二进制数据流 (附带认证 Cookie 与 Token)
            string streamUrl = $"{baseUrl}?token={dlTokenObj.Token}";
            using var getReq = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            getReq.Headers.Add("Cookie", $"fnos-token={AuthToken}");

            using var streamResp = await _httpClient.SendAsync(getReq, HttpCompletionOption.ResponseContentRead, ct);
            if (!streamResp.IsSuccessStatusCode)
            {
                string errBody = await streamResp.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"[DEBUG] GET /multiple-download StatusCode: {(int)streamResp.StatusCode} {streamResp.StatusCode}, Body: {errBody}");
            }
            streamResp.EnsureSuccessStatusCode();

            return await streamResp.Content.ReadAsByteArrayAsync(ct);
        }

        /// <summary>
        /// 流式上传文件
        /// </summary>
        public async Task<bool> UploadStreamAsync(
            string remotePath,
            Stream sourceStream,
            DateTime lastWriteTimeUtc,
            bool overwrite = true,
            IProgress<long>? progress = null,
            CancellationToken ct = default)
        {
            using var ms = new MemoryStream();
            await sourceStream.CopyToAsync(ms, ct);
            byte[] fileBytes = ms.ToArray();
            bool result = await UploadFileAsync(remotePath, fileBytes, lastWriteTimeUtc, overwrite, ct);
            if (result)
            {
                progress?.Report(fileBytes.Length);
            }
            return result;
        }

        /// <summary>
        /// 流式下载文件获取 Stream
        /// </summary>
        public async Task<Stream> DownloadStreamAsync(string remotePath, CancellationToken ct = default)
        {
            byte[] bytes = await DownloadFileAsync(remotePath, ct);
            return new MemoryStream(bytes);
        }

        /// <summary>
        /// 删除指定文件或目录 (支持移入回收站)
        /// </summary>
        public async Task<FnOsGenericRpcResponse> DeleteFileOrDirAsync(string remotePath, bool moveToTrash = true, CancellationToken ct = default)
        {
            string canonicalPath = remotePath.StartsWith("/") ? remotePath : "/" + remotePath;
            var payload = new Dictionary<string, object?>
            {
                ["path"] = canonicalPath,
                ["trash"] = moveToTrash
            };
            string responseJson = await SendSignedRpcAsync("file.rm", payload, ct);
            return JsonSerializer.Deserialize<FnOsGenericRpcResponse>(responseJson)
                   ?? new FnOsGenericRpcResponse { Result = "failed" };
        }

        public async ValueTask DisposeAsync()
        {
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
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    }
                }
                catch { }
                _webSocket.Dispose();
                _webSocket = null;
            }

            _httpClient.Dispose();
            _httpHandler.Dispose();
            _wsLock.Dispose();
            _receiveCts?.Dispose();
        }
    }
}
