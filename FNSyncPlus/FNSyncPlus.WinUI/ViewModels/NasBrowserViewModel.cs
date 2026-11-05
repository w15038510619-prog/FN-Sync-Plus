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
        public string Subtitle { get; set; } = string.Empty;
        public string UncPath { get; set; } = string.Empty;
        public string IconGlyph { get; set; } = "\uE8B7";
        public NasShareCategory Category { get; set; }
        public bool IsPersonal { get; set; }
        public bool IsConfigured { get; set; } = true;
    }

    public partial class NasBrowserViewModel : ObservableObject
    {
        private readonly SmbAuthService _smbService;
        private readonly AppSettings _settings;
        private readonly FnOsIntegrationService _fnOsService;

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

        [ObservableProperty]
        private bool _isPersonalSpaceGuidanceActive = false;

        [ObservableProperty]
        private string _personalSpaceTitle = "未发现可访问的 fnOS 个人专属 SMB 共享";

        [ObservableProperty]
        private string _personalSpaceExplanation = string.Empty;

        [ObservableProperty]
        private string _personalSpaceSetupSteps = string.Empty;

        [ObservableProperty]
        private string _officialClientInfoText = string.Empty;

        [ObservableProperty]
        private string _customPathErrorMessage = string.Empty;

        public string ConfirmedSelectedPath { get; private set; } = string.Empty;

        public NasBrowserViewModel(SmbAuthService smbService, AppSettings settings, FnOsIntegrationService fnOsService)
        {
            _smbService = smbService;
            _settings = settings;
            _fnOsService = fnOsService;

            _ = InitializeAsync();
        }

        public async Task InitializeAsync()
        {
            string server = _settings.NasServer.Trim();
            if (string.IsNullOrEmpty(server)) return;

            IsLoading = true;
            try
            {
                var status = await _fnOsService.EvaluatePersonalSpaceAsync(server, _settings.Username, _settings.PreferredBackupRoot);
                PersonalSpaceTitle = status.DisplayTitle;
                PersonalSpaceExplanation = status.Explanation;
                PersonalSpaceSetupSteps = status.SetupGuidance;
                OfficialClientInfoText = status.OfficialClientInfo?.SummaryText ?? string.Empty;

                var categorized = await _smbService.EnumerateCategorizedSharesAsync(server, _settings.Username);

                Categories.Clear();

                // 1. 首选置顶项：👤 我的文件 (个人空间)
                if (status.HasPersonalSpaceTarget)
                {
                    Categories.Add(new CategoryItem
                    {
                        Title = "👤 我的文件 (个人空间)",
                        Subtitle = "已就绪 · 具备专属读写权限",
                        UncPath = status.PersonalSpaceRootUnc,
                        Category = NasShareCategory.PersonalSpace,
                        IsPersonal = true,
                        IsConfigured = true,
                        IconGlyph = "\uE77B"
                    });
                }
                else
                {
                    Categories.Add(new CategoryItem
                    {
                        Title = "👤 我的文件 (个人空间)",
                        Subtitle = "未发现专属 SMB 共享 · 点击配置",
                        UncPath = "FN_PERSONAL_SPACE_GUIDANCE",
                        Category = NasShareCategory.PersonalSpace,
                        IsPersonal = true,
                        IsConfigured = false,
                        IconGlyph = "\uE77B"
                    });
                }

                // 2. 团队共享与其它共享
                foreach (var share in categorized.Shares)
                {
                    // 若该共享已被指定为个人空间，避免在下方重复
                    if (status.HasPersonalSpaceTarget && string.Equals(share.UncPath, status.PersonalSpaceRootUnc, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool isPers = share.Category == NasShareCategory.PersonalSpace;
                    Categories.Add(new CategoryItem
                    {
                        Title = isPers ? $"👤 {share.Name}" : $"👥 {share.Name}",
                        Subtitle = isPers ? "私人共享" : "团队文件 (公共协作)",
                        UncPath = share.UncPath,
                        Category = share.Category,
                        IsPersonal = isPers,
                        IsConfigured = true,
                        IconGlyph = isPers ? "\uE77B" : "\uE716"
                    });
                }

                // 3. 默认导航策略：
                // 如果个人空间已就绪，优先直接进入个人空间！
                if (status.HasPersonalSpaceTarget)
                {
                    IsPersonalSpaceGuidanceActive = false;
                    await NavigateToFolderAsync(status.PersonalSpaceRootUnc, status.PersonalSpaceRootUnc);
                }
                else
                {
                    // 默认停留在「我的文件 (个人空间)」指引页面，绝不自动使用团队共享！
                    IsPersonalSpaceGuidanceActive = true;
                    CurrentUncPath = "我的文件 (个人空间)";
                    UpdateBreadcrumbs("我的文件 (个人空间)");
                    FolderItems.Clear();
                    HasWritePermission = false;
                    PermissionStatusText = "未配置个人专属共享";
                    SecurityTitle = status.DisplayTitle;
                    SecurityDescription = "为防止私人数据泄露，系统已阻止默认使用团队共享。请按指引开启个人共享或指定路径。";
                    SecurityBadgeBg = "#FEF3C7";
                    SecurityBadgeColor = "#D97706";
                    IsTeamShare = false;
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task SelectCategoryAsync(CategoryItem category)
        {
            if (category == null) return;

            if (category.UncPath == "FN_PERSONAL_SPACE_GUIDANCE")
            {
                IsPersonalSpaceGuidanceActive = true;
                CurrentUncPath = "我的文件 (个人空间)";
                UpdateBreadcrumbs("我的文件 (个人空间)");
                FolderItems.Clear();
                HasWritePermission = false;
                PermissionStatusText = "未配置个人专属共享";
                SecurityTitle = PersonalSpaceTitle;
                SecurityDescription = "请根据右侧指引在飞牛 fnOS 开通个人专属 SMB 共享或手动指定路径。";
                SecurityBadgeBg = "#FEF3C7";
                SecurityBadgeColor = "#D97706";
                IsTeamShare = false;
            }
            else
            {
                IsPersonalSpaceGuidanceActive = false;
                await NavigateToFolderAsync(category.UncPath, category.UncPath);
            }
        }

        public async Task<bool> ApplyCustomPersonalPathAsync(string customPath)
        {
            if (string.IsNullOrWhiteSpace(customPath))
            {
                CustomPathErrorMessage = "路径不能为空。请输入如 \\\\192.168.8.120\\个人备份 的完整 UNC 路径。";
                return false;
            }

            var (server, share, baseShareUnc, isServerRoot) = PathHelper.ParseUncShare(customPath);
            if (isServerRoot || string.IsNullOrEmpty(share))
            {
                CustomPathErrorMessage = "不能直接使用服务器根路径，必须指定具体的共享文件夹。";
                return false;
            }

            var validation = await _smbService.ValidateDirectoryAccessStateAsync(customPath, _settings.NasServer);
            if (!validation.DoesShareExist)
            {
                CustomPathErrorMessage = $"指定的共享不存在 (Win32 Error 67: 找不到网络名)。请确认在飞牛 fnOS 中已开启该共享名称。";
                return false;
            }

            if (!validation.IsDirectoryWritable)
            {
                CustomPathErrorMessage = $"指定路径不可写 (Win32 Error 5: 拒绝访问)。请确认当前账号具备读写权限。";
                return false;
            }

            _settings.PreferredBackupRoot = customPath;
            _settings.Save();
            CustomPathErrorMessage = string.Empty;

            await InitializeAsync();
            return true;
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
            if (IsPersonalSpaceGuidanceActive)
            {
                LastErrorMessage = "当前尚未配置个人专属 SMB 共享，无法作为备份存储位置。请根据指引配置后再确认。";
                return false;
            }

            string candidate = SelectedFolder != null ? SelectedFolder.FullUncPath : CurrentUncPath;
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains("FN_PERSONAL_SPACE_GUIDANCE")) return false;

            var (server, share, baseShareUnc, isServerRoot) = PathHelper.ParseUncShare(candidate);
            if (isServerRoot || string.IsNullOrEmpty(share))
            {
                LastErrorMessage = "无法直接使用服务器根目录，必须选择具体的共享或其子文件夹。";
                return false;
            }

            if (!HasWritePermission)
            {
                LastErrorMessage = "所选目录无写入权限。";
                return false;
            }

            ConfirmedSelectedPath = candidate;
            return true;
        }
    }
}
