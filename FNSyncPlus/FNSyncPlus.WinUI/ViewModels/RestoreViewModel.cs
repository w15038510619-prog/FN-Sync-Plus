using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.WinUI.Helpers;

namespace FNSyncPlus.WinUI.ViewModels
{
    public class RestoreFileDisplayItem
    {
        public string OriginalRelPath { get; set; } = string.Empty;
        public string StoredRelPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string FormattedSize => $"{Size:N0} B";
        public bool IsLongName { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }

    public class RestoreHistoryDisplayItem
    {
        public ManifestHistoryEntry Entry { get; }
        public RestoreHistoryDisplayItem(ManifestHistoryEntry entry) => Entry = entry;
        public string OriginalRel => Entry.OriginalRel;
        public string ArchivedAt => Entry.ArchivedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string Reason => Entry.Reason switch
        {
            "renamed" => "重命名前",
            "deleted" => "本地删除前",
            "superseded" => "修改前",
            _ => "历史版本"
        };
        public string Size => $"{Entry.Size:N0} B";
    }

    public partial class RestoreViewModel : ObservableObject
    {
        private readonly RestoreEngine _restoreEngine;
        private readonly AppSettings _settings;
        private readonly NasConnectionService _connectionService;

        public ObservableCollection<DiscoveredBackupPackage> DiscoveredPackages { get; } = new();
        public ObservableCollection<RestoreFileDisplayItem> PackageFiles { get; } = new();
        public ObservableCollection<RestoreHistoryDisplayItem> HistoryFilesList { get; } = new();
        public ObservableCollection<RestoreFileResult> FailedFilesList { get; } = new();
        public ObservableCollection<string> RestoreLogs { get; } = new();

        [ObservableProperty]
        private string _nasScanRoot = string.Empty;

        [ObservableProperty]
        private DiscoveredBackupPackage? _selectedPackage;

        [ObservableProperty]
        private RestoreHistoryDisplayItem? _selectedHistory;

        [ObservableProperty]
        private string _localRestoreTarget = @"D:\Restored_Files";

        [ObservableProperty]
        private int _conflictPolicyIndex = 0; // 0=Skip, 1=Overwrite, 2=Timestamp

        [ObservableProperty]
        private double _restoreProgress = 0;

