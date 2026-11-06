using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Utils;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace FNSyncPlus.App
{
    public class LocalFolderItem
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
    }

    public class PlannedTaskPreviewItem
    {
        public string TaskName { get; set; } = string.Empty;
        public string SourceFolder { get; set; } = string.Empty;
        public string DestinationFolder { get; set; } = string.Empty;
        public string StatusNote { get; set; } = "就绪";
    }

    public partial class AddFolderWizardDialog : Window
    {
        private readonly AppSettings _settings;
        private readonly SmbAuthService _smbService;
        private readonly TaskManager _taskManager;

        private int _currentStep = 1;
        private readonly ObservableCollection<LocalFolderItem> _selectedFolders = new();
        private readonly List<PlannedTaskPreviewItem> _plannedTasks = new();
        private ShareSecurityEvaluation _currentSecurityEvaluation = new();

        public AddFolderWizardDialog(AppSettings settings, SmbAuthService smbService, TaskManager taskManager)
        {
            InitializeComponent();

            _settings = settings;
            _smbService = smbService;
            _taskManager = taskManager;

            GridSelectedFolders.ItemsSource = _selectedFolders;

            Loaded += AddFolderWizardDialog_Loaded;
        }

        private async void AddFolderWizardDialog_Loaded(object sender, RoutedEventArgs e)
        {
            // 初始化 NAS 备份根目录（优先使用上次记住的根目录，或者自动探测私人空间）
            string candidateRoot = "";
            if (!string.IsNullOrEmpty(_settings.PreferredBackupRoot))
            {
                candidateRoot = _settings.PreferredBackupRoot;
            }
            else if (!string.IsNullOrEmpty(_settings.NasServer))
            {
                try
                {
                    var sharesRes = await _smbService.EnumerateCategorizedSharesAsync(_settings.NasServer, _settings.Username);
                    var personalShare = sharesRes.Shares.FirstOrDefault(s => s.IsPersonal);
                    if (personalShare != null)
                    {
                        candidateRoot = personalShare.UncPath;
                    }
                    // 若无经证明的私人空间，绝不擅自把团队公共共享当作个人空间！
                }
                catch { }
            }

            TxtNasBackupRoot.Text = candidateRoot;
            await UpdateSecurityBadgeAsync(candidateRoot);
        }

        private async System.Threading.Tasks.Task UpdateSecurityBadgeAsync(string rootUnc)
        {
            if (string.IsNullOrWhiteSpace(rootUnc))
            {
                _currentSecurityEvaluation = SmbAuthService.EvaluateShareSecurity(null, _settings.Username);
            }
            else
            {
                var (canWrite, permErr) = await _smbService.TestDirectoryWritePermissionAsync(rootUnc);
                _currentSecurityEvaluation = SmbAuthService.EvaluateShareSecurity(rootUnc, _settings.Username, canWrite, permErr);
            }

            // 更新 UI 样式与文案
            var colorConverter = new System.Windows.Media.BrushConverter();
            var bgBrush = (System.Windows.Media.Brush?)colorConverter.ConvertFromString(_currentSecurityEvaluation.BadgeBgHex) ?? System.Windows.Media.Brushes.Transparent;
            var textBrush = (System.Windows.Media.Brush?)colorConverter.ConvertFromString(_currentSecurityEvaluation.BadgeColorHex) ?? System.Windows.Media.Brushes.Black;

            BorderSecurityNotice.Background = bgBrush;
            TxtSecurityTitle.Text = _currentSecurityEvaluation.Title;
            TxtSecurityTitle.Foreground = textBrush;
            PathSecurityIcon.Fill = textBrush;

            if (_currentSecurityEvaluation.State == NasShareSecurityState.VerifiedPersonalSpace)
            {
                PathSecurityIcon.Data = (System.Windows.Media.Geometry)FindResource("GeoShield");
                TxtSecurityDesc1.Text = "• " + _currentSecurityEvaluation.Description;
                TxtSecurityDesc2.Text = "• 所有选中的本地文件夹将在此私人主目录下自动创建对应子目录，其他团队成员无法查看。";
            }
            else if (_currentSecurityEvaluation.State == NasShareSecurityState.TeamShare)
            {
                PathSecurityIcon.Data = (System.Windows.Media.Geometry)FindResource("GeoShieldAlert");
                TxtSecurityDesc1.Text = "• 警告：当前选中的是团队公共共享文件，团队内其他成员可能拥有查看或修改权限！";
                TxtSecurityDesc2.Text = "• 强烈建议在飞牛 fnOS【文件服务】开启『用户个人主目录』作为专属私人备份。";
            }
            else if (_currentSecurityEvaluation.State == NasShareSecurityState.NoPermission)
            {
                PathSecurityIcon.Data = (System.Windows.Media.Geometry)FindResource("GeoWarning");
                TxtSecurityDesc1.Text = "• " + _currentSecurityEvaluation.Description;
                TxtSecurityDesc2.Text = "• 请在飞牛后台为该账号开通对该目录的写入权限，否则备份将无法执行。";
            }
            else
            {
                PathSecurityIcon.Data = (System.Windows.Media.Geometry)FindResource("GeoInfo");
                TxtSecurityDesc1.Text = "• " + _currentSecurityEvaluation.Description;
                TxtSecurityDesc2.Text = "• 请点击右侧【浏览并选择 NAS 目录】指定备份根目录。";
            }
        }

        #region Step Navigation

        private void UpdateStepUi()
        {
            PageStep1.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
            PageStep2.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
            PageStep3.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;

            BadgeStep1.Background = _currentStep >= 1 ? (System.Windows.Media.Brush)FindResource("PrimaryAccentBrush") : (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240));
            BadgeStep2.Background = _currentStep >= 2 ? (System.Windows.Media.Brush)FindResource("PrimaryAccentBrush") : (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240));
            BadgeStep3.Background = _currentStep >= 3 ? (System.Windows.Media.Brush)FindResource("PrimaryAccentBrush") : (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(226, 232, 240));

            BtnPrevStep.Visibility = _currentStep > 1 ? Visibility.Visible : Visibility.Collapsed;
            BtnNextStep.Visibility = _currentStep < 3 ? Visibility.Visible : Visibility.Collapsed;
            BtnFinishWizard.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;

            switch (_currentStep)
            {
                case 1:
                    TxtStepTitle.Text = "第一步：选择要备份的本地文件夹";
                    TxtStepDescription.Text = "您可以添加一个或多个需要自动保护的本地工作目录（支持拖入文件夹）";
                    break;
                case 2:
                    TxtStepTitle.Text = "第二步：选择 NAS 私人备份根目录";
                    TxtStepDescription.Text = "系统将优先使用私人空间存放备份，所有文件夹将在此根目录下自动创建独立子目录";
                    break;
                case 3:
                    TxtStepTitle.Text = "第三步：确认自动规划的备份任务";
                    TxtStepDescription.Text = "系统已为您完成同名子文件夹规划与重名检查，确认后自动创建任务并开始备份";
                    break;
            }
        }

        private void BtnPrevStep_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep > 1)
            {
                _currentStep--;
                UpdateStepUi();
            }
        }

        private async void BtnNextStep_Click(object sender, RoutedEventArgs e)
        {
            if (_currentStep == 1)
            {
                if (_selectedFolders.Count == 0)
                {
                    WpfMessageBox.Show("请至少添加一个要备份的本地文件夹", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _currentStep = 2;
                UpdateStepUi();
            }
            else if (_currentStep == 2)
            {
                string root = TxtNasBackupRoot.Text.Trim();
                if (string.IsNullOrEmpty(root))
                {
                    WpfMessageBox.Show("请选择或指定一个有效的 NAS 备份根目录", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 检查根目录写入权限
                var (canWrite, permErr) = await _smbService.TestDirectoryWritePermissionAsync(root);
                if (!canWrite)
                {
                    WpfMessageBox.Show($"备份根目录写入权限检查失败:\n{permErr}", "权限警告", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _currentSecurityEvaluation = SmbAuthService.EvaluateShareSecurity(root, _settings.Username, canWrite, permErr);

                // 若选中的是团队公用共享，必须弹出明确警示并获得用户的主动确认！
                if (_currentSecurityEvaluation.State == NasShareSecurityState.TeamShare)
                {
                    var confirm = WpfMessageBox.Show(
                        $"【隐私安全重要提示】\n\n您当前选中的 NAS 备份目标属于【团队共享】：\n{root}\n\n该目录下的所有备份文件可能会被局域网或团队其他成员查看与修改。\n若要保护个人隐私，强烈建议在飞牛 fnOS 管理后台开启『用户个人主目录』。\n\n您是否确认仍要将文件备份到此团队公共共享？",
                        "团队共享隐私确认",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (confirm != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                // 规划第三步的任务预览 (支持同批次同名去重)
                await PlanTasksForStep3Async(root);

                _currentStep = 3;
                UpdateStepUi();
            }
        }

        #endregion

        #region Step 1: 本地文件夹选择与拖拽

        private void BtnBrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "选择要备份的本地文件夹",
                UseDescriptionForTitle = true
            };
            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                AddFolderToList(dialog.SelectedPath);
            }
        }

        private void AddFolderToList(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

            string canonical = PathHelper.GetCanonicalPath(path);
            if (_selectedFolders.Any(f => string.Equals(PathHelper.GetCanonicalPath(f.FullPath), canonical, StringComparison.OrdinalIgnoreCase)))
            {
                return; // 已添加
            }

            // 检查是否与已有任务冲突
            foreach (var t in _taskManager.Tasks)
            {
                if (PathHelper.ArePathsSameOrNested(canonical, t.SourceFolder, out string reason))
                {
                    WpfMessageBox.Show($"文件夹【{path}】与已有任务【{t.TaskName}】冲突:\n{reason}", "冲突提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            string name = Path.GetFileName(canonical.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(name)) name = canonical;

            _selectedFolders.Add(new LocalFolderItem
            {
                Name = name,
                FullPath = canonical
            });

            TxtSelectedFolderCount.Text = $"已添加 {_selectedFolders.Count} 个文件夹";
        }

        private void BtnRemoveFolderItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is LocalFolderItem item)
            {
                _selectedFolders.Remove(item);
                TxtSelectedFolderCount.Text = $"已添加 {_selectedFolders.Count} 个文件夹";
            }
        }

        private void BtnClearSelectedFolders_Click(object sender, RoutedEventArgs e)
        {
            _selectedFolders.Clear();
            TxtSelectedFolderCount.Text = "已添加 0 个文件夹";
        }

        private void GridSelectedFolders_DragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
            }
            else
            {
                e.Effects = System.Windows.DragDropEffects.None;
            }
            e.Handled = true;
        }

        private void GridSelectedFolders_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(System.Windows.DataFormats.FileDrop);
                foreach (var f in files)
                {
                    if (Directory.Exists(f))
                    {
                        AddFolderToList(f);
                    }
                }
            }
        }

        #endregion

        #region Step 2: 浏览 NAS 根目录

        private async void BtnBrowseNasRoot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var browserDlg = new NasBrowserDialog(_settings, _smbService)
                {
                    Owner = this
                };

                if (browserDlg.ShowDialog() == true)
                {
                    TxtNasBackupRoot.Text = browserDlg.SelectedUncPath;
                    await UpdateSecurityBadgeAsync(browserDlg.SelectedUncPath);
                }
            }
            catch (Exception ex)
            {
                App.LogCrashException("OpenNasBrowserFromWizard", ex);
                WpfMessageBox.Show($"打开 NAS 文件夹浏览器失败: {ex.Message}", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        #endregion

        #region Step 3: 规划任务与最终创建

        private async System.Threading.Tasks.Task PlanTasksForStep3Async(string rootUnc)
        {
            _plannedTasks.Clear();

            var allocatedSubNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 包含当前 rootUnc 下已有任务的子目录
            foreach (var t in _taskManager.Tasks)
            {
                if (PathHelper.ArePathsSameOrNested(rootUnc, t.DestinationFolder, out _))
                {
                    string sub = Path.GetRelativePath(rootUnc, t.DestinationFolder);
                    if (!string.IsNullOrEmpty(sub) && sub != ".") allocatedSubNames.Add(sub);
                }
            }

            foreach (var folder in _selectedFolders)
            {
                var (finalSub, isExisting, hasConflict) = await _smbService.PlanTargetSubFolderAsync(
                    rootUnc, 
                    folder.Name, 
                    folder.FullPath,
                    allocatedSubNames);

                allocatedSubNames.Add(finalSub);
                string fullTarget = Path.Combine(rootUnc, finalSub);

                string status = "✔ 正常新目录";
                if (hasConflict)
                {
                    status = "▲ 存在同名，已自动去重";
                }
                else if (isExisting)
                {
                    status = "● 复用已有备份目录";
                }

                _plannedTasks.Add(new PlannedTaskPreviewItem
                {
                    TaskName = folder.Name,
                    SourceFolder = folder.FullPath,
                    DestinationFolder = fullTarget,
                    StatusNote = status
                });
            }

            GridPlannedTasks.ItemsSource = null;
            GridPlannedTasks.ItemsSource = _plannedTasks;

            BtnFinishWizard.Content = $"✔ 完成创建并开始备份 ({_plannedTasks.Count} 个任务)";
        }

        private async void BtnFinishWizard_Click(object sender, RoutedEventArgs e)
        {
            BtnFinishWizard.IsEnabled = false;
            BtnFinishWizard.Content = "⏳ 正在创建并初始化...";

            string rootUnc = TxtNasBackupRoot.Text.Trim();

            try
            {
                // 1. 记住用户的常用备份根目录
                _settings.PreferredBackupRoot = rootUnc;
                _settings.Save();

                // 2. 为每个规划的任务自动建立 NAS 目标子目录并注册任务
                foreach (var planned in _plannedTasks)
                {
                    string targetFolder = planned.DestinationFolder;
                    string longTarget = PathHelper.EnsureLongPathPrefix(targetFolder);

                    // 仅在确认创建任务时在 NAS 上创建该目录，绝不在纯浏览时创建空文件夹！
                    await Task.Run(() =>
                    {
                        if (!Directory.Exists(longTarget))
                        {
                            Directory.CreateDirectory(longTarget);
                        }
                    });

                    var newTask = new SyncTask
                    {
                        TaskName = planned.TaskName,
                        SourceFolder = planned.SourceFolder,
                        NasDeviceId = _settings.NasServer,
                        DestinationRoot = rootUnc,
                        DestinationFolder = targetFolder,
                        RealtimeWatchEnabled = ChkWizardRealtimeWatch.IsChecked == true,
                        AutoSyncEnabled = ChkWizardAutoInterval.IsChecked == true,
                        IntervalMinutes = 5,
                        LastSyncStatus = "排队准备首次备份"
                    };

                    _taskManager.AddTask(newTask);
                    // 立即触发首次同步
                    _taskManager.TriggerSync(newTask.TaskId);
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"创建同步任务失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnFinishWizard.IsEnabled = true;
                BtnFinishWizard.Content = "✔ 完成并立即创建任务";
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        #endregion
    }
}
