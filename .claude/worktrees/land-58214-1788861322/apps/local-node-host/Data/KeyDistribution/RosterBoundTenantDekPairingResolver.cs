using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Data.KeyDistribution;

/// <summary>
/// MD-1 / <b>G-1</b> — the PRODUCTION roster-bound <see cref="ITenantDekPairingResolver"/>. Resolves the recipient
/// wrap key for a party SOLELY from the verified team roster (<see cref="NodeTeamRoster.DmPublicKeyOf"/>) — the
/// X25519 DM public key SIGNED INTO that party's genesis-rooted admission (forge-proof, C5). For any party NOT
/// admitted (or admitted but carrying no DM key) it returns <c>null</c> → no DEK is wrapped (fail-closed).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is the no-mock-crypto anchor (the bug-1312 lesson, applied forward).</b> The recipient key is NOT a
/// function of the party id (an attacker who claims an admitted party's id does NOT get that party's wrap key — the
/// id is not the source). It is the key the roster's SIGNED admission chain bound for that party:
/// <c>MemberRoster.DmPublicKeyOf</c> surfaces ONLY the DM key the admission SIGNATURE attests (a substituted /
/// unsigned DM key is dropped on rebuild, C5). So a DEK can only ever be wrapped to a key a genesis-rooted admission
/// bound — never to an identity-derivable stand-in. A rogue / unadmitted party resolves <c>null</c> and gets no DEK.
/// </para>
/// <para>
/// <b>Reads the CURRENT roster on each call</b> so a freshly admitted party becomes pairable live, and a party whose
/// admission has not yet converged is not (fail-closed) — the same liveness the C5 <c>RosterDmKeyResolver</c> has.
/// </para>
/// <para>
/// <b>It holds no private key and derives nothing.</b> It is a thin lookup over the roster — the wrap itself (the
/// <c>ITenantDekWrapper</c> construction) and the recipient's node-secret private key (the unwrap) live elsewhere.
/// This keeps the fenceable surface small: the ONLY shipped <see cref="ITenantDekPairingResolver"/> is this
/// roster-bound type, and the arch-fence asserts exactly that.
/// </para>
/// </remarks>
public sealed class RosterBoundTenantDekPairingResolver : ITenantDekPairingResolver
{
    private readonly NodeTeamRoster _roster;

    /// <summary>Construct over the install-level verified team roster — the single source of admitted recipient keys.</summary>
    public RosterBoundTenantDekPairingResolver(NodeTeamRoster roster)
    {
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
    }

    /// <inheritdoc />
    public byte[]? ResolveRecipientWrapKey(string recipientPartyId)
    {
        if (string.IsNullOrWhiteSpace(recipientPartyId))
        {
            return null;
        }

        // FORGE-PROOF / FAIL-CLOSED: the recipient key is ONLY the X25519 DM public key the verified roster bound for
        // this party via its SIGNED admission. An unadmitted party (or one whose admission carried no DM key) yields
        // null → the pairing route does not wrap a DEK to it. The party id is NOT the key source — it is the roster
        // lookup KEY; the bound value came from a genesis-rooted, signature-validated admission (C5), so a forged id
        // does not produce a usable wrap key. This is the structural difference from the #1325 identity-derivable
        // stand-in (where the id WAS the key source).
        return _roster.DmPublicKeyOf(recipientPartyId);
    }
}
