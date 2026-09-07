using System;

namespace Harborline.Api.Foundation.IdentityAtlas.Enrollment;

/// <summary>
/// A single-use, short-TTL admission INVITE — the bearer credential the remote (invite-code) admission mode
/// uses (enrollment Phase B; cerebrum [2026-06-20] "the admin generates a single-use, short-TTL invite (token +
/// team trust anchor) delivered out-of-band; the new device presents token + its pubkey → admitted").
/// </summary>
/// <remarks>
/// <para>
/// <b>It is a BEARER credential — so it is deliberately weak on its own.</b> Anyone who holds the token can
/// present it. That is why it is (1) <b>single-use</b> (consumed on first redemption — replay is rejected by the
/// issuing store, see <see cref="IAdmissionTokenStore"/>), (2) <b>short-TTL</b> (<see cref="IsExpired"/> — a
/// stale token is dead), and (3) grants only the <b>minimal (member) composition by default</b> until an admin
/// elevates the new member (cerebrum: "the admitted member gets the minimal (member) composition by default
/// until elevated"). These three bounds are what make a leaked invite low-blast: a thief gets, at most, a
/// time-boxed, single-shot, least-privilege membership that an admin can revoke.
/// </para>
/// <para>
/// <b>Contrast with proximity/QR.</b> The invite mode trades the proximity mode's MITM-resistance (the in-person
/// channel binds the right pubkey to the right person) for remote reach. The token carries the
/// <see cref="TeamTrustAnchor"/> so the joiner authenticates the team; the bearer-credential weakness is on the
/// JOINER's identity (the admin cannot see who physically redeems it), which the single-use + TTL + minimal-
/// default bounds contain. Mutual-QR (proximity) is the strongest; invite is the remote convenience tier.
/// </para>
/// </remarks>
/// <param name="TokenId">The opaque, unguessable token id (a fresh GUID; the redemption + single-use key).</param>
/// <param name="Anchor">The team trust anchor the joiner authenticates the team with.</param>
/// <param name="IssuedAt">When the invite was minted (UTC).</param>
/// <param name="Ttl">How long the invite is valid from <see cref="IssuedAt"/>.</param>
public sealed record AdmissionToken(
    string TokenId,
    TeamTrustAnchor Anchor,
    DateTimeOffset IssuedAt,
    TimeSpan Ttl)
{
    /// <summary>The instant after which this invite is dead.</summary>
    public DateTimeOffset ExpiresAt => IssuedAt + Ttl;

    /// <summary>True iff the invite has passed its TTL at <paramref name="now"/> (fail-closed — an unparsable /
    /// non-positive TTL is treated as already-expired).</summary>
    public bool IsExpired(DateTimeOffset now) => Ttl <= TimeSpan.Zero || now >= ExpiresAt;

    /// <summary>
    /// Mint a fresh single-use invite for a team, valid for <paramref name="ttl"/> from <paramref name="now"/>.
    /// The default TTL is deliberately short (15 minutes) — an invite is meant to be redeemed promptly, not to
    /// linger as a standing credential.
    /// </summary>
    public static AdmissionToken Mint(TeamTrustAnchor anchor, DateTimeOffset now, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var span = ttl ?? TimeSpan.FromMinutes(15);
        if (span <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ttl), "An invite TTL must be positive.");
        return new AdmissionToken(Guid.NewGuid().ToString("N"), anchor, now, span);
    }
}
