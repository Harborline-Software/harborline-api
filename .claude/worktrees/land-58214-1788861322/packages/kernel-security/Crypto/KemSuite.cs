using System.Collections.Generic;

namespace Harborline.Api.Kernel.Security.Crypto;

/// <summary>
/// Versioned, on-the-wire cryptographic SUITE dimension for the kernel-security <b>sealed box</b>
/// (the KEM / key-agreement construction that wraps load-bearing key material — the tenant DEK
/// (<see cref="Harborline.Api.Kernel.Security.KeyDistribution.WrappedTenantDek"/>), role keys
/// (<see cref="Harborline.Api.Kernel.Security.Keys.RoleKeyBundle"/>), and the recovery root-seed
/// envelope (<c>Harborline.Api.Foundation.Recovery.TrusteeEncryptedSeed</c>)).
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>KEM-axis</b> companion to <c>Harborline.Api.Foundation.Recovery.CryptoSuite</c> (#1481).
/// That sibling versions the <i>at-rest field AEAD</i> dimension (AES-256-GCM); this one versions the
/// <i>key-distribution sealed-box</i> dimension (today X25519, tomorrow a PQC hybrid). They are two
/// independent algorithm axes per the ADR 0004 §1 confidentiality/KEM split, so they are deliberately
/// two enums in two packages at the correct tiers — NOT one shared type.
/// </para>
/// <para>
/// <b>Layering — why this enum is kernel-local.</b> The dependency direction is one-way:
/// <c>foundation-recovery</c> → <c>kernel-security</c> (foundation is the higher tier; kernel is the
/// lower). <see cref="Harborline.Api.Kernel.Security.KeyDistribution.WrappedTenantDek"/> and
/// <see cref="Harborline.Api.Kernel.Security.Keys.RoleKeyBundle"/> live in <c>kernel-security</c>;
/// <c>TrusteeEncryptedSeed</c> lives in <c>foundation-recovery</c> but its box is produced by
/// kernel-security's <see cref="IX25519KeyAgreement"/>. Putting the suite enum in <c>kernel-security</c>
/// lets all three envelopes reference it: the two kernel envelopes directly, and the foundation
/// envelope via the legal downward dependency. The reverse — a suite type in foundation that kernel
/// referenced — would be an illegal upward dependency.
/// </para>
/// <para>
/// <b>Entry #1 is the ONLY registered suite today</b> and is exactly the construction already shipped:
/// an X25519 NaCl-style sealed box (X25519 ECDH → HKDF-SHA256 → ChaCha20-Poly1305), per
/// <see cref="IX25519KeyAgreement"/>. This change adds NO new algorithm and changes NO runtime crypto
/// behavior — it only makes the box envelopes suite-aware with the existing algorithm as the sole
/// default.
/// </para>
/// <para>
/// <b>Hard back-compat invariant.</b> Every box envelope ever written pre-dates this dimension and
/// therefore carries NO suite tag on the wire. A missing / absent suite tag MUST be read as
/// <see cref="X25519SealedBox_v1"/> (the implicit legacy default) so 100% of existing wrapped DEKs,
/// role keys, and recovery seeds stay openable forever. The envelope records realize this by defaulting
/// their suite member to <see cref="KemSuites.LegacyDefault"/>.
/// </para>
/// <para>
/// <b>Suite #2 added (Phase 2b).</b> <see cref="HybridX25519MlKem_v1"/> (value 2) is registered:
/// the X25519 ‖ ML-KEM-768 TLS-concat hybrid box (<see cref="HybridSealedBox"/> over
/// <see cref="IMlKem768"/>). The wire format and all existing suite-#1 data are untouched.
/// </para>
/// <para>
/// <b>Suite #3 added (Phase 2c-i, the PRODUCTION construction).</b> <see cref="XWingX25519MlKem768_v1"/>
/// (value 3) is registered: the standardized IETF/CFRG X-Wing KEM (X25519 + ML-KEM-768, KAT-vectored)
/// fed through the same AEAD-box pattern (<see cref="XWingSealedBox"/> over <see cref="IXWingKem"/>). Per
/// CIC decision D1 (ADR 0004 Amendment 2) this is the production hybrid, chosen over a bespoke combiner;
/// suite #2 stays as a tested reference and suite #1 stays for back-compat. Integer values are part of the
/// persisted contract and MUST be stable — never renumber. <b>Production call sites still box as suite
/// #1</b>; the recipient key-model + policy-gated cutover that lets a recipient be boxed-for as suite #3
/// is increment 2c-iii (eager re-box sweep = 2c-ii).
/// </para>
/// </remarks>
public enum KemSuite
{
    /// <summary>
    /// X25519 sealed box: X25519 ECDH shared secret → HKDF-SHA256 → ChaCha20-Poly1305 AEAD (the
    /// construction shipped before the suite dimension existed; see <see cref="IX25519KeyAgreement"/>).
    /// This is the implicit default for any legacy untagged envelope. <b>Shor-breakable</b> — the
    /// hybrid suite #2 hardens it against a CRQC, but suite #1 stays registered for back-compat.
    /// </summary>
    X25519SealedBox_v1 = 1,

    /// <summary>
    /// PQC <b>hybrid</b> sealed box (PQC Phase 2 / BL-01, increment 2b): the suite-#1 X25519 ECDH shared
    /// secret CONCATENATED with an ML-KEM-768 (FIPS-203) encapsulated secret, fed through the same
    /// HKDF-SHA256 → ChaCha20-Poly1305 AEAD under a hybrid-specific HKDF <c>info</c> label. The
    /// TLS-1.3 <c>X25519MLKEM768</c> hybrid pattern — <b>confidential if EITHER half holds</b>: a
    /// classical/CRQC attacker who breaks X25519 still cannot derive the ML-KEM secret, and vice versa,
    /// so the HKDF input (hence the AEAD key) stays secret unless BOTH KEMs are broken. Implemented by
    /// <see cref="HybridSealedBox"/> over <see cref="IMlKem768"/>; the wire envelope additionally carries
    /// the 1088-byte ML-KEM ciphertext. Suite #1 is byte-for-byte unchanged — this is strictly additive.
    /// </summary>
    HybridX25519MlKem_v1 = 2,

    /// <summary>
    /// PQC <b>production</b> hybrid sealed box (PQC Phase 2 / BL-01, increment 2c-i; CIC decision D1, ADR
    /// 0004 Amendment 2): the standardized IETF/CFRG <b>X-Wing</b> KEM (<c>draft-connolly-cfrg-xwing-kem</c>
    /// = X25519 + ML-KEM-768) feeding HKDF-SHA256 → ChaCha20-Poly1305 under a suite-#3-specific HKDF
    /// <c>info</c> label. Unlike suite #2's TLS-concat combiner, X-Wing is a <i>named, IND-CCA-proven,
    /// KAT-vectored</i> construction whose combiner
    /// (<c>SHA3-256(ss_M ‖ ss_X ‖ ct_X ‖ pk_X ‖ XWingLabel)</c>) binds the X25519 ciphertext + recipient
    /// public key into the shared secret — <b>confidential if EITHER half holds</b>. The production KEM
    /// chosen over a bespoke combiner (D1); ML-KEM-1024 / cat-5 is a DEFERRED triggered backlog suite-add.
    /// Implemented by <see cref="XWingSealedBox"/> over <see cref="IXWingKem"/>; the wire envelope carries
    /// the 1120-byte X-Wing ciphertext (<c>ct_M ‖ ct_X</c>). Suites #1 and #2 are byte-for-byte unchanged —
    /// this is strictly additive. <b>Production write paths still box as suite #1</b>; the recipient
    /// key-model + policy-gated cutover that lets a recipient be boxed-for as suite #3 is increment 2c-iii
    /// (and the eager re-box sweep is 2c-ii). Integer value is part of the persisted contract — never
    /// renumber.
    /// </summary>
    XWingX25519MlKem768_v1 = 3,
}

/// <summary>
/// Registry of the sealed-box KEM suites this build understands. The seam's extension point and policy
/// boundary: a reader fails closed on an unrecognized suite rather than mis-opening a box under the
/// wrong construction, and the default for an untagged envelope is pinned to the legacy suite #1.
/// </summary>
public static class KemSuites
{
    /// <summary>
    /// The implicit suite for any sealed-box envelope written without a suite tag. Pinned to
    /// <see cref="KemSuite.X25519SealedBox_v1"/> — the construction in use before this dimension
    /// existed — so legacy wrapped key material remains openable.
    /// </summary>
    public const KemSuite LegacyDefault = KemSuite.X25519SealedBox_v1;

    private static readonly HashSet<KemSuite> Registered = new()
    {
        KemSuite.X25519SealedBox_v1,
        KemSuite.HybridX25519MlKem_v1,
        KemSuite.XWingX25519MlKem768_v1,
    };

    /// <summary>
    /// True if <paramref name="suite"/> is a suite this build can box/open with. A reader uses this to
    /// fail closed on a forward-version or garbage suite tag — it MUST deny rather than silently
    /// downgrade an unknown tagged value to the legacy suite #1.
    /// </summary>
    public static bool IsRegistered(KemSuite suite) => Registered.Contains(suite);
}
