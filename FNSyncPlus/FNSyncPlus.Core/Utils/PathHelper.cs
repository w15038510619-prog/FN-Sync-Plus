using System;
using System.IO;
using System.Linq;
using System.Text;

namespace FNSyncPlus.Core.Utils
{
    public static class PathHelper
    {
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        private static readonly string[] ReservedDeviceNames = new[]
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

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
        /// 检验相对路径是否安全（防止目录遍历如 .. 或绝对路径、盘符冒号）
        /// </summary>
        public static bool IsSafeRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return false;
            string norm = NormalizeRelPath(relativePath);
            if (string.IsNullOrWhiteSpace(norm)) return false;

            // 禁止以 / 开头、禁止包含 ..
            if (norm.StartsWith("/") || norm.Contains("..")) return false;

            // 分割各段进行严格检查
            string[] segments = norm.Split('/');
            foreach (var segment in segments)
            {
                if (string.IsNullOrWhiteSpace(segment)) return false;
                if (segment == "." || segment == "..") return false;
                if (segment.Contains(':')) return false;
            }

            // 禁止带 Windows 盘符根或根路径
            if (relativePath.Contains(':') || Path.IsPathRooted(relativePath)) return false;

            return true;
        }

        /// <summary>
        /// 校验文件夹名称是否合法（检查非法字符、UTF-8 字节限制、结尾空格/句点、Windows 保留字）
        /// </summary>
        public static bool IsValidDirectoryName(string folderName, out string errorMessage)
        {
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(folderName))
            {
                errorMessage = "文件夹名称不能为空或纯空白字符";
                return false;
            }

            if (folderName.EndsWith(".") || folderName.EndsWith(" "))
            {
                errorMessage = "文件夹名称结尾不能包含空格或句点 (.)";
                return false;
            }

            folderName = folderName.Trim();

            if (folderName == "." || folderName == "..")
            {
                errorMessage = "文件夹名称不能为 '.' 或 '..'";
                return false;
            }

            if (folderName.IndexOfAny(InvalidFileNameChars) >= 0)
            {
                errorMessage = "文件夹名称包含非法字符，不能包含以下字符: \\ / : * ? \" < > |";
                return false;
            }

            int byteCount = Encoding.UTF8.GetByteCount(folderName);
            if (byteCount > 255)
            {
                errorMessage = $"文件夹名称 UTF-8 字节数达到 {byteCount} 字节，超过了 Linux / NAS 单个目录名 255 字节的上限";
                return false;
            }

