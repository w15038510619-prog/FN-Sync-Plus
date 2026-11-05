using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace FNSyncPlus.WinUI.Helpers
{
    public static class WindowHelper
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        public static void SetMicaBackdrop(Window window)
        {
            try
            {
                window.SystemBackdrop = new MicaBackdrop
                {
                    Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base
                };
            }
            catch
            {
                // 系统不支持 Mica 时优雅回退
            }
        }

        public static void ConfigureWindow(Window window, string title, int width = 1200, int height = 780)
        {
            window.Title = title;
            var hWnd = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                // 设置窗口大小与初始位置（居中）
                var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                if (displayArea != null)
                {
                    int x = Math.Max(0, (displayArea.WorkArea.Width - width) / 2);
                    int y = Math.Max(0, (displayArea.WorkArea.Height - height) / 2);
                    appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
                }
                else
                {
                    appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
                }

                // 允许内容延伸至自定义标题栏
                appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
                appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
                appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            }
        }

        public static IntPtr GetHandle(Window window)
        {
            return WindowNative.GetWindowHandle(window);
        }

        public static async System.Threading.Tasks.Task<string?> PickFolderAsync(IntPtr hwnd)
        {
            try
            {
                var folderPicker = new Windows.Storage.Pickers.FolderPicker();
                folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop;
                folderPicker.FileTypeFilter.Add("*");

                if (hwnd != IntPtr.Zero)
                {
                    InitializeWithWindow.Initialize(folderPicker, hwnd);
                }

                var folder = await folderPicker.PickSingleFolderAsync();
                return folder?.Path;
            }
            catch (Exception ex)
            {
                App.LogCrashException(ex, "WindowHelper.PickFolderAsync");
                return null;
            }
        }
    }
}
