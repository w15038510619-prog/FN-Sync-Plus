using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services
{
    /// <summary>
    /// 飞牛官方客户端同步任务记录
    /// </summary>
    public class FnOsOfficialTask
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string NasPath { get; set; } = string.Empty;
        public string LocalPath { get; set; } = string.Empty;
        public int Status { get; set; }
    }

    /// <summary>
    /// 飞牛官方客户端环境与账号元数据检测结果
    /// </summary>
    public class FnOsOfficialClientInfo
    {
        public bool IsInstalled { get; set; }
        public string ClientInstallPath { get; set; } = string.Empty;
        public string DatabasePath { get; set; } = string.Empty;
        public int Uid { get; set; }
        public string Username { get; set; } = string.Empty;
        public string NasName { get; set; } = string.Empty;
        public string ServerAddress { get; set; } = string.Empty;
        public string FnosVersion { get; set; } = string.Empty;
        public string PersonalPhysicalPath => Uid > 0 ? $"/vol1/{Uid}" : string.Empty;
        public List<FnOsOfficialTask> OfficialTasks { get; set; } = new();

        public string SummaryText
        {
            get
            {
                if (!IsInstalled) return "本机未检测到飞牛官方桌面客户端";
                string tasksStr = OfficialTasks.Count > 0
                    ? $"，配置同步任务数: {OfficialTasks.Count}"
                    : "，暂无官方同步任务";
                return $"检测到飞牛官方客户端 (UID: {Uid}, 账号: {Username}, fnOS: {FnosVersion}, 私人物理目录: {PersonalPhysicalPath}{tasksStr})";
            }
        }
    }

    /// <summary>
    /// 飞牛 fnOS 个人备份空间 (我的文件) 状态评估模型
    /// </summary>
    public class FnOsPersonalSpaceStatus
    {
        public bool HasPersonalSpaceTarget { get; set; }
        public string PersonalSpaceRootUnc { get; set; } = string.Empty;
        public string DisplayTitle { get; set; } = string.Empty;
        public string Explanation { get; set; } = string.Empty;
        public string SetupGuidance { get; set; } = string.Empty;
        public FnOsOfficialClientInfo? OfficialClientInfo { get; set; }
        public bool CanDirectlyBackup { get; set; }
        public bool IsTeamShareDiverted { get; set; } = false; // 严禁自动挪用团队文件标记
    }

    /// <summary>
    /// 飞牛 fnOS 个人空间集成与 SMB 权限诊断服务
    /// 负责研究官方客户端个人目录机制、检测 fnOS SMB 用户可见共享配置、指导个人空间开启并实现验证过的个人空间备份规划
    /// </summary>
    public class FnOsIntegrationService
    {
        private readonly SmbAuthService _smbService;

        public FnOsIntegrationService(SmbAuthService smbService)
        {
            _smbService = smbService;
        }

        /// <summary>
        /// 探测本地飞牛官方客户端安装与配置数据库
        /// </summary>
        public FnOsOfficialClientInfo DetectOfficialClientInfo()
        {
            var info = new FnOsOfficialClientInfo();

            // 1. 查找官方客户端可执行路径
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string[] candidateInstallDirs = new[]
            {
                @"D:\Users\S\AppData\Local\飞牛同步",
                Path.Combine(localAppData, "飞牛同步"),
                Path.Combine(localAppData, "com.fn-sync-client.app")
            };

            foreach (var dir in candidateInstallDirs)
            {
                string exePath = Path.Combine(dir, "fn-sync-client.exe");
                if (File.Exists(exePath))
                {
                    info.IsInstalled = true;
                    info.ClientInstallPath = exePath;
                    break;
                }
            }

            // 2. 查找官方 SQLite 数据库
            string[] candidateDbPaths = new[]
            {
                Path.Combine(appData, "com.fn-sync-client.app", "database.sqlite"),
                Path.Combine(localAppData, "com.fn-sync-client.app", "database.sqlite")
            };

            string foundDb = candidateDbPaths.FirstOrDefault(File.Exists) ?? string.Empty;
            if (string.IsNullOrEmpty(foundDb))
            {
                return info;
            }

            info.DatabasePath = foundDb;
            info.IsInstalled = true;

            // 3. 安全以只读模式读取数据库内容 (绝不加锁，绝不影响官方客户端运行)
            try
            {
                string connStr = new SqliteConnectionStringBuilder
                {
                    DataSource = foundDb,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString();

                using var conn = new SqliteConnection(connStr);
                conn.Open();

                // 读取账号信息
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT uid, username, nas_name, address, fnos_version FROM nas_accounts LIMIT 1;";
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        info.Uid = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                        info.Username = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                        info.NasName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                        info.ServerAddress = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                        info.FnosVersion = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                    }
                }

                // 读取同步任务列表
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, name, mode, nas_path, pc_path, status FROM tasks LIMIT 20;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        info.OfficialTasks.Add(new FnOsOfficialTask
                        {
                            Id = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                            Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                            Mode = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            NasPath = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                            LocalPath = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                            Status = reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                // 读取非致命异常，保留已知信息
                System.Diagnostics.Debug.WriteLine($"[FnOsIntegration] 读取官方客户端数据库异常: {ex.Message}");
            }

            return info;
        }

        /// <summary>
        /// 全面评估 NAS 个人空间 (我的文件) 可用性与 SMB 暴露配置
        /// </summary>
        public async Task<FnOsPersonalSpaceStatus> EvaluatePersonalSpaceAsync(string serverAddress, string username, string preferredRoot)
        {
            var status = new FnOsPersonalSpaceStatus();
            var officialInfo = DetectOfficialClientInfo();
            status.OfficialClientInfo = officialInfo;

            // 1. 优先校验已配置的偏好根目录 (PreferredBackupRoot) 是否为真实可写个人空间
            if (!string.IsNullOrWhiteSpace(preferredRoot))
            {
                var validation = await _smbService.ValidateDirectoryAccessStateAsync(preferredRoot, serverAddress);
                if (validation.DoesShareExist && validation.IsDirectoryWritable)
                {
                    var eval = SmbAuthService.EvaluateShareSecurity(preferredRoot, username, true);
                    if (eval.State == NasShareSecurityState.VerifiedPersonalSpace)
                    {
                        status.HasPersonalSpaceTarget = true;
                        status.PersonalSpaceRootUnc = preferredRoot;
                        status.CanDirectlyBackup = true;
                        status.DisplayTitle = "👤 个人空间 (我的文件) 已就绪";
                        status.Explanation = $"已连接至已验证的个人备份专属位置：{preferredRoot}。具备完整读写权限。";
                        return status;
                    }
                }
            }

            // 2. 枚举当前 NAS 上真实对外开放的 SMB 共享
            var categorized = await _smbService.EnumerateCategorizedSharesAsync(serverAddress, username);
            var personalShare = categorized.Shares.FirstOrDefault(s => s.Category == NasShareCategory.PersonalSpace);

            if (personalShare != null)
            {
                // 验证该共享是否可写
                var validation = await _smbService.ValidateDirectoryAccessStateAsync(personalShare.UncPath, serverAddress);
                if (validation.DoesShareExist && validation.IsDirectoryWritable)
                {
                    status.HasPersonalSpaceTarget = true;
                    status.PersonalSpaceRootUnc = personalShare.UncPath;
                    status.CanDirectlyBackup = true;
                    status.DisplayTitle = "👤 个人空间 (我的文件) 已就绪";
                    status.Explanation = $"检测到 NAS 个人专属 SMB 共享：{personalShare.UncPath}。已通过读写权限强验证。";
                    return status;
                }
            }

            // 3. 未在 SMB 中发现个人专属共享：生成真实原因剖析与专属配置指引 (绝不自动使用团队共享)
            status.HasPersonalSpaceTarget = false;
            status.PersonalSpaceRootUnc = string.Empty;
            status.CanDirectlyBackup = false;
            status.IsTeamShareDiverted = false;

            string uidInfo = officialInfo.Uid > 0 ? $" (检测到您在 fnOS 的内部 UID 为 {officialInfo.Uid}，物理路径 /vol1/{officialInfo.Uid})" : "";

            status.DisplayTitle = "未发现可访问的 fnOS 个人专属 SMB 共享";
            status.Explanation =
                $"当前 NAS 账号【{username}】未开放独立的个人空间 SMB 共享目录{uidInfo}。\n" +
                "飞牛 fnOS 默认将「我的文件」存放在系统内部存储空间中，默认未对局域网开放个人主目录的 SMB 网络共享；Windows SMB 列表中当前仅存在团队共享文件。\n" +
                "为了保护您的私人数据安全，FN Sync Plus 严格遵循隐私隔离规范：\n" +
                "1. 绝不自动将个人文件备份至公共团队共享；\n" +
                "2. 严禁通过拼接用户名伪造不存在的私人网络路径。";

            status.SetupGuidance =
                "【如何在飞牛 fnOS 中开启个人专属 SMB 共享】：\n" +
                $"1. 使用电脑浏览器登录飞牛后台管理界面 (https://{serverAddress}:5667)；\n" +
                "2. 进入【文件管理】->【我的文件】，或在【控制面板】->【共享文件夹】中；\n" +
                "3. 为您的账号新建一个专属的共享文件夹（例如命名为『个人备份』或『我的文件』），并在权限设置中将其分配给当前账号【读写】权限，其他用户设为【禁止访问】；\n" +
                "4. 完成后返回本软件，点击【刷新当前目录】，FN Sync Plus 将自动识别并优先挂载为「我的文件（个人空间）」。";

            return status;
        }

        /// <summary>
        /// 在经过验证的个人空间根目录下，自动规划同名子备份目录并建立
        /// 例如本地源为 D:\MyData\Docs，个人根目录为 \\server\personal，自动建立并规划为 \\server\personal\Docs
        /// </summary>
        public async Task<(bool Success, string TargetUnc, string ErrorMessage)> EnsureSameNameBackupFolderAsync(string personalRootUnc, string localSourcePath)
        {
            if (string.IsNullOrWhiteSpace(personalRootUnc))
            {
                return (false, string.Empty, "规划备份目录失败：尚未配置个人空间根目录。");
            }

            if (string.IsNullOrWhiteSpace(localSourcePath))
            {
                return (false, string.Empty, "规划备份目录失败：本地源文件夹路径不能为空。");
            }

            string folderName = Path.GetFileName(localSourcePath.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(folderName))
            {
                folderName = "BackupFolder";
            }

            // 校验合法性
            if (!PathHelper.IsValidDirectoryName(folderName, out string valErr))
            {
                return (false, string.Empty, $"本地文件夹名称不符合 NAS 命名规范: {valErr}");
            }

            // 校验个人根目录状态
            var (server, share, baseShareUnc, isServerRoot) = PathHelper.ParseUncShare(personalRootUnc);
            if (isServerRoot || string.IsNullOrEmpty(share))
            {
                return (false, string.Empty, "无法在服务器根路径建立备份目录，必须是已验证的具体共享。");
            }

            string targetUnc = PathHelper.CombineWindowsPath(personalRootUnc, folderName);
            string longPath = PathHelper.EnsureLongPathPrefix(targetUnc);

            try
            {
                if (!Directory.Exists(longPath))
                {
                    // 调用底层经过强验证的创建子目录逻辑
                    await _smbService.CreateSubFolderAsync(personalRootUnc, folderName, baseShareUnc);
                }

                // 再次验证读写权限
                var validation = await _smbService.ValidateDirectoryAccessStateAsync(targetUnc, server);
                if (!validation.IsDirectoryWritable)
                {
                    return (false, targetUnc, $"已建立同名目录，但当前账号无写入权限: {validation.DetailedMessage}");
                }

                return (true, targetUnc, string.Empty);
            }
            catch (Exception ex)
            {
                var (_, msg) = SmbAuthService.TranslateSmbException(ex, targetUnc);
                return (false, targetUnc, msg);
            }
        }
    }
}
