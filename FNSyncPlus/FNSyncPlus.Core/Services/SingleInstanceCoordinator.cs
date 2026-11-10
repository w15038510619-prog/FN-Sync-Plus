using System.Diagnostics;
using System.Threading;

namespace FNSyncPlus.Core.Services;

/// <summary>Uses named Windows objects so independently installed copies share one instance.</summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    // Keep the legacy WPF application's mutex name so both desktop versions exclude each other.
    public const string DesktopInstanceName = @"Global\FNSyncPlus_SingleInstance_Mutex_2026";
    private readonly Mutex _marker;
    private readonly EventWaitHandle? _activation;
    private RegisteredWaitHandle? _listener;

    public bool IsPrimary { get; }

    public SingleInstanceCoordinator(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _marker = new Mutex(false, name, out bool created);
        IsPrimary = created;
        if (created)
        {
            try { _activation = new EventWaitHandle(false, EventResetMode.AutoReset, name + ".Activate"); }
            catch { _marker.Dispose(); throw; }
        }
    }

    private string Name { get; }

    public bool NotifyExisting(TimeSpan timeout)
    {
        if (IsPrimary) throw new InvalidOperationException("当前进程是主实例。");
        var timer = Stopwatch.StartNew();
        do
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(Name + ".Activate");
                return signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
        } while (timer.Elapsed < timeout);
        return false;
    }

    public void StartListening(Action activated)
    {
        ArgumentNullException.ThrowIfNull(activated);
        if (!IsPrimary || _activation == null || _listener != null)
            throw new InvalidOperationException("单实例监听状态无效。");
        _listener = ThreadPool.RegisterWaitForSingleObject(_activation, (_, _) => activated(), null, Timeout.Infinite, false);
    }

    public void Dispose()
    {
        _listener?.Unregister(null);
        _activation?.Dispose();
        _marker.Dispose();
    }
}
