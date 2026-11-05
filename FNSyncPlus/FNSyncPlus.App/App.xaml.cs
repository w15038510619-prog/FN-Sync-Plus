using System;
using System.Threading;
using System.Windows;

namespace FNSyncPlus.App
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private static Mutex? _appMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            const string mutexName = "Global\\FNSyncPlus_SingleInstance_Mutex_2026";
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
            _appMutex?.ReleaseMutex();
            _appMutex?.Dispose();
            base.OnExit(e);
        }
    }
}
