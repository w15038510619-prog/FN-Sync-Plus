using System;

namespace FNSyncPlus.Core.Services;

/// <summary>
/// A scoped, owned permission to keep the backup queue paused while an update is prepared.
///
/// The previous design used a bare boolean with a public setter, so any code path could clear a
/// hold it did not own — and every early return in the update preparation leaked the hold, leaving
/// automatic backups permanently paused after a failed update. A lease fixes both problems:
///
/// <list type="bullet">
/// <item><description>Only the owner can release it: the manager compares lease ids.</description></item>
/// <item><description>Release is idempotent: disposing twice, or releasing after a failed handoff,
/// never clears somebody else's hold.</description></item>
/// <item><description>Release is explicit, never implied by "which branch did we take". Callers must
/// either hand the lease to the installation path or release it.</description></item>
/// </list>
/// </summary>
public sealed class MaintenanceLease : IDisposable
{
    private readonly Action<MaintenanceLease> _release;
    private int _released;

    internal MaintenanceLease(string id, string reason, Action<MaintenanceLease> release)
    {
        Id = id;
        Reason = reason;
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    /// <summary>Opaque owner token. Two leases never share an id.</summary>
    public string Id { get; }

    /// <summary>Why the hold was taken; written to the task log for after-the-fact diagnosis.</summary>
    public string Reason { get; }

    public bool IsReleased => Volatile.Read(ref _released) != 0;

    /// <summary>
    /// Releases the hold. Returns true only for the call that actually released it, so a duplicate
    /// release is observable instead of silently succeeding.
    /// </summary>
    public bool Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return false;
        _release(this);
        return true;
    }

    public void Dispose() => Release();
}
