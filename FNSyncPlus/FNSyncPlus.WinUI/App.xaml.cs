using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FNSyncPlus.Core.Services;
using FNSyncPlus.WinUI.Services;
using Microsoft.UI.Xaml;

namespace FNSyncPlus.WinUI
{
    public partial class App : Application
    {
        private Window? _window;
        private SingleInstanceCoordinator? _singleInstance;
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

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogCrashException(e.Exception, "TaskScheduler.UnobservedTaskException");
                e.SetObserved(); // 标记已观察，防止未捕获后台异步异常引发运行时强制终止
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                _singleInstance = new SingleInstanceCoordinator(SingleInstanceCoordinator.DesktopInstanceName);
                if (!_singleInstance.IsPrimary)
                {
                    bool notified = _singleInstance.NotifyExisting(TimeSpan.FromSeconds(5));
                    _singleInstance.Dispose();
                    _singleInstance = null;
                    if (!notified) ShowStartupMessage("程序已经运行，但无法唤醒原窗口。请从系统托盘打开，或退出旧实例后重试。");
                    Exit();
                    return;
                }

                // 初始化依赖注入容器与底层 Core 业务引擎
                AppServices.Initialize();

                string[] cmdArgs = Environment.GetCommandLineArgs();
                string initialView = "Tasks";
                int wizardStep = 0;

                foreach (var arg in cmdArgs)
                {
                    if (arg.StartsWith("--view=", StringComparison.OrdinalIgnoreCase))
                    {
                        initialView = arg.Substring("--view=".Length);
                    }
                    else if (arg.StartsWith("--wizard-step=", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(arg.Substring("--wizard-step=".Length), out int s))
                        {
                            wizardStep = s;
                        }
                    }
                }

                var win = new MainWindow(initialView, wizardStep);
                MainWindowInstance = win;
                _window = win;

                // 绑定 UI 调度器，确保所有后台通知安全切回主线程
                Helpers.UIThreadHelper.Initialize(win.DispatcherQueue);

                win.Activate();
                _singleInstance.StartListening(() =>
                {
                    try
                    {
                        win.DispatcherQueue.TryEnqueue(() =>
                        {
                            try { win.ShowFromTray(); }
                            catch (Exception ex) { LogCrashException(ex, "App.SingleInstanceActivation"); }
                        });
                    }
                    catch (Exception ex) { LogCrashException(ex, "App.SingleInstanceDispatch"); }
                });
                if (AppServices.IsIsolatedTestSession || AppServices.IsVersionedBetaSession)
                    _ = AppServices.ConnectIsolatedSessionAsync();
                if (Array.Exists(cmdArgs, arg => arg.Equals("--minimize", StringComparison.OrdinalIgnoreCase)))
                    win.HideToTray();
            }
            catch (Exception ex)
            {
                LogCrashException(ex, "App.OnLaunched");
                ShowStartupMessage("软件启动失败或无法确认是否已经运行。请检查系统托盘和日志后重试。");
                Exit();
            }
        }

        internal void ReleaseSingleInstance()
        {
            _singleInstance?.Dispose();
            _singleInstance = null;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

        private static void ShowStartupMessage(string message) => MessageBoxW(IntPtr.Zero, message, "飞牛同步增强版", 0x30);

        public static void LogCrashException(Exception ex, string context)
        {
            try
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string logDir = Path.Combine(localApp, "FNSyncPlus", "logs");
                Directory.CreateDirectory(logDir);

                string logFile = Path.Combine(logDir, $"crash_{DateTime.Now:yyyyMMdd}.log");
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                int pid = Environment.ProcessId;
                int tid = Environment.CurrentManagedThreadId;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("================================================================================");
                sb.AppendLine($"[CRASH LOG] Timestamp: {timestamp} | Version: v0.4.0-beta | PID: {pid} | Thread: {tid}");
                sb.AppendLine($"[STAGE/CONTEXT] {context}");
                sb.AppendLine($"[EXCEPTION TYPE] {ex.GetType().FullName}");
                sb.AppendLine($"[MESSAGE] {ex.Message}");
                if (ex.InnerException != null)
                {
                    sb.AppendLine($"[INNER EXCEPTION] {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                }
                sb.AppendLine($"[STACK TRACE]\n{ex.StackTrace}");
                sb.AppendLine("================================================================================\n");

                string rawMsg = sb.ToString();

                // 敏感凭据多重安全脱敏
                string sanitized = Regex.Replace(rawMsg, @"(?i)(password|pwd|secret|token|accesstoken|longtoken)=([^\s&;]+)", "$1=******");
                File.AppendAllText(logFile, sanitized);
            }
            catch
            {
                // 忽略写入日志自身异常
            }
        }
    }
}
