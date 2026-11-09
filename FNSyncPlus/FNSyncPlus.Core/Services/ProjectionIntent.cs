namespace FNSyncPlus.Core.Services;

// Intent records are immutable and bind a projection attempt to its exact lock owner.
// Older experiments without this binding are intentionally not auto-recoverable.
internal sealed record ProjectionIntent(int Schema, string Kind, string LeaseDirectory, string OwnerSha256,
    ExperimentalPhysicalSnapshot.Result Next, ExperimentalPhysicalSnapshot.Result? Previous = null,
    string? PreviousJournal = null, string? Archive = null);
