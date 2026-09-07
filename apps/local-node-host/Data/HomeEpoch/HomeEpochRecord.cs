namespace Harborline.Api.LocalNodeHost.Data.HomeEpoch;

/// <summary>
/// Durable, tenant-keyed, monotonic, roster-signed <b>home-failover fencing-epoch</b> record — the MD-2
/// invariant-bearing serialization that lets a tenant's authoritative "home" device fail over to a new
/// device <em>without two homes double-committing</em> (the joint ADR 0113+0117 amendment; ADR 0135 §D3
/// "fencing epoch — bump-on-promotion, reject-stale-at-the-point-of-effect").
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is — and is NOT.</b> This is a NEW durable primitive, NOT a reuse of the
/// <c>Harborline.Api.Kernel.SchemaRegistry.Epochs.EpochCoordinator</c> (security verdict F-C). That coordinator
/// is an <em>in-memory</em>, <em>global</em> (not tenant-keyed), <em>unsigned</em> <em>schema</em>-epoch
/// placeholder — it shares only a monotonic-counter <em>shape</em>. The home-failover fence needs all
/// three things the schema-epoch placeholder lacks: durable persistence into <c>local-node.db</c>,
/// tenant-keying, and a forge-proof roster signature on every bump. This record supplies them.
/// </para>
/// <para>
/// <b>Tenant-keyed + monotonic (composite key <c>(TenantId, EpochNumber)</c>).</b> The "current home epoch"
/// for a tenant is the row with the highest <see cref="EpochNumber"/>. Promotion (a planned hand-off or a
/// recovery failover) appends a row with <c>EpochNumber = previous + 1</c> — the store rejects any
/// non-strictly-increasing append, so the sequence is monotonic by construction and the tip is
/// unambiguous. Per ADR 0135 §D3 / security verdict Q5 the epoch is <b>per-tenant-home</b> (one fence to
/// prove), never per-data-class.
/// </para>
/// <para>
/// <b>Roster-signed / forge-proof (the bump is the authority, not a flag).</b> Every bump carries an
/// Ed25519 signature over the canonical <see cref="HomeEpochSignaturePayload"/> by an in-roster admin
/// holding the home-promotion authority, produced through the same <c>IOperationSigner</c> /
/// <c>CanonicalJson</c> / <c>SignedOperation&lt;T&gt;</c> path the trust roster uses for admissions
/// (<c>RosterSigning</c>). A forged or tampered bump reconstructs different signable bytes ⇒ the Ed25519
/// check fails ⇒ the row is dropped exactly like a forged admission. The signature, not a writable column,
/// is what makes "this device is home as of epoch N" unforgeable.
/// </para>
/// <para>
/// <b>Multi-actor floor for the contested case (security verdict G-5 / ADR 0068 §3.1).</b> A
/// <see cref="HomePromotionKind.RecoveryFailover"/> bump (old home lost, cannot surrender) MUST carry a
/// second, distinct co-approver signature (<see cref="CoApproverIssuerId"/> /
/// <see cref="CoApproverSignature"/>) so one compromised device cannot unilaterally seize the home and
/// discard the legitimate home's books. A <see cref="HomePromotionKind.PlannedHandoff"/> bump (the old
/// device surrenders, confirmed offline) may be single-admin. The store enforces this floor on advance.
/// </para>
/// <para>
/// <b>SC-4-safe + strictly append-only.</b> The row lives in the recoverable, Store-DEK-enveloped
/// <c>local-node.db</c>, so a passphrase reseed preserves the home-epoch history with everything else.
/// Like the audit system-of-record, the table is append-only — a superseded epoch is never UPDATEd or
/// DELETEd; a new higher-numbered row supersedes it.
/// </para>
/// </remarks>
public sealed class HomeEpochRecord
{
    /// <summary>The tenant this home-epoch is scoped to — the active-team-derived tenant
    /// (<c>ActiveTeamTenantContext.ProjectTenantId</c>; ADR 0032). The explicit column + WHERE filter is
    /// the node's defence-in-depth, per-org boundary (ADR 0092 — no ambient query filter on the node).
    /// Part of the composite key.</summary>
    public required string TenantId { get; init; }

