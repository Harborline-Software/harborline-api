using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.Foundation.UpdateFeed.Contract;

/// <summary>
/// The channel index (<c>channel.json</c>, design note §2.1) — the ONE mutable pointer that says
/// "what is current, and what is revoked." It is the <c>payload</c> of a
/// <c>SignedOperation&lt;ChannelIndex&gt;</c> signed by the channel root (reusing
/// <c>SignedOperation</c> — S-14), so the channel-root signature covers every field below, including
/// the freshness fences.
/// </summary>
/// <remarks>
/// <para>
/// <b>Consumer display-text contract:</b> publisher card strings reached through this feed remain
/// untrusted after signature verification. Consumers MUST escape them as text, MUST never interpret
/// them as markup, URLs, templates, or commands, and MUST project each value through
/// <see cref="Harborline.Api.Foundation.Packs.Model.PackCardDisplayText.PrepareNameOrTitleForRender"/> or
/// <see cref="Harborline.Api.Foundation.Packs.Model.PackCardDisplayText.PrepareFreeTextOrDescriptionForRender"/>
/// before rendering it with the platform's text-escaping API. Those boundaries sanitize legacy values,
/// enforce the visible limits, and add the trusted Unicode FSI/PDI isolate without rewriting signed bytes.
/// </para>
/// <para><b>The three fences this doc carries (§7.2):</b></para>
/// <list type="bullet">
///   <item><see cref="Sequence"/> — the monotonic anti-<b>ROLLBACK</b> fence (a node persists the
///     highest seen per channel and refuses a lower one).</item>
///   <item><see cref="ValidUntil"/> — the signer-committed anti-<b>FREEZE</b> fence (a frozen index
///     passes refuse-lower because <c>42 == 42</c>; only the signed deadline catches it — F1). Posture
///     past it is channel-TYPED (see <see cref="ChannelKind"/>).</item>
///   <item><see cref="RevocationsCid"/> and <see cref="FeedPolicyCid"/> — the F3/D3 coupling: the
///     index VOUCHES for the exact revocation list and compliance policy, so neither can be replaced
///     with an older document under a fresh index.</item>
/// </list>
/// </remarks>
/// <param name="FeedFormat">The feed ENVELOPE contract version (§2.5). A reader refuses a value it does
/// not understand (<see cref="FeedFormats.V1"/>).</param>
/// <param name="Channel">The channel identifier (e.g. <c>harborline-official</c>, or a dogfood/dev
/// channel).</param>
/// <param name="ChannelRootKeyId">The self-declared root key-id (<see cref="FeedKeyId"/>) — a legible
/// pointer cross-checked against the envelope's real signer; NOT the trust decision (§3.1 F6).</param>
/// <param name="GeneratedAt">ISSUANCE time — not a deadline (see <see cref="ValidUntil"/>); staleness is
/// surfaced, not guessed.</param>
/// <param name="ValidUntil">The SIGNED freshness deadline (TUF timestamp-role, F1) — a signer
/// COMMITMENT, not a node-side guess. Past it, an <see cref="ChannelKind.Online"/> channel goes
/// fail-closed for the install path; a <see cref="ChannelKind.Sideload"/> channel is expected-stale.</param>
/// <param name="Sequence">The MONOTONIC anti-rollback counter (§7.2). Persisted per channel; a lower
/// value than the high-water is refused.</param>
/// <param name="TtlSeconds">Advisory cache/refresh hint only — the signed fences are the real defense,
/// not the TTL (§2.6).</param>
/// <param name="RootAttestationUrl">RESERVED in <see cref="FeedFormats.V1"/> — the channel-root
/// rotation-attestation seam (F4, §7.4). When non-null (a future format), an old-signs-new attestation
/// delivered IN THE FEED so a root rotation is never manual-per-node-only. The mechanism is deferred;
/// the slot is not. A <see cref="FeedFormats.V1"/> document MUST carry <c>null</c> here.</param>
/// <param name="Packs">The per-pack pointers (each chains trust down by <see cref="ChannelPackRef.IndexCid"/>).</param>
/// <param name="RevocationsUrl">The relative URL of the signed revocation list.</param>
/// <param name="RevocationsCid">The content-address of the EXACT revocation list this index vouches for
/// (F3 coupling — cannot strip revocations while serving a fresh index).</param>
/// <param name="RevocationSequence">The monotonic sequence of the coupled revocation list (a
/// channel-level anti-rollback for the revocation surface specifically).</param>
/// <param name="FeedPolicyUrl">The relative URL of the channel-root-signed feed policy.</param>
/// <param name="FeedPolicyCid">The content address of the exact feed policy this index vouches for.
/// The policy therefore inherits this index's <see cref="Sequence"/> and <see cref="ValidUntil"/>
/// downgrade/freeze fences (ADR 0153 D3).</param>
public sealed record ChannelIndex(
    int FeedFormat,
    string Channel,
    string ChannelRootKeyId,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ValidUntil,
    long Sequence,
    int TtlSeconds,
    string? RootAttestationUrl,
    IReadOnlyList<ChannelPackRef> Packs,
    string RevocationsUrl,
    Cid RevocationsCid,
    long RevocationSequence,
    string FeedPolicyUrl,
    Cid FeedPolicyCid);

/// <summary>
/// One pack's pointer inside the channel index (§2.1). The <see cref="IndexCid"/> content-addresses the
/// per-pack index, chaining the channel-root trust down to the version lineage.
/// </summary>
/// <param name="PackKey">The pack key (ADR 0129 D1 — a stable key, never a mutable id).</param>
/// <param name="Latest">The version the channel declares CURRENT for this pack.</param>
/// <param name="IndexUrl">The relative URL of the per-pack version index.</param>
/// <param name="IndexCid">The content-address of the per-pack index (the trust hand-off down).</param>
/// <param name="Listing">RESERVED channel-signed marketplace-curation slot. V1 leaves it null; the
/// feed builder does not populate it.</param>
public sealed record ChannelPackRef(
    string PackKey,
    string Latest,
    string IndexUrl,
    Cid IndexCid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ChannelPackListing? Listing = null);

/// <summary>
/// Reserved shape for future channel-owned marketplace curation. The publisher-signed intrinsic card
/// metadata remains on the pack manifest; v1 defines no channel override fields and emits no listing.
/// </summary>
public sealed record ChannelPackListing;
