using System.Collections.Generic;

namespace Harborline.Api.Foundation.Recovery;

/// <summary>
/// Versioned, on-the-wire cryptographic SUITE dimension for <see cref="EncryptedField"/>.
/// Realizes the ADR 0004 §1 algorithm-agility requirement at the field-encryption layer:
/// the cryptographic construction (AEAD + KDF) is a first-class, extensible, persisted
/// dimension so future swaps are ADDITIVE — a new suite is a new enum member + a new
/// encryptor/decryptor branch, never a data-format break.
/// </summary>
/// <remarks>
/// <para>
/// <b>Entry #1 is the ONLY registered suite today</b> and is exactly the algorithm
/// already shipped: AES-256-GCM with a per-tenant (or per-subject) DEK derived via HKDF.
/// This change adds NO new algorithm and changes NO runtime crypto behavior — it only makes
/// the format suite-aware with the existing algorithm as the sole default.
/// </para>
/// <para>
/// <b>Hard back-compat invariant.</b> Every <see cref="EncryptedField"/> ever written
/// pre-dates this dimension and therefore carries NO suite tag on the wire. A missing /
/// absent suite tag MUST be read as <see cref="AesGcm256Hkdf_v1"/> (the implicit legacy
/// default) so 100% of existing persisted ciphertext stays decryptable forever. See
/// <c>EncryptedFieldJsonConverter</c> for the wire-level realization.
/// </para>
/// <para>
/// <b>Adding a suite (Phase 2+).</b> Append a new member with the next integer value
/// (e.g. <c>HybridX25519MlKem_v1 = 2</c>), register it in <see cref="CryptoSuites.IsRegistered"/>,
/// and add the matching encrypt/decrypt branch. Existing data and the wire format are untouched.
/// Integer values are part of the persisted contract and MUST be stable — never renumber.
/// </para>
/// </remarks>
public enum CryptoSuite
{
    /// <summary>
    /// AES-256-GCM AEAD over an HKDF-derived DEK (the algorithm shipped before the suite
    /// dimension existed). This is the implicit default for any legacy untagged field.
    /// </summary>
    AesGcm256Hkdf_v1 = 1,
}

/// <summary>
/// Registry of the cryptographic suites this build understands. The seam's extension point:
/// a reader fails closed on an unrecognized suite rather than mis-decrypting under the wrong
/// construction, and the default for an untagged field is pinned to the legacy suite #1.
/// </summary>
public static class CryptoSuites
{
    /// <summary>
    /// The implicit suite for any <see cref="EncryptedField"/> written without a suite tag.
    /// Pinned to <see cref="CryptoSuite.AesGcm256Hkdf_v1"/> — the algorithm in use before this
    /// dimension existed — so legacy ciphertext remains decryptable.
    /// </summary>
    public const CryptoSuite LegacyDefault = CryptoSuite.AesGcm256Hkdf_v1;

    private static readonly HashSet<CryptoSuite> Registered = new()
    {
        CryptoSuite.AesGcm256Hkdf_v1,
    };

    /// <summary>
    /// True if <paramref name="suite"/> is a suite this build can encrypt/decrypt with.
    /// A reader uses this to fail closed on a forward-version or garbage suite tag.
    /// </summary>
    public static bool IsRegistered(CryptoSuite suite) => Registered.Contains(suite);
}
