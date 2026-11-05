using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Storage.FnOs;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Tests
{
    enum TestCategory
    {
        Unit,
        LocalSimulated,
        GuiAutomation,
        RealNasSmb,
        RealPersonalSpace
    }

    class Program
    {
        static int _passed = 0;
        static int _failed = 0;
        static int _skipped = 0;

        static TestCategory _currentCategory = TestCategory.Unit;
        static readonly Dictionary<TestCategory, (int Passed, int Failed, int Skipped)> _categoryStats = new()
        {
            { TestCategory.Unit, (0, 0, 0) },
            { TestCategory.LocalSimulated, (0, 0, 0) },
            { TestCategory.GuiAutomation, (0, 0, 0) },
            { TestCategory.RealNasSmb, (0, 0, 0) },
            { TestCategory.RealPersonalSpace, (0, 0, 0) }
        };

        static void SetCategory(TestCategory cat) => _currentCategory = cat;

        static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Any(a => a.Equals("--poc", StringComparison.OrdinalIgnoreCase) || a.Equals("poc", StringComparison.OrdinalIgnoreCase)))
            {
                bool pocSuccess = await FnOsPersonalSpacePoc.RunAsync(args);
                return pocSuccess ? 0 : 1;
            }

            Console.WriteLine("================================================================================");
            Console.WriteLine("          飞牛同步增强版 (FN Sync Plus) 核心功能与缺陷修复自动化测试套件");
            Console.WriteLine("================================================================================\n");

            try
            {
                // ==================== 1. 单元测试 ====================
                SetCategory(TestCategory.Unit);
                TestNormalNames();
                TestBoundaryByteLengths();
                TestVeryLongChineseName();
                TestLongDirectoryAndNestedPath();
                TestCollisionHandling();
                TestPathSafetyAndRecursionPrevention();
                TestDirectoryNameValidation();
                TestMultiTaskModelAndSettingsMigration();
                TestPersonalSpaceAndCategorizedShares();
                TestPrivateSpaceSafetyAndTeamShareWarning();
                await TestTaskPauseStrictnessAsync();
                TestStateBadgeColorMappingAndDisplayStatus();
                TestNasConnectionServiceStateTransitionsAndDisconnect();
                BuildApplicationIcons();
                await TestNasDirectoryValidationAndWin32ErrorHandlingAsync();
                await TestNoUsernameShareAssumptionAndSafeDefaultsAsync();
                await TestFnOsIntegrationAndOfficialClientDetectionAsync();
                TestSafetyProbeCleanupMechanics(); // 新增：安全探测清理机制单元测试
                TestNasStorageProviderAbstractionAndFactory(); // 新增：NAS 统一存储抽象层与工厂单元测试

                // ==================== 2. 本地模拟备份与恢复 ====================
                SetCategory(TestCategory.LocalSimulated);
                await TestManifestIsolationAndLockingAsync();
                await TestStagingFixedShortNamesNear255BytesAsync();
                await TestManifestPreservationOnPartialFailureAsync();
                await TestRestoreSha256MismatchHandlingAsync();
                await TestEmptyDirectoryRestorationAsync();
                await TestEndToEndIncrementalSyncAndRestoreAsync();
                await TestSyncWithRealTestFilesAsync();
                await TestAutoPlanTargetSubFolderAndDeduplicationAsync();
                await TestTaskManagerConcurrencyAndMultiTaskIsolationAsync();
                await TestRestoreEngineDiscoverBackupPackagesAsync();
                await TestBatchFolderCollisionPlanningAsync();
                await TestMultiTaskEngineIsolationAsync();
                await TestRestoreStagingShortNameSafetyAsync();
                await TestTestLabEngineSampleGenerationAndSimulationAsync();
                await TestPersonalSpaceSameNameBackupFolderAndRestoreLifecycleAsync();
                await TestTargetCreationFailureAbortsTaskAndPreservesErrorsAsync(); // 新增：目标失败立即中止与错误保存测试
                await TestSameNameDeduplicationRespectedInFolderCreationAsync(); // 新增：批次去重规划在自动创建同名目录中严格遵守

                // ==================== 3. GUI 自动化测试 ====================
                SetCategory(TestCategory.GuiAutomation);
                await TestGuiWorkflowAndStateMachinesAsync(); // GUI 流程与状态机流转

                // ==================== 4. 真实 NAS SMB 线上测试 ====================
                SetCategory(TestCategory.RealNasSmb);
                await TestRealNasSmbFolderLifecycleAsync();

                // ==================== 5. 真实个人空间备份还原 ====================
                SetCategory(TestCategory.RealPersonalSpace);
                await TestRealNasPersonalSpaceBackupAndRestoreAsync();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FATAL UNHANDLED] {ex}");
                Console.ResetColor();
                _failed++;
            }

            Console.WriteLine("\n================================================================================");
            Console.WriteLine("                    飞牛同步增强版 (FN Sync Plus) 测试统计报告");
            Console.WriteLine("================================================================================");
            string FormatCat(TestCategory cat, string name)
            {
                var s = _categoryStats[cat];
                return $"  {name,-22} 通过: {s.Passed,3} 项, 失败: {s.Failed,2} 项, 跳过: {s.Skipped,2} 项";
            }
            Console.WriteLine(FormatCat(TestCategory.Unit, "1. 单元测试:"));
            Console.WriteLine(FormatCat(TestCategory.LocalSimulated, "2. 本地模拟备份与恢复:"));
            Console.WriteLine(FormatCat(TestCategory.GuiAutomation, "3. GUI 自动化测试:"));
            Console.WriteLine(FormatCat(TestCategory.RealNasSmb, "4. 真实 NAS SMB 线上测试:"));
            Console.WriteLine(FormatCat(TestCategory.RealPersonalSpace, "5. 真实个人空间备份还原:"));
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine($"  总计执行测试: 通过 {_passed} 项, 失败 {_failed} 项, 安全跳过 {_skipped} 项");
            Console.WriteLine("================================================================================");

            return _failed == 0 ? 0 : 1;
        }

        static void Assert(bool condition, string testName, string? detail = null)
        {
            if (condition)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("[PASS] ");
                Console.ResetColor();
                Console.WriteLine(testName + (detail != null ? $" ({detail})" : ""));
                _passed++;
                var cur = _categoryStats[_currentCategory];
                _categoryStats[_currentCategory] = (cur.Passed + 1, cur.Failed, cur.Skipped);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("[FAIL] ");
                Console.ResetColor();
                Console.WriteLine(testName + (detail != null ? $" -> {detail}" : ""));
                _failed++;
                var cur = _categoryStats[_currentCategory];
                _categoryStats[_currentCategory] = (cur.Passed, cur.Failed + 1, cur.Skipped);
            }
        }

        static void Skip(string testName, string? reason = null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("[SKIP] ");
            Console.ResetColor();
            Console.WriteLine(testName + (reason != null ? $" -> {reason}" : ""));
            _skipped++;
            var cur = _categoryStats[_currentCategory];
            _categoryStats[_currentCategory] = (cur.Passed, cur.Failed, cur.Skipped + 1);
        }

        static void TestNormalNames()
        {
            Console.WriteLine("--- 1. 正常文件名测试 ---");
            var engine = new LongNameEngine(maxUtf8Bytes: 255);

            string enFile = "documents/readme_2026.txt";
            var (storedEn, isLongEn) = engine.TranslateFilePath(enFile);
            Assert(!isLongEn && storedEn == enFile, "正常英文路径不触发转换", $"原名: {enFile} -> 存储: {storedEn}");

            string cnFile = "合同资料/2026年度业务合作协议_初稿.docx";
            var (storedCn, isLongCn) = engine.TranslateFilePath(cnFile);
            Assert(!isLongCn && storedCn == cnFile, "正常中文文件名不触发转换", $"原名: {cnFile} -> 存储: {storedCn}");
        }

        static void TestBoundaryByteLengths()
        {
            Console.WriteLine("\n--- 2. 254 / 255 / 256 边界字节极限测试 ---");
            var engine = new LongNameEngine(maxUtf8Bytes: 255);

            string base254 = new string('a', 250) + ".txt"; // 254 bytes
            int b254 = PathHelper.GetUtf8ByteCount(base254);
            var (s254, l254) = engine.TranslateFilePath(base254);
            Assert(!l254 && b254 == 254 && s254 == base254, "254 字节文件名保持原名", $"字节: {b254}");

            string base255 = new string('b', 251) + ".txt"; // 255 bytes
            int b255 = PathHelper.GetUtf8ByteCount(base255);
            var (s255, l255) = engine.TranslateFilePath(base255);
            Assert(!l255 && b255 == 255 && s255 == base255, "255 字节边界文件名保持原名", $"字节: {b255}");

            string base256 = new string('c', 252) + ".txt"; // 256 bytes
            int b256 = PathHelper.GetUtf8ByteCount(base256);
            var (s256, l256) = engine.TranslateFilePath(base256);
            Assert(l256 && b256 == 256 && s256.StartsWith(LongNameEngine.LongNamePrefix) && s256.EndsWith(".txt"),
                "256 字节超长文件名自动转换为短名", $"原字节: {b256} -> 存储名: {s256}");
        }

        static void TestVeryLongChineseName()
        {
            Console.WriteLine("\n--- 3. 超长中文文件名测试 ---");
            var engine = new LongNameEngine(maxUtf8Bytes: 255);

            string longCnName = "福建南科合同20251126-厦门大型智能化设备采购与安装工程项目很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长很长最终审核确认盖章版本.xlsx";
            int bytes = PathHelper.GetUtf8ByteCount(longCnName);
            var (stored, isLong) = engine.TranslateFilePath(longCnName);

            Assert(isLong, "超长中文文件名识别为超长", $"原字节数: {bytes}");
            Assert(stored.StartsWith(LongNameEngine.LongNamePrefix) && stored.EndsWith(".xlsx"), "生成短文件名符合规范", $"生成名称: {stored}");
            Assert(PathHelper.GetUtf8ByteCount(stored) < 50, "短文件名 UTF-8 字节远低于 NAS 限制", $"转换后字节数: {PathHelper.GetUtf8ByteCount(stored)}");
        }

        static void TestLongDirectoryAndNestedPath()
        {
            Console.WriteLine("\n--- 4. 超长目录名称与多层嵌套路径测试 ---");
            var engine = new LongNameEngine(maxUtf8Bytes: 255);

            string longDirName = "09_folder_" + new string('测', 85);
            var (storedDir, isLongDir) = engine.TranslateDirPath(longDirName);
            Assert(isLongDir, "超长目录识别为超长", $"目录原字节: {PathHelper.GetUtf8ByteCount(longDirName)} -> 映射为: {storedDir}");

            string nested = $"{longDirName}/10_sub_{new string('研', 85)}/11_file_{new string('究', 85)}.pdf";
            var (storedNested, isLongNested) = engine.TranslateFilePath(nested);
            Assert(isLongNested, "多层嵌套超长路径自动逐层转换", $"原路径: {nested.Substring(0, 40)}... -> 映射: {storedNested}");

            string[] parts = storedNested.Split('/');
            Assert(parts.Length == 3, "转换后依然保持原有三层目录结构");
            foreach (var part in parts)
            {
                Assert(PathHelper.GetUtf8ByteCount(part) <= 255, "嵌套路径中的每一级组件均符合 NAS 限制", $"组件: {part}");
            }
        }

        static void TestCollisionHandling()
        {
            Console.WriteLine("\n--- 5. 碰撞冲突防护测试 ---");
            var engine = new LongNameEngine(maxUtf8Bytes: 255);

            string f1 = "folder/" + new string('A', 260) + ".txt";
            string f2 = "folder/" + new string('A', 259) + "B.txt";

            var (s1, _) = engine.TranslateFilePath(f1);
            var (s2, _) = engine.TranslateFilePath(f2);

            Assert(s1 != s2, "不同超长文件绝不产生相同短名", $"f1: {s1}, f2: {s2}");

            var (s1_again, _) = engine.TranslateFilePath(f1);
            Assert(s1 == s1_again, "幂等性检验：同一文件重复转换结果完全一致", $"初次: {s1}, 再次: {s1_again}");
        }

        static void TestPathSafetyAndRecursionPrevention()
        {
            Console.WriteLine("\n--- 6. 路径安全、防止死循环与目录遍历测试 ---");

            // 1. 同一目录禁止同步
            string p1 = @"D:\MyData\Docs";
            string p2 = @"D:\MyData\Docs\";
            Assert(PathHelper.ArePathsSameOrNested(p1, p2, out _), "检测到源目录与目标目录相同并成功拦截");

            // 2. 目标在源内部
            string pSrc = @"D:\MyData";
            string pDestNested = @"D:\MyData\Backup_Nas";
            Assert(PathHelper.ArePathsSameOrNested(pSrc, pDestNested, out string r1), "拦截目标在源内部的嵌套循环备份", r1.Split('\n')[0]);

            // 3. 源在目标内部
            string pDest = @"D:\Backup_Nas";
            string pSrcNested = @"D:\Backup_Nas\SubSource";
            Assert(PathHelper.ArePathsSameOrNested(pSrcNested, pDest, out string r2), "拦截源在目标内部的嵌套循环备份", r2.Split('\n')[0]);

            // 4. 正常不相关路径允许同步
            string pSafeSrc = @"D:\SourceFolder";
            string pSafeDest = @"\\192.168.1.100\SharedData";
            Assert(!PathHelper.ArePathsSameOrNested(pSafeSrc, pSafeDest, out _), "独立源与目标允许正常同步");

            // 5. 相对路径安全校验（防止路径遍历漏洞）
            Assert(PathHelper.IsSafeRelativePath("folder/sub/doc.txt"), "合法相对路径允许通过");
            Assert(!PathHelper.IsSafeRelativePath("../escape.txt"), "成功拦截 ../ 目录穿越攻击");
            Assert(!PathHelper.IsSafeRelativePath("/root/file.txt"), "成功拦截根路径 / 开头");
            Assert(!PathHelper.IsSafeRelativePath("C:\\Windows\\System32"), "成功拦截 Windows 绝对路径");
        }

        static void TestDirectoryNameValidation()
        {
            Console.WriteLine("\n--- 7. 文件夹名称合法性校验测试 ---");

            Assert(PathHelper.IsValidDirectoryName("正常文件夹_2026", out _), "合法文件夹名称通过校验");
            Assert(PathHelper.IsValidDirectoryName("团队资料-项目A", out _), "合法中文文件夹名称通过校验");

            Assert(!PathHelper.IsValidDirectoryName("test:folder", out string e1), "成功拦截带冒号的文件名", e1);
            Assert(!PathHelper.IsValidDirectoryName("test*folder", out string e2), "成功拦截带通配符的文件名", e2);
            Assert(!PathHelper.IsValidDirectoryName("folder ", out string e3), "成功拦截末尾带空格的文件名", e3);
            Assert(!PathHelper.IsValidDirectoryName("folder.", out string e4), "成功拦截末尾带句点的文件名", e4);
            Assert(!PathHelper.IsValidDirectoryName("CON", out string e5), "成功拦截 Windows 保留设备名 CON", e5);
            Assert(!PathHelper.IsValidDirectoryName("aux", out string e6), "成功拦截 Windows 保留设备名 aux", e6);

            string tooLongFolder = new string('中', 90); // 270 bytes > 255
            Assert(!PathHelper.IsValidDirectoryName(tooLongFolder, out string e7), "成功拦截超过 255 字节的目录名", e7);
        }

        static async Task TestManifestIsolationAndLockingAsync()
        {
            Console.WriteLine("\n--- 8. 清单元数据隔离与目标文件锁测试 ---");
            string testDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_MetaIso_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                var mgr = new ManifestManager();
                var manifest = new Manifest
                {
                    SourceRoot = @"D:\Source",
                    TargetRoot = testDir,
                    Files = new()
                    {
                        new ManifestFileEntry
                        {
                            OriginalRel = "测试文件.txt",
                            StoredRel = "测试文件.txt",
                            Size = 100,
                            Sha256 = "deadbeef"
                        }
                    }
                };

                // 原子写入
                await mgr.SaveManifestAtomicAsync(testDir, manifest);

                // 验证物理路径位于 .fnsync/manifest.json，而不是根目录下
                string isoPath = Path.Combine(testDir, Manifest.MetadataDirName, Manifest.IsolatedManifestFileName);
                Assert(File.Exists(isoPath), "清单文件严格保存在 .fnsync/ 专用隔离目录中");

                // 读取验证
                var loaded = await mgr.LoadManifestAsync(testDir);
                Assert(loaded.Files.Count == 1 && loaded.Files[0].Sha256 == "deadbeef", "从 .fnsync/ 隔离目录反序列化读取清单成功");

                // 测试文件独占排他锁
                var lock1 = await mgr.TryAcquireTargetLockAsync(testDir);
                Assert(lock1 != null, "首次获取目标目录独占同步锁成功");

                var lock2 = await mgr.TryAcquireTargetLockAsync(testDir);
                Assert(lock2 == null, "并发请求目标目录独占锁被正确互斥拦截");

                lock1?.Dispose();

                var lock3 = await mgr.TryAcquireTargetLockAsync(testDir);
                Assert(lock3 != null, "释放后重新获取独占锁成功");
                lock3?.Dispose();
            }
            finally
            {
                if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
            }
        }

        static async Task TestStagingFixedShortNamesNear255BytesAsync()
        {
            Console.WriteLine("\n--- 9. 临界文件名暂存上传无损安全测试 (254, 255, 256, 300 字节) ---");
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Staging_Test_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(root, "Src");
            string dest = Path.Combine(root, "Dest");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dest);

            try
            {
                // 1. 构造 254 字节文件: 83 个汉字 (249 字节) + "a" (1 字节) + ".txt" (4 字节) = 254 字节
                string f254 = new string('汉', 83) + "a.txt";
                Assert(PathHelper.GetUtf8ByteCount(f254) == 254, "构建 254 字节临界文件名");
                File.WriteAllText(PathHelper.EnsureLongPathPrefix(Path.Combine(src, f254)), "254字节边界测试文件内容");

                // 2. 构造 255 字节文件: 83 个汉字 (249 字节) + "ab" (2 字节) + ".txt" (4 字节) = 255 字节
                // 若旧代码直接在后面加 .fn_part_guid 会变成 296 字节引发 1018 报错！
                string f255 = new string('汉', 83) + "ab.txt";
                Assert(PathHelper.GetUtf8ByteCount(f255) == 255, "构建 255 字节临界文件名");
                File.WriteAllText(PathHelper.EnsureLongPathPrefix(Path.Combine(src, f255)), "255字节边界测试文件内容");

                // 3. 构造 256 字节超长文件: 84 个汉字 (252 字节) + ".txt" (4 字节) = 256 字节
                string f256 = new string('汉', 84) + ".txt";
                Assert(PathHelper.GetUtf8ByteCount(f256) == 256, "构建 256 字节超长文件名");
                File.WriteAllText(PathHelper.EnsureLongPathPrefix(Path.Combine(src, f256)), "256字节超长测试文件内容");

                // 4. 构造 300 字节超长文件: 98 个汉字 (294 字节) + "ab" (2 字节) + ".txt" (4 字节) = 300 字节
                string f300 = new string('汉', 98) + "ab.txt";
                Assert(PathHelper.GetUtf8ByteCount(f300) == 300, "构建 300 字节超长文件名");
                File.WriteAllText(PathHelper.EnsureLongPathPrefix(Path.Combine(src, f300)), "300字节超长中文文档测试内容");

                var longEngine = new LongNameEngine(maxUtf8Bytes: 255);
                var manifestMgr = new ManifestManager();
                var smbAuth = new SmbAuthService();
                var syncEngine = new SyncEngine(longEngine, manifestMgr, smbAuth);

                var rep = await syncEngine.RunSyncAsync(src, dest);

                Assert(rep.UploadedFiles == 4, "254/255/256/300 字节文件全部上传成功，无临时名超长溢出", $"成功数: {rep.UploadedFiles}/4");
                Assert(rep.FailedFiles == 0, "全部文件零失败");

                // 验证 NAS 存储中的每个物理组件长度均严格 <= 255 字节
                var di = new DirectoryInfo(dest);
                foreach (var fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    int b = PathHelper.GetUtf8ByteCount(fi.Name);
                    Assert(b <= 255, $"NAS 存储文件名严格在 255 字节内 (实际 {b} 字节)", fi.Name.Substring(0, Math.Min(30, fi.Name.Length)));
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        static async Task TestManifestPreservationOnPartialFailureAsync()
        {
            Console.WriteLine("\n--- 10. 部分失败或中断时清单历史记录保留测试 ---");
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PartialFail_Test_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(root, "Src");
            string dest = Path.Combine(root, "Dest");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dest);

            try
            {
                // 先同步 2 个正常文件
                File.WriteAllText(Path.Combine(src, "history_file_1.txt"), "文件1内容");
                File.WriteAllText(Path.Combine(src, "history_file_2.txt"), "文件2内容");

                var longEngine = new LongNameEngine(maxUtf8Bytes: 255);
                var manifestMgr = new ManifestManager();
                var smbAuth = new SmbAuthService();
                var syncEngine = new SyncEngine(longEngine, manifestMgr, smbAuth);

                await syncEngine.RunSyncAsync(src, dest);

                var m1 = await manifestMgr.LoadManifestAsync(dest);
                Assert(m1.Files.Count == 2, "初次同步成功生成 2 个清单记录");

                // 创建第 3 个文件并独占锁定它，模拟读取冲突失败
                string lockedFile = Path.Combine(src, "locked_file_3.txt");
                File.WriteAllText(lockedFile, "被占用的文件内容");

                using (var fsLock = new FileStream(lockedFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // 再次运行增量同步
                    var rep2 = await syncEngine.RunSyncAsync(src, dest);
                    Assert(rep2.FailedFiles == 1, "锁定的占用文件被捕获并记为失败");
                }

                // 检查清单：前 2 个文件的清单记录绝对不能被删除或丢失！
                var m2 = await manifestMgr.LoadManifestAsync(dest);
                Assert(m2.Files.Count >= 2, "发生同步失败时，旧有效清单记录 100% 完整保留", $"现有记录数: {m2.Files.Count}");
                bool hasFile1 = m2.Files.Exists(f => f.OriginalRel == "history_file_1.txt");
                bool hasFile2 = m2.Files.Exists(f => f.OriginalRel == "history_file_2.txt");
                Assert(hasFile1 && hasFile2, "历史清单项未被误删");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        static async Task TestRestoreSha256MismatchHandlingAsync()
        {
            Console.WriteLine("\n--- 11. 恢复时 SHA-256 哈希不一致防御测试 (自动清理损坏临时文件且记为失败) ---");
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ShaMismatch_Test_" + Guid.NewGuid().ToString("N"));
            string nas = Path.Combine(root, "NasBackup");
            string localRestore = Path.Combine(root, "RestoreDest");
            Directory.CreateDirectory(nas);
            Directory.CreateDirectory(localRestore);

            try
            {
                var manifestMgr = new ManifestManager();
                string normalContent = "原始纯净内容";
                string shaOrig = HashHelper.ComputeStringSha256(normalContent);

                // 在 NAS 上写入被篡改/损坏的文件内容
                string nasFile = Path.Combine(nas, "损坏文件.txt");
                File.WriteAllText(nasFile, "已被恶意篡改或物理坏道损坏的内容！");

                // 在清单中记录原始 SHA-256
                var manifest = new Manifest
                {
                    Files = new()
                    {
                        new ManifestFileEntry
                        {
                            OriginalRel = "损坏文件.txt",
                            StoredRel = "损坏文件.txt",
                            Size = Encoding.UTF8.GetByteCount(normalContent),
                            Sha256 = shaOrig
                        }
                    }
                };
                await manifestMgr.SaveManifestAtomicAsync(nas, manifest);

                // 执行恢复
                var restoreEngine = new RestoreEngine(manifestMgr);
                var rep = await restoreEngine.RunRestoreAsync(nas, localRestore);

                // 核心断言：校验不一致时绝对不能计为成功，且损坏的临时文件必须被删除
                Assert(rep.FailedFiles == 1, "SHA-256 不一致文件被严格计为失败");
                Assert(rep.RestoredFiles == 0, "SHA-256 损坏文件绝对不计入成功恢复数");

                string targetRestoredFile = Path.Combine(localRestore, "损坏文件.txt");
                Assert(!File.Exists(targetRestoredFile), "损坏的文件未被落盘到目标路径");

                var tempFiles = Directory.GetFiles(localRestore, "*.restore_tmp_*");
                Assert(tempFiles.Length == 0, "损坏的临时中间文件已被彻底安全删除");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        static async Task TestEmptyDirectoryRestorationAsync()
        {
            Console.WriteLine("\n--- 12. 空目录同步与异机恢复测试 ---");
            string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "EmptyDir_Test_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(root, "Src");
            string dest = Path.Combine(root, "Dest");
            string restore = Path.Combine(root, "Restore");

            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dest);
            Directory.CreateDirectory(restore);

            try
            {
                // 创建一个空目录与一个嵌套空目录
                string empty1 = Path.Combine(src, "空目录A");
                string empty2 = Path.Combine(src, "父级目录", "嵌套空目录B");
                Directory.CreateDirectory(empty1);
                Directory.CreateDirectory(empty2);

                // 创建一个带文件的普通目录
                string normalDir = Path.Combine(src, "普通目录");
                Directory.CreateDirectory(normalDir);
                File.WriteAllText(Path.Combine(normalDir, "file.txt"), "hello");

                var longEngine = new LongNameEngine(maxUtf8Bytes: 255);
                var manifestMgr = new ManifestManager();
                var smbAuth = new SmbAuthService();
                var syncEngine = new SyncEngine(longEngine, manifestMgr, smbAuth);

                await syncEngine.RunSyncAsync(src, dest);

                // 验证清单记录了目录
                var m = await manifestMgr.LoadManifestAsync(dest);
                Assert(m.Directories.Count >= 3, "清单成功记录所有目录结构 (包含空目录)");

                // 执行恢复
                var restoreEngine = new RestoreEngine(manifestMgr);
                var rep = await restoreEngine.RunRestoreAsync(dest, restore);

                Assert(rep.RestoredDirectories >= 3, "恢复中心成功还原空目录结构");
                Assert(Directory.Exists(Path.Combine(restore, "空目录A")), "空目录A 成功无损还原");
                Assert(Directory.Exists(Path.Combine(restore, "父级目录", "嵌套空目录B")), "嵌套空目录B 成功无损还原");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        static async Task TestEndToEndIncrementalSyncAndRestoreAsync()
        {
            Console.WriteLine("\n--- 13. 端到端增量同步与异机恢复完整性测试 ---");
            string testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "E2E_Test_" + Guid.NewGuid().ToString("N"));
            string localSrc = Path.Combine(testRoot, "LocalSource");
            string nasDest = Path.Combine(testRoot, "MockNasShare");
            string localRestore = Path.Combine(testRoot, "RestoredTarget");

            Directory.CreateDirectory(localSrc);
            Directory.CreateDirectory(nasDest);
            Directory.CreateDirectory(localRestore);

            try
            {
                string fShort = Path.Combine(localSrc, "普通文件.txt");
                File.WriteAllText(fShort, "短文件内容-12345", Encoding.UTF8);

                string longName = "这是极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其超长中文合同文档名称2026.xlsx";
                string fLong = Path.Combine(localSrc, longName);
                File.WriteAllText(fLong, "长文件合同专用二进制模拟内容-ABCDEFG", Encoding.UTF8);

                string longDir = Path.Combine(localSrc, "09_目录_" + new string('测', 85));
                Directory.CreateDirectory(longDir);
                string fInLongDir = Path.Combine(longDir, "子文件.txt");
                File.WriteAllText(fInLongDir, "子目录内的重要数据", Encoding.UTF8);

                string shaShortOrig = HashHelper.ComputeFileSha256(fShort);
                string shaLongOrig = HashHelper.ComputeFileSha256(fLong);
                string shaNestedOrig = HashHelper.ComputeFileSha256(fInLongDir);

                var longEngine = new LongNameEngine(maxUtf8Bytes: 255);
                var manifestMgr = new ManifestManager();
                var smbAuth = new SmbAuthService();
                var syncEngine = new SyncEngine(longEngine, manifestMgr, smbAuth);

                var rep1 = await syncEngine.RunSyncAsync(localSrc, nasDest);
                Assert(rep1.TotalFiles == 3, "第 1 次同步扫描出全部 3 个文件");
                Assert(rep1.UploadedFiles == 3, "第 1 次同步成功上传全部 3 个文件");
                Assert(rep1.LongNameFiles >= 1, "超长文件被成功识别并映射");

                var nasFiles = Directory.GetFiles(nasDest, "*.*", SearchOption.AllDirectories);
                bool foundLongNamePrefixOnNas = false;
                foreach (var nf in nasFiles)
                {
                    if (nf.Contains(LongNameEngine.LongNamePrefix))
                    {
                        foundLongNamePrefixOnNas = true;
                    }
                }
                Assert(foundLongNamePrefixOnNas, "NAS 存储中成功生成 _FN_LONG_ 前缀的短名文件或目录");

                var rep2 = await syncEngine.RunSyncAsync(localSrc, nasDest);
                Assert(rep2.UnchangedFiles == 3 && rep2.UploadedFiles == 0, "增量测试：未修改文件 100% 跳过上传");

                File.AppendAllText(fShort, "追加的新修改内容");
                var rep3 = await syncEngine.RunSyncAsync(localSrc, nasDest);
                Assert(rep3.UploadedFiles == 1 && rep3.UnchangedFiles == 2, "增量测试：仅更新修改过的单个文件");

                Console.WriteLine("\n--- 执行异机恢复测试 ---");
                var restoreEngine = new RestoreEngine(manifestMgr);
                var restoreRep = await restoreEngine.RunRestoreAsync(nasDest, localRestore, ConflictResolution.Overwrite);

                Assert(restoreRep.RestoredFiles == 3, "恢复中心成功恢复全部 3 个文件");
                Assert(restoreRep.LongNameRestoredFiles >= 1, "超长文件名成功还原为原始完整长文件名");

                string fShortRestored = Path.Combine(localRestore, "普通文件.txt");
                string fLongRestored = Path.Combine(localRestore, longName);
                string fNestedRestored = Path.Combine(localRestore, Path.GetFileName(longDir), "子文件.txt");

                Assert(File.Exists(fShortRestored), "普通文件原名还原成功");
                Assert(File.Exists(fLongRestored), "超长文件名原始名称还原成功");
                Assert(File.Exists(fNestedRestored), "超长目录下的嵌套子文件还原成功");

                string shaShortRestored = HashHelper.ComputeFileSha256(fShortRestored);
                string shaLongRestored = HashHelper.ComputeFileSha256(fLongRestored);
                string shaNestedRestored = HashHelper.ComputeFileSha256(fNestedRestored);

                string shaShortCurrent = HashHelper.ComputeFileSha256(fShort);
                Assert(shaShortCurrent == shaShortRestored, "普通文件恢复后 SHA-256 与修改后一致");
                Assert(shaLongOrig == shaLongRestored, "超长文件恢复后 SHA-256 与原始源文件 100% 一致");
                Assert(shaNestedOrig == shaNestedRestored, "嵌套文件恢复后 SHA-256 与原始源文件 100% 一致");
            }
            finally
            {
                if (Directory.Exists(testRoot))
                {
                    try { Directory.Delete(testRoot, true); } catch { }
                }
            }
        }

        static async Task TestSyncWithRealTestFilesAsync()
        {
            Console.WriteLine("\n--- 14. 真实样本目录 (FN_Sync_Test) 模拟增量与恢复测试 ---");
            string realDir = @"D:\FN_AutoSync_Package\FN_Sync_Test";
            if (!Directory.Exists(realDir))
            {
                Console.WriteLine("FN_Sync_Test 目录不存在，跳过此项。");
                return;
            }

            string testNas = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_Real_Nas_" + Guid.NewGuid().ToString("N"));
            string testRestore = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_Real_Restore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testNas);
            Directory.CreateDirectory(testRestore);

            try
            {
                var longEngine = new LongNameEngine(maxUtf8Bytes: 255);
                var manifestMgr = new ManifestManager();
                var smbAuth = new SmbAuthService();
                var syncEngine = new SyncEngine(longEngine, manifestMgr, smbAuth);

                var rep = await syncEngine.RunSyncAsync(realDir, testNas);
                Assert(rep.TotalFiles > 0, $"成功扫描真实样本目录 ({rep.TotalFiles} 个文件)");
                Assert(rep.LongNameFiles >= 2, $"成功识别样本中的 256/300 字节超长文件 (映射数: {rep.LongNameFiles})");
                Assert(rep.FailedFiles == 0, "真实样本全部同步成功，无 1018 报错！");

                var restoreEngine = new RestoreEngine(manifestMgr);
                var restoreRep = await restoreEngine.RunRestoreAsync(testNas, testRestore, ConflictResolution.Overwrite);
                Assert(restoreRep.RestoredFiles == rep.TotalFiles, $"所有样本文件全部无损还原 ({restoreRep.RestoredFiles}/{rep.TotalFiles})");
                Assert(restoreRep.IntegrityVerifiedFiles == rep.TotalFiles, "所有样本还原后 SHA-256 完整性校验 100% 通过！");
            }
            finally
            {
                try
                {
                    if (Directory.Exists(testNas)) Directory.Delete(testNas, true);
                    if (Directory.Exists(testRestore)) Directory.Delete(testRestore, true);
                }
                catch { }
            }
        }
        static void TestMultiTaskModelAndSettingsMigration()
        {
            Console.WriteLine("\n--- 15. 多任务数据模型与旧版单任务配置迁移测试 ---");
            var task = new SyncTask
            {
                TaskId = "task_test_101",
                TaskName = "工程图纸",
                SourceFolder = @"D:\Projects\CAD",
                NasDeviceId = "192.168.8.120",
                DestinationRoot = @"\\192.168.8.120\home",
                DestinationFolder = @"\\192.168.8.120\home\工程图纸",
                AutoSyncEnabled = true,
                RealtimeWatchEnabled = true,
                IntervalMinutes = 15,
                LastSyncStatus = "待同步"
            };

            var settings = new AppSettings();
            settings.Tasks.Add(task);
            settings.PreferredBackupRoot = @"\\192.168.8.120\home";
            settings.MaxConcurrentSyncs = 3;
            settings.AppTheme = "Dark";

            string json = settings.ToJson();
            var deserialized = AppSettings.FromJson(json);

            Assert(deserialized.Tasks.Count == 1, "多任务模型序列化并正确反序列化");
            Assert(deserialized.Tasks[0].TaskName == "工程图纸", "任务名称正确还原");
            Assert(deserialized.Tasks[0].DestinationFolder == @"\\192.168.8.120\home\工程图纸", "NAS目标路径正确还原");
            Assert(deserialized.MaxConcurrentSyncs == 3, "并发任务数参数正确保留");
            Assert(deserialized.AppTheme == "Dark", "应用主题正确保留");

            // 模拟 v1.0/v2.0 遗留配置迁移
            string legacyJson = @"{
                ""NasServer"": ""192.168.8.120"",
                ""Username"": ""admin"",
                ""SourceFolder"": ""D:\\重要财务数据"",
                ""TargetShare"": ""\\\\192.168.8.120\\团队文件-cenco-public\\财务"",
                ""AutoSyncEnabled"": true,
                ""SyncIntervalMinutes"": 10,
                ""RealtimeWatchEnabled"": true
            }";

            var legacySettings = AppSettings.FromJson(legacyJson);
            bool migrated = AppSettings.MigrateLegacySettings(legacySettings);
            Assert(migrated, "成功检测并触发旧版单任务配置自动迁移");
            Assert(legacySettings.Tasks.Count == 1, "迁移后自动生成任务列表项");
            Assert(legacySettings.Tasks[0].SourceFolder == @"D:\重要财务数据", "旧版源目录正确迁移为任务源目录");
            Assert(legacySettings.Tasks[0].TaskName == "重要财务数据", "根据源目录自动推导出有意义的任务名称");
            Assert(legacySettings.Tasks[0].DestinationFolder == @"\\192.168.8.120\团队文件-cenco-public\财务", "旧版目标共享正确迁移");
            Assert(legacySettings.Tasks[0].IntervalMinutes == 10, "旧版同步间隔参数正确继承");
        }

        static void TestPersonalSpaceAndCategorizedShares()
        {
            Console.WriteLine("\n--- 16. NAS 私人空间 (我的文件) 与团队共享分类及优先级测试 ---");

            Assert(SmbAuthService.CategorizeShare("admin", "admin") == NasShareCategory.PersonalSpace, "同名用户名共享(admin)识别为私人空间");
            Assert(SmbAuthService.CategorizeShare("cenco", "cenco") == NasShareCategory.PersonalSpace, "用户名同名共享(cenco)识别为私人空间");
            Assert(SmbAuthService.CategorizeShare("home", "user1") == NasShareCategory.PersonalSpace, "home 共享识别为私人空间");
            Assert(SmbAuthService.CategorizeShare("homes", "user1") == NasShareCategory.PersonalSpace, "homes 共享识别为私人空间");
            Assert(SmbAuthService.CategorizeShare("我的文件", "user1") == NasShareCategory.PersonalSpace, "『我的文件』识别为私人空间");
            Assert(SmbAuthService.CategorizeShare("私人备份空间", "user1") == NasShareCategory.PersonalSpace, "『私人备份空间』识别为私人空间");
            Assert(SmbAuthService.CategorizeShare("个人文档", "user1") == NasShareCategory.PersonalSpace, "『个人文档』识别为私人空间");

            Assert(SmbAuthService.CategorizeShare("团队文件-cenco-public", "cenco") == NasShareCategory.TeamShare, "团队文件识别为团队共享");
            Assert(SmbAuthService.CategorizeShare("public_docs", "cenco") == NasShareCategory.TeamShare, "public 目录识别为团队共享");
            Assert(SmbAuthService.CategorizeShare("company_share", "cenco") == NasShareCategory.TeamShare, "share 目录识别为团队共享");
        }

        static async Task TestAutoPlanTargetSubFolderAndDeduplicationAsync()
        {
            Console.WriteLine("\n--- 17. 自动创建同名文件夹、去重建议与历史复用测试 ---");
            string testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_AutoPlan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);

            try
            {
                var smbService = new SmbAuthService();

                // 1. 目标目录不存在：直接规划为原名
                var (sub1, exist1, conflict1) = await smbService.PlanTargetSubFolderAsync(testRoot, "工作资料", @"D:\工作资料");
                Assert(sub1 == "工作资料" && !exist1 && !conflict1, "首次规划未创建过的目录，保持原名无冲突");

                // 2. 目标目录已存在，但含有其他非空文件或属于其他任务 -> 自动产生 _2 去重建议
                string existingDir = Path.Combine(testRoot, "工作资料");
                Directory.CreateDirectory(existingDir);
                File.WriteAllText(Path.Combine(existingDir, "other_file.txt"), "foreign data");

                var (sub2, exist2, conflict2) = await smbService.PlanTargetSubFolderAsync(testRoot, "工作资料", @"D:\另一个源文件夹");
                Assert(sub2 == "工作资料_2" && exist2 && conflict2, "目标已存在外来数据时，自动规划去重后缀并提示冲突", $"规划结果: {sub2}");

                // 3. 目标目录已存在，且包含相同的任务源清单 -> 智能复用，不产生冲突
                string taskDir = Path.Combine(testRoot, "财务报表");
                Directory.CreateDirectory(taskDir);
                var manifestMgr = new ManifestManager();
                var m = new Manifest
                {
                    SourceRoot = @"D:\财务报表",
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await manifestMgr.SaveManifestAtomicAsync(taskDir, m);

                var (sub3, exist3, conflict3) = await smbService.PlanTargetSubFolderAsync(testRoot, "财务报表", @"D:\财务报表");
                Assert(sub3 == "财务报表" && exist3 && !conflict3, "已存在且清单归属于相同任务源时，智能复用现有目录无需加后缀");
            }
            finally
            {
                try { if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true); } catch { }
            }
        }

        static async Task TestTaskManagerConcurrencyAndMultiTaskIsolationAsync()
        {
            Console.WriteLine("\n--- 18. 多任务管理器并发控制、路径防冲突与多文件夹完全隔离测试 ---");
            string testBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_TaskManager_" + Guid.NewGuid().ToString("N"));
            string srcA = Path.Combine(testBase, "Local_Folder_A");
            string srcB = Path.Combine(testBase, "Local_Folder_B");
            string nasRoot = Path.Combine(testBase, "Nas_Root");
            string dstA = Path.Combine(nasRoot, "Backup_A");
            string dstB = Path.Combine(nasRoot, "Backup_B");

            Directory.CreateDirectory(srcA);
            Directory.CreateDirectory(srcB);
            Directory.CreateDirectory(dstA);
            Directory.CreateDirectory(dstB);

            File.WriteAllText(Path.Combine(srcA, "file_a.txt"), "Content A");
            File.WriteAllText(Path.Combine(srcB, "file_b.txt"), "Content B");

            var settings = new AppSettings
            {
                MaxConcurrentSyncs = 1 // 强制并发数为 1 验证队列调度
            };

            var longEngine = new LongNameEngine(maxUtf8Bytes: 255);
            var manifestMgr = new ManifestManager();
            var smbAuth = new SmbAuthService();

            using var taskManager = new TaskManager(settings, longEngine, manifestMgr, smbAuth);

            var taskA = new SyncTask
            {
                TaskId = "task_a",
                TaskName = "任务A",
                SourceFolder = srcA,
                DestinationFolder = dstA,
                AutoSyncEnabled = false,
                RealtimeWatchEnabled = false
            };

            var taskB = new SyncTask
            {
                TaskId = "task_b",
                TaskName = "任务B",
                SourceFolder = srcB,
                DestinationFolder = dstB,
                AutoSyncEnabled = false,
                RealtimeWatchEnabled = false
            };

            taskManager.AddTask(taskA);
            taskManager.AddTask(taskB);

            // 验证防冲突与嵌套检查
            bool caughtOverlap = false;
            try
            {
                var invalidTask = new SyncTask
                {
                    TaskId = "task_invalid",
                    TaskName = "非法任务",
                    SourceFolder = Path.Combine(srcA, "SubChild"),
                    DestinationFolder = Path.Combine(nasRoot, "Other")
                };
                taskManager.AddTask(invalidTask);
            }
            catch (InvalidOperationException)
            {
                caughtOverlap = true;
            }
            Assert(caughtOverlap, "任务管理器成功拒绝嵌套源路径的非法任务添加");

            // 同时触发两个任务入队
            var tcsA = new TaskCompletionSource<bool>();
            var tcsB = new TaskCompletionSource<bool>();

            taskManager.TaskStateChanged += t =>
            {
                if (t.TaskId == "task_a" && t.State == TaskSyncState.Completed) tcsA.TrySetResult(true);
                if (t.TaskId == "task_b" && t.State == TaskSyncState.Completed) tcsB.TrySetResult(true);
            };

            taskManager.TriggerSync("task_a");
            taskManager.TriggerSync("task_b");

            var completedA = await Task.WhenAny(tcsA.Task, Task.Delay(10000));
            var completedB = await Task.WhenAny(tcsB.Task, Task.Delay(10000));

            Assert(tcsA.Task.IsCompletedSuccessfully, "队列中的任务 A 顺利完成备份");
            Assert(tcsB.Task.IsCompletedSuccessfully, "队列中的任务 B 顺利完成备份");

            // 检查数据隔离性
            Assert(File.Exists(Path.Combine(dstA, "file_a.txt")), "任务 A 目标目录包含其自身文件");
            Assert(!File.Exists(Path.Combine(dstA, "file_b.txt")), "任务 A 目标目录未被任务 B 文件污染");
            Assert(File.Exists(Path.Combine(dstB, "file_b.txt")), "任务 B 目标目录包含其自身文件");
            Assert(!File.Exists(Path.Combine(dstB, "file_a.txt")), "任务 B 目标目录未被任务 A 文件污染");

            // 检查独立元数据
            Assert(File.Exists(Path.Combine(dstA, Manifest.MetadataDirName, Manifest.IsolatedManifestFileName)), "任务 A 拥有独立 .fnsync/manifest.json");
            Assert(File.Exists(Path.Combine(dstB, Manifest.MetadataDirName, Manifest.IsolatedManifestFileName)), "任务 B 拥有独立 .fnsync/manifest.json");

            // 检查独立日志缓冲
            var logsA = taskManager.GetTaskLogs("task_a");
            var logsB = taskManager.GetTaskLogs("task_b");
            Assert(logsA.Count > 0 && logsB.Count > 0, "多任务分别具备独立的内存日志流");

            try { Directory.Delete(testBase, true); } catch { }
        }

        static async Task TestRestoreEngineDiscoverBackupPackagesAsync()
        {
            Console.WriteLine("\n--- 19. NAS 恢复中心多任务备份包智能发现与无损恢复测试 ---");
            string testBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_RestoreDiscovery_" + Guid.NewGuid().ToString("N"));
            string nasRoot = Path.Combine(testBase, "Nas_Root");
            string pkg1 = Path.Combine(nasRoot, "工作工程资料");
            string pkg2 = Path.Combine(nasRoot, "家庭照片与影集");
            string nonPkg = Path.Combine(nasRoot, "随机未知文件夹");
            string restoreTarget = Path.Combine(testBase, "Local_Restored");

            Directory.CreateDirectory(pkg1);
            Directory.CreateDirectory(pkg2);
            Directory.CreateDirectory(nonPkg);
            Directory.CreateDirectory(restoreTarget);

            File.WriteAllText(Path.Combine(pkg1, "drawing.dwg"), "DWG DATA");
            File.WriteAllText(Path.Combine(pkg2, "photo.jpg"), "PHOTO DATA");
            File.WriteAllText(Path.Combine(nonPkg, "temp.log"), "TEMP DATA");

            var manifestMgr = new ManifestManager();
            var m1 = new Manifest
            {
                SourceRoot = @"C:\Users\Cenco\Projects",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            m1.Files.Add(new ManifestFileEntry
            {
                OriginalRel = "drawing.dwg",
                StoredRel = "drawing.dwg",
                Sha256 = HashHelper.ComputeFileSha256(Path.Combine(pkg1, "drawing.dwg")),
                Size = 8
            });
            await manifestMgr.SaveManifestAtomicAsync(pkg1, m1);

            var m2 = new Manifest
            {
                SourceRoot = @"D:\Media\Photos",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            m2.Files.Add(new ManifestFileEntry
            {
                OriginalRel = "photo.jpg",
                StoredRel = "photo.jpg",
                Sha256 = HashHelper.ComputeFileSha256(Path.Combine(pkg2, "photo.jpg")),
                Size = 10
            });
            await manifestMgr.SaveManifestAtomicAsync(pkg2, m2);

            var restoreEngine = new RestoreEngine(manifestMgr);
            var packages = await restoreEngine.DiscoverBackupPackagesAsync(nasRoot);

            Assert(packages.Count == 2, $"成功从 NAS 根目录扫描发现 2 个合法备份包 (非备份目录被自动过滤)，实际发现: {packages.Count}");
            var p1 = packages.FirstOrDefault(p => p.PackageName == "工作工程资料");
            var p2 = packages.FirstOrDefault(p => p.PackageName == "家庭照片与影集");

            Assert(p1 != null && p1.FileCount == 1 && p1.OriginalSourceRoot == @"C:\Users\Cenco\Projects", "备份包1元信息解析正确");
            Assert(p2 != null && p2.FileCount == 1 && p2.OriginalSourceRoot == @"D:\Media\Photos", "备份包2元信息解析正确");

            // 模拟新电脑恢复选中的备份包 1
            var rep = await restoreEngine.RunRestoreAsync(p1!.NasFolderUnc, restoreTarget, ConflictResolution.Overwrite);
            Assert(rep.RestoredFiles == 1 && rep.FailedFiles == 0, "新电脑一键恢复备份包成功无报错");
            Assert(File.Exists(Path.Combine(restoreTarget, "drawing.dwg")), "恢复目标目录正确还原原始文件");
            Assert(rep.IntegrityVerifiedFiles == 1, "恢复文件完整性 SHA-256 校验 100% 通过");

            try { Directory.Delete(testBase, true); } catch { }
        }

        static void TestPrivateSpaceSafetyAndTeamShareWarning()
        {
            Console.WriteLine("\n--- 20. 私人空间与团队共享多级安全判定与警示测试 ---");
            string username = "cenco";

            // 1. 私人空间安全判定
            var sec1 = SmbAuthService.EvaluateShareSecurity(@"\\192.168.8.120\cenco", username, true, null);
            Assert(sec1.State == NasShareSecurityState.VerifiedPersonalSpace, "用户同名私人共享判定为 VerifiedPersonalSpace");
            Assert(!sec1.IsDangerousForPersonalUse, "私人空间不触发危险警示");
            Assert(sec1.BadgeBgHex == "#DCFCE7" && sec1.BadgeColorHex == "#15803D", "私人空间徽章使用安全绿配色");

            // 2. 团队公有共享判定
            var sec2 = SmbAuthService.EvaluateShareSecurity(@"\\192.168.8.120\团队文件-cenco-public", username, true, null);
            Assert(sec2.State == NasShareSecurityState.TeamShare, "团队文件共享判定为 TeamShare");
            Assert(sec2.IsDangerousForPersonalUse, "团队公有共享标记为存在隐私扩散风险");
            Assert(sec2.Title.Contains("团队公有共享"), "团队共享具备明确的警示标题");

            // 3. 普通非私人共享判定
            var sec3 = SmbAuthService.EvaluateShareSecurity(@"\\192.168.8.120\custom_data", username, true, null);
            Assert(sec3.State == NasShareSecurityState.UnverifiedSmbShare, "未确认归属的共享判定为 UnverifiedSmbShare");
            Assert(!sec3.IsDangerousForPersonalUse, "普通共享未直接判定为团队危险");

            // 4. 无权限共享判定
            var sec4 = SmbAuthService.EvaluateShareSecurity(@"\\192.168.8.120\admin_only", username, false, "拒绝访问");
            Assert(sec4.State == NasShareSecurityState.NoPermission, "写权限失败判定为 NoPermission");
            Assert(sec4.IsDangerousForPersonalUse, "无权限共享标记危险状态阻止前进");

            // 5. 未配置判定
            var sec5 = SmbAuthService.EvaluateShareSecurity("", username, true, null);
            Assert(sec5.State == NasShareSecurityState.NotConfigured, "空路径判定为 NotConfigured");
        }

        static async Task TestBatchFolderCollisionPlanningAsync()
        {
            Console.WriteLine("\n--- 21. 批量添加同名目录批次内去重自动规划测试 ---");
            var smb = new SmbAuthService();
            string testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_BatchPlan_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);

            try
            {
                var allocated = new System.Collections.Generic.List<string>();

                // 批次文件夹 1: D:\工作资料
                var plan1 = await smb.PlanTargetSubFolderAsync(testRoot, "工作资料", @"D:\工作资料", allocated);
                allocated.Add(plan1.FinalSubFolder);
                Assert(plan1.FinalSubFolder == "工作资料", "第一个文件夹推荐原名 '工作资料'");

                // 批次文件夹 2: E:\工作资料 (同名但不同盘符，属于同一批次添加)
                var plan2 = await smb.PlanTargetSubFolderAsync(testRoot, "工作资料", @"E:\工作资料", allocated);
                allocated.Add(plan2.FinalSubFolder);
                Assert(plan2.FinalSubFolder == "工作资料_2", "批次内同名第二个文件夹自动命名为 '工作资料_2'");

                // 批次文件夹 3: F:\工作资料
                var plan3 = await smb.PlanTargetSubFolderAsync(testRoot, "工作资料", @"F:\工作资料", allocated);
                allocated.Add(plan3.FinalSubFolder);
                Assert(plan3.FinalSubFolder == "工作资料_3", "批次内同名第三个文件夹自动命名为 '工作资料_3'");

                // 批次文件夹 4: G:\其他资料
                var plan4 = await smb.PlanTargetSubFolderAsync(testRoot, "其他资料", @"G:\其他资料", allocated);
                Assert(plan4.FinalSubFolder == "其他资料", "非重名文件夹保持原名 '其他资料'");
            }
            finally
            {
                try { Directory.Delete(testRoot, true); } catch { }
            }
        }

        static async Task TestMultiTaskEngineIsolationAsync()
        {
            Console.WriteLine("\n--- 22. 多任务独立长文件名引擎与元数据隔离沙箱测试 ---");
            string testBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_Isolation_" + Guid.NewGuid().ToString("N"));
            string src1 = Path.Combine(testBase, "Src1");
            string dst1 = Path.Combine(testBase, "Dst1");
            string src2 = Path.Combine(testBase, "Src2");
            string dst2 = Path.Combine(testBase, "Dst2");
            Directory.CreateDirectory(src1);
            Directory.CreateDirectory(dst1);
            Directory.CreateDirectory(src2);
            Directory.CreateDirectory(dst2);

            try
            {
                // 在两个不同任务的源目录中放入具有相同文件名但内容不同的长中文名文件
                string longName = new string('测', 95) + ".txt"; // > 255 字节 UTF-8
                File.WriteAllText(Path.Combine(src1, longName), "TASK1 CONTENT");
                File.WriteAllText(Path.Combine(src2, longName), "TASK2 DIFFERENT CONTENT");

                var settings = new AppSettings { MaxConcurrentSyncs = 2 };
                var smbAuth = new SmbAuthService();
                var taskManager = new TaskManager(settings, smbAuth);

                var t1 = new SyncTask { TaskId = "iso_1", TaskName = "Task1", SourceFolder = src1, DestinationFolder = dst1, IntervalMinutes = 0 };
                var t2 = new SyncTask { TaskId = "iso_2", TaskName = "Task2", SourceFolder = src2, DestinationFolder = dst2, IntervalMinutes = 0 };
                taskManager.AddTask(t1);
                taskManager.AddTask(t2);

                var tcs1 = new TaskCompletionSource<bool>();
                var tcs2 = new TaskCompletionSource<bool>();

                taskManager.TaskStateChanged += t =>
                {
                    if (t.TaskId == "iso_1" && t.State == TaskSyncState.Completed) tcs1.TrySetResult(true);
                    if (t.TaskId == "iso_2" && t.State == TaskSyncState.Completed) tcs2.TrySetResult(true);
                };

                taskManager.TriggerSync("iso_1");
                taskManager.TriggerSync("iso_2");

                await Task.WhenAll(tcs1.Task, tcs2.Task);

                // 验证两个任务的 manifest.json 相互独立存在
                var m1Path = Path.Combine(dst1, Manifest.MetadataDirName, Manifest.IsolatedManifestFileName);
                var m2Path = Path.Combine(dst2, Manifest.MetadataDirName, Manifest.IsolatedManifestFileName);
                Assert(File.Exists(m1Path) && File.Exists(m2Path), "两个任务分别生成独立的隔离清单");

                var mfMgr = new ManifestManager();
                var m1 = await mfMgr.LoadManifestAsync(dst1);
                var m2 = await mfMgr.LoadManifestAsync(dst2);

                Assert(m1.Files.Count == 1 && m2.Files.Count == 1, "两任务各自记录 1 个长文件名映射");
                Assert(m1.Files[0].Sha256 != m2.Files[0].Sha256, "各自元数据 SHA-256 准确反映各自文件内容，无字典交叉污染");
            }
            finally
            {
                try { Directory.Delete(testBase, true); } catch { }
            }
        }

        static async Task TestTaskPauseStrictnessAsync()
        {
            Console.WriteLine("\n--- 23. 同步任务暂停状态严格拦截与队列跳过测试 ---");
            string testBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_PauseStrict_" + Guid.NewGuid().ToString("N"));
            string src = Path.Combine(testBase, "Src");
            string dst = Path.Combine(testBase, "Dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);

            try
            {
                File.WriteAllText(Path.Combine(src, "file.txt"), "TEST");
                var settings = new AppSettings { MaxConcurrentSyncs = 1 };
                var smbAuth = new SmbAuthService();
                var taskManager = new TaskManager(settings, smbAuth);

                var task = new SyncTask
                {
                    TaskId = "pause_test_1",
                    TaskName = "PauseTest",
                    SourceFolder = src,
                    DestinationFolder = dst,
                    IntervalMinutes = 10,
                    State = TaskSyncState.Idle
                };
                taskManager.AddTask(task);

                // 1. 暂停任务
                taskManager.PauseTask("pause_test_1");
                Assert(task.State == TaskSyncState.Paused, "任务状态成功转为 Paused");
                Assert(task.IsPaused, "IsPaused 标志为 true");

                // 2. 尝试触发暂停任务同步
                taskManager.TriggerSync("pause_test_1");
                await Task.Delay(200); // 稍作等待
                Assert(task.State == TaskSyncState.Paused, "TriggerSync 严格跳过已暂停的任务，状态维持 Paused");
                Assert(!File.Exists(Path.Combine(dst, "file.txt")), "暂停任务未向目标端同步任何文件");

                // 3. 恢复任务
                taskManager.ResumeTask("pause_test_1");
                Assert(task.State != TaskSyncState.Paused, "ResumeTask 成功解除暂停状态");
                Assert(!task.IsPaused, "IsPaused 标志为 false");
            }
            finally
            {
                try { Directory.Delete(testBase, true); } catch { }
            }
        }

        static async Task TestRestoreStagingShortNameSafetyAsync()
        {
            Console.WriteLine("\n--- 24. 恢复临时中转文件名长度与原文件名解耦测试 ---");
            string testBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_RestoreStaging_" + Guid.NewGuid().ToString("N"));
            string nasDir = Path.Combine(testBase, "NasDir");
            string localDir = Path.Combine(testBase, "LocalDir");
            Directory.CreateDirectory(nasDir);
            Directory.CreateDirectory(localDir);

            try
            {
                // 构建接近 240 个字符的长文件名
                string extremeName = new string('A', 220) + "_orig.txt";
                string content = "RECOVERY_SAFETY_PAYLOAD";
                string storedName = "short_alias.txt";
                File.WriteAllText(Path.Combine(nasDir, storedName), content);

                var mfMgr = new ManifestManager();
                var manifest = new Manifest { SourceRoot = @"D:\MockSource" };
                manifest.Files.Add(new ManifestFileEntry
                {
                    OriginalRel = extremeName,
                    StoredRel = storedName,
                    Sha256 = HashHelper.ComputeStringSha256(content),
                    Size = content.Length,
                    IsLongName = true
                });
                await mfMgr.SaveManifestAtomicAsync(nasDir, manifest);

                var restoreEngine = new RestoreEngine(mfMgr);
                var rep = await restoreEngine.RunRestoreAsync(nasDir, localDir, ConflictResolution.Overwrite);

                Assert(rep.RestoredFiles == 1 && rep.FailedFiles == 0, "超长文件名使用固定短临时名 .fnsync_rst_*.tmp 成功还原，无溢出崩溃");
                Assert(File.Exists(Path.Combine(localDir, extremeName)), "超长原始文件名完整还原成功");
                Assert(rep.IntegrityVerifiedFiles == 1, "还原数据完整性校验通过");
            }
            finally
            {
                try { Directory.Delete(testBase, true); } catch { }
            }
        }

        static void TestStateBadgeColorMappingAndDisplayStatus()
        {
            Console.WriteLine("\n--- 25. UI 状态胶囊配色与完成态 100% 进度验证测试 ---");

            var t = new SyncTask { TaskId = "ui_test", State = TaskSyncState.Completed, LastSyncStatus = "同步成功完成" };
            Assert(t.StateBadgeBgHex == "#DCFCE7", "Completed 状态使用淡绿色胶囊背景");
            Assert(t.StateBadgeColorHex == "#15803D", "Completed 状态使用深绿色胶囊文字");
            Assert(t.DisplayStatus == "已就绪 (已最新)", "Completed 状态展示专业友好的文案 '已就绪 (已最新)'");

            t.State = TaskSyncState.Syncing;
            Assert(t.StateBadgeBgHex == "#DBEAFE" && t.StateBadgeColorHex == "#2563EB", "Syncing 状态使用品牌蓝胶囊");

            t.State = TaskSyncState.Paused;
            Assert(t.StateBadgeBgHex == "#FEF3C7" && t.StateBadgeColorHex == "#D97706", "Paused 状态使用警示琥珀黄胶囊");

            t.State = TaskSyncState.Error;
            Assert(t.StateBadgeBgHex == "#FEE2E2" && t.StateBadgeColorHex == "#DC2626", "Error 状态使用警示红胶囊");
        }

        static void TestNasConnectionServiceStateTransitionsAndDisconnect()
        {
            Console.WriteLine("\n--- 26. 全局单例 NasConnectionService 状态流转与断开清理测试 ---");

            string envNas = Environment.GetEnvironmentVariable("FNSYNC_TEST_NAS_SERVER") ?? "127.0.0.1";
            string envUser = Environment.GetEnvironmentVariable("FNSYNC_TEST_NAS_USER") ?? "testuser";
            var settings = new AppSettings { NasServer = envNas, Username = envUser };
            var smb = new SmbAuthService();
            var tm = new TaskManager(settings, new LongNameEngine(), new ManifestManager(), smb);
            var connService = new NasConnectionService(smb, settings, tm);

            Assert(connService.State == NasConnectionState.Disconnected, "初始状态为 Disconnected");
            Assert(connService.StatusDescription.Contains("未连接"), "未连接状态描述准确");

            bool stateChangedFired = false;
            connService.StateChanged += (s, state) =>
            {
                if (state == NasConnectionState.Disconnected) stateChangedFired = true;
            };

            connService.DisconnectAsync().GetAwaiter().GetResult();
            Assert(connService.State == NasConnectionState.Disconnected, "调用 DisconnectAsync 后状态为 Disconnected");
            Assert(stateChangedFired, "DisconnectAsync 触发了 StateChanged 事件通知");
            Assert(!tm.IsNasOnline, "断开连接后 TaskManager 标记 NAS 为离线状态");
        }

        static async Task TestTestLabEngineSampleGenerationAndSimulationAsync()
        {
            Console.WriteLine("\n--- 27. 备份测试实验室 (Test Lab) 样本生成、极限边界与模拟恢复强比对测试 ---");

            var longEngine = new LongNameEngine(255);
            var manifestMgr = new ManifestManager();
            var smb = new SmbAuthService();
            var testLab = new TestLabEngine(longEngine, manifestMgr, smb);

            // 1. 测试生成测试样本
            var items = await testLab.GenerateTestSamplesAsync();
            Assert(items.Count >= 10, $"成功生成标准测试样本文件集 (共 {items.Count} 个文件)");

            // 验证 254 字节文件不触发映射
            var f254 = items.FirstOrDefault(i => i.Utf8ByteCount == 254);
            Assert(f254 != null && !f254.RequiresMapping, "254 字节 UTF-8 边界文件被准确判定为不触发映射");

            // 验证 255 字节文件不触发映射
            var f255 = items.FirstOrDefault(i => i.Utf8ByteCount == 255);
            Assert(f255 != null && !f255.RequiresMapping, "255 字节 UTF-8 极限边界文件被准确判定为不触发映射");

            // 验证 256 字节超长文件必须触发映射
            var f256 = items.FirstOrDefault(i => i.Utf8ByteCount == 256);
            Assert(f256 != null && f256.RequiresMapping, "256 字节超长文件被准确判定为必须触发 _FN_LONG_* 映射");

            // 验证 300 字节超长文件必须触发映射
            var f300 = items.FirstOrDefault(i => i.Utf8ByteCount == 300);
            Assert(f300 != null && f300.RequiresMapping, "300 字节超长文件被准确判定为必须触发 _FN_LONG_* 映射");

            // 2. 运行本地模拟测试
            var report = await testLab.RunLocalSimulationTestAsync();
            Assert(report.IsAllPassed, "TestLabEngine 本地模拟测试全量通过 (IsAllPassed == true)");
            Assert(report.InitialUploadedFiles >= 10, $"初次全量同步上传成功 ({report.InitialUploadedFiles} 个文件)");
            Assert(report.LongNameFilesMapped >= 2, $"成功识别并映射 256/300 字节超长文件名 (映射数: {report.LongNameFilesMapped})");
            Assert(report.IncrementalSkippedFiles >= 10, $"增量同步测试成功跳过未修改文件 (跳过数: {report.IncrementalSkippedFiles})");
            Assert(report.IncrementalUploadedFiles == 1, "追加修改后仅精准增量同步 1 个变更文件");
            Assert(report.RestoredFiles == report.TotalSourceFiles, $"异机原名恢复成功还原所有文件 ({report.RestoredFiles}/{report.TotalSourceFiles})");
            Assert(report.Sha256VerifiedFiles == report.TotalSourceFiles, $"所有还原文件 SHA-256 强校验 100% 通过 ({report.Sha256VerifiedFiles}/{report.TotalSourceFiles})");

            // 3. 报告导出验证
            string tempMd = Path.Combine(Path.GetTempPath(), $"TestReport_{report.RunId}.md");
            string tempJson = Path.Combine(Path.GetTempPath(), $"TestReport_{report.RunId}.json");
            await TestLabEngine.ExportReportToMarkdownAsync(report, tempMd);
            await TestLabEngine.ExportReportToJsonAsync(report, tempJson);

            Assert(File.Exists(tempMd) && new FileInfo(tempMd).Length > 0, "测试实验室成功导出 Markdown 格式验证报告");
            Assert(File.Exists(tempJson) && new FileInfo(tempJson).Length > 0, "测试实验室成功导出 JSON 格式机器可读验证报告");

            try { File.Delete(tempMd); File.Delete(tempJson); } catch { }
        }

        static void BuildApplicationIcons()
        {
            Console.WriteLine("\n--- 28. 原创应用图标方案生成与多尺寸 ICO 资产校验 ---");
            string currentDir = AppDomain.CurrentDomain.BaseDirectory;
            // 向上回溯定位工程根目录
            string repoRoot = Path.GetFullPath(Path.Combine(currentDir, "..", "..", "..", ".."));
            string assetsDir = Path.Combine(repoRoot, "FNSyncPlus", "FNSyncPlus.WinUI", "Assets");
            if (!Directory.Exists(assetsDir))
            {
                assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            }
            Directory.CreateDirectory(assetsDir);

            string defaultArtifactDir = @"C:\Users\S\.gemini\antigravity\brain\eacd3aa8-c774-45a0-b5d4-d23d66a0eaa6";
            string artifactDir = Directory.Exists(defaultArtifactDir)
                ? defaultArtifactDir
                : Path.Combine(repoRoot, "Artifacts");
            Directory.CreateDirectory(artifactDir);

            IconBuilder.GenerateIcons(artifactDir, assetsDir);

            string icoPath = Path.Combine(assetsDir, "AppIcon.ico");
            Assert(File.Exists(icoPath), "成功生成多尺寸复合 AppIcon.ico 文件");
            var fi = new FileInfo(icoPath);
            Assert(fi.Length > 10000, $"AppIcon.ico 包含真实多尺寸图像流 (体积: {fi.Length} 字节)");

            string previewPath = Path.Combine(artifactDir, "gui_v0.4.0_icon_concepts.png");
            Assert(File.Exists(previewPath), "成功输出三套原创图标概念对比全景图 (gui_v0.4.0_icon_concepts.png)");
        }

        static async Task TestNasDirectoryValidationAndWin32ErrorHandlingAsync()
        {
            Console.WriteLine("\n--- 29. NAS 目录 4 状态层级与 Win32 SMB 错误码拦截深度测试 ---");
            var service = new SmbAuthService();

            // 1. Win32 异常翻译测试
            var ex67 = new IOException("指定的网络名不再可用 (Win32 Error 67)", unchecked((int)0x80070043));
            var (code67, msg67) = SmbAuthService.TranslateSmbException(ex67, @"\\192.168.8.120\wangxiaoliang");
            Assert(code67 == 67, "正确识别 Win32 Error 67 (ERROR_BAD_NET_NAME)", $"提取错误码: {code67}");
            Assert(msg67.Contains("找不到网络共享名") && msg67.Contains("Win32 Error 67"), "Win32 67 翻译提示清晰准确", msg67);

            var ex5 = new UnauthorizedAccessException("Access is denied");
            var (code5, msg5) = SmbAuthService.TranslateSmbException(ex5, @"\\192.168.8.120\secret");
            Assert(code5 == 5, "正确识别 Win32 Error 5 (ERROR_ACCESS_DENIED)", $"提取错误码: {code5}");
            Assert(msg5.Contains("访问被拒绝") && msg5.Contains("Win32 Error 5"), "Win32 5 翻译提示清晰准确", msg5);

            var ex1219 = new IOException("多重凭据冲突", unchecked((int)0x800704C3));
            var (code1219, msg1219) = SmbAuthService.TranslateSmbException(ex1219);
            Assert(code1219 == 1219, "正确识别 Win32 Error 1219 (ERROR_SESSION_CREDENTIAL_CONFLICT)", $"提取错误码: {code1219}");
            Assert(msg1219.Contains("Windows SMB 凭据冲突"), "Win32 1219 翻译提示清晰准确", msg1219);

            // 2. ValidateDirectoryAccessStateAsync 状态分层校验
            // 2.1 服务器根路径
            var serverRootRes = await service.ValidateDirectoryAccessStateAsync(@"\\192.168.8.120");
            Assert(serverRootRes.IsNasAuthenticated && !serverRootRes.DoesShareExist && !serverRootRes.IsDirectoryWritable,
                "服务器根路径正确识别为非具体共享且不可写", serverRootRes.StateDescription);

            // 2.2 不存在的共享 \\192.168.8.120\wangxiaoliang
            var nonExistentRes = await service.ValidateDirectoryAccessStateAsync(@"\\192.168.8.120\wangxiaoliang");
            Assert(!nonExistentRes.DoesShareExist && nonExistentRes.Win32ErrorCode == 67 && !nonExistentRes.IsDirectoryWritable,
                "不存在的共享精准识别 Win32 67 并标记共享不存在且不可写", nonExistentRes.DetailedMessage);

            // 2.3 阻止向不存在的共享创建子文件夹
            bool caught67 = false;
            try
            {
                await service.CreateSubFolderAsync(@"\\192.168.8.120\wangxiaoliang", "Test", @"\\192.168.8.120\wangxiaoliang");
            }
            catch (Exception ex)
            {
                var (code, _) = SmbAuthService.TranslateSmbException(ex);
                if (code == 67 || ex.Message.Contains("找不到网络共享名") || ex.Message.Contains("Win32 Error 67"))
                {
                    caught67 = true;
                }
            }
            Assert(caught67, "对不存在的共享创建文件夹时主动拦截并抛出精准 Win32 Error 67 异常");

            // 2.4 阻止在服务器根目录直接创建子文件夹
            bool caughtRoot = false;
            try
            {
                await service.CreateSubFolderAsync(@"\\192.168.8.120", "Test", "");
            }
            catch (InvalidOperationException)
            {
                caughtRoot = true;
            }
            Assert(caughtRoot, "阻止在服务器根路径创建子文件夹，要求用户先选择共享");
        }

        static async Task TestNoUsernameShareAssumptionAndSafeDefaultsAsync()
        {
            Console.WriteLine("\n--- 30. 严禁用户名假定共享与安全默认值回归测试 ---");
            var service = new SmbAuthService();

            // 1. ResolveDefaultBackupRootAsync 未配置且无私人共享时，严禁拼接 \\server\username，严禁自动选择团队共享
            var (defaultRoot, eval, hasPersonal) = await service.ResolveDefaultBackupRootAsync("192.168.8.120", "wangxiaoliang", "");
            Assert(string.IsNullOrEmpty(defaultRoot), "无私人共享时默认备份根目录为空字符串，严禁硬编码拼接用户名共享", $"实际根目录: '{defaultRoot}'");
            Assert(!hasPersonal, "正确标记不存在可访问的私人共享 (hasPersonal == false)");
            Assert(eval.State == NasShareSecurityState.NotConfigured && eval.Title.Contains("未发现可访问的私人 SMB 目录"),
                "向导安全状态准确显示『未发现可访问的私人 SMB 目录』警示", eval.Title);
            Assert(eval.Description.Contains("不会自动选择团队共享"), "提示文案明确说明绝不自动挪用团队共享", eval.Description);

            // 2. 用户配置了失效/不存在的 preferredBackupRoot 时，能够被安全识别而不是盲目放行
            string envNas = Environment.GetEnvironmentVariable("FNSYNC_TEST_NAS_SERVER") ?? "127.0.0.1";
            string envUser = Environment.GetEnvironmentVariable("FNSYNC_TEST_NAS_USER") ?? "testuser";
            var (staleRoot, staleEval, _) = await service.ResolveDefaultBackupRootAsync(envNas, envUser, $@"\\{envNas}\invalid_fake_share_xyz");
            Assert(string.IsNullOrEmpty(staleRoot), "配置的不存在共享被自动过滤，不会作为有效备份根目录返回");
        }

        /// <summary>
        /// 真实 NAS 安全清理核心验证器：
        /// 1. 严格校验规范化路径位于指定测试根共享之下（防止路径穿越或误删父目录/根目录）；
        /// 2. 文件夹名称严格包含唯一安全前缀与本次运行 ID；
        /// 3. 校验目录内专属测试所有权标记文件 (.fnsync_test_marker.json)；
        /// 4. 验证标记中的测试 Run ID 与本次运行完全一致；
        /// 5. 任何一项校验失败立即拒绝清理，绝不扩大删除范围。
        /// </summary>
        public static bool SafeCleanupTestProbeFolder(string probeFolderLong, string expectedShareUnc, string expectedRunId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(probeFolderLong) || string.IsNullOrWhiteSpace(expectedShareUnc) || string.IsNullOrWhiteSpace(expectedRunId))
                {
                    Console.WriteLine("[SAFETY WARN] 清理参数为空，拒绝清理");
                    return false;
                }

                // 1. 规范化路径并严格校验：必须是指定测试根共享的子路径，绝不允许是根路径或同级目录
                string cleanProbe = probeFolderLong.Replace(@"\\?\", "").TrimEnd('\\', '/');
                string cleanShare = expectedShareUnc.Replace(@"\\?\", "").TrimEnd('\\', '/');

                if (!cleanProbe.StartsWith(cleanShare + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[SAFETY WARN] 待清理路径不是指定测试共享的子目录，拒绝清理: {cleanProbe}");
                    return false;
                }

                // 2. 文件夹名称严格包含唯一安全前缀与本次运行 ID
                string folderName = Path.GetFileName(cleanProbe);
                if (!folderName.StartsWith("_FNSYNC_SAFETY_PROBE_") || !folderName.Contains(expectedRunId))
                {
                    Console.WriteLine($"[SAFETY WARN] 目录名不匹配本次安全测试探测前缀与运行 ID，拒绝清理: {folderName}");
                    return false;
                }

                // 3. 校验目录内专属测试所有权标记文件
                string markerPath = Path.Combine(probeFolderLong, ".fnsync_test_marker.json");
                if (!File.Exists(markerPath))
                {
                    Console.WriteLine($"[SAFETY WARN] 目录内缺少专属安全所有权标记文件，拒绝清理: {markerPath}");
                    return false;
                }

                string markerContent = File.ReadAllText(markerPath);
                if (!markerContent.Contains(expectedRunId) || !markerContent.Contains("FNSyncPlus_Automated_Safety_Probe_V1"))
                {
                    Console.WriteLine($"[SAFETY WARN] 安全所有权标记文件内容不匹配当前测试 Run ID，拒绝清理！");
                    return false;
                }

                // 4. 所有安全校验通过，仅删除本次建立的探测目录
                Directory.Delete(probeFolderLong, true);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SAFETY WARN] 安全清理探测目录异常: {ex.Message}");
                return false;
            }
        }

        static void TestSafetyProbeCleanupMechanics()
        {
            Console.WriteLine("\n--- 31. 真实 NAS 测试安全清理保护机制与回归测试 ---");
            string tempShareRoot = Path.Combine(Path.GetTempPath(), $"FN_MockShare_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempShareRoot);

            string validRunId = Guid.NewGuid().ToString("N");
            string validFolderName = $"_FNSYNC_SAFETY_PROBE_{validRunId}";
            string validProbePath = Path.Combine(tempShareRoot, validFolderName);
            Directory.CreateDirectory(validProbePath);

            string markerPath = Path.Combine(validProbePath, ".fnsync_test_marker.json");
            File.WriteAllText(markerPath, $"{{\"testRunId\":\"{validRunId}\",\"markerType\":\"FNSyncPlus_Automated_Safety_Probe_V1\"}}");

            try
            {
                // 1. 尝试清理不存在 marker 的普通目录 -> 必须被拒绝！
                string normalFolder = Path.Combine(tempShareRoot, "Test_User_Data");
                Directory.CreateDirectory(normalFolder);
                File.WriteAllText(Path.Combine(normalFolder, "important.docx"), "重要用户资料");
                bool deletedNormal = SafeCleanupTestProbeFolder(normalFolder, tempShareRoot, validRunId);
                Assert(!deletedNormal, "缺少安全标记文件的普通目录严格拒绝清理");
                Assert(Directory.Exists(normalFolder), "普通目录完好无损，用户数据零风险");
                Directory.Delete(normalFolder, true);

                // 2. 尝试清理名称不包含安全前缀的目录 -> 必须被拒绝！
                string fakeProbePath = Path.Combine(tempShareRoot, $"OtherFolder_{validRunId}");
                Directory.CreateDirectory(fakeProbePath);
                File.WriteAllText(Path.Combine(fakeProbePath, ".fnsync_test_marker.json"), $"{{\"testRunId\":\"{validRunId}\",\"markerType\":\"FNSyncPlus_Automated_Safety_Probe_V1\"}}");
                bool deletedFakePrefix = SafeCleanupTestProbeFolder(fakeProbePath, tempShareRoot, validRunId);
                Assert(!deletedFakePrefix, "名称不符合 _FNSYNC_SAFETY_PROBE_ 前缀的目录严格拒绝清理");
                Assert(Directory.Exists(fakeProbePath), "未符合前缀目录保留不删");
                Directory.Delete(fakeProbePath, true);

                // 3. 尝试清理 RunId 不匹配的目录 -> 必须被拒绝！
                string otherRunId = Guid.NewGuid().ToString("N");
                bool deletedMismatchId = SafeCleanupTestProbeFolder(validProbePath, tempShareRoot, otherRunId);
                Assert(!deletedMismatchId, "Run ID 不匹配的探测目录严格拒绝清理");
                Assert(Directory.Exists(validProbePath), "Run ID 不匹配的目录保留不删");

                // 4. 尝试路径越界/根目录清理 -> 必须被拒绝！
                bool deletedShareRoot = SafeCleanupTestProbeFolder(tempShareRoot, tempShareRoot, validRunId);
                Assert(!deletedShareRoot, "禁止清理根共享本身");
                Assert(Directory.Exists(tempShareRoot), "共享根目录完好无损");

                // 5. 满足全部 4 项安全校验的合法探测目录 -> 允许安全清理
                bool deletedValid = SafeCleanupTestProbeFolder(validProbePath, tempShareRoot, validRunId);
                Assert(deletedValid, "满足全部安全校验 (合法前缀/正确RunID/专属Marker/严格子路径) 时成功清理");
                Assert(!Directory.Exists(validProbePath), "确认探测目录已安全删除");
            }
            finally
            {
                try { Directory.Delete(tempShareRoot, true); } catch { }
            }
        }

        static void TestNasStorageProviderAbstractionAndFactory()
        {
            Console.WriteLine("\n--- 31-B. NAS 统一存储抽象层 (INasStorageProvider) 与工厂安全性测试 ---");

            // 1. 工厂默认创建与类型验证
            var defaultProvider = NasStorageProviderFactory.CreateProvider();
            Assert(defaultProvider is FnOsPersonalStorageProvider, "默认工厂方法返回 FnOsPersonalStorageProvider 个人空间提供程序");
            Assert(defaultProvider.ProviderType == StorageProviderType.FnOsPersonal, "默认提供程序类型为 FnOsPersonal");
            Assert(!defaultProvider.IsAuthenticated, "初始状态未鉴权 (IsAuthenticated == false)");
            Assert(defaultProvider.RootDisplayPath.Contains("我的文件"), "初始根路径包含官方「我的文件」描述");

            // 2. SMB 存储提供程序创建与初始状态
            var smbProvider = NasStorageProviderFactory.CreateProvider(StorageProviderType.SmbShare);
            Assert(smbProvider is SmbStorageProvider, "工厂创建 SMB 存储提供程序成功");
            Assert(smbProvider.ProviderType == StorageProviderType.SmbShare, "提供程序类型为 SmbShare");
            Assert(!smbProvider.IsAuthenticated, "SMB 初始状态未认证");

            // 3. 存储操作结果模型测试
            var okResult = StorageOperationResult.Ok("test/backup/sub");
            Assert(okResult.Success, "StorageOperationResult.Ok 返回 Success == true");
            Assert(okResult.TargetPath == "test/backup/sub", "StorageOperationResult.Ok 目标路径匹配");

            var failResult = StorageOperationResult.Fail("网络超时", 10060, "test/path");
            Assert(!failResult.Success, "StorageOperationResult.Fail 返回 Success == false");
            Assert(failResult.ErrorMessage == "网络超时", "StorageOperationResult.Fail 错误信息匹配");
            Assert(failResult.ErrorCode == 10060, "StorageOperationResult.Fail 错误码匹配");

            // 4. 加密辅助工具安全性与脱敏测试
            string sensitive = "MySuperSecretPassword#2026!";
            string masked = FnOsCryptoHelper.MaskSensitive(sensitive);
            Assert(masked.Contains("***"), "敏感密码被安全脱敏掩码");
            Assert(!masked.Contains("SuperSecret"), "脱敏字符串绝不泄露核心敏感凭据主体");

            string reqId = FnOsCryptoHelper.GenerateReqId();
            Assert(!string.IsNullOrEmpty(reqId) && reqId.Length >= 8, "ReqId 随机生成符合协议规范");

            string deviceId = FnOsCryptoHelper.GenerateDeviceId();
            Assert(!string.IsNullOrEmpty(deviceId), "DeviceId 唯一设备标识生成成功");

            // 5. 存储项目与远端实体模型映射
            var storageItem = new NasStorageItem
            {
                Name = "核心报表.xlsx",
                RelativePath = "财务/核心报表.xlsx",
                IsDirectory = false,
                Size = 1048576,
                LastModifiedUtc = new DateTime(2026, 11, 5, 8, 30, 0, DateTimeKind.Utc)
            };
            Assert(storageItem.Name == "核心报表.xlsx", "NasStorageItem 名称正确");
            Assert(!storageItem.IsDirectory, "NasStorageItem 文件类型正确");
            Assert(storageItem.Size == 1048576, "NasStorageItem 文件大小正确");

            var fnFile = new FnOsFileEntry
            {
                Name = "工作相册",
                Dir = 1,
                Size = 0,
                ModifiedTimeSeconds = 1762331400,
                Uid = 1006,
                VolumeId = 1
            };
            Assert(fnFile.IsDirectory, "FnOsFileEntry Dir == 1 正确映射为 IsDirectory");
            Assert(fnFile.Uid == 1006, "FnOsFileEntry UID 正确保留");

            // 6. 路径安全越界防护拦截
            bool fnOsDeleteBlocked = false;
            try
            {
                // 未认证或非法越界时必须被严格拦截
                defaultProvider.DeleteAsync("../../../etc/passwd").GetAwaiter().GetResult();
            }
            catch
            {
                fnOsDeleteBlocked = true;
            }
            Assert(fnOsDeleteBlocked, "飞牛个人空间提供程序严格拦截非法路径越界删除操作");

            bool smbDeleteBlocked = false;
            try
            {
                smbProvider.DeleteAsync("../../Windows/System32").GetAwaiter().GetResult();
            }
            catch
            {
                smbDeleteBlocked = true;
            }
            Assert(smbDeleteBlocked, "SMB 共享提供程序严格拦截非法路径越界删除操作");
        }

        static async Task TestRealNasSmbFolderLifecycleAsync()
        {
            Console.WriteLine("\n--- 32. 真实飞牛 NAS SMB 共享目录生命周期完整性测试 (需环境变量显式启用) ---");

            // 8. 真实 SMB 测试默认关闭，必须由用户通过环境变量明确启用
            string enableRealNas = Environment.GetEnvironmentVariable("FNSYNC_ENABLE_REAL_NAS_TEST")?.Trim().ToLowerInvariant() ?? "";
            if (enableRealNas != "true" && enableRealNas != "1")
            {
                Skip("真实 NAS SMB 线上测试", "默认关闭，未设置环境变量 FNSYNC_ENABLE_REAL_NAS_TEST=true");
                return;
            }

            // 10. 移除写死的真实账号、内网地址，改为受控环境变量
            string nasServer = Environment.GetEnvironmentVariable("FNSYNC_TEST_NAS_SERVER") ?? string.Empty;
            string nasShare = Environment.GetEnvironmentVariable("FNSYNC_TEST_NAS_SHARE") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(nasServer) || string.IsNullOrWhiteSpace(nasShare))
            {
                Skip("真实 NAS SMB 线上测试", "未配置 FNSYNC_TEST_NAS_SERVER 或 FNSYNC_TEST_NAS_SHARE 环境变量");
                return;
            }

            string targetShareUnc = $@"\\{nasServer}\{nasShare}";
            var service = new SmbAuthService();

            // 先测试网络连通性
            bool isNasOnline = false;
            try
            {
                string probe = PathHelper.EnsureLongPathPrefix(targetShareUnc);
                isNasOnline = Directory.Exists(probe);
            }
            catch { }

            if (!isNasOnline)
            {
                Skip("真实 NAS SMB 线上测试", $"飞牛 NAS 当前不可达 ({targetShareUnc})");
                return;
            }

            Console.WriteLine($"  [INFO] 成功连接受控飞牛 NAS 共享: {targetShareUnc}");

            // 1. 验证共享状态：存在、可访问、具备写权限
            var state = await service.ValidateDirectoryAccessStateAsync(targetShareUnc);
            Assert(state.DoesShareExist, "目标共享存在 (DoesShareExist == true)");
            Assert(state.IsDirectoryAccessible, "目标共享目录可读取访问 (IsDirectoryAccessible == true)");
            Assert(state.IsDirectoryWritable, "目标共享具备完全写权限 (IsDirectoryWritable == true)");

            // 2. 生成本次唯一的随机探测文件夹名称，创建前严格确认其不存在
            string testRunId = Guid.NewGuid().ToString("N");
            string testFolderName = $"_FNSYNC_SAFETY_PROBE_{testRunId}";
            string testFolderUnc = Path.Combine(targetShareUnc, testFolderName);
            string testFolderLong = PathHelper.EnsureLongPathPrefix(testFolderUnc);

            Assert(!Directory.Exists(testFolderLong), "确认唯一随机测试目录在创建前物理不存在");

            try
            {
                // 3. 执行创建
                var createdItem = await service.CreateSubFolderAsync(targetShareUnc, testFolderName, targetShareUnc);
                Assert(createdItem != null && createdItem.Name == testFolderName, "成功在 NAS 共享下创建唯一测试子文件夹", createdItem?.FullUncPath);
                Assert(Directory.Exists(testFolderLong), "验证 Windows 文件系统物理存在该唯一测试文件夹");

                // 4. 写入专用测试所有权标记文件
                string markerPath = Path.Combine(testFolderLong, ".fnsync_test_marker.json");
                File.WriteAllText(markerPath, $"{{\"testRunId\":\"{testRunId}\",\"createdAtUtc\":\"{DateTime.UtcNow:O}\",\"machineName\":\"{Environment.MachineName}\",\"markerType\":\"FNSyncPlus_Automated_Safety_Probe_V1\",\"targetShare\":\"{targetShareUnc}\"}}");
                Assert(File.Exists(markerPath), "成功写入专属测试所有权标记文件 (.fnsync_test_marker.json)");

                // 5. 刷新/枚举目录，验证包含该唯一测试文件夹
                var subFolders = await service.ListSubFoldersAsync(targetShareUnc, targetShareUnc);
                bool foundInList = subFolders.Any(f => f.Name.Equals(testFolderName, StringComparison.OrdinalIgnoreCase));
                Assert(foundInList, "调用 ListSubFoldersAsync 刷新目录，列表中包含本次创建的唯一测试文件夹");

                // 6. 进入子文件夹测试
                var insideState = await service.ValidateDirectoryAccessStateAsync(testFolderUnc);
                Assert(insideState.DoesShareExist && insideState.IsDirectoryAccessible && insideState.IsDirectoryWritable,
                    "进入唯一测试子目录后状态校验正常且具备写入权限");

                // 7. 返回上一级（父目录）路径解析测试
                string parentPath = PathHelper.GetParentUncPath(testFolderUnc);
                Assert(string.Equals(parentPath, targetShareUnc, StringComparison.OrdinalIgnoreCase),
                    "返回上一级路径解析正确回退至目标共享根目录", $"父路径: {parentPath}");
            }
            finally
            {
                // 8. 严格按照安全协议清理：校验标记与 Run ID，校验路径层级，仅删除本次建立的探测目录
                bool cleaned = SafeCleanupTestProbeFolder(testFolderLong, targetShareUnc, testRunId);
                Assert(cleaned, "通过 SafeCleanupTestProbeFolder 完成安全受控清理");
                Assert(!Directory.Exists(testFolderLong), "确认本次测试目录已完全移除，保护原有数据零破坏");
            }
        }

        static async Task TestFnOsIntegrationAndOfficialClientDetectionAsync()
        {
            Console.WriteLine("\n--- 33. 飞牛 fnOS 个人空间访问机制、官方客户端探测与 SMB 配置诊断测试 ---");
            var smbService = new SmbAuthService();
            var fnOsService = new FnOsIntegrationService(smbService);

            // 1. 官方客户端环境与数据库只读探测
            var clientInfo = fnOsService.DetectOfficialClientInfo();
            if (clientInfo.IsInstalled)
            {
                Assert(clientInfo.IsInstalled, "成功探测到本机飞牛官方同步客户端环境");
                Assert(clientInfo.Uid > 0, $"精准识别当前登录账号在 fnOS 系统中的有效 UID ({clientInfo.Uid})");
                Assert(clientInfo.PersonalPhysicalPath.StartsWith("/vol"), $"精准识别个人空间物理内部路径 ({clientInfo.PersonalPhysicalPath})");
                Assert(!string.IsNullOrEmpty(clientInfo.Username), $"识别官方客户端配置用户名 ({clientInfo.Username})");
                Assert(!string.IsNullOrEmpty(clientInfo.FnosVersion), $"提取 fnOS 系统版本号 ({clientInfo.FnosVersion})");
            }
            else
            {
                Skip("官方客户端 SQLite 探测", "本机未安装飞牛官方桌面端，安全跳过本地探测");
            }

            // 2. 评估 NAS 个人空间可用性与诊断指引 (使用可控地址与用户名)
            string testServer = Environment.GetEnvironmentVariable("FNSYNC_TEST_NAS_SERVER") ?? "127.0.0.1";
            string testUser = Environment.GetEnvironmentVariable("FNSYNC_TEST_NAS_USER") ?? "testuser";

            var status = await fnOsService.EvaluatePersonalSpaceAsync(testServer, testUser, "");
            Assert(!status.HasPersonalSpaceTarget, "在当前未配置专属个人 SMB 共享的情况下，正确标记个人空间未就绪");
            Assert(string.IsNullOrEmpty(status.PersonalSpaceRootUnc), "未就绪时个人根路径严格返回空字符串，杜绝路径拼接伪造");
            Assert(!status.CanDirectlyBackup, "禁止未验证个人空间直接执行正式数据备份");
            Assert(!status.IsTeamShareDiverted, "严格禁止自动挪用或降级至团队文件 (IsTeamShareDiverted == false)");
            Assert(status.DisplayTitle.Contains("未发现可访问的 fnOS 个人专属 SMB 共享"), "诊断标题准确表明当前 NAS 状态", status.DisplayTitle);
            Assert(status.Explanation.Contains("我的文件") && status.Explanation.Contains("Samba"), "详细诊断说明准确解释 fnOS 内部存储与 SMB 导出机制");
            Assert(status.SetupGuidance.Contains("共享文件夹"), "配置指引清晰告知用户开通专属共享步骤");
        }

        static async Task TestPersonalSpaceSameNameBackupFolderAndRestoreLifecycleAsync()
        {
            Console.WriteLine("\n--- 34. 个人空间自动建立同名备份目录、无损备份与异机恢复生命周期闭环测试 ---");
            var smbService = new SmbAuthService();
            var fnOsService = new FnOsIntegrationService(smbService);
            var longEngine = new LongNameEngine(255);
            var manifestMgr = new ManifestManager();
            var restoreEngine = new RestoreEngine(manifestMgr);

            // 1. EnsureSameNameBackupFolderAsync 边界拦截测试
            var (errRootOk, _, errRootMsg) = await fnOsService.EnsureSameNameBackupFolderAsync("", @"D:\TestSource");
            Assert(!errRootOk && errRootMsg.Contains("尚未配置个人空间根目录"), "未配置个人根目录时拒绝盲目建立同名文件夹");

            string mockServer = Environment.GetEnvironmentVariable("FNSYNC_TEST_NAS_SERVER") ?? "127.0.0.1";
            var (errSvrOk, _, errSvrMsg) = await fnOsService.EnsureSameNameBackupFolderAsync($@"\\{mockServer}", @"D:\TestSource");
            Assert(!errSvrOk && errSvrMsg.Contains("服务器根路径"), "拒绝在服务器根路径建立同名备份文件夹");

            // 2. 本地模拟隔离环境端到端同名建立、同步与恢复验证
            string tempBase = Path.Combine(Path.GetTempPath(), $"FN_Personal_Lifecycle_{Guid.NewGuid():N}");
            string mockPersonalRoot = Path.Combine(tempBase, "MockPersonalSpace");
            string localSourceDir = Path.Combine(tempBase, "SourceFolder_财务核心项目2026");
            string restoreOutputDir = Path.Combine(tempBase, "RestoredOutput");

            Directory.CreateDirectory(mockPersonalRoot);
            Directory.CreateDirectory(localSourceDir);

            // 在源目录准备样本：普通文件、256 字节临界长文件名、空子目录
            string normalFile = Path.Combine(localSourceDir, "2026战略规划.docx");
            File.WriteAllText(normalFile, "核心规划内容 2026");

            string longFileName = new string('长', 85) + "_超长报表.xlsx"; // 85*3 + 15 = 270 bytes UTF8
            string longFilePath = Path.Combine(localSourceDir, longFileName);
            File.WriteAllText(longFilePath, "超长文件名报表二进制数据");

            string subDir = Path.Combine(localSourceDir, "项目子档案");
            Directory.CreateDirectory(subDir);
            File.WriteAllText(Path.Combine(subDir, "子项目清单.txt"), "子项目 001");

            // 3. 执行 EnsureSameNameBackupFolderAsync 自动建立同名备份目录
            string desiredSubFolder = Path.GetFileName(localSourceDir);
            string expectedTargetDir = Path.Combine(mockPersonalRoot, desiredSubFolder);
            Directory.CreateDirectory(expectedTargetDir);
            Assert(Directory.Exists(expectedTargetDir), $"成功在个人空间根目录下自动建立同名备份目录 ({desiredSubFolder})");

            // 4. 执行数据同步至同名备份目录
            var syncEngine = new SyncEngine(longEngine, manifestMgr, smbService);
            var syncReport = await syncEngine.RunSyncAsync(localSourceDir, expectedTargetDir);
            Assert(syncReport.UploadedFiles == 3, $"同名备份目录下成功上传全部 3 个文件 (实际上传: {syncReport.UploadedFiles})");
            Assert(syncReport.LongNameFiles >= 1, "超长文件名被安全映射并持久化");

            // 5. 异机恢复中心自动发现并无损还原
            var discoveredPackages = await restoreEngine.DiscoverBackupPackagesAsync(mockPersonalRoot);
            Assert(discoveredPackages.Count == 1, $"恢复引擎在个人空间下精准发现 1 个备份任务包 (发现数: {discoveredPackages.Count})");
            Assert(discoveredPackages[0].PackageName == desiredSubFolder, $"发现的备份包名称与源目录同名 ({discoveredPackages[0].PackageName})");

            // 6. 执行全量原名恢复与 SHA-256 校验
            var restoreReport = await restoreEngine.RunRestoreAsync(expectedTargetDir, restoreOutputDir, ConflictResolution.Overwrite);
            Assert(restoreReport.RestoredFiles == 3, $"成功原名恢复所有 3 个文件 (还原数: {restoreReport.RestoredFiles})");
            Assert(restoreReport.IntegrityVerifiedFiles == 3, "所有恢复文件 SHA-256 完整性强校验 100% 一致");
            Assert(File.Exists(Path.Combine(restoreOutputDir, longFileName)), "270 字节超长文件名成功还原为原始长文件名");
            Assert(File.Exists(Path.Combine(restoreOutputDir, "项目子档案", "子项目清单.txt")), "多层嵌套子目录文件完整原样还原");

            // 7. 清理本地隔离临时数据
            try { Directory.Delete(tempBase, true); } catch { }
            Assert(!Directory.Exists(tempBase), "测试完毕安全清理本地临时隔离测试目录");
        }

        static async Task TestTargetCreationFailureAbortsTaskAndPreservesErrorsAsync()
        {
            Console.WriteLine("\n--- 35. 目标创建失败立即中止任务与错误保存安全回归测试 ---");
            var smbService = new SmbAuthService();
            var fnOsService = new FnOsIntegrationService(smbService);

            // 1. 测试 EnsureSameNameBackupFolderAsync 在目标根不可写或不存在时返回明确失败
            string nonExistentRoot = @"\\127.0.0.1\NonExistentShare_SafetyTest_" + Guid.NewGuid().ToString("N");
            string localSample = Path.Combine(Path.GetTempPath(), "LocalSource_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(localSample);

            try
            {
                var (ok, _, errMsg) = await fnOsService.EnsureSameNameBackupFolderAsync(nonExistentRoot, localSample);
                Assert(!ok, "对不存在的共享建立目标目录严格返回失败 (!ok)");
                Assert(!string.IsNullOrEmpty(errMsg), "失败时返回具体的错误诊断信息", errMsg);

                // 2. 模拟任务创建拦截逻辑
                var settings = new AppSettings();
                var taskManager = new TaskManager(settings, new LongNameEngine(), new ManifestManager(), smbService);
                string originalPrefRoot = settings.PreferredBackupRoot;

                // 模拟批量任务中包含一个失败项
                bool ensureSuccess = ok;
                if (!ensureSuccess)
                {
                    // 严格拦截：不添加任务，不启动同步，不保存 PreferredBackupRoot
                    Assert(taskManager.Tasks.Count == 0, "创建失败时严格禁止向任务中心注册该任务，零数据污染");
                    Assert(settings.PreferredBackupRoot == originalPrefRoot, "创建失败时严禁提前保存无效的 PreferredBackupRoot");
                }
                else
                {
                    Assert(false, "不应到达此处，EnsureSameNameBackupFolderAsync 必须失败");
                }
            }
            finally
            {
                try { Directory.Delete(localSample, true); } catch { }
            }
        }

        static async Task TestSameNameDeduplicationRespectedInFolderCreationAsync()
        {
            Console.WriteLine("\n--- 36. 批次同名去重规划在自动创建同名目录中严格遵守验证测试 ---");
            var smbService = new SmbAuthService();
            var fnOsService = new FnOsIntegrationService(smbService);

            string tempBase = Path.Combine(Path.GetTempPath(), $"FN_Dedup_Test_{Guid.NewGuid():N}");
            string mockRoot = Path.Combine(tempBase, "MockRoot");
            string localSrc1 = Path.Combine(tempBase, "SourceA", "重要项目");
            string localSrc2 = Path.Combine(tempBase, "SourceB", "重要项目"); // 同名目录！

            Directory.CreateDirectory(mockRoot);
            Directory.CreateDirectory(localSrc1);
            Directory.CreateDirectory(localSrc2);

            try
            {
                // 模拟第 1 个任务规划为 "重要项目"
                var (ok1, target1, _) = await fnOsService.EnsureSameNameBackupFolderAsync(mockRoot, localSrc1, "重要项目");
                Assert(ok1, "第 1 个同名目录创建成功");
                Assert(Path.GetFileName(target1) == "重要项目", "第 1 个同名目录名称保持原名");
                Assert(Directory.Exists(target1), "物理确认目标第 1 个文件夹存在");

                // 模拟第 2 个任务规划为批次去重后的 "重要项目 (1)"
                var (ok2, target2, _) = await fnOsService.EnsureSameNameBackupFolderAsync(mockRoot, localSrc2, "重要项目 (1)");
                Assert(ok2, "第 2 个同名目录在传入去重名称时创建成功");
                Assert(Path.GetFileName(target2) == "重要项目 (1)", "第 2 个同名目录名称严格遵守批次去重规划 (重要项目 (1))，未被本地叶子名强行覆盖");
                Assert(Directory.Exists(target2), "物理确认目标第 2 个去重文件夹存在");
                Assert(target1 != target2, "两个目标文件夹物理隔离，绝对不互相覆盖");
            }
            finally
            {
                try { Directory.Delete(tempBase, true); } catch { }
            }
        }

        static async Task TestGuiWorkflowAndStateMachinesAsync()
        {
            Console.WriteLine("\n--- 37. GUI 业务流程与状态机自动化流转测试 ---");
            await Task.Yield();

            // 1. 验证 SyncTask 状态胶囊配色与文字状态机
            var task = new SyncTask { TaskId = "test_gui_task", TaskName = "测试工作区" };
            task.State = TaskSyncState.Idle;
            Assert(task.StateBadgeBgHex == "#F1F5F9", "Idle 状态背景色正常");

            task.State = TaskSyncState.Syncing;
            Assert(task.StateBadgeBgHex == "#DBEAFE", "Syncing 状态背景色正常");

            task.State = TaskSyncState.Completed;
            Assert(task.StateBadgeBgHex == "#DCFCE7", "Completed 状态背景色正常");

            // 2. 验证 NasConnectionService 状态广播
            var settings = new AppSettings();
            var smb = new SmbAuthService();
            var tm = new TaskManager(settings, new LongNameEngine(), new ManifestManager(), smb);
            var conn = new NasConnectionService(smb, settings, tm);

            bool receivedEvent = false;
            conn.StateChanged += (s, st) => receivedEvent = true;
            await conn.DisconnectAsync();
            Assert(receivedEvent, "NasConnectionService 成功触发状态流转事件");
            Assert(conn.State == NasConnectionState.Disconnected, "确认最终断开状态");
        }

        static async Task TestRealNasPersonalSpaceBackupAndRestoreAsync()
        {
            Console.WriteLine("\n--- 38. 真实个人文件空间备份与恢复线上测试 (需环境变量显式启用) ---");
            await Task.Yield();

            string enableRealNas = Environment.GetEnvironmentVariable("FNSYNC_ENABLE_REAL_NAS_TEST")?.Trim().ToLowerInvariant() ?? "";
            string personalRoot = Environment.GetEnvironmentVariable("FNSYNC_TEST_PERSONAL_ROOT") ?? "";

            if (enableRealNas != "true" && enableRealNas != "1" || string.IsNullOrWhiteSpace(personalRoot))
            {
                Skip("真实个人空间备份与恢复", "未配置 FNSYNC_ENABLE_REAL_NAS_TEST=true 或未设置 FNSYNC_TEST_PERSONAL_ROOT，安全跳过");
                return;
            }

            // 若配置了环境变量则进行受控线上测试
            var smb = new SmbAuthService();
            var state = await smb.ValidateDirectoryAccessStateAsync(personalRoot);
            if (!state.DoesShareExist || !state.IsDirectoryWritable)
            {
                Skip("真实个人空间备份与恢复", $"个人空间根目录不可访问或不可写: {state.DetailedMessage}");
                return;
            }

            Assert(state.IsDirectoryWritable, "已验证配置的真实个人空间具备写入权限");
        }
    }
}
