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

    public partial class RestoreViewModel : ObservableObject
    {
        private readonly RestoreEngine _restoreEngine;
        private readonly AppSettings _settings;
        private readonly NasConnectionService _connectionService;

        public ObservableCollection<DiscoveredBackupPackage> DiscoveredPackages { get; } = new();
        public ObservableCollection<RestoreFileDisplayItem> PackageFiles { get; } = new();
        public ObservableCollection<ManifestHistoryEntry> HistoryFilesList { get; } = new();
        public ObservableCollection<RestoreFileResult> FailedFilesList { get; } = new();
        public ObservableCollection<string> RestoreLogs { get; } = new();

        [ObservableProperty]
        private string _nasScanRoot = string.Empty;

        [ObservableProperty]
        private DiscoveredBackupPackage? _selectedPackage;

        [ObservableProperty]
        private string _localRestoreTarget = @"D:\Restored_Files";

        [ObservableProperty]
        private int _conflictPolicyIndex = 0; // 0=Skip, 1=Overwrite, 2=Timestamp

        [ObservableProperty]
        private double _restoreProgress = 0;

        [ObservableProperty]
        private string _restoreStatusText = "请点击上方按钮检索 NAS 备份包";

        [ObservableProperty]
        private bool _isRestoring = false;

        [ObservableProperty]
        private bool _isScanning = false;

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
            if (string.IsNullOrWhiteSpace(NasScanRoot))
            {
                RestoreStatusText = "请输入有效的 NAS 扫描根目录。";
                return;
            }

            IsScanning = true;
            RestoreStatusText = "正在从 NAS 备份根目录检索备份包清单...";
            DiscoveredPackages.Clear();
            PackageFiles.Clear();
            HistoryFilesList.Clear();
            FailedFilesList.Clear();

            try
            {
                List<DiscoveredBackupPackage> packages;
                if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider)
                {
                    string relScan = NasScanRoot == "我的文件" ? "" : NasScanRoot.TrimStart('/');
                    packages = await _restoreEngine.DiscoverBackupPackagesAsync(fnProvider, relScan);
                }
                else
                {
                    packages = await _restoreEngine.DiscoverBackupPackagesAsync(NasScanRoot.Trim());
                }

                foreach (var p in packages)
                {
                    DiscoveredPackages.Add(p);
                }

                if (DiscoveredPackages.Count > 0)
                {
                    SelectedPackage = DiscoveredPackages[0];
                    RestoreStatusText = $"检索完成，发现 {DiscoveredPackages.Count} 个完整备份包。";
                }
                else
                {
                    RestoreStatusText = "未在指定位置找到包含有效清单 (manifest.json) 的备份包。";
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
                if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider)
                {
                    files = await _restoreEngine.PreviewBackupAsync(fnProvider, pkg.NasFolderUnc);
                    try
                    {
                        var mfMgr = new ManifestManager();
                        var manifest = await mfMgr.LoadManifestAsync(fnProvider, pkg.NasFolderUnc);
                        if (manifest?.History != null)
                        {
                            foreach (var h in manifest.History)
                            {
                                HistoryFilesList.Add(h);
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    files = await _restoreEngine.PreviewBackupAsync(pkg.NasFolderUnc);
                }

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
                DetailedErrorMessage = ex.ToString();
            }
        }

        [RelayCommand]
        public async Task ExecuteRestoreAsync()
        {
            if (SelectedPackage == null || string.IsNullOrWhiteSpace(LocalRestoreTarget)) return;

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
                    report = await _restoreEngine.RunRestoreAsync(fnProvider, SelectedPackage.NasFolderUnc, LocalRestoreTarget, policy);
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

                    if (report.FailedFiles > 0)
                    {
                        RestoreStatusText = $"还原完成（存在失败项）：成功 {report.RestoredFiles} 个，失败 {report.FailedFiles} 个，校验通过 {report.IntegrityVerifiedFiles} 个。请查看失败列表并可点击重试。";
                    }
                    else
                    {
                        RestoreStatusText = $"还原成功完成！共恢复 {report.RestoredFiles} 个文件，跳过 {report.SkippedFiles} 个，SHA-256 校验通过 {report.IntegrityVerifiedFiles} 个。";
                    }
                });
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
            if (SelectedPackage == null || FailedFilesList.Count == 0 || string.IsNullOrWhiteSpace(LocalRestoreTarget)) return;

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
                    report = await _restoreEngine.RunRestoreSelectedFilesAsync(fnProvider, SelectedPackage.NasFolderUnc, LocalRestoreTarget, targetFiles, policy);
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

                    if (report.FailedFiles == 0)
                    {
                        RestoreStatusText = $"重试完成！所有先前失败的文件均已成功恢复并通过 SHA-256 校验。";
                    }
                    else
                    {
                        RestoreStatusText = $"重试完成，仍有 {report.FailedFiles} 个文件未成功。";
                    }
                });
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
