using System;
using System.IO;
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
    }
}
