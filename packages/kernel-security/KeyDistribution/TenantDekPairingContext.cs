namespace Harborline.Api.Kernel.Security.KeyDistribution;

/// <summary>
/// The AUTHENTICATED CONTEXT a <see cref="WrappedTenantDek"/> is bound to — the MD-1 answer to framing-correction
/// <b>F-A</b> (joint ADR 0113+0117 amendment, 2026-06-24). The 0046-A6 X25519 sealed-box
/// (<see cref="Harborline.Api.Kernel.Security.Crypto.IX25519KeyAgreement"/>) is <b>anonymous-sender, encrypt-only, with
/// NO caller-supplied AAD</b>: a bare ciphertext does NOT self-attest <i>which tenant's</i> DEK it carries, <i>who</i>
/// it is for, or <i>which</i> home epoch it was minted under. Without this binding a wrap blob is context-free and
/// replayable across tenants/recipients/epochs.
/// </summary>
/// <remarks>
/// <para>
/// This record is the canonical context that is (a) signed INTO the outer Ed25519 signature
/// (<see cref="WrappedTenantDek.Signature"/>) by the roster-bound pairing admin — so a verifier confirms the
/// context is exactly what the admin attested — and (b) re-asserted by the recipient at unwrap against the
/// (tenantId, recipientPartyId, homeEpoch, purpose) it EXPECTS, so a wrap minted for tenant-A / party-X / epoch-1
/// is rejected if presented as a wrap for tenant-B / party-Y / epoch-2 (replay / context-mismatch defence).
/// </para>
/// <para>
/// <b>Why the home epoch is bound (forward-looking to MD-2/G-4/Q4).</b> A revocation that triggers a DEK rotation
/// (the security verdict's Q4 condition) bumps the home epoch; binding the epoch here lets a recipient reject a
/// wrap minted under a stale epoch once rotation lands, so a revoked party's old wrapped DEK does not open
/// re-keyed data. MD-1 ships the binding; the rotation machinery that consumes it is MD-2/Q4-gated (deferred).
/// </para>
/// </remarks>
/// <param name="TenantId">The tenant whose DEK this wrap carries (G-2: the wrap's blast radius is ONE tenant — it
/// wraps the per-tenant DERIVED DEK, never a root seed, never a sibling app's DEK).</param>
/// <param name="RecipientPartyId">The admitted party the DEK is wrapped FOR — the recipient of the X25519 box. Bound
/// so a wrap for party-X cannot be replayed as a wrap for party-Y.</param>
/// <param name="HomeEpoch">The home-failover epoch the wrap was minted under (MD-2 <c>HomeEpochRecord</c>; pinned to
/// 0 in MD-1 until the fencing-epoch lands). Bound for the Q4 rotation cutoff.</param>
/// <param name="Purpose">The fixed purpose label — always <see cref="DekPairing"/> — domain-separating this wrap
/// from any other X25519 box use (DM seals, role keys) so a blob from another context can never be honoured here.</param>
public sealed record TenantDekPairingContext(
    string TenantId,
    string RecipientPartyId,
    long HomeEpoch,
    string Purpose)
{
    /// <summary>The fixed purpose label for a tenant-DEK pairing wrap — the F-A context-binding domain separator.</summary>
    public const string DekPairing = "dek-pairing";

    /// <summary>
    /// Construct the canonical MD-1 pairing context with the fixed <see cref="DekPairing"/> purpose. Validates the
    /// identifiers are present (a wrap with an empty tenant/recipient is meaningless and must never be minted).
    /// </summary>
    public static TenantDekPairingContext For(string tenantId, string recipientPartyId, long homeEpoch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientPartyId);
        if (homeEpoch < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(homeEpoch), homeEpoch, "Home epoch must be non-negative.");
        }
        return new TenantDekPairingContext(tenantId, recipientPartyId, homeEpoch, DekPairing);
    }
}
