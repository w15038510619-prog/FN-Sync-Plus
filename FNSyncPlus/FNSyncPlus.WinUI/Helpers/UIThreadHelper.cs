using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace FNSyncPlus.WinUI.Helpers
{
    /// <summary>
    /// UI 线程调度辅助类：确保所有来自后台异步服务（如连接状态变更、网络心跳、任务进度）的通知
    /// 均在 WinUI 3 的 DispatcherQueue 主线程上安全执行，彻底杜绝跨线程 RPC_E_WRONG_THREAD 崩溃。
    /// </summary>
    public static class UIThreadHelper
    {
        public static DispatcherQueue? DispatcherQueue { get; set; }

        public static void Initialize(DispatcherQueue queue)
        {
            DispatcherQueue = queue;
        }

        public static void RunOnUIThread(Action action)
        {
            if (action == null) return;

            if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    App.LogCrashException(ex, "UIThreadHelper.RunOnUIThread(Direct)");
                }
            }
            else
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        App.LogCrashException(ex, "UIThreadHelper.RunOnUIThread(Enqueue)");
                    }
                });
            }
        }

        public static Task RunOnUIThreadAsync(Func<Task> asyncAction)
        {
            if (asyncAction == null) return Task.CompletedTask;

            if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
            {
                return asyncAction();
            }

            var tcs = new TaskCompletionSource();
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await asyncAction();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    App.LogCrashException(ex, "UIThreadHelper.RunOnUIThreadAsync");
                    tcs.TrySetException(ex);
                }
            });
            return tcs.Task;
        }
    }
}
