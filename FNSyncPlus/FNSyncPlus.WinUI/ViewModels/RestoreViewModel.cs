using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
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
        public string OriginalPath { get; set; } = string.Empty;
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
        public string DisplayPath => RestoreDisplayText.OriginalPath(Entry.OriginalRel);
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
        private CancellationTokenSource? _restoreCancellation;
        private (DiscoveredBackupPackage Package, string Target, int Policy)? _retryContext;

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
        private string _localRestoreTarget = string.Empty;

        [ObservableProperty]
        private int _conflictPolicyIndex = 0; // 0=Skip, 1=Overwrite, 2=Timestamp

        [ObservableProperty]
        private double _restoreProgress = 0;

        [ObservableProperty]
        private string _restoreStatusText = "请点击上方按钮检索 NAS 备份包";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConfigureRestore))]
        [NotifyPropertyChangedFor(nameof(CanRetryCurrentRestore))]
        private bool _isRestoring = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConfigureRestore))]
        [NotifyPropertyChangedFor(nameof(CanRetryCurrentRestore))]
        private bool _isScanning = false;

        public bool CanConfigureRestore => !IsRestoring && !IsScanning;
        public bool CanRetryCurrentRestore => CanConfigureRestore && HasFailedFiles && _retryContext is { } retry &&
            retry.Package == SelectedPackage && retry.Target == LocalRestoreTarget && retry.Policy == ConflictPolicyIndex &&
            FailedFilesList.Count > 0 && _connectionService.IsConnected;

        partial void OnLocalRestoreTargetChanged(string value) => OnPropertyChanged(nameof(CanRetryCurrentRestore));
        partial void OnConflictPolicyIndexChanged(int value) => OnPropertyChanged(nameof(CanRetryCurrentRestore));

        [RelayCommand]
        private void CancelRestore() => _restoreCancellation?.Cancel();

        [ObservableProperty]
        private int _restoredFilesCount = 0;

        [ObservableProperty]
        private int _skippedFilesCount = 0;

        [ObservableProperty]
        private int _failedFilesCount = 0;

        [ObservableProperty]
        private int _integrityVerifiedCount = 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanRetryCurrentRestore))]
        private bool _hasFailedFiles = false;

        [ObservableProperty]
        private string _detailedErrorMessage = string.Empty;

        public RestoreViewModel(RestoreEngine restoreEngine, AppSettings settings, NasConnectionService connectionService)
        {
            _restoreEngine = restoreEngine;
            PackageFiles.CollectionChanged += (_, _) => { if (!_buildingPreview) RefreshFileViews(); };
            HistoryFilesList.CollectionChanged += (_, _) => { if (!_buildingPreview) RefreshFileViews(); };
            _settings = settings;
            LocalRestoreTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FN-Restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            _connectionService = connectionService;

            _connectionService.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(NasConnectionService.State) && _connectionService.IsConnected && DiscoveredPackages.Count == 0)
                    UIThreadHelper.RunOnUIThread(async () => await LoadKnownBackupsAsync());
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
                NasScanRoot = "我的文件";
            }
        }

        private bool _buildingPreview;
        private int _previewGeneration;
        [ObservableProperty] private bool _otherLocationsExpanded;
        public ObservableCollection<RestoreFileDisplayItem> VisibleFiles { get; } = new();
        public ObservableCollection<RestoreHistoryDisplayItem> VisibleHistory { get; } = new();
        public ObservableCollection<string> Folders { get; } = new();
        public HashSet<string> SelectedCurrentPaths { get; } = new(StringComparer.Ordinal);
        public HashSet<RestoreHistoryDisplayItem> SelectedHistoryItems { get; } = new();
        public string HistoryRestoreLabel => $"恢复所选 {SelectedHistoryItems.Count} 条历史版本";
        public void NotifyHistorySelection() => OnPropertyChanged(nameof(HistoryRestoreLabel));
        public bool RefreshingViews { get; private set; }
        public string CurrentRestoreLabel => SelectedCurrentPaths.Count > 0 ? $"恢复所选 {SelectedCurrentPaths.Count} 个文件" : "恢复全部最新文件";
        public void NotifySelectionChanged() => OnPropertyChanged(nameof(CurrentRestoreLabel));
        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private string _currentFolder = "";
        [ObservableProperty] private DateTimeOffset? _historyDate;
        partial void OnSearchTextChanged(string value) => RefreshFileViews();
        partial void OnCurrentFolderChanged(string value) => RefreshFileViews();
        partial void OnHistoryDateChanged(DateTimeOffset? value) => RefreshFileViews();
        public void RefreshFileViews()
        {
            RefreshingViews = true;
            VisibleFiles.Clear(); VisibleHistory.Clear(); Folders.Clear();
            string prefix = string.IsNullOrEmpty(CurrentFolder) ? "" : CurrentFolder.TrimEnd('/') + "/";
            foreach (var file in PackageFiles.Where(f => f.OriginalRelPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                string rest = file.OriginalRelPath[prefix.Length..];
                int slash = rest.IndexOf('/');
                if (slash >= 0 && !Folders.Contains(prefix + rest[..slash])) Folders.Add(prefix + rest[..slash]);
                if ((slash < 0 || SearchText.Length > 0) && file.OriginalRelPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase)) VisibleFiles.Add(file);
            }
            foreach (var file in HistoryFilesList.Where(f => f.DisplayPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
                (!HistoryDate.HasValue || f.Entry.ArchivedAt.ToLocalTime().Date == HistoryDate.Value.Date))) VisibleHistory.Add(file);
            RefreshingViews = false;
            NotifySelectionChanged();
        }
        [RelayCommand] private void FolderUp() { int slash = CurrentFolder.LastIndexOf('/'); CurrentFolder = slash < 0 ? "" : CurrentFolder[..slash]; }
        private async Task<INasStorageProvider> ProviderForPackageAsync(DiscoveredBackupPackage package) =>
            package.NasFolderUnc.StartsWith(@"\\") ? await _connectionService.ConnectTeamShareAsync(package.NasFolderUnc) :
            _connectionService.CurrentStorageProvider ?? throw new IOException("请先连接 NAS。");
        [RelayCommand] public async Task LoadKnownBackupsAsync()
        {
            if (!CanConfigureRestore) return;
            if (!_connectionService.IsConnected) { RestoreStatusText = "请先连接 NAS，再刷新备份。"; return; }
            IsScanning = true;
            try
            {
                DiscoveredPackages.Clear();
                foreach (var task in _settings.Tasks.Where(t => t.NasUsername == _connectionService.Username &&
                    (!t.NasAccountUid.HasValue || t.NasAccountUid == _connectionService.CurrentUid)).ToArray())
                {
                    try
                    {
                        List<DiscoveredBackupPackage> found;
                        if (task.ProviderType == StorageProviderType.SmbShare)
                        {
                            await _connectionService.ConnectTeamShareAsync(task.DestinationFolder);
                            found = await _restoreEngine.DiscoverBackupPackagesAsync(task.DestinationFolder);
                        }
                        else found = await _restoreEngine.DiscoverBackupPackagesAsync(_connectionService.CurrentStorageProvider!, task.DestinationFolder);
                        foreach (var package in found.Where(p => !DiscoveredPackages.Any(x => x.NasFolderUnc == p.NasFolderUnc))) DiscoveredPackages.Add(package);
                    }
                    catch (Exception ex) { App.LogCrashException(ex, "Restore.LoadKnownBackup"); RestoreLogs.Add("有备份暂时无法读取，请检查连接与权限。"); }
                }
                SelectedPackage = DiscoveredPackages.FirstOrDefault();
                RestoreStatusText = DiscoveredPackages.Count > 0 ? "请选择备份和要恢复的内容。" : "未找到可用备份，可浏览其他备份位置。";
            }
            finally { IsScanning = false; }
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
                if (!NasScanRoot.StartsWith(@"\\") && _connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider)
                {
                    string relScan = NasScanRoot == "我的文件" ? "" : NasScanRoot.TrimStart('/');
                    packages = await _restoreEngine.DiscoverBackupPackagesAsync(fnProvider, relScan, warnings: scanWarnings);
                }
                else
                {
                    await _connectionService.ConnectTeamShareAsync(NasScanRoot);
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
            SelectedCurrentPaths.Clear(); SelectedHistoryItems.Clear(); NotifyHistorySelection();
            CurrentFolder = "";
            SelectedHistory = null;
            if (value != null) OtherLocationsExpanded = false;
            FailedFilesList.Clear();
            HasFailedFiles = false;
            LoadPackagePreview(value);
        }

        private async void LoadPackagePreview(DiscoveredBackupPackage? pkg)
        {
            int generation = ++_previewGeneration;
            PackageFiles.Clear();
            HistoryFilesList.Clear();
            RefreshFileViews();
            if (pkg == null) return;
            _buildingPreview = true;

            try
            {
                List<RestorePreviewItem> files;
                var historyItems = new List<ManifestHistoryEntry>();
                var provider = await ProviderForPackageAsync(pkg);
                var manifest = await _restoreEngine.ReadPackageManifestAsync(provider, pkg);
                if (generation != _previewGeneration || SelectedPackage != pkg) return;
                if (manifest.IsNew) throw new InvalidDataException("备份已不存在，请刷新。");
                files = manifest.Files.Select(f => new RestorePreviewItem { OriginalRelPath = f.OriginalRel,
                    StoredRelPath = f.StoredRel, Size = f.Size, IsLongName = f.IsLongName, Sha256 = f.Sha256, ModTime = f.ModTimeIso }).ToList();
                if (manifest.History != null) historyItems.AddRange(manifest.History);

                if (!ReferenceEquals(SelectedPackage, pkg)) return;
                foreach (var history in historyItems.OrderByDescending(h => h.ArchivedAt)) HistoryFilesList.Add(new RestoreHistoryDisplayItem(history));

                foreach (var f in files)
                {
                    PackageFiles.Add(new RestoreFileDisplayItem
                    {
                        OriginalPath = f.OriginalRelPath,
                        OriginalRelPath = RestoreDisplayText.OriginalPath(f.OriginalRelPath),
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
            finally { if (generation == _previewGeneration) { _buildingPreview = false; RefreshFileViews(); } }
        }

        [RelayCommand]
        public async Task ExecuteRestoreAsync()
        {
            if (!CanConfigureRestore) return;
            if (SelectedPackage == null) { RestoreStatusText = "请先检索并选择备份。"; return; }
            if (string.IsNullOrWhiteSpace(LocalRestoreTarget)) { RestoreStatusText = "请选择还原到本地的目标文件夹。"; return; }

            using var cancellation = new CancellationTokenSource();
            _restoreCancellation = cancellation;
            var package = SelectedPackage;
            var destination = LocalRestoreTarget;
            _retryContext = (package, destination, ConflictPolicyIndex);
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
                    double pct = rep.TotalFiles > 0 ? (double)(rep.RestoredFiles + rep.SkippedFiles + rep.FailedFiles) / rep.TotalFiles * 100.0 : 0.0;
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
                var provider = await ProviderForPackageAsync(package);
                report = SelectedCurrentPaths.Count > 0
                    ? await _restoreEngine.RetryRestorePackageAsync(provider, package, destination, SelectedCurrentPaths.ToArray(), policy, cancellation.Token)
                    : await _restoreEngine.RunRestorePackageAsync(provider, package, destination, policy, cancellation.Token);

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
                    RestoreStatusText = "还原失败。请查看恢复报告，检查目标文件夹与 NAS 连接后重试。";
                    HasFailedFiles = true;
                });
            }
            finally
            {
                _restoreCancellation = null;
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
            if (!CanRetryCurrentRestore || _retryContext is not { } retry) return;

            using var cancellation = new CancellationTokenSource();
            _restoreCancellation = cancellation;
            IsRestoring = true;
            RestoreStatusText = "正在重新恢复失败文件...";
            var targetFiles = FailedFilesList.Select(f => f.OriginalRel).ToList();

            try
            {
                RestoreProgressReport report;
                var retryProvider = await ProviderForPackageAsync(retry.Package);
                {
                    var policy = ConflictPolicyIndex switch
                    {
                        1 => ConflictResolution.Overwrite,
                        2 => ConflictResolution.RenameWithTimestamp,
                        _ => ConflictResolution.SkipExisting
                    };
                    report = await _restoreEngine.RetryRestorePackageAsync(retryProvider, retry.Package, retry.Target, targetFiles, policy, cancellation.Token);
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
                    RestoreStatusText = "重试失败。请检查目标文件夹与 NAS 连接。";
                });
            }
            finally
            {
                _restoreCancellation = null;
                UIThreadHelper.RunOnUIThread(() =>
                {
                    IsRestoring = false;
                });
            }
        }

        [RelayCommand]
        public async Task RestoreHistoryAsync()
        {
            if (!CanConfigureRestore) return;
            if (SelectedPackage == null || SelectedHistory == null) { RestoreStatusText = "请先选择要恢复的历史版本。"; return; }
            if (string.IsNullOrWhiteSpace(LocalRestoreTarget)) { RestoreStatusText = "请选择还原到本地的目标文件夹。"; return; }
            var package = SelectedPackage;
            var histories = (SelectedHistoryItems.Count > 0 ? SelectedHistoryItems : new[] { SelectedHistory }.AsEnumerable())
                .OrderByDescending(h => h.Entry.ArchivedAt).ToArray();
            string destination = LocalRestoreTarget;
            var policy = ConflictPolicyIndex switch
            {
                1 => ConflictResolution.Overwrite,
                2 => ConflictResolution.RenameWithTimestamp,
                _ => ConflictResolution.SkipExisting
            };
            using var cancellation = new CancellationTokenSource();
            _restoreCancellation = cancellation;
            _retryContext = null;
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
            RestoreLogs.Clear();
            RestoreLogs.Add("所选历史版本：" + string.Join("；", histories.Select(h => h.DisplayPath + "（" + h.ArchivedAt + "）")));
            try
            {
                var provider = await ProviderForPackageAsync(package);
                int completed = 0;
                foreach (var selected in histories)
                {
                    var report = await _restoreEngine.RestorePackageHistoryAsync(provider, package, destination, selected.Entry, policy, cancellation.Token);
                    RestoredFilesCount += report.RestoredFiles;
                    SkippedFilesCount += report.SkippedFiles;
                    FailedFilesCount += report.FailedFiles;
                    IntegrityVerifiedCount += report.IntegrityVerifiedFiles;
                    RestoreProgress = ++completed * 100.0 / histories.Length;
                    foreach (var item in report.FailedItems) RestoreLogs.Add($"[历史恢复失败] {RestoreDisplayText.OriginalPath(item.OriginalRel)}: {item.ErrorMessage}");
                }
                HasFailedFiles = FailedFilesCount > 0;
                RestoreStatusText = $"历史恢复完成：成功 {RestoredFilesCount}，已校验 {IntegrityVerifiedCount}，跳过 {SkippedFilesCount}，失败 {FailedFilesCount}。";
                RestoreLogs.Add(RestoreStatusText);
            }
            catch (OperationCanceledException) { RestoreStatusText = "历史恢复已取消。"; }
            catch (Exception ex)
            {
                DetailedErrorMessage = ex.ToString();
                RestoreStatusText = "历史恢复失败。请查看恢复报告，检查目标文件夹与 NAS 连接后重试。";
                RestoreLogs.Add(RestoreStatusText);
                HasFailedFiles = true;
            }
            finally { _restoreCancellation = null; IsRestoring = false; }
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
                        lines.Add($"[FAIL] {RestoreDisplayText.OriginalPath(f.OriginalRel)} -> {f.ErrorMessage}");
                        if (!string.IsNullOrEmpty(f.DetailedException)) lines.Add($"  Exception: {f.DetailedException}");
                    }
                }

                await RestoreDisplayText.WriteReportAsync(logFile, lines);
                RestoreStatusText = $"报告已导出至: {logFile}";
            }
            catch (Exception ex)
            {
                RestoreStatusText = $"导出日志失败: {ex.Message}";
            }
        }
    }
}
