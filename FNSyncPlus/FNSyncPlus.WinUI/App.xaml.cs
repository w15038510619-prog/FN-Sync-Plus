using System;
using System.IO;
using System.Text.RegularExpressions;
using FNSyncPlus.WinUI.Services;
using Microsoft.UI.Xaml;

namespace FNSyncPlus.WinUI
{
    public partial class App : Application
    {
        private Window? _window;
        public static MainWindow? MainWindowInstance { get; private set; }
        public static IntPtr MainWindowHandle => MainWindowInstance != null ? Helpers.WindowHelper.GetHandle(MainWindowInstance) : IntPtr.Zero;

        public App()
        {
            InitializeComponent();

            // 全局崩溃异常捕获防线
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex)
                {
                    LogCrashException(ex, "AppDomain.UnhandledException");
                }
            };

            this.UnhandledException += (s, e) =>
            {
                LogCrashException(e.Exception, "Application.UnhandledException");
                e.Handled = true; // 阻止静默闪退
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                // 初始化依赖注入容器与底层 Core 业务引擎
                AppServices.Initialize();

                var win = new MainWindow();
                MainWindowInstance = win;
                _window = win;
                win.Activate();
            }
            catch (Exception ex)
            {
                LogCrashException(ex, "App.OnLaunched");
                throw;
            }
        }

        public static void LogCrashException(Exception ex, string context)
        {
            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string logDir = Path.Combine(localApp, "FNSyncPlus", "logs");
                Directory.CreateDirectory(logDir);

                string logFile = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd}.log");
                string rawMsg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{context}] {ex}\n";

                // 凭据脱敏
                string sanitized = Regex.Replace(rawMsg, @"(?i)(password|pwd|secret)=([^\s&;]+)", "$1=******");
                File.AppendAllText(logFile, sanitized);
            }
            catch
            {
                // 忽略写入日志自身异常
            }
        }
    }
}
