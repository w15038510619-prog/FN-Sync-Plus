using System;
using System.Collections.Generic;
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
                // Isolated test sessions get their OWN single-instance identity (per session id)
                // so a test instance and a real, possibly production instance can coexist on the
                // same machine: an acceptance run must never be able to wake up or exclude the
                // user's real instance, and vice versa. Production keeps the legacy name so the
                // old WPF build still excludes this one.
                string instanceName = SingleInstanceCoordinator.DesktopInstanceName;
                string[] launchArgs = Environment.GetCommandLineArgs();
                string? stateArg = Array.Find(launchArgs,
                    arg => arg.StartsWith("--isolated-test-state=", StringComparison.Ordinal));
                bool isolatedLaunch = Array.Exists(launchArgs,
                    arg => arg.Equals("--isolated-test-session", StringComparison.Ordinal));
                if (isolatedLaunch)
                {
                    instanceName += ".Isolated." +
                        (stateArg != null ? stateArg["--isolated-test-state=".Length..] : "Unbounded");
                }
                // An isolated test DATA ROOT (FNSYNC_DATA_ROOT) additionally isolates the
                // versioned-beta profile's instance identity: a test-root beta session must
                // never take, signal or block the production single-instance mutex.
                string? rootSuffix = AppStorageRoot.SingleInstanceSuffix();
                if (rootSuffix != null) instanceName += rootSuffix;
                _singleInstance = new SingleInstanceCoordinator(instanceName);
                if (!_singleInstance.IsPrimary)
                {
                    bool notified = _singleInstance.NotifyExisting(TimeSpan.FromSeconds(5));
                    _singleInstance.Dispose();
                    _singleInstance = null;
                    if (!notified) ShowStartupMessage("程序已经运行，但无法唤醒原窗口。请从系统托盘打开，或退出旧实例后重试。");
                    Exit();
                    return;
                }

                // 数据格式守卫：更高版本写入的配置不允许旧程序再写入。
                // 纯视觉验收模式连生产格式标记也不读不写——该模式必须零接触真实数据。
                // (Parsed here from raw args because the guard runs before AppServices.Initialize.)
                string[] startupArgs = Environment.GetCommandLineArgs();
                bool isolatedArg = Array.Exists(startupArgs, arg => arg.Equals("--isolated-test-session", StringComparison.Ordinal));
                bool uiVisualMode = isolatedArg &&
                    Array.Exists(startupArgs, arg => arg.Equals("--ui-visual-acceptance", StringComparison.Ordinal));
                // Test sessions that must not touch production data at all: the pure visual run
                // and the update-handoff probe (which installs into its own sandbox layout).
                bool offlineTestMode = uiVisualMode ||
                    (isolatedArg && Array.Exists(startupArgs, arg => arg.StartsWith("--update-handoff-probe=", StringComparison.Ordinal)));
                if (offlineTestMode)
                {
                    System.Diagnostics.Debug.WriteLine("测试会话：跳过生产数据格式标记检查");
                }
                else if (!EnsureDataFormatCompatible()) return;

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
                bool healthProbe = Array.Exists(cmdArgs,
                    arg => arg.Equals("--update-health-check", StringComparison.OrdinalIgnoreCase));
                MarkLaunchedHealthy();
                StartBackgroundUpdateChecks(cmdArgs);
                if (healthProbe)
                {
                    // The updater starts this process only to learn whether the new payload really
                    // becomes usable. Report first, then leave: a probe that stays alive would race
                    // the real launch for the single-instance mutex.
                    win.DispatcherQueue.TryEnqueue(() =>
                    {
                        try { Exit(); }
                        catch (Exception ex) { LogCrashException(ex, "App.HealthProbeExit"); }
                    });
                }
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
                if (AppServices.IsUiVisualAcceptance ||
                    Array.Exists(cmdArgs, arg => arg.StartsWith("--update-handoff-probe=", StringComparison.Ordinal)))
                {
                    // Pure visual acceptance / update-handoff probe: never read any saved
                    // credentials and never dial any NAS, not even the isolated test-session ones.
                    System.Diagnostics.Debug.WriteLine("测试会话：跳过隔离会话自动连接");
                }
                else if (AppServices.IsIsolatedTestSession || AppServices.IsVersionedBetaSession)
                    _ = AppServices.ConnectIsolatedSessionAsync();
                if (Array.Exists(cmdArgs, arg => arg.Equals("--minimize", StringComparison.OrdinalIgnoreCase)))
                    win.HideToTray();

                // Update-exit probe (test harness only, isolated sessions only): proves the REAL
                // application exits through the REAL shutdown coordinator and safe-exit sequence,
                // the same path an unattended update handoff takes. Never enabled in production.
                string? probeArg = Array.Find(cmdArgs, arg => arg.StartsWith("--update-exit-probe=", StringComparison.OrdinalIgnoreCase));
                if (probeArg != null && AppServices.IsIsolatedTestSession &&
                    int.TryParse(probeArg["--update-exit-probe=".Length..], out int probeDelay) && probeDelay > 0)
                {
                    _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(Math.Min(probeDelay, 120)))
                        .ContinueWith(_ => win.DispatcherQueue.TryEnqueue(() =>
                        {
                            try
                            {
                                AppServices.GetService<FNSyncPlus.Core.Updates.IApplicationShutdownCoordinator>()
                                    .RequestShutdown("更新交接退出探针：验证程序按更新流程安全退出");
                            }
                            catch (Exception ex) { LogCrashException(ex, "App.UpdateExitProbe"); }
                        }));
                }

                // Handoff probe (test harness only, isolated sessions only): performs a REAL
                // update handoff from a pre-signed descriptor — verify, take the maintenance
                // lease, start the real updater, request a safe exit — and records the PIDs and
                // lease state for the acceptance report. This is the same code path the
                // unattended scheduler drives; only the release discovery is supplied by the
                // harness instead of an HTTPS feed.
                string? handoffArg = Array.Find(cmdArgs, arg => arg.StartsWith("--update-handoff-probe=", StringComparison.OrdinalIgnoreCase));
                if (handoffArg != null && AppServices.IsIsolatedTestSession)
                {
                    string descriptorPath = handoffArg["--update-handoff-probe=".Length..].Trim('"');
                    RunHandoffProbe(descriptorPath);
                }
            }
            catch (Exception ex)
            {
                LogCrashException(ex, "App.OnLaunched");
                ShowStartupMessage("软件启动失败或无法确认是否已经运行。请检查系统托盘和日志后重试。");
                Exit();
            }
        }

        /// <summary>
        /// Runs one real handoff and writes its state (PIDs, lease ownership, exit request) to
        /// handoff-probe.json so the acceptance report can quote facts instead of assumptions.
        /// Every step is the production path; only the release comes from the harness.
        /// </summary>
        private static void RunHandoffProbe(string descriptorPath)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var payload = new Dictionary<string, object?>
                {
                    ["app_pid"] = Environment.ProcessId,
                    ["descriptor"] = descriptorPath,
                    ["attempted_at"] = DateTimeOffset.UtcNow,
                };
                try
                {
                    var settings = AppServices.GetService<AppSettings>();
                    var service = AppServices.GetService<UpdateService>();
                    string directory = settings.IsolatedSessionPath != null
                        ? Path.GetDirectoryName(settings.IsolatedSessionPath)!
                        : Path.GetTempPath();
                    string reportPath = Path.Combine(directory, "handoff-probe.json");

                    var (session, error) = await service.BeginHandoffFromDescriptorAsync(
                        descriptorPath, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                    payload["prepared"] = session?.Plan.Ready == true;
                    payload["prepare_error"] = error;
                    if (session == null || !session.Plan.Ready)
                    {
                        payload["session_state"] = session?.State.ToString();
                        WriteProbeReport(reportPath, payload);
                        return;
                    }
                    payload["session_state"] = session.State.ToString();
                    payload["lease_held_after_prepare"] = session.HoldsQueue;
                    payload["target_version"] = session.Release?.Version;

                    if (!service.TryStartUpdater(session, TimeSpan.FromMinutes(2), out string startError))
                    {
                        payload["updater_started"] = false;
                        payload["updater_error"] = startError;
                        payload["session_state"] = session.State.ToString();
                        payload["lease_released_after_failure"] = !session.HoldsQueue;
                        WriteProbeReport(reportPath, payload);
                        return;
                    }
                    payload["updater_started"] = true;
                    payload["updater_pid"] = session.UpdaterProcessId;
                    payload["session_state"] = session.State.ToString();
                    payload["lease_held_after_start"] = session.HoldsQueue;

                    bool delivered = AppServices.GetService<FNSyncPlus.Core.Updates.IApplicationShutdownCoordinator>()
                        .RequestShutdown("更新交接探针：程序将退出并安装更新");
                    payload["shutdown_delivered"] = delivered;
                    if (delivered) session.MarkShutdownRequested();
                    payload["session_state"] = session.State.ToString();
                    payload["lease_held_before_exit"] = session.HoldsQueue;
                    WriteProbeReport(reportPath, payload);
                }
                catch (Exception ex)
                {
                    payload["error"] = $"{ex.GetType().Name}: {ex.Message}";
                    try
                    {
                        var settings = AppServices.GetService<AppSettings>();
                        string directory = settings.IsolatedSessionPath != null
                            ? Path.GetDirectoryName(settings.IsolatedSessionPath)! : Path.GetTempPath();
                        WriteProbeReport(Path.Combine(directory, "handoff-probe.json"), payload);
                    }
                    catch { }
                    LogCrashException(ex, "App.HandoffProbe");
                }
            });
        }

        private static void WriteProbeReport(string path, Dictionary<string, object?> payload)
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }

        /// <summary>
        /// Starts the periodic update check. Never blocks startup: the first check is delayed, and a
        /// broken feed only logs. Skipped for the updater's health probe (a short-lived process that
        /// must not start network work) and for isolated test sessions.
        /// </summary>
        private static void StartBackgroundUpdateChecks(string[] cmdArgs)
        {
            try
            {
                if (AppServices.IsIsolatedTestSession) return;
                if (Array.Exists(cmdArgs, arg => arg.Equals("--update-health-check", StringComparison.OrdinalIgnoreCase))) return;
                AppServices.GetService<UpdateScheduler>().Start();
            }
            catch (Exception ex)
            {
                LogCrashException(ex, "App.StartBackgroundUpdateChecks");
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
            string path = Path.Combine(AppStorageRoot.LocalAppDataDir(), "data-format.json");
            var guard = new DataFormatGuard(path, SupportedDataFormat,
                message => LogService.Default.LogConnection("WARN", "[data-format] " + message));
            if (guard.TryClaim(UpdateService.CurrentVersion.ToString(), out string error)) return true;
            LogCrashException(new InvalidOperationException(error), "App.DataFormatGuard");
            ShowStartupMessage(error);
            return false;
        }

        /// <summary>
        /// Tells the updater that this version really became usable. Only a managed installation
        /// (app-{version} under a root with current.json) takes part in the update protocol, and the
        /// health marker is written only when the startup report says services actually came up:
        /// "the window appeared" is not evidence that backups can run.
        /// </summary>
        private static void MarkLaunchedHealthy()
        {
            try
            {
                string? root = VersionedInstallLayout.TryFindRootFromAppDirectory(AppContext.BaseDirectory);
                if (root == null) return;
                var layout = new VersionedInstallLayout(root);
                if (layout.ActiveVersion is not { } version) return;
                bool usable = WriteLaunchReport(root, version);
                if (!usable)
                {
                    LogCrashException(new InvalidOperationException("启动报告未通过可用性检查，不写入健康标记"),
                        "App.MarkLaunchedHealthy");
                    return;
                }
                new LaunchHealthTracker(root).MarkHealthy(version, Environment.ProcessId);
            }
            catch (Exception ex)
            {
                LogCrashException(ex, "App.MarkLaunchedHealthy");
            }
        }

        /// <summary>
        /// Records what actually worked at startup, not just "the window appeared". Returns whether
        /// the build is genuinely usable: services came up, the task list is readable and the local
        /// data format is readable. NAS connectivity is reported separately because "the program
        /// started" is not "backups work again".
        /// </summary>
        private static bool WriteLaunchReport(string root, string version)
        {
            var report = new Dictionary<string, object?>
            {
                ["version"] = version,
                ["pid"] = Environment.ProcessId,
                ["started_at"] = DateTimeOffset.UtcNow,
                ["supported_data_format"] = SupportedDataFormat
            };
            bool servicesOk = false;
            try
            {
                var manager = AppServices.GetService<TaskManager>();
                var tasks = manager.Tasks;
                report["services_initialized"] = true;
                report["task_manager_ok"] = tasks != null;
                report["nas_online"] = manager.IsNasOnline;
                report["task_count"] = tasks?.Count ?? 0;
                servicesOk = tasks != null;
            }
            catch (Exception ex)
            {
                report["services_initialized"] = false;
                report["task_manager_ok"] = false;
                report["nas_online"] = false;
                report["services_error"] = ex.Message;
            }
            bool formatOk = false;
            try
            {
                string marker = Path.Combine(AppStorageRoot.LocalAppDataDir(), "data-format.json");
                var probe = new DataFormatGuard(marker, SupportedDataFormat).Probe();
                report["config_readable"] = probe.State == FormatMarkerState.Valid;
                report["data_format"] = probe.Format;
                report["format_compatible"] = probe.State == FormatMarkerState.Valid && probe.Format <= SupportedDataFormat;
                report["format_state"] = probe.State.ToString();
                formatOk = probe.State == FormatMarkerState.Valid && probe.Format <= SupportedDataFormat;
            }
            catch (Exception ex)
            {
                report["config_readable"] = false;
                report["format_compatible"] = false;
                report["format_error"] = ex.Message;
            }
            report["usable"] = servicesOk && formatOk;

            string directory = Path.Combine(root, ".health");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"report-{version}.json");
            string temp = path + ".tmp";
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(report,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            return servicesOk && formatOk;
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
                    ? AppServices.IsolatedLogsDirectory ?? Path.Combine(AppStorageRoot.TestSessionsRoot(), "startup-crashes")
                    : Path.Combine(AppStorageRoot.LocalAppDataDir(), "logs");
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
