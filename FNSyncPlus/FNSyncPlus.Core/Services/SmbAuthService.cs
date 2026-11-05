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
                // 去除可能携带的端口号（SMB 固定 445 / 139 端口）
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
                    lpLocalName = null, // 无需分配驱动器盘符
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
                    // 检查当前会话是否已经能够访问该共享
                    try
                    {
                        if (!string.IsNullOrEmpty(shareName))
                        {
                            string checkPath = $@"\\{serverAddress}\{shareName}";
                            if (Directory.Exists(checkPath))
                            {
                                return (true, "已复用当前 Windows 已建立的有效会话连接成功");
                            }
                        }
                    }
                    catch
                    {
                        // 无法直接访问，需要断开原有无凭据或旧凭据空闲连接并重新连接
                    }

                    // 尝试断开可能残留的 IPC$ 冲突会话重试一次
                    WNetCancelConnection2($@"\\{serverAddress}\IPC$", 0, bForce: false);
                    result = WNetAddConnection2(ref nr, password, username, 0);

                    if (result == NO_ERROR)
                    {
                        return (true, "已重置连接并验证成功");
                    }

                    if (result == ERROR_SESSION_CREDENTIAL_CONFLICT)
                    {
                        return (false, $"Windows 存在同一服务器的不同账号凭据冲突。请在控制面板-凭据管理器中移除对 {serverAddress} 的旧凭据，或重启该服务器连接后重试。");
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
                            // 过滤管理共享和隐藏共享
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
                    // 若 NetShareEnum 失败（部分权限受限环境），尝试探测常用共享名
                    string[] commonShares = { "FN_Sync_Test", "public", "homes", "data", "backup", "sk" };
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
                        catch
                        {
                            // 忽略单个探测失败
                        }
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
                    throw new DirectoryNotFoundException($"指定的 NAS 目录不存在: {uncPath}");
                }

                var di = new DirectoryInfo(longPath);
                foreach (var sub in di.EnumerateDirectories())
                {
                    // 忽略隐藏系统目录如 .recycle 等
                    if (sub.Name.StartsWith("$") || sub.Name.Equals(".recycle", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string subUnc = PathHelper.RemoveLongPathPrefix(sub.FullName);
                    string relSub = "";
                    if (subUnc.StartsWith(baseShareUnc, StringComparison.OrdinalIgnoreCase))
                    {
                        relSub = subUnc.Substring(baseShareUnc.Length).TrimStart('\\');
                    }

                    result.Add(new NasFolderItem
                    {
                        Name = sub.Name,
                        FullUncPath = subUnc,
                        RelativeSubPath = relSub,
                        LastModified = sub.LastWriteTime
                    });
                }

                return result;
            });
        }

        /// <summary>
        /// 在 NAS 上创建新子文件夹
        /// </summary>
        public async Task<NasFolderItem> CreateSubFolderAsync(string parentUncPath, string newFolderName, string baseShareUnc)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(newFolderName))
                {
                    throw new ArgumentException("文件夹名称不能为空", nameof(newFolderName));
                }

                string newUnc = Path.Combine(parentUncPath, newFolderName.Trim());
                string longPath = PathHelper.EnsureLongPathPrefix(newUnc);

                if (Directory.Exists(longPath))
                {
                    throw new IOException($"文件夹已存在: {newFolderName}");
                }

                var di = Directory.CreateDirectory(longPath);
                string fullUnc = PathHelper.RemoveLongPathPrefix(di.FullName);
                string relSub = fullUnc.StartsWith(baseShareUnc, StringComparison.OrdinalIgnoreCase)
                    ? fullUnc.Substring(baseShareUnc.Length).TrimStart('\\')
                    : newFolderName;

                return new NasFolderItem
                {
                    Name = di.Name,
                    FullUncPath = fullUnc,
                    RelativeSubPath = relSub,
                    LastModified = di.LastWriteTime
                };
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
            catch
            {
                // 保存失败不破坏主运行
            }
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
            catch
            {
                // 解密失败可能由于环境变化，返回 null
            }
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
