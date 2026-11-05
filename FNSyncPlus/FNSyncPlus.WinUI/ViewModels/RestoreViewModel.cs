using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;

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

        public ObservableCollection<DiscoveredBackupPackage> DiscoveredPackages { get; } = new();
        public ObservableCollection<RestoreFileDisplayItem> PackageFiles { get; } = new();

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

        public RestoreViewModel(RestoreEngine restoreEngine, AppSettings settings)
        {
            _restoreEngine = restoreEngine;
            _settings = settings;

            if (!string.IsNullOrEmpty(_settings.PreferredBackupRoot))
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

            try
            {
                var packages = await _restoreEngine.DiscoverBackupPackagesAsync(NasScanRoot.Trim());
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
                RestoreStatusText = $"检索异常: {ex.Message}";
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
            if (pkg == null) return;

            try
            {
                var files = await _restoreEngine.PreviewBackupAsync(pkg.NasFolderUnc);
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
            catch { }
        }

        [RelayCommand]
        public async Task ExecuteRestoreAsync()
        {
            if (SelectedPackage == null || string.IsNullOrWhiteSpace(LocalRestoreTarget)) return;

            IsRestoring = true;
            RestoreProgress = 0;
            RestoreStatusText = "正在执行还原并强校验 SHA-256 完整性...";

            var policy = ConflictPolicyIndex switch
            {
                1 => ConflictResolution.Overwrite,
                2 => ConflictResolution.RenameWithTimestamp,
                _ => ConflictResolution.SkipExisting
            };

            void OnProgress(RestoreProgressReport rep)
            {
                double pct = rep.TotalFiles > 0 ? (double)rep.RestoredFiles / rep.TotalFiles * 100.0 : 0.0;
                RestoreProgress = pct;
                RestoreStatusText = $"正在还原: {rep.CurrentFile} ({pct:F0}%)";
            }

            try
            {
                _restoreEngine.ProgressChanged += OnProgress;

                var report = await _restoreEngine.RunRestoreAsync(SelectedPackage.NasFolderUnc, LocalRestoreTarget, policy);

                RestoreProgress = 100;
                RestoreStatusText = $"还原成功完成！共恢复 {report.RestoredFiles} 个文件，跳过 {report.SkippedFiles} 个，SHA-256 校验通过 {report.IntegrityVerifiedFiles} 个。";
            }
            catch (Exception ex)
            {
                RestoreStatusText = $"还原失败: {ex.Message}";
            }
            finally
            {
                _restoreEngine.ProgressChanged -= OnProgress;
                IsRestoring = false;
            }
        }
    }
}
