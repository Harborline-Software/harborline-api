namespace Harborline.Api.LocalNodeHost.Data.Admission;

/// <summary>
/// Node-local-authoritative ADMISSION-INVITE record — the durable row for one minted single-use invite token
/// (enrollment Phase B; the admitter side of <see cref="Harborline.Api.Foundation.IdentityAtlas.Enrollment.AdmissionToken"/>).
/// It is the durable backing for <see cref="DurableAdmissionTokenStore"/>, which replaces the in-memory v1
/// (<c>InMemoryAdmissionTokenStore</c>) so a minted invite SURVIVES a node restart within its TTL — a minted
/// invite is no longer lost when the host process recycles (cerebrum [2026-06-21] in-memory admission token store
/// follow-on).
/// </summary>
/// <remarks>
/// <para>
/// <b>Admitter-local, NOT synced.</b> Unlike the roster doctype, an invite is a purely admitter-local single-use
/// + TTL ledger — it is the server side of a BEARER exchange and never crosses the wire. So this is a flat
/// node-exclusive table (deliberately NOT a shared <c>IHarborlineEntityModule</c>, NO CRDT, NO delta-router
/// registration), mapped by its own <see cref="NodeLocalAdmissionDbContext"/>. The joiner never reads it; only the
/// admitting node mints (issues) and redeems (consumes) against it.
/// </para>
/// <para>
/// <b>Encrypted at rest (SC-1).</b> The row lives in the SAME SQLCipher-encrypted <c>local-node.db</c> file,
/// keyed through the same connection interceptor as every other node-local table — there is no plaintext path.
/// An invite carries the (public) team trust anchor, the TTL window, and a single redeemed flag; none of it is a
/// secret beyond the bearer token id, which IS the row's primary key (an unguessable GUID).
/// </para>
/// <para>
/// <b>Single-use is a column, not a deletion.</b> The store keeps a redeemed row (flag flipped) rather than
/// deleting it, so a replay of an already-consumed id is reported as <c>AlreadyRedeemed</c> (not the misleading
/// <c>UnknownToken</c>) — the same distinction the in-memory store made with its <c>_redeemed</c> set, now durable.
/// </para>
/// </remarks>
public sealed class NodeAdmissionTokenRecord
{
    /// <summary>The opaque, unguessable token id (the foundation token's <c>TokenId</c>) and primary key — the
    /// redemption + single-use key.</summary>
    public required string TokenId { get; set; }

    /// <summary>The (public) team id the invite admits into — string form of the Guid (the anchor's team id).</summary>
    public required string AnchorTeamId { get; set; }

    /// <summary>The (public) genesis party id from the team trust anchor.</summary>
    public required string AnchorGenesisPartyId { get; set; }

    /// <summary>base64url of the (public) genesis verifying key from the team trust anchor.</summary>
    public required string AnchorGenesisPublicKey { get; set; }

    /// <summary>When the invite was minted (UTC) — the foundation token's <c>IssuedAt</c>.</summary>
    public DateTimeOffset IssuedAtUtc { get; set; }

    /// <summary>How long the invite is valid from <see cref="IssuedAtUtc"/>, stored as whole milliseconds.</summary>
    public long TtlMilliseconds { get; set; }

    /// <summary>True once this invite has been redeemed exactly once — single-use enforcement. A row with this
    /// set is kept (not deleted) so a replay reports <c>AlreadyRedeemed</c> rather than <c>UnknownToken</c>.</summary>
    public bool Redeemed { get; set; }
}
