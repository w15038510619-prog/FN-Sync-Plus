using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Tests
{
    class Program
    {
        static int _passed = 0;
        static int _failed = 0;

        static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.WriteLine("================================================================================");
            Console.WriteLine("          飞牛同步增强版 (FN Sync Plus) 核心功能与缺陷修复自动化测试套件");
            Console.WriteLine("================================================================================\n");

            try
            {
                TestNormalNames();
                TestBoundaryByteLengths();
                TestVeryLongChineseName();
                TestLongDirectoryAndNestedPath();
                TestCollisionHandling();
                TestPathSafetyAndRecursionPrevention();
                TestDirectoryNameValidation();
                await TestManifestIsolationAndLockingAsync();
                await TestStagingFixedShortNamesNear255BytesAsync();
                await TestManifestPreservationOnPartialFailureAsync();
                await TestRestoreSha256MismatchHandlingAsync();
                await TestEmptyDirectoryRestorationAsync();
                await TestEndToEndIncrementalSyncAndRestoreAsync();
                await TestSyncWithRealTestFilesAsync();
                TestMultiTaskModelAndSettingsMigration();
                TestPersonalSpaceAndCategorizedShares();
                await TestAutoPlanTargetSubFolderAndDeduplicationAsync();
                await TestTaskManagerConcurrencyAndMultiTaskIsolationAsync();
                await TestRestoreEngineDiscoverBackupPackagesAsync();
                TestPrivateSpaceSafetyAndTeamShareWarning();
                await TestBatchFolderCollisionPlanningAsync();
                await TestMultiTaskEngineIsolationAsync();
                await TestTaskPauseStrictnessAsync();
                await TestRestoreStagingShortNameSafetyAsync();
                TestStateBadgeColorMappingAndDisplayStatus();
                TestNasConnectionServiceStateTransitionsAndDisconnect();
                await TestTestLabEngineSampleGenerationAndSimulationAsync();
                BuildApplicationIcons();
                await TestNasDirectoryValidationAndWin32ErrorHandlingAsync();
                await TestNoUsernameShareAssumptionAndSafeDefaultsAsync();
                await TestRealNasSmbFolderLifecycleAsync();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FATAL UNHANDLED] {ex}");
                Console.ResetColor();
                _failed++;
            }

            Console.WriteLine("\n================================================================================");
            Console.WriteLine($"测试完成: 共通过 {_passed} 项, 失败 {_failed} 项");
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
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("[FAIL] ");
                Console.ResetColor();
                Console.WriteLine(testName + (detail != null ? $" -> {detail}" : ""));
                _failed++;
            }
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

            var settings = new AppSettings { NasServer = "192.168.8.120", Username = "cenco" };
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
            var (staleRoot, staleEval, _) = await service.ResolveDefaultBackupRootAsync("192.168.8.120", "wangxiaoliang", @"\\192.168.8.120\invalid_fake_share_xyz");
            Assert(string.IsNullOrEmpty(staleRoot), "配置的不存在共享被自动过滤，不会作为有效备份根目录返回");
        }

        static async Task TestRealNasSmbFolderLifecycleAsync()
        {
            Console.WriteLine("\n--- 31. 真实飞牛 NAS SMB 共享目录生命周期完整性测试 (创建/刷新/进入/返回/取消/清理) ---");
            var service = new SmbAuthService();
            string teamShareUnc = @"\\192.168.8.120\团队文件-cenco-public";

            // 先测试网络连通性
            bool isNasOnline = false;
            try
            {
                string probe = PathHelper.EnsureLongPathPrefix(teamShareUnc);
                isNasOnline = Directory.Exists(probe);
            }
            catch { }

            if (!isNasOnline)
            {
                Console.WriteLine("  [SKIP] 飞牛 NAS 当前不可达，跳过真实 SMB 线上交互测试");
                return;
            }

            Console.WriteLine($"  [INFO] 成功连接真实飞牛 NAS 共享: {teamShareUnc}");

            // 1. 验证共享状态：存在、可访问、具备写权限
            var state = await service.ValidateDirectoryAccessStateAsync(teamShareUnc);
            Assert(state.DoesShareExist, "真实团队共享存在 (DoesShareExist == true)");
            Assert(state.IsDirectoryAccessible, "真实团队共享目录可读取访问 (IsDirectoryAccessible == true)");
            Assert(state.IsDirectoryWritable, "真实团队共享具备完全写权限 (IsDirectoryWritable == true)");

            // 2. 测试在真实共享中创建测试文件夹 "Test"
            string testFolderName = "Test";
            string testFolderUnc = Path.Combine(teamShareUnc, testFolderName);
            string testFolderLong = PathHelper.EnsureLongPathPrefix(testFolderUnc);

            // 清理可能存在的历史测试残留
            if (Directory.Exists(testFolderLong))
            {
                try { Directory.Delete(testFolderLong, true); } catch { }
            }

            // 执行创建
            var createdItem = await service.CreateSubFolderAsync(teamShareUnc, testFolderName, teamShareUnc);
            Assert(createdItem != null && createdItem.Name == testFolderName, "成功在真实 NAS 共享下创建子文件夹 'Test'", createdItem?.FullUncPath);
            Assert(Directory.Exists(testFolderLong), "验证 Windows 文件系统物理存在该 'Test' 文件夹");

            // 3. 刷新/枚举目录，验证包含 'Test'
            var subFolders = await service.ListSubFoldersAsync(teamShareUnc, teamShareUnc);
            bool foundInList = subFolders.Any(f => f.Name.Equals(testFolderName, StringComparison.OrdinalIgnoreCase));
            Assert(foundInList, "调用 ListSubFoldersAsync 刷新目录，列表中包含新创建的 'Test' 文件夹");

            // 4. 进入子文件夹测试
            var insideState = await service.ValidateDirectoryAccessStateAsync(testFolderUnc);
            Assert(insideState.DoesShareExist && insideState.IsDirectoryAccessible && insideState.IsDirectoryWritable,
                "进入 'Test' 子目录后状态校验正常且具备写入权限");

            // 5. 返回上一级（父目录）路径解析测试
            string parentPath = PathHelper.GetParentUncPath(testFolderUnc);
            Assert(string.Equals(parentPath, teamShareUnc, StringComparison.OrdinalIgnoreCase),
                "返回上一级路径解析正确回退至团队共享根目录", $"父路径: {parentPath}");

            // 6. 模拟界面取消操作测试：取消时不对 NAS 产生任何多余文件夹或写操作
            string cancelFolderName = "CancelledFolderCandidate";
            string cancelFolderUnc = Path.Combine(teamShareUnc, cancelFolderName);
            string cancelFolderLong = PathHelper.EnsureLongPathPrefix(cancelFolderUnc);
            // 模拟取消：用户在 UI 撤销输入，后端不调用 CreateSubFolderAsync
            Assert(!Directory.Exists(cancelFolderLong), "取消新建文件夹操作时，NAS 上不产生任何多余目录");

            // 7. 清理测试文件夹并刷新验证
            Directory.Delete(testFolderLong, true);
            Assert(!Directory.Exists(testFolderLong), "测试完毕，安全彻底清理 NAS 上的 'Test' 文件夹，保护 NAS 原有数据");
            var afterCleanList = await service.ListSubFoldersAsync(teamShareUnc, teamShareUnc);
            bool cleaned = !afterCleanList.Any(f => f.Name.Equals(testFolderName, StringComparison.OrdinalIgnoreCase));
            Assert(cleaned, "刷新目录确认 'Test' 已完全移除，无任何测试残留");
        }
    }
}