            string upper = folderName.ToUpperInvariant();
            if (ReservedDeviceNames.Contains(upper))
            {
                errorMessage = $"'{folderName}' 是 Windows 系统保留设备名，不能用作文件夹名称";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 判断给定路径是否为 Windows UNC 网络路径 (如 \\server\share)
        /// </summary>
        public static bool IsUncPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string trimmed = path.Trim();
            return trimmed.StartsWith(@"\\") || trimmed.StartsWith("//");
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

            // Extended Windows paths do not normalize forward slashes. Keep drive roots intact.
            path = path.Replace('/', '\\');
            bool driveRoot = path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2..].All(c => c == '\\');
            bool extendedDriveRoot = path.Length >= 7 && path.StartsWith(@"\\?\", StringComparison.Ordinal) &&
                char.IsLetter(path[4]) && path[5] == ':' && path[6..].All(c => c == '\\');
            if (driveRoot) path = path[..3];
            else if (extendedDriveRoot) path = path[..7];
            else if (path != @"\") path = path.TrimEnd('\\');

            // 已经有长路径前缀
            if (path.StartsWith(@"\\?\")) return path;

            // UNC 路径 \\server\share\... -> \\?\UNC\server\share\...
            if (path.StartsWith(@"\\"))
            {
                return @"\\?\UNC\" + path.Substring(2);
            }

            // 本地绝对路径 C:\... -> \\?\C:\...
            if (path.Length >= 3 && path[1] == ':' && path[2] == '\\')
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
        /// 解析 UNC 路径，提取服务器名称、共享名称、共享根 UNC 与是否仅为服务器根路径
        /// 例如: "\\192.168.8.120\share\sub" -> (ServerName: "192.168.8.120", ShareName: "share", BaseShareUnc: "\\192.168.8.120\share", IsServerRoot: false)
        ///       "\\192.168.8.120" -> (ServerName: "192.168.8.120", ShareName: "", BaseShareUnc: "\\192.168.8.120", IsServerRoot: true)
        /// </summary>
        public static (string ServerName, string ShareName, string BaseShareUnc, bool IsServerRoot) ParseUncShare(string uncPath)
        {
            if (string.IsNullOrWhiteSpace(uncPath)) return (string.Empty, string.Empty, string.Empty, true);
            string clean = RemoveLongPathPrefix(uncPath).Trim().Replace('/', '\\').Trim('\\');
            var parts = clean.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return (string.Empty, string.Empty, string.Empty, true);
            if (parts.Length == 1) return (parts[0], string.Empty, $@"\\{parts[0]}", true);
            return (parts[0], parts[1], $@"\\{parts[0]}\{parts[1]}", false);
        }

        /// <summary>
        /// 获取 UNC 路径的父级路径。
        /// 如 \\192.168.8.120\share\sub -> \\192.168.8.120\share
        /// 如 \\192.168.8.120\share -> \\192.168.8.120
        /// 如 \\192.168.8.120 -> \\192.168.8.120
        /// </summary>
        public static string GetParentUncPath(string uncPath)
        {
            if (string.IsNullOrWhiteSpace(uncPath)) return string.Empty;
            string clean = RemoveLongPathPrefix(uncPath).Trim().Replace('/', '\\').TrimEnd('\\');
            var parts = clean.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1)
            {
                return parts.Length == 1 ? $@"\\{parts[0]}" : string.Empty;
            }

            var parentParts = parts.Take(parts.Length - 1);
            return $@"\\{string.Join('\\', parentParts)}";
        }

        /// <summary>
        /// 安全规范化本地文件夹路径，支持 Windows 长路径、Unicode 字符与异常兜底，严防崩溃
        /// </summary>
        public static bool TryNormalizeFullPath(string rawPath, out string normalizedPath, out string? errorMessage)
        {
            normalizedPath = string.Empty;
            errorMessage = null;

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                errorMessage = "文件夹路径不能为空";
                return false;
            }

            try
            {
                string clean = RemoveLongPathPrefix(rawPath).Trim();
                clean = clean.Replace('/', '\\').TrimEnd('\\');

                // 检查是否包含非法字符
                if (clean.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                {
                    errorMessage = "路径包含系统非法字符";
                    return false;
                }

                // 尝试获取全路径
                string full = Path.GetFullPath(clean).TrimEnd('\\');
                normalizedPath = full;
                return true;
            }
            catch (PathTooLongException)
            {
                try
                {
                    string prefixed = EnsureLongPathPrefix(rawPath).TrimEnd('\\');
                    normalizedPath = prefixed;
                    return true;
                }
                catch (Exception ex)
                {
                    errorMessage = $"Windows 超长路径解析异常: {ex.Message}";
                    return false;
                }
            }
            catch (Exception ex)
            {
                errorMessage = $"无效路径格式: {ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// 标准化本地路径用于唯一性对比 (不抛异常)
        /// </summary>
        public static string NormalizeLocalPath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;
            if (TryNormalizeFullPath(rawPath, out string norm, out _))
            {
                return norm;
            }
            return RemoveLongPathPrefix(rawPath).Trim().Replace('/', '\\').TrimEnd('\\');
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
            return fileName.IndexOfAny(InvalidFileNameChars) >= 0;
        }

        /// <summary>
        /// 规范化路径用于对比判定（解析长路径前缀、统一大小写、统一尾部分隔符）
        /// </summary>
        public static string GetCanonicalPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            string clean = RemoveLongPathPrefix(path).Trim();
            clean = clean.Replace('/', '\\').TrimEnd('\\');

            try
            {
                // 如果是本地路径，尝试获取完整绝对路径以解析相对符号如 . 或 ..
                if (clean.Length >= 2 && clean[1] == ':')
                {
                    clean = Path.GetFullPath(clean).TrimEnd('\\');
                }
            }
            catch
            {
                // 忽略解析异常
            }

            return clean;
        }

        /// <summary>
        /// 检查源路径和目标路径是否相同、或者互相嵌套包含，防止死循环无限备份与灾难性数据污染
        /// </summary>
        public static bool ArePathsSameOrNested(string pathA, string pathB, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
            {
                return false;
            }

            string canonA = GetCanonicalPath(pathA);
            string canonB = GetCanonicalPath(pathB);

            if (string.Equals(canonA, canonB, StringComparison.OrdinalIgnoreCase))
            {
                reason = "源文件夹与目标文件夹路径相同，无法执行同步";
                return true;
            }

            string prefixA = canonA + "\\";
            string prefixB = canonB + "\\";

            if (canonB.StartsWith(prefixA, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"目标备份目录位于源文件夹内部（嵌套包含），会导致递归循环扫描自身备份产生严重数据污染！\n源: {canonA}\n目标: {canonB}";
                return true;
            }

            if (canonA.StartsWith(prefixB, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"源文件夹位于目标备份目录内部，会导致同步死循环！\n源: {canonA}\n目标: {canonB}";
                return true;
            }

            return false;
        }
    }
}
