using System.Collections.Generic;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.UpdateFeed;
using Harborline.Api.Foundation.UpdateFeed.Serialization;
using Harborline.Api.Foundation.UpdateFeed.Verify;
using Harborline.Api.LocalNodeHost.Data.Packs;

namespace Harborline.Api.LocalNodeHost.Feed;

/// <summary>Stable, localizable finding codes the channel feed client raises for the pre-verify terminal
/// states (the verify-stage codes are <see cref="FeedVerifyCodes"/>).</summary>
public static class ChannelFeedCodes
{
    /// <summary>No channel with the requested id is configured.</summary>
    public const string UnknownChannel = "feed.channel.unknown";

    /// <summary>The channel is configured but disabled.</summary>
    public const string ChannelDisabled = "feed.channel.disabled";

    /// <summary>The channel row exists but has no local trust pin (F6 — needs the add-root ceremony).</summary>
    public const string NeedsTrustCeremony = "feed.channel.needs-trust-ceremony";

    /// <summary>The channel index could not be fetched (offline / transport).</summary>
    public const string ChannelUnreachable = "feed.channel.unreachable";
}

/// <summary>
/// The node-side update-feed client (update-feed design note §5.1 / U2). On an EXPLICIT user-initiated check
/// (the scheduled/auto-check opt-in wiring is U6) it: resolves the channel + its per-node trust pin (F6 — a
/// channel with no local pin is a "proposed channel" that acquires nothing); fetches the feed (HTTP for an
/// online channel, the local tree for a sideload); computes the anti-rollback floor from the durable
/// per-channel high-water AND the build-time first-contact floor (F2); runs the ONE shared
/// <see cref="FeedTreeVerifier"/> (channel-root signature → sequence monotonicity → validUntil per channel
/// type → revocation coupling → per-pack index sigs → CID addressing → pack verify + revocation); and, ONLY
/// on a clean verify, advances the durable high-water and STAGES each verified artifact for the existing
/// <c>/packs/preview</c> engine. Nothing is staged and the high-water is NOT advanced on any failure.
/// </summary>
public interface IChannelFeedClient
{
    /// <summary>Runs an explicit check of <paramref name="channelId"/> for <paramref name="tenant"/>.</summary>
    Task<ChannelFeedCheckResult> CheckAsync(TenantId tenant, string channelId, CancellationToken ct);
}

/// <inheritdoc cref="IChannelFeedClient" />
public sealed class ChannelFeedClient : IChannelFeedClient
{
    /// <summary>The revocation-list staleness horizon surfaced (matches the install path's horizon).</summary>
    public static readonly TimeSpan RevocationMaxAge = TimeSpan.FromDays(30);

    private static readonly IReadOnlyList<StagedFeedPack> NoPacks = Array.Empty<StagedFeedPack>();
    private static readonly IReadOnlyList<FeedVerifyFinding> NoFindings = Array.Empty<FeedVerifyFinding>();

    private readonly IChannelRegistry _registry;
    private readonly HttpFeedFetcher _fetcher;
    private readonly IChannelSequenceStore _sequenceStore;
    private readonly TimeProvider _time;
    private readonly FeedTreeVerifier _verifier;

