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
            Console.WriteLine("          飞牛同步增强版 (FN Sync Plus) 核心功能自动化测试套件");
            Console.WriteLine("================================================================================\n");

            try
            {
                TestNormalNames();
                TestBoundaryByteLengths();
                TestVeryLongChineseName();
                TestLongDirectoryAndNestedPath();
                TestCollisionHandling();
                await TestManifestAtomicPersistenceAndLegacyCompatAsync();
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

            // 构造恰好 254 字节的文件名
            string base254 = new string('a', 250) + ".txt"; // 254 bytes
            int b254 = PathHelper.GetUtf8ByteCount(base254);
            var (s254, l254) = engine.TranslateFilePath(base254);
            Assert(!l254 && b254 == 254 && s254 == base254, "254 字节文件名保持原名", $"字节: {b254}");

            // 构造恰好 255 字节的文件名
            string base255 = new string('b', 251) + ".txt"; // 255 bytes
            int b255 = PathHelper.GetUtf8ByteCount(base255);
            var (s255, l255) = engine.TranslateFilePath(base255);
            Assert(!l255 && b255 == 255 && s255 == base255, "255 字节边界文件名保持原名", $"字节: {b255}");

            // 构造 256 字节的文件名 (触发转换!)
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

            // 中文每个字符 3 字节，90 个汉字即 270 字节 > 255 字节
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

            string longDirName = "09_folder_" + new string('测', 90); // 279 字节
            int dirBytes = PathHelper.GetUtf8ByteCount(longDirName);
            var (storedDir, dirLong) = engine.TranslateDirPath(longDirName);

            Assert(dirLong, "超长目录识别为超长", $"目录原字节: {dirBytes} -> 映射为: {storedDir}");

            // 嵌套路径：超长目录 / 超长子目录 / 超长文件
            string longSubDir = "sub_" + new string('试', 88);
            string longFile = "file_" + new string('文', 88) + ".pdf";
            string nestedOriginal = $"{longDirName}/{longSubDir}/{longFile}";

            var (storedNested, nestedLong) = engine.TranslateFilePath(nestedOriginal);
            Assert(nestedLong, "多层嵌套超长路径自动逐层转换", $"原路径: {nestedOriginal.Substring(0, 30)}... -> 映射: {storedNested}");

            string[] storedParts = storedNested.Split('/');
            Assert(storedParts.Length == 3, "转换后依然保持原有三层目录结构");
            foreach (var part in storedParts)
            {
                Assert(PathHelper.GetUtf8ByteCount(part) <= 255, "嵌套路径中的每一级组件均符合 NAS 限制", $"组件: {part}");
            }
        }

        static void TestCollisionHandling()
        {
            Console.WriteLine("\n--- 5. 碰撞冲突防护测试 ---");
            var engine = new LongNameEngine(maxUtf8Bytes: 255);

            string f1 = "folder/" + new string('甲', 90) + ".txt";
            string f2 = "folder/" + new string('乙', 90) + ".txt";

            var (s1, _) = engine.TranslateFilePath(f1);
            var (s2, _) = engine.TranslateFilePath(f2);

            Assert(s1 != s2, "不同超长文件绝不产生相同短名", $"f1: {s1}, f2: {s2}");

            // 幂等性：同一文件再次调用必须返回完全一致的短名
            var (s1_again, _) = engine.TranslateFilePath(f1);
            Assert(s1 == s1_again, "幂等性检验：同一文件重复转换结果完全一致", $"初次: {s1}, 再次: {s1_again}");
        }

        static async Task TestManifestAtomicPersistenceAndLegacyCompatAsync()
        {
            Console.WriteLine("\n--- 6. 清单原子持久化与旧版兼容测试 ---");
            string testDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_Manifest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                var mgr = new ManifestManager();
                var manifest = new Manifest
                {
                    SourceRoot = @"C:\TestSource",
                    TargetRoot = @"\\192.168.8.120\Backup",
                    Files = new()
                    {
                        new ManifestFileEntry
                        {
                            OriginalRel = "测试中文文件.txt",
                            StoredRel = "测试中文文件.txt",
                            Size = 1024,
                            ModTimeIso = DateTimeOffset.UtcNow,
                            ModTimeUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                            Sha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                            IsLongName = false
                        }
                    }
                };

                // 原子写入测试
                await mgr.SaveManifestAtomicAsync(testDir, manifest);
                string mainFile = Path.Combine(testDir, Manifest.CurrentManifestFileName);
                Assert(File.Exists(mainFile), "清单文件成功原子写入到目标目录");

                // 读取验证
                var loaded = await mgr.LoadManifestAsync(testDir);
                Assert(loaded.Files.Count == 1 && loaded.Files[0].OriginalRel == "测试中文文件.txt", "清单内容反序列化校验通过");

                // 旧版兼容测试：写入旧版 __FN_ORIGINAL_NAMES__.json
                string legacyDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Test_Legacy_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(legacyDir);
                string legacyFile = Path.Combine(legacyDir, Manifest.LegacyManifestFileName);
                string legacyJson = @"{
                    ""version"": 1,
                    ""note"": ""old go format"",
                    ""files"": [
                        {
                            ""original"": ""旧版文件.txt"",
                            ""stored"": ""_FN_LONG_abc123.txt"",
                            ""size"": 2048,
                            ""mtime_ns"": 1700000000000000000,
                            ""sha256"": ""112233445566778899""
                        }
                    ]
                }";
                File.WriteAllText(legacyFile, legacyJson, Encoding.UTF8);

                var legacyLoaded = await mgr.LoadManifestAsync(legacyDir);
                Assert(legacyLoaded.Files.Count == 1 && legacyLoaded.Files[0].OriginalRel == "旧版文件.txt", "旧版 Go 清单自动无缝兼容并迁移");
                Assert(legacyLoaded.Files[0].StoredRel == "_FN_LONG_abc123.txt", "旧版存储别名解析正确");

                // 清理
                Directory.Delete(legacyDir, true);
            }
            finally
            {
                if (Directory.Exists(testDir)) Directory.Delete(testDir, true);
            }
        }

        static async Task TestEndToEndIncrementalSyncAndRestoreAsync()
        {
            Console.WriteLine("\n--- 7. 端到端增量同步与异机恢复完整性测试 ---");
            string testRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "E2E_Test_" + Guid.NewGuid().ToString("N"));
            string localSrc = Path.Combine(testRoot, "LocalSource");
            string nasDest = Path.Combine(testRoot, "MockNasShare");
            string localRestore = Path.Combine(testRoot, "RestoredTarget");

            Directory.CreateDirectory(localSrc);
            Directory.CreateDirectory(nasDest);
            Directory.CreateDirectory(localRestore);

            try
            {
                // 创建三种文件：
                // 1. 普通短文件
                string fShort = Path.Combine(localSrc, "普通文件.txt");
                File.WriteAllText(fShort, "短文件内容-12345", Encoding.UTF8);

                // 2. 超长文件名文件 (95 个汉字 = 285 字节 > 255 字节)
                string longName = "这是极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其极其超长中文合同文档名称2026.xlsx";
                string fLong = Path.Combine(localSrc, longName);
                File.WriteAllText(fLong, "长文件合同专用二进制模拟内容-ABCDEFG", Encoding.UTF8);

                // 3. 超长目录下的子文件
                string longDir = Path.Combine(localSrc, "09_目录_" + new string('测', 85));
                Directory.CreateDirectory(longDir);
                string fInLongDir = Path.Combine(longDir, "子文件.txt");
                File.WriteAllText(fInLongDir, "子目录内的重要数据", Encoding.UTF8);

                // 计算原始文件的 SHA-256 用于最终校验
                string shaShortOrig = HashHelper.ComputeFileSha256(fShort);
                string shaLongOrig = HashHelper.ComputeFileSha256(fLong);
                string shaNestedOrig = HashHelper.ComputeFileSha256(fInLongDir);

                // 启动同步引擎
                var longEngine = new LongNameEngine(maxUtf8Bytes: 255);
                var manifestMgr = new ManifestManager();
                var smbAuth = new SmbAuthService();
                var syncEngine = new SyncEngine(longEngine, manifestMgr, smbAuth);

                // 执行第 1 次同步
                var rep1 = await syncEngine.RunSyncAsync(localSrc, nasDest);
                Assert(rep1.TotalFiles == 3, "第 1 次同步扫描出全部 3 个文件");
                Assert(rep1.UploadedFiles == 3, "第 1 次同步成功上传全部 3 个文件");
                Assert(rep1.LongNameFiles >= 1, "超长文件被成功识别并映射");

                // 验证 NAS 上的物理文件名
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

                // 增量同步测试：无变化时再次同步应跳过
                var rep2 = await syncEngine.RunSyncAsync(localSrc, nasDest);
                Assert(rep2.UnchangedFiles == 3 && rep2.UploadedFiles == 0, "增量测试：未修改文件 100% 跳过上传");

                // 修改一个文件后增量同步
                File.AppendAllText(fShort, "追加的新修改内容");
                var rep3 = await syncEngine.RunSyncAsync(localSrc, nasDest);
                Assert(rep3.UploadedFiles == 1 && rep3.UnchangedFiles == 2, "增量测试：仅更新修改过的单个文件");

                // 异机还原测试（全新独立 RestoreEngine，仅基于 NAS 备份与元数据还原）
                Console.WriteLine("\n--- 执行异机恢复测试 ---");
                var restoreEngine = new RestoreEngine(manifestMgr);
                var restoreRep = await restoreEngine.RunRestoreAsync(nasDest, localRestore, ConflictResolution.Overwrite);

                Assert(restoreRep.RestoredFiles == 3, "恢复中心成功恢复全部 3 个文件");
                Assert(restoreRep.LongNameRestoredFiles >= 1, "超长文件名成功还原为原始完整长文件名");

                // 验证恢复出来的文件与原文件是否 100% 一致（包括路径、名称、内容 SHA-256）
                string fShortRestored = Path.Combine(localRestore, "普通文件.txt");
                string fLongRestored = Path.Combine(localRestore, longName);
                string fNestedRestored = Path.Combine(localRestore, Path.GetFileName(longDir), "子文件.txt");

                Assert(File.Exists(fShortRestored), "普通文件原名还原成功");
                Assert(File.Exists(fLongRestored), "超长文件名原始名称还原成功");
                Assert(File.Exists(fNestedRestored), "超长目录下的嵌套子文件还原成功");

                // 校验 SHA-256 完整性
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
            Console.WriteLine("\n--- 8. 真实样本目录 (FN_Sync_Test) 模拟增量与恢复测试 ---");
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

                // 跑真实样本目录
                var rep = await syncEngine.RunSyncAsync(realDir, testNas);
                Assert(rep.TotalFiles > 0, $"成功扫描真实样本目录 ({rep.TotalFiles} 个文件)");
                Assert(rep.LongNameFiles >= 2, $"成功识别样本中的 256/300 字节超长文件 (映射数: {rep.LongNameFiles})");
                Assert(rep.FailedFiles == 0, "真实样本全部同步成功，无 1018 报错！");

                // 执行恢复
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
