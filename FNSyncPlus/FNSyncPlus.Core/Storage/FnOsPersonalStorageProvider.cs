using System;
using System.Collections.Generic;
using System.IO;
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
                        return new StorageAuthResult
                        {
                            Success = false,
                            TwoFactorRequired = true,
                            TwoFactorAccessToken = loginResp.AccessToken,
                            ErrorMessage = "账号已启用两步验证 (2FA)，请输入验证码。"
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

            string fullPath = CombineWithPersonalRoot(relativePath);
            var resp = await _apiClient.CreateDirectoryAsync(fullPath, ct).ConfigureAwait(false);

            if (resp.Result == "succ" || resp.ErrNo == 17) // 17 = EEXIST (已存在视为幂等成功)
            {
                return StorageOperationResult.Ok(fullPath);
            }

            return StorageOperationResult.Fail(resp.ErrMsg ?? "创建目录失败", resp.ErrNo, fullPath);
        }

        public async Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(relativePath)) return true;

            string normalized = relativePath.Trim('/', '\\').Replace('\\', '/');
            string? parentDir = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            string targetName = Path.GetFileName(normalized);

            if (string.IsNullOrEmpty(targetName)) return true;

            try
            {
                var entries = await ListEntriesAsync(parentDir, ct).ConfigureAwait(false);
                foreach (var item in entries)
                {
                    if (string.Equals(item.Name, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch
            {
                return false;
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
                bool success = await _apiClient.UploadStreamAsync(
                    fullPath,
                    sourceStream,
                    lastWriteTimeUtc,
                    overwrite,
                    progress,
                    ct).ConfigureAwait(false);

                if (success)
                {
                    return StorageOperationResult.Ok(fullPath);
                }

                return StorageOperationResult.Fail("上传未返回成功状态码", path: fullPath);
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
                if (stream.Length != expectedSize)
                {
                    return false;
                }

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

        public async Task<StorageOperationResult> DeleteAsync(string relativePath, bool moveToTrash = true, CancellationToken ct = default)
        {
            EnsureAuthenticated();

            // 严格安全保护：严禁删除根目录或非法的上级跳跃
            string clean = relativePath.Trim('/', '\\').Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(clean) || clean == "." || clean == ".." || clean.Contains("../") || clean.Contains("/.."))
            {
                throw new InvalidOperationException("安全保护激活：严禁删除个人空间根目录或越界访问！");
            }

            string fullPath = CombineWithPersonalRoot(relativePath);
            var resp = await _apiClient.DeleteFileOrDirAsync(fullPath, moveToTrash, ct).ConfigureAwait(false);

            if (resp.Result == "succ")
            {
                return StorageOperationResult.Ok(fullPath);
            }

            return StorageOperationResult.Fail(resp.ErrMsg ?? "删除失败", resp.ErrNo, fullPath);
        }

        private string CombineWithPersonalRoot(string relativePath)
        {
            string root = _personalRootPath ?? $"vol1/{_apiClient.CurrentUid ?? 1000}";
            string cleanRel = relativePath.TrimStart('/', '\\').Replace('\\', '/');
            if (string.IsNullOrEmpty(cleanRel))
            {
                return root;
            }
            return $"{root}/{cleanRel}";
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