    /// <summary>The monotonic epoch number. The current home is the row with the highest value for the
    /// tenant. Genesis is <c>1</c>; every promotion appends <c>previous + 1</c>. Part of the composite
    /// key; the store rejects any non-strictly-increasing append.</summary>
    public required long EpochNumber { get; init; }

    /// <summary>The <see cref="EpochNumber"/> this bump supersedes (<c>0</c> for the genesis epoch). Bound
    /// INTO the signature so a bump cannot be replayed against a different predecessor.</summary>
    public required long PreviousEpochNumber { get; init; }

    /// <summary>The device id this epoch designates as the authoritative home. A write asserting a stale
    /// (lower) epoch is rejected inside the effect's own transaction (the fence), so only this device's
    /// writes commit while this is the current epoch.</summary>
    public required string HomeDeviceId { get; init; }

    /// <summary>Whether this bump is a planned hand-off (old device surrenders, confirmed offline —
    /// single-admin allowed) or a recovery failover (old home lost — multi-actor floor REQUIRED, G-5).</summary>
    public required HomePromotionKind PromotionKind { get; init; }

    /// <summary>Wall-clock time the bump was issued (the signed instant; truncated to epoch-ms so the
    /// stored and signed instants are byte-aligned, matching <c>RosterSigning</c>).</summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>The per-bump nonce (also the signature nonce). Stored so the signature is independently
    /// re-verifiable from the row alone.</summary>
    public required Guid Nonce { get; init; }

    /// <summary>Base64Url of the promoting admin's Ed25519 public key (the roster-bound issuer). The
    /// receiving roster ADDITIONALLY checks this key is an in-roster admin holding the home-promotion
    /// authority before trusting the bump (the authority check is applied where the bump is verified
    /// against the trust roster, not stored on this row).</summary>
    public required string IssuerId { get; init; }

    /// <summary>Base64Url of the Ed25519 signature over the canonical <see cref="HomeEpochSignaturePayload"/>.
    /// Forge-proof: a tampered field reconstructs different signable bytes ⇒ verify fails ⇒ the row is
    /// dropped.</summary>
    public required string Signature { get; init; }

    /// <summary>Base64Url of the DISTINCT co-approver's Ed25519 public key. REQUIRED (non-null, distinct
    /// from <see cref="IssuerId"/>) for a <see cref="HomePromotionKind.RecoveryFailover"/> bump (the G-5
    /// multi-actor floor); null for a <see cref="HomePromotionKind.PlannedHandoff"/> bump.</summary>
    public string? CoApproverIssuerId { get; init; }

    /// <summary>Base64Url of the co-approver's Ed25519 signature over the SAME canonical
    /// <see cref="HomeEpochSignaturePayload"/>. REQUIRED for recovery-failover; null for planned-handoff.</summary>
    public string? CoApproverSignature { get; init; }
}

/// <summary>
/// The kind of home promotion a <see cref="HomeEpochRecord"/> records — drives the authorization floor
/// (security verdict G-5 / ADR 0068 §3.1).
/// </summary>
public enum HomePromotionKind
{
    /// <summary>The old home device surrenders the home voluntarily, confirmed offline. A single
    /// roster-admin signature suffices.</summary>
    PlannedHandoff = 0,

    /// <summary>The old home is lost and cannot surrender (the contested case). A multi-actor floor
    /// (proposer + a distinct co-approver, ADR 0068 §3.1) is REQUIRED so one compromised device cannot
    /// seize the home and discard the legitimate home's work.</summary>
    RecoveryFailover = 1,

    /// <summary>The install-time GENESIS (ADR 0101 Rev 3.2 precondition 2): epoch 1 recording the
    /// installing device as the home. Not a transfer — there is no predecessor and no contest, so the
    /// single-admin floor applies (the node's own principal signs). Additive member: the append-only
    /// table stores the kind as its string form, so a genesis row is never conflated with a
    /// planned-handoff transfer.</summary>
    Genesis = 2,
}
