using System.Linq;

using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.LocalNodeHost.Feed;

/// <summary>
/// The running node's own schema-epoch counter (update-feed design note §6.4 / UF-4: CIC-ruled a SINGLE
/// node-wide counter, matching <c>HandshakeProtocol.DefaultSchemaVersion</c>'s "one number for the whole
/// binary" shape). Bumped only when the binary's content-facing schema actually changes — the same "= 1"
/// placeholder discipline the sibling <c>OwnRosterEpoch</c> / <c>ChannelEpoch</c> / <c>PublisherEpoch</c>
/// constants already use elsewhere in this app. A staged pack's <see cref="StagedFeedPack.MinNodeEpoch"/>
/// above this value is the ONE honest "you need a newer node first" block (§6.4 — content waits on binary,
/// never the reverse).
/// </summary>
public static class NodeKernelEpoch
{
    /// <summary>The schema-epoch this running binary implements.</summary>
    public const long Current = 1L;
}

/// <summary>
/// The §5.3 "update available" state — a stable token the client localizes off, never an English literal.
/// Derived (never re-signed, never re-verified) from a <see cref="StagedFeedPack"/> already verified by the
/// shared <c>FeedTreeVerifier</c> against the installed state (F5) and its S-8 watermark hits (already
/// computed by the existing install-preview engine — reused, not reimplemented).
/// </summary>
public enum PackUpdateState
{
    /// <summary>The feed's latest matches the installed version exactly — no affordance.</summary>
    UpToDate = 0,

    /// <summary>A newer version is available and installable — the update affordance renders.</summary>
    UpdateAvailable = 1,

    /// <summary>A newer version is available but applying it would weaken an S-8 safety floor — shown
    /// honestly ("needs a review to apply"); only a break-glass ceremony can proceed. Never a silent
    /// click-through.</summary>
    AvailableButBlocked = 2,

    /// <summary>The version's <see cref="StagedFeedPack.MinNodeEpoch"/> exceeds <see cref="NodeKernelEpoch.Current"/>
    /// — blocked pending a binary update (§6.4, one-way plane-2→plane-3 gate). No upgrade path is offered
    /// here (that is release-engineering's, not this surface's).</summary>
    NeedsNewerNode = 3,

    /// <summary>The feed's latest is BELOW the installed version (e.g. after a yank) — never offered; S-8
    /// refuses the downgrade at apply time regardless.</summary>
    Downgrade = 4,

    /// <summary>The pack is in the feed but not installed on this node at all — a feed-only entry, offered
    /// as a fresh install, not an "update."</summary>
    NotInstalled = 5,
}

/// <summary>
/// The §5.3 derivation: "update available" state = feed latest vs installed (F5) vs the S-8 watermark vs
/// the UF-4 node-wide epoch counter. Pure comparison — it stages nothing, verifies nothing (that already
/// happened in <see cref="IChannelFeedClient.CheckAsync"/>), and re-derives no version-compare logic (reuses
/// <see cref="PackVersion.Compare"/> and the watermark hits the existing install-preview engine already
/// computed for the SAME staged bytes — single source, no A4 drift).
/// </summary>
public static class PackUpdateStateDeriver
{
    /// <summary>Derives the §5.3 state for one staged pack, given the installed pack (or null if not
    /// installed) and the EXISTING install-preview already computed over the same staged artifact.</summary>
    public static PackUpdateState Derive(StagedFeedPack pack, InstalledPack? active, PackInstallPreview preview)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(preview);

        // §6.4 — the epoch gate takes priority over every other signal: a node that cannot even run the
        // content is not offered it as "available," regardless of version comparison.
        if (pack.MinNodeEpoch > NodeKernelEpoch.Current)
        {
            return PackUpdateState.NeedsNewerNode;
        }

        if (active is null)
        {
            return PackUpdateState.NotInstalled;
        }

        if (PackVersion.Compare(pack.Latest, active.Version) == 0)
        {
            return PackUpdateState.UpToDate;
        }

        if (preview.WatermarkHits.Any(h => h.Kind == PackWatermarkHitKind.VersionDowngrade))
        {
            return PackUpdateState.Downgrade;
        }

        if (preview.WatermarkHits.Any(h => h.Kind == PackWatermarkHitKind.FloorWeakened))
        {
            return PackUpdateState.AvailableButBlocked;
        }

        return PackUpdateState.UpdateAvailable;
    }
}
