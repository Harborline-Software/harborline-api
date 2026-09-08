using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.UpdateFeed.Verify;

/// <summary>
/// The out-of-band pinned channel root a verifier trusts (design note §7.4 — the official root ships
/// PINNED IN THE SIGNED BINARY; trust-on-first-use is not acceptable for the anchor). The offline
/// verify script takes it as an explicit input; the node client (U2) reads it from the binary pin.
/// </summary>
/// <param name="KeyId">The channel root's public signing key. Every signed feed document MUST be signed
/// by exactly this key (the verifier never trusts the serving URL — §2.3).</param>
/// <param name="PublisherEpoch">The epoch under which artifacts on this channel were publisher-signed
/// (ADR 0126 D4 sealed-epoch). Used to build the pack trust store for the artifact's own verification.
/// In the v1 / dogfood first-party model the publisher IS the channel root, so this is the channel
/// root's current epoch.</param>
public sealed record FeedPinnedRoot(PrincipalId KeyId, long PublisherEpoch);

/// <summary>
/// Consumer-side verification parameters that are NOT in the (mirror-identical) feed bytes.
/// </summary>
/// <param name="ChannelKind">Online vs sideload posture past <c>validUntil</c> (F1).</param>
/// <param name="Now">The instant to evaluate freshness against (injectable for tests / a fixed clock).</param>
/// <param name="MinSequence">The anti-rollback floor: the highest channel <c>sequence</c> this consumer
/// has already seen, OR the build-time first-contact floor pinned beside the root (F2). A channel index
/// whose sequence is BELOW this is refused. <c>null</c> ⇒ no floor (accept any sequence — a first-ever
/// verify with no baseline; the node client supplies a real floor).</param>
/// <param name="RevocationMaxAge">If set, a revocation list older than this at <see cref="Now"/> raises
/// a non-fatal staleness WARNING (offline-tolerant, surfaced not blocking — S-11). <c>null</c> ⇒ do not
/// surface staleness.</param>
public sealed record FeedVerifyOptions(
    ChannelKind ChannelKind,
    DateTimeOffset Now,
    long? MinSequence = null,
    TimeSpan? RevocationMaxAge = null);
