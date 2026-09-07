namespace Harborline.Api.LocalNodeHost.Data.KeyDistribution;

/// <summary>
/// MD-1 / <b>G-1</b> (joint ADR 0113+0117 amendment, 2026-06-24) — resolves the X25519 PUBLIC key a tenant DEK may
/// be wrapped TO for a recipient party, the LOAD-BEARING no-mock-crypto seam. The wrap recipient key MUST come ONLY
/// from the verified trust roster (a party the genesis-rooted chain validated); for any unadmitted / unbound
/// recipient the production resolver returns <c>null</c> and the pairing route FAILS CLOSED (no DEK is wrapped).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the exact seam the #1325 / bug-1312 false-positive lived in.</b> The C4 DM stand-in derived a party's
/// private key from a public identifier (party id) — so anyone holding the binary could derive any party's key by
/// claiming its id, and the dev-gate, not crypto, was load-bearing for confidentiality. A leak test that was GREEN
/// against that stand-in was a FALSE POSITIVE. The MD-1 generalization: the recipient key a DEK is wrapped to must
/// be roster-bound (signed into a genesis-rooted admission), never identity-derivable. The
/// <c>DekPairingResolverArchFence</c> asserts the SHIPPED assembly declares ZERO identity-derivable DEK resolvers and
/// that production resolves the roster-bound one; the leak test runs against the REAL roster-bound resolver.
/// </para>
/// <para>
/// <b>What this resolver does and does NOT do.</b> It resolves the (forge-proof, roster-bound) recipient PUBLIC key
/// for an admitted party — the confidentiality of the wrap then rests on the recipient holding the matching
/// node-secret PRIVATE key (which never leaves the recipient node). It does NOT itself wrap (that is the
/// <c>ITenantDekWrapper</c> construction) — it is the gate that decides WHO a DEK may be wrapped to. Separating the
/// two means the no-mock-crypto fence has a single, small, structurally-fenceable surface (this interface).
/// </para>
/// </remarks>
public interface ITenantDekPairingResolver
{
    /// <summary>
    /// The recipient X25519 DM PUBLIC key (raw 32 bytes) a tenant DEK may be wrapped to for
    /// <paramref name="recipientPartyId"/>, or <c>null</c> when the party is NOT an admitted member of the verified
    /// roster (fail-closed — no DEK may be wrapped to an unadmitted recipient). In production this is ALWAYS
    /// <c>MemberRoster.DmPublicKeyOf</c> (the key signed into the party's genesis-rooted admission) — never a key
    /// derived from the party id or any other public identifier.
    /// </summary>
    byte[]? ResolveRecipientWrapKey(string recipientPartyId);
}
