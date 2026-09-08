using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Security.Crypto;

namespace Harborline.Api.Kernel.Security.KeyDistribution;

/// <summary>
/// A tenant DEK wrapped for ONE admitted party — the MD-1 wire/at-rest envelope (joint ADR 0113+0117 amendment,
/// 2026-06-24). The tenant DEK (the per-tenant SQLCipher key) is sealed under the recipient's <b>roster-bound</b>
/// X25519 DM public key via the 0046-A6 sealed box, then the whole {ciphertext + context} is covered by an OUTER
/// Ed25519 signature by a roster-bound pairing admin (encrypt-then-sign, the SC-3 discipline).
/// </summary>
/// <remarks>
/// <para>
/// <b>This envelope is the F-A correction made concrete.</b> The raw X25519 box proves only "someone holding a
/// private key for this recipient pubkey made a ciphertext" — anonymous sender, no context. The two MD-1 additions
/// turn that into an authenticated, context-bound grant:
/// <list type="number">
///   <item><b>Context binding</b> (<see cref="TenantDekPairingContext"/>): the (tenantId, recipientPartyId,
///     homeEpoch, purpose) the wrap is FOR. The recipient re-asserts this against what it expects, rejecting a
///     replayed / cross-tenant / cross-recipient / cross-epoch blob.</item>
///   <item><b>Outer signature</b> (<see cref="Signature"/> by <see cref="PairingAdminPartyId"/> /
///     <see cref="PairingAdminPublicKey"/>): the roster-bound admin attests "I, an in-roster pairing admin,
///     minted THIS ciphertext for THIS context." A verifier checks the signature against the admin's
///     roster-bound principal key, so a forged/tampered wrap (ciphertext or context changed) fails closed.</item>
/// </list>
/// </para>
/// <para>
/// <b>Encrypt-then-sign ordering.</b> The signature covers the CIPHERTEXT (not the plaintext DEK) plus the context —
/// the ciphertext exists before the signature is computed. So no plaintext DEK is ever reachable in the signable
/// bytes, and the SC-3 arch-test "no plaintext DEK reaches a Bridge-bound payload" extends to this envelope by
/// construction.
/// </para>
/// <para>
/// <b>Nothing secret rides here in the clear.</b> Ciphertext, nonce, sender ephemeral PUBLIC key, the (public)
/// context, and the admin's (public) signature + key. The tenant DEK is recoverable ONLY by the holder of the
/// recipient party's node-secret DM PRIVATE key (<see cref="Harborline.Api.Kernel.Security.KeyDistribution.ITenantDekUnwrapper"/>),
/// which never leaves the recipient node.
/// </para>
/// </remarks>
/// <param name="Context">The authenticated context the wrap is bound to (F-A). Signed over; re-asserted at unwrap.</param>
/// <param name="Ciphertext">The X25519 sealed-box ciphertext of the 32-byte tenant DEK (plaintext length + 16 tag).</param>
/// <param name="Nonce">The 24-byte sealed-box nonce returned by <c>Box</c>.</param>
/// <param name="SenderEphemeralPublicKey">The raw 32-byte X25519 PUBLIC key of the sender's per-wrap ephemeral
/// keypair — the <c>senderPublicKey</c> input the recipient passes to <c>OpenBox</c>. Ephemeral (fresh per wrap),
/// so a sender-key compromise cannot retro-open prior wraps and the wrap is unlinkable to the admin's long-term key.
/// </param>
/// <param name="PairingAdminPartyId">The in-roster pairing admin that signed this wrap (the authority anchor).</param>
/// <param name="PairingAdminPublicKey">The admin's Ed25519 PRINCIPAL public key — the verifier checks the signature
/// against this AND the caller checks it equals the admin's roster-bound key (so a stranger's signature is rejected).
/// </param>
/// <param name="Signature">The outer Ed25519 signature over the canonical signable form (ciphertext+nonce+sender
/// pubkey+context), produced by the pairing admin's signer.</param>
public sealed record WrappedTenantDek(
    TenantDekPairingContext Context,
    byte[] Ciphertext,
    byte[] Nonce,
    byte[] SenderEphemeralPublicKey,
    string PairingAdminPartyId,
    string PairingAdminPublicKey,
    string Signature)
{
    /// <summary>
    /// The sealed-box KEM suite that produced <see cref="Ciphertext"/> (ADR 0004 §1 KEM axis).
    /// Defaults to <see cref="KemSuites.LegacyDefault"/> (suite #1, the X25519 sealed box) so the
    /// 7-argument positional construction and every legacy untagged wrap resolve to the construction
    /// already in use. This member is NOT part of the positional primary constructor — deliberately,
    /// to keep all existing call sites source-compatible. New code may set an explicit suite via a
    /// <c>with</c> expression once a second suite exists (Phase 2b).
    /// </summary>
    /// <remarks>
    /// <b>Back-compat seam.</b> A wrap deserialized from a pre-this-change persisted form carries no
    /// suite tag; the reader leaves this at <see cref="KemSuites.LegacyDefault"/>. The unwrap policy
    /// boundary (a future hybrid-aware unwrapper) MUST fail closed on an unregistered suite rather than
    /// silently treating an unknown tagged value as suite #1 — see <see cref="KemSuites.IsRegistered"/>.
    /// </remarks>
    public KemSuite Suite { get; init; } = KemSuites.LegacyDefault;

    /// <summary>
    /// The X-Wing KEM ciphertext (suite #3 only, ADR 0004 Amendment 2 GATE condition 2 / D1). For a
    /// suite-#3 (<see cref="KemSuite.XWingX25519MlKem768_v1"/>) wrap this carries the 1120-byte X-Wing
    /// ciphertext (<c>ct_M ‖ ct_X</c>) the recipient needs to recover the X-Wing shared secret. <c>null</c>
    /// for suites #1/#2 (which carry no separate KEM ciphertext on this envelope). <b>Signed over</b> — it
    /// is bound into <see cref="TenantDekWrapSignable"/> so a substituted/tampered KEM ciphertext under an
    /// otherwise-valid wrap fails closed (the envelope-signing fix; the signature IS the transcript).
    /// </summary>
    public byte[]? KemCiphertext { get; init; }

    /// <summary>
    /// The recipient's X-Wing PUBLIC key (suite #3 only, ADR 0004 Amendment 2 GATE condition 2 / D1). For
    /// a suite-#3 wrap this carries the 1216-byte X-Wing encapsulation key (<c>pk_M ‖ pk_X</c> — the
    /// ML-KEM-768 public key concatenated with the X25519 public key) the wrap was sealed FOR. <c>null</c>
    /// for suites #1/#2. <b>Signed over</b> so a wrap re-pointed at a different recipient ML-KEM key fails
    /// closed. (Named for the gate's "recipient ML-KEM public key" requirement; the X-Wing public key
    /// embeds the recipient ML-KEM public key as its first 1184 bytes.)
    /// </summary>
    public byte[]? RecipientMlKemPublicKey { get; init; }
}

