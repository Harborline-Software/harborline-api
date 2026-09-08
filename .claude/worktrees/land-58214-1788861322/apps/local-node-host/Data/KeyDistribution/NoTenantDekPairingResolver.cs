namespace Harborline.Api.LocalNodeHost.Data.KeyDistribution;

/// <summary>
/// MD-1 / <b>G-1</b> — the FAIL-CLOSED null-object <see cref="ITenantDekPairingResolver"/>. Resolves NO recipient
/// key for ANY party, so a minimal DI graph (no roster wired) can wrap NO tenant DEK to anyone — the pairing route
/// fails closed (no DEK distributed), NEVER plaintext.
/// </summary>
/// <remarks>
/// <para>
/// This is the production DEFAULT registered by <c>AddNodeTenantDekPairing</c>. A FULL host that supplies the
/// verified roster replaces it with <see cref="RosterBoundTenantDekPairingResolver"/> via
/// <c>AddRosterBoundTenantDekPairingResolver</c> — the same degrade-to-fail-closed posture the comms DM key provider
/// uses (<c>NoDmConversationKeyProvider</c>). A host that FORGETS to wire the roster-bound resolver degrades to
/// "cannot pair a DEK", never to "pairs to an identity-derivable key" — the structural defence against the #1325
/// false-positive (the dev-gate must never silently become the confidentiality boundary).
/// </para>
/// <para>
/// It declares no key material and derives nothing — it is a pure null-object, structurally incapable of leaking.
/// </para>
/// </remarks>
public sealed class NoTenantDekPairingResolver : ITenantDekPairingResolver
{
    /// <inheritdoc />
    /// <remarks>Always <c>null</c> — no recipient is ever resolvable, so no DEK is ever wrapped (fail-closed).</remarks>
    public byte[]? ResolveRecipientWrapKey(string recipientPartyId) => null;
}
