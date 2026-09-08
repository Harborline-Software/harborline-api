using System.Collections.Generic;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.UpdateFeed;
using Harborline.Api.Foundation.UpdateFeed.Verify;

namespace Harborline.Api.LocalNodeHost.Feed;

/// <summary>
/// One row of the per-instance/org channel table (update-feed design note §3.1) — the CONFIG that names a
/// channel. Under the F6 invariant these config fields (id / display name / feed base / expected-root
/// POINTER / enabled) are the part that would sync as org data; the trust-root PIN is a SEPARATE per-node
/// fact (<see cref="ChannelRootPin"/>) that NEVER syncs. A channel row alone confers no trust — trust exists
/// only when a matching pin is locally present.
/// </summary>
/// <param name="ChannelId">The channel identifier (e.g. <c>harborline-dogfood</c>).</param>
/// <param name="DisplayName">The human label for the channel-management surface (U4).</param>
/// <param name="FeedBase">The feed base — an HTTP(S) base URL when <see cref="Kind"/> is
/// <see cref="ChannelKind.Online"/>, or a local directory path when it is <see cref="ChannelKind.Sideload"/>.
/// A mirror is just a DIFFERENT base for the SAME pinned root (signatures verify vs the root, not the URL —
/// §2.3), so a mirror URL needs no new pin.</param>
/// <param name="Kind">Online (fail-closed past validUntil) vs sideload (expected-stale, no lockout) — F1.</param>
/// <param name="ExpectsRootKeyId">The root this channel EXPECTS (an <c>ed25519:…</c> reference pointer, NOT
/// the trust decision — §3.1/F6). Trust exists only if a <see cref="ChannelRootPin"/> for this channel is
/// locally present AND its key matches.</param>
/// <param name="Enabled">Whether the node will check this channel.</param>
public sealed record NodeChannel(
    string ChannelId,
    string DisplayName,
    string FeedBase,
    ChannelKind Kind,
    string ExpectsRootKeyId,
    bool Enabled);

/// <summary>
/// The PER-NODE trust-root pin for a channel (update-feed design note §3.1/§7.4 / F6). This is the trust
/// decision that a synced config row deliberately does NOT carry: it is established per-node either by the
/// binary pin (the official/dogfood root ships pinned in the signed binary) or by the U4 add-root ceremony —
/// NEVER by sync. A channel with no pin renders as "proposed channel — needs a trust ceremony" and can
/// acquire nothing (the sole root-pinning authority; keeps plane-1 sync free of trust decisions).
/// </summary>
/// <param name="ChannelId">The channel this pin trusts.</param>
/// <param name="Root">The pinned channel root (the key every signed feed document MUST be signed by, plus the
/// publisher epoch for the artifact's own pack verification — §7.4).</param>
/// <param name="MinSequenceFloor">The build-time first-contact anti-rollback floor (F2): a fresh /
/// restored-from-backup node with no persisted high-water refuses any channel index whose <c>sequence</c> is
/// below this. It moves forward with releases; pinned beside the root.</param>
/// <param name="Grade">A provenance label surfaced in code + UX — e.g. <c>dogfood-dev</c> for the U2 dogfood
/// root. The production official root is a human CP signing ceremony (§7.3 / U7), NEVER minted here.</param>
public sealed record ChannelRootPin(
    string ChannelId,
    FeedPinnedRoot Root,
    long MinSequenceFloor,
    string Grade);

/// <summary>
/// The node's channel table + its per-node trust pins (update-feed §3). U2 ships one binary-pinned channel
/// (the dogfood channel) plus whatever sideload channels a caller registers; U4 adds durable CRUD + the
/// add-root ceremony. The F6 separation is structural: <see cref="GetChannel"/> returns config, and
/// <see cref="GetPin"/> returns the SEPARATE per-node trust decision (null ⇒ proposed/needs-ceremony).
/// </summary>
public interface IChannelRegistry
{
    /// <summary>Every configured channel row (config only).</summary>
    IReadOnlyList<NodeChannel> ListChannels();

    /// <summary>The config row for <paramref name="channelId"/>, or <c>null</c> if no such channel.</summary>
    NodeChannel? GetChannel(string channelId);

    /// <summary>The per-node trust pin for <paramref name="channelId"/>, or <c>null</c> if the channel is
    /// present in the table but NOT locally pinned (F6 — "proposed channel, needs a trust ceremony").</summary>
    ChannelRootPin? GetPin(string channelId);
}

/// <summary>
/// The U2 channel registry: an immutable in-binary table. The official/dogfood root ships PINNED IN THE
/// BINARY (via <see cref="DogfoodChannel"/>); additional channels + pins are supplied at construction (tests,
/// and — later — the U4 durable add-root ceremony). Config rows and trust pins are held separately so a
/// channel can exist WITHOUT a pin (F6 proposed-channel state).
/// </summary>
public sealed class BinaryPinnedChannelRegistry : IChannelRegistry
{
    private readonly IReadOnlyList<NodeChannel> _channels;
    private readonly IReadOnlyDictionary<string, ChannelRootPin> _pins;

