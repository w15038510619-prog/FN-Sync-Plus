using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.WinUI.ViewModels
{
    public class BreadcrumbItem
    {
        public string Label { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
    }

    public class CategoryItem
    {
        public string Title { get; set; } = string.Empty;
        public string UncPath { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "\uE8B7";
        public NasShareCategory Category { get; set; }
        public bool IsPersonal { get; set; }
    }

    public partial class NasBrowserViewModel : ObservableObject
    {
        private readonly SmbAuthService _smbService;
        private readonly AppSettings _settings;

        public ObservableCollection<BreadcrumbItem> BreadcrumbItems { get; } = new();
        public ObservableCollection<CategoryItem> Categories { get; } = new();
        public ObservableCollection<NasFolderItem> FolderItems { get; } = new();

        private List<NasFolderItem> _allLoadedItems = new();
        private readonly Stack<string> _history = new();
        private string _baseShare = string.Empty;

        [ObservableProperty]
        private string _currentUncPath = string.Empty;

        [ObservableProperty]
        private NasFolderItem? _selectedFolder;

        [ObservableProperty]
        private string _securityTitle = string.Empty;

        [ObservableProperty]
        private string _securityDescription = string.Empty;

        [ObservableProperty]
        private string _securityBadgeBg = "#F1F5F9";

        [ObservableProperty]
        private string _securityBadgeColor = "#475569";

        [ObservableProperty]
        private bool _isTeamShare = false;

        [ObservableProperty]
        private bool _hasWritePermission = true;

        [ObservableProperty]
        private string _permissionStatusText = "● 检查权限中...";

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private bool _isLoading = false;

        public string ConfirmedSelectedPath { get; private set; } = string.Empty;

        public NasBrowserViewModel(SmbAuthService smbService, AppSettings settings)
        {
            _smbService = smbService;
            _settings = settings;

            _ = InitializeAsync();
        }

        public async Task InitializeAsync()
        {
            string server = _settings.NasServer.Trim();
            if (string.IsNullOrEmpty(server)) return;

            IsLoading = true;
            try
            {
                var categorized = await _smbService.EnumerateCategorizedSharesAsync(server, _settings.Username);

                Categories.Clear();
                foreach (var share in categorized.Shares)
                {
                    bool isPers = share.Category == NasShareCategory.PersonalSpace;
                    Categories.Add(new CategoryItem
                    {
                        Title = share.Name,
                        UncPath = share.UncPath,
                        Category = share.Category,
                        IsPersonal = isPers,
                        IconGlyph = isPers ? "\uE77B" : "\uE716"
                    });
                }

                // 默认选中第一个私人空间，或根路径
                var defaultShare = categorized.Shares.FirstOrDefault(s => s.Category == NasShareCategory.PersonalSpace)
                                   ?? categorized.Shares.FirstOrDefault();

                if (defaultShare != null)
                {
                    await NavigateToFolderAsync(defaultShare.UncPath, defaultShare.UncPath);
                }
                else
                {
                    await NavigateToFolderAsync($@"\\{server}", "");
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task NavigateToFolderAsync(string uncPath, string baseShare)
        {
            CurrentUncPath = uncPath;
            _baseShare = baseShare;
            UpdateBreadcrumbs(uncPath);

            IsLoading = true;
            try
            {
                if (string.IsNullOrEmpty(baseShare) || uncPath.TrimEnd('\\').Count(c => c == '\\') <= 2)
                {
                    // 处于服务器根目录
                    string server = _settings.NasServer.Trim();
                    var categorized = await _smbService.EnumerateCategorizedSharesAsync(server, _settings.Username);
                    _allLoadedItems = categorized.Shares.Select(s => new NasFolderItem
                    {
                        Name = s.Name,
                        FullUncPath = s.UncPath,
                        Category = s.Category,
                        IsShareRoot = true
                    }).ToList();
                }
                else
                {
                    var subs = await _smbService.ListSubFoldersAsync(uncPath, baseShare);
                    _allLoadedItems = subs;
                }

                ApplyFilter();
                await EvaluatePermissionAndSecurityAsync(uncPath);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void UpdateBreadcrumbs(string uncPath)
        {
            BreadcrumbItems.Clear();
            var parts = uncPath.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0) return;

            string accumulated = $@"\\{parts[0]}";
            BreadcrumbItems.Add(new BreadcrumbItem { Label = $@"\\{parts[0]}", Path = accumulated });

            for (int i = 1; i < parts.Length; i++)
            {
                accumulated = Path.Combine(accumulated, parts[i]);
                BreadcrumbItems.Add(new BreadcrumbItem { Label = parts[i], Path = accumulated });
            }
        }

        private async Task EvaluatePermissionAndSecurityAsync(string targetUnc)
        {
            var (canWrite, writeErr) = await _smbService.TestDirectoryWritePermissionAsync(targetUnc);
            HasWritePermission = canWrite;
            PermissionStatusText = canWrite ? "● 具备写入权限" : "▲ 只读无写入权限";

            var eval = SmbAuthService.EvaluateShareSecurity(targetUnc, _settings.Username, canWrite, writeErr);
            SecurityTitle = eval.Title;
            SecurityDescription = eval.Description;
            SecurityBadgeBg = eval.BadgeBgHex;
            SecurityBadgeColor = eval.BadgeColorHex;
            IsTeamShare = eval.State == NasShareSecurityState.TeamShare;
        }

        partial void OnSearchTextChanged(string value)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var query = _allLoadedItems.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                query = query.Where(i => i.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
            }

            FolderItems.Clear();
            foreach (var item in query)
            {
                FolderItems.Add(item);
            }
        }

        public async Task<bool> CreateSubFolderAsync(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrWhiteSpace(CurrentUncPath)) return false;

            try
            {
                var created = await _smbService.CreateSubFolderAsync(CurrentUncPath, folderName, _baseShare);
                await NavigateToFolderAsync(CurrentUncPath, _baseShare);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool ConfirmSelection()
        {
            string candidate = SelectedFolder != null ? SelectedFolder.FullUncPath : CurrentUncPath;
            if (string.IsNullOrWhiteSpace(candidate)) return false;

            ConfirmedSelectedPath = candidate;
            return true;
        }
    }
}
