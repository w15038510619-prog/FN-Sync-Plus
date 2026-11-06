using System;
using System.IO;
using System.Windows;
using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Services;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace FNSyncPlus.App
{
    public partial class TaskLogDialog : Window
    {
        private readonly SyncTask _task;
        private readonly TaskManager _taskManager;

        public TaskLogDialog(SyncTask task, TaskManager taskManager)
        {
            InitializeComponent();

            _task = task;
            _taskManager = taskManager;

            TxtTaskTitle.Text = $"任务日志: {_task.TaskName}";
            TxtTaskPath.Text = $"{_task.SourceFolder} ──► {_task.DestinationFolder}";

            LoadLogs();

            _taskManager.TaskLogEmitted += OnTaskLogEmitted;
            Closed += (s, e) => _taskManager.TaskLogEmitted -= OnTaskLogEmitted;
        }

        private void LoadLogs()
        {
            var logs = _taskManager.GetTaskLogs(_task.TaskId);
            TxtLogOutput.Text = string.Join(Environment.NewLine, logs);
            TxtLogOutput.ScrollToEnd();
        }

        private void OnTaskLogEmitted(SyncTask task, string level, string message)
        {
            if (task.TaskId == _task.TaskId)
            {
                Dispatcher.InvokeAsync(() =>
                {
                    TxtLogOutput.AppendText(message + Environment.NewLine);
                    TxtLogOutput.ScrollToEnd();
                });
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _taskManager.ClearTaskLogs(_task.TaskId);
            TxtLogOutput.Clear();
        }

        private void BtnExportLog_Click(object sender, RoutedEventArgs e)
        {
            using var sfd = new WinForms.SaveFileDialog
            {
                Filter = "日志文本文件 (*.txt)|*.txt",
                FileName = $"FN_Sync_Log_{_task.TaskName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };
            if (sfd.ShowDialog() == WinForms.DialogResult.OK)
            {
                File.WriteAllText(sfd.FileName, TxtLogOutput.Text, System.Text.Encoding.UTF8);
                WpfMessageBox.Show("日志已成功导出！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
