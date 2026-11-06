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
            InitializeComponent();
        }

        public bool HasNotice(string notice)
        {
            return !string.IsNullOrWhiteSpace(notice);
        }
    }
}
