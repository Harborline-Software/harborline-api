using System;
using System.Text.Json.Serialization;
using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.IdentityAtlas.Enrollment;

/// <summary>
/// The team's TRUST ANCHOR — the minimal, public verifying root a new device needs to authenticate the team it
/// is joining (enrollment Phase B; cerebrum [2026-06-20] "the admin's device presents a QR carrying the team
/// trust anchor (genesis/roster verifying key)"). It is carried over the admission channel (a QR the admin's
/// device shows, or an invite payload delivered out-of-band) so the JOINING device can verify it is being
/// admitted to the RIGHT team, rooted in the RIGHT genesis — before it sends its own pubkey back.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is PUBLIC by design.</b> The anchor carries only the team id + the genesis party id + the genesis
/// PUBLIC key. None of it is secret — it is the same information any roster member already holds. Publishing it
/// in a QR / invite leaks nothing: an attacker who learns the anchor still cannot admit themselves, because
/// admission requires an in-roster admin's SIGNATURE over the joining (party, key) pair (the Phase A
/// <see cref="MemberRoster.Admit"/> guard), which the anchor does not confer.
/// </para>
/// <para>
/// <b>What it lets the joiner verify.</b> The joining device, given the anchor, can confirm the team id it is
/// about to join and pin the genesis key as the chain root — so a later <see cref="MemberRoster.ValidatesToGenesis"/>
/// over a synced roster proves the roster it received is the one rooted in THIS anchor's genesis, not an
/// attacker's parallel roster. The anchor is the joiner's first, out-of-band-delivered, trust pin.
/// </para>
/// </remarks>
/// <param name="TeamId">The team/org this anchor identifies (string form of the Guid).</param>
/// <param name="GenesisPartyId">The founding member's party id — the immutable chain root's identity.</param>
/// <param name="GenesisPublicKey">base64url of the genesis member's Ed25519 public key — the chain-root verifying key.</param>
public sealed record TeamTrustAnchor(
    string TeamId,
    string GenesisPartyId,
    string GenesisPublicKey)
{
    /// <summary>
    /// Public discovery scope for this immutable roster root. The team id prevents one genesis key from
    /// merging distinct teams; the genesis key prevents parallel rosters that reuse a team id from merging.
    /// </summary>
    [JsonIgnore]
    public string RosterId => TeamId + ":" + GenesisPublicKey;

    /// <summary>
    /// Build a trust anchor from a founded roster — reads the genesis party + the genesis member's bound key.
    /// Throws when the roster has no genesis member bound (a malformed roster cannot publish an anchor).
    /// </summary>
    public static TeamTrustAnchor FromRoster(MemberRoster roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        var genesisKey = roster.PublicKeyOf(roster.GenesisPartyId)
            ?? throw new InvalidOperationException(
                "Cannot publish a trust anchor: the roster has no bound genesis member.");
        return new TeamTrustAnchor(
            TeamId: roster.TeamId.ToString("D"),
            GenesisPartyId: roster.GenesisPartyId,
            GenesisPublicKey: genesisKey.ToBase64Url());
    }

    /// <summary>The genesis verifying key as a <see cref="PrincipalId"/>; throws <see cref="FormatException"/>
    /// on a malformed key.</summary>
    public PrincipalId GenesisKey() => PrincipalId.FromBase64Url(GenesisPublicKey);
}
