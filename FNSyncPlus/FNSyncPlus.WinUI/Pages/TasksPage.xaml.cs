using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FNSyncPlus.Core.Services;
using FNSyncPlus.WinUI.Dialogs;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI.Pages
{
    public sealed partial class TasksPage : Page
    {
        public TasksViewModel ViewModel { get; }
        private bool _isDeleting = false;
        private bool _isShowingDetails = false;
        private bool _isEditingSettings = false;
        private bool _isInspectingInterruption = false;
        private bool _isViewingLegacy = false;

        public TasksPage()
        {
            ViewModel = AppServices.GetService<TasksViewModel>();
            InitializeComponent();
            BtnViewLegacyTasks.Visibility = AppServices.GetService<AppSettings>().IsVersionedBetaProfile ? Visibility.Visible : Visibility.Collapsed;

            Loaded += OnPageLoaded;
            Unloaded += OnPageUnloaded;
        }

        private void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            ViewModel.RequestAddFolderWizard -= OpenAddFolderWizardDialog;
            ViewModel.RequestAddFolderWizard += OpenAddFolderWizardDialog;

            ViewModel.RequestTaskDetails -= OpenTaskDetailsDialog;
            ViewModel.RequestTaskDetails += OpenTaskDetailsDialog;

            ViewModel.RequestEditSettings -= OpenTaskSettingsDialog;
            ViewModel.RequestEditSettings += OpenTaskSettingsDialog;

            ViewModel.RequestConfirmDelete -= ConfirmDeleteTask;
            ViewModel.RequestConfirmDelete += ConfirmDeleteTask;
            ViewModel.RequestOperationError -= ShowOperationError;
            ViewModel.RequestOperationError += ShowOperationError;
            ViewModel.RequestInspectInterruption -= InspectInterruptedBackup;
            ViewModel.RequestInspectInterruption += InspectInterruptedBackup;
        }

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            ViewModel.RequestAddFolderWizard -= OpenAddFolderWizardDialog;
            ViewModel.RequestTaskDetails -= OpenTaskDetailsDialog;
            ViewModel.RequestEditSettings -= OpenTaskSettingsDialog;
            ViewModel.RequestConfirmDelete -= ConfirmDeleteTask;
            ViewModel.RequestOperationError -= ShowOperationError;
            ViewModel.RequestInspectInterruption -= InspectInterruptedBackup;
        }

        private async void ShowOperationError(string message)
        {
            var dialog = new ContentDialog
            {
                Title = "任务操作未完成",
                Content = message,
                CloseButtonText = "知道了",
                XamlRoot = this.XamlRoot ?? App.MainWindowInstance?.Content?.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async void BtnViewLegacyTasks_Click(object sender, RoutedEventArgs e)
        {
            if (_isViewingLegacy || XamlRoot == null) return;
            _isViewingLegacy = true;
            try
            {
                var settings = AppServices.GetService<AppSettings>();
                var legacy = await Task.Run(settings.ReadVersionedLegacySettings);
                var text = new StringBuilder();
                if (legacy == null || (legacy.Tasks.Count == 0 && string.IsNullOrWhiteSpace(legacy.LegacySourceFolder) && string.IsNullOrWhiteSpace(legacy.LegacyTargetShare)))
                {
                    text.AppendLine("没有找到本软件的旧任务。若之前使用飞牛官方同步，请在添加文件夹时选择本地目录和飞牛上的已有目录。");
                }
                else
                {
                    text.AppendLine("以下是本软件旧配置中的任务，仅供查看；旧配置没有被修改。");
                    if (!string.IsNullOrWhiteSpace(legacy.LegacySourceFolder) || !string.IsNullOrWhiteSpace(legacy.LegacyTargetShare))
                        text.AppendLine($"\n旧单任务\n本地：{legacy.LegacySourceFolder}\n目标：{legacy.LegacyTargetShare}");
                    foreach (var task in legacy.Tasks.Take(50))
                    {
                        if (task == null) { text.AppendLine("\n有一项旧任务无效，请先检查旧配置。"); continue; }
                        text.AppendLine($"\n{task.TaskName}\n本地：{task.SourceFolder}\n目标：{task.DestinationFolder}");
                    }
                    if (legacy.Tasks.Count > 50) text.AppendLine($"\n另有 {legacy.Tasks.Count - 50} 项未在此列出。");
                }
                text.AppendLine("\n迁移前请停止旧程序和其他可能写入相同目标的设备。选择已有飞牛目录用于复用，并为新版备份选择独立位置；匹配文件会在 NAS 内复制并占用空间。");
                await new ContentDialog { Title = "查看旧任务", Content = new ScrollViewer { MaxHeight = 420,
                    Content = new TextBlock { Text = text.ToString(), TextWrapping = TextWrapping.Wrap } },
                    CloseButtonText = "知道了", XamlRoot = XamlRoot }.ShowAsync();
            }
            catch (Exception ex)
            {
                await new ContentDialog { Title = "无法查看旧任务", Content = ex.Message, CloseButtonText = "知道了", XamlRoot = XamlRoot }.ShowAsync();
            }
            finally { _isViewingLegacy = false; }
        }

        private async void InspectInterruptedBackup(SyncTaskItemViewModel item)
        {
            if (_isInspectingInterruption || !AppServices.IsIsolatedTestSession || item?.Model.IsExperimentalBackup != true || XamlRoot == null) return;
            _isInspectingInterruption = true;
            try
            {
                var manager = AppServices.GetService<TaskManager>();
                await using var session = await manager.BeginIsolatedRecoveryAsync(item.TaskId, TimeSpan.FromSeconds(10));
                item.UpdateVisualState();
                if (session == null)
                {
                    await new ContentDialog { Title = "写入尚未停止", Content = "任务已暂停，但仍有写入操作尚未结束。请稍后再检查；期间不会执行恢复。",
                        CloseButtonText = "知道了", XamlRoot = XamlRoot }.ShowAsync();
                    return;
                }
                var inspection = await session.InspectAsync();
                if (inspection == null)
                {
                    await new ContentDialog { Title = "没有发现中断的发布", Content = "没有发现未释放的备份锁。任务保持暂停，确认后可手动恢复同步。",
                        CloseButtonText = "知道了", XamlRoot = XamlRoot }.ShowAsync();
                    return;
                }
                string stage = inspection.Stage switch
                {
                    ExperimentalProjectionRecoveryInspector.Stage.FirstPrepared => "首次备份的候选目录尚未发布",
                    ExperimentalProjectionRecoveryInspector.Stage.FirstMoved => "首次备份目录已移动，完成记录尚未写入",
                    ExperimentalProjectionRecoveryInspector.Stage.BeforeArchive => "旧目录尚未归档",
                    ExperimentalProjectionRecoveryInspector.Stage.OldArchived => "旧目录已归档，新目录尚未发布",
                    ExperimentalProjectionRecoveryInspector.Stage.NewMoved => "新目录已发布，完成记录尚未写入",
                    ExperimentalProjectionRecoveryInspector.Stage.Committed => "发布记录已完成，但锁尚未释放",
                    _ => "发布状态未知"
                };
                var confirm = new ContentDialog
                {
                    Title = "确认修复中断的隔离备份",
                    Content = $"检查结果：{stage}。\n\n本机此任务已暂停并停止写入。请先确认原同步程序和所有其他可能写入此备份的设备都已停止；软件无法替你判断其他设备是否停止。\n\n确认后会按记录完成目录恢复并释放该锁。任务仍保持暂停，需手动恢复同步。",
                    PrimaryButtonText = "已确认全部停止，修复",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                await session.RecoverAsync(inspection, ExperimentalProjectionRecovery.RequiredAcknowledgement);
                await new ContentDialog { Title = "隔离备份已修复", Content = "目录和记录已核验，任务仍保持暂停。请检查结果后手动恢复同步。",
                    CloseButtonText = "知道了", XamlRoot = XamlRoot }.ShowAsync();
            }
            catch (Exception ex)
            {
                await new ContentDialog { Title = "中断检查或修复未完成", Content = $"请检查任务状态和发布记录，再决定如何处理；不要直接重复修复。\n{ex.Message}",
                    CloseButtonText = "知道了", XamlRoot = XamlRoot }.ShowAsync();
            }
            finally
            {
                item.UpdateVisualState();
                _isInspectingInterruption = false;
            }
        }

        private async void OpenAddFolderWizardDialog()
        {
            var connectionService = AppServices.GetService<FNSyncPlus.Core.Services.NasConnectionService>();
            bool isConnected = connectionService.IsConnected &&
                               connectionService.CurrentStorageProvider is FNSyncPlus.Core.Storage.FnOsPersonalStorageProvider fnProvider &&
                               fnProvider.IsAuthenticated;

            if (isConnected)
            {
                // 已连接：直接进入添加向导，并标记自动弹出文件夹选择窗口
                connectionService.PendingOpenLocalFolderPicker = true;
                connectionService.IsPreservingWizardDraft = false;

                if (App.MainWindowInstance != null)
                {
                    App.MainWindowInstance.NavigateToTag("AddFolder");
                }
                else
                {
                    Frame.Navigate(typeof(AddFolderPage));
                }
            }
            else
            {
                // 尚未连接飞牛官方个人空间，弹出对话框引导用户
                var dialog = new ContentDialog
                {
                    Title = "请先连接飞牛 NAS",
                    Content = "当前尚未连接飞牛官方个人空间。请先登录你的 fnOS 账号，连接成功后即可自动备份到「我的文件」。",
                    PrimaryButtonText = "前往连接",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot ?? App.MainWindowInstance?.Content?.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    connectionService.PendingWizardOnConnected = true;
                    connectionService.PendingOpenLocalFolderPicker = true;
                    connectionService.IsPreservingWizardDraft = false;
                    if (App.MainWindowInstance != null)
                    {
                        App.MainWindowInstance.NavigateToTag("Device");
                    }
                    else
                    {
                        Frame.Navigate(typeof(DevicePage));
                    }
                }
                else
                {
                    connectionService.PendingWizardOnConnected = false;
                    connectionService.PendingOpenLocalFolderPicker = false;
                    connectionService.IsPreservingWizardDraft = false;
                }
            }
        }

        private async void OpenTaskDetailsDialog(SyncTaskItemViewModel taskItem)
        {
            if (_isShowingDetails || taskItem == null) return;
            _isShowingDetails = true;

            try
            {
                if (this.XamlRoot == null) return;

                var dialog = new TaskDetailsDialog(taskItem.Model)
                {
                    XamlRoot = this.XamlRoot
                };

                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TasksPage] 打开任务详情弹窗异常: {ex.Message}");
            }
            finally
            {
                _isShowingDetails = false;
            }
        }

        private async void OpenTaskSettingsDialog(SyncTaskItemViewModel taskItem)
        {
            if (_isEditingSettings || taskItem == null) return;
            _isEditingSettings = true;

            try
            {
                if (this.XamlRoot == null) return;

                var tm = AppServices.GetService<FNSyncPlus.Core.Services.TaskManager>();
                var dialog = new TaskSettingsDialog(taskItem.Model, tm)
                {
                    XamlRoot = this.XamlRoot
                };

                await dialog.ShowAsync();
                taskItem.UpdateVisualState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TasksPage] 打开任务设置弹窗异常: {ex.Message}");
            }
            finally
            {
                _isEditingSettings = false;
            }
        }

        private async void ConfirmDeleteTask(SyncTaskItemViewModel taskItem)
        {
            if (_isDeleting || taskItem == null) return;
            _isDeleting = true;

            try
            {
                if (this.XamlRoot == null) return;

                var confirmDialog = new ContentDialog
                {
                    Title = "确认仅移除任务配置",
                    Content = $"确定要在当前软件中移除任务【{taskItem.TaskName}】的配置吗？\n\n程序会先请求停止当前同步，再移除任务配置。此操作不会主动删除本地源文件或 NAS 备份文件。",
                    PrimaryButtonText = "确认移除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                var res = await confirmDialog.ShowAsync();
                if (res == ContentDialogResult.Primary)
                {
                    var tm = AppServices.GetService<FNSyncPlus.Core.Services.TaskManager>();
                    if (!await tm.RemoveTaskAsync(taskItem.TaskId))
                    {
                        var warning = new ContentDialog
                        {
                            Title = "任务配置未移除",
                            Content = taskItem.Model.LastError ?? "任务可能仍在停止或已被移除，请检查任务状态后重试。",
                            CloseButtonText = "知道了",
                            XamlRoot = this.XamlRoot
                        };
                        await warning.ShowAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TasksPage] 移除任务弹窗或处理异常: {ex.Message}");
                if (this.XamlRoot != null)
                {
                    var error = new ContentDialog
                    {
                        Title = "移除任务失败",
                        Content = $"任务配置未能移除：{ex.Message}",
                        CloseButtonText = "知道了",
                        XamlRoot = this.XamlRoot
                    };
                    await error.ShowAsync();
                }
            }
            finally
            {
                _isDeleting = false;
            }
        }

        private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenAddFolderWizardDialog();
        }

        private void BtnSyncAll_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SyncAllCommand.Execute(null);
        }
    }
}
