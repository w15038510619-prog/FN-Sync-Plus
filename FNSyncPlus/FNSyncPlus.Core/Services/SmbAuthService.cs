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

    public enum NasShareSecurityState
    {
        NotConfigured,         // 尚未配置 (中性灰)
        VerifiedPersonalSpace, // 已验证的私人备份位置 (绿色盾牌)
        UnverifiedSmbShare,    // 普通 SMB 共享，私人属性未验证 (蓝色)
        TeamShare,             // 团队共享 (黄色警告，可能被其他成员查看)
        NoPermission,          // 无写入权限 (红色)
        ShareNotFound          // 目标共享不存在 (找不到网络名 Win32 Error 67，红色)
    }

    public class ShareSecurityEvaluation
    {
        public NasShareSecurityState State { get; set; } = NasShareSecurityState.NotConfigured;
        public string Title { get; set; } = "尚未配置";
        public string Description { get; set; } = "";
        public bool IsDangerousForPersonalUse => State == NasShareSecurityState.TeamShare || State == NasShareSecurityState.NoPermission || State == NasShareSecurityState.ShareNotFound;

        public string BadgeColorHex => State switch
        {
            NasShareSecurityState.VerifiedPersonalSpace => "#15803D", // Green
            NasShareSecurityState.TeamShare => "#B45309",             // Amber
            NasShareSecurityState.UnverifiedSmbShare => "#2563EB",    // Blue
            NasShareSecurityState.NoPermission => "#B91C1C",          // Red
            NasShareSecurityState.ShareNotFound => "#B91C1C",         // Red
            _ => "#64748B"                                           // Slate
        };

        public string BadgeBgHex => State switch
        {
            NasShareSecurityState.VerifiedPersonalSpace => "#DCFCE7", // Light Green
            NasShareSecurityState.TeamShare => "#FEF3C7",             // Light Amber
            NasShareSecurityState.UnverifiedSmbShare => "#DBEAFE",    // Light Blue
            NasShareSecurityState.NoPermission => "#FEE2E2",          // Light Red
            NasShareSecurityState.ShareNotFound => "#FEE2E2",         // Light Red
            _ => "#F1F5F9"                                           // Light Slate
        };
    }

    /// <summary>
    /// 区分 NAS 访问的四个状态层级：认证存在、共享存在、目录可读访问、目录可写
    /// </summary>
    public class NasDirectoryValidationResult
    {
        public bool IsNasAuthenticated { get; set; }
        public bool DoesShareExist { get; set; }
        public bool IsDirectoryAccessible { get; set; }
        public bool IsDirectoryWritable { get; set; }
        public int Win32ErrorCode { get; set; }
        public string ServerName { get; set; } = string.Empty;
        public string ShareName { get; set; } = string.Empty;
        public string StateDescription { get; set; } = string.Empty;
        public string DetailedMessage { get; set; } = string.Empty;
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
        /// 断开与指定 NAS 服务器的 SMB 会话
        /// </summary>
        public async Task<(bool Success, string Message)> DisconnectNasAsync(string serverAddress)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(serverAddress))
                {
                    return (true, "已断开");
                }

                serverAddress = serverAddress.Trim().Trim('\\', '/');
                if (serverAddress.Contains(':'))
                {
                    serverAddress = serverAddress.Split(':')[0];
                }

                // 尝试断开 IPC$ 及挂载会话
                try
                {
                    WNetCancelConnection2($@"\\{serverAddress}\IPC$", 0, bForce: true);
                }
                catch { }

                try
                {
                    using var p = new System.Diagnostics.Process();
                    p.StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "net.exe",
                        Arguments = $"use \\\\{serverAddress} /delete /y",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    p.Start();
                    p.WaitForExit(3000);
                }
                catch { }

                return (true, "已安全断开 SMB 会话");
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
        /// 解析并翻译 Windows SMB Win32 异常，精准提取 Win32 错误码（Error 67 找不到网络名、Error 5 拒绝访问、Error 1219 凭据冲突等）
        /// </summary>
        public static (int ErrorCode, string FriendlyMessage) TranslateSmbException(Exception ex, string? targetPath = null)
        {
            int win32Code = 0;
            if (ex is Win32Exception w32ex)
            {
                win32Code = w32ex.NativeErrorCode;
            }
            else if (ex.HResult != 0)
            {
                win32Code = ex.HResult & 0xFFFF;
            }

            // 如果异常消息中直接包含特定错误特征
            string msg = ex.Message ?? string.Empty;
            if (win32Code == 0)
            {
                if (msg.Contains("找不到网络名") || msg.Contains("network name cannot be found") || msg.Contains("BAD_NET_NAME"))
                {
                    win32Code = 67;
                }
                else if (msg.Contains("拒绝访问") || msg.Contains("Access is denied") || ex is UnauthorizedAccessException)
                {
                    win32Code = 5;
                }
                else if (msg.Contains("1219") || msg.Contains("SESSION_CREDENTIAL_CONFLICT"))
                {
                    win32Code = 1219;
                }
            }

            string pathInfo = !string.IsNullOrEmpty(targetPath) ? $"【{targetPath}】" : "";

            string friendly = win32Code switch
            {
                67 => $"找不到网络共享名{pathInfo} (Win32 Error 67: ERROR_BAD_NET_NAME)。该共享在 NAS 上不存在或未对外开放。系统已阻止向不存在的共享执行操作。",
                5 => $"访问被拒绝{pathInfo} (Win32 Error 5: ERROR_ACCESS_DENIED)。当前 NAS 账号缺少访问或写入权限。请在飞牛 fnOS 控制面板分配权限。",
                1219 => $"Windows SMB 凭据冲突 (Win32 Error 1219: ERROR_SESSION_CREDENTIAL_CONFLICT)。已存在使用其他账号连接到该 NAS 的会话。请断开现有连接后重试。",
                53 => $"找不到网络路径{pathInfo} (Win32 Error 53: ERROR_BAD_NETPATH)。无法连接到目标 NAS 服务器，请检查局域网连接与 NAS IP 地址。",
                1326 => $"登录身份验证失败 (Win32 Error 1326: ERROR_LOGON_FAILURE)。用户名或密码错误，请检查 NAS 账号密码。",
                _ => $"操作失败{pathInfo}: {ex.Message} (Win32 Code: {win32Code})"
            };

            return (win32Code, friendly);
        }

        /// <summary>
        /// 深度校验指定 NAS 路径，严格区分「NAS 已认证」「共享存在」「目录可访问」「目录可写」四种状态
        /// </summary>
        public async Task<NasDirectoryValidationResult> ValidateDirectoryAccessStateAsync(string uncPath, string? fallbackServer = null)
        {
            return await Task.Run(() =>
            {
                var result = new NasDirectoryValidationResult();
                if (string.IsNullOrWhiteSpace(uncPath))
                {
                    result.StateDescription = "未配置路径";
                    result.DetailedMessage = "尚未指定 NAS 备份根目录。";
                    return result;
                }

                var (server, share, baseShareUnc, isServerRoot) = PathHelper.ParseUncShare(uncPath);
                if (string.IsNullOrEmpty(server)) server = fallbackServer ?? string.Empty;
                result.ServerName = server;
                result.ShareName = share;

                if (isServerRoot || string.IsNullOrEmpty(share))
                {
                    result.IsNasAuthenticated = true;
                    result.DoesShareExist = false;
                    result.StateDescription = "处于服务器根路径 (尚未选择具体共享)";
                    result.DetailedMessage = "请在共享列表中点击进入具体的共享文件夹。";
                    return result;
                }

                string shareLongPath = PathHelper.EnsureLongPathPrefix(baseShareUnc);
                string targetLongPath = PathHelper.EnsureLongPathPrefix(uncPath);

                // 1. 验证目标共享是否存在 (排除 Win32 Error 67)
                try
                {
                    if (!Directory.Exists(shareLongPath))
                    {
                        result.IsNasAuthenticated = true;
                        result.DoesShareExist = false;
                        result.Win32ErrorCode = 67; // ERROR_BAD_NET_NAME
                        result.StateDescription = "共享不存在 (找不到网络名)";
                        result.DetailedMessage = $"找不到网络共享名【{share}】(Win32 Error 67: ERROR_BAD_NET_NAME)。该共享在 NAS 上不存在，请先选择有效共享。";
                        return result;
                    }
                    result.DoesShareExist = true;
                }
                catch (Exception ex)
                {
                    var (code, msg) = TranslateSmbException(ex, baseShareUnc);
                    result.Win32ErrorCode = code;
                    result.DoesShareExist = false;
                    result.StateDescription = code == 67 ? "共享不存在 (找不到网络名)" : "共享验证失败";
                    result.DetailedMessage = msg;
                    return result;
                }

                // 2. 验证目录是否可访问/列出 (排除 Win32 Error 5)
                try
                {
                    if (Directory.Exists(targetLongPath))
                    {
                        var di = new DirectoryInfo(targetLongPath);
                        _ = di.EnumerateDirectories().Take(1).ToList();
                        result.IsDirectoryAccessible = true;
                    }
                    else
                    {
                        result.IsDirectoryAccessible = true;
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    result.IsDirectoryAccessible = false;
                    result.Win32ErrorCode = 5; // ERROR_ACCESS_DENIED
                    result.StateDescription = "目录拒绝访问 (权限不足)";
                    result.DetailedMessage = $"访问被拒绝 (Win32 Error 5: ERROR_ACCESS_DENIED)。当前账号没有读取目录【{uncPath}】的权限。";
                    return result;
                }
                catch (Exception ex)
                {
                    var (code, msg) = TranslateSmbException(ex, uncPath);
                    result.IsDirectoryAccessible = false;
                    result.Win32ErrorCode = code;
                    result.StateDescription = "目录无法访问";
                    result.DetailedMessage = msg;
                    return result;
                }

                // 3. 验证目录是否具备写入权限
                string probeBase = Directory.Exists(targetLongPath) ? uncPath : baseShareUnc;
                string testFile = Path.Combine(probeBase, $".fn_perm_test_{Guid.NewGuid():N}.tmp");
                string testFileLong = PathHelper.EnsureLongPathPrefix(testFile);

                try
                {
                    using (var fs = new FileStream(testFileLong, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] testBytes = Encoding.UTF8.GetBytes("probe");
                        fs.Write(testBytes, 0, testBytes.Length);
                    }
                    try { File.Delete(testFileLong); } catch { }
                    result.IsDirectoryWritable = true;
                    result.StateDescription = "就绪且具备读写权限";
                    result.DetailedMessage = $"目录【{uncPath}】有效，且当前账号具备完整的读写权限。";
                }
                catch (UnauthorizedAccessException)
                {
                    result.IsDirectoryWritable = false;
                    result.Win32ErrorCode = 5;
                    result.StateDescription = "目录只读 (无写入权限)";
                    result.DetailedMessage = $"当前账号在【{uncPath}】缺少写入权限 (Win32 Error 5: ERROR_ACCESS_DENIED)。请在飞牛后台开放写权限。";
                }
                catch (Exception ex)
                {
                    var (code, msg) = TranslateSmbException(ex, uncPath);
                    result.IsDirectoryWritable = false;
                    result.Win32ErrorCode = code;
                    result.StateDescription = "无法写入";
                    result.DetailedMessage = msg;
                }

                return result;
            });
        }

        /// <summary>
        /// 深度评估指定 NAS 路径的安全与隐私级别，明确区分私人空间、团队共享、未验证共享及无权限
        /// </summary>
        public static ShareSecurityEvaluation EvaluateShareSecurity(string? uncPath, string? username, bool canWrite = true, string? writeError = null)
        {
            var eval = new ShareSecurityEvaluation();

            if (string.IsNullOrWhiteSpace(uncPath))
            {
                eval.State = NasShareSecurityState.NotConfigured;
                eval.Title = "尚未配置私人备份位置";
                eval.Description = "请点击【浏览并选择 NAS 目录】指定备份位置，或在飞牛 fnOS 开启用户主目录。";
                return eval;
            }

            var (server, share, baseShareUnc, isServerRoot) = PathHelper.ParseUncShare(uncPath);
            if (isServerRoot || string.IsNullOrEmpty(share))
            {
                eval.State = NasShareSecurityState.NotConfigured;
                eval.Title = "处于 NAS 服务器根目录";
                eval.Description = "请在共享列表中选择具体的共享文件夹作为备份存储位置。";
                return eval;
            }

            // 检查是否为共享不存在 (Error 67: 找不到网络名)
            if (writeError != null && (writeError.Contains("67") || writeError.Contains("找不到网络名") || writeError.Contains("network name cannot be found") || writeError.Contains("BAD_NET_NAME")))
            {
                eval.State = NasShareSecurityState.ShareNotFound;
                eval.Title = "找不到网络共享名 (Win32 Error 67)";
                eval.Description = $"NAS 上未找到指定的共享文件夹【{share}】。该共享不存在或已被管理员关闭。系统已阻止向不存在的共享创建任务。";
                return eval;
            }

            if (!canWrite)
            {
                eval.State = NasShareSecurityState.NoPermission;
                eval.Title = "无写入权限";
                eval.Description = $"当前账号无法向此目录写入文件: {writeError ?? "拒绝访问"}。请在 fnOS 管理后台为此账号开通读写权限。";
                return eval;
            }

            // 1. 优先检查是否为已验证的私人主目录
            bool isPersonal = false;
            if (!string.IsNullOrEmpty(username) && string.Equals(share, username, StringComparison.OrdinalIgnoreCase))
            {
                isPersonal = true;
            }
            else if (string.Equals(share, "home", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(share, "homes", StringComparison.OrdinalIgnoreCase) ||
                     share.Contains("我的文件") ||
                     (share.Contains("私人") && !share.Contains("团队")) ||
                     (share.Contains("个人") && !share.Contains("公")))
            {
                isPersonal = true;
            }

            if (isPersonal)
            {
                eval.State = NasShareSecurityState.VerifiedPersonalSpace;
                eval.Title = "已验证的私人备份安全空间";
                eval.Description = $"当前目录位于您的专属私人空间【{share}】，仅您当前账号具备访问权限，数据隐私安全得到充分保障。";
                return eval;
            }

            // 2. 严格检查是否属于公共或团队文件共享 (绝不能因为有可写权限就当作私人空间)
            if (share.Contains("团队") ||
                share.Contains("public", StringComparison.OrdinalIgnoreCase) ||
                share.Contains("share", StringComparison.OrdinalIgnoreCase) ||
                share.Contains("common", StringComparison.OrdinalIgnoreCase))
            {
                eval.State = NasShareSecurityState.TeamShare;
                eval.Title = "团队公有共享文件 (非私人空间)";
                eval.Description = $"共享【{share}】属于团队公用空间，其他成员可能拥有查看或修改权限。建议个人工作文件备份到专属个人主目录。";
                return eval;
            }

            // 3. 普通未验证 SMB 共享
            eval.State = NasShareSecurityState.UnverifiedSmbShare;
            eval.Title = "普通 SMB 共享 (私人属性未验证)";
            eval.Description = $"目录【{share}】为标准网络共享，未验证是否具备多用户隐私隔离。如需绝对隐私，请使用个人主目录。";
            return eval;
        }

        /// <summary>
        /// 解析默认备份根目录：优先使用有效且可写的 PreferredBackupRoot，其次从真实枚举共享中选择已验证的私人空间。
        /// 严禁根据用户名盲目拼接共享名！若未发现可访问的私人共享，严格返回空并提示未配置，绝不自动选择团队文件。
        /// </summary>
        public async Task<(string DefaultRootUnc, ShareSecurityEvaluation Security, bool HasAccessiblePersonalShare)> ResolveDefaultBackupRootAsync(
            string serverAddress,
            string? currentUsername,
            string? preferredBackupRoot)
        {
            if (string.IsNullOrWhiteSpace(serverAddress))
            {
                return (string.Empty, new ShareSecurityEvaluation 
                { 
                    State = NasShareSecurityState.NotConfigured, 
                    Title = "尚未连接 NAS", 
                    Description = "请先在【NAS 设备】页面完成登录认证。" 
                }, false);
            }

            // 1. 如果已配置 PreferredBackupRoot，测试其真实有效性与写入权限
            if (!string.IsNullOrWhiteSpace(preferredBackupRoot))
            {
                var (canWrite, writeErr) = await TestDirectoryWritePermissionAsync(preferredBackupRoot);
                if (canWrite)
                {
                    var eval = EvaluateShareSecurity(preferredBackupRoot, currentUsername, canWrite, writeErr);
                    return (preferredBackupRoot, eval, eval.State == NasShareSecurityState.VerifiedPersonalSpace);
                }
            }

            // 2. 从真实枚举的共享列表中寻找已验证且可写的私人共享 (严禁根据用户名盲目拼接共享名)
            var categorized = await EnumerateCategorizedSharesAsync(serverAddress, currentUsername);
            var personalCandidates = categorized.Shares.Where(s => s.Category == NasShareCategory.PersonalSpace).ToList();

            foreach (var personal in personalCandidates)
            {
                var (canWrite, writeErr) = await TestDirectoryWritePermissionAsync(personal.UncPath);
                if (canWrite)
                {
                    var eval = EvaluateShareSecurity(personal.UncPath, currentUsername, canWrite, writeErr);
                    return (personal.UncPath, eval, true);
                }
            }

            // 3. 未发现可访问的私人共享：严格返回空，严禁自动选择团队共享或伪造路径
            var emptyEval = new ShareSecurityEvaluation
            {
                State = NasShareSecurityState.NotConfigured,
                Title = "未发现可访问的私人 SMB 目录",
                Description = "当前 NAS 账号未发现可访问的专属私人 SMB 共享。为保护您的数据隐私，系统不会自动选择团队共享。请点击下一步在 NAS 浏览器中手动选择，或在飞牛 fnOS 控制面板开启个人主目录。"
            };

            return (string.Empty, emptyEval, false);
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
                if (string.IsNullOrWhiteSpace(uncPath))
                {
                    return (false, "未指定测试路径");
                }

                var (server, share, baseShareUnc, isServerRoot) = PathHelper.ParseUncShare(uncPath);
                if (isServerRoot || string.IsNullOrEmpty(share))
                {
                    return (false, "当前处于 NAS 服务器根路径，未选择具体共享");
                }

                // 优先检查共享本身是否存在，避免无意义的异常 (Win32 Error 67: 找不到网络名)
                string shareLongPath = PathHelper.EnsureLongPathPrefix(baseShareUnc);
                try
                {
                    if (!Directory.Exists(shareLongPath))
                    {
                        return (false, $"找不到网络共享名【{share}】(Win32 Error 67: ERROR_BAD_NET_NAME)。共享不存在。");
                    }
                }
                catch (Exception ex)
                {
                    var (code, friendlyMsg) = TranslateSmbException(ex, baseShareUnc);
                    return (false, friendlyMsg);
                }

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
                    return (false, "当前登录账号在此 NAS 文件夹没有写入权限 (Win32 Error 5: ERROR_ACCESS_DENIED)。请联系管理员或在飞牛 NAS 管理面板为该账号分配【读写】权限。");
                }
                catch (Exception ex)
                {
                    var (code, friendlyMsg) = TranslateSmbException(ex, uncPath);
                    return (false, friendlyMsg);
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

                if (string.IsNullOrWhiteSpace(parentUncPath))
                {
                    throw new ArgumentException("创建文件夹失败：目标父目录路径不能为空。", nameof(parentUncPath));
                }

                var (server, share, shareBase, isServerRoot) = PathHelper.ParseUncShare(parentUncPath);
                if (isServerRoot || string.IsNullOrEmpty(share))
                {
                    throw new InvalidOperationException($"创建文件夹失败：无法直接在 NAS 服务器根路径【{parentUncPath}】创建文件夹，必须先选择具体的共享目录。");
                }

                // 严格验证父共享是否存在，杜绝向不存在的共享创建文件夹 (Error 67)
                string shareLongPath = PathHelper.EnsureLongPathPrefix(shareBase);
                if (!Directory.Exists(shareLongPath))
                {
                    throw new IOException($"创建文件夹失败：找不到网络共享名【{share}】(Win32 Error 67: ERROR_BAD_NET_NAME)。该共享在 NAS 上不存在，请先选择有效共享。");
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
                catch (Exception ex)
                {
                    var (code, friendlyMsg) = TranslateSmbException(ex, newUnc);
                    if (code == 67)
                    {
                        throw new IOException($"创建文件夹失败：找不到网络共享名【{newUnc}】(Win32 Error 67: ERROR_BAD_NET_NAME)。该共享在 NAS 上不存在。", ex);
                    }
                    if (code == 5 || ex is UnauthorizedAccessException)
                    {
                        throw new UnauthorizedAccessException($"创建文件夹失败：当前 NAS 账号在【{parentUncPath}】缺少写入权限 (Win32 Error 5: ERROR_ACCESS_DENIED)。请在飞牛系统管理中开放该共享的写权限。", ex);
                    }
                    if (code == 1219)
                    {
                        throw new IOException($"创建文件夹失败：Windows SMB 会话凭据冲突 (Win32 Error 1219)。", ex);
                    }
                    throw new IOException(friendlyMsg, ex);
                }
            });
        }

        /// <summary>
        /// 检查目标目录是否已存在以及是否与其他任务冲突，自动规划去重建议名称（支持同批次批量添加同名去重）
        /// </summary>
        public async Task<(string FinalSubFolder, bool IsExisting, bool HasForeignConflict)> PlanTargetSubFolderAsync(
            string destinationRootUnc,
            string desiredFolderName,
            string sourceFolderPath,
            IEnumerable<string>? alreadyAllocatedSubNames = null)
        {
            return await Task.Run(async () =>
            {
                var allocatedSet = alreadyAllocatedSubNames != null 
                    ? new HashSet<string>(alreadyAllocatedSubNames, StringComparer.OrdinalIgnoreCase) 
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string candidateName = desiredFolderName.Trim();
                string targetUnc = Path.Combine(destinationRootUnc, candidateName);

                // 1. 同一批次内已经有前面的文件夹占用了该名称 (例如 D:\资料 与 E:\资料 同时添加)
                if (allocatedSet.Contains(candidateName))
                {
                    int suffix = 2;
                    string dedupName;
                    while (true)
                    {
                        dedupName = $"{candidateName}_{suffix}";
                        string testUnc = Path.Combine(destinationRootUnc, dedupName);
                        if (!allocatedSet.Contains(dedupName) && !Directory.Exists(testUnc))
                        {
                            break;
                        }
                        suffix++;
                    }
                    return (dedupName, false, true);
                }

                // 2. 目标目录不存在且未被同批次占用：直接采用原名
                if (!Directory.Exists(targetUnc))
                {
                    return (candidateName, false, false);
                }

                // 3. 目标目录已在 NAS 存在：检查是否包含当前任务的历史元数据
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
                            if (!allocatedSet.Contains(dedupName) && !Directory.Exists(testUnc))
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
