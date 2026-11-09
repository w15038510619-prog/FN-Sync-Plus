using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;

namespace FNSyncPlus.WinUI.ViewModels
{
    public partial class TestLabViewModel : ObservableObject
    {
        private readonly TestLabEngine _engine;
        private readonly NasConnectionService _connectionService;

        public ObservableCollection<TestFileItem> GeneratedSamples { get; } = new();
        public ObservableCollection<string> ExecutionLogs { get; } = new();
        public ObservableCollection<TestFileVerificationResult> VerificationResults { get; } = new();

        [ObservableProperty]
        private bool _isBusy = false;

        [ObservableProperty]
        private string _statusText = "备份测试实验室就绪。可先点击【生成测试文件集】准备临界样本。";

        [ObservableProperty]
        private int _selectedModeIndex = 0; // 0 = 本地模拟, 1 = 真实飞牛个人空间, 2 = 真实飞牛 SMB

        [ObservableProperty]
        private string _targetNasShare = string.Empty;

        [ObservableProperty]
        private bool _hasReport = false;

        [ObservableProperty]
        private string _reportVerdictText = string.Empty;

        [ObservableProperty]
        private string _reportVerdictColor = "#15803D";

        [ObservableProperty]
        private string _reportVerdictBg = "#DCFCE7";

        [ObservableProperty]
        private string _reportStatsSummary = string.Empty;

        [ObservableProperty]
        private double _testElapsedSeconds = 0;

        [ObservableProperty]
        private TestLabReport? _lastReport;

        public TestLabViewModel(TestLabEngine engine, NasConnectionService connectionService)
        {
            _engine = engine;
            _connectionService = connectionService;

            // A saved backup location is not authorization to create a test there.
            TargetNasShare = string.Empty;
        }

        [RelayCommand]
        public async Task GenerateSamplesAsync()
        {
            IsBusy = true;
            StatusText = "正在生成包含 254/255/256/300 字节边界、超长中文与特殊字符的测试集...";
            ExecutionLogs.Clear();

            var progress = new Progress<string>(msg =>
            {
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            });

            try
            {
                var items = await _engine.GenerateTestSamplesAsync(progress);
                GeneratedSamples.Clear();
                foreach (var it in items)
                {
                    GeneratedSamples.Add(it);
                }

                StatusText = $"样本集生成成功！共包含 {items.Count} 个测试文件，涵盖超长中文、边界字节及多层嵌套目录。";
            }
            catch (Exception ex)
            {
                StatusText = $"生成测试文件失败: {ex.Message}";
                ExecutionLogs.Add($"[ERROR] {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task RunTestAsync()
        {
            IsBusy = true;
            ExecutionLogs.Clear();
            VerificationResults.Clear();
            HasReport = false;

            var progress = new Progress<string>(msg =>
            {
                ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            });

            try
            {
                TestLabReport report;
                if (SelectedModeIndex == 1)
                {
                    if (_connectionService.CurrentStorageProvider is not FnOsPersonalStorageProvider fnProvider || !fnProvider.IsAuthenticated)
                    {
                        StatusText = "测试失败：未连接飞牛官方个人空间。请先在【NAS 设备】页面完成登录认证。";
                        ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] 错误：未连接飞牛个人空间，无法执行官方「我的文件」端到端测试。");
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(TargetNasShare))
                    {
                        StatusText = "测试失败：请先明确填写已授权的个人空间测试父目录。";
                        return;
                    }
                    StatusText = "正在执行真实飞牛 NAS「我的文件」端到端自动化测试 (包含分块流式传输、增量跳过与哈希校验)...";
                    report = await _engine.RunNasFnOsPersonalTestAsync(fnProvider, progress, includeLargeFile: false, authorizedTestParent: TargetNasShare);
                }
                else if (SelectedModeIndex == 2)
                {
                    string target = TargetNasShare;
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        StatusText = "测试失败：未配置有效的真实 NAS 共享路径。请先在上方输入或选择有效的 NAS 共享。";
                        ExecutionLogs.Add($"[{DateTime.Now:HH:mm:ss}] 错误：未指定目标 NAS 共享，无法执行网络端到端测试。");
                        return;
                    }
                    StatusText = "正在执行真实飞牛 NAS SMB 端到端测试 (自动创建隔离目录 FN_TEST_*)...";
                    report = await _engine.RunNasSmbTestAsync(target, progress);
                }
                else
                {
                    StatusText = "正在执行本地隔离模拟环境端到端自动化测试...";
                    report = await _engine.RunLocalSimulationTestAsync(progress);
                }

                LastReport = report;
                TestElapsedSeconds = report.ElapsedSeconds;

                foreach (var v in report.ItemVerifications)
                {
                    VerificationResults.Add(v);
                }

                HasReport = true;
                if (report.IsAllPassed)
                {
                    ReportVerdictText = "🟢 测试全部通过 (100% 完整性无损通过)";
                    ReportVerdictColor = "#15803D";
                    ReportVerdictBg = "#DCFCE7";
                }
                else
                {
                    ReportVerdictText = "🔴 测试发现异常 (未达到 100% 通过)";
                    ReportVerdictColor = "#B91C1C";
                    ReportVerdictBg = "#FEE2E2";
                }

                ReportStatsSummary = $"样本总数: {report.TotalSourceFiles} | 长名映射: {report.LongNameFilesMapped} | 增量跳过: {report.IncrementalSkippedFiles} | 恢复文件: {report.RestoredFiles} | SHA-256校验通过: {report.Sha256VerifiedFiles}/{report.TotalSourceFiles} | 耗时: {report.ElapsedSeconds:F2}秒";
                StatusText = $"测试流程执行完毕！耗时 {report.ElapsedSeconds:F2} 秒。已生成逐项比对报告。";
            }
            catch (Exception ex)
            {
                StatusText = $"测试发生未捕获异常: {ex.Message}";
                ExecutionLogs.Add($"[CRITICAL] {ex.Message}");
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        public async Task ExportReportAsync()
        {
            if (LastReport == null) return;

            try
            {
                string exportDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "FNSyncPlus_TestReports");
                Directory.CreateDirectory(exportDir);

                string mdPath = Path.Combine(exportDir, $"TestReport_{LastReport.RunId}.md");
                string jsonPath = Path.Combine(exportDir, $"TestReport_{LastReport.RunId}.json");

                await TestLabEngine.ExportReportToMarkdownAsync(LastReport, mdPath);
                await TestLabEngine.ExportReportToJsonAsync(LastReport, jsonPath);

                StatusText = $"测试报告已成功导出至桌面: {mdPath}";
            }
            catch (Exception ex)
            {
                StatusText = $"导出报告失败: {ex.Message}";
            }
        }
    }
}
