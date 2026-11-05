using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services
{
    public class SmbShareItem
    {
        public string Name { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
        public string UncPath { get; set; } = string.Empty;
    }

    public class NasFolderItem
    {
        public string Name { get; set; } = string.Empty;
        public string FullUncPath { get; set; } = string.Empty;
        public string RelativeSubPath { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
        public bool IsShareRoot { get; set; }
    }

    public class SmbAuthService
    {
        #region Win32 API Definitions

        private const int RESOURCETYPE_DISK = 0x00000001;
        private const int CONNECT_TEMPORARY = 0x00000004;
        private const int CONNECT_INTERACTIVE = 0x00000008;
        private const int CONNECT_PROMPT = 0x00000010;
        private const int CONNECT_UPDATE_PROFILE = 0x00000001;

        private const int NO_ERROR = 0;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_BAD_NETPATH = 53;
        private const int ERROR_BAD_NET_NAME = 67;
        private const int ERROR_INVALID_PASSWORD = 86;
        private const int ERROR_ALREADY_ASSIGNED = 85;
        private const int ERROR_SESSION_CREDENTIAL_CONFLICT = 1219;
        private const int ERROR_LOGON_FAILURE = 1326;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NETRESOURCE
        {
            public uint dwScope;
            public uint dwType;
            public uint dwDisplayType;
            public uint dwUsage;
            public string? lpLocalName;
            public string? lpRemoteName;
            public string? lpComment;
            public string? lpProvider;
        }

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetAddConnection2(
            ref NETRESOURCE lpNetResource,
            string? lpPassword,
            string? lpUserName,
            uint dwFlags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetCancelConnection2(
            string lpName,
            uint dwFlags,
            bool bForce);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHARE_INFO_1
        {
            public string shi1_netname;
            public uint shi1_type;
            public string shi1_remark;
        }

        private const uint STYPE_DISKTREE = 0;
        private const uint STYPE_SPECIAL = 0x80000000;

        [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetShareEnum(
            string? ServerName,
            int level,
            ref IntPtr bufPtr,
            int prefmaxlen,
            ref int entriesread,
            ref int totalentries,
            ref int resume_handle);

        [DllImport("Netapi32.dll")]
        private static extern int NetApiBufferFree(IntPtr Buffer);

        #endregion

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FNSyncPlus_Credentials_Entropy_2026");

        /// <summary>
        /// 登录并连接 NAS SMB 服务，建立身份验证会话
        /// </summary>
        public async Task<(bool Success, string Message)> ConnectNasAsync(string serverAddress, string username, string password, string? shareName = null)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(serverAddress))
                {
                    return (false, "NAS 地址不能为空");
                }

                serverAddress = serverAddress.Trim().Trim('\\', '/');
                if (serverAddress.Contains(':'))
                {
                    serverAddress = serverAddress.Split(':')[0];
                }

                string remoteTarget = string.IsNullOrEmpty(shareName)
                    ? $@"\\{serverAddress}\IPC$"
                    : $@"\\{serverAddress}\{shareName.Trim('\\', '/')}";

                var nr = new NETRESOURCE
                {
                    dwType = RESOURCETYPE_DISK,
                    lpLocalName = null,
                    lpRemoteName = remoteTarget,
                    lpProvider = null
                };

                // 先尝试建立连接
                int result = WNetAddConnection2(ref nr, password, username, 0);

                if (result == NO_ERROR)
                {
                    return (true, "连接成功");
                }

                // 处理已有会话凭据冲突 (Error 1219)
                if (result == ERROR_SESSION_CREDENTIAL_CONFLICT)
                {
                    // 先检查当前已有的 Windows 会话是否能够直接访问
                    try
                    {
                        if (!string.IsNullOrEmpty(shareName))
                        {
                            string checkPath = $@"\\{serverAddress}\{shareName}";
                            if (Directory.Exists(checkPath))
                            {
                                return (true, "已复用当前 Windows 会话连接成功");
                            }
                        }
                    }
                    catch
                    {
                        // 无法直接访问
                    }

                    // 仅断开临时的 IPC$ 连接尝试重试一次，绝不断开用户的其他现有盘符或正常会话
                    WNetCancelConnection2($@"\\{serverAddress}\IPC$", 0, bForce: false);
                    result = WNetAddConnection2(ref nr, password, username, 0);

                    if (result == NO_ERROR)
                    {
                        return (true, "已建立连接并验证成功");
                    }

                    if (result == ERROR_SESSION_CREDENTIAL_CONFLICT)
                    {
                        return (false, $"Windows 提示已存在与 NAS 服务器 ({serverAddress}) 的其他账号网络会话 (错误代码 1219)。为保障您其他工作连接的安全，请在 Windows 控制面板【凭据管理器】中查看并管理旧凭据，或使用一致的账号登录。");
                    }
                }

                string errorMsg = result switch
                {
                    ERROR_LOGON_FAILURE => "用户名或密码错误，请检查 NAS 账号与密码",
                    ERROR_INVALID_PASSWORD => "密码错误，请重新输入",
                    ERROR_ACCESS_DENIED => "访问被拒绝，当前账号没有访问此 NAS 的权限",
                    ERROR_BAD_NETPATH => $"无法连接到 NAS 地址: {serverAddress}，请检查 IP、主机名或局域网网络连接",
                    ERROR_BAD_NET_NAME => $"NAS 上未找到指定的共享文件夹: {shareName}",
                    _ => $"连接失败 (错误代码 0x{result:X}: {new Win32Exception(result).Message})"
                };

                return (false, errorMsg);
            });
        }

        /// <summary>
        /// 列出 NAS 上所有可访问的 SMB 共享目录（排除 IPC$ 等隐藏管理共享）
        /// </summary>
        public async Task<List<SmbShareItem>> EnumerateSharesAsync(string serverAddress)
        {
            return await Task.Run(() =>
            {
                var list = new List<SmbShareItem>();
                serverAddress = serverAddress.Trim().Trim('\\', '/');
                if (serverAddress.Contains(':')) serverAddress = serverAddress.Split(':')[0];

                IntPtr bufPtr = IntPtr.Zero;
                int entriesread = 0;
                int totalentries = 0;
                int resume_handle = 0;

                int ret = NetShareEnum(serverAddress, 1, ref bufPtr, -1, ref entriesread, ref totalentries, ref resume_handle);
                if (ret == 0 && bufPtr != IntPtr.Zero)
                {
                    try
                    {
                        int offset = Marshal.SizeOf(typeof(SHARE_INFO_1));
                        for (int i = 0; i < entriesread; i++)
                        {
                            IntPtr currentPtr = IntPtr.Add(bufPtr, i * offset);
                            var info = Marshal.PtrToStructure<SHARE_INFO_1>(currentPtr);

                            string shareName = info.shi1_netname;
                            if (string.IsNullOrEmpty(shareName)) continue;
                            if (shareName.EndsWith("$", StringComparison.OrdinalIgnoreCase)) continue;
                            if ((info.shi1_type & STYPE_SPECIAL) != 0) continue;

                            list.Add(new SmbShareItem
                            {
                                Name = shareName,
                                Remark = info.shi1_remark ?? "",
                                UncPath = $@"\\{serverAddress}\{shareName}"
                            });
                        }
                    }
                    finally
                    {
                        NetApiBufferFree(bufPtr);
                    }
                }
                else
                {
                    // 在部分限制 NetShareEnum 的环境中，探测常用共享名称
                    string[] commonShares = { "public", "data", "homes", "backup", "files" };
                    foreach (var sh in commonShares)
                    {
                        string testUnc = $@"\\{serverAddress}\{sh}";
                        try
                        {
                            if (Directory.Exists(testUnc))
                            {
                                list.Add(new SmbShareItem
                                {
                                    Name = sh,
                                    Remark = "已探测共享",
                                    UncPath = testUnc
                                });
                            }
                        }
                        catch { }
                    }
                }

                return list;
            });
        }

        /// <summary>
        /// 浏览 NAS 共享或其子目录中的文件夹列表
        /// </summary>
        public async Task<List<NasFolderItem>> ListSubFoldersAsync(string uncPath, string baseShareUnc)
        {
            return await Task.Run(() =>
            {
                var result = new List<NasFolderItem>();
                string longPath = PathHelper.EnsureLongPathPrefix(uncPath);

                if (!Directory.Exists(longPath))
                {
                    throw new DirectoryNotFoundException($"指定的 NAS 目录不存在或无访问权限: {uncPath}");
                }

                var di = new DirectoryInfo(longPath);
                foreach (var sub in di.EnumerateDirectories())
                {
                    // 忽略隐藏系统目录如 .recycle、.fnsync
                    if (sub.Name.StartsWith("$") ||
                        sub.Name.Equals(".recycle", StringComparison.OrdinalIgnoreCase) ||
                        sub.Name.Equals(FNSyncPlus.Core.Models.Manifest.MetadataDirName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string subUnc = PathHelper.RemoveLongPathPrefix(sub.FullName);
                    string relSub = "";
                    if (!string.IsNullOrEmpty(baseShareUnc) && subUnc.StartsWith(baseShareUnc, StringComparison.OrdinalIgnoreCase))
                    {
                        relSub = subUnc.Substring(baseShareUnc.Length).TrimStart('\\');
                    }

                    result.Add(new NasFolderItem
                    {
                        Name = sub.Name,
                        FullUncPath = subUnc,
                        RelativeSubPath = relSub,
                        LastModified = sub.LastWriteTime,
                        IsShareRoot = false
                    });
                }

                return result;
            });
        }

        /// <summary>
        /// 测试目标 SMB 目录是否具备写入权限
        /// </summary>
        public async Task<(bool CanWrite, string ErrorMessage)> TestDirectoryWritePermissionAsync(string uncPath)
        {
            return await Task.Run(() =>
            {
                string testFile = Path.Combine(uncPath, $".fn_perm_test_{Guid.NewGuid():N}.tmp");
                string longPath = PathHelper.EnsureLongPathPrefix(testFile);

                try
                {
                    using (var fs = new FileStream(longPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] testBytes = Encoding.UTF8.GetBytes("test");
                        fs.Write(testBytes, 0, testBytes.Length);
                    }

                    try { File.Delete(longPath); } catch { }
                    return (true, string.Empty);
                }
                catch (UnauthorizedAccessException)
                {
                    return (false, "当前登录账号在此 NAS 文件夹没有写入权限。请联系管理员或在飞牛 NAS 管理面板为该账号分配【读写】权限。");
                }
                catch (Exception ex)
                {
                    return (false, $"无法写入 NAS 文件夹: {ex.Message}");
                }
            });
        }

        /// <summary>
        /// 在 NAS 上创建新子文件夹（包含文件夹名称合法性校验和权限检查）
        /// </summary>
        public async Task<NasFolderItem> CreateSubFolderAsync(string parentUncPath, string newFolderName, string baseShareUnc)
        {
            return await Task.Run(() =>
            {
                if (!PathHelper.IsValidDirectoryName(newFolderName, out string validationError))
                {
                    throw new ArgumentException(validationError, nameof(newFolderName));
                }

                string newUnc = Path.Combine(parentUncPath, newFolderName.Trim());
                string longPath = PathHelper.EnsureLongPathPrefix(newUnc);

                if (Directory.Exists(longPath))
                {
                    throw new IOException($"NAS 上已存在同名文件夹: {newFolderName}");
                }

                try
                {
                    var di = Directory.CreateDirectory(longPath);
                    string fullUnc = PathHelper.RemoveLongPathPrefix(di.FullName);
                    string relSub = (!string.IsNullOrEmpty(baseShareUnc) && fullUnc.StartsWith(baseShareUnc, StringComparison.OrdinalIgnoreCase))
                        ? fullUnc.Substring(baseShareUnc.Length).TrimStart('\\')
                        : newFolderName;

                    return new NasFolderItem
                    {
                        Name = di.Name,
                        FullUncPath = fullUnc,
                        RelativeSubPath = relSub,
                        LastModified = di.LastWriteTime,
                        IsShareRoot = false
                    };
                }
                catch (UnauthorizedAccessException)
                {
                    throw new UnauthorizedAccessException($"创建文件夹被拒绝：当前 NAS 账号在【{parentUncPath}】缺少写入权限。请在飞牛系统管理中开放该共享的写权限。");
                }
            });
        }

        #region 凭据 DPAPI 安全保存与读取

        private static string GetCredentialStoragePath()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string folder = Path.Combine(appData, "FNSyncPlus");
            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
            }
            return Path.Combine(folder, "credentials.dat");
        }

        public static void SaveCredentials(string server, string username, string password)
        {
            try
            {
                string payload = $"{server}\n{username}\n{password}";
                byte[] raw = Encoding.UTF8.GetBytes(payload);
                byte[] encrypted = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(GetCredentialStoragePath(), encrypted);
            }
            catch { }
        }

        public static (string Server, string Username, string Password)? LoadCredentials()
        {
            try
            {
                string path = GetCredentialStoragePath();
                if (!File.Exists(path)) return null;

                byte[] encrypted = File.ReadAllBytes(path);
                byte[] raw = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                string payload = Encoding.UTF8.GetString(raw);
                string[] parts = payload.Split('\n');
                if (parts.Length >= 3)
                {
                    return (parts[0], parts[1], parts[2]);
                }
            }
            catch { }
            return null;
        }

        public static void ClearSavedCredentials()
        {
            try
            {
                string path = GetCredentialStoragePath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }

        #endregion
    }
}
