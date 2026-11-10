using System;
using System.IO;
using System.Threading.Tasks;
using FNSyncPlus.Core.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FNSyncPlus.WinUI.Services
{
    public static class AppServices
    {
        private static IServiceProvider? _serviceProvider;
        public static bool IsIsolatedTestSession { get; private set; }

        public static IServiceProvider Provider => _serviceProvider 
            ?? throw new InvalidOperationException("AppServices has not been initialized.");

        public static void Initialize()
        {
            var services = new ServiceCollection();

            // 1. Core Services & Data Configuration
            IsIsolatedTestSession = Array.Exists(Environment.GetCommandLineArgs(),
                arg => arg.Equals("--isolated-test-session", StringComparison.Ordinal));
            var args = Environment.GetCommandLineArgs();
            string? stateArg = Array.Find(args, arg => arg.StartsWith("--isolated-test-state=", StringComparison.Ordinal));
            string? remoteArg = Array.Find(args, arg => arg.StartsWith("--isolated-test-remote=", StringComparison.Ordinal));
            if (!IsIsolatedTestSession && (stateArg != null || remoteArg != null))
                throw new InvalidOperationException("隔离会话参数只能用于隔离测试模式");
            AppSettings settings;
            if (stateArg != null || remoteArg != null)
            {
                if (stateArg == null || remoteArg == null || !Guid.TryParseExact(stateArg["--isolated-test-state=".Length..], "N", out var id) || id == Guid.Empty)
                    throw new InvalidOperationException("隔离测试会话参数不完整");
                string remoteRoot = remoteArg["--isolated-test-remote=".Length..];
                settings = AppSettings.LoadIsolatedTestSession(id);
                settings.SetIsolatedTestBoundary(remoteRoot);
            }
            else settings = IsIsolatedTestSession ? AppSettings.CreateInMemory() : AppSettings.Load();
            if (IsIsolatedTestSession)
            {
                settings.IsolatedCredentialPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FNSyncPlus", "test-session", "credentials.dat");
                settings.RememberCredentials = File.Exists(settings.IsolatedCredentialPath);
                string productionSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FNSyncPlus", "settings.json");
                settings.TrustedCertThumbprints = AppSettings.ReadTrustedCertificates(productionSettings);
            }
            services.AddSingleton(settings);

            var smbService = new SmbAuthService();
            services.AddSingleton(smbService);

            var longNameEngine = new LongNameEngine(maxUtf8Bytes: 255);
            services.AddSingleton(longNameEngine);

            var manifestManager = new ManifestManager();
            services.AddSingleton(manifestManager);

            var taskManager = new TaskManager(settings, longNameEngine, manifestManager, smbService,
                experimentalVersionedBackup: IsIsolatedTestSession);
            services.AddSingleton(taskManager);

            var connectionService = new NasConnectionService(smbService, settings, taskManager);
            services.AddSingleton(connectionService);

            var restoreEngine = new RestoreEngine(manifestManager);
            services.AddSingleton(restoreEngine);

            var testLabEngine = new TestLabEngine(longNameEngine, manifestManager, smbService);
            services.AddSingleton(testLabEngine);

            var fnOsService = new FnOsIntegrationService(smbService);
            services.AddSingleton(fnOsService);

            // 2. ViewModels
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<TasksViewModel>();
            services.AddTransient<AddFolderWizardViewModel>();
            services.AddSingleton<NasBrowserViewModel>();
            services.AddSingleton<RestoreViewModel>();
            services.AddSingleton<DeviceViewModel>();
            services.AddSingleton<TestLabViewModel>();
            services.AddSingleton<SettingsViewModel>();
            services.AddTransient<TaskDetailsViewModel>();

            _serviceProvider = services.BuildServiceProvider();
        }

        public static T GetService<T>() where T : class
        {
            return Provider.GetRequiredService<T>();
        }

        public static async Task ConnectIsolatedSessionAsync()
        {
            if (!IsIsolatedTestSession) return;
            var settings = GetService<AppSettings>();
            if (!settings.IsInMemoryOnly || string.IsNullOrEmpty(settings.IsolatedCredentialPath)) return;
            var credentials = SmbAuthService.LoadCredentials(settings.IsolatedCredentialPath);
            if (credentials == null) return;
            try
            {
                await GetService<NasConnectionService>().ConnectAsync(credentials.Value.Server, credentials.Value.Username,
                    credentials.Value.Password, rememberCredentials: true);
            }
            catch (Exception)
            {
                System.Diagnostics.Debug.WriteLine("隔离会话自动连接失败，请在界面检查连接状态。");
            }
        }
    }
}