/// <summary>
/// The canonical SIGNABLE payload the outer Ed25519 signature covers — everything that authenticates a
/// <see cref="WrappedTenantDek"/> EXCEPT the signature itself. Serialized via the proven
/// <see cref="Harborline.Api.Foundation.Crypto.IOperationSigner"/> canonical-JSON path (byte-stable, cross-language), the
/// same discipline <c>RosterSigning</c> (in the sibling foundation-identity-atlas package) uses for admissions — so the wrap
/// is "never trusted bare", mirroring the roster's sign-around-the-key pattern.
/// </summary>
/// <remarks>
/// The ciphertext / nonce / sender public key are carried as base64url strings so the canonical-JSON form is
/// deterministic across platforms (byte arrays have no canonical JSON form). The CONTEXT is signed in full, so a
/// tampered tenantId / recipientPartyId / homeEpoch / purpose breaks the signature.
/// </remarks>
/// <param name="CiphertextB64Url">base64url of the sealed-box ciphertext.</param>
/// <param name="NonceB64Url">base64url of the 24-byte nonce.</param>
/// <param name="SenderEphemeralPublicKeyB64Url">base64url of the sender ephemeral X25519 public key.</param>
/// <param name="TenantId">Bound tenant id (from the context).</param>
/// <param name="RecipientPartyId">Bound recipient party id (from the context).</param>
/// <param name="HomeEpoch">Bound home epoch (from the context).</param>
/// <param name="Purpose">Bound purpose label (from the context — always "dek-pairing").</param>
/// <param name="KemCiphertextB64Url">base64url of the suite-#3 X-Wing KEM ciphertext (ADR 0004 Amendment 2
/// GATE condition 2). <c>null</c> — hence OMITTED from the canonical JSON — for suites #1/#2, so the
/// suite-#1 signable bytes are byte-for-byte unchanged. Present (signed-over) for a suite-#3 wrap so a
/// substituted/tampered KEM ciphertext fails closed.</param>
/// <param name="RecipientMlKemPublicKeyB64Url">base64url of the recipient's suite-#3 X-Wing public key
/// (which embeds the recipient ML-KEM public key; ADR 0004 Amendment 2 GATE condition 2). <c>null</c> —
/// OMITTED — for suites #1/#2. Present (signed-over) for a suite-#3 wrap so a wrap re-pointed at a
/// different recipient key fails closed.</param>
public sealed record TenantDekWrapSignable(
    string CiphertextB64Url,
    string NonceB64Url,
    string SenderEphemeralPublicKeyB64Url,
    string TenantId,
    string RecipientPartyId,
    long HomeEpoch,
    string Purpose,
    // Null-when-suite-#1/#2 → OMITTED by the JCS canonicalizer (WhenWritingNull), so a legacy/suite-#1
    // signable serializes EXACTLY as before this change. Present only for suite #3 (the envelope-signing
    // fix that binds the KEM ciphertext + recipient key into the signed bytes).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? KemCiphertextB64Url = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RecipientMlKemPublicKeyB64Url = null);
