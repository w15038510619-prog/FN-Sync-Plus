using System;
using System.IO;
using System.Text;

namespace FNSyncPlus.Core.Utils
{
    public static class PathHelper
    {
        /// <summary>
        /// 将相对路径标准化为统一正斜杠格式 (如 "folder/subfolder/file.txt")
        /// </summary>
        public static string NormalizeRelPath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;
            string normalized = relativePath.Replace('\\', '/').Trim('/');
            while (normalized.Contains("//"))
            {
                normalized = normalized.Replace("//", "/");
            }
            return normalized;
        }

        /// <summary>
        /// 检验相对路径是否安全（防止目录遍历如 .. 或绝对路径）
        /// </summary>
        public static bool IsSafeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return false;
            string norm = NormalizeRelPath(relativePath);
            if (norm.StartsWith("/") || norm.Contains("..")) return false;
            if (norm.Contains(":") || Path.IsPathRooted(relativePath)) return false;
            return true;
        }

        /// <summary>
        /// 获取字符串的 UTF-8 字节长度
        /// </summary>
        public static int GetUtf8ByteCount(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return Encoding.UTF8.GetByteCount(s);
        }

        /// <summary>
        /// 为 Windows 路径加上 \\?\ 或 \\?\UNC\ 前缀，以突破 260 字符 MAX_PATH 限制
        /// </summary>
        public static string EnsureLongPathPrefix(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;

            path = path.TrimEnd('\\', '/');

            // 已经有长路径前缀
            if (path.StartsWith(@"\\?\")) return path;

            // UNC 路径 \\server\share\... -> \\?\UNC\server\share\...
            if (path.StartsWith(@"\\"))
            {
                return @"\\?\UNC\" + path.Substring(2);
            }

            // 本地绝对路径 C:\... -> \\?\C:\...
            if (path.Length >= 2 && path[1] == ':')
            {
                return @"\\?\" + path;
            }

            return path;
        }

        /// <summary>
        /// 移除 \\?\ 或 \\?\UNC\ 前缀，方便界面显示与用户交互
        /// </summary>
        public static string RemoveLongPathPrefix(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;

            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path.Substring(8);
            }

            if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(4);
            }

            return path;
        }

        /// <summary>
        /// 组合 Windows 本地或 UNC 路径，并使用反斜杠
        /// </summary>
        public static string CombineWindowsPath(string basePath, string relativePath)
        {
            string cleanRel = relativePath.Replace('/', '\\').TrimStart('\\');
            if (string.IsNullOrEmpty(basePath)) return cleanRel;
            string cleanBase = basePath.TrimEnd('\\', '/');
            return cleanBase + "\\" + cleanRel;
        }

        /// <summary>
        /// 检查文件名是否包含 Windows 非法字符 (用于恢复时的安全防护)
        /// </summary>
        public static bool ContainsInvalidFileNameChars(string fileName)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            return fileName.IndexOfAny(invalid) >= 0;
        }
    }
}
