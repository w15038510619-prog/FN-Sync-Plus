using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Utils;
using WpfMessageBox = System.Windows.MessageBox;

namespace FNSyncPlus.App
{
    public partial class NasBrowserDialog : Window
    {
        private readonly AppSettings _settings;
        private readonly SmbAuthService _smbService;

        public string SelectedUncPath { get; private set; } = string.Empty;

        private string _currentUnc = "";
        private string _baseShare = "";
        private readonly Stack<string> _history = new();
        private List<NasFolderItem> _allItems = new();
        private bool _isViewReady = false;

        public NasBrowserDialog(AppSettings settings, SmbAuthService smbService)
        {
            InitializeComponent();

            _settings = settings;
            _smbService = smbService;

            Loaded += NasBrowserDialog_Loaded;
        }

        private async void NasBrowserDialog_Loaded(object sender, RoutedEventArgs e)
        {
            _isViewReady = true;

            // 确保在控件已完整创建并加载后，再安全挂载单选过滤器事件
            RadioFilterAll.Checked += (s, ev) => ApplyCurrentFilter();
            RadioFilterPersonal.Checked += (s, ev) => ApplyCurrentFilter();
            RadioFilterTeam.Checked += (s, ev) => ApplyCurrentFilter();
            RadioFilterAll.IsChecked = true;

            string server = _settings.NasServer.Trim();
            if (string.IsNullOrEmpty(server))
            {
                TxtNasBrowserSelectionHint.Text = "未配置 NAS 地址，请先在主界面登录设备。";
                return;
            }

            await LoadSharesAsync(server);
        }

        private async Task LoadSharesAsync(string server)
        {
            _currentUnc = $@"\\{server}";
            _baseShare = "";
            TxtNasBrowserCurrentPath.Text = _currentUnc;
            TxtNasBrowserSelectionHint.Text = "正在获取 NAS 共享列表...";
            SetPermissionBadge(true, "● 共享根目录");

            try
            {
                var result = await _smbService.EnumerateCategorizedSharesAsync(server, _settings.Username);
                _allItems = result.Shares.Select(s => new NasFolderItem
                {
                    Name = s.Name,
                    FullUncPath = s.UncPath,
                    RelativeSubPath = "",
                    IsShareRoot = true,
                    Category = s.Category
                }).ToList();

                if (!result.HasPersonalShare && !string.IsNullOrEmpty(result.GuidanceMessage))
                {
                    BorderPersonalGuidance.Visibility = Visibility.Visible;
                    TxtPersonalGuidance.Text = result.GuidanceMessage;
                }
                else
                {
                    BorderPersonalGuidance.Visibility = Visibility.Collapsed;
                }

                ApplyCurrentFilter();
                TxtNasBrowserSelectionHint.Text = $"找到 {_allItems.Count} 个可用共享。优先推荐选择私人空间目录。";
            }
            catch (Exception ex)
            {
                TxtNasBrowserSelectionHint.Text = $"获取共享失败: {ex.Message}";
                SetPermissionBadge(false, "● 无法连接");
            }
        }

        private void ApplyCurrentFilter()
        {
            if (!_isViewReady || TxtSearchBox == null || ListNasBrowserItems == null)
            {
                return;
            }

            var filtered = _allItems.AsEnumerable();

            if (RadioFilterPersonal?.IsChecked == true)
            {
                filtered = filtered.Where(i => i.Category == NasShareCategory.PersonalSpace);
            }
            else if (RadioFilterTeam?.IsChecked == true)
            {
                filtered = filtered.Where(i => i.Category == NasShareCategory.TeamShare);
            }

            string kw = TxtSearchBox.Text.Trim();
            if (!string.IsNullOrEmpty(kw))
            {
                filtered = filtered.Where(i => i.Name.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                                               i.FullUncPath.Contains(kw, StringComparison.OrdinalIgnoreCase));
            }

            ListNasBrowserItems.ItemsSource = filtered.ToList();
        }

        private void FilterRadio_Checked(object sender, RoutedEventArgs e)
        {
            ApplyCurrentFilter();
        }

        private void TxtSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyCurrentFilter();
        }

