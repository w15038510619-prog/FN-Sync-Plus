using System;
using FNSyncPlus.Core.Services;
using FNSyncPlus.WinUI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FNSyncPlus.WinUI.Services
{
    public static class AppServices
    {
        private static IServiceProvider? _serviceProvider;

        public static IServiceProvider Provider => _serviceProvider 
            ?? throw new InvalidOperationException("AppServices has not been initialized.");

        public static void Initialize()
        {
            var services = new ServiceCollection();

            // 1. Core Services & Data Configuration
            var settings = AppSettings.Load();
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
            services.AddSingleton<AddFolderWizardViewModel>();
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
