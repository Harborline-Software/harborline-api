namespace Harborline.Api.LocalNodeHost.Data.Admission;

/// <summary>
/// MTW-2 #3167 (R6/D) — the DURABLE row for a web-admitted-member device-pairing binding: the four web-plane
/// membership pins (tenant, canonical principal, party, grant identity) captured off the member's authenticated
/// web session at mint, keyed by the single-use pairing token id. It is the SQLCipher-backed replacement for the
/// v1 in-memory <c>InMemoryWebPairingInviteBindingStore</c>, so a pairing minted before a node recycle can still
/// be redeemed by the joining device within the token's TTL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate concern from the token store.</b> Single-use enforcement stays with the
/// <see cref="NodeAdmissionTokenRecord"/> / <c>DurableAdmissionTokenStore</c> (the atomic CAS Redeem); this row is
/// a pure keyed LOOKUP of the bound pins (a binding read after the token is redeemed still returns the pins — the
/// single-use gate is the token store, not this). They share the same encrypted <c>local-node.db</c> file + the
/// <see cref="NodeLocalAdmissionDbContext"/>.
/// </para>
/// <para>
/// <b>Membership stored as JSON.</b> The bound <c>TenantMembershipSnapshot</c> is persisted as its
/// canonical JSON so the row stays a flat record (the same posture the roster/comms rows take for their
/// permission arrays); it is re-read live at redemption for pin re-verification, so the stored snapshot carries
/// only the COORDINATES, never authority.
/// </para>
/// </remarks>
public sealed class NodePairingInviteBindingRecord
{
    /// <summary>The opaque single-use pairing token id the mint issued (primary key; the redemption lookup key).</summary>
    public required string TokenId { get; set; }

    /// <summary>The People PartyId the token was minted for (the session principal's canonical party).</summary>
    public required string BoundPartyId { get; set; }

    /// <summary>#3167 R1.2 — the opaque mint-time SessionCorrelationId (audit provenance, bound into the admission
    /// at redemption). Defaults empty for a legacy binding.</summary>
    public string SessionCorrelationId { get; set; } = string.Empty;

    /// <summary>The four web-plane membership pins as canonical JSON of the bound <c>TenantMembershipSnapshot</c>.</summary>
    public required string MembershipJson { get; set; }
}
