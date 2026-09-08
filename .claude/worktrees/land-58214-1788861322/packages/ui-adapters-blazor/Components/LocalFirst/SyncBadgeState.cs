namespace Harborline.Api.UIAdapters.Blazor.Components.LocalFirst;

/// <summary>
/// Per-record replication states for <see cref="HarborlineSyncStateBadge"/>,
/// mirroring the ui-react SyncStateBadge <c>state</c> union
/// (synced / syncing / pending / error / offline).
/// </summary>
/// <remarks>
/// Distinct from <see cref="SyncState"/> (the Local-Node paper's
/// freshness vocabulary): this enum describes an in-flight replication
/// lifecycle, not staleness thresholds.
/// </remarks>
public enum SyncBadgeState
{
    /// <summary>Local record changes completed their configured replication exchange.</summary>
    Synced,

    /// <summary>Replication in progress.</summary>
    Syncing,

    /// <summary>Local changes queued, not yet sent.</summary>
    Pending,

    /// <summary>Replication failed; user attention required.</summary>
    Error,

    /// <summary>No transport available.</summary>
    Offline,
}
