using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FNSyncPlus.Core.Services;
using FNSyncPlus.Core.Updates;
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

                // 数据格式守卫：更高版本写入的配置不允许旧程序再写入
                if (!EnsureDataFormatCompatible()) return;

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
                MarkLaunchedHealthy();
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

        /// <summary>Configuration format this build understands. Bump only with a migration.</summary>
        private const int SupportedDataFormat = 1;

        /// <summary>
        /// Refuses to run when the local configuration was written by a newer build. Rolling the
        /// binary back must never be allowed to rewrite data it does not understand.
        /// </summary>
        private static bool EnsureDataFormatCompatible()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FNSyncPlus", "data-format.json");
            var guard = new DataFormatGuard(path, SupportedDataFormat);
            if (guard.TryClaim(UpdateService.CurrentVersion.ToString(), out string error)) return true;
            LogCrashException(new InvalidOperationException(error), "App.DataFormatGuard");
            ShowStartupMessage(error);
            return false;
        }

        /// <summary>
        /// Tells the updater that this version really became usable. Only a managed installation
        /// (app-{version} under a root with current.json) takes part in the update protocol.
        /// </summary>
        private static void MarkLaunchedHealthy()
        {
            try
            {
                string? root = VersionedInstallLayout.TryFindRootFromAppDirectory(AppContext.BaseDirectory);
                if (root == null) return;
                var layout = new VersionedInstallLayout(root);
                if (layout.ActiveVersion is { } version)
                    new LaunchHealthTracker(root).MarkHealthy(version, Environment.ProcessId);
            }
            catch (Exception ex)
            {
                LogCrashException(ex, "App.MarkLaunchedHealthy");
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
                bool isolated = Array.Exists(Environment.GetCommandLineArgs(), arg => arg.Equals("--isolated-test-session", StringComparison.Ordinal));
                string logDir = isolated
                    ? AppServices.IsolatedLogsDirectory ?? Path.Combine(Path.GetTempPath(), "FNSyncPlus_CodexTests", "startup-crashes")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FNSyncPlus", "logs");
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
