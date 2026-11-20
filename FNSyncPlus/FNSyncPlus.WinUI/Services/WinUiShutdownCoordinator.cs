using System;
using FNSyncPlus.Core.Updates;

namespace FNSyncPlus.WinUI.Services
{
    /// <summary>
    /// Delivers an update handoff's exit request to the UI thread and runs the SAME safe-exit
    /// sequence as the exit menu: stop accepting new work, wait for running tasks, flush logs,
    /// then really close. A hide-to-tray "close" never intercepts this path, and the scheduler
    /// (a worker thread) never touches UI types directly.
    ///
    /// Manual "install now" and the unattended scheduler both go through this coordinator, so
    /// there is exactly one way for the program to exit for an update.
    /// </summary>
    public sealed class WinUiShutdownCoordinator : IApplicationShutdownCoordinator
    {
        public bool RequestShutdown(string reason)
        {
            var window = App.MainWindowInstance;
            var queue = window?.DispatcherQueue;
            if (window == null || queue == null) return false;
            if (queue.HasThreadAccess)
            {
                window.BeginUpdateShutdown(reason);
                return true;
            }
            return queue.TryEnqueue(() => window.BeginUpdateShutdown(reason));
        }
    }
}
