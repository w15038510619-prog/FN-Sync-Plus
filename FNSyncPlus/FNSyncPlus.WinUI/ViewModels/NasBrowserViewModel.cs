using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Utils;

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
        private string _emptyFolderNotice = string.Empty;

        [ObservableProperty]
        private bool _hasWritePermission = true;

        [ObservableProperty]
        private string _permissionStatusText = "● 检查权限中...";

        [ObservableProperty]
        private string _lastErrorMessage = string.Empty;

        [ObservableProperty]
        private bool _isServerRoot = false;

        [ObservableProperty]
        private bool _canNavigateUp = false;

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

                if (Categories.Count == 0)
                {
                    PermissionStatusText = "未在 NAS 检测到可用共享";
                    SecurityTitle = "无可用共享目录";
                    SecurityDescription = "当前 NAS 账号未被授权访问任何 SMB 共享，或共享未开放。请在飞牛 fnOS 控制面板检查共享权限。";
                    SecurityBadgeBg = "#FEE2E2";
                    SecurityBadgeColor = "#B91C1C";
                    HasWritePermission = false;
                    FolderItems.Clear();
                    EmptyFolderNotice = "未在 NAS 检测到可用共享目录";
                    return;
                }

                // 仅当存在已验证的私人空间时才默认选中；如果没有私人空间，绝不自动选择团队共享！
                var personalShare = Categories.FirstOrDefault(s => s.Category == NasShareCategory.PersonalSpace);

                if (personalShare != null)
                {
                    await NavigateToFolderAsync(personalShare.UncPath, personalShare.UncPath);
                }
                else
                {
                    // 处于服务器根目录，要求用户明确选择共享
                    await NavigateToFolderAsync($@"\\{server}", "");
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task NavigateToFolderAsync(string uncPath, string? baseShare = null)
        {
            if (string.IsNullOrWhiteSpace(uncPath)) return;

            var (serverName, shareName, parsedBase, isServerRoot) = PathHelper.ParseUncShare(uncPath);
            if (string.IsNullOrEmpty(baseShare) || isServerRoot)
            {
                baseShare = isServerRoot ? "" : parsedBase;
            }

            CurrentUncPath = uncPath;
            _baseShare = baseShare;
            IsServerRoot = isServerRoot;
            CanNavigateUp = !isServerRoot && !string.IsNullOrEmpty(uncPath);
            UpdateBreadcrumbs(uncPath);

            IsLoading = true;
            try
            {
                if (isServerRoot)
                {
                    // 处于服务器根目录
                    string server = _settings.NasServer.Trim();
                    if (string.IsNullOrEmpty(server)) server = serverName;

                    var categorized = await _smbService.EnumerateCategorizedSharesAsync(server, _settings.Username);
                    _allLoadedItems = categorized.Shares.Select(s => new NasFolderItem
                    {
                        Name = s.Name,
                        FullUncPath = s.UncPath,
                        Category = s.Category,
                        IsShareRoot = true
                    }).ToList();

                    ApplyFilter();
                    HasWritePermission = false;
                    PermissionStatusText = "处于服务器根目录 (不可写)";
                    SecurityTitle = "未发现可访问的私人 SMB 目录";
                    SecurityDescription = "当前 NAS 账号未发现专属私人 SMB 共享。为防止数据扩散，系统未默认选中团队文件共享。请在下方手动选择要使用的共享文件夹。";
                    SecurityBadgeBg = "#FEF3C7";
                    SecurityBadgeColor = "#D97706";
                    IsTeamShare = false;
                    return;
                }

                var subs = await _smbService.ListSubFoldersAsync(uncPath, _baseShare);
                _allLoadedItems = subs;

                ApplyFilter();
                await EvaluatePermissionAndSecurityAsync(uncPath);
            }
            catch (Exception ex)
            {
                var (code, friendlyMsg) = SmbAuthService.TranslateSmbException(ex, uncPath);
                PermissionStatusText = "▲ 访问异常或网络断开";
                SecurityTitle = code == 67 ? "找不到网络共享名" : "网络访问故障";
                SecurityDescription = friendlyMsg;
                SecurityBadgeBg = "#FEE2E2";
                SecurityBadgeColor = "#B91C1C";
                HasWritePermission = false;
                FolderItems.Clear();
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

            EmptyFolderNotice = FolderItems.Count == 0 ? "此目录为空 (暂无子文件夹)" : string.Empty;
        }

        public async Task RefreshCurrentFolderAsync()
        {
            if (string.IsNullOrWhiteSpace(CurrentUncPath))
            {
                await InitializeAsync();
            }
            else
            {
                await NavigateToFolderAsync(CurrentUncPath, _baseShare);
            }
        }

        public async Task NavigateUpAsync()
        {
            if (string.IsNullOrWhiteSpace(CurrentUncPath) || IsServerRoot) return;
            string parent = PathHelper.GetParentUncPath(CurrentUncPath);
            if (!string.IsNullOrEmpty(parent))
            {
                await NavigateToFolderAsync(parent);
            }
        }

        public async Task<bool> CreateSubFolderAsync(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                LastErrorMessage = "文件夹名称不能为空。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(CurrentUncPath) || IsServerRoot || string.IsNullOrEmpty(_baseShare))
            {
                LastErrorMessage = "请先选择并进入具体的 NAS 共享目录后再新建文件夹。无法在服务器根路径创建。";
                SecurityDescription = LastErrorMessage;
                return false;
            }

            if (!HasWritePermission)
            {
                LastErrorMessage = $"新建文件夹失败：当前目录【{CurrentUncPath}】没有写入权限。";
                SecurityDescription = LastErrorMessage;
                return false;
            }

            try
            {
                var created = await _smbService.CreateSubFolderAsync(CurrentUncPath, folderName, _baseShare);
                await NavigateToFolderAsync(CurrentUncPath, _baseShare);
                SelectedFolder = FolderItems.FirstOrDefault(f => string.Equals(f.Name, folderName, StringComparison.OrdinalIgnoreCase));
                LastErrorMessage = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                var (code, friendlyMsg) = SmbAuthService.TranslateSmbException(ex, CurrentUncPath);
                LastErrorMessage = friendlyMsg;
                SecurityDescription = friendlyMsg;
                return false;
            }
        }

        public bool ConfirmSelection()
        {
            string candidate = SelectedFolder != null ? SelectedFolder.FullUncPath : CurrentUncPath;
            if (string.IsNullOrWhiteSpace(candidate)) return false;

            var (server, share, baseShareUnc, isServerRoot) = PathHelper.ParseUncShare(candidate);
            if (isServerRoot || string.IsNullOrEmpty(share))
            {
                return false;
            }

            ConfirmedSelectedPath = candidate;
            return true;
        }
    }
}
