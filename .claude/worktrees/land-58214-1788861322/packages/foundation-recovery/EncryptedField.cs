using System;
using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.Recovery;

/// <summary>
/// ADR 0046-A2 envelope for an at-rest encrypted scalar field
/// (TIN, SSN, payout-account number, signature payload, etc.).
/// </summary>
/// <remarks>
/// <para>
/// W#32 Phase 1 deliverable per ADR 0046-A2.1: net-new value type only,
/// no DI changes, no behavior. The encryptor / decryptor reference
/// implementations land in Phase 2 (ADR 0046-A4). Phase 1 deliberately
/// does not validate ranges — the structural contract is "three opaque
/// fields"; range invariants (e.g. <c>KeyVersion &gt;= 1</c>) are enforced
/// by the decryptor in Phase 2 per amendment A5.5.
/// </para>
/// <para>
/// <b>Crypto-suite dimension (ADR 0004 §1).</b> The <see cref="Suite"/> property
/// makes the cryptographic construction a first-class, versioned dimension so future
/// algorithm swaps are additive rather than a data-format break. The 3-argument
/// construction <c>new EncryptedField(ct, nonce, kv)</c> is preserved and implicitly
/// means <see cref="CryptoSuites.LegacyDefault"/> (suite #1, AES-256-GCM+HKDF) — every
/// existing call site keeps compiling unchanged. New code may set an explicit suite via
/// the 4-argument constructor or a <c>with</c> expression.
/// </para>
/// <para>
/// <b>JSON shape</b> (via <see cref="EncryptedFieldJsonConverter"/>):
/// <c>{ "ct": "&lt;base64url&gt;", "nonce": "&lt;base64url&gt;", "kv": &lt;int&gt;, "suite": &lt;int&gt; }</c>.
/// The <c>suite</c> tag is OPTIONAL on the wire: a value written without it (every blob
/// persisted before this dimension existed) is read as <see cref="CryptoSuites.LegacyDefault"/>.
/// Base64url (no padding) keeps the on-the-wire form URL-safe and avoids
/// quoted '+' / '/' / '=' characters that complicate downstream tooling.
/// </para>
/// <para>
/// <b>What this is NOT.</b> It is not a persistence column type, not a
/// PII-classification marker, and not a key-management primitive. It is
/// only the byte-shape that travels with an encrypted field.
/// </para>
/// </remarks>
[JsonConverter(typeof(EncryptedFieldJsonConverter))]
public readonly record struct EncryptedField(
    ReadOnlyMemory<byte> Ciphertext,
    ReadOnlyMemory<byte> Nonce,
    int KeyVersion)
{
    /// <summary>
    /// The cryptographic suite that sealed this field. Defaults to
    /// <see cref="CryptoSuites.LegacyDefault"/> (suite #1) so the 3-argument
    /// construction and every legacy untagged value resolve to the algorithm
    /// already in use. This member is NOT part of the positional primary
    /// constructor — that is deliberate, to keep all existing 3-arg call sites
    /// source-compatible.
    /// </summary>
    public CryptoSuite Suite { get; init; } = CryptoSuites.LegacyDefault;

    /// <summary>
    /// Constructs an envelope with an explicit cryptographic suite. Equivalent to the
    /// 3-argument constructor followed by <c>with { Suite = suite }</c>.
    /// </summary>
    public EncryptedField(ReadOnlyMemory<byte> ciphertext, ReadOnlyMemory<byte> nonce, int keyVersion, CryptoSuite suite)
        : this(ciphertext, nonce, keyVersion)
    {
        Suite = suite;
    }

    /// <summary>
    /// Returns a redacted summary that intentionally omits ciphertext and
    /// nonce bytes. Phase 1 invariant: <c>ToString()</c> must never expose
    /// the field contents — log-leak defense in depth.
    /// </summary>
    public override string ToString()
        => $"EncryptedField(Suite={Suite}, KeyVersion={KeyVersion}, CiphertextLength={Ciphertext.Length}, NonceLength={Nonce.Length})";
}
