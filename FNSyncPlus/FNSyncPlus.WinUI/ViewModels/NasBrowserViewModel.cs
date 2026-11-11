using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Storage;
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
        private readonly NasConnectionService _connectionService;

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

        public void ResetDialogSelection()
        {
            SearchText = string.Empty;
            SelectedFolder = null;
            ConfirmedSelectedPath = string.Empty;
        }

        public NasBrowserViewModel(
            SmbAuthService smbService, 
            AppSettings settings, 
            FnOsIntegrationService fnOsService,
            NasConnectionService connectionService)
        {
            _smbService = smbService;
            _settings = settings;
            _fnOsService = fnOsService;
            _connectionService = connectionService;

            _ = InitializeAsync();
        }

        public async Task InitializeAsync()
        {
            string server = _settings.NasServer.Trim();
            if (string.IsNullOrEmpty(server)) return;

            IsLoading = true;
            try
            {
                // 1. 若当前已通过飞牛原生个人空间登录，直接置顶并进入「我的文件」
                if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider && fnProvider.IsAuthenticated)
                {
                    Categories.Clear();
                    Categories.Add(new CategoryItem
                    {
                        Title = "👤 我的文件 (官方个人空间)",
                        Subtitle = $"已就绪 · UID: {fnProvider.CurrentUid} (专属读写)",
                        UncPath = "我的文件",
                        Category = NasShareCategory.PersonalSpace,
                        IsPersonal = true,
                        IsConfigured = true,
                        IconGlyph = "\uE77B"
                    });

                    try
                    {
                        var categorized = await _smbService.EnumerateCategorizedSharesAsync(server, _settings.Username);
                        foreach (var share in categorized.Shares)
                        {
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
                    }
                    catch { }

                    IsPersonalSpaceGuidanceActive = false;
                    await NavigateToFolderAsync("我的文件");
                    return;
                }

                var status = await _fnOsService.EvaluatePersonalSpaceAsync(server, _settings.Username, _settings.PreferredBackupRoot);
                PersonalSpaceTitle = status.DisplayTitle;
                PersonalSpaceExplanation = status.Explanation;
                PersonalSpaceSetupSteps = status.SetupGuidance;
                OfficialClientInfoText = status.OfficialClientInfo?.SummaryText ?? string.Empty;

                var catShares = await _smbService.EnumerateCategorizedSharesAsync(server, _settings.Username);

                Categories.Clear();

                // 首选置顶项：👤 我的文件 (个人空间)
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
                foreach (var share in catShares.Shares)
                {
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

                // 3. 默认导航策略
                if (status.HasPersonalSpaceTarget)
                {
                    IsPersonalSpaceGuidanceActive = false;
                    await NavigateToFolderAsync(status.PersonalSpaceRootUnc, status.PersonalSpaceRootUnc);
                }
                else
                {
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
            else if (category.UncPath == "我的文件")
            {
                IsPersonalSpaceGuidanceActive = false;
                await NavigateToFolderAsync("我的文件");
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
                CustomPathErrorMessage = "路径不能为空。请输入如 \\\\192.168.1.100\\个人备份 的完整 UNC 路径。";
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
            if (!string.Equals(CurrentUncPath, uncPath, StringComparison.OrdinalIgnoreCase))
            {
                SearchText = string.Empty;
                SelectedFolder = null;
            }

            // 1. 飞牛原生个人空间「我的文件」专用导航通道
            if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider && 
                (uncPath == "我的文件" || uncPath.StartsWith("我的文件/") || !uncPath.StartsWith(@"\\")))
            {
                CurrentUncPath = uncPath;
                _baseShare = "我的文件";
                IsServerRoot = false;

                string subPath = uncPath == "我的文件" ? "" : uncPath.Substring("我的文件".Length).TrimStart('/', '\\');
                CanNavigateUp = !string.IsNullOrEmpty(subPath);
                UpdateBreadcrumbs(uncPath);

                IsLoading = true;
                try
                {
                    var entries = await fnProvider.ListEntriesAsync(subPath);
                    _allLoadedItems = entries
                        .Where(e => e.IsDirectory)
                        .Select(e => new NasFolderItem
                        {
                            Name = e.Name,
                            FullUncPath = string.IsNullOrEmpty(subPath) ? $"我的文件/{e.Name}" : $"我的文件/{subPath}/{e.Name}",
                            RelativeSubPath = string.IsNullOrEmpty(subPath) ? e.Name : $"{subPath}/{e.Name}",
                            Category = NasShareCategory.PersonalSpace,
                            IsShareRoot = false
                        })
                        .ToList();

                    ApplyFilter();
                    HasWritePermission = true;
                    PermissionStatusText = "● 飞牛官方原生个人空间 (专属读写)";
                    SecurityTitle = "飞牛官方个人空间 [我的文件]";
                    SecurityDescription = "已直连飞牛官方「我的文件」原生存储，具备独立目录隔离与安全保护。";
                    SecurityBadgeBg = "#DCFCE7";
                    SecurityBadgeColor = "#15803D";
                    IsTeamShare = false;
                    IsPersonalSpaceGuidanceActive = false;
                }
                catch (Exception ex)
                {
                    LastErrorMessage = $"列出个人空间目录异常: {ex.Message}";
                    SecurityDescription = LastErrorMessage;
                    FolderItems.Clear();
                }
                finally
                {
                    IsLoading = false;
                }
                return;
            }

            // 2. 传统 Windows SMB 共享导航通道
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

            if (uncPath.StartsWith("我的文件") || !uncPath.StartsWith(@"\\"))
            {
                string clean = uncPath.Replace('\\', '/').Trim('/');
                var segments = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length == 0)
                {
                    BreadcrumbItems.Add(new BreadcrumbItem { Label = "我的文件", Path = "我的文件" });
                    return;
                }

                string acc = "";
                for (int i = 0; i < segments.Length; i++)
                {
                    acc = i == 0 ? segments[0] : $"{acc}/{segments[i]}";
                    BreadcrumbItems.Add(new BreadcrumbItem { Label = segments[i], Path = acc });
                }
                return;
            }

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

            // 个人空间「我的文件」上级返回
            if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider && 
                (CurrentUncPath.StartsWith("我的文件") || !CurrentUncPath.StartsWith(@"\\")))
            {
                if (CurrentUncPath == "我的文件") return;
                string clean = CurrentUncPath.Replace('\\', '/').TrimEnd('/');
                int lastSlash = clean.LastIndexOf('/');
                string parent = lastSlash > 0 ? clean.Substring(0, lastSlash) : "我的文件";
                await NavigateToFolderAsync(parent);
                return;
            }

            string parentUnc = PathHelper.GetParentUncPath(CurrentUncPath);
            if (!string.IsNullOrEmpty(parentUnc))
            {
                await NavigateToFolderAsync(parentUnc);
            }
        }

        public async Task<bool> CreateSubFolderAsync(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                LastErrorMessage = "文件夹名称不能为空。";
                return false;
            }

            // 1. 飞牛官方「我的文件」原生新建目录通道
            if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider fnProvider && 
                (CurrentUncPath.StartsWith("我的文件") || !CurrentUncPath.StartsWith(@"\\")))
            {
                try
                {
                    string sub = CurrentUncPath == "我的文件" ? folderName : $"{CurrentUncPath.Substring("我的文件".Length).Trim('/')}/{folderName}";
                    var res = await fnProvider.CreateDirectoryAsync(sub);
                    if (res.Success)
                    {
                        await NavigateToFolderAsync(CurrentUncPath);
                        SelectedFolder = FolderItems.FirstOrDefault(f => string.Equals(f.Name, folderName, StringComparison.OrdinalIgnoreCase));
                        LastErrorMessage = string.Empty;
                        return true;
                    }
                    else
                    {
                        LastErrorMessage = $"在飞牛个人空间创建目录失败: {res.ErrorMessage}";
                        SecurityDescription = LastErrorMessage;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    LastErrorMessage = $"创建目录异常: {ex.Message}";
                    SecurityDescription = LastErrorMessage;
                    return false;
                }
            }

            // 2. 传统 SMB 新建目录通道
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

            // 飞牛个人空间直接选定确认
            if (_connectionService.CurrentStorageProvider is FnOsPersonalStorageProvider && 
                (candidate.StartsWith("我的文件") || !candidate.StartsWith(@"\\")))
            {
                ConfirmedSelectedPath = candidate;
                return true;
            }

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