    /// <summary>Constructs a registry over the given channels and their (optional) per-node pins. A channel
    /// with no entry in <paramref name="pins"/> is a "proposed channel — needs a trust ceremony" (F6).</summary>
    public BinaryPinnedChannelRegistry(IReadOnlyList<NodeChannel> channels, IReadOnlyList<ChannelRootPin> pins)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(pins);
        _channels = channels;
        _pins = pins.ToDictionary(p => p.ChannelId, StringComparer.Ordinal);
    }

    /// <summary>Builds the default node registry: the single binary-pinned dogfood channel (config + pin).</summary>
    public static BinaryPinnedChannelRegistry CreateDefault(string? feedBaseOverride = null)
        => new(
            new[] { DogfoodChannel.Channel(feedBaseOverride) },
            new[] { DogfoodChannel.Pin() });

    /// <inheritdoc />
    public IReadOnlyList<NodeChannel> ListChannels() => _channels;

    /// <inheritdoc />
    public NodeChannel? GetChannel(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        return _channels.FirstOrDefault(c => string.Equals(c.ChannelId, channelId, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public ChannelRootPin? GetPin(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        return _pins.TryGetValue(channelId, out var pin) ? pin : null;
    }
}

/// <summary>
/// The DOGFOOD channel root, PINNED IN THE BINARY (update-feed §3.2/§7.4 — the official/dogfood root ships
/// pinned; trust-on-first-use is not acceptable for the anchor). This is the public key of the DOGFOOD-DEV
/// channel root U1 minted for the dogfood feed — clearly labelled <c>dogfood-dev</c>, NOT the production
/// official root. The production root is a human CP signing ceremony (§7.3 / U7); this build NEVER mints or
/// pins it. The feed base URL is overridable (an env override for the preview host) but the ROOT is fixed —
/// a different root is a different (U4-ceremony) channel, never a config flip.
/// </summary>
public static class DogfoodChannel
{
    /// <summary>The dogfood channel id (matches U1's published <c>channel.json</c>).</summary>
    public const string ChannelId = "harborline-dogfood";

    /// <summary>The DOGFOOD-DEV channel-root public key (base64url), pinned in the binary. Published by U1 as
    /// <c>public/feed/channel-root.pub.json</c> (grade <c>dogfood-dev</c>). NOT the production official
    /// root.</summary>
    public const string RootKeyIdBase64Url = "1S_fuTCz2FPyE-_jo1YDyHeATRjU2UiX23WNX1zXaLs";

    /// <summary>The publisher epoch under which the dogfood packs were signed. In the dogfood first-party
    /// model the publisher IS the channel root, so this is the channel root's epoch (ADR 0126 D4).</summary>
    public const long PublisherEpoch = 1L;

    /// <summary>The build-time first-contact anti-rollback floor (F2) for the dogfood channel — the sequence
    /// baseline this build was cut at. Moves forward with releases. U1's published index is sequence 1.</summary>
    public const long MinSequenceFloor = 1L;

    /// <summary>The provenance grade — surfaced in code + UX so no one mistakes this for the official
    /// root.</summary>
    public const string Grade = "dogfood-dev";

    /// <summary>The eventual www home of the dogfood feed (served at <c>/feed/</c> — U1/U7). Overridable via
    /// <see cref="FeedBaseEnvVar"/> for the preview host / an air-gapped mirror; the ROOT never changes with
    /// the URL (a mirror is a dumb copy — §2.3).</summary>
    public const string DefaultFeedBaseUrl = "https://www.harborline.software/feed/";

    /// <summary>Environment variable that overrides the dogfood feed base URL (e.g. the winhub preview host)
    /// without changing the pinned root.</summary>
    public const string FeedBaseEnvVar = "HARBORLINE_DOGFOOD_FEED_BASE_URL";

    /// <summary>The pre-rename spelling of <see cref="FeedBaseEnvVar"/>. Still honoured (new name wins) so an
    /// operator script or preview host that sets the old name keeps working for one release; delete after that.
    /// </summary>
    public const string LegacyFeedBaseEnvVar = "SHIPYARD_DOGFOOD_FEED_BASE_URL";

    /// <summary>The pinned root, as a <see cref="FeedPinnedRoot"/> for the feed verifier.</summary>
    public static FeedPinnedRoot PinnedRoot()
        => new(PrincipalId.FromBase64Url(RootKeyIdBase64Url), PublisherEpoch);

    /// <summary>The per-node trust pin for the dogfood channel (binary-pinned; grade <c>dogfood-dev</c>).</summary>
    public static ChannelRootPin Pin() => new(ChannelId, PinnedRoot(), MinSequenceFloor, Grade);

    /// <summary>The dogfood channel's config row. <paramref name="feedBaseOverride"/> (or the
    /// <see cref="FeedBaseEnvVar"/> env var, or <see cref="DefaultFeedBaseUrl"/>) sets the base; the root is
    /// fixed.</summary>
    public static NodeChannel Channel(string? feedBaseOverride = null)
    {
        var feedBase = feedBaseOverride
            ?? Environment.GetEnvironmentVariable(FeedBaseEnvVar)
            ?? Environment.GetEnvironmentVariable(LegacyFeedBaseEnvVar)
            ?? DefaultFeedBaseUrl;
        return new NodeChannel(
            ChannelId,
            DisplayName: "Harborline (dogfood)",
            FeedBase: feedBase,
            Kind: ChannelKind.Online,
            ExpectsRootKeyId: FeedKeyId.Format(PrincipalId.FromBase64Url(RootKeyIdBase64Url)),
            Enabled: true);
    }
}
