namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// The canonical, signable payload a <see cref="HomeEpochRecord"/> bump signature covers. Signed through
/// the foundation <c>IOperationSigner</c> / <c>CanonicalJson</c> / <c>SignedOperation&lt;T&gt;</c> path —
/// the SAME byte-stable, cross-language signing discipline the trust roster uses for admissions
/// (<c>Harborline.Api.Foundation.IdentityAtlas.RosterSigning</c>), so a home-epoch bump is forge-proof exactly
/// like a roster admission.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every invariant-bearing field of the bump is INSIDE the signed payload.</b> The tenant, the new
/// epoch number, the predecessor it supersedes, the device it designates as home, and the promotion kind
/// are all covered. A roster writer who tampers with ANY of them (re-aims the home to a different device,
/// replays the bump against a different predecessor, downgrades a recovery-failover to a planned-handoff
/// to dodge the multi-actor floor) produces a record whose canonical bytes differ from the signed bytes
/// ⇒ the Ed25519 check fails ⇒ the row is dropped on verification. This is the same construction that
/// makes the (party → key) roster binding forge-proof.
/// </para>
/// <para>
/// <b>Both signatures (proposer + co-approver) cover the IDENTICAL payload.</b> For a recovery-failover
/// the co-approver signs the same <see cref="HomeEpochSignaturePayload"/> bytes, so the multi-actor floor
/// is "two distinct in-roster admins independently attested THIS exact promotion", not two unrelated
/// signatures.
/// </para>
/// </remarks>
/// <param name="PayloadType">The constant payload-type domain separator
/// (<see cref="TypeDiscriminator"/> = <c>home-epoch/v1</c>) bound into the canonical payload —
/// the sibling pattern ADR 0168 D2-A4(c) pins for the spatial mint. (Declared first for
/// readability only; <c>CanonicalJson</c> sorts keys ordinally, so position carries no
/// signing semantics — presence in the signed bytes is what matters.) Binding the type
/// into the signed bytes means a signature over ANY other payload type produced by the same
/// roster keys can never verify as a home-epoch bump (no cross-protocol replay), and a future
/// payload revision bumps the version rather than silently changing meaning. ADR 0101 text
/// amendment tracked on card 3738.</param>
/// <param name="TenantId">The tenant the home-epoch is scoped to (<c>TenantId.Value</c>).</param>
/// <param name="EpochNumber">The new monotonic epoch number this bump establishes.</param>
/// <param name="PreviousEpochNumber">The epoch this bump supersedes (<c>0</c> at genesis) — bound in so a
/// bump cannot be replayed against a different predecessor.</param>
/// <param name="HomeDeviceId">The device this epoch designates as the authoritative home.</param>
/// <param name="PromotionKind">The promotion kind (string form of <see cref="HomePromotionKind"/>) — bound
/// in so a recovery-failover cannot be silently signed as a planned-handoff to evade the G-5 floor.</param>
public sealed record HomeEpochSignaturePayload(
    string PayloadType,
    string TenantId,
    long EpochNumber,
    long PreviousEpochNumber,
    string HomeDeviceId,
    string PromotionKind)
{
    /// <summary>The constant payload-type domain separator every home-epoch signature covers.
    /// Version-stamped so a future v2 payload shape coexists with deployed v1 rows.</summary>
    public const string TypeDiscriminator = "home-epoch/v1";

    /// <summary>
    /// Builds the canonical payload for a <see cref="HomeEpochRecord"/>'s signature inputs. The single
    /// source of truth for "what bytes a home-epoch bump signs", so the producer (the advance path) and
    /// the verifier (the store + any peer rebuild) assemble byte-identical payloads.
    /// </summary>
    public static HomeEpochSignaturePayload For(
        string tenantId,
        long epochNumber,
        long previousEpochNumber,
        string homeDeviceId,
        HomePromotionKind promotionKind) =>
        new(
            PayloadType: TypeDiscriminator,
            TenantId: tenantId,
            EpochNumber: epochNumber,
            PreviousEpochNumber: previousEpochNumber,
            HomeDeviceId: homeDeviceId,
            // Stringify the kind so the signed bytes are stable + human-auditable and a future enum
            // reordering cannot silently change a signature's meaning.
            PromotionKind: promotionKind.ToString());
}
