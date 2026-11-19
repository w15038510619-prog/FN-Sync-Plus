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
        private string _restoreStatusText = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConfigureRestore))]
        [NotifyPropertyChangedFor(nameof(CanRestoreSelection))]
        [NotifyPropertyChangedFor(nameof(CanRestoreEntireBackup))]
        [NotifyPropertyChangedFor(nameof(CanRestoreHistory))]
        [NotifyPropertyChangedFor(nameof(CanRetryCurrentRestore))]
        private bool _isRestoring = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanConfigureRestore))]
        [NotifyPropertyChangedFor(nameof(CanRestoreSelection))]
        [NotifyPropertyChangedFor(nameof(CanRestoreEntireBackup))]
        [NotifyPropertyChangedFor(nameof(CanRestoreHistory))]
        [NotifyPropertyChangedFor(nameof(CanRetryCurrentRestore))]
        private bool _isScanning = false;

        public bool CanConfigureRestore => !IsRestoring && !IsScanning;
        public bool CanRetryCurrentRestore => PreviewUsable && HasFailedFiles && _retryContext is { } retry &&
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
            _settings = settings;
            LocalRestoreTarget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FN-Restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            _connectionService = connectionService;

            _connectionService.PropertyChanged += (sender, args) =>
            {
                if (args.PropertyName == nameof(NasConnectionService.State))
                    UIThreadHelper.RunOnUIThread(async () =>
                    {
                        if (!_connectionService.IsConnected)
                        {
                            _connectionEpoch++;
                            _restoreCancellation?.Cancel();
                            SelectedPackage = null;
                            DiscoveredPackages.Clear();
                            NotifyPreviewChanged();
                        }
                        else if (DiscoveredPackages.Count == 0) await LoadKnownBackupsAsync();
                    });
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
        private bool _previewReady;
        private bool _previewFailed;
        private int _previewGeneration;
        private int _connectionEpoch;
        private CancellationTokenSource? _previewCancellation;
        private RestoreBrowserItem _latestRoot = new("备份", null, true);
        private RestoreBrowserItem _historyRoot = new("备份", null, true);
        private RestoreBrowserItem? _folder;
        private RestoreBrowserItem? _historyFile;
        private ILookup<string, RestoreHistoryDisplayItem>? _versions;
        private readonly HashSet<RestoreBrowserItem> _checked = new();

        public ObservableCollection<RestoreBrowserItem> BrowserItems { get; } = new();
        public ObservableCollection<RestoreBrowserItem> Breadcrumbs { get; } = new();
        public ObservableCollection<RestoreHistoryDisplayItem> VisibleHistory { get; } = new();
        private bool PreviewUsable => CanConfigureRestore && _previewReady && _connectionService.IsConnected && SelectedPackage != null;
        public bool CanRestoreSelection => PreviewUsable && _checked.Any(item => item.OriginalPath != null);
        public bool CanRestoreEntireBackup => PreviewUsable && _latestRoot.Children.Count > 0;
        public bool CanRestoreHistory => PreviewUsable && SelectedHistory != null && VisibleHistory.Contains(SelectedHistory);
        public bool CanGoUp => _folder?.Parent != null;
        public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
        public bool ShowBrowserMessage => BrowserItems.Count == 0;
        public string BrowserMessage => _buildingPreview || IsScanning ? "正在读取备份…"
            : _previewFailed ? "无法读取备份，请刷新后重试。"
            : !_previewReady ? (_connectionService.IsConnected ? "请选择备份。" : "NAS 未连接，请先连接设备。")
            : IsSearching ? "未找到匹配的文件或文件夹。"
            : IsHistoryMode ? "这里没有历史版本。" : "此文件夹为空。";
        public string SelectionSummary => $"已选 {_checked.Count(item => !item.IsFolder)} 个文件、{_checked.Count(item => item.IsFolder && item.Children.Count == 0)} 个空文件夹";
        public string HistoryCaption => _historyFile == null ? "选择文件查看历史版本" : _historyFile.DisplayPath;
        public string[] SelectedFilePaths => _checked.Where(item => !item.IsFolder).Select(item => item.OriginalPath!).ToArray();
        public string[] SelectedDirectoryPaths => _checked.Where(item => item.IsFolder && item.OriginalPath != null).Select(item => item.OriginalPath!).ToArray();

        [ObservableProperty] private string _searchText = "";
        [ObservableProperty] private bool _isHistoryMode;
        partial void OnSearchTextChanged(string value) => RefreshFileViews();
        partial void OnIsHistoryModeChanged(bool value) => OpenFolder(value ? _historyRoot : _latestRoot);
        partial void OnSelectedHistoryChanged(RestoreHistoryDisplayItem? value) => OnPropertyChanged(nameof(CanRestoreHistory));
        partial void OnIsScanningChanged(bool value) => OnPropertyChanged(nameof(BrowserMessage));

        private void NotifyPreviewChanged()
        {
            OnPropertyChanged(nameof(BrowserMessage));
            OnPropertyChanged(nameof(ShowBrowserMessage));
            OnPropertyChanged(nameof(CanRestoreSelection));
            OnPropertyChanged(nameof(CanRestoreEntireBackup));
            OnPropertyChanged(nameof(CanRestoreHistory));
            OnPropertyChanged(nameof(CanRetryCurrentRestore));
            OnPropertyChanged(nameof(SelectionSummary));
        }

        private static void AddBrowserPath(RestoreBrowserItem root, string rawPath, bool directory)
        {
            // Hierarchy is keyed by the original path segments. Formatting each label must not
            // merge differently encoded paths or replace the identity sent to the restore engine.
            var segments = (RestoreDisplayText.OriginalPath(rawPath) == rawPath ? rawPath.Replace('\\', '/') : rawPath)
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parent = root;
            for (int i = 0; i < segments.Length; i++)
            {
                bool leaf = i == segments.Length - 1;
                string part = segments[i];
                bool folder = !leaf || directory;
                string key = (folder ? "D:" : "F:") + part;
                if (!parent.Children.TryGetValue(key, out var child))
                    parent.Children.Add(key, child = new(part, parent, folder));
                parent = child;
                if (leaf) parent.OriginalPath = rawPath;
            }
        }

        private void BuildBrowser(IEnumerable<string> directories)
        {
            string name = SelectedPackage?.PackageName ?? "备份";
            _latestRoot = new(name, null, true);
            _historyRoot = new(name, null, true);
            foreach (var path in directories) AddBrowserPath(_latestRoot, path, directory: true);
            foreach (var file in PackageFiles) AddBrowserPath(_latestRoot, file.OriginalPath, directory: false);
            _versions = HistoryFilesList.ToLookup(item => item.Entry.OriginalRel, StringComparer.Ordinal);
            foreach (var group in _versions) AddBrowserPath(_historyRoot, group.Key, directory: false);
            OpenFolder(IsHistoryMode ? _historyRoot : _latestRoot);
        }

        public void OpenFolder(RestoreBrowserItem item)
        {
            if (!item.IsFolder) return;
            _folder = item;
            SetHistoryFile(null);
            if (SearchText.Length > 0) SearchText = "";
            else RefreshFileViews();
        }

        public void SetHistoryFile(RestoreBrowserItem? file)
        {
            _historyFile = file;
            SelectedHistory = null;
            VisibleHistory.Clear();
            if (file?.OriginalPath is string path && _versions != null)
                foreach (var version in _versions[path]) VisibleHistory.Add(version);
            OnPropertyChanged(nameof(HistoryCaption));
        }

        public void RefreshFileViews()
        {
            BrowserItems.Clear();
            Breadcrumbs.Clear();
            var folder = _folder ?? (IsHistoryMode ? _historyRoot : _latestRoot);
            var trail = new Stack<RestoreBrowserItem>();
            for (var item = folder; item != null; item = item.Parent) trail.Push(item);
            foreach (var item in trail) Breadcrumbs.Add(item);

            var query = SearchText.Trim();
            var root = IsHistoryMode ? _historyRoot : _latestRoot;
            var rows = IsSearching ? root.Descendants().Skip(1).Where(item => item.DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase))
                : folder.Children.Values;
            foreach (var item in rows.OrderByDescending(item => item.IsFolder).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                item.CanCheck = !IsHistoryMode;
                item.ShowLocation = IsSearching && item.Parent?.Parent != null;
                BrowserItems.Add(item);
            }
            OnPropertyChanged(nameof(CanGoUp));
            OnPropertyChanged(nameof(IsSearching));
            NotifyPreviewChanged();
        }

        public void ToggleSelection(RestoreBrowserItem item)
        {
            bool select = item.IsChecked != true;
            foreach (var child in item.Descendants()) SetChecked(child, select);
            for (var folder = item.Parent; folder?.Parent != null; folder = folder.Parent)
            {
                bool? state = folder.Children.Values.All(child => child.IsChecked == true) ? true
                    : folder.Children.Values.All(child => child.IsChecked == false) ? false : null;
                SetChecked(folder, state);
            }
            NotifyPreviewChanged();
        }

        private void SetChecked(RestoreBrowserItem item, bool? value)
        {
            item.IsChecked = value;
            if (value == true) _checked.Add(item);
            else _checked.Remove(item);
        }

        [RelayCommand] private void FolderUp() { if (_folder?.Parent is { } parent) OpenFolder(parent); }

        internal void LoadVisualFixture(bool history)
        {
            _buildingPreview = false;
            _previewReady = true;
            foreach (var path in new[] { "子目录/文件A.xlsx", "子目录/文件B.xlsx", "普通/文件C.xlsx", "普通/文件D.xlsx", "版本/二级/文件E.xlsx" })
                PackageFiles.Add(new() { OriginalPath = path, OriginalRelPath = path, Size = 1 });
            HistoryFilesList.Add(new(new ManifestHistoryEntry { OriginalRel = "普通/文件C.xlsx", ArchivedAt = DateTimeOffset.Now, Reason = "superseded", Size = 1 }));
            HistoryFilesList.Add(new(new ManifestHistoryEntry { OriginalRel = @"普通/\u7248\u672c\u8bf4\u660e.txt", ArchivedAt = DateTimeOffset.Now.AddDays(-1), Reason = "renamed", Size = 1 }));
            BuildBrowser(Array.Empty<string>());
            IsHistoryMode = history;
            if (history)
            {
                var file = _historyRoot.Descendants().First(item => !item.IsFolder);
                OpenFolder(file.Parent!);
                SetHistoryFile(file);
            }
        }

        private async Task<INasStorageProvider> ProviderForPackageAsync(DiscoveredBackupPackage package) =>
            package.NasFolderUnc.StartsWith(@"\\") ? await _connectionService.ConnectTeamShareAsync(package.NasFolderUnc) :
            _connectionService.CurrentStorageProvider ?? throw new IOException("请先连接 NAS。");
        [RelayCommand] public async Task LoadKnownBackupsAsync()
        {
            if (!CanConfigureRestore) return;
            if (!_connectionService.IsConnected) { RestoreStatusText = "请先连接 NAS，再刷新备份。"; return; }
            var epoch = _connectionEpoch;
            IsScanning = true;
            SelectedPackage = null;
            try
            {
                DiscoveredPackages.Clear();
                foreach (var task in _settings.Tasks.Where(t => t.NasUsername == _connectionService.Username &&
                    (!t.NasAccountUid.HasValue || t.NasAccountUid == _connectionService.CurrentUid) &&
                    (string.IsNullOrWhiteSpace(t.NasDeviceId) || _connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider device &&
                        device.MatchesServerAddress(t.NasDeviceId))).ToArray())
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
                        if (epoch != _connectionEpoch) return;
                        foreach (var package in found.Where(p => !DiscoveredPackages.Any(x => x.NasFolderUnc == p.NasFolderUnc))) DiscoveredPackages.Add(package);
                    }
                    catch (Exception ex) { App.LogCrashException(ex, "Restore.LoadKnownBackup"); RestoreLogs.Add("有备份暂时无法读取，请检查连接与权限。"); }
                }
                if (epoch != _connectionEpoch) return;
                SelectedPackage = DiscoveredPackages.FirstOrDefault();
                if (SelectedPackage == null) RestoreStatusText = "未找到可用备份，可浏览其他备份位置。";
            }
            finally { IsScanning = false; }
        }

        [RelayCommand]
        public async Task ScanPackagesAsync()
        {
            if (!CanConfigureRestore) return;
            if (!_connectionService.IsConnected) { RestoreStatusText = "请先连接 NAS。"; return; }
            if (string.IsNullOrWhiteSpace(NasScanRoot))
            {
                RestoreStatusText = "请输入有效的 NAS 扫描根目录。";
                return;
            }

            var epoch = _connectionEpoch;
            IsScanning = true;
            SelectedPackage = null;
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

                if (epoch != _connectionEpoch) return;
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

        partial void OnSelectedPackageChanged(DiscoveredBackupPackage? value) => _ = LoadPackagePreview(value);

        private async Task LoadPackagePreview(DiscoveredBackupPackage? package)
        {
            int generation = ++_previewGeneration;
            _previewCancellation?.Cancel();
            _previewCancellation = null;
            _previewReady = false;
            _previewFailed = false;
            _buildingPreview = package != null;
            _checked.Clear();
            _retryContext = null;
            FailedFilesList.Clear();
            HasFailedFiles = false;
            RestoreProgress = 0;
            RestoredFilesCount = SkippedFilesCount = FailedFilesCount = IntegrityVerifiedCount = 0;
            DetailedErrorMessage = "";
            RestoreStatusText = "";
            PackageFiles.Clear();
            HistoryFilesList.Clear();
            BuildBrowser(Array.Empty<string>());
            if (package == null) return;

            using var cancellation = new CancellationTokenSource();
            _previewCancellation = cancellation;
            try
            {
                var provider = await ProviderForPackageAsync(package);
                var manifest = await _restoreEngine.ReadPackageManifestAsync(provider, package, cancellation.Token);
                if (generation != _previewGeneration || !_connectionService.IsConnected) return;
                if (manifest.IsNew) throw new InvalidDataException("备份已不存在，请刷新。");

                foreach (var history in (manifest.History ?? new List<ManifestHistoryEntry>()).OrderByDescending(item => item.ArchivedAt))
                    HistoryFilesList.Add(new(history));
                foreach (var file in manifest.Files)
                    PackageFiles.Add(new()
                    {
                        OriginalPath = file.OriginalRel,
                        OriginalRelPath = RestoreDisplayText.OriginalPath(file.OriginalRel),
                        StoredRelPath = file.StoredRel,
                        Size = file.Size,
                        IsLongName = file.IsLongName,
                        Sha256 = file.Sha256
                    });
                BuildBrowser(manifest.Directories.Select(item => item.OriginalRel));
                _previewReady = true;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (generation != _previewGeneration) return;
                DetailedErrorMessage = ex.ToString();
                PackageFiles.Clear();
                HistoryFilesList.Clear();
                BuildBrowser(Array.Empty<string>());
                _previewFailed = _connectionService.IsConnected;
            }
            finally
            {
                if (generation == _previewGeneration)
                {
                    _previewCancellation = null;
                    _buildingPreview = false;
                    NotifyPreviewChanged();
                }
            }
        }

        public async Task ExecuteRestoreAsync(bool entireBackup = false)
        {
            if (entireBackup ? !CanRestoreEntireBackup : !CanRestoreSelection) return;
            var files = SelectedFilePaths;
            var directories = SelectedDirectoryPaths;
            if (string.IsNullOrWhiteSpace(LocalRestoreTarget)) { RestoreStatusText = "请选择还原到本地的目标文件夹。"; return; }

            using var cancellation = new CancellationTokenSource();
            _restoreCancellation = cancellation;
            var package = SelectedPackage!;
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
                report = entireBackup
                    ? await _restoreEngine.RunRestorePackageAsync(provider, package, destination, policy, cancellation.Token)
                    : await _restoreEngine.RestorePackageSelectionAsync(provider, package, destination, files, directories, policy, cancellation.Token);

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
            if (!CanRestoreHistory) return;
            if (string.IsNullOrWhiteSpace(LocalRestoreTarget)) { RestoreStatusText = "请选择还原到本地的目标文件夹。"; return; }
            var package = SelectedPackage!;
            var selected = SelectedHistory!;
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
            RestoreLogs.Add($"所选历史版本：{selected.DisplayPath}（{selected.ArchivedAt}）");
            try
            {
                var provider = await ProviderForPackageAsync(package);
                var report = await _restoreEngine.RestorePackageHistoryAsync(provider, package, destination, selected.Entry, policy, cancellation.Token);
                RestoredFilesCount = report.RestoredFiles;
                SkippedFilesCount = report.SkippedFiles;
                FailedFilesCount = report.FailedFiles;
                IntegrityVerifiedCount = report.IntegrityVerifiedFiles;
                RestoreProgress = 100;
                foreach (var item in report.FailedItems) RestoreLogs.Add($"[历史恢复失败] {RestoreDisplayText.OriginalPath(item.OriginalRel)}: {item.ErrorMessage}");
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
