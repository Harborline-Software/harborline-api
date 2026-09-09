using System;

using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// MTW-2 #3107 — the DEVICE-PAIRING TOKEN MINT for a web-admitted member's FIRST device (the ADR 0113 +
/// 0117 "Multi-device enablement" amendment's pairing design, applied to a member whose only existing
/// identity is web-plane; admiral-ruling-2026-07-23T1745Z). A member admitted through the web invitation
/// flow (#2614) exists on the web tenant-identity plane but is not yet in the signed atlas
/// <see cref="MemberRoster"/>. When that member takes an authenticated "connect your device" action, this
/// mint issues a single-use, short-TTL pairing token and binds the four web-plane membership pins to it —
/// under the member's OWN authenticated web-session authority.
/// </summary>
/// <remarks>
/// <para>
/// <b>Minted under session-derived authority (ADR 0160 R3-D), never pre-signed, never node-standing.</b>
/// The pins are read straight off the immutable <see cref="SelectedSessionRequestPrincipal"/> the listener
/// derived from the selected session — the browser supplies none of them, and no caller-passed membership
/// snapshot is trusted. The mint is NOT invoked at invitation-issuance time (no pre-signing) and does NOT
/// grant the node any standing tenant-scoped admitter authority; it is the member, present and
/// authenticated, asking to pair their own device. The token's admit is still an in-roster admin signing
/// at redemption (the existing <see cref="AdmissionCoordinator.AdmitOverInvite"/> path).
/// </para>
/// <para>
/// <b>The four pins.</b> tenant (<see cref="SelectedSessionRequestPrincipal.TenantId"/>), canonical
/// principal (<see cref="SelectedSessionRequestPrincipal.PrincipalUserId"/>), party
/// (<see cref="SelectedSessionRequestPrincipal.CanonicalParty"/>), and grant identity (the single pinned
/// grant id + owner-version + <see cref="SelectedSessionRequestPrincipal.AuthorizationEpoch"/>). A live
/// selected session implies an Active membership, so the bound snapshot is Active by construction; the
/// bridge RE-READS the live grant at redemption, so revocation between mint and enrollment still refuses.
/// </para>
/// <para>
/// <b>Bounds.</b> The web membership is single-grant (ADR 0160 R3). A session principal that carries any
/// other grant cardinality is outside this slice — the mint REFUSES rather than guess which grant anchors
/// the pairing. Nothing is minted on a refusal.
/// </para>
/// </remarks>
internal sealed class WebAdmittedMemberPairingTokenMint
{
    private readonly AdmissionCoordinator _coordinator;
    private readonly IWebPairingInviteBindingStore _bindings;

    public WebAdmittedMemberPairingTokenMint(
        AdmissionCoordinator coordinator,
        IWebPairingInviteBindingStore bindings)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
    }

    /// <summary>
    /// Mint a single-use, short-TTL pairing token for the member behind <paramref name="session"/> and bind
    /// the four web-plane pins to it. The token carries the team trust anchor (from <paramref name="roster"/>)
    /// so the joining device authenticates the team, exactly as the base invite mode. On success the caller
    /// delivers the returned token to the member's new device (the authenticated route's job — the live
    /// "connect your device" surface is the flagged wiring follow-on).
    /// </summary>
    /// <param name="session">The authenticated selected-session principal (source of the pins; not caller-overridable).</param>
    /// <param name="roster">The home node's current signed roster (source of the team trust anchor).</param>
    /// <param name="ttl">Optional TTL override; defaults to the invite store's short default.</param>
    public PairingTokenMintOutcome MintForSession(
        SelectedSessionRequestPrincipal session,
        MemberRoster roster,
        TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(roster);

        var pinnedGrant = session.PinnedGrantOwnerVersions[0];

        // The four pins come straight off the session-derived principal (R3-D). A live selected session
        // implies an Active membership; the bound snapshot is Active by construction and the bridge
        // re-reads the live grant at redemption (revocation-after-mint still refuses there).
        var membership = new TenantMembershipSnapshot(
            MembershipId: session.MembershipId,
            AccountId: session.AccountId,
            TenantId: session.TenantId.Value,
            CanonicalPrincipalId: session.PrincipalUserId.Value,
            GrantId: pinnedGrant.GrantId,
            GrantOwnerVersion: pinnedGrant.OwnerVersion,
            AuthorizationEpoch: session.AuthorizationEpoch,
            Status: TenantMembershipStatus.Active,
            OwnerVersion: session.MembershipOwnerVersion);

        // Mint the single-use, short-TTL bearer token (issued into the token store's single-use gate), then
        // bind the session-derived pins to it server-side. Only the opaque TokenId will cross the wire.
        var token = _coordinator.CreateInvite(TeamTrustAnchor.FromRoster(roster), ttl);
        // #3167 R1.2 — capture the session correlation id off the authenticated session principal so it can be
        // bound into the signed admission as the minting-session evidence at redemption (audit provenance).
        _bindings.Bind(new WebPairingInviteBinding(
            // Ticket 294 slice 2a — the token is bound to the ONE party key: the session's canonical tenant
            // PRINCIPAL id, which is what the enrolling device presents as JoiningPartyId and what the roster
            // edge is keyed by. session.CanonicalParty stays the attribution stamped on writes, not a key.
            token.TokenId, membership, session.PrincipalUserId.Value, token.Anchor, session.SessionCorrelationId));
        return PairingTokenMintOutcome.Issued(token);
    }
}

/// <summary>
/// The outcome of a pairing-token mint: the issued token on success, or a coarse, PII-free refusal reason.
/// On refusal NOTHING is minted (no token issued, no binding recorded).
/// </summary>
internal sealed record PairingTokenMintOutcome
{
    private PairingTokenMintOutcome(bool minted, AdmissionToken? token, string? refusalReason)
    {
        Minted = minted;
        Token = token;
        RefusalReason = refusalReason;
    }

    /// <summary>True iff a pairing token was issued and its pin binding recorded.</summary>
    public bool Minted { get; }

    /// <summary>The issued single-use pairing token on success; otherwise null.</summary>
    public AdmissionToken? Token { get; }

    /// <summary>A coarse, PII-free refusal reason on failure; otherwise null.</summary>
    public string? RefusalReason { get; }

    internal static PairingTokenMintOutcome Issued(AdmissionToken token) => new(true, token, null);

    internal static PairingTokenMintOutcome Refuse(string reason) => new(false, null, reason);
}
