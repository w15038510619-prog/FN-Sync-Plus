using System;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI.Dialogs
{
    public sealed partial class TaskSettingsDialog : ContentDialog
    {
        private readonly SyncTask _task;
        private readonly TaskManager _taskManager;

        public bool WasConfigSaved { get; private set; }

        public TaskSettingsDialog(SyncTask task, TaskManager taskManager)
        {
            _task = task ?? throw new ArgumentNullException(nameof(task));
            _taskManager = taskManager ?? throw new ArgumentNullException(nameof(taskManager));

            InitializeComponent();

            PrimaryButtonClick += OnPrimaryButtonClick;

            LoadTaskSettings();
        }

        private void LoadTaskSettings()
        {
            TxtTaskName.Text = _task.TaskName;
            TxtSourcePath.Text = _task.SourceFolder;
            TxtDestPath.Text = _task.DestinationFolder;

            // 1. 调度模式
            int scheduleIdx = _task.ScheduleMode switch
            {
                TaskSyncScheduleMode.SmartAuto => 0,
                TaskSyncScheduleMode.RealtimeOnly => 1,
                TaskSyncScheduleMode.PeriodicOnly => 2,
                TaskSyncScheduleMode.ManualOnly => 3,
                _ => 0
            };
            CmbScheduleMode.SelectedIndex = scheduleIdx;

            // 2. 检查周期
            SelectComboBoxItemByTag(CmbInterval, _task.IntervalMinutes.ToString());
            if (CmbInterval.SelectedIndex < 0) CmbInterval.SelectedIndex = 0;

            // 3. 防抖延迟
            SelectComboBoxItemByTag(CmbSettlingDelay, _task.SettlingDelaySeconds.ToString());
            if (CmbSettlingDelay.SelectedIndex < 0) CmbSettlingDelay.SelectedIndex = 1; // 3s

            // 4. 备份策略
            int stratIdx = _task.Strategy switch
            {
                BackupStrategy.Smart => 0,
                BackupStrategy.Archive => 1,
                BackupStrategy.Mirror => 2,
                _ => 0
            };
            CmbStrategy.SelectedIndex = stratIdx;

            UpdateIntervalPanelVisibility();
        }

        private static void SelectComboBoxItemByTag(ComboBox cmb, string tagValue)
        {
            foreach (var item in cmb.Items)
            {
                if (item is ComboBoxItem cbi && cbi.Tag?.ToString() == tagValue)
                {
                    cmb.SelectedItem = cbi;
                    return;
                }
            }
        }

        private void CmbScheduleMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateIntervalPanelVisibility();
        }

        private void UpdateIntervalPanelVisibility()
        {
            if (PnlInterval == null) return;
            // 智能自动与仅定时模式需要展示检查周期
            bool showInterval = CmbScheduleMode.SelectedIndex == 0 || CmbScheduleMode.SelectedIndex == 2;
            PnlInterval.Visibility = showInterval ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            string newName = TxtTaskName.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(newName))
            {
                args.Cancel = true;
                return;
            }

            _task.TaskName = newName;

            // 调度模式
            _task.ScheduleMode = CmbScheduleMode.SelectedIndex switch
            {
                0 => TaskSyncScheduleMode.SmartAuto,
                1 => TaskSyncScheduleMode.RealtimeOnly,
                2 => TaskSyncScheduleMode.PeriodicOnly,
                3 => TaskSyncScheduleMode.ManualOnly,
                _ => TaskSyncScheduleMode.SmartAuto
            };

            // 周期
            if (CmbInterval.SelectedItem is ComboBoxItem intervalItem && int.TryParse(intervalItem.Tag?.ToString(), out int intervalVal))
            {
                _task.IntervalMinutes = intervalVal;
            }

            // 防抖
            if (CmbSettlingDelay.SelectedItem is ComboBoxItem delayItem && int.TryParse(delayItem.Tag?.ToString(), out int delayVal))
            {
                _task.SettlingDelaySeconds = delayVal;
            }

            // 策略
            _task.Strategy = CmbStrategy.SelectedIndex switch
            {
                0 => BackupStrategy.Smart,
                1 => BackupStrategy.Archive,
                2 => BackupStrategy.Mirror,
                _ => BackupStrategy.Smart
            };

            _taskManager.UpdateTask(_task);
            WasConfigSaved = true;
        }
    }
}
