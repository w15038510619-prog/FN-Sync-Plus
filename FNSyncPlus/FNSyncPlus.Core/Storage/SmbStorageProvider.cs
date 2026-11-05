using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Storage
{
    /// <summary>
    /// Windows SMB / CIFS 网络共享存储提供程序
    /// 作为传统网络共享的兼容实现与备用方案
    /// </summary>
    public class SmbStorageProvider : INasStorageProvider
    {
        private readonly SmbAuthService _smbService;
        private string? _rootUncPath;
        private bool _isAuthenticated;

        public StorageProviderType ProviderType => StorageProviderType.SmbShare;
        public bool IsAuthenticated => _isAuthenticated;
        public string RootDisplayPath => _rootUncPath ?? "SMB 网络共享未配置";

        public SmbStorageProvider(SmbAuthService? smbService = null, string? rootUncPath = null)
        {
            _smbService = smbService ?? new SmbAuthService();
            _rootUncPath = rootUncPath;
            if (!string.IsNullOrEmpty(_rootUncPath) && Directory.Exists(_rootUncPath))
            {
                _isAuthenticated = true;
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
                throw new ArgumentException("SMB 服务器/共享路径不能为空", nameof(server));

            try
            {
                // 若传入的是主机名或 IP，规范化为基本 UNC 或由用户指定共享
                string targetUnc = server.StartsWith(@"\\") ? server : $@"\\{server}";
                string serverName = targetUnc.TrimStart('\\');
                string? share = null;
                int slashIdx = serverName.IndexOf('\\');
                if (slashIdx > 0)
                {
                    share = serverName[(slashIdx + 1)..];
                    serverName = serverName[..slashIdx];
                }

                var (success, message) = await _smbService.ConnectNasAsync(serverName, username, password, share).ConfigureAwait(false);

                if (!success)
                {
                    _isAuthenticated = false;
                    return new StorageAuthResult
                    {
                        Success = false,
                        ErrorMessage = message ?? "SMB 连接失败"
                    };
                }

                _rootUncPath = targetUnc;
                _isAuthenticated = true;

                return new StorageAuthResult
                {
                    Success = true,
                    AuthenticatedUser = username,
                    PersonalRootPath = targetUnc
                };
            }
            catch (Exception ex)
            {
                _isAuthenticated = false;
                return new StorageAuthResult
                {
                    Success = false,
                    ErrorMessage = $"SMB 认证异常: {ex.Message}"
                };
            }
        }

        public Task<IReadOnlyList<NasStorageItem>> ListEntriesAsync(string? relativePath = null, CancellationToken ct = default)
        {
            EnsureAuthenticated();

            string targetPath = CombineUnc(relativePath);
            string longPath = PathHelper.EnsureLongPathPrefix(targetPath);

            var items = new List<NasStorageItem>();

            if (!Directory.Exists(longPath))
            {
                return Task.FromResult<IReadOnlyList<NasStorageItem>>(items);
            }

            var di = new DirectoryInfo(longPath);
            foreach (var entry in di.EnumerateFileSystemInfos())
            {
                ct.ThrowIfCancellationRequested();

                bool isDir = (entry.Attributes & FileAttributes.Directory) == FileAttributes.Directory;
                long size = isDir ? 0 : (entry as FileInfo)?.Length ?? 0;

                string itemRel = string.IsNullOrWhiteSpace(relativePath)
                    ? entry.Name
                    : $"{relativePath.TrimEnd('/', '\\').Replace('\\', '/')}/{entry.Name}";

                items.Add(new NasStorageItem
                {
                    Name = entry.Name,
                    RelativePath = itemRel,
                    IsDirectory = isDir,
                    Size = size,
                    LastModifiedUtc = entry.LastWriteTimeUtc
                });
            }

            return Task.FromResult<IReadOnlyList<NasStorageItem>>(items);
        }

        public Task<StorageOperationResult> CreateDirectoryAsync(string relativePath, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(relativePath))
                return Task.FromResult(StorageOperationResult.Fail("目录路径不能为空"));

            string targetPath = CombineUnc(relativePath);
            string longPath = PathHelper.EnsureLongPathPrefix(targetPath);

            try
            {
                if (!Directory.Exists(longPath))
                {
                    Directory.CreateDirectory(longPath);
                }
                return Task.FromResult(StorageOperationResult.Ok(targetPath));
            }
            catch (Exception ex)
            {
                return Task.FromResult(StorageOperationResult.Fail($"创建 SMB 目录失败: {ex.Message}", path: targetPath));
            }
        }

        public Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(relativePath)) return Task.FromResult(true);

            string targetPath = CombineUnc(relativePath);
            string longPath = PathHelper.EnsureLongPathPrefix(targetPath);

            bool exists = File.Exists(longPath) || Directory.Exists(longPath);
            return Task.FromResult(exists);
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
                return StorageOperationResult.Fail("目标相对路径不能为空");

            string targetPath = CombineUnc(relativePath);
            string longTarget = PathHelper.EnsureLongPathPrefix(targetPath);
            string targetDir = Path.GetDirectoryName(longTarget)!;

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            string stagingPath = $"{longTarget}.staging_{Guid.NewGuid():N}.tmp";

            try
            {
                // 1. 流式写入 staging 临时文件
                using (var fs = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    var buffer = new byte[64 * 1024];
                    int bytesRead;
                    long totalCopied = 0;

                    while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, bytesRead, ct).ConfigureAwait(false);
                        totalCopied += bytesRead;
                        progress?.Report(totalCopied);
                    }
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                }

                // 2. 恢复源文件原始时间戳
                File.SetLastWriteTimeUtc(stagingPath, lastWriteTimeUtc);

                // 3. 原子性重命名 / 替换目标
                if (File.Exists(longTarget))
                {
                    if (!overwrite)
                    {
                        File.Delete(stagingPath);
                        return StorageOperationResult.Fail("目标文件已存在且未指定覆盖", path: targetPath);
                    }
                    File.Move(stagingPath, longTarget, overwrite: true);
                }
                else
                {
                    File.Move(stagingPath, longTarget);
                }

                return StorageOperationResult.Ok(targetPath);
            }
            catch (Exception ex)
            {
                try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch { }
                return StorageOperationResult.Fail($"SMB 上传失败: {ex.Message}", path: targetPath);
            }
        }

        public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("读取路径不能为空", nameof(relativePath));

            string targetPath = CombineUnc(relativePath);
            string longPath = PathHelper.EnsureLongPathPrefix(targetPath);

            if (!File.Exists(longPath))
            {
                throw new FileNotFoundException($"远端文件不存在: {targetPath}", targetPath);
            }

            Stream fs = new FileStream(longPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
            return Task.FromResult(fs);
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
                string targetPath = CombineUnc(relativePath);
                string longPath = PathHelper.EnsureLongPathPrefix(targetPath);

                var fi = new FileInfo(longPath);
                if (!fi.Exists || fi.Length != expectedSize)
                {
                    return false;
                }

                using var stream = new FileStream(longPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
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
            EnsureAuthenticated();

            // 严格安全保护：严禁删除根共享或非法的路径跳跃
            string clean = relativePath.Trim('/', '\\');
            if (string.IsNullOrWhiteSpace(clean) || clean == "." || clean == ".." || clean.Contains(".."))
            {
                throw new InvalidOperationException("安全保护激活：严禁删除 SMB 根共享或越界路径！");
            }

            string targetPath = CombineUnc(clean);
            string longPath = PathHelper.EnsureLongPathPrefix(targetPath);

            try
            {
                if (File.Exists(longPath))
                {
                    File.Delete(longPath);
                    return Task.FromResult(StorageOperationResult.Ok(targetPath));
                }
                else if (Directory.Exists(longPath))
                {
                    Directory.Delete(longPath, recursive: true);
                    return Task.FromResult(StorageOperationResult.Ok(targetPath));
                }

                return Task.FromResult(StorageOperationResult.Fail("目标不存在", path: targetPath));
            }
            catch (Exception ex)
            {
                return Task.FromResult(StorageOperationResult.Fail($"删除失败: {ex.Message}", path: targetPath));
            }
        }

        private string CombineUnc(string? relativePath)
        {
            if (string.IsNullOrEmpty(_rootUncPath))
            {
                throw new InvalidOperationException("SMB 根路径尚未配置。");
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return _rootUncPath;
            }

            string cleanRel = relativePath.TrimStart('/', '\\').Replace('/', '\\');
            return Path.Combine(_rootUncPath, cleanRel);
        }

        private void EnsureAuthenticated()
        {
            if (!_isAuthenticated && !string.IsNullOrEmpty(_rootUncPath) && Directory.Exists(_rootUncPath))
            {
                _isAuthenticated = true;
            }

            if (!_isAuthenticated)
            {
                throw new InvalidOperationException("SMB 共享尚未认证或无法访问。");
            }
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