        [ObservableProperty]
        private string _restoreStatusText = "请点击上方按钮检索 NAS 备份包";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConfigureRestore))]
        private bool _isRestoring = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConfigureRestore))]
        private bool _isScanning = false;

        public bool CanConfigureRestore => !IsRestoring && !IsScanning;

        [ObservableProperty]
        private int _restoredFilesCount = 0;

        [ObservableProperty]
        private int _skippedFilesCount = 0;

        [ObservableProperty]
        private int _failedFilesCount = 0;

        [ObservableProperty]
        private int _integrityVerifiedCount = 0;

        [ObservableProperty]
        private bool _hasFailedFiles = false;

        [ObservableProperty]
        private string _detailedErrorMessage = string.Empty;

        public RestoreViewModel(RestoreEngine restoreEngine, AppSettings settings, NasConnectionService connectionService)
        {
            _restoreEngine = restoreEngine;
            _settings = settings;
            _connectionService = connectionService;

            _connectionService.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(NasConnectionService.CurrentStorageProvider) &&
                    _connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider)
                {
                    UIThreadHelper.RunOnUIThread(() =>
                    {
                        if (string.IsNullOrWhiteSpace(NasScanRoot)) NasScanRoot = "我的文件";
                    });
                }
            };

            if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider)
            {
                NasScanRoot = "我的文件";
            }
            else if (!string.IsNullOrEmpty(_settings.PreferredBackupRoot))
            {
                NasScanRoot = _settings.PreferredBackupRoot;
            }
            else if (!string.IsNullOrEmpty(_settings.NasServer))
            {
                NasScanRoot = $@"\\{_settings.NasServer.Trim()}\{_settings.Username.Trim()}";
            }
        }

        [RelayCommand]
        public async Task ScanPackagesAsync()
        {
            if (!CanConfigureRestore) return;
            if (string.IsNullOrWhiteSpace(NasScanRoot))
            {
                RestoreStatusText = "请输入有效的 NAS 扫描根目录。";
                return;
            }

            IsScanning = true;
            RestoreStatusText = "正在从 NAS 备份根目录检索备份包清单...";
            DetailedErrorMessage = string.Empty;
            DiscoveredPackages.Clear();
            PackageFiles.Clear();
            HistoryFilesList.Clear();
            FailedFilesList.Clear();

            try
            {
                List<DiscoveredBackupPackage> packages;
                var scanWarnings = new List<string>();
                if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider)
                {
                    string relScan = NasScanRoot == "我的文件" ? "" : NasScanRoot.TrimStart('/');
                    packages = await _restoreEngine.DiscoverBackupPackagesAsync(fnProvider, relScan, warnings: scanWarnings);
                }
                else
                {
                    packages = await _restoreEngine.DiscoverBackupPackagesAsync(NasScanRoot.Trim(), warnings: scanWarnings);
                }

                foreach (var p in packages)
                {
                    DiscoveredPackages.Add(p);
                }

                if (DiscoveredPackages.Count > 0)
                {
                    SelectedPackage = DiscoveredPackages[0];
                    RestoreStatusText = $"检索完成，发现 {DiscoveredPackages.Count} 个备份清单。";
                }
                else
                {
                    RestoreStatusText = "未在指定位置找到包含有效清单的备份包。";
                }
                if (scanWarnings.Count > 0)
                {
                    DetailedErrorMessage = string.Join(System.Environment.NewLine, scanWarnings);
                    foreach (var warning in scanWarnings) RestoreLogs.Add($"[扫描警告] {warning}");
                    RestoreStatusText = $"扫描未完成：发现 {DiscoveredPackages.Count} 个备份清单，{scanWarnings.Count} 处读取失败；悬停查看原因。";
                }
            }
            catch (Exception ex)
            {
                RestoreStatusText = $"检索异常: [{ex.GetType().Name}] {ex.Message}";
                DetailedErrorMessage = ex.ToString();
            }
            finally
            {
                IsScanning = false;
            }
        }

        partial void OnSelectedPackageChanged(DiscoveredBackupPackage? value)
        {
            SelectedHistory = null;
            FailedFilesList.Clear();
            HasFailedFiles = false;
            LoadPackagePreview(value);
        }

        private async void LoadPackagePreview(DiscoveredBackupPackage? pkg)
        {
            PackageFiles.Clear();
            HistoryFilesList.Clear();
            if (pkg == null) return;

            try
            {
                List<RestorePreviewItem> files;
                var historyItems = new List<ManifestHistoryEntry>();
                if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider)
                {
                    var manifest = await _restoreEngine.ReadPackageManifestAsync(fnProvider, pkg);
                    if (manifest.IsNew) throw new InvalidDataException("备份清单已不存在，请重新扫描");
                    files = manifest.Files.Select(f => new RestorePreviewItem {
                        OriginalRelPath = f.OriginalRel, StoredRelPath = f.StoredRel,
                        Size = f.Size, IsLongName = f.IsLongName, Sha256 = f.Sha256, ModTime = f.ModTimeIso
                    }).ToList();
                    if (manifest.History != null) historyItems.AddRange(manifest.History);
                }
                else
                {
                    files = await _restoreEngine.PreviewBackupAsync(pkg.NasFolderUnc);
                }

                if (!ReferenceEquals(SelectedPackage, pkg)) return;
                foreach (var history in historyItems.OrderByDescending(h => h.ArchivedAt)) HistoryFilesList.Add(new RestoreHistoryDisplayItem(history));

                foreach (var f in files)
                {
                    PackageFiles.Add(new RestoreFileDisplayItem
                    {
                        OriginalRelPath = f.OriginalRelPath,
                        StoredRelPath = f.StoredRelPath,
                        Size = f.Size,
                        IsLongName = f.IsLongName,
                        Sha256 = f.Sha256
                    });
                }
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(SelectedPackage, pkg)) return;
                DetailedErrorMessage = ex.ToString();
                RestoreStatusText = "备份清单读取失败；悬停查看原因。";
            }
        }

        [RelayCommand]
        public async Task ExecuteRestoreAsync()
        {
            if (!CanConfigureRestore || SelectedPackage == null || string.IsNullOrWhiteSpace(LocalRestoreTarget)) return;

            IsRestoring = true;
            RestoreProgress = 0;
            RestoredFilesCount = 0;
            SkippedFilesCount = 0;
            FailedFilesCount = 0;
            IntegrityVerifiedCount = 0;
            HasFailedFiles = false;
            DetailedErrorMessage = string.Empty;
            FailedFilesList.Clear();
            RestoreLogs.Clear();
            RestoreStatusText = "正在执行还原并强校验 SHA-256 完整性...";

            var policy = ConflictPolicyIndex switch
            {
                1 => ConflictResolution.Overwrite,
                2 => ConflictResolution.RenameWithTimestamp,
                _ => ConflictResolution.SkipExisting
            };

            void OnProgress(RestoreProgressReport rep)
            {
                UIThreadHelper.RunOnUIThread(() =>
                {
                    double pct = rep.TotalFiles > 0 ? (double)rep.RestoredFiles / rep.TotalFiles * 100.0 : 0.0;
                    RestoreProgress = pct;
                    RestoreStatusText = $"正在还原: {rep.CurrentFile} ({pct:F0}%)";
                    RestoredFilesCount = rep.RestoredFiles;
                    SkippedFilesCount = rep.SkippedFiles;
                    FailedFilesCount = rep.FailedFiles;
                    IntegrityVerifiedCount = rep.IntegrityVerifiedFiles;
                });
            }

            void OnLog(string level, string msg)
            {
                UIThreadHelper.RunOnUIThread(() =>
                {
                    RestoreLogs.Add(msg);
                });
            }

            try
            {
                _restoreEngine.ProgressChanged += OnProgress;
                _restoreEngine.LogEmitted += OnLog;

                RestoreProgressReport report;
                if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider)
                {
                    report = await _restoreEngine.RunRestorePackageAsync(fnProvider, SelectedPackage, LocalRestoreTarget, policy);
                }
                else
                {
                    report = await _restoreEngine.RunRestoreAsync(SelectedPackage.NasFolderUnc, LocalRestoreTarget, policy);
                }

                UIThreadHelper.RunOnUIThread(() =>
                {
                    RestoreProgress = 100;
                    RestoredFilesCount = report.RestoredFiles;
                    SkippedFilesCount = report.SkippedFiles;
                    FailedFilesCount = report.FailedFiles;
                    IntegrityVerifiedCount = report.IntegrityVerifiedFiles;
                    HasFailedFiles = report.FailedFiles > 0;

                    FailedFilesList.Clear();
                    foreach (var item in report.FailedItems)
                    {
                        FailedFilesList.Add(item);
                    }

                    RestoreStatusText = report.FailedFiles > 0
                        ? $"{report.StatusMessage}。请查看失败列表并可点击重试。"
                        : report.StatusMessage;
                });
            }
            catch (OperationCanceledException)
            {
                UIThreadHelper.RunOnUIThread(() => RestoreStatusText = "还原已取消。");
            }
            catch (Exception ex)
            {
                App.LogCrashException(ex, "RestoreViewModel.ExecuteRestoreAsync");
                UIThreadHelper.RunOnUIThread(() =>
                {
                    DetailedErrorMessage = ex.ToString();
                    RestoreStatusText = $"还原失败: [{ex.GetType().Name}] {ex.Message}";
                    HasFailedFiles = true;
                });
            }
            finally
            {
                _restoreEngine.ProgressChanged -= OnProgress;
                _restoreEngine.LogEmitted -= OnLog;
                UIThreadHelper.RunOnUIThread(() =>
                {
                    IsRestoring = false;
                });
            }
        }

        [RelayCommand]
        public async Task RetryFailedFilesAsync()
        {
            if (!CanConfigureRestore || SelectedPackage == null || FailedFilesList.Count == 0 || string.IsNullOrWhiteSpace(LocalRestoreTarget)) return;

            IsRestoring = true;
            RestoreStatusText = "正在重新恢复失败文件...";
            var targetFiles = FailedFilesList.Select(f => f.OriginalRel).ToList();

            try
            {
                RestoreProgressReport report;
                if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider)
                {
                    var policy = ConflictPolicyIndex switch
                    {
                        1 => ConflictResolution.Overwrite,
                        2 => ConflictResolution.RenameWithTimestamp,
                        _ => ConflictResolution.SkipExisting
                    };
                    report = await _restoreEngine.RetryRestorePackageAsync(fnProvider, SelectedPackage, LocalRestoreTarget, targetFiles, policy);
                }
                else
                {
                    throw new NotSupportedException("当前存储类型尚不支持重试，请重新执行恢复。");
                }

                UIThreadHelper.RunOnUIThread(() =>
                {
                    RestoredFilesCount += report.RestoredFiles;
                    IntegrityVerifiedCount += report.IntegrityVerifiedFiles;
                    FailedFilesCount = report.FailedFiles;
                    HasFailedFiles = report.FailedFiles > 0;

                    FailedFilesList.Clear();
                    foreach (var item in report.FailedItems)
                    {
                        FailedFilesList.Add(item);
                    }

                    RestoreStatusText = $"重试结果：{report.StatusMessage}";
                });
            }
            catch (OperationCanceledException)
            {
                UIThreadHelper.RunOnUIThread(() => RestoreStatusText = "重试已取消。");
            }
            catch (Exception ex)
            {
                UIThreadHelper.RunOnUIThread(() =>
                {
                    DetailedErrorMessage = ex.ToString();
                    RestoreStatusText = $"重试异常: {ex.Message}";
                });
            }
            finally
            {
                UIThreadHelper.RunOnUIThread(() =>
                {
                    IsRestoring = false;
                });
            }
        }

        [RelayCommand]
        public async Task RestoreHistoryAsync()
        {
            if (!CanConfigureRestore || SelectedPackage == null || SelectedHistory == null || string.IsNullOrWhiteSpace(LocalRestoreTarget)) return;
            if (_connectionService.CurrentStorageProvider is not FnOsPersonalStorageProvider provider)
            {
                RestoreStatusText = "当前存储类型尚不支持从此界面恢复历史版本。";
                return;
            }
            var package = SelectedPackage;
            var history = SelectedHistory.Entry;
            string destination = LocalRestoreTarget;
            var policy = ConflictPolicyIndex switch
            {
                1 => ConflictResolution.Overwrite,
                2 => ConflictResolution.RenameWithTimestamp,
                _ => ConflictResolution.SkipExisting
            };
            IsRestoring = true;
            RestoreStatusText = "正在核对并恢复所选历史版本...";
            RestoreProgress = 0;
            RestoredFilesCount = 0;
            SkippedFilesCount = 0;
            FailedFilesCount = 0;
            IntegrityVerifiedCount = 0;
            DetailedErrorMessage = string.Empty;
            FailedFilesList.Clear();
            HasFailedFiles = false;
            try
            {
                var report = await _restoreEngine.RestorePackageHistoryAsync(provider, package, destination, history, policy);
                RestoredFilesCount = report.RestoredFiles;
                SkippedFilesCount = report.SkippedFiles;
                FailedFilesCount = report.FailedFiles;
                IntegrityVerifiedCount = report.IntegrityVerifiedFiles;
                HasFailedFiles = report.FailedFiles > 0;
                RestoreProgress = 100;
                RestoreStatusText = report.FailedFiles > 0
                    ? $"历史版本：{report.StatusMessage}。请重新选择该历史版本后重试。"
                    : $"历史版本：{report.StatusMessage}";
                foreach (var item in report.FailedItems) RestoreLogs.Add($"[历史恢复失败] {item.OriginalRel}: {item.ErrorMessage}");
            }
            catch (OperationCanceledException) { RestoreStatusText = "历史恢复已取消。"; }
            catch (Exception ex)
            {
                DetailedErrorMessage = ex.ToString();
                RestoreStatusText = $"历史恢复失败: {ex.Message}";
                HasFailedFiles = true;
            }
            finally { IsRestoring = false; }
        }

        [RelayCommand]
        public async Task ExportRestoreLogAsync()
        {
            try
            {
                string targetDir = Directory.Exists(LocalRestoreTarget) ? LocalRestoreTarget : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string logFile = Path.Combine(targetDir, $"Restore_Report_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                var lines = new List<string>
                {
                    "================================================================================",
                    "            飞牛同步增强版 (FN Sync Plus) 异机还原与完整性校验报告",
                    "================================================================================",
                    $"还原时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"备份来源: {SelectedPackage?.PackageName ?? "未知"} ({SelectedPackage?.NasFolderUnc ?? ""})",
                    $"还原目标: {LocalRestoreTarget}",
                    $"成功恢复: {RestoredFilesCount} 项",
                    $"跳过已有: {SkippedFilesCount} 项",
                    $"SHA-256通过: {IntegrityVerifiedCount} 项",
                    $"失败数量: {FailedFilesCount} 项",
                    "--------------------------------------------------------------------------------",
                    "详细操作日志:"
                };
                lines.AddRange(RestoreLogs);
                if (FailedFilesList.Count > 0)
                {
                    lines.Add("--------------------------------------------------------------------------------");
                    lines.Add("失败文件清单与原因:");
                    foreach (var f in FailedFilesList)
                    {
                        lines.Add($"[FAIL] {f.OriginalRel} -> {f.ErrorMessage}");
                        if (!string.IsNullOrEmpty(f.DetailedException)) lines.Add($"  Exception: {f.DetailedException}");
                    }
                }

                await File.WriteAllLinesAsync(logFile, lines);
                RestoreStatusText = $"报告已导出至: {logFile}";
            }
            catch (Exception ex)
            {
                RestoreStatusText = $"导出日志失败: {ex.Message}";
            }
        }
    }
}