    /// <summary>Constructs the client over the channel table, the HTTP fetcher, the durable sequence store,
    /// and a clock. The verifier is the shared <see cref="FeedTreeVerifier"/> (U1) over the feed + pack
    /// codecs.</summary>
    public ChannelFeedClient(
        IChannelRegistry registry,
        HttpFeedFetcher fetcher,
        IChannelSequenceStore sequenceStore,
        TimeProvider time)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _sequenceStore = sequenceStore ?? throw new ArgumentNullException(nameof(sequenceStore));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _verifier = new FeedTreeVerifier(new FeedFileCodec(), new PackFileCodec());
    }

    /// <inheritdoc />
    public async Task<ChannelFeedCheckResult> CheckAsync(TenantId tenant, string channelId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var channel = _registry.GetChannel(channelId);
        if (channel is null)
        {
            return ChannelFeedCheckResult.Terminal(channelId, ChannelCheckStatus.UnknownChannel,
                new FeedVerifyFinding(ChannelFeedCodes.UnknownChannel, $"no channel '{channelId}' is configured"));
        }
        if (!channel.Enabled)
        {
            return ChannelFeedCheckResult.Terminal(channelId, ChannelCheckStatus.ChannelDisabled,
                new FeedVerifyFinding(ChannelFeedCodes.ChannelDisabled, $"channel '{channelId}' is disabled"));
        }

        // F6: a channel row with no LOCAL pin conveys config only — it cannot acquire until the per-node
        // add-root ceremony (U4) establishes trust. Sync never conveys the trust decision.
        var pin = _registry.GetPin(channelId);
        if (pin is null)
        {
            return ChannelFeedCheckResult.Terminal(channelId, ChannelCheckStatus.NeedsTrustCeremony,
                new FeedVerifyFinding(ChannelFeedCodes.NeedsTrustCeremony,
                    $"channel '{channelId}' is proposed but not locally trusted — needs the add-root ceremony"));
        }

        // Acquire the feed through the SAME IFeedSource abstraction for both shapes: HTTP for an online
        // channel (the node makes the outbound fetch), the local tree for a sideload. A mirror is just a
        // different online base for the same pinned root (§2.3).
        IFeedSource source = channel.Kind == ChannelKind.Sideload
            ? new DirectoryFeedSource(channel.FeedBase)
            : await _fetcher.FetchAsync(channel.FeedBase, ct).ConfigureAwait(false);

        // Transport check: if the entry document is not even present, this is a reachability failure
        // (offline / wrong base / empty sideload media), distinct from a verification failure.
        if (!source.TryRead(FeedPaths.ChannelJson, out _))
        {
            return ChannelFeedCheckResult.Terminal(channelId, ChannelCheckStatus.ChannelUnreachable,
                new FeedVerifyFinding(ChannelFeedCodes.ChannelUnreachable,
                    $"channel '{channelId}' feed is unreachable (no channel.json at {channel.FeedBase})"));
        }

        // F2 anti-rollback floor: the higher of the durable per-(tenant, channel) high-water and the
        // build-time first-contact floor pinned beside the root. A fresh/restored node (high-water 0) is
        // still protected by the binary floor.
        var highWater = _sequenceStore.GetHighWater(tenant, channelId);
        var minSequence = Math.Max(highWater, pin.MinSequenceFloor);

        var options = new FeedVerifyOptions(channel.Kind, _time.GetUtcNow(), minSequence, RevocationMaxAge);
        var verify = _verifier.Verify(source, pin.Root, options);

        if (!verify.Ok || verify.Summary is null)
        {
            // Fail-closed: nothing staged, high-water NOT advanced (a rolled-back / frozen / tampered feed
            // never advances the fence it is trying to defeat).
            return new ChannelFeedCheckResult(
                channelId, ChannelCheckStatus.VerificationFailed, verify.Summary, NoPacks, verify.Failures, verify.Warnings);
        }

        // Verified: advance the durable high-water (monotonic — a no-op if this sequence is not higher) and
        // stage each verified pack's artifact for the existing preview engine.
        _sequenceStore.AdvanceHighWater(tenant, channelId, verify.Summary.Sequence);

        var staged = new List<StagedFeedPack>(verify.Summary.Packs.Count);
        foreach (var pack in verify.Summary.Packs)
        {
            var artifactPath = FeedPaths.ArtifactPath(pack.PackKey, pack.Latest, pack.ArtifactCid);
            if (source.TryRead(artifactPath, out var bytes))
            {
                staged.Add(new StagedFeedPack(pack.PackKey, pack.Latest, pack.MinNodeEpoch, pack.ArtifactCid, bytes));
            }
        }

        return new ChannelFeedCheckResult(
            channelId, ChannelCheckStatus.Verified, verify.Summary, staged, NoFindings, verify.Warnings);
    }
}
