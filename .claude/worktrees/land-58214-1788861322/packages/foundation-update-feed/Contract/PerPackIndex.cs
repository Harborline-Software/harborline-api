using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.Foundation.UpdateFeed.Contract;

/// <summary>
/// The per-pack version index (<c>packs/&lt;key&gt;/index.json</c>, design note §2.2) — the version
/// lineage for one pack. It is the <c>payload</c> of a <c>SignedOperation&lt;PerPackIndex&gt;</c> signed
/// by the channel root, so the channel-root signature covers the whole lineage.
/// </summary>
/// <param name="FeedFormat">The feed envelope contract version (§2.5).</param>
/// <param name="PackKey">The pack key this lineage belongs to (cross-checked against the channel
/// index's <see cref="ChannelPackRef.PackKey"/>).</param>
/// <param name="ChannelRootKeyId">The self-declared root key-id (cross-checked against the envelope's
/// real signer, F6).</param>
/// <param name="Versions">The version lineage, newest-declared-latest surfaced via the channel
/// index's <see cref="ChannelPackRef.Latest"/>.</param>
public sealed record PerPackIndex(
    int FeedFormat,
    string PackKey,
    string ChannelRootKeyId,
    IReadOnlyList<PerPackVersionEntry> Versions);

/// <summary>
/// One version of a pack in the lineage (§2.2).
/// </summary>
/// <remarks>
/// <para>The design note's §2.2 example is illustrative; U1 completes the SHAPE the node needs to
/// actually acquire an artifact by adding <see cref="ArtifactUrl"/> + <see cref="ArtifactCid"/> beside
/// <see cref="ManifestUrl"/> + <see cref="ManifestCid"/>. Both the manifest (the cheap
/// verify-the-signature-and-see-what's-inside surface) and the artifact (the full pack blob the install
/// engine consumes) are content-addressed and immutable (§2.3): the path IS the integrity check.</para>
/// </remarks>
/// <param name="Version">The PINNED semantic version (immutable once published, ADR 0011).</param>
/// <param name="ManifestUrl">Relative URL of the pack's already-signed manifest envelope
/// (<c>SignedOperation&lt;PackSignatureSubject&gt;</c>) — the publisher signature + merkle-bound content
/// addresses (S-14), extracted for a cheap fetch.</param>
/// <param name="ManifestCid">Content-address of the manifest document (the trust hand-off).</param>
/// <param name="ArtifactUrl">Relative URL of the full content-addressed pack blob
/// (<c>&lt;version&gt;/artifact.&lt;cid&gt;.pack</c>) the install engine consumes.</param>
/// <param name="ArtifactCid">Content-address of the pack blob — also embedded in the filename, so the
/// path is the integrity check.</param>
/// <param name="PublishedAt">When this version was published to the channel.</param>
/// <param name="MinNodeEpoch">The schema-epoch floor (ADR 0147 D4; the one-way plane-2→plane-3 gate,
/// §6.4). If the running binary's epoch is lower, the content update is blocked pending a binary update
/// — content waits on binary, never the reverse. This is the SINGLE node-wide counter (CIC ruling
/// 2026-07-07 / UF-4), distinct from the pack's key-rotation signing epoch.</param>
/// <param name="Supersedes">The prior version this one supersedes, or <c>null</c> for the first.</param>
/// <param name="YankedAt">Soft-yank marker (§7.2): present ⇒ "do not offer as latest" (a buggy build);
/// distinct from a revocation (a compromised key). Already-installed nodes keep a yanked version
/// (S-8 immutable seed); the index simply advances <c>latest</c> past it.</param>
/// <param name="RolloutBucket">Staged-rollout SEAM (§2.4). v1 is ALWAYS <c>null</c> (all-or-nothing per
/// channel; a static feed has no server compute to bucket a rollout — CIC ruling 2026-07-07 / UF-6).
/// Reserved-not-populated so real rollout later needs no feed-format bump.</param>
public sealed record PerPackVersionEntry(
    string Version,
    string ManifestUrl,
    Cid ManifestCid,
    string ArtifactUrl,
    Cid ArtifactCid,
    DateTimeOffset PublishedAt,
    long MinNodeEpoch,
    string? Supersedes,
    DateTimeOffset? YankedAt,
    string? RolloutBucket);
