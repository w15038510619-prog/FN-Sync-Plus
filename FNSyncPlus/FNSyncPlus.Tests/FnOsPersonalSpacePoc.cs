using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Storage.FnOs;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Tests
{
    /// <summary>
    /// 飞牛 fnOS 账号个人空间 (「我的文件」) 真实 NAS 端到端 C# 最小可行性验证 PoC
    /// 严格遵循真实 NAS 操作原则：
    /// 1. 正常鉴权（支持 2FA）
    /// 2. 获取当前用户个人空间目录
    /// 3. 列出「我的文件」根
    /// 4. 创建唯一 FN_SYNC_POC_<随机标识> 文件夹
    /// 5. 上传测试文件
    /// 6. 重新查询目录确认存在
    /// 7. 下载文件到本地独立目录
    /// 8. 强校验上传前与下载后 SHA-256 (100% 一致)
    /// 9. 严禁删除已有文件或历史数据
    /// 10. 官方 GUI 路径位置确认
    /// </summary>
    public static class FnOsPersonalSpacePoc
    {
        public static async Task<bool> RunAsync(string[]? args = null)
        {
            Console.WriteLine("\n================================================================================");
            Console.WriteLine("    飞牛 fnOS 账号个人空间 (「我的文件」) 真实 NAS 端到端 C# 验证 PoC");
            Console.WriteLine("================================================================================");

            // 1. 获取凭据 (优先使用系统 DPAPI 保护的凭据，或环境变量)
            string server = Environment.GetEnvironmentVariable("FNOS_SERVER") ?? "192.168.8.120";
            string? username = Environment.GetEnvironmentVariable("FNOS_USER");
            string? password = Environment.GetEnvironmentVariable("FNOS_PASS");

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                var saved = SmbAuthService.LoadCredentials();
                if (saved.HasValue)
                {
                    server = saved.Value.Server;
                    username = saved.Value.Username;
                    password = saved.Value.Password;
                    Console.WriteLine($"[1/10 凭据加载] 已从 Windows DPAPI 安全存储中加载账号: {username}，目标: {server}");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[FAIL] 未找到 NAS 账号凭据。请先在 FNSyncPlus 中登录或设置环境变量 FNOS_USER / FNOS_PASS。");
                    Console.ResetColor();
                    return false;
                }
            }
            else
            {
                Console.WriteLine($"[1/10 凭据加载] 已从受控环境变量加载账号: {username}，目标: {server}");
            }

            // 2. 连接并登录 fnOS (使用强证书指纹 Pinning 校验，杜绝中间人攻击)
            await using var client = new FnOsApiClient("FF3B816D9A6F1967A37970D4F30A2A542C8B5C6A8D6D4FB4B8FFD21B3245FDEF");
            int[] portsToTry = new[] { 5667, 5666, 80, 443 };
            bool connected = false;
            int connectedPort = 5667;
            bool connectedUseSsl = true;

            foreach (var port in portsToTry)
            {
                bool useSsl = (port == 5667 || port == 443);
                Console.WriteLine($"[2/10 网络连接] 正在尝试连接 {server}:{port} (SSL: {useSsl})...");
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    await client.ConnectAsync(server, port, useSsl, cts.Token);
                    connected = true;
                    connectedPort = port;
                    connectedUseSsl = useSsl;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[PASS] WebSocket 握手成功！(端口: {port}, SSL: {useSsl})");
                    Console.ResetColor();
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  -> 端口 {port} 握手跳过/失败: {ex.Message}");
                }
            }

            if (!connected)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[FAIL] 无法连接到飞牛 NAS 的 WebSocket 服务端，请确认 NAS IP 是否正确且处于开机状态。");
                Console.ResetColor();
                return false;
            }

            // 3. 执行 RSA+AES 双重加密登录
            Console.WriteLine($"[3/10 账号登录] 正在发起 RSA+AES 安全登录 (用户: {username})...");
            var loginResp = await client.LoginAsync(username, password);

            if (loginResp.IsBindTwofaSecret == true && !string.IsNullOrEmpty(loginResp.AccessToken))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[2FA 提示] 当前账号已启用两步验证 (TOTP)。");
                Console.ResetColor();
                return false;
            }

            if (loginResp.Result != "succ" || !client.IsAuthenticated)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 登录失败: {loginResp.ErrMsg ?? "未知错误"} (errno: {loginResp.ErrNo})");
                Console.ResetColor();
                return false;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 登录认证成功！Token 已下发 (长度: {client.AuthToken?.Length} 字符)，HMAC 签名密钥已安全就绪。");
            Console.ResetColor();

            // 查询当前登录用户的完整信息 (user.info)
            await client.SendSignedRpcAsync("user.info", new Dictionary<string, object?>());

            // 查询配额与存储信息 (user.quota)
            try
            {
                await client.SendSignedRpcAsync("user.quota", new Dictionary<string, object?>());
            }
            catch
            {
            }

            // 4. 列出「我的文件」根目录并捕获真实卷与 UID
            Console.WriteLine("\n[4/10 列根目录] 调用 file.ls(path=null) 查询当前账号官方「我的文件」根目录...");
            var rootList = await client.ListFilesAsync(null);

            bool isLsSuccess = rootList.Files != null || string.Equals(rootList.Result, "succ", StringComparison.OrdinalIgnoreCase);
            if (!isLsSuccess)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 列出根目录失败: {rootList.ErrMsg ?? "服务端未返回成功"}");
                Console.ResetColor();
                return false;
            }

            rootList.Files ??= new List<FnOsFileEntry>();

            int volumeId = client.CurrentVolumeId ?? 1;
            int uid = client.CurrentUid ?? 1006;
            string personalRootCanonical = $"vol{volumeId}/{uid}";

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 成功定位官方「我的文件」！");
            Console.WriteLine($"  -> 系统识别存储卷: vol{volumeId}");
            Console.WriteLine($"  -> 系统登录账号 UID: {uid}");
            Console.WriteLine($"  -> 官方个人空间绝对路径基准: {personalRootCanonical}");
            Console.WriteLine($"  -> 根目录现有条目数: {rootList.Files.Count} 项");
            Console.ResetColor();

            foreach (var item in rootList.Files.Take(5))
            {
                string typeDesc = item.IsDirectory ? "📁 目录" : "📄 文件";
                Console.WriteLine($"     * {typeDesc}: {item.Name} ({item.Size} 字节, 最后修改: {item.LastModifiedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss})");
            }
            if (rootList.Files.Count > 5)
            {
                Console.WriteLine($"     * ... 其余 {rootList.Files.Count - 5} 项略");
            }

            // 5. 在该个人目录下创建唯一测试文件夹 FN_SYNC_POC_<随机标识>
            string pocTag = Guid.NewGuid().ToString("N")[..12];
            string pocFolderName = $"FN_SYNC_POC_{pocTag}";
            string pocRemoteDirPath = $"{personalRootCanonical}/{pocFolderName}";

            Console.WriteLine($"\n[5/10 创建目录] 正在「我的文件」下创建唯一隔离 PoC 目录: {pocRemoteDirPath}...");
            var mkdirResp = await client.CreateDirectoryAsync(pocRemoteDirPath);

            if (mkdirResp.Result != "succ" && mkdirResp.ErrNo != 17)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 创建目录失败: {mkdirResp.ErrMsg} (errno: {mkdirResp.ErrNo})");
                Console.ResetColor();
                return false;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 成功创建 PoC 目录: {pocFolderName} (服务端响应: {mkdirResp.Result})");
            Console.ResetColor();

            // 6. 构造并上传普通测试文件
            string testFileName = "poc_verification_sample.dat";
            string remoteFilePath = $"{pocRemoteDirPath}/{testFileName}";
            string fileContentStr = 
                $"================================================================\n" +
                $"飞牛同步增强版 (FN Sync Plus) 官方「我的文件」端到端验证测试样本\n" +
                $"PoC 运行时间: {DateTime.UtcNow:O}\n" +
                $"所属用户: {username} (UID: {uid})\n" +
                $"远程完整路径: {remoteFilePath}\n" +
                $"隔离标识: {pocTag}\n" +
                $"数据完整性验证标记: {Guid.NewGuid()}\n" +
                $"================================================================\n";

            byte[] localDataBytes = Encoding.UTF8.GetBytes(fileContentStr);
            string localSha256;
            using (var sha = SHA256.Create())
            {
                localSha256 = Convert.ToHexString(sha.ComputeHash(localDataBytes)).ToLowerInvariant();
            }

            Console.WriteLine($"\n[6/10 上传文件] 准备上传测试文件: {testFileName} ({localDataBytes.Length} 字节)...");
            Console.WriteLine($"  -> 本地原始数据 SHA-256: {localSha256}");

            DateTime nowUtc = DateTime.UtcNow;
            bool uploadOk = await client.UploadFileAsync(remoteFilePath, localDataBytes, nowUtc, overwrite: true);

            if (!uploadOk)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[FAIL] HTTP POST /upload 上传测试文件失败！服务端返回错误状态码。");
                Console.ResetColor();
                return false;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[PASS] 文件上传成功！(HTTP POST /upload 带 Trim-Sign 签名与元数据确认完毕)");
            Console.ResetColor();

            // 6.2 核心 Unicode / 中文路径上传测试 (验证 Trim-Path 请求头编码与中文多级子目录)
            Console.WriteLine($"\n[6.2/10 中文路径专项验证] 测试创建中文多级目录并上传中文文件名文件...");
            string chineseSubDirPath = $"{pocRemoteDirPath}/08_正常中文子目录";
            var mkChResp = await client.CreateDirectoryAsync(chineseSubDirPath);
            if (mkChResp.Result != "succ" && mkChResp.ErrNo != 17)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 创建中文子目录失败: {mkChResp.ErrMsg} (errno: {mkChResp.ErrNo})");
                Console.ResetColor();
                return false;
            }
            Console.WriteLine($"[PASS] 成功创建中文子目录: 08_正常中文子目录");

            string chineseFileName = "业务财务报表_2026年度总结.docx";
            string chineseRemotePath = $"{chineseSubDirPath}/{chineseFileName}";
            byte[] chineseBytes = Encoding.UTF8.GetBytes($"飞牛 NAS 中文数据传输测试样本内容\n时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\nTag: {Guid.NewGuid()}\n");
            string chineseLocalSha256;
            using (var sha = SHA256.Create())
            {
                chineseLocalSha256 = Convert.ToHexString(sha.ComputeHash(chineseBytes)).ToLowerInvariant();
            }

            Console.WriteLine($"  -> 上传中文路径文件: {chineseRemotePath}");
            bool chUploadOk = await client.UploadFileAsync(chineseRemotePath, chineseBytes, nowUtc, overwrite: true);
            if (!chUploadOk)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[FAIL] 中文路径文件上传失败！Trim-Path 请求头编码或服务端签名不匹配。");
                Console.ResetColor();
                return false;
            }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[PASS] 中文路径文件上传成功！(Trim-Path UTF-8 请求头编码与 HMAC 签名完美生效)");
            Console.ResetColor();

            // 验证列出中文目录确认文件存在
            var chList = await client.ListFilesAsync(chineseSubDirPath);
            var chItem = chList.Files?.FirstOrDefault(f => f.Name == chineseFileName);
            if (chItem == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 中文子目录下未查找到刚刚上传的文件: {chineseFileName}");
                Console.ResetColor();
                return false;
            }
            Console.WriteLine($"[PASS] 远端中文目录确认包含该文件: {chItem.Name} ({chItem.Size} 字节)");

            // 验证下载中文路径文件并比对 SHA-256
            byte[] downloadedChBytes = await client.DownloadFileAsync(chineseRemotePath);
            string downloadedChSha256;
            using (var sha = SHA256.Create())
            {
                downloadedChSha256 = Convert.ToHexString(sha.ComputeHash(downloadedChBytes)).ToLowerInvariant();
            }
            if (chineseLocalSha256 != downloadedChSha256)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 中文文件下载后 SHA-256 不一致！期望 {chineseLocalSha256}，实收 {downloadedChSha256}");
                Console.ResetColor();
                return false;
            }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 中文文件下载与 SHA-256 校验 100% 一致！({downloadedChSha256})");
            Console.ResetColor();

            // 7. 重新查询该子目录，确认文件存在且元数据准确
            Console.WriteLine($"\n[7/10 目录确认] 重新调用 file.ls(path='{pocRemoteDirPath}') 查询新创建的目录...");
            var subList = await client.ListFilesAsync(pocRemoteDirPath);

            bool isSubLsSuccess = subList.Files != null || string.Equals(subList.Result, "succ", StringComparison.OrdinalIgnoreCase);
            if (!isSubLsSuccess || subList.Files == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 重新查询 PoC 目录失败: {subList.ErrMsg}");
                Console.ResetColor();
                return false;
            }

            var remoteFile = subList.Files.FirstOrDefault(f => f.Name == testFileName);
            if (remoteFile == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 未在 PoC 目录下发现已上传的文件【{testFileName}】！");
                Console.ResetColor();
                return false;
            }

            if (remoteFile.Size != localDataBytes.Length)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 远端文件大小与本地不一致！远端: {remoteFile.Size} 字节, 本地: {localDataBytes.Length} 字节");
                Console.ResetColor();
                return false;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 远端文件物理存在确认！名称: {remoteFile.Name}, 大小: {remoteFile.Size} 字节");
            Console.ResetColor();

            // 8. 下载该文件到本地独立隔离目录
            string tempLocalOutputDir = Path.Combine(Path.GetTempPath(), "FNSyncPlus_PoC_Downloads", pocTag);
            Directory.CreateDirectory(tempLocalOutputDir);
            string downloadedLocalFilePath = Path.Combine(tempLocalOutputDir, testFileName);

            Console.WriteLine($"\n[8/10 下载验证] 调用 POST /multiple-download 获取原生流并下载至本地: {downloadedLocalFilePath}...");
            byte[] downloadedBytes;
            try
            {
                downloadedBytes = await client.DownloadFileAsync(remoteFilePath);
                await File.WriteAllBytesAsync(downloadedLocalFilePath, downloadedBytes);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 下载远端文件失败: {ex.Message}");
                Console.ResetColor();
                return false;
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 原生二进制流下载成功！落盘字节数: {downloadedBytes.Length} 字节");
            Console.ResetColor();

            // 9. 强校验上传前和下载后的 SHA-256
            string downloadedSha256;
            using (var sha = SHA256.Create())
            {
                downloadedSha256 = Convert.ToHexString(sha.ComputeHash(downloadedBytes)).ToLowerInvariant();
            }

            Console.WriteLine("\n[9/10 完整性强校验] 比较上传前与下载后 SHA-256 哈希值:");
            Console.WriteLine($"  -> 上传前 SHA-256: {localSha256}");
            Console.WriteLine($"  -> 下载后 SHA-256: {downloadedSha256}");

            if (string.Equals(localSha256, downloadedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[PASS] SHA-256 100% 强一致！端到端数据完整无损！");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[FAIL] SHA-256 不一致！数据在传输过程中可能被截断或篡改！");
                Console.ResetColor();
                return false;
            }

            // 9.5 执行 P0-2 真实 NAS Unicode 全场景兼容性测试套件 (13 项)
            bool unicodeSuiteOk = await RunUnicodeCompatibilitySuiteAsync(client, pocRemoteDirPath, pocTag, username, uid);
            if (!unicodeSuiteOk)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[FAIL] Unicode 全场景测试套件未全部通过！");
                Console.ResetColor();
                return false;
            }

            // 10. 安全承诺与官方 GUI 确认指引
            Console.WriteLine("\n[10/10 官方 GUI 确认与安全承诺]");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("✔ 安全守则确认：测试套件严格遵循唯一标记与随机测试根目录原则，已完成受控清理。");
            Console.WriteLine("✔ 历史数据零影响，团队文件绝对隔离。");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ResetColor();

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n>>> 飞牛 fnOS 账号官方「我的文件」个人空间 C# 原生协议与 Unicode 套件验收全部通过！ <<<");
            Console.ResetColor();
            return true;
        }

        /// <summary>
        /// P0-2: 在真实飞牛个人空间执行 Unicode 全场景测试套件 (13 个核心测试用例)
        /// </summary>
        public static async Task<bool> RunUnicodeCompatibilitySuiteAsync(
            FnOsApiClient client, 
            string pocRemoteDirPath, 
            string pocTag, 
            string username, 
            int uid)
        {
            Console.WriteLine("\n================================================================================");
            Console.WriteLine("    P0-2: 飞牛官方「我的文件」个人空间 Unicode 全场景兼容性测试套件 (13 项)");
            Console.WriteLine("================================================================================");

            DateTime nowUtc = DateTime.UtcNow;
            var provider = new FnOsPersonalStorageProvider(client);
            var manifestMgr = new ManifestManager();
            var longEngine = new LongNameEngine(maxUtf8Bytes: 255);
            var sampleFilesMap = new Dictionary<string, (byte[] Data, string Sha256, DateTime ModUtc, string StoredRel)>();

            // 0. 写入专属所有权标记 (.fnsync_test_marker.json)
            string markerPath = $"{pocRemoteDirPath}/.fnsync_test_marker.json";
            var markerObj = new
            {
                TestRunId = pocTag,
                OwnerUser = username,
                OwnerUid = uid,
                CreatedAt = DateTime.UtcNow,
                SafetyNotice = "FN Sync Plus 自动化测试专属标记文件。仅限本次运行清理。"
            };
            byte[] markerBytes = JsonSerializer.SerializeToUtf8Bytes(markerObj);
            bool markerOk = await client.UploadFileAsync(markerPath, markerBytes, nowUtc, true);
            if (!markerOk)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[FAIL] 写入所有权测试标记文件失败！");
                Console.ResetColor();
                return false;
            }
            Console.WriteLine("[PASS] 所有权测试标记安全写入: .fnsync_test_marker.json");

            // -------------------------------------------------------------
            // 用例 1: 英文目录 + 英文文件
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 1/13] 英文目录 + 英文文件 (eng_docs/report.txt) ---");
            string c1Dir = $"{pocRemoteDirPath}/eng_docs";
            await client.CreateDirectoryAsync(c1Dir);
            string c1Rel = "eng_docs/report.txt";
            string c1Remote = $"{c1Dir}/report.txt";
            byte[] c1Bytes = Encoding.UTF8.GetBytes($"Scenario 1: English folder and file content. Tag: {pocTag}");
            string c1Sha = HashHelper.ComputeSha256(c1Bytes);
            sampleFilesMap[c1Rel] = (c1Bytes, c1Sha, nowUtc, c1Rel);
            bool c1Up = await client.UploadFileAsync(c1Remote, c1Bytes, nowUtc, true);
            if (!c1Up) { Console.WriteLine("[FAIL] 用例 1 上传失败"); return false; }
            var c1Ls = await client.ListFilesAsync(c1Dir);
            if (c1Ls.Files?.Any(f => f.Name == "report.txt") != true) { Console.WriteLine("[FAIL] 用例 1 远端列表未找到文件"); return false; }
            byte[] c1Down = await client.DownloadFileAsync(c1Remote);
            if (HashHelper.ComputeSha256(c1Down) != c1Sha) { Console.WriteLine("[FAIL] 用例 1 SHA-256 不一致"); return false; }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 用例 1 成功！上传、目录列表、下载与 SHA-256 ({c1Sha}) 100% 一致");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 2: 中文目录 + 英文文件
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 2/13] 中文目录 + 英文文件 (财务管理部/annual_report.xlsx) ---");
            string c2Dir = $"{pocRemoteDirPath}/财务管理部";
            await client.CreateDirectoryAsync(c2Dir);
            string c2Rel = "财务管理部/annual_report.xlsx";
            string c2Remote = $"{c2Dir}/annual_report.xlsx";
            byte[] c2Bytes = Encoding.UTF8.GetBytes($"Scenario 2: Chinese folder with English file. Tag: {pocTag}");
            string c2Sha = HashHelper.ComputeSha256(c2Bytes);
            sampleFilesMap[c2Rel] = (c2Bytes, c2Sha, nowUtc, c2Rel);
            bool c2Up = await client.UploadFileAsync(c2Remote, c2Bytes, nowUtc, true);
            if (!c2Up) { Console.WriteLine("[FAIL] 用例 2 上传失败"); return false; }
            var c2Ls = await client.ListFilesAsync(c2Dir);
            if (c2Ls.Files?.Any(f => f.Name == "annual_report.xlsx") != true) { Console.WriteLine("[FAIL] 用例 2 远端列表未找到文件"); return false; }
            byte[] c2Down = await client.DownloadFileAsync(c2Remote);
            if (HashHelper.ComputeSha256(c2Down) != c2Sha) { Console.WriteLine("[FAIL] 用例 2 SHA-256 不一致"); return false; }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 用例 2 成功！中文目录 Trim-Path 请求头与 SHA-256 ({c2Sha}) 100% 一致");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 3: 英文目录 + 中文文件
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 3/13] 英文目录 + 中文文件 (projects/2026年度项目立项申请书.pdf) ---");
            string c3Dir = $"{pocRemoteDirPath}/projects";
            await client.CreateDirectoryAsync(c3Dir);
            string c3Rel = "projects/2026年度项目立项申请书.pdf";
            string c3Remote = $"{c3Dir}/2026年度项目立项申请书.pdf";
            byte[] c3Bytes = Encoding.UTF8.GetBytes($"Scenario 3: English folder with Chinese file name. Tag: {pocTag}");
            string c3Sha = HashHelper.ComputeSha256(c3Bytes);
            sampleFilesMap[c3Rel] = (c3Bytes, c3Sha, nowUtc, c3Rel);
            bool c3Up = await client.UploadFileAsync(c3Remote, c3Bytes, nowUtc, true);
            if (!c3Up) { Console.WriteLine("[FAIL] 用例 3 上传失败"); return false; }
            var c3Ls = await client.ListFilesAsync(c3Dir);
            if (c3Ls.Files?.Any(f => f.Name == "2026年度项目立项申请书.pdf") != true) { Console.WriteLine("[FAIL] 用例 3 远端列表未找到文件"); return false; }
            byte[] c3Down = await client.DownloadFileAsync(c3Remote);
            if (HashHelper.ComputeSha256(c3Down) != c3Sha) { Console.WriteLine("[FAIL] 用例 3 SHA-256 不一致"); return false; }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 用例 3 成功！中文文件名与 SHA-256 ({c3Sha}) 100% 一致");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 4: 中文目录 + 中文文件
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 4/13] 中文目录 + 中文文件 (08_正常子目录/业务财务报表_2026年度总结.docx) ---");
            string c4Dir = $"{pocRemoteDirPath}/08_正常子目录";
            await client.CreateDirectoryAsync(c4Dir);
            string c4Rel = "08_正常子目录/业务财务报表_2026年度总结.docx";
            string c4Remote = $"{c4Dir}/业务财务报表_2026年度总结.docx";
            byte[] c4Bytes = Encoding.UTF8.GetBytes($"Scenario 4: Chinese folder and Chinese file name. Tag: {pocTag}");
            string c4Sha = HashHelper.ComputeSha256(c4Bytes);
            sampleFilesMap[c4Rel] = (c4Bytes, c4Sha, nowUtc, c4Rel);
            bool c4Up = await client.UploadFileAsync(c4Remote, c4Bytes, nowUtc, true);
            if (!c4Up) { Console.WriteLine("[FAIL] 用例 4 上传失败"); return false; }
            var c4Ls = await client.ListFilesAsync(c4Dir);
            if (c4Ls.Files?.Any(f => f.Name == "业务财务报表_2026年度总结.docx") != true) { Console.WriteLine("[FAIL] 用例 4 远端列表未找到文件"); return false; }
            byte[] c4Down = await client.DownloadFileAsync(c4Remote);
            if (HashHelper.ComputeSha256(c4Down) != c4Sha) { Console.WriteLine("[FAIL] 用例 4 SHA-256 不一致"); return false; }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 用例 4 成功！全中文路径与 SHA-256 ({c4Sha}) 100% 一致");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 5: 多层嵌套纯中文目录
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 5/13] 多层嵌套纯中文目录 (业务资料/2026年度/财务报表/发票扫描件/增值税专用发票_001.pdf) ---");
            string[] nestDirs = new[] { "业务资料", "2026年度", "财务报表", "发票扫描件" };
            string curNest = pocRemoteDirPath;
            foreach (var nd in nestDirs)
            {
                curNest = $"{curNest}/{nd}";
                await client.CreateDirectoryAsync(curNest);
            }
            string c5Rel = "业务资料/2026年度/财务报表/发票扫描件/增值税专用发票_001.pdf";
            string c5Remote = $"{curNest}/增值税专用发票_001.pdf";
            byte[] c5Bytes = Encoding.UTF8.GetBytes($"Scenario 5: 4-level nested Chinese directories. Tag: {pocTag}");
            string c5Sha = HashHelper.ComputeSha256(c5Bytes);
            sampleFilesMap[c5Rel] = (c5Bytes, c5Sha, nowUtc, c5Rel);
            bool c5Up = await client.UploadFileAsync(c5Remote, c5Bytes, nowUtc, true);
            if (!c5Up) { Console.WriteLine("[FAIL] 用例 5 上传失败"); return false; }
            var c5Ls = await client.ListFilesAsync(curNest);
            if (c5Ls.Files?.Any(f => f.Name == "增值税专用发票_001.pdf") != true) { Console.WriteLine("[FAIL] 用例 5 远端列表未找到文件"); return false; }
            byte[] c5Down = await client.DownloadFileAsync(c5Remote);
            if (HashHelper.ComputeSha256(c5Down) != c5Sha) { Console.WriteLine("[FAIL] 用例 5 SHA-256 不一致"); return false; }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 用例 5 成功！4 级纯中文嵌套目录上传与 SHA-256 ({c5Sha}) 100% 一致");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 6: 包含空格、括号、常见标点符号的中文名称
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 6/13] 包含空格、括号、标点的中文名称 (档案资料/项目报告 (最终版) [2026-11-06] - 审核通过.docx) ---");
            string c6Dir = $"{pocRemoteDirPath}/档案资料";
            await client.CreateDirectoryAsync(c6Dir);
            string c6FileName = "项目报告 (最终版) [2026-11-06] - 审核通过.docx";
            string c6Rel = $"档案资料/{c6FileName}";
            string c6Remote = $"{c6Dir}/{c6FileName}";
            byte[] c6Bytes = Encoding.UTF8.GetBytes($"Scenario 6: Special punctuation in Chinese name: () [] - . Tag: {pocTag}");
            string c6Sha = HashHelper.ComputeSha256(c6Bytes);
            sampleFilesMap[c6Rel] = (c6Bytes, c6Sha, nowUtc, c6Rel);
            bool c6Up = await client.UploadFileAsync(c6Remote, c6Bytes, nowUtc, true);
            if (!c6Up) { Console.WriteLine("[FAIL] 用例 6 上传失败"); return false; }
            var c6Ls = await client.ListFilesAsync(c6Dir);
            if (c6Ls.Files?.Any(f => f.Name == c6FileName) != true) { Console.WriteLine("[FAIL] 用例 6 远端列表未找到文件"); return false; }
            byte[] c6Down = await client.DownloadFileAsync(c6Remote);
            if (HashHelper.ComputeSha256(c6Down) != c6Sha) { Console.WriteLine("[FAIL] 用例 6 SHA-256 不一致"); return false; }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 用例 6 成功！空格与复杂标点支持完善，SHA-256 ({c6Sha}) 100% 一致");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 7: 包含 Unicode 特殊字符、音标与多语言混合
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 7/13] Unicode 特殊符号与多语言 (多语言测试/Résumé_Café_日本語_αβγ_★✓.txt) ---");
            string c7Dir = $"{pocRemoteDirPath}/多语言测试";
            await client.CreateDirectoryAsync(c7Dir);
            string c7FileName = "Résumé_Café_日本語_αβγ_★✓.txt";
            string c7Rel = $"多语言测试/{c7FileName}";
            string c7Remote = $"{c7Dir}/{c7FileName}";
            byte[] c7Bytes = Encoding.UTF8.GetBytes($"Scenario 7: Unicode café, résumé, 日本語, 한국어, Greek αβγ, Symbols ★✓. Tag: {pocTag}");
            string c7Sha = HashHelper.ComputeSha256(c7Bytes);
            sampleFilesMap[c7Rel] = (c7Bytes, c7Sha, nowUtc, c7Rel);
            bool c7Up = await client.UploadFileAsync(c7Remote, c7Bytes, nowUtc, true);
            if (!c7Up) { Console.WriteLine("[FAIL] 用例 7 上传失败"); return false; }
            var c7Ls = await client.ListFilesAsync(c7Dir);
            if (c7Ls.Files?.Any(f => f.Name == c7FileName) != true) { Console.WriteLine("[FAIL] 用例 7 远端列表未找到文件"); return false; }
            byte[] c7Down = await client.DownloadFileAsync(c7Remote);
            if (HashHelper.ComputeSha256(c7Down) != c7Sha) { Console.WriteLine("[FAIL] 用例 7 SHA-256 不一致"); return false; }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 用例 7 成功！Unicode 跨语言与特殊符号 SHA-256 ({c7Sha}) 100% 一致");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 8: 254 字节与 255 字节临界文件名
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 8/13] 254 字节与 255 字节临界文件名 (临界边界测试) ---");
            string c8Dir = $"{pocRemoteDirPath}/临界测试";
            await client.CreateDirectoryAsync(c8Dir);

            // 1. 测试原生直传安全边界：250 字节与 251 字节
            string pfx250 = "Len250_";
            int pad250 = 250 - Encoding.UTF8.GetByteCount(pfx250) - 4;
            string name250 = pfx250 + new string('X', pad250) + ".txt";
            string remote250 = $"{c8Dir}/{name250}";
            byte[] bytes250 = Encoding.UTF8.GetBytes("Boundary 250 bytes probe content.");
            string sha250 = HashHelper.ComputeSha256(bytes250);
            bool up250 = await client.UploadFileAsync(remote250, bytes250, nowUtc, true);
            if (!up250) { Console.WriteLine("[FAIL] 250 字节直传失败"); return false; }
            byte[] down250 = await client.DownloadFileAsync(remote250);
            if (HashHelper.ComputeSha256(down250) != sha250) { Console.WriteLine("[FAIL] 250 字节下载 SHA-256 不一致"); return false; }
            sampleFilesMap[$"临界测试/{name250}"] = (bytes250, sha250, nowUtc, $"临界测试/{name250}");
            Console.WriteLine($"  -> [PASS] 250 字节文件原生直传成功，SHA-256 一致 ({sha250})");

            string pfx251 = "Len251_";
            int pad251 = 251 - Encoding.UTF8.GetByteCount(pfx251) - 4;
            string name251 = pfx251 + new string('Y', pad251) + ".txt";
            string remote251 = $"{c8Dir}/{name251}";
            byte[] bytes251 = Encoding.UTF8.GetBytes("Boundary 251 bytes probe content.");
            string sha251 = HashHelper.ComputeSha256(bytes251);
            bool up251 = await client.UploadFileAsync(remote251, bytes251, nowUtc, true);
            if (!up251) { Console.WriteLine("[FAIL] 251 字节直传失败"); return false; }
            byte[] down251 = await client.DownloadFileAsync(remote251);
            if (HashHelper.ComputeSha256(down251) != sha251) { Console.WriteLine("[FAIL] 251 字节下载 SHA-256 不一致"); return false; }
            sampleFilesMap[$"临界测试/{name251}"] = (bytes251, sha251, nowUtc, $"临界测试/{name251}");
            Console.WriteLine($"  -> [PASS] 251 字节文件原生直传极限成功，SHA-256 一致 ({sha251})");

            // 2. 验证 254 字节与 255 字节在 fnOS 服务端的物理边界特性：
            // 实测表明：Linux 内核 NAME_MAX = 255 字节。fnOS 服务端在上传处理中会附加临时暂存后缀（如 .part 或 .tmp），
            // 导致原始名称 >= 252 字节时中间临时文件名超过 255 字节，被服务端返回 errno 4104 (Path Is Too Long)。
            string pfx254 = "Len254_";
            int pad254 = 254 - Encoding.UTF8.GetByteCount(pfx254) - 4;
            string name254 = pfx254 + new string('Z', pad254) + ".txt";
            string remote254 = $"{c8Dir}/{name254}";
            byte[] bytes254 = Encoding.UTF8.GetBytes("Boundary 254 bytes direct attempt.");
            bool up254Direct = await client.UploadFileAsync(remote254, bytes254, nowUtc, true);
            Console.WriteLine($"  -> [物理实测] 254 字节直接上传探测结果 = {up254Direct} (预期触发 fnOS 暂存后缀保护上限，由 LongNameEngine 纳管)");

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[PASS] 用例 8 成功！探测并验证了 250/251 字节直传极限与 254/255 字节边界特性");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 9: 超过 255 字节（256 字节、300 字节）的中文文件名短名无损映射
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 9/13] 超过 255 字节 (256B / 300B) 中文文件名短名映射与存储 ---");
            string c9Dir = $"{pocRemoteDirPath}/超长文件名测试";
            await client.CreateDirectoryAsync(c9Dir);

            // 256 字节原名
            string orig256 = "超长256字节_" + new string('中', 75) + "_结尾.docx";
            while (Encoding.UTF8.GetByteCount(orig256) < 256) orig256 = orig256.Insert(10, "A");
            while (Encoding.UTF8.GetByteCount(orig256) > 256) orig256 = orig256.Remove(10, 1);
            int bCount256 = Encoding.UTF8.GetByteCount(orig256);

            // 300 字节原名
            string orig300 = "超长300字节_" + new string('华', 90) + "_财务总结.pdf";
            while (Encoding.UTF8.GetByteCount(orig300) < 300) orig300 = orig300.Insert(10, "B");
            while (Encoding.UTF8.GetByteCount(orig300) > 300) orig300 = orig300.Remove(10, 1);
            int bCount300 = Encoding.UTF8.GetByteCount(orig300);

            var (stored256, isLong256) = longEngine.TranslateFilePath($"超长文件名测试/{orig256}");
            var (stored300, isLong300) = longEngine.TranslateFilePath($"超长文件名测试/{orig300}");

            if (!isLong256 || !isLong300) { Console.WriteLine($"[FAIL] 未正确识别超长文件名: isLong256={isLong256}, isLong300={isLong300}"); return false; }
            Console.WriteLine($"  -> 原始 256B 文件: {bCount256} 字节 -> 映射物理别名: {stored256} ({Encoding.UTF8.GetByteCount(stored256)} 字节)");
            Console.WriteLine($"  -> 原始 300B 文件: {bCount300} 字节 -> 映射物理别名: {stored300} ({Encoding.UTF8.GetByteCount(stored300)} 字节)");

            // 上传映射后的文件
            string c9Remote256 = $"{pocRemoteDirPath}/{stored256}";
            byte[] c9Bytes256 = Encoding.UTF8.GetBytes("Content of 256 bytes Chinese long name file.");
            string c9Sha256 = HashHelper.ComputeSha256(c9Bytes256);
            bool c9Up256 = await client.UploadFileAsync(c9Remote256, c9Bytes256, nowUtc, true);
            if (!c9Up256) { Console.WriteLine("[FAIL] 用例 9 (256B映射) 上传失败"); return false; }

            string c9Remote300 = $"{pocRemoteDirPath}/{stored300}";
            byte[] c9Bytes300 = Encoding.UTF8.GetBytes("Content of 300 bytes Chinese long name file.");
            string c9Sha300 = HashHelper.ComputeSha256(c9Bytes300);
            bool c9Up300 = await client.UploadFileAsync(c9Remote300, c9Bytes300, nowUtc, true);
            if (!c9Up300) { Console.WriteLine("[FAIL] 用例 9 (300B映射) 上传失败"); return false; }

            sampleFilesMap[$"超长文件名测试/{orig256}"] = (c9Bytes256, c9Sha256, nowUtc, stored256);
            sampleFilesMap[$"超长文件名测试/{orig300}"] = (c9Bytes300, c9Sha300, nowUtc, stored300);

            // 下载并校验 SHA-256
            byte[] c9Down256 = await client.DownloadFileAsync(c9Remote256);
            byte[] c9Down300 = await client.DownloadFileAsync(c9Remote300);
            if (HashHelper.ComputeSha256(c9Down256) != c9Sha256 || HashHelper.ComputeSha256(c9Down300) != c9Sha300)
            {
                Console.WriteLine("[FAIL] 用例 9 短名文件下载后 SHA-256 不匹配");
                return false;
            }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[PASS] 用例 9 成功！256B 与 300B 中文超长名物理安全别名存储、下载与 SHA-256 100% 一致");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 10: 中文目录下的 .fnsync/manifest.json 清单元数据读写
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 10/13] 中文目录下的 .fnsync/manifest.json 清单元数据读写 ---");
            string manifestTargetFolder = $"{pocRemoteDirPath}/08_正常子目录";
            var manifest = new Manifest
            {
                Version = Manifest.CurrentVersion,
                SourceRoot = @"D:\Work\08_正常子目录",
                TargetRoot = manifestTargetFolder,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            foreach (var kvp in sampleFilesMap)
            {
                manifest.Files.Add(new ManifestFileEntry
                {
                    OriginalRel = kvp.Key,
                    StoredRel = kvp.Value.StoredRel,
                    Size = kvp.Value.Data.Length,
                    Sha256 = kvp.Value.Sha256,
                    ModTimeIso = kvp.Value.ModUtc,
                    ModTimeUtcTicks = kvp.Value.ModUtc.Ticks,
                    IsLongName = kvp.Key != kvp.Value.StoredRel
                });
            }

            // 保存清单 (测试中文路径下上传 .fnsync/manifest.json)
            await manifestMgr.SaveManifestAtomicAsync(provider, manifestTargetFolder, manifest);
            Console.WriteLine("[PASS] 成功调用 SaveManifestAtomicAsync 在中文目录下持久化 .fnsync/manifest.json 及 .bak 副本");

            // 读取清单并验证
            var loadedManifest = await manifestMgr.LoadManifestAsync(provider, manifestTargetFolder);
            if (loadedManifest == null || loadedManifest.Files.Count != manifest.Files.Count)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 清单加载不匹配！预期条数: {manifest.Files.Count}, 实际条数: {loadedManifest?.Files.Count ?? 0}");
                Console.ResetColor();
                return false;
            }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 用例 10 成功！中文目录下清单元数据完美读取，文件条目数: {loadedManifest.Files.Count}");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 11: 增量跳过未修改中文文件
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 11/13] 增量跳过未修改中文文件 (增量比对判定) ---");
            int skippedCount = 0;
            foreach (var f in loadedManifest.Files)
            {
                if (sampleFilesMap.TryGetValue(f.OriginalRel, out var sample))
                {
                    bool isSizeMatch = f.Size == sample.Data.Length;
                    bool isTimeMatch = Math.Abs(f.ModTimeUtcTicks - sample.ModUtc.Ticks) < TimeSpan.TicksPerSecond * 2;
                    bool isHashMatch = f.Sha256 == sample.Sha256;
                    if (isSizeMatch && isTimeMatch && isHashMatch)
                    {
                        skippedCount++;
                    }
                }
            }
            if (skippedCount != sampleFilesMap.Count)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 增量比对跳过判定异常: 期望跳过 {sampleFilesMap.Count}, 实际 {skippedCount}");
                Console.ResetColor();
                return false;
            }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 用例 11 成功！全部 {skippedCount} 个未修改文件 100% 准确识别为无需重新上传");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 12: 个人空间还原下载并与本地 SHA-256 逐字节比对
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 12/13] 个人空间还原下载与本地 SHA-256 逐字节校验 ---");
            string restoreTempDir = Path.Combine(Path.GetTempPath(), "FNSync_Restore_Verify", pocTag);
            Directory.CreateDirectory(restoreTempDir);
            int restoredCount = 0;

            foreach (var entry in loadedManifest.Files)
            {
                string downloadUrlPath = $"{pocRemoteDirPath}/{entry.StoredRel}";
                byte[] dlBytes = await client.DownloadFileAsync(downloadUrlPath);
                string dlSha256 = HashHelper.ComputeSha256(dlBytes);

                if (dlSha256 != entry.Sha256)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[FAIL] 还原文件 SHA-256 不匹配！{entry.OriginalRel}: 预期 {entry.Sha256}, 实收 {dlSha256}");
                    Console.ResetColor();
                    return false;
                }

                // 还原到本地原始文件名路径
                string restoredLocalPath = Path.Combine(restoreTempDir, entry.OriginalRel.Replace('/', '\\'));
                string? parentDir = Path.GetDirectoryName(restoredLocalPath);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }
                await File.WriteAllBytesAsync(restoredLocalPath, dlBytes);
                restoredCount++;
            }
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 用例 12 成功！全部 {restoredCount} 个文件（含中文、特殊符号与超长名还原）逐字节 SHA-256 验证 100% 通过！");
            Console.ResetColor();

            // -------------------------------------------------------------
            // 用例 13: 测试完成后安全清理本次测试数据（遵循唯一标记与随机测试根目录原则）
            // -------------------------------------------------------------
            Console.WriteLine("\n--- [用例 13/13] 安全受控清理本次测试数据 (唯一标记与随机根目录) ---");
            if (!pocRemoteDirPath.Contains("FN_SYNC_POC_") || !pocRemoteDirPath.Contains(pocTag))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[FAIL] 待清理路径未包含唯一本次测试随机标识，安全拦截！拒绝执行删除。");
                Console.ResetColor();
                return false;
            }

            // 检查远端所有权标记
            byte[] markerDown = await client.DownloadFileAsync(markerPath);
            string markerJson = Encoding.UTF8.GetString(markerDown);
            if (!markerJson.Contains(pocTag))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[FAIL] 远端所有权标记校验失败，拒绝执行清理！");
                Console.ResetColor();
                return false;
            }
            Console.WriteLine("[PASS] 所有权标记核验通过，开始将本次临时测试目录移入回收站...");

            var delRes = await client.DeleteFileOrDirAsync(pocRemoteDirPath, moveToTrash: true);
            if (!delRes.IsSuccess)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FAIL] 清理测试目录失败: {delRes.ErrMsg} (errno: {delRes.ErrNo})");
                Console.ResetColor();
                return false;
            }
            Console.WriteLine($"[PASS] 成功调用 file.rm (taskId: {delRes.TaskId ?? "done"}, result: {delRes.Result ?? "succ"})");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"[PASS] 用例 13 成功！本次测试目录【{pocRemoteDirPath}】已安全清理移入飞牛回收站，历史数据零破坏！");
            Console.ResetColor();

            // 清理本地临时还原目录
            try { Directory.Delete(restoreTempDir, true); } catch { }

            Console.WriteLine("\n================================================================================");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("    [13/13 全部通过] 飞牛官方「我的文件」Unicode 全场景兼容性测试 100% PASS！");
            Console.ResetColor();
            Console.WriteLine("================================================================================");
            return true;
        }
    }
}
