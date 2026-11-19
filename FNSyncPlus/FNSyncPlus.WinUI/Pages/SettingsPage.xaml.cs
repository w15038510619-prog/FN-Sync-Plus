using System;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
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

        private async void LoadLegacyReference(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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
                LegacyPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            }
            catch (Exception ex) { App.LogCrashException(ex, "Settings.LegacyReference"); }
        }

        public bool HasNotice(string notice)
        {
            return !string.IsNullOrWhiteSpace(notice);
        }
    }
}