        private async void ListNasBrowserItems_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ListNasBrowserItems.SelectedItem is NasFolderItem selected)
            {
                _history.Push(_currentUnc);
                if (string.IsNullOrEmpty(_baseShare))
                {
                    _baseShare = selected.FullUncPath;
                }
                _currentUnc = selected.FullUncPath;
                TxtNasBrowserCurrentPath.Text = _currentUnc;
                await RefreshSubFoldersAsync();
            }
        }

        private async void ListNasBrowserItems_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListNasBrowserItems.SelectedItem is NasFolderItem sel)
            {
                TxtNasBrowserSelectionHint.Text = $"已选中: 【{sel.Name}】。双击进入，或点击【选择当前文件夹】确认。";
                
                try
                {
                    var (canWrite, writeErr) = await _smbService.TestDirectoryWritePermissionAsync(sel.FullUncPath);
                    var sec = SmbAuthService.EvaluateShareSecurity(sel.FullUncPath, _settings.Username, canWrite, writeErr);
                    TxtSecurityNoticeBottom.Text = $"[{sec.Title}] {sec.Description}";
                    TxtSecurityNoticeBottom.Foreground = sec.IsDangerousForPersonalUse 
                        ? new SolidColorBrush(Color.FromRgb(217, 119, 6)) // 警示黄
                        : new SolidColorBrush(Color.FromRgb(15, 118, 110)); // 安全青绿
                }
                catch
                {
                    // 忽略异步探测异常
                }
            }
        }

        private async void BtnNasBrowserUp_Click(object sender, RoutedEventArgs e)
        {
            if (_history.Count > 0)
            {
                _currentUnc = _history.Pop();
                if (_currentUnc.Count(c => c == '\\') <= 2)
                {
                    await LoadSharesAsync(_settings.NasServer.Trim());
                    return;
                }
                TxtNasBrowserCurrentPath.Text = _currentUnc;
                await RefreshSubFoldersAsync();
            }
            else
            {
                await LoadSharesAsync(_settings.NasServer.Trim());
            }
        }

        private async void BtnNasBrowserRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_baseShare))
            {
                await LoadSharesAsync(_settings.NasServer.Trim());
            }
            else
            {
                await RefreshSubFoldersAsync();
            }
        }

        private async Task RefreshSubFoldersAsync()
        {
            TxtNasBrowserSelectionHint.Text = "正在读取文件夹列表...";
            try
            {
                var subs = await _smbService.ListSubFoldersAsync(_currentUnc, _baseShare);
                _allItems = subs;
                ApplyCurrentFilter();

                var (canWrite, _) = await _smbService.TestDirectoryWritePermissionAsync(_currentUnc);
                SetPermissionBadge(canWrite, canWrite ? "● 具备写入权限" : "▲ 只读无写权限");

                TxtNasBrowserSelectionHint.Text = $"当前目录有 {subs.Count} 个子文件夹。可双击深入，或点击【选择当前文件夹】。";
            }
            catch (Exception ex)
            {
                TxtNasBrowserSelectionHint.Text = $"读取目录异常: {ex.Message}";
                SetPermissionBadge(false, "▲ 无法访问");
            }
        }

        private void SetPermissionBadge(bool ok, string text)
        {
            TxtPermissionStatus.Text = text;
            if (ok)
            {
                BadgePermission.Background = new SolidColorBrush(Color.FromRgb(220, 252, 231));
                TxtPermissionStatus.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
            }
            else
            {
                BadgePermission.Background = new SolidColorBrush(Color.FromRgb(254, 226, 226));
                TxtPermissionStatus.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            }
        }

        private async void BtnNasBrowserNewFolder_Click(object sender, RoutedEventArgs e)
        {
            string targetParent;
            if (string.IsNullOrEmpty(_baseShare))
            {
                if (ListNasBrowserItems.SelectedItem is NasFolderItem selShare)
                {
                    _baseShare = selShare.FullUncPath;
                    _currentUnc = selShare.FullUncPath;
                    _history.Push($@"\\{_settings.NasServer.Trim()}");
                    TxtNasBrowserCurrentPath.Text = _currentUnc;
                    targetParent = selShare.FullUncPath;
                }
                else
                {
                    WpfMessageBox.Show("请在列表中先单击选中一个共享目录（如私人空间），即可在其下新建子文件夹。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            else
            {
                targetParent = _currentUnc;
            }

            var (canWrite, permErr) = await _smbService.TestDirectoryWritePermissionAsync(targetParent);
            if (!canWrite)
            {
                WpfMessageBox.Show(permErr, "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new InputFolderDialog { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var created = await _smbService.CreateSubFolderAsync(targetParent, dlg.FolderName, _baseShare);
                    await RefreshSubFoldersAsync();

                    var createdItem = _allItems.FirstOrDefault(i => i.Name.Equals(created.Name, StringComparison.OrdinalIgnoreCase));
                    if (createdItem != null)
                    {
                        ListNasBrowserItems.SelectedItem = createdItem;
                        ListNasBrowserItems.ScrollIntoView(createdItem);
                    }
                }
                catch (Exception ex)
                {
                    WpfMessageBox.Show($"新建文件夹失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void BtnSelectFolder_Click(object sender, RoutedEventArgs e)
        {
            string candidatePath = string.Empty;

            if (string.IsNullOrEmpty(_baseShare))
            {
                if (ListNasBrowserItems.SelectedItem is NasFolderItem sel)
                {
                    candidatePath = sel.FullUncPath;
                }
                else
                {
                    WpfMessageBox.Show("请在列表中选择一个具体的共享文件夹作为备份根目录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
            }
            else
            {
                if (ListNasBrowserItems.SelectedItem is NasFolderItem selSub)
                {
                    candidatePath = selSub.FullUncPath;
                }
                else
                {
                    candidatePath = _currentUnc;
                }
            }

            // 安全检查：如果所选目录为团队共享，进行强提示
            var sec = SmbAuthService.EvaluateShareSecurity(candidatePath, _settings.Username, true, null);
            if (sec.State == NasShareSecurityState.TeamShare)
            {
                var warnRes = WpfMessageBox.Show(
                    $"【隐私安全重要提醒】\n\n您选择的目标目录：\n{candidatePath}\n\n该目录属于【团队共享文件】！NAS 上其他团队成员有权查看、下载此目录中的所有文件。\n\n如果您是要备份个人工作/私人文件，极度建议取消并改用【私人空间】。\n\n是否仍然确认使用此团队共享目录？",
                    "团队共享文件 - 隐私安全确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (warnRes != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            SelectedUncPath = candidatePath;
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
