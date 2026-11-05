using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services
{
    public enum NasShareCategory
    {
        PersonalSpace,   // 私人空间 / 个人主目录 (我的文件) - 优先级最高
        TeamShare,       // 团队共享 / 公共文件 (团队文件)
        SystemOrOther    // 其他系统共享
    }

    public class SmbShareItem
    {
        public string Name { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
        public string UncPath { get; set; } = string.Empty;
        public NasShareCategory Category { get; set; } = NasShareCategory.TeamShare;

        public bool IsPersonal => Category == NasShareCategory.PersonalSpace;

        public string CategoryDisplayName => Category switch
        {
            NasShareCategory.PersonalSpace => "👤 私人空间 (我的文件)",
            NasShareCategory.TeamShare => "👥 团队文件 (团队共享)",
            _ => "📁 其他共享"
        };
    }

    public class NasFolderItem
    {
        public string Name { get; set; } = string.Empty;
        public string FullUncPath { get; set; } = string.Empty;
        public string RelativeSubPath { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
        public bool IsShareRoot { get; set; }
        public NasShareCategory Category { get; set; } = NasShareCategory.PersonalSpace;

        public string DisplayCategory => IsShareRoot
            ? (Category == NasShareCategory.PersonalSpace ? "👤 私人共享根" : "👥 团队共享根")
            : "📁 文件夹";
    }

    public class NasSharesResult
    {
        public List<SmbShareItem> Shares { get; set; } = new();
        public bool HasPersonalShare { get; set; }
        public string GuidanceMessage { get; set; } = string.Empty;
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
                    catch { }

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
        /// 列出 NAS 上所有可访问的 SMB 共享目录，并智能区分为【私人空间 (我的文件)】与【团队共享 (团队文件)】，优先推荐私人备份
        /// </summary>
        public async Task<NasSharesResult> EnumerateCategorizedSharesAsync(string serverAddress, string? currentUsername = null)
        {
            return await Task.Run(() =>
            {
                var result = new NasSharesResult();
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

                            var cat = CategorizeShare(shareName, currentUsername);
                            list.Add(new SmbShareItem
                            {
                                Name = shareName,
                                Remark = info.shi1_remark ?? "",
                                UncPath = $@"\\{serverAddress}\{shareName}",
                                Category = cat
                            });
                        }
                    }
                    finally
                    {
                        NetApiBufferFree(bufPtr);
                    }
                }

                // 2. 主动探测当前用户的个人私人目录候选（解决部分 fnOS/Linux 环境下 NetShareEnum 隐藏个人主目录的问题）
                if (!string.IsNullOrEmpty(currentUsername))
                {
                    string[] personalCandidates = new[]
                    {
                        currentUsername,                  // \\server\<username>
                        "home",                           // \\server\home
                        $"homes\\{currentUsername}",       // \\server\homes\<username>
                        "我的文件",                        // \\server\我的文件
                        "个人空间",                        // \\server\个人空间
                        "私人备份"                         // \\server\私人备份
                    };

                    foreach (var cand in personalCandidates)
                    {
                        string testUnc = $@"\\{serverAddress}\{cand}";
                        // 如果列表中尚未包含该项
                        if (!list.Any(s => s.UncPath.Equals(testUnc, StringComparison.OrdinalIgnoreCase)))
                        {
                            try
                            {
                                if (Directory.Exists(testUnc))
                                {
                                    list.Add(new SmbShareItem
                                    {
                                        Name = cand,
                                        Remark = "用户个人私人主目录",
                                        UncPath = testUnc,
                                        Category = NasShareCategory.PersonalSpace
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                }

                // 3. 排序：将私人空间排在最前，团队共享排在后面
                result.Shares = list
                    .OrderByDescending(s => s.Category == NasShareCategory.PersonalSpace)
                    .ThenBy(s => s.Category == NasShareCategory.TeamShare)
                    .ThenBy(s => s.Name)
                    .ToList();

                result.HasPersonalShare = result.Shares.Any(s => s.Category == NasShareCategory.PersonalSpace);

                if (!result.HasPersonalShare)
                {
                    result.GuidanceMessage =
                        "💡 提示：当前 NAS 用户账号尚未发现独立的 SMB 私人主目录共享。\n" +
                        "建议在飞牛 fnOS 管理后台 ->【文件服务】/【共享文件夹】中，开启『用户个人主目录』或为您的账号新建一个专属的个人私有共享文件夹（如『私人备份』并仅对您的账号授权），即可在列表中优先选择。您也可以直接在上方自定义输入备份路径。";
                }

                return result;
            });
        }

        public static NasShareCategory CategorizeShare(string shareName, string? username)
        {
            if (!string.IsNullOrEmpty(username))
            {
                if (string.Equals(shareName, username, StringComparison.OrdinalIgnoreCase))
                    return NasShareCategory.PersonalSpace;
            }

            if (string.Equals(shareName, "home", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(shareName, "homes", StringComparison.OrdinalIgnoreCase) ||
                shareName.Contains("我的文件") ||
                shareName.Contains("私人") ||
                shareName.Contains("个人"))
            {
                return NasShareCategory.PersonalSpace;
            }

            if (shareName.Contains("团队") ||
                shareName.Contains("public", StringComparison.OrdinalIgnoreCase) ||
                shareName.Contains("share", StringComparison.OrdinalIgnoreCase) ||
                shareName.Contains("cenco", StringComparison.OrdinalIgnoreCase))
            {
                return NasShareCategory.TeamShare;
            }

            return NasShareCategory.TeamShare;
        }

        /// <summary>
        /// 兼容旧版调用的共享列出方法
        /// </summary>
        public async Task<List<SmbShareItem>> EnumerateSharesAsync(string serverAddress)
        {
            var res = await EnumerateCategorizedSharesAsync(serverAddress);
            return res.Shares;
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
                    if (sub.Name.StartsWith("$") ||
                        sub.Name.Equals(".recycle", StringComparison.OrdinalIgnoreCase) ||
                        sub.Name.Equals(Manifest.MetadataDirName, StringComparison.OrdinalIgnoreCase))
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
                        IsShareRoot = false,
                        Category = NasShareCategory.PersonalSpace
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
                        IsShareRoot = false,
                        Category = NasShareCategory.PersonalSpace
                    };
                }
                catch (UnauthorizedAccessException)
                {
                    throw new UnauthorizedAccessException($"创建文件夹被拒绝：当前 NAS 账号在【{parentUncPath}】缺少写入权限。请在飞牛系统管理中开放该共享的写权限。");
                }
            });
        }

        /// <summary>
        /// 检查目标目录是否已存在以及是否与其他任务冲突，自动规划去重建议名称
        /// </summary>
        public async Task<(string FinalSubFolder, bool IsExisting, bool HasForeignConflict)> PlanTargetSubFolderAsync(
            string destinationRootUnc,
            string desiredFolderName,
            string sourceFolderPath)
        {
            return await Task.Run(async () =>
            {
                string candidateName = desiredFolderName.Trim();
                string targetUnc = Path.Combine(destinationRootUnc, candidateName);

                if (!Directory.Exists(targetUnc))
                {
                    return (candidateName, false, false);
                }

                // 目标目录已存在：检查是否包含当前任务的历史元数据
                var manifestMgr = new ManifestManager();
                try
                {
                    var manifest = await manifestMgr.LoadManifestAsync(targetUnc);
                    // 如果清单中的 SourceRoot 与当前源目录一致，则说明是当前任务的原有备份目录，直接复用
                    if (string.Equals(PathHelper.GetCanonicalPath(manifest.SourceRoot), PathHelper.GetCanonicalPath(sourceFolderPath), StringComparison.OrdinalIgnoreCase))
                    {
                        return (candidateName, true, false);
                    }

                    // 如果清单属于其他源文件夹，或者已存在非空文件，为防止数据混杂，自动生成去重后缀建议
                    if (manifest.Files.Count > 0 || Directory.EnumerateFileSystemEntries(targetUnc).Any())
                    {
                        int suffix = 2;
                        string dedupName;
                        while (true)
                        {
                            dedupName = $"{candidateName}_{suffix}";
                            string testUnc = Path.Combine(destinationRootUnc, dedupName);
                            if (!Directory.Exists(testUnc))
                            {
                                break;
                            }
                            suffix++;
                        }
                        return (dedupName, true, true);
                    }
                }
                catch { }

                return (candidateName, true, false);
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
