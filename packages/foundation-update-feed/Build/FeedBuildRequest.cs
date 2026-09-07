using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.UpdateFeed.Contract;

namespace Harborline.Api.Foundation.UpdateFeed.Build;

/// <summary>
/// The inputs to <see cref="FeedBuilder"/> — everything needed to produce a signed feed tree for one
/// channel. Artifacts arrive already-exported + publisher-signed (the caller runs the existing
/// <c>PackExporter</c>; the feed only DISTRIBUTES what the pack composer already signs, §7.1).
/// </summary>
/// <param name="Channel">The channel identifier.</param>
/// <param name="GeneratedAt">Issuance time stamped into every signed document.</param>
/// <param name="ValidUntil">The signed freshness deadline (F1). Must be after
/// <paramref name="GeneratedAt"/>.</param>
/// <param name="Sequence">The channel index's monotonic anti-rollback counter (§7.2).</param>
/// <param name="TtlSeconds">Advisory cache/refresh hint.</param>
/// <param name="RevocationSequence">The coupled revocation list's monotonic sequence.</param>
/// <param name="Revoked">The <c>{key-id, epoch}</c> pairs revoked on this channel (S-11; may be
/// empty).</param>
/// <param name="RevocationIssuedAt">Issuance instant of the revocation list (drives staleness
/// surfacing).</param>
/// <param name="Policy">The per-feed compliance policy. The builder channel-root-signs it and binds
/// its CID into the signed channel index; a free-floating policy is never emitted.</param>
/// <param name="Packs">The packs (each with its published version lineage) to list.</param>
public sealed record FeedBuildRequest(
    string Channel,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ValidUntil,
    long Sequence,
    int TtlSeconds,
    long RevocationSequence,
    IReadOnlyList<PackRevokedKey> Revoked,
    DateTimeOffset RevocationIssuedAt,
    FeedPolicy Policy,
    IReadOnlyList<FeedPackInput> Packs);

/// <summary>One pack and its published version lineage.</summary>
/// <param name="PackKey">The pack key (ADR 0129 D1).</param>
/// <param name="Latest">The version the channel declares CURRENT. Must be one of
/// <paramref name="Versions"/> and must not be yanked.</param>
/// <param name="Versions">The version lineage (at least one).</param>
public sealed record FeedPackInput(
    string PackKey,
    string Latest,
    IReadOnlyList<FeedPackVersionInput> Versions);

/// <summary>One published version of a pack — the already-signed artifact + its extracted envelope.</summary>
/// <param name="Version">The pinned semantic version (must match the artifact's manifest version).</param>
/// <param name="ArtifactBytes">The full <c>PackFile</c> bytes (the sneakernet blob from
/// <c>PackExporter</c>) — content-addressed into <c>artifact.&lt;cid&gt;.pack</c>.</param>
/// <param name="ManifestEnvelope">The <c>PackFile.Envelope</c> — the publisher-signed
/// <c>SignedOperation&lt;PackSignatureSubject&gt;</c> written out as <c>manifest.json</c> (the cheap
/// verify-the-signature surface). Never <c>null</c>: an unsigned pack is never published.</param>
/// <param name="PublishedAt">When this version was published.</param>
/// <param name="MinNodeEpoch">The schema-epoch floor (§6.4 / UF-4 single node-wide counter).</param>
/// <param name="Supersedes">The prior version this supersedes, or <c>null</c>.</param>
/// <param name="YankedAt">Soft-yank marker, or <c>null</c> for a live version.</param>
public sealed record FeedPackVersionInput(
    string Version,
    ReadOnlyMemory<byte> ArtifactBytes,
    SignedOperation<PackSignatureSubject> ManifestEnvelope,
    DateTimeOffset PublishedAt,
    long MinNodeEpoch,
    string? Supersedes,
    DateTimeOffset? YankedAt);
