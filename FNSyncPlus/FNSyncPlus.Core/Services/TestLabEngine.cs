using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services
{
    public class TestFileItem
    {
        public string RelativePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public int Utf8ByteCount { get; set; }
        public long FileSizeBytes { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Sha256Hash { get; set; } = string.Empty;
        public bool RequiresMapping => Utf8ByteCount > 255;
    }

    public class TestFileVerificationResult
    {
        public string RelativePath { get; set; } = string.Empty;
        public int Utf8Bytes { get; set; }
        public bool WasMapped { get; set; }
        public string OriginalSha256 { get; set; } = string.Empty;
        public string RestoredSha256 { get; set; } = string.Empty;
        public bool IsVerified { get; set; }
        public string StatusMessage { get; set; } = "PASS";
    }

    public class TestLabReport
    {
        public string RunId { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Mode { get; set; } = "LocalSimulation"; // LocalSimulation or RealNasSmb
        public string TargetPath { get; set; } = string.Empty;
        public int TotalSourceFiles { get; set; }
        public int LongNameFilesMapped { get; set; }
        public int InitialUploadedFiles { get; set; }
        public int IncrementalSkippedFiles { get; set; }
        public int IncrementalUploadedFiles { get; set; }
        public int RestoredFiles { get; set; }
        public int Sha256VerifiedFiles { get; set; }
        public bool IsAllPassed { get; set; }
        public double ElapsedSeconds { get; set; }
        public List<string> LogEntries { get; set; } = new();
        public List<TestFileVerificationResult> ItemVerifications { get; set; } = new();
    }

    /// <summary>
    /// 备份测试实验室核心引擎：安全隔离生成临界测试集、执行双模式测试、SHA-256逐字节强比对与报告导出
    /// </summary>
    public class TestLabEngine
    {
        private readonly LongNameEngine _longNameEngine;
        private readonly ManifestManager _manifestManager;
        private readonly SmbAuthService _smbService;

        public static string TestLabBaseDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FNSyncPlus",
            "TestLab");

        public static string TestSourceDir => Path.Combine(TestLabBaseDir, "Source");
        public static string MockNasDir => Path.Combine(TestLabBaseDir, "MockNas");
        public static string RestoredBaseDir => Path.Combine(TestLabBaseDir, "Restored");

        public TestLabEngine(LongNameEngine longNameEngine, ManifestManager manifestManager, SmbAuthService smbService)
        {
            _longNameEngine = longNameEngine;
            _manifestManager = manifestManager;
            _smbService = smbService;
        }

        /// <summary>
        /// 生成标准测试文件集于 %LOCALAPPDATA%\FNSyncPlus\TestLab\Source，杜绝污染用户个人目录
        /// </summary>
        public async Task<List<TestFileItem>> GenerateTestSamplesAsync(IProgress<string>? progress = null)
        {
            return await Task.Run(() =>
            {
                progress?.Report("正在准备隔离测试目录环境: " + TestSourceDir);

                if (Directory.Exists(TestSourceDir))
                {
                    try { Directory.Delete(TestSourceDir, true); } catch { }
                }
                Directory.CreateDirectory(TestSourceDir);

                var items = new List<TestFileItem>();

                // 1. 常规中英文多格式文件
                CreateTestFile(items, "01_普通文件/README_文档.txt", "这是一份标准的普通测试文本说明文档，编码采用UTF-8。", "常规文本");
                CreateTestFile(items, "01_普通文件/2026年度业务合作协议.docx", "DOCX_MOCK_DATA_2026_AGREEMENT_BINARY_CONTENT_SAMPLE_FOR_TESTING", "Office文档");
                CreateTestFile(items, "01_普通文件/财务明细预算表_Q1.xlsx", "XLSX_MOCK_SHEET_FINANCIAL_EXPENSES_DATA_2026", "Office表格");
                CreateTestFile(items, "01_普通文件/技术架构规范图谱.pdf", "%PDF-1.7 mock header stream test content 2026", "PDF文档");
                CreateBinaryTestFile(items, "01_普通文件/备份测试数据块.bin", 64 * 1024, "二进制块");

                // 2. 含有空格、特殊符号与 Unicode 字符的文件
                CreateTestFile(items, "02_特殊符号/财务 2026 年（第一季度）· 报告 & 审计 [终稿] 🚀.xlsx", "包含Emoji和全角半角标点的测试报表内容", "特殊字符/Emoji");
                CreateTestFile(items, "02_特殊符号/System~Config #1 @Office(Cenco)+Sub.txt", "含井号、波浪号与括号的文件名测试", "特殊符号");

                // 3. 254 / 255 / 256 / 300 字节 UTF-8 边界极限文件名
                // 254 字节: 83 汉字 (249) + 'a' (1) + '.txt' (4) = 254
                string name254 = new string('汉', 83) + "a.txt";
                CreateTestFile(items, $"03_极限边界/{name254}", "这是 254 字节边界文件内容，NAS 允许直接原生存储无需映射。", "254字节边界(原名)");

                // 255 字节: 83 汉字 (249) + 'ab' (2) + '.txt' (4) = 255
                string name255 = new string('汉', 83) + "ab.txt";
                CreateTestFile(items, $"03_极限边界/{name255}", "这是 255 字节临界文件内容，恰好位于 Linux NAS 单名上限。", "255字节极限(原名)");

                // 256 字节: 84 汉字 (252) + '.txt' (4) = 256 (必须触发短名映射)
                string name256 = new string('汉', 84) + ".txt";
                CreateTestFile(items, $"03_极限边界/{name256}", "这是 256 字节超长文件内容，超过 NAS 255 上限，必须被映射为 _FN_LONG_*。", "256字节超长(映射)");

                // 300 字节: 98 汉字 (294) + 'ab' (2) + '.txt' (4) = 300 (必须触发短名映射)
                string name300 = new string('汉', 98) + "ab.txt";
                CreateTestFile(items, $"03_极限边界/{name300}", "这是 300 字节深度超长中文文件，必须安全无损映射并可100%还原。", "300字节超长(映射)");

                // 4. 超长多层嵌套目录 (每一层 85 汉字 = 255 字节，共三层嵌套)
                string dirLevel1 = "04_超长目录_层级一_" + new string('级', 70);
                string dirLevel2 = "层级二_" + new string('深', 75);
                string relNestedDir = Path.Combine("04_超长目录", dirLevel1, dirLevel2);
                CreateTestFile(items, Path.Combine(relNestedDir, "嵌套超长目录内重要文件.pdf"), "三层嵌套长目录下的重要文件内容", "多层嵌套超长目录");

                // 5. 空目录与嵌套空目录
                string emptyDir1 = Path.Combine(TestSourceDir, "05_空目录", "独立空文件夹A");
                string emptyDir2 = Path.Combine(TestSourceDir, "05_空目录", "父目录", "嵌套空子文件夹B");
                Directory.CreateDirectory(emptyDir1);
                Directory.CreateDirectory(emptyDir2);

                // 6. 跨目录同名文件（验证清单防覆盖能力）
                CreateTestFile(items, "06_同名跨目录/目录A/合同文件.docx", "目录A的合同文件数据", "同名文件A");
                CreateTestFile(items, "06_同名跨目录/目录B/合同文件.docx", "目录B的合同文件数据 (独立不混淆)", "同名文件B");

                progress?.Report($"成功生成 {items.Count} 个测试文件及空目录结构。");
                return items;
            });
        }

        private void CreateTestFile(List<TestFileItem> list, string relPath, string textContent, string category)
        {
            relPath = relPath.Replace('/', '\\');
            string localFullPath = Path.GetFullPath(Path.Combine(TestSourceDir, relPath));
            string longPath = PathHelper.EnsureLongPathPrefix(localFullPath);
            string? dir = Path.GetDirectoryName(longPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(longPath, textContent, Encoding.UTF8);

            string fileName = Path.GetFileName(relPath);
            int byteLen = PathHelper.GetUtf8ByteCount(fileName);
            var fi = new FileInfo(longPath);
            string sha = HashHelper.ComputeFileSha256(longPath);

            list.Add(new TestFileItem
            {
                RelativePath = relPath.Replace('\\', '/'),
                FileName = fileName,
                Utf8ByteCount = byteLen,
                FileSizeBytes = fi.Length,
                Category = category,
                Sha256Hash = sha
            });
        }

        private void CreateBinaryTestFile(List<TestFileItem> list, string relPath, int sizeBytes, string category)
        {
            relPath = relPath.Replace('/', '\\');
            string localFullPath = Path.GetFullPath(Path.Combine(TestSourceDir, relPath));
            string longPath = PathHelper.EnsureLongPathPrefix(localFullPath);
            string? dir = Path.GetDirectoryName(longPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            byte[] data = new byte[sizeBytes];
            new Random(2026).NextBytes(data);
            File.WriteAllBytes(longPath, data);

            string fileName = Path.GetFileName(relPath);
            int byteLen = PathHelper.GetUtf8ByteCount(fileName);
            string sha = HashHelper.ComputeFileSha256(longPath);

            list.Add(new TestFileItem
            {
                RelativePath = relPath.Replace('\\', '/'),
                FileName = fileName,
                Utf8ByteCount = byteLen,
                FileSizeBytes = sizeBytes,
                Category = category,
                Sha256Hash = sha
            });
        }

        /// <summary>
        /// 运行本地隔离环境全流程模拟测试
        /// </summary>
        public async Task<TestLabReport> RunLocalSimulationTestAsync(IProgress<string>? progress = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var report = new TestLabReport
            {
                Mode = "LocalSimulation",
                TargetPath = MockNasDir
            };

            void Log(string msg)
            {
                report.LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
                progress?.Report(msg);
            }

            Log(">>> 开始执行本地隔离备份还原全流程验证测试...");

            // 1. 确保源样本已准备
            if (!Directory.Exists(TestSourceDir) || Directory.GetFiles(TestSourceDir, "*.*", SearchOption.AllDirectories).Length == 0)
            {
                Log("检测到源样本尚未生成，自动生成标准测试样本集...");
                await GenerateTestSamplesAsync(progress);
            }

            // 清理旧的模拟目标和恢复目录
            if (Directory.Exists(MockNasDir))
            {
                try { Directory.Delete(MockNasDir, true); } catch { }
            }
            Directory.CreateDirectory(MockNasDir);

            string runRestoreDir = Path.Combine(RestoredBaseDir, $"Run_{report.RunId}");
            if (Directory.Exists(runRestoreDir))
            {
                try { Directory.Delete(runRestoreDir, true); } catch { }
            }
            Directory.CreateDirectory(runRestoreDir);

            try
            {
                var syncEngine = new SyncEngine(_longNameEngine, _manifestManager, _smbService);

                // 2. 执行第 1 次全量同步
                Log("【第 1 阶段】正在执行初次全量同步到 MockNas 隔离存储...");
                var syncRep1 = await syncEngine.RunSyncAsync(TestSourceDir, MockNasDir);
                report.TotalSourceFiles = syncRep1.TotalFiles;
                report.InitialUploadedFiles = syncRep1.UploadedFiles;
                report.LongNameFilesMapped = syncRep1.LongNameFiles;
                Log($"初次同步完成：总扫描 {syncRep1.TotalFiles}，上传 {syncRep1.UploadedFiles}，长文件名映射 {syncRep1.LongNameFiles}，失败 {syncRep1.FailedFiles}");

                if (syncRep1.FailedFiles > 0)
                {
                    Log($"▲ 错误：初次同步发生 {syncRep1.FailedFiles} 个文件失败！");
                }

                // 3. 执行第 2 次增量测试 (无修改时 0 传输)
                Log("【第 2 阶段】正在执行无变更增量同步测试...");
                var syncRep2 = await syncEngine.RunSyncAsync(TestSourceDir, MockNasDir);
                report.IncrementalSkippedFiles = syncRep2.UnchangedFiles;
                Log($"增量测试 1：未修改文件跳过数: {syncRep2.UnchangedFiles}，实际上传: {syncRep2.UploadedFiles}");

                // 4. 修改单个文件并再次增量
                string modifyTarget = PathHelper.EnsureLongPathPrefix(Path.Combine(TestSourceDir, "01_普通文件", "README_文档.txt"));
                if (File.Exists(modifyTarget))
                {
                    File.AppendAllText(modifyTarget, "\n【测试追加修改内容-触发精确增量】", Encoding.UTF8);
                    Log("已对 01_普通文件/README_文档.txt 追加数据，验证精准单文件增量...");
                    var syncRep3 = await syncEngine.RunSyncAsync(TestSourceDir, MockNasDir);
                    report.IncrementalUploadedFiles = syncRep3.UploadedFiles;
                    Log($"增量测试 2：仅上传变更文件数: {syncRep3.UploadedFiles}，未变更跳过: {syncRep3.UnchangedFiles}");
                }

                // 5. 执行异机完整还原测试
                Log($"【第 3 阶段】正在执行异机原名恢复测试，还原至: {runRestoreDir}");
                var restoreEngine = new RestoreEngine(_manifestManager);
                var restoreRep = await restoreEngine.RunRestoreAsync(MockNasDir, runRestoreDir, ConflictResolution.Overwrite);
                report.RestoredFiles = restoreRep.RestoredFiles;
                Log($"恢复完成：还原文件 {restoreRep.RestoredFiles} 个，还原长文件名 {restoreRep.LongNameRestoredFiles} 个，还原空目录 {restoreRep.RestoredDirectories} 个");

                // 6. 执行 SHA-256 逐字节强比对
                Log("【第 4 阶段】正在执行原文件与还原文件 SHA-256 逐字节完整性校验...");
                int verifiedCount = 0;
                bool allMatch = true;

                var sourceFiles = Directory.GetFiles(TestSourceDir, "*.*", SearchOption.AllDirectories);
                foreach (var sFile in sourceFiles)
                {
                    string relPath = Path.GetRelativePath(TestSourceDir, sFile);
                    string rFile = Path.Combine(runRestoreDir, relPath);

                    var itemRes = new TestFileVerificationResult
                    {
                        RelativePath = relPath.Replace('\\', '/'),
                        Utf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(relPath)),
                        WasMapped = PathHelper.GetUtf8ByteCount(Path.GetFileName(relPath)) > 255
                    };

                    if (!File.Exists(rFile))
                    {
                        itemRes.IsVerified = false;
                        itemRes.StatusMessage = "FAIL (文件未还原)";
                        allMatch = false;
                        Log($"▲ 校验失败：文件未找到: {relPath}");
                    }
                    else
                    {
                        string origSha = HashHelper.ComputeFileSha256(sFile);
                        string restSha = HashHelper.ComputeFileSha256(rFile);
                        itemRes.OriginalSha256 = origSha;
                        itemRes.RestoredSha256 = restSha;

                        if (string.Equals(origSha, restSha, StringComparison.OrdinalIgnoreCase))
                        {
                            itemRes.IsVerified = true;
                            itemRes.StatusMessage = "PASS (哈希完全一致)";
                            verifiedCount++;
                        }
                        else
                        {
                            itemRes.IsVerified = false;
                            itemRes.StatusMessage = "FAIL (哈希不匹配)";
                            allMatch = false;
                            Log($"▲ 哈希不一致: {relPath} (源: {origSha[..8]}, 还原: {restSha[..8]})");
                        }
                    }

                    report.ItemVerifications.Add(itemRes);
                }

                report.Sha256VerifiedFiles = verifiedCount;
                report.IsAllPassed = allMatch && verifiedCount == sourceFiles.Length && syncRep1.FailedFiles == 0;

                Log(report.IsAllPassed
                    ? $"🎉 本地模拟测试全部通过！共校验 {verifiedCount}/{sourceFiles.Length} 个文件，SHA-256 100% 一致！"
                    : $"▲ 测试发现缺陷：通过 {verifiedCount}/{sourceFiles.Length}");
            }
            catch (Exception ex)
            {
                Log($"▲ 测试运行异常中断: {ex.Message}");
                report.IsAllPassed = false;
            }
            finally
            {
                sw.Stop();
                report.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            }

            return report;
        }

        /// <summary>
        /// 运行真实飞牛 NAS SMB 网络端到端测试（在目标共享创建专用隔离测试目录 FN_TEST_<guid>，测试完毕后验证）
        /// </summary>
        public async Task<TestLabReport> RunNasSmbTestAsync(string targetShareRoot, IProgress<string>? progress = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var report = new TestLabReport
            {
                Mode = "RealNasSmb",
                TargetPath = targetShareRoot
            };

            void Log(string msg)
            {
                report.LogEntries.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
                progress?.Report(msg);
            }

            Log($">>> 开始执行真实飞牛 NAS 端到端测试，目标路径: {targetShareRoot}...");

            if (string.IsNullOrWhiteSpace(targetShareRoot) || !Directory.Exists(targetShareRoot))
            {
                Log("▲ 错误：指定的 NAS 共享目标不可访问，请检查 NAS 连接与权限。");
                report.IsAllPassed = false;
                return report;
            }

            string isolatedNasTestDir = Path.Combine(targetShareRoot, $"FN_TEST_{report.RunId}");
            string runRestoreDir = Path.Combine(RestoredBaseDir, $"Run_NAS_{report.RunId}");

            try
            {
                Directory.CreateDirectory(isolatedNasTestDir);
                Directory.CreateDirectory(runRestoreDir);
                Log($"已在 NAS 端建立专用测试沙箱目录: {isolatedNasTestDir}");

                // 1. 确保源样本已生成
                if (!Directory.Exists(TestSourceDir) || Directory.GetFiles(TestSourceDir, "*.*", SearchOption.AllDirectories).Length == 0)
                {
                    Log("检测到源样本尚未生成，自动生成标准测试样本集...");
                    await GenerateTestSamplesAsync(progress);
                }

                var syncEngine = new SyncEngine(_longNameEngine, _manifestManager, _smbService);

                // 2. 执行第 1 次全量同步到真实 NAS
                Log("【第 1 阶段】正在执行初次同步到真实飞牛 NAS 存储...");
                var syncRep1 = await syncEngine.RunSyncAsync(TestSourceDir, isolatedNasTestDir);
                report.TotalSourceFiles = syncRep1.TotalFiles;
                report.InitialUploadedFiles = syncRep1.UploadedFiles;
                report.LongNameFilesMapped = syncRep1.LongNameFiles;
                Log($"NAS 初次同步完成：总扫描 {syncRep1.TotalFiles}，上传 {syncRep1.UploadedFiles}，长文件名映射 {syncRep1.LongNameFiles}，失败 {syncRep1.FailedFiles}");

                // 3. 执行无变更增量同步
                Log("【第 2 阶段】正在执行真实 NAS 增量同步测试...");
                var syncRep2 = await syncEngine.RunSyncAsync(TestSourceDir, isolatedNasTestDir);
                report.IncrementalSkippedFiles = syncRep2.UnchangedFiles;
                Log($"NAS 增量测试 1：未变更跳过数: {syncRep2.UnchangedFiles}，上传数: {syncRep2.UploadedFiles}");

                // 4. 从 NAS 执行还原
                Log($"【第 3 阶段】正在从真实 NAS 下载并执行无损异机恢复，目标: {runRestoreDir}");
                var restoreEngine = new RestoreEngine(_manifestManager);
                var restoreRep = await restoreEngine.RunRestoreAsync(isolatedNasTestDir, runRestoreDir, ConflictResolution.Overwrite);
                report.RestoredFiles = restoreRep.RestoredFiles;
                Log($"NAS 还原完成：还原文件 {restoreRep.RestoredFiles} 个，还原长名 {restoreRep.LongNameRestoredFiles} 个");

                // 5. 逐一执行 SHA-256 强校验
                Log("【第 4 阶段】正在执行 NAS 恢复文件 SHA-256 逐项比对...");
                int verifiedCount = 0;
                bool allMatch = true;

                var sourceFiles = Directory.GetFiles(TestSourceDir, "*.*", SearchOption.AllDirectories);
                foreach (var sFile in sourceFiles)
                {
                    string relPath = Path.GetRelativePath(TestSourceDir, sFile);
                    string rFile = Path.Combine(runRestoreDir, relPath);

                    var itemRes = new TestFileVerificationResult
                    {
                        RelativePath = relPath.Replace('\\', '/'),
                        Utf8Bytes = PathHelper.GetUtf8ByteCount(Path.GetFileName(relPath)),
                        WasMapped = PathHelper.GetUtf8ByteCount(Path.GetFileName(relPath)) > 255
                    };

                    if (!File.Exists(rFile))
                    {
                        itemRes.IsVerified = false;
                        itemRes.StatusMessage = "FAIL (文件未还原)";
                        allMatch = false;
                    }
                    else
                    {
                        string origSha = HashHelper.ComputeFileSha256(sFile);
                        string restSha = HashHelper.ComputeFileSha256(rFile);
                        itemRes.OriginalSha256 = origSha;
                        itemRes.RestoredSha256 = restSha;

                        if (string.Equals(origSha, restSha, StringComparison.OrdinalIgnoreCase))
                        {
                            itemRes.IsVerified = true;
                            itemRes.StatusMessage = "PASS (哈希完全一致)";
                            verifiedCount++;
                        }
                        else
                        {
                            itemRes.IsVerified = false;
                            itemRes.StatusMessage = "FAIL (哈希不匹配)";
                            allMatch = false;
                        }
                    }

                    report.ItemVerifications.Add(itemRes);
                }

                report.Sha256VerifiedFiles = verifiedCount;
                report.IsAllPassed = allMatch && verifiedCount == sourceFiles.Length && syncRep1.FailedFiles == 0;

                Log(report.IsAllPassed
                    ? $"🎉 真实 NAS 端到端测试 100% 成功！共校验 {verifiedCount}/{sourceFiles.Length} 个文件，全部哈希一致！"
                    : $"▲ 真实 NAS 测试未完全通过：校验通过 {verifiedCount}/{sourceFiles.Length}");
            }
            catch (Exception ex)
            {
                Log($"▲ 真实 NAS 测试异常: {ex.Message}");
                report.IsAllPassed = false;
            }
            finally
            {
                // 清理 NAS 上的隔离测试目录
                try
                {
                    if (Directory.Exists(isolatedNasTestDir))
                    {
                        Directory.Delete(isolatedNasTestDir, true);
                        Log($"已安全清理 NAS 端隔离测试目录: {isolatedNasTestDir}");
                    }
                }
                catch { }

                sw.Stop();
                report.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            }

            return report;
        }

        /// <summary>
        /// 导出测试报告为 JSON 格式
        /// </summary>
        public static async Task<string> ExportReportToJsonAsync(TestLabReport report, string destinationFilePath)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(report, options);
            await File.WriteAllTextAsync(destinationFilePath, json, Encoding.UTF8);
            return destinationFilePath;
        }

        /// <summary>
        /// 导出测试报告为 Markdown 格式
        /// </summary>
        public static async Task<string> ExportReportToMarkdownAsync(TestLabReport report, string destinationFilePath)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# 飞牛同步增强版 v0.4.0-beta - 备份测试实验室验证报告");
            sb.AppendLine();
            sb.AppendLine($"- **运行编号**: `{report.RunId}`");
            sb.AppendLine($"- **测试时间**: `{report.Timestamp:yyyy-MM-dd HH:mm:ss}`");
            sb.AppendLine($"- **测试模式**: `{(report.Mode == "LocalSimulation" ? "本地模拟环境" : "真实飞牛 NAS SMB 端到端")}`");
            sb.AppendLine($"- **测试目标路径**: `{report.TargetPath}`");
            sb.AppendLine($"- **耗时**: `{report.ElapsedSeconds:F2} 秒`");
            sb.AppendLine($"- **测试判定**: {(report.IsAllPassed ? "🟢 全部通过 (100% PASS)" : "🔴 未完全通过 (FAIL)")}");
            sb.AppendLine();
            sb.AppendLine("## 核心指标");
            sb.AppendLine();
            sb.AppendLine("| 指标项 | 统计数值 | 说明 |");
            sb.AppendLine("|:---|:---|:---|");
            sb.AppendLine($"| 样本文件总数 | {report.TotalSourceFiles} | 涵盖普通、特殊字符、254/255/256/300 字节边界 |");
            sb.AppendLine($"| 触发长文件名映射数 | {report.LongNameFilesMapped} | 转换为 _FN_LONG_* 前缀短名安全存储 |");
            sb.AppendLine($"| 初次全量上传数 | {report.InitialUploadedFiles} | 首次上传写入 NAS |");
            sb.AppendLine($"| 增量同步跳过数 | {report.IncrementalSkippedFiles} | 无变更文件 100% 跳过上传 |");
            sb.AppendLine($"| 还原文件总数 | {report.RestoredFiles} | 从备份源无损恢复到新目录 |");
            sb.AppendLine($"| SHA-256 强校验通过数 | {report.Sha256VerifiedFiles} / {report.TotalSourceFiles} | 逐字节哈希一致性比对 |");
            sb.AppendLine();
            sb.AppendLine("## 测试文件明细与完整性比对");
            sb.AppendLine();
            sb.AppendLine("| 相对路径 | UTF-8 字节 | 是否长名映射 | 状态 | 源文件 SHA-256 | 还原文件 SHA-256 |");
            sb.AppendLine("|:---|:---:|:---:|:---:|:---|:---|");

            foreach (var item in report.ItemVerifications)
            {
                string origShort = item.OriginalSha256.Length >= 8 ? item.OriginalSha256[..8] : item.OriginalSha256;
                string restShort = item.RestoredSha256.Length >= 8 ? item.RestoredSha256[..8] : item.RestoredSha256;
                sb.AppendLine($"| `{item.RelativePath}` | {item.Utf8Bytes} | {(item.WasMapped ? "✅ 是" : "否")} | {(item.IsVerified ? "🟢 PASS" : "🔴 FAIL")} | `{origShort}` | `{restShort}` |");
            }

            sb.AppendLine();
            sb.AppendLine("## 完整运行日志");
            sb.AppendLine();
            sb.AppendLine("```text");
            foreach (var log in report.LogEntries)
            {
                sb.AppendLine(log);
            }
            sb.AppendLine("```");

            await File.WriteAllTextAsync(destinationFilePath, sb.ToString(), Encoding.UTF8);
            return destinationFilePath;
        }
    }
}
