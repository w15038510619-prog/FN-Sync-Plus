using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Storage.FnOs;

namespace FNSyncPlus.Core.Storage
{
    /// <summary>
    /// 飞牛 fnOS 账号原生个人空间存储提供程序 (「我的文件」)
    /// 严格遵循官方 API 权限模型，直接对接个人空间，杜绝团队共享混用与伪造用户名路径
    /// </summary>
    public class FnOsPersonalStorageProvider : INasStorageProvider
    {
        private readonly FnOsApiClient _apiClient;
        private string? _personalRootPath; // 例如 vol1/1006
        private string? _serverHost;
        private int _serverPort;
        private bool _disposed;

        public StorageProviderType ProviderType => StorageProviderType.FnOsPersonal;
        public bool IsAuthenticated => _apiClient.IsAuthenticated;
        public string RootDisplayPath => string.IsNullOrEmpty(_personalRootPath)
            ? "飞牛官方个人空间 [我的文件]"
            : $"飞牛官方个人空间 [我的文件] ({_personalRootPath})";

        public int? CurrentUid => _apiClient.CurrentUid;
        public int? CurrentVolumeId => _apiClient.CurrentVolumeId;

        public FnOsPersonalStorageProvider(string? pinnedCertSha256 = null)
        {
            _apiClient = new FnOsApiClient(pinnedCertSha256);
        }

        public FnOsPersonalStorageProvider(FnOsApiClient apiClient)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            if (_apiClient.IsAuthenticated && _apiClient.CurrentUid.HasValue)
            {
                _personalRootPath = $"vol{_apiClient.CurrentVolumeId ?? 1}/{_apiClient.CurrentUid.Value}";
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

        public async Task<StorageOperationResult> CreateDirectoryAsync(string relativePath, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(relativePath))
                return StorageOperationResult.Fail("目录路径不能为空");

            // 严格安全边界校验：任何包含 .. 或非法组件的越界遍历直接拦截并抛出 ArgumentException
            string validatedFullPath = CombineWithPersonalRoot(relativePath);

            string clean = relativePath.Trim().Replace('\\', '/').Trim('/');
            if (clean.StartsWith("我的文件/", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring("我的文件/".Length).TrimStart('/');
            }
            else if (clean.Equals("我的文件", StringComparison.OrdinalIgnoreCase))
            {
                clean = "";
            }

            if (string.IsNullOrEmpty(clean))
            {
                return StorageOperationResult.Ok(CombineWithPersonalRoot(""));
            }

            // 1. 先快速检查目标是否已经存在
            var existingItem = await GetItemAsync(clean, ct).ConfigureAwait(false);
            if (existingItem != null)
            {
                if (existingItem.IsDirectory)
                {
                    // 目录已存在，100% 幂等复用成功，绝不报错
                    return StorageOperationResult.Ok(validatedFullPath);
                }
                else
                {
                    // 冲突：已存在同名常规文件
                    return StorageOperationResult.Fail($"路径冲突：目标 '{clean}' 已存在同名文件，无法创建为目录。", 17, validatedFullPath);
                }
            }

            // 2. 逐级创建缺失目录 (mkdir -p 递归语义)
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

                if (resp.Result == "succ" || resp.ErrNo == 17)
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

            string clean = relativePath.Trim().Replace('\\', '/').Trim('/');
            if (clean.StartsWith("我的文件/", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring("我的文件/".Length).TrimStart('/');
            }
            else if (clean.Equals("我的文件", StringComparison.OrdinalIgnoreCase))
            {
                clean = "";
            }

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
            catch
            {
                return null;
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
                // 1. 真正的分块流式上传并校验业务响应
                bool success = await _apiClient.UploadStreamAsync(
                    fullPath,
                    sourceStream,
                    fileSize,
                    lastWriteTimeUtc,
                    overwrite,
                    progress,
                    ct).ConfigureAwait(false);

                if (!success)
                {
                    return StorageOperationResult.Fail("上传接口返回失败或业务校验未通过", path: fullPath);
                }

                // 2. 上传完成后的物理存在与文件大小强校验，杜绝虚假成功
                string parentRel = Path.GetDirectoryName(relativePath.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
                string fileName = Path.GetFileName(relativePath.Replace('\\', '/'));

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

            // 严格安全保护：正式个人空间暂时禁止物理删除，仅允许在用户明确授权的测试探测目录中清理
            string clean = relativePath.Trim('/', '\\').Replace('\\', '/');
            bool isAuthorizedTestFolder = clean.StartsWith("_FNSYNC_SAFETY_PROBE_", StringComparison.OrdinalIgnoreCase) ||
                                         clean.StartsWith("FN_SYNC_POC_", StringComparison.OrdinalIgnoreCase) ||
                                         clean.StartsWith("FN_TEST_", StringComparison.OrdinalIgnoreCase);

            if (!isAuthorizedTestFolder)
            {
                return Task.FromResult(StorageOperationResult.Fail("安全保护拦截：正式个人空间暂禁止删除操作，确保用户数据绝对安全。"));
            }

            string fullPath = CombineWithPersonalRoot(relativePath);
            return Task.Run(async () =>
            {
                var resp = await _apiClient.DeleteFileOrDirAsync(fullPath, moveToTrash, ct).ConfigureAwait(false);
                if (resp.Result == "succ")
                {
                    return StorageOperationResult.Ok(fullPath);
                }
                return StorageOperationResult.Fail(resp.ErrMsg ?? "删除失败", resp.ErrNo, fullPath);
            });
        }

        private string CombineWithPersonalRoot(string? relativePath)
        {
            string root = _personalRootPath ?? $"vol1/{_apiClient.CurrentUid ?? 1000}";
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return root;
            }

            string clean = relativePath.Trim().Replace('\\', '/');
            if (clean.StartsWith("/"))
            {
                clean = clean.TrimStart('/');
            }

            // 支持兼容「我的文件」或「我的文件/...」的前缀
            if (clean.Equals("我的文件", StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
            if (clean.StartsWith("我的文件/", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring("我的文件/".Length).TrimStart('/');
                if (string.IsNullOrEmpty(clean)) return root;
            }

            // 支持已包含 root 前缀 (例如 vol1/1006/...) 的路径
            if (clean.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
            if (clean.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean.Substring(root.Length + 1).TrimStart('/');
                if (string.IsNullOrEmpty(clean)) return root;
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
