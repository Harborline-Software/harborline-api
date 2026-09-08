using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Kernel.Security.KeyDistribution;

/// <summary>
/// MD-1 wrap side — seal a tenant DEK for ONE admitted party's roster-bound X25519 public key, then sign the
/// {ciphertext + context} with a roster-bound pairing admin's Ed25519 key (encrypt-then-sign; the F-A correction).
/// </summary>
/// <remarks>
/// This is the CONSTRUCTION only — it knows nothing about the roster. The roster-bound binding of <i>which</i>
/// recipient public key is admissible (G-1, the no-mock-crypto anchor) is enforced ABOVE this, by the host's
/// <c>ITenantDekPairingResolver</c> (the only production source of the recipient DM public key is
/// <c>MemberRoster.DmPublicKeyOf</c> for an admitted party). Handing this wrapper a non-roster recipient key wraps
/// to it just fine — which is exactly why the recipient-key SOURCE must be arch-fenced, not this primitive.
/// </remarks>
public interface ITenantDekWrapper
{
    /// <summary>
    /// Wrap <paramref name="tenantDek"/> for the recipient identified by <paramref name="recipientDmPublicKey"/>,
    /// bound to <paramref name="context"/>, signed by <paramref name="pairingAdminSigner"/>.
    /// </summary>
    /// <param name="tenantDek">The 32-byte tenant DEK (the per-tenant SQLCipher key) to wrap. G-2: this is the
    /// DERIVED per-tenant DEK, never a root seed.</param>
    /// <param name="recipientDmPublicKey">The recipient party's raw 32-byte X25519 DM PUBLIC key — in production,
    /// ONLY ever a key returned by <c>MemberRoster.DmPublicKeyOf</c> for an admitted party (G-1).</param>
    /// <param name="context">The authenticated context to bind (F-A) — the tenant, recipient, epoch, purpose.</param>
    /// <param name="pairingAdminPartyId">The in-roster pairing admin's party id — stamped for diagnostics/audit; the
    /// authority anchor is the admin KEY (the signer's <see cref="IOperationSigner.IssuerId"/>), checked against the
    /// roster at unwrap.</param>
    /// <param name="pairingAdminSigner">The in-roster pairing admin's signer (its <see cref="IOperationSigner.IssuerId"/>
    /// is stamped as the wrap's <see cref="WrappedTenantDek.PairingAdminPublicKey"/>).</param>
    /// <param name="recipientXWingPublicKey">The recipient party's raw 1216-byte X-Wing PUBLIC key
    /// (<c>pk_M ‖ pk_X</c>) — in production ONLY ever a key the verified roster bound for the party
    /// (<c>NodeTeamRoster.XWingPublicKeyOf</c>). The PER-RECIPIENT CAPABILITY gate (PQC Phase 2 increment
    /// 2c-iii-b; ADR 0004 Amendment 2 GATE condition 4b): when this is non-null AND the hybrid-write policy is
    /// ON, the wrap is boxed as suite #3 (X-Wing); otherwise it is boxed as suite #1 (the safe degrade — an
    /// X-Wing-INCAPABLE recipient, or the kill-switch off). OPTIONAL (default <c>null</c>) so every existing call
    /// site stays source-compatible and resolves to the suite-#1 behavior. The recipient X-Wing key is signed into
    /// the suite-#3 wrap's OUTER Ed25519 signature (the 2c-i envelope-signing fix), so a re-pointed recipient key
    /// fails closed at the recipient.</param>
    WrappedTenantDek Wrap(
        ReadOnlySpan<byte> tenantDek,
        ReadOnlySpan<byte> recipientDmPublicKey,
        TenantDekPairingContext context,
        string pairingAdminPartyId,
        IOperationSigner pairingAdminSigner,
        ReadOnlySpan<byte> recipientXWingPublicKey = default);
}

/// <summary>
/// MD-1 unwrap side — VERIFY a <see cref="WrappedTenantDek"/> (outer signature + context match), then OPEN the box
/// with the recipient's node-secret DM PRIVATE key to recover the tenant DEK. Fail-closed on every check.
/// </summary>
public interface ITenantDekUnwrapper
{
    /// <summary>
    /// Verify + unwrap. Returns the 32-byte tenant DEK iff: (1) the outer Ed25519 signature verifies for the stamped
    /// admin key; (2) the stamped admin key equals <paramref name="expectedAdminPublicKey"/> (the caller resolves
    /// this from the roster — so a wrap signed by a non-admin / stranger is rejected); (3) the bound context equals
    /// <paramref name="expectedContext"/> (replay / cross-tenant / cross-recipient / cross-epoch defence); and
    /// (4) the sealed box opens under the recipient's private key for the wrap's KEM suite. Returns <c>null</c> on
    /// ANY failure — never throws on a bad wrap, never returns a partially-trusted key.
    /// </summary>
    /// <remarks>
    /// <b>Suite acceptance (PQC Phase 2 / ADR 0004 Amendment 2).</b> A suite-#1 (X25519) wrap opens under
    /// <paramref name="recipientDmPrivateKey"/>. A suite-#3 (X-Wing) wrap opens under
    /// <paramref name="recipientXWingPrivateKeySeed"/> when the recipient is X-Wing-capable (it supplies the seed
    /// AND the implementation was composed with the X-Wing box) — the additive READ-before-write capability (GATE
    /// condition 4a): production can OPEN suite #3 BEFORE any writer EMITS it. An X-Wing-incapable recipient (no
    /// seed supplied) safely degrades — a suite-#3 wrap fails closed (null), the recipient stays suite-#1-only.
    /// Suite #2 and any unregistered/forward suite are always denied (no silent downgrade). Production WRITE paths
    /// still box suite #1 regardless — the writer flip is the separately-gated 2c-iii-b CP cutover.
    /// </remarks>
    /// <param name="wrap">The wrap to open.</param>
    /// <param name="expectedAdminPublicKey">The pairing admin's Ed25519 public key the recipient EXPECTS (resolved
    /// from the recipient's own verified roster — the authority anchor). A wrap whose stamped admin key differs is
    /// rejected even if its signature is internally valid.</param>
    /// <param name="expectedContext">The context the recipient EXPECTS (its own tenant id, its own party id, the
    /// epoch it is at, purpose "dek-pairing"). A wrap bound to a different context is rejected.</param>
    /// <param name="recipientDmPrivateKey">The recipient's 32-byte node-secret X25519 DM private key
    /// (HKDF(node-root, teamId) over the DM domain) — never leaves the recipient node. Used to open a suite-#1 wrap.</param>
    /// <param name="verifier">The Ed25519 operation verifier (canonical-JSON path).</param>
    /// <param name="recipientXWingPrivateKeySeed">The recipient's 32-byte node-secret X-Wing private-key seed
    /// (HKDF(node-root, teamId) over the X-Wing domain — see <c>IXWingSubkeyDerivation</c>) — never leaves the
    /// recipient node. Used to open a suite-#3 wrap. OPTIONAL: omit (default) for an X-Wing-incapable recipient, in
    /// which case a suite-#3 wrap fails closed and the recipient stays suite-#1-only.</param>
    byte[]? VerifyAndUnwrap(
        WrappedTenantDek wrap,
        PrincipalId expectedAdminPublicKey,
        TenantDekPairingContext expectedContext,
        ReadOnlySpan<byte> recipientDmPrivateKey,
        IOperationVerifier verifier,
        ReadOnlySpan<byte> recipientXWingPrivateKeySeed = default);
}
