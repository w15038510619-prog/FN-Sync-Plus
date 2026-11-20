using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI.Pages
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; }

        public SettingsPage()
        {
            ViewModel = AppServices.GetService<SettingsViewModel>();
            ViewModel.RefreshCapacityTasks();
            InitializeComponent();
            Loaded += LoadLegacyReference;
        }

        private void SettingsScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0)
            {
                SettingsContentPanel.Width = Math.Min(960, e.NewSize.Width);
            }
        }

        private async void LoadLegacyReference(object sender, RoutedEventArgs e)
        {
            Loaded -= LoadLegacyReference;
            var settings = AppServices.GetService<FNSyncPlus.Core.Services.AppSettings>();
            if (!settings.IsVersionedBetaProfile) return;
            try
            {
                var legacy = await Task.Run(settings.ReadVersionedLegacySettings);
                if (legacy == null) return;
                var entries = new List<string>();
                void Add(string? name, string? local, string? target)
                {
                    if (!string.IsNullOrWhiteSpace(local) || !string.IsNullOrWhiteSpace(target))
                        entries.Add($"{name}\n本地　{local}\nNAS　{target}");
                }
                Add("旧版任务", legacy.LegacySourceFolder, legacy.LegacyTargetShare);
                foreach (var task in legacy.Tasks)
                    if (task != null) Add(task.TaskName, task.SourceFolder, task.DestinationFolder);
                if (entries.Count == 0) return;
                LegacyPaths.Text = string.Join("\n\n", entries);
                LegacyPanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { App.LogCrashException(ex, "Settings.LegacyReference"); }
        }

        private async void BtnClearCredentials_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "确认清除已保存的登录凭据？",
                Content = "清除后将删除本地 Windows DPAPI 加密保存的密码记录。已创建的备份任务与 NAS 上的历史文件不受影响，下次连接 NAS 时重新输入账号密码即可。",
                PrimaryButtonText = "确认清除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
                RequestedTheme = this.ActualTheme
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.ClearCredentials();
            }
        }

        private async void BtnInstallUpdateFromSettings_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.PrepareUpdateAsync(async (version, notes) =>
            {
                var body = new StackPanel { Spacing = 8, MaxWidth = 420 };
                body.Children.Add(new TextBlock
                {
                    Text = $"版本 v{version} 已通过签名校验。确认后会等待正在运行的备份和恢复完成，然后自动退出安装并在完成后重启。不会中断正在传输的文件。",
                    TextWrapping = TextWrapping.Wrap
                });
                if (!string.IsNullOrWhiteSpace(notes))
                {
                    body.Children.Add(new TextBlock
                    {
                        Text = notes,
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.85
                    });
                }
                var confirm = new ContentDialog
                {
                    XamlRoot = this.XamlRoot,
                    RequestedTheme = this.ActualTheme,
                    Title = $"确认安装 v{version}",
                    Content = body,
                    PrimaryButtonText = "安装并重启",
                    CloseButtonText = "稍后",
                    DefaultButton = ContentDialogButton.Close
                };
                return await confirm.ShowAsync() == ContentDialogResult.Primary;
            });
        }

        public bool HasNotice(string notice)
        {
            return !string.IsNullOrWhiteSpace(notice);
        }
    }
}
