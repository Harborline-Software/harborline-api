using System.Collections.Generic;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.UpdateFeed.Verify;

namespace Harborline.Api.LocalNodeHost.Feed;

/// <summary>The overall status of a channel check (a stable token the client localizes off, never the English
/// detail — the fleet localizable-code discipline).</summary>
public enum ChannelCheckStatus
{
    /// <summary>The whole feed tree verified against the pinned root; staged artifacts are ready for
    /// <c>/packs/preview</c>.</summary>
    Verified = 0,

    /// <summary>The feed was fetched but FAILED verification (tamper / rollback / freeze-on-online-channel /
    /// revocation-coupling mismatch / unsupported format …). Nothing is staged; the high-water is NOT
    /// advanced.</summary>
    VerificationFailed = 1,

    /// <summary>The channel row exists but has NO local trust pin (F6) — a "proposed channel" that needs the
    /// add-root ceremony (U4) before it can acquire anything.</summary>
    NeedsTrustCeremony = 2,

    /// <summary>No channel with that id is configured on this node.</summary>
    UnknownChannel = 3,

    /// <summary>The channel is configured but disabled — the node does not check it.</summary>
    ChannelDisabled = 4,

    /// <summary>The channel index could not be fetched at all (offline / DNS / connection) — a transport
    /// failure, distinct from a verification failure.</summary>
    ChannelUnreachable = 5,
}

/// <summary>
/// One verified pack whose latest artifact has been staged for the existing <c>/packs/preview</c> engine
/// (update-feed §6.1 — no new install path). The bytes are the FULL content-addressed <c>PackFile</c> the
/// feed verifier already CID- + signature- + revocation-verified; handing them to <c>installer.Preview</c>
/// re-runs the SAME verify-before-effect gate the manual install path uses.
/// </summary>
/// <param name="PackKey">The pack key.</param>
/// <param name="Latest">The channel's declared-current version.</param>
/// <param name="MinNodeEpoch">The schema-epoch floor this version needs (§6.4 — the one-way content→binary
/// gate; a value above the running kernel epoch is the "needs a newer node first" state U3 surfaces).</param>
/// <param name="ArtifactCid">The verified artifact's content-address.</param>
/// <param name="ArtifactBytes">The staged, verified artifact bytes (the <c>PackFile</c> to hand to
/// <c>/packs/preview</c>).</param>
public sealed record StagedFeedPack(
    string PackKey,
    string Latest,
    long MinNodeEpoch,
    Cid ArtifactCid,
    ReadOnlyMemory<byte> ArtifactBytes);

/// <summary>
/// The outcome of an <see cref="IChannelFeedClient.CheckAsync"/> — the status, the verified channel facts
/// (when it got far enough), the staged verified packs (only on <see cref="ChannelCheckStatus.Verified"/>),
/// and the fail-closed findings. On any non-verified status <see cref="StagedPacks"/> is empty — nothing is
/// ever staged from an unverified feed, and the anti-rollback high-water is only advanced on success.
/// </summary>
public sealed class ChannelFeedCheckResult
{
    /// <summary>Constructs a result.</summary>
    public ChannelFeedCheckResult(
        string channelId,
        ChannelCheckStatus status,
        FeedVerifySummary? summary,
        IReadOnlyList<StagedFeedPack> stagedPacks,
        IReadOnlyList<FeedVerifyFinding> failures,
        IReadOnlyList<FeedVerifyFinding> warnings)
    {
        ChannelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        Status = status;
        Summary = summary;
        StagedPacks = stagedPacks ?? throw new ArgumentNullException(nameof(stagedPacks));
        Failures = failures ?? throw new ArgumentNullException(nameof(failures));
        Warnings = warnings ?? throw new ArgumentNullException(nameof(warnings));
    }

    /// <summary>The channel this check ran against.</summary>
    public string ChannelId { get; }

    /// <summary>The overall status token.</summary>
    public ChannelCheckStatus Status { get; }

    /// <summary>True iff the whole feed verified (<see cref="ChannelCheckStatus.Verified"/>).</summary>
    public bool Ok => Status == ChannelCheckStatus.Verified;

    /// <summary>The confirmed channel facts (sequence / validUntil / revoked count / packs), or <c>null</c>
    /// if verification could not summarize (unreachable / malformed / unpinned).</summary>
    public FeedVerifySummary? Summary { get; }

    /// <summary>The staged, verified packs (empty unless <see cref="Ok"/>).</summary>
    public IReadOnlyList<StagedFeedPack> StagedPacks { get; }

    /// <summary>The fatal findings (empty ⇒ verified).</summary>
    public IReadOnlyList<FeedVerifyFinding> Failures { get; }

    /// <summary>Non-fatal findings surfaced honestly (e.g. sideload past validUntil, stale revocation).</summary>
    public IReadOnlyList<FeedVerifyFinding> Warnings { get; }

    private static readonly IReadOnlyList<StagedFeedPack> NoPacks = Array.Empty<StagedFeedPack>();
    private static readonly IReadOnlyList<FeedVerifyFinding> NoFindings = Array.Empty<FeedVerifyFinding>();

    /// <summary>A terminal (pre-verify) status with no staged packs and a single explanatory finding.</summary>
    public static ChannelFeedCheckResult Terminal(string channelId, ChannelCheckStatus status, FeedVerifyFinding finding)
        => new(channelId, status, summary: null, NoPacks, new[] { finding }, NoFindings);
}
