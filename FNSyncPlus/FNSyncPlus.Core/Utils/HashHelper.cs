using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FNSyncPlus.Core.Utils
{
    public static class HashHelper
    {
        /// <summary>
        /// 计算字符串的 SHA-256 并返回十六进制小写字符串
        /// </summary>
        public static string ComputeSha256String(string input)
        {
            if (input == null) return string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(input);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string ComputeStringSha256(string input) => ComputeSha256String(input);

        /// <summary>
        /// 计算字节数组的 SHA-256 并返回十六进制小写字符串
        /// </summary>
        public static string ComputeSha256(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return string.Empty;
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string ComputeBytesSha256(byte[] bytes) => ComputeSha256(bytes);

        /// <summary>
        /// 计算文件的完整 SHA-256 哈希值
        /// </summary>
        public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
        {
            string longPath = PathHelper.EnsureLongPathPrefix(filePath);
            using var sha = SHA256.Create();
            // 以只读、共享读的方式打开文件，防止锁冲突
            using var stream = new FileStream(longPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
            byte[] buffer = new byte[64 * 1024];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha.TransformBlock(buffer, 0, bytesRead, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }

        /// <summary>
        /// 同步计算文件的 SHA-256 哈希值
        /// </summary>
        public static string ComputeFileSha256(string filePath)
        {
            string longPath = PathHelper.EnsureLongPathPrefix(filePath);
            using var sha = SHA256.Create();
            using var stream = new FileStream(longPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
            byte[] hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
