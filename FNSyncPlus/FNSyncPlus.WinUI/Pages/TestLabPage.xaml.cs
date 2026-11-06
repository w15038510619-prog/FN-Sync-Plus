using System;
using FNSyncPlus.WinUI.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FNSyncPlus.WinUI.Pages
{
    public sealed partial class TestLabPage : Page
    {
        public TestLabViewModel ViewModel { get; }

        public TestLabPage()
        {
            ViewModel = AppServices.GetService<TestLabViewModel>();
            InitializeComponent();
        }
    }
}
