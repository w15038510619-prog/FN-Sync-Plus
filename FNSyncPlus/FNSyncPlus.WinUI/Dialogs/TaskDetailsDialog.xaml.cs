using System;
using FNSyncPlus.Core.Models;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI.Dialogs
{
    public sealed partial class TaskDetailsDialog : ContentDialog
    {
        public TaskDetailsViewModel ViewModel { get; }

        public TaskDetailsDialog(SyncTask task)
        {
            InitializeComponent();
            ViewModel = AppServices.GetService<TaskDetailsViewModel>();
            _ = ViewModel.LoadTaskAsync(task);
        }
    }
}
