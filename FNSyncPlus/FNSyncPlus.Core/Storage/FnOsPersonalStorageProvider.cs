using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Storage.FnOs;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Storage
{
    /// <summary>
    /// 飞牛 fnOS 账号原生个人空间存储提供程序 (「我的文件」)
    /// 严格遵循官方 API 权限模型，直接对接个人空间，杜绝团队共享混用与伪造用户名路径
    /// </summary>
    public class FnOsPersonalStorageProvider : INasStorageProvider, IExclusiveDirectoryProvider, IExperimentalFileMoveProvider, IExperimentalContentReuseProvider, IExperimentalCandidateDirectoryMover
    {
        private readonly FnOsApiClient _apiClient;
        private string? _personalRootPath; // 例如 vol1/1006
        private string? _serverHost;
        private int _serverPort;
        private bool _disposed;

        public StorageProviderType ProviderType => StorageProviderType.FnOsPersonal;
        public bool IsAuthenticated => _apiClient.IsAuthenticated;
        public event Action? ConnectionLost
        {
            add => _apiClient.ConnectionLost += value;
            remove => _apiClient.ConnectionLost -= value;
        }
        public string RootDisplayPath => string.IsNullOrEmpty(_personalRootPath)
            ? "飞牛官方个人空间 [我的文件]"
            : $"飞牛官方个人空间 [我的文件] ({_personalRootPath})";

        public int? CurrentUid => _apiClient.CurrentUid;
        public int? CurrentVolumeId => _apiClient.CurrentVolumeId;
        public string? CurrentUsername => _apiClient.AuthenticatedUser;

        // Address comparison is conservative: aliases for the same device must be reconfigured explicitly.
        public bool MatchesServerAddress(string server)
        {
            if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(_serverHost)) return false;
            ParseServerAddress(server, out string host, out int port, out bool useSsl);
            return useSsl && port == _serverPort && string.Equals(host, _serverHost, StringComparison.OrdinalIgnoreCase);
        }

        public FnOsPersonalStorageProvider(string? pinnedCertSha256 = null)
        {
            _apiClient = new FnOsApiClient(pinnedCertSha256);
        }

        public FnOsPersonalStorageProvider(FnOsApiClient apiClient)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            if (_apiClient.IsAuthenticated && _apiClient.CurrentUid.HasValue)
            {
                _personalRootPath = ResolveRootFromAuthenticatedUid();
            }
        }

        public async Task<StorageAuthResult> ConnectAndAuthenticateAsync(
            string server,
            string username,
            string password,
            string? twoFactorCode = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(server))
                throw new ArgumentException("NAS 服务器地址不能为空", nameof(server));
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("用户名不能为空", nameof(username));

            // 解析主机名与端口
            ParseServerAddress(server, out string host, out int port, out bool useSsl);
            _serverHost = host;
            _serverPort = port;

            try
            {
                // 1. 建立 WebSocket 控制通道与密钥协商
                await _apiClient.ConnectAsync(host, port, useSsl, ct).ConfigureAwait(false);

                // 2. 发起加密鉴权登录
                var loginResp = await _apiClient.LoginAsync(username, password, ct).ConfigureAwait(false);

                if (loginResp.Result != "succ")
                {
                    if (loginResp.IsBindTwofaSecret == true)
                    {
                        if (!string.IsNullOrEmpty(twoFactorCode) && !string.IsNullOrEmpty(loginResp.AccessToken))
                        {
                            // 若调用方已提供两步验证码，直接执行二次鉴权
                            return await VerifyTwoFactorCodeAsync(loginResp.AccessToken, twoFactorCode, username, ct).ConfigureAwait(false);
                        }

                        return new StorageAuthResult
                        {
                            Success = false,
                            TwoFactorRequired = true,
                            TwoFactorAccessToken = loginResp.AccessToken,
                            ErrorMessage = "账号已启用两步验证 (2FA)，请输入 6 位动态验证码。"
                        };
                    }

                    string err = loginResp.ErrMsg ?? $"登录失败 (错误代码: {loginResp.ErrNo})";
                    return new StorageAuthResult
                    {
                        Success = false,
                        ErrorMessage = err
                    };
                }

                // 3. 校验并获取个人空间根路径 (vol{v}/{uid})
                int uid = _apiClient.CurrentUid ?? throw new InvalidOperationException("登录成功但未能解析用户权威 UID。");
                int vol = _apiClient.CurrentVolumeId ?? 1;
                _personalRootPath = $"vol{vol}/{uid}";

                // 4. 验证「我的文件」根目录可访问性
                var rootLs = await _apiClient.ListFilesAsync(null, ct).ConfigureAwait(false);
                if (rootLs.Files == null && rootLs.Result != "succ")
                {
                    return new StorageAuthResult
                    {
                        Success = false,
                        ErrorMessage = $"登录成功但个人空间根目录不可访问: {rootLs.ErrMsg ?? "未知错误"}"
                    };
                }

                return new StorageAuthResult
                {
                    Success = true,
                    AuthenticatedUser = username,
                    UserUid = uid,
                    PersonalRootPath = _personalRootPath
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException) { throw; }
            catch (Exception ex)
            {
                return new StorageAuthResult
                {
                    Success = false,
                    ErrorMessage = $"连接飞牛个人空间异常: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 验证并提交 TOTP 两步验证码
        /// </summary>
        public async Task<StorageAuthResult> VerifyTwoFactorCodeAsync(
            string accessToken,
            string twoFactorCode,
            string username,
            CancellationToken ct = default)
        {
            try
            {
                var loginResp = await _apiClient.LoginWithTwoFactorAsync(accessToken, twoFactorCode, username, ct).ConfigureAwait(false);
                if (loginResp.Result != "succ")
                {
                    return new StorageAuthResult
                    {
                        Success = false,
                        ErrorMessage = loginResp.ErrMsg ?? "两步验证失败，请确认验证码是否有效或已过期。"
                    };
                }

                int uid = _apiClient.CurrentUid ?? throw new InvalidOperationException("两步验证登录成功但未能解析权威 UID。");
                int vol = _apiClient.CurrentVolumeId ?? 1;
                _personalRootPath = $"vol{vol}/{uid}";

                return new StorageAuthResult
                {
                    Success = true,
                    AuthenticatedUser = username,
                    UserUid = uid,
                    PersonalRootPath = _personalRootPath
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return new StorageAuthResult
                {
                    Success = false,
                    ErrorMessage = $"两步验证异常: {ex.Message}"
                };
            }
        }

        public async Task<IReadOnlyList<NasStorageItem>> ListEntriesAsync(string? relativePath = null, CancellationToken ct = default)
        {
            EnsureAuthenticated();

            string? queryPath = null;
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                queryPath = CombineWithPersonalRoot(relativePath);
            }

            var resp = await _apiClient.ListFilesAsync(queryPath, ct).ConfigureAwait(false);
            if (!resp.IsSuccess)
                throw new IOException($"读取远端目录失败: {resp.ErrMsg ?? resp.Result ?? "无响应"} (errno: {resp.ErrNo})");
            var results = new List<NasStorageItem>();

            if (resp.Files != null)
            {
                foreach (var f in resp.Files)
                {
                    string itemRelPath = string.IsNullOrWhiteSpace(relativePath)
                        ? f.Name
                        : $"{relativePath.TrimEnd('/', '\\').Replace('\\', '/')}/{f.Name}";

                    results.Add(new NasStorageItem
                    {
                        Name = f.Name,
                        RelativePath = itemRelPath,
                        IsDirectory = f.IsDirectory,
                        Size = f.Size,
                        LastModifiedUtc = f.LastModifiedUtc,
                        VolumeId = f.VolumeId?.ToString(),
                        OwnerUid = f.Uid
                    });
                }
            }

            return results;
        }

        public async Task<ExclusiveDirectoryResult> TryCreateDirectoryExclusiveAsync(string relativePath, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrEmpty(NormalizeRelativePath(relativePath)))
                throw new ArgumentException("独占目录路径不能为空或根目录", nameof(relativePath));
            string fullPath = CombineWithPersonalRoot(relativePath);
            var response = await _apiClient.CreateDirectoryAsync(fullPath, ct).ConfigureAwait(false);
            if (response.Result == "succ" && (response.ErrNo == null || response.ErrNo == 0))
                return ExclusiveDirectoryResult.Created;
            // 4102 was observed for an existing directory, but verify the target before treating it as a collision.
            if (response.ErrNo == 4102)
            {
                var existing = await GetItemAsync(relativePath, ct).ConfigureAwait(false);
                if (existing?.IsDirectory == true) return ExclusiveDirectoryResult.AlreadyExists;
            }
            throw new IOException($"独占创建目录失败 (errno: {response.ErrNo})");
        }

        public async Task<StorageOperationResult> CreateDirectoryAsync(string relativePath, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(relativePath))
                return StorageOperationResult.Fail("目录路径不能为空");

            // 严格安全边界校验：任何包含 .. 或非法组件的越界遍历直接拦截并抛出 ArgumentException
            string validatedFullPath = CombineWithPersonalRoot(relativePath);
            string clean = NormalizeRelativePath(relativePath);

            if (string.IsNullOrEmpty(clean))
            {
                return StorageOperationResult.Ok(CombineWithPersonalRoot(""));
            }

            // 逐级检查并创建，确保查询每一级时其父目录已经存在。
            string[] segments = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string currentRel = "";

            for (int i = 0; i < segments.Length; i++)
            {
                currentRel = string.IsNullOrEmpty(currentRel) ? segments[i] : $"{currentRel}/{segments[i]}";
                string fullPath = CombineWithPersonalRoot(currentRel);

                // 检查当前层级是否已存在
                var segItem = await GetItemAsync(currentRel, ct).ConfigureAwait(false);
                if (segItem != null)
                {
                    if (!segItem.IsDirectory)
                    {
                        return StorageOperationResult.Fail($"路径冲突：上级路径 '{currentRel}' 是文件而非目录。", 17, fullPath);
                    }
                    continue; // 当前层级已存在且是目录，继续下一级
                }

                // 调用 RPC 创建当前层级目录
                var resp = await _apiClient.CreateDirectoryAsync(fullPath, ct).ConfigureAwait(false);

                if (resp.Result == "succ" && (resp.ErrNo == null || resp.ErrNo == 0))
                {
                    continue;
                }

                // 容错二次确认：若 RPC 返回失败，再次检查远端是否实际已被创建（例如并发创建或已存在而状态码非 17）
                var recheck = await GetItemAsync(currentRel, ct).ConfigureAwait(false);
                if (recheck != null && recheck.IsDirectory)
                {
                    continue;
                }

                return StorageOperationResult.Fail(
                    $"创建个人空间目标目录失败: {resp.ErrMsg ?? "未知错误"} (错误码: {resp.ErrNo})",
                    resp.ErrNo,
                    fullPath);
            }

            return StorageOperationResult.Ok(validatedFullPath);
        }

        public async Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(relativePath)) return true;

            var item = await GetItemAsync(relativePath, ct).ConfigureAwait(false);
            return item != null;
        }

        public async Task<NasStorageItem?> GetItemAsync(string relativePath, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return new NasStorageItem
                {
                    Name = "我的文件",
                    RelativePath = "",
                    IsDirectory = true
                };
            }

            // 严格安全边界校验：任何包含 .. 或非法组件的越界遍历直接拦截并抛出 ArgumentException
            CombineWithPersonalRoot(relativePath);

            string clean = NormalizeRelativePath(relativePath);
            if (string.IsNullOrEmpty(clean))
            {
                return new NasStorageItem
                {
                    Name = "我的文件",
                    RelativePath = "",
                    IsDirectory = true
                };
            }

            string? parentDir = Path.GetDirectoryName(clean)?.Replace('\\', '/');
            string targetName = Path.GetFileName(clean);

            if (string.IsNullOrEmpty(targetName))
            {
                return new NasStorageItem
                {
                    Name = "我的文件",
                    RelativePath = "",
                    IsDirectory = true
                };
            }

            try
            {
                var entries = await ListEntriesAsync(string.IsNullOrEmpty(parentDir) ? null : parentDir, ct).ConfigureAwait(false);
                foreach (var item in entries)
                {
                    if (string.Equals(item.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        return item;
                    }
                }
                return null;
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new IOException($"无法读取远端目录以确认路径是否存在: {relativePath}", ex);
            }
        }

        public async Task<StorageOperationResult> UploadFileAsync(
            string relativePath,
            Stream sourceStream,
            long fileSize,
            DateTime lastWriteTimeUtc,
            bool overwrite = true,
            IProgress<long>? progress = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(relativePath))
                return StorageOperationResult.Fail("上传目标路径不能为空");

            string fullPath = CombineWithPersonalRoot(relativePath);

            try
            {
                // fnOS has been observed overwriting even when Trim-Overwrite is false.
                // This check prevents an already-existing file from being replaced, but is not
                // an atomic create and must not be used as a cross-device lock.
                if (!overwrite && await GetItemAsync(relativePath, ct).ConfigureAwait(false) != null)
                    return StorageOperationResult.Fail("目标已存在，拒绝覆盖；飞牛不支持已验证的原子独占上传", 17, fullPath);

                // 1. 真正的分块流式上传并校验业务响应
                var uploadRes = await _apiClient.UploadStreamDetailedAsync(
                    fullPath,
                    sourceStream,
                    fileSize,
                    lastWriteTimeUtc,
                    overwrite,
                    progress,
                    ct).ConfigureAwait(false);

                if (!uploadRes.Success)
                {
                    string detail = !string.IsNullOrEmpty(uploadRes.ErrMsg) ? uploadRes.ErrMsg : uploadRes.DetailMessage ?? "未知错误";
                    string err = $"[fnOS 上传失败] HTTP {uploadRes.HttpStatusCode} (result: {uploadRes.Result ?? "fail"}), errno: {uploadRes.ErrNo}, errmsg: {detail}";
                    return StorageOperationResult.Fail(err, uploadRes.ErrNo, fullPath);
                }

                // 2. 上传完成后的物理存在与文件大小强校验，杜绝虚假成功
                string clean = NormalizeRelativePath(relativePath);
                string parentRel = Path.GetDirectoryName(clean)?.Replace('\\', '/') ?? "";
                string fileName = Path.GetFileName(clean);

                bool verified = false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (attempt > 0) await Task.Delay(100, ct).ConfigureAwait(false);
                    var entries = await ListEntriesAsync(parentRel, ct).ConfigureAwait(false);
                    var found = entries.FirstOrDefault(e => !e.IsDirectory && string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        if (found.Size == fileSize)
                        {
                            verified = true;
                            break;
                        }
                        else
                        {
                            return StorageOperationResult.Fail($"上传后远端物理大小不一致 (期望: {fileSize}, 实际: {found.Size})", path: fullPath);
                        }
                    }
                }

                if (!verified)
                {
                    return StorageOperationResult.Fail("上传请求已发送但在远端目录中未能在时限内检索到目标物理实体", path: fullPath);
                }

                return StorageOperationResult.Ok(fullPath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return StorageOperationResult.Fail($"上传异常: {ex.Message}", path: fullPath);
            }
        }

        public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("读取路径不能为空", nameof(relativePath));

            string fullPath = CombineWithPersonalRoot(relativePath);
            return await _apiClient.DownloadStreamAsync(fullPath, ct).ConfigureAwait(false);
        }

        public async Task<bool> VerifyFileIntegrityAsync(
            string relativePath,
            string expectedSha256,
            long expectedSize,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            try
            {
                using var stream = await OpenReadAsync(relativePath, ct).ConfigureAwait(false);
                using var sha256 = SHA256.Create();
                byte[] hashBytes = await Task.Run(() => sha256.ComputeHash(stream), ct).ConfigureAwait(false);
                string actualHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

                return string.Equals(actualHash, expectedSha256.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Experimental no-overwrite move of one verified file. The final RPC response and both paths are checked;
        /// server concurrency and crash semantics are not established, so production tasks must not use this yet.
        /// </summary>
        public async Task<VerifiedMoveOutcome> MoveFileVerifiedAsync(string sourcePath, string destinationDirectory,
            string expectedSha256, long expectedSize, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            string source = NormalizeRelativePath(sourcePath);
            string directory = NormalizeRelativePath(destinationDirectory);
            if (!PathHelper.IsSafeRelativePath(source) || !PathHelper.IsSafeRelativePath(directory))
                throw new ArgumentException("移动路径必须是个人空间内的非空安全相对路径");
            if (expectedSize < 0 || expectedSha256 == null || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("移动前必须提供有效的文件大小和 SHA-256");
            int slash = source.LastIndexOf('/');
            string parent = slash < 0 ? "" : source[..slash];
            string name = slash < 0 ? source : source[(slash + 1)..];
            if (string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("源文件和目标目录相同");
            string destination = $"{directory}/{name}";
            var sourceItem = await GetItemAsync(source, ct).ConfigureAwait(false);
            bool sourceValid = sourceItem != null && !sourceItem.IsDirectory && sourceItem.Size == expectedSize &&
                await VerifyFileIntegrityAsync(source, expectedSha256, expectedSize, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!sourceValid)
                throw new InvalidDataException("待移动文件不存在或校验不一致");
            var targetDirectory = await GetItemAsync(directory, ct).ConfigureAwait(false);
            if (targetDirectory == null || !targetDirectory.IsDirectory)
                throw new DirectoryNotFoundException("移动目标目录不存在");
            if (await GetItemAsync(destination, ct).ConfigureAwait(false) != null) return VerifiedMoveOutcome.DestinationExists;
            ct.ThrowIfCancellationRequested();

            try
            {
                string json = await _apiClient.SendSignedTaskRpcAsync("file.mv", new Dictionary<string, object?> {
                    ["files"] = new[] { "/" + CombineWithPersonalRoot(source) },
                    ["pathTo"] = "/" + CombineWithPersonalRoot(directory),
                    ["overwrite"] = "skip",
                    ["details"] = new { name, count = 1 }
                }, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                using var response = JsonDocument.Parse(json);
                string? result = response.RootElement.TryGetProperty("result", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString() : null;
                var sourceAfter = await GetItemAsync(source, ct).ConfigureAwait(false);
                var destinationAfter = await GetItemAsync(destination, ct).ConfigureAwait(false);
                if (sourceAfter != null && !sourceAfter.IsDirectory && sourceAfter.Size == expectedSize &&
                    await VerifyFileIntegrityAsync(source, expectedSha256, expectedSize, ct).ConfigureAwait(false))
                    return VerifiedMoveOutcome.NotMoved;
                if (result == "succ" && sourceAfter == null && destinationAfter != null && !destinationAfter.IsDirectory &&
                    destinationAfter.Size == expectedSize && await VerifyFileIntegrityAsync(destination, expectedSha256, expectedSize, ct).ConfigureAwait(false))
                    return VerifiedMoveOutcome.Moved;
                throw new RemoteMoveUncertainException("移动结果无法确认，请检查源和目标后再继续");
            }
            catch (RemoteMoveUncertainException) { throw; }
            catch (Exception ex)
            {
                throw new RemoteMoveUncertainException("移动请求已发送或结果不明，请检查源和目标，不能自动重试", ex);
            }
        }

        /// <summary>Moves only a candidate's current directory into an empty backup root.</summary>
        public async Task<VerifiedMoveOutcome> MoveCandidateDirectoryAsync(string backupRoot, string candidateDirectory,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (!PathHelper.IsSafeRelativePath(backupRoot) || !PathHelper.IsSafeRelativePath(candidateDirectory))
                throw new ArgumentException("候选发布路径必须是安全的相对路径");
            string root = NormalizeRelativePath(backupRoot);
            string candidate = NormalizeRelativePath(candidateDirectory);
            string prefix = $"{root}/.fnsync/projections/candidate-";
            if (!PathHelper.IsSafeRelativePath(root) || !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(candidate[prefix.Length..], "N", out _))
                throw new ArgumentException("候选目录必须位于备份的专用候选目录下");
            string source = $"{candidate}/current";
            string destination = $"{root}/current";
            var sourceItem = await GetItemAsync(source, ct).ConfigureAwait(false);
            if (sourceItem == null || !sourceItem.IsDirectory) throw new DirectoryNotFoundException("候选文件树不存在");
            if (await GetItemAsync(destination, ct).ConfigureAwait(false) != null) return VerifiedMoveOutcome.DestinationExists;
            ct.ThrowIfCancellationRequested();
            try
            {
                string json = await _apiClient.SendSignedTaskRpcAsync("file.mv", new Dictionary<string, object?> {
                    ["files"] = new[] { "/" + CombineWithPersonalRoot(source) },
                    ["pathTo"] = "/" + CombineWithPersonalRoot(root), ["overwrite"] = "skip",
                    ["details"] = new { name = "current", count = 1 }
                }, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                using var response = JsonDocument.Parse(json);
                bool succeeded = response.RootElement.TryGetProperty("result", out var value) &&
                    value.ValueKind == JsonValueKind.String && value.GetString() == "succ";
                if (response.RootElement.TryGetProperty("errno", out var error) && error.ValueKind == JsonValueKind.Number &&
                    error.TryGetInt32(out int errorCode) && errorCode != 0) succeeded = false;
                var oldItem = await GetItemAsync(source, ct).ConfigureAwait(false);
                var newItem = await GetItemAsync(destination, ct).ConfigureAwait(false);
                if (succeeded && oldItem == null && newItem?.IsDirectory == true) return VerifiedMoveOutcome.Moved;
                if (oldItem?.IsDirectory == true && newItem == null) return VerifiedMoveOutcome.NotMoved;
                throw new RemoteMoveUncertainException("候选目录移动结果不确定，请检查后再处理");
            }
            catch (RemoteMoveUncertainException) { throw; }
            catch (Exception ex) { throw new RemoteMoveUncertainException("候选目录移动结果不确定，不能自动重试", ex); }
        }

        /// <summary>Moves the current tree only into an existing uniquely named archive directory.</summary>
        public async Task<VerifiedMoveOutcome> MoveCurrentToArchiveAsync(string backupRoot, string archiveDirectory,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (!PathHelper.IsSafeRelativePath(backupRoot) || !PathHelper.IsSafeRelativePath(archiveDirectory))
                throw new ArgumentException("归档路径必须是安全的相对路径");
            string root = NormalizeRelativePath(backupRoot);
            string archive = NormalizeRelativePath(archiveDirectory);
            string prefix = $"{root}/.fnsync/projections/archive-";
            if (!PathHelper.IsSafeRelativePath(root) || !archive.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(archive[prefix.Length..], "N", out _))
                throw new ArgumentException("归档目录必须位于备份的专用归档目录下");
            string source = $"{root}/current", destination = $"{archive}/current";
            var oldItem = await GetItemAsync(source, ct).ConfigureAwait(false);
            var archiveItem = await GetItemAsync(archive, ct).ConfigureAwait(false);
            if (oldItem?.IsDirectory != true || archiveItem?.IsDirectory != true)
                throw new DirectoryNotFoundException("当前目录或归档目录不存在");
            if (await GetItemAsync(destination, ct).ConfigureAwait(false) != null) return VerifiedMoveOutcome.DestinationExists;
            ct.ThrowIfCancellationRequested();
            try
            {
                string json = await _apiClient.SendSignedTaskRpcAsync("file.mv", new Dictionary<string, object?> {
                    ["files"] = new[] { "/" + CombineWithPersonalRoot(source) },
                    ["pathTo"] = "/" + CombineWithPersonalRoot(archive), ["overwrite"] = "skip",
                    ["details"] = new { name = "current", count = 1 }
                }, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
                using var response = JsonDocument.Parse(json);
                bool succeeded = response.RootElement.TryGetProperty("result", out var value) &&
                    value.ValueKind == JsonValueKind.String && value.GetString() == "succ";
                if (response.RootElement.TryGetProperty("errno", out var error) && error.ValueKind == JsonValueKind.Number &&
                    error.TryGetInt32(out int errorCode) && errorCode != 0) succeeded = false;
                var sourceAfter = await GetItemAsync(source, ct).ConfigureAwait(false);
                var destinationAfter = await GetItemAsync(destination, ct).ConfigureAwait(false);
                if (succeeded && sourceAfter == null && destinationAfter?.IsDirectory == true) return VerifiedMoveOutcome.Moved;
                if (sourceAfter?.IsDirectory == true && destinationAfter == null) return VerifiedMoveOutcome.NotMoved;
                throw new RemoteMoveUncertainException("当前目录归档结果不确定，请检查后再处理");
            }
            catch (RemoteMoveUncertainException) { throw; }
            catch (Exception ex) { throw new RemoteMoveUncertainException("当前目录归档结果不确定，不能自动重试", ex); }
        }

        /// <summary>
        /// Experimental server-side copy into a unique candidate tree. Each copy uses its own
        /// exclusive work directory; uncertain outcomes are left in place and never retried.
        /// </summary>
        public async Task CopyObjectToCandidateAsync(string backupRoot, string candidateRoot, string objectRel,
            string physicalRel, string expectedSha256, long expectedSize, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (!PathHelper.IsSafeRelativePath(backupRoot) || !PathHelper.IsSafeRelativePath(candidateRoot) ||
                !PathHelper.IsSafeRelativePath(objectRel) || !PathHelper.IsSafeRelativePath(physicalRel))
                throw new ArgumentException("候选复制路径必须是安全的相对路径");
            string root = NormalizeRelativePath(backupRoot);
            string candidate = NormalizeRelativePath(candidateRoot);
            string objectPath = PathHelper.NormalizeRelPath(objectRel);
            string physical = PathHelper.NormalizeRelPath(physicalRel);
            string candidatePrefix = $"{root}/.fnsync/projections/candidate-";
            if (!PathHelper.IsSafeRelativePath(root) || !PathHelper.IsSafeRelativePath(candidate) ||
                !PathHelper.IsSafeRelativePath(objectPath) || !PathHelper.IsSafeRelativePath(physical) ||
                !candidate.StartsWith(candidatePrefix, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(candidate[candidatePrefix.Length..], "N", out _) ||
                !objectPath.StartsWith(".fnsync/objects/obj-", StringComparison.Ordinal) ||
                expectedSize < 0 || expectedSha256?.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("候选复制路径或校验值无效");

            string source = $"{root}/{objectPath}";
            string target = $"{candidate}/current/{physical}";
            int separator = target.LastIndexOf('/');
            string targetDirectory = target[..separator];
            string desiredName = target[(separator + 1)..];
            var sourceItem = await GetItemAsync(source, ct).ConfigureAwait(false);
            var destinationDirectory = await GetItemAsync(targetDirectory, ct).ConfigureAwait(false);
            bool sourceValid = sourceItem != null && !sourceItem.IsDirectory && sourceItem.Size == expectedSize &&
                await VerifyFileIntegrityAsync(source, expectedSha256, expectedSize, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!sourceValid)
                throw new InvalidDataException("源备份对象校验失败");
            if (destinationDirectory == null || !destinationDirectory.IsDirectory || await GetItemAsync(target, ct).ConfigureAwait(false) != null)
                throw new IOException("候选目标目录不存在或目标文件已存在");

            string work = $"{candidate}/work-{Guid.NewGuid():N}";
            if (await TryCreateDirectoryExclusiveAsync(work, ct).ConfigureAwait(false) != ExclusiveDirectoryResult.Created)
                throw new IOException("候选复制工作目录冲突");
            string sourceName = source[(source.LastIndexOf('/') + 1)..];
            string copied = $"{work}/{sourceName}";
            long copyTimeoutSeconds = Math.Clamp(120L + expectedSize / (4L * 1024 * 1024), 120L, 24L * 60 * 60);
            string json = await _apiClient.SendSignedTaskRpcAsync("file.cp", new Dictionary<string, object?> {
                ["files"] = new[] { "/" + CombineWithPersonalRoot(source) },
                ["pathTo"] = "/" + CombineWithPersonalRoot(work), ["overwrite"] = "skip",
                ["details"] = new { name = sourceName, count = 1 }
            }, TimeSpan.FromSeconds(copyTimeoutSeconds), ct).ConfigureAwait(false);
            using (var response = JsonDocument.Parse(json))
                if (!response.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String || result.GetString() != "succ" ||
                    (response.RootElement.TryGetProperty("errno", out var error) && error.ValueKind == JsonValueKind.Number && error.TryGetInt32(out int code) && code != 0))
                    throw new IOException("飞牛候选复制未确认成功，保留工作目录供检查");
            bool copyValid = await VerifyFileIntegrityAsync(copied, expectedSha256, expectedSize, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!copyValid)
                throw new InvalidDataException("飞牛候选复制内容校验失败");

            if (!string.Equals(sourceName, desiredName, StringComparison.Ordinal))
            {
                string renamed = $"{work}/{desiredName}";
                string renameJson = await _apiClient.SendSignedRpcAsync("file.rename", new Dictionary<string, object?> {
                    ["path"] = "/" + CombineWithPersonalRoot(copied), ["newName"] = desiredName
                }, ct).ConfigureAwait(false);
                using var response = JsonDocument.Parse(renameJson);
                bool responseSucceeded = response.RootElement.TryGetProperty("result", out var result) &&
                    result.ValueKind == JsonValueKind.String && result.GetString() == "succ";
                var oldItem = await GetItemAsync(copied, ct).ConfigureAwait(false);
                bool renamedValid = await VerifyFileIntegrityAsync(renamed, expectedSha256, expectedSize, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!responseSucceeded || oldItem != null || !renamedValid)
                    throw new IOException("候选文件改名结果不确定，保留工作目录供检查");
                copied = renamed;
            }
            if (await MoveFileVerifiedAsync(copied, targetDirectory, expectedSha256, expectedSize, ct).ConfigureAwait(false) != VerifiedMoveOutcome.Moved)
                throw new IOException("候选文件移动未确认完成，保留工作目录供检查");
        }

        /// <summary>
        /// Experimental: copy an existing file on the NAS into a newly created object directory.
        /// The source and the copy are independently hashed. Nothing is deleted or overwritten.
        /// </summary>
        public async Task<string?> CopyIntoNewObjectAsync(string sourcePath, string backupRoot, string expectedSha256,
            long expectedSize, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (!PathHelper.IsSafeRelativePath(sourcePath) || !PathHelper.IsSafeRelativePath(backupRoot))
                throw new ArgumentException("复制源和备份目录必须是安全的相对路径");
            if (expectedSize < 0 || expectedSha256 == null || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
                throw new ArgumentException("复制前必须提供有效的文件大小和 SHA-256");
            string source = NormalizeRelativePath(sourcePath);
            string root = NormalizeRelativePath(backupRoot);
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(root) ||
                source.Equals(root, StringComparison.OrdinalIgnoreCase) || source.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("复制源不能位于目标备份内部");

            async Task<bool> MatchesAsync(string path)
            {
                var item = await GetItemAsync(path, ct).ConfigureAwait(false);
                if (item == null || item.IsDirectory || item.Size != expectedSize) return false;
                using var stream = await OpenReadAsync(path, ct).ConfigureAwait(false);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
                return string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase);
            }

            // A mismatch is safe to fall back to a local upload only before any copy is requested.
            if (!await MatchesAsync(source).ConfigureAwait(false)) return null;
            var rootItem = await GetItemAsync(root, ct).ConfigureAwait(false);
            if (rootItem == null || !rootItem.IsDirectory) throw new DirectoryNotFoundException("备份目标目录不存在");
            string objectParent = $"{root}/.fnsync/objects";
            foreach (string directory in new[] { $"{root}/.fnsync", objectParent })
            {
                var prepared = await CreateDirectoryAsync(directory, ct).ConfigureAwait(false);
                if (!prepared.Success) throw new IOException($"无法准备内容对象目录: {prepared.ErrorMessage}");
            }
            string name = source[(source.LastIndexOf('/') + 1)..];
            string objectRelativeDir = $".fnsync/objects/obj-{Guid.NewGuid():N}";
            string relative = $"{objectRelativeDir}/{name}";
            string objectDir = $"{root}/{objectRelativeDir}";
            if (await TryCreateDirectoryExclusiveAsync(objectDir, ct).ConfigureAwait(false) != ExclusiveDirectoryResult.Created)
                throw new IOException("内容对象目录冲突，拒绝复用");
            string copied = $"{objectDir}/{name}";
            // Server-side copies can legitimately take much longer than small RPCs.
            // Timeout still means the outcome is unknown; the caller must not retry this object.
            long copyTimeoutSeconds = Math.Clamp(120L + expectedSize / (4L * 1024 * 1024), 120L, 24L * 60 * 60);
            string json = await _apiClient.SendSignedTaskRpcAsync("file.cp", new Dictionary<string, object?> {
                ["files"] = new[] { "/" + CombineWithPersonalRoot(source) },
                ["pathTo"] = "/" + CombineWithPersonalRoot(objectDir),
                ["overwrite"] = "skip", ["details"] = new { name, count = 1 }
            }, TimeSpan.FromSeconds(copyTimeoutSeconds), ct).ConfigureAwait(false);
            using (var response = JsonDocument.Parse(json))
                if (!response.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.String || result.GetString() != "succ" ||
                    (response.RootElement.TryGetProperty("errno", out var error) && error.ValueKind == JsonValueKind.Number &&
                        error.TryGetInt32(out int errorCode) && errorCode != 0))
                    throw new IOException("飞牛复制未确认成功，保留对象目录供检查");
            var sourceAfter = await GetItemAsync(source, ct).ConfigureAwait(false);
            if (sourceAfter == null || sourceAfter.IsDirectory)
                throw new InvalidDataException("复制后源文件不可见，未引用该对象");
            if (!await MatchesAsync(copied).ConfigureAwait(false))
                throw new InvalidDataException("复制对象最终校验失败，未引用该对象");
            return relative;
        }

        public Task<StorageOperationResult> DeleteAsync(string relativePath, bool moveToTrash = true, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains("..") || relativePath.Contains(":"))
            {
                return Task.FromResult(StorageOperationResult.Fail("安全保护激活：严禁删除根目录或非法越界相对路径！", 403, relativePath));
            }

            if (!_apiClient.IsAuthenticated)
            {
                return Task.FromResult(StorageOperationResult.Fail("飞牛个人空间尚未认证，无法执行操作。"));
            }

            // A directory name is not proof of ownership or consent. Until a scoped,
            // explicitly approved deletion workflow exists, all personal-space deletes fail closed.
            return Task.FromResult(StorageOperationResult.Fail("安全保护拦截：飞牛个人空间删除功能尚未启用。"));
        }

        /// <summary>
        /// 根路径只从登录会话的权威 UID 推导。UID 未知时拒绝操作，绝不猜测默认账号目录。
        /// </summary>
        private string ResolveRootFromAuthenticatedUid()
        {
            if (!_apiClient.CurrentUid.HasValue)
                throw new InvalidOperationException("无法确认当前登录账号的个人空间归属 (UID 未知)，已阻止访问以避免误写入其他账号目录。");
            return $"vol{_apiClient.CurrentVolumeId ?? 1}/{_apiClient.CurrentUid.Value}";
        }

        internal string NormalizeRelativePath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return "";
            }

            string clean = relativePath.Trim().Replace('\\', '/').Trim('/');
            string root = _personalRootPath ?? ResolveRootFromAuthenticatedUid();

            // 支持兼容「我的文件」或「我的文件/...」的前缀
            if (clean.Equals("我的文件", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }
            if (clean.StartsWith("我的文件/", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring("我的文件/".Length).TrimStart('/');
            }

            // 支持已包含 root 前缀 (例如 vol1/1006/...) 的路径
            if (clean.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }
            if (clean.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(root.Length + 1).TrimStart('/');
            }

            return clean;
        }

        private string CombineWithPersonalRoot(string? relativePath)
        {
            string root = _personalRootPath ?? ResolveRootFromAuthenticatedUid();
            string clean = NormalizeRelativePath(relativePath);
            if (string.IsNullOrEmpty(clean))
            {
                return root;
            }

            // 严格检查目录遍历与非法字符
            string[] segments = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (var seg in segments)
            {
                if (seg == ".." || seg == "." || seg.Contains(":") || seg.Contains("\\"))
                {
                    throw new ArgumentException($"安全拦截：检测到非法路径组件 '{seg}'，严禁目录遍历或越界访问！", nameof(relativePath));
                }
            }

            string combined = $"{root}/{string.Join('/', segments)}";
            if (!combined.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("安全拦截：解析后路径越出个人空间边界！");
            }

            return combined;
        }

        private void EnsureAuthenticated()
        {
            if (!_apiClient.IsAuthenticated)
            {
                throw new InvalidOperationException("飞牛个人空间尚未认证，无法执行操作。");
            }
        }

        private static void ParseServerAddress(string server, out string host, out int port, out bool useSsl)
        {
            string s = server.Trim();
            useSsl = true; // 飞牛 5667 默认使用 SSL
            port = 5667;

            if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                useSsl = false;
                s = s[7..];
                port = 5666;
            }
            else if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                useSsl = true;
                s = s[8..];
                port = 5667;
            }

            s = s.TrimEnd('/');
            int colonIndex = s.IndexOf(':');
            if (colonIndex > 0)
            {
                host = s[..colonIndex];
                if (int.TryParse(s[(colonIndex + 1)..], out int p))
                {
                    port = p;
                }
            }
            else
            {
                host = s;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await _apiClient.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
