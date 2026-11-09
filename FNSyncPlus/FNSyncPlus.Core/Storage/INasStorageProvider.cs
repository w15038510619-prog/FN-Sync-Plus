using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FNSyncPlus.Core.Storage
{
    /// <summary>
    /// 存储提供程序类型标识
    /// </summary>
    public enum StorageProviderType
    {
        /// <summary>飞牛 fnOS 账号原生个人空间 (「我的文件」)</summary>
        FnOsPersonal,
        /// <summary>Windows SMB / CIFS 网络共享存储</summary>
        SmbShare,
        /// <summary>本地文件系统 / 单元测试沙盒模拟存储</summary>
        LocalFileSystem
    }

    public enum ExclusiveDirectoryResult { Created, AlreadyExists }

    /// <summary>
    /// Experimental capability. A provider must distinguish creating a new directory from an existing one.
    /// Callers must still evaluate the server's concurrency and durability guarantees.
    /// </summary>
    public interface IExclusiveDirectoryProvider
    {
        Task<ExclusiveDirectoryResult> TryCreateDirectoryExclusiveAsync(string relativePath, CancellationToken ct = default);
    }

    public enum VerifiedMoveOutcome { Moved, DestinationExists, NotMoved }

    /// <summary>Experimental only. A timeout after a move request leaves the remote outcome uncertain.</summary>
    public interface IExperimentalFileMoveProvider
    {
        Task<VerifiedMoveOutcome> MoveFileVerifiedAsync(string sourcePath, string destinationDirectory,
            string expectedSha256, long expectedSize, CancellationToken ct = default);
    }

    public sealed class RemoteMoveUncertainException : IOException
    {
        public RemoteMoveUncertainException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>
    /// NAS 存储项目元数据模型
    /// </summary>
    public class NasStorageItem
    {
        public string Name { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public long Size { get; set; }
        public DateTime LastModifiedUtc { get; set; }
        public string? VolumeId { get; set; }
        public int? OwnerUid { get; set; }
    }

    /// <summary>
    /// NAS 认证结果模型
    /// </summary>
    public class StorageAuthResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public bool TwoFactorRequired { get; set; }
        public string? TwoFactorAccessToken { get; set; }
        public string? AuthenticatedUser { get; set; }
        public int? UserUid { get; set; }
        public string? PersonalRootPath { get; set; }
    }

    /// <summary>
    /// 存储操作执行结果模型
    /// </summary>
    public class StorageOperationResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public int? ErrorCode { get; set; }
        public string? TargetPath { get; set; }

        public static StorageOperationResult Ok(string? path = null) => new() { Success = true, TargetPath = path };
        public static StorageOperationResult Fail(string error, int? code = null, string? path = null) =>
            new() { Success = false, ErrorMessage = error, ErrorCode = code, TargetPath = path };
    }

    /// <summary>
    /// NAS 统一存储抽象接口
    /// 解耦底层存储协议（fnOS 原生个人空间 vs SMB 共享）与上层增量同步、长文件名折叠、清单管理与异机恢复引擎
    /// </summary>
    public interface INasStorageProvider : IAsyncDisposable
    {
        /// <summary>存储提供程序类型</summary>
        StorageProviderType ProviderType { get; }

        /// <summary>当前是否已连接并成功鉴权</summary>
        bool IsAuthenticated { get; }

        /// <summary>当前认证上下文中的个人根路径描述（如 vol1/1006 或 \\server\share）</summary>
        string RootDisplayPath { get; }

        /// <summary>
        /// 连接并完成鉴权（支持双重验证）
        /// </summary>
        Task<StorageAuthResult> ConnectAndAuthenticateAsync(
            string server, 
            string username, 
            string password, 
            string? twoFactorCode = null, 
            CancellationToken ct = default);

        /// <summary>
        /// 列出指定相对路径下的文件和子目录项（当 relativePath 为空或 null 时，默认定位至根目录「我的文件」）
        /// </summary>
        Task<IReadOnlyList<NasStorageItem>> ListEntriesAsync(string? relativePath = null, CancellationToken ct = default);

        /// <summary>
        /// 创建子目录（支持多层级自动建立与幂等性保障）
        /// </summary>
        Task<StorageOperationResult> CreateDirectoryAsync(string relativePath, CancellationToken ct = default);

        /// <summary>
        /// 检查远端文件或目录是否存在
        /// </summary>
        Task<bool> ExistsAsync(string relativePath, CancellationToken ct = default);

        /// <summary>
        /// 获取指定远端路径的元数据项（如不存在返回 null）
        /// </summary>
        Task<NasStorageItem?> GetItemAsync(string relativePath, CancellationToken ct = default);

        /// <summary>
        /// 流式上传文件（支持修改时间保留与进度回调）。overwrite=false 不保证跨客户端原子独占，不能用作锁。
        /// </summary>
        Task<StorageOperationResult> UploadFileAsync(
            string relativePath,
            Stream sourceStream,
            long fileSize,
            DateTime lastWriteTimeUtc,
            bool overwrite = true,
            IProgress<long>? progress = null,
            CancellationToken ct = default);

        /// <summary>
        /// 流式读取/下载远端文件（供异机恢复与强比对校验使用）
        /// </summary>
        Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default);

        /// <summary>
        /// 校验远端文件是否存在且 SHA-256/大小与期望值严格一致
        /// </summary>
        Task<bool> VerifyFileIntegrityAsync(
            string relativePath, 
            string expectedSha256, 
            long expectedSize, 
            CancellationToken ct = default);

        /// <summary>
        /// 安全删除指定目录下的文件或移入回收站（严格限制在任务子目录下，禁止删除根或外部文件）
        /// </summary>
        Task<StorageOperationResult> DeleteAsync(string relativePath, bool moveToTrash = true, CancellationToken ct = default);
    }
}
