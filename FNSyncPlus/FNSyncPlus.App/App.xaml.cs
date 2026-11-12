using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FNSyncPlus.App
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// 包含全生命周期单例互斥锁、全局未处理异常捕获与无敏感信息崩溃诊断日志
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static Mutex? _appMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 1. 注册全局未处理异常安全防护与诊断日志
            SetupExceptionHandling();

            // 2. 单实例互斥锁（隔离测试数据根目录下使用独立实例标识，绝不占用生产互斥锁）
            string mutexName = "Global\\FNSyncPlus_SingleInstance_Mutex_2026" +
                (FNSyncPlus.Core.Services.AppStorageRoot.SingleInstanceSuffix() ?? "");
            _appMutex = new Mutex(true, mutexName, out bool createdNew);

            if (!createdNew)
            {
                System.Windows.MessageBox.Show("飞牛同步增强版已在运行中，请检查系统托盘区域。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _appMutex?.ReleaseMutex();
                _appMutex?.Dispose();
            }
            catch { }
            base.OnExit(e);
        }

        private void SetupExceptionHandling()
        {
            // UI 线程异常捕获
            DispatcherUnhandledException += (s, args) =>
            {
                LogCrashException("UI_Dispatcher", args.Exception);
                args.Handled = true; // 拦截异常，防止进程闪退
                ShowFriendlyErrorDialog("界面交互异常", args.Exception.Message);
            };

            // 非 UI 线程/后台任务异常捕获
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                if (args.ExceptionObject is Exception ex)
                {
                    LogCrashException("AppDomain_Unhandled", ex);
                }
            };

            // 异步任务未观察异常捕获
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                LogCrashException("TaskScheduler_Unobserved", args.Exception);
                args.SetObserved();
            };
        }

        public static void LogCrashException(string source, Exception ex)
        {
            try
            {
                string logDir = Path.Combine(FNSyncPlus.Core.Services.AppStorageRoot.LocalAppDataDir(), "logs");
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                string logPath = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd}.log");
                string sanitizedMessage = SanitizeSensitiveText(ex.Message);
                string sanitizedStackTrace = SanitizeSensitiveText(ex.StackTrace ?? "");

                string entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] [v2.2.0.0]\n" +
                               $"Type: {ex.GetType().FullName}\n" +
                               $"Message: {sanitizedMessage}\n" +
                               $"Source: {ex.Source}\n" +
                               $"StackTrace:\n{sanitizedStackTrace}\n";

                if (ex.InnerException != null)
                {
                    entry += $"InnerType: {ex.InnerException.GetType().FullName}\n" +
                             $"InnerMessage: {SanitizeSensitiveText(ex.InnerException.Message)}\n" +
                             $"InnerStackTrace:\n{SanitizeSensitiveText(ex.InnerException.StackTrace ?? "")}\n";
                }

                entry += new string('-', 80) + "\n\n";

                File.AppendAllText(logPath, entry);
            }
            catch
            {
                // 日志写入失败绝不二次抛出异常
            }
        }

        public static string SanitizeSensitiveText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            // 过滤密码参数与凭据信息 (例如 password=..., pwd=..., Token=...)
            string sanitized = Regex.Replace(text, @"(password|pwd|secret|token)\s*[:=]\s*[^\s,;]+", "$1=******", RegexOptions.IgnoreCase);
            return sanitized;
        }

        private static void ShowFriendlyErrorDialog(string title, string message)
        {
            try
            {
                System.Windows.MessageBox.Show(
                    $"软件遇到一个非预期的操作错误，已记录到日志，程序已安全拦截保护。\n\n" +
                    $"错误概要: {SanitizeSensitiveText(message)}\n\n" +
                    $"日志位置: %LOCALAPPDATA%\\FNSyncPlus\\logs",
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch { }
        }
    }
}
