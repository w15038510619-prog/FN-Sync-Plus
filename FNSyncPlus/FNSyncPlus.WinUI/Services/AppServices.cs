using System;
using System.IO;
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
            var settings = IsIsolatedTestSession
                ? AppSettings.CreateInMemory()
                : AppSettings.Load();
            if (IsIsolatedTestSession)
            {
                settings.IsolatedCredentialPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FNSyncPlus", "test-session", "credentials.dat");
                settings.RememberCredentials = File.Exists(settings.IsolatedCredentialPath);
            }
            services.AddSingleton(settings);

            var smbService = new SmbAuthService();
            services.AddSingleton(smbService);

            var longNameEngine = new LongNameEngine(maxUtf8Bytes: 255);
            services.AddSingleton(longNameEngine);

            var manifestManager = new ManifestManager();
            services.AddSingleton(manifestManager);

            var taskManager = new TaskManager(settings, longNameEngine, manifestManager, smbService);
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
    }
}
