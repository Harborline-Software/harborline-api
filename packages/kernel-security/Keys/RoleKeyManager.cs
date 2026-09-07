using System.Security.Cryptography;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.KeyDistribution;

namespace Harborline.Api.Kernel.Security.Keys;

/// <summary>
/// Default <see cref="IRoleKeyManager"/>. Role keys are 32 bytes (matches the key
/// size of ChaCha20-Poly1305 and AES-256-GCM — the expected Layer 2 field-level
/// AEADs from paper §11.2). Keys are generated with <see cref="RandomNumberGenerator"/>
/// and wrapped with X25519 sealed-box (<see cref="IX25519KeyAgreement"/>). Unwrapped
/// keys are cached in the OS keystore via <see cref="IKeystore"/>.
/// </summary>
public sealed class RoleKeyManager : IRoleKeyManager
{
    /// <summary>Role-key length in bytes.</summary>
    public const int RoleKeyLength = 32;

    /// <summary>Keystore name prefix for stored role keys.</summary>
    public const string KeystoreNamespace = "sunfish:role-key:";

    private readonly IX25519KeyAgreement _kem;
    private readonly IKeystore _keystore;
    private readonly IXWingSealedBox? _xwingBox;
    private readonly IHybridKemWritePolicy _writePolicy;

    /// <summary>
    /// Constructs a manager bound to the X25519 primitive, an OS keystore, and — optionally — the suite-#3
    /// X-Wing sealed box (<see cref="IXWingSealedBox"/>) + the hybrid-write policy (the 2c-iii-b kill-switch).
    /// The X-Wing box equips <see cref="UnwrapRoleKey"/> to <b>READ</b> a suite-#3
    /// (<see cref="KemSuite.XWingX25519MlKem768_v1"/>) bundle (2c-iii-a, read-before-write) AND — as of 2c-iii-b
    /// — equips <see cref="WrapRoleKey"/> to <b>WRITE</b> one when the policy is ON and a recipient X-Wing public
    /// key + signer are supplied. Both optional args default to the fail-closed state: with no X-Wing box a
    /// suite-#3 bundle can be neither written nor read; with the default disabled policy the writer stays
    /// suite-#1-only — so the manager behaves exactly as before this increment unless a host EXPLICITLY composes
    /// an enabled policy (the cutover is a deliberate act, not a default).
    /// </summary>
    public RoleKeyManager(
        IX25519KeyAgreement kem,
        IKeystore keystore,
        IXWingSealedBox? xwingBox = null,
        IHybridKemWritePolicy? writePolicy = null)
    {
        _kem = kem ?? throw new ArgumentNullException(nameof(kem));
        _keystore = keystore ?? throw new ArgumentNullException(nameof(keystore));
        _xwingBox = xwingBox;
        _writePolicy = writePolicy ?? DisabledHybridKemWritePolicy.Instance;
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> GenerateRoleKey()
    {
        var key = new byte[RoleKeyLength];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    /// <inheritdoc />
    public RoleKeyBundle WrapRoleKey(
        ReadOnlyMemory<byte> roleKey,
        string role,
        byte[] memberPublicKey,
        ReadOnlyMemory<byte> adminPrivateKey,
        byte[] adminPublicKey,
        ReadOnlyMemory<byte> memberXWingPublicKey = default,
        IOperationSigner? signer = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentNullException.ThrowIfNull(memberPublicKey);
        ArgumentNullException.ThrowIfNull(adminPublicKey);
        if (roleKey.Length != RoleKeyLength)
        {
            throw new ArgumentException(
                $"Role key must be {RoleKeyLength} bytes (was {roleKey.Length}).", nameof(roleKey));
        }

        // ── 2c-iii-b capability + kill-switch gate ────────────────────────────────────────────────────────────
        // Emit suite #3 (X-Wing) IFF (a) the hybrid-write policy is ON (the kill-switch), (b) the recipient is
        // X-Wing-CAPABLE (a non-empty X-Wing public key supplied), (c) this manager has the X-Wing box, AND (d) a
        // signer was supplied for the NIT-1 outer signature. ANY false ⇒ box suite #1 (the safe degrade). The
        // signer is MANDATORY for suite #3: X-Wing is an anonymous-sender KEM, so without the outer Ed25519
        // signature the bundle would lose the admin-sender authentication suite #1's authenticated box provides.
        var recipientIsXWingCapable = !memberXWingPublicKey.IsEmpty;
        var emitSuite3 = _writePolicy.HybridWritesEnabled && recipientIsXWingCapable && _xwingBox is not null;
        if (emitSuite3)
        {
            if (signer is null)
            {
                // A capable recipient + policy-on but no signer is a composition error (the host must supply the
                // admin signer to emit a NIT-1-signed suite-#3 bundle). Throw loudly rather than silently
                // degrading to suite #1 — a silent degrade would hide a mis-wired cutover.
                throw new ArgumentNullException(
                    nameof(signer),
                    "A suite-#3 (X-Wing) role-key wrap requires an Ed25519 signer for the NIT-1 outer signature " +
                    "(the role-key path's restoration of the admin-sender authentication X-Wing's anonymous-sender " +
                    "KEM drops). Supply the admin signer, or omit the X-Wing key / disable the policy to box suite #1.");
            }
            return WrapRoleKeySuite3(roleKey, role, memberXWingPublicKey, signer);
        }

        // ── Suite #1 (the legacy / safe-degrade authenticated box — byte-for-byte unchanged) ──────────────────
        var (ciphertext, nonce) = _kem.Box(roleKey.Span, memberPublicKey, adminPrivateKey.Span);
        return new RoleKeyBundle(
            Role: role,
            MemberPublicKey: memberPublicKey,
            WrappedKey: ciphertext,
            Nonce: nonce);
    }

    /// <summary>
    /// The 2c-iii-b suite-#3 (X-Wing) role-key WRITER + the NIT-1 outer Ed25519 signature. Boxes the role key to
    /// the recipient's X-Wing public key (anonymous-sender KEM), then signs the canonical
    /// <see cref="RoleKeyBundleSignable"/> — binding the role, the AEAD ciphertext + nonce, the KEM ciphertext,
    /// and the recipient X-Wing key. The signature RESTORES the admin-sender authentication the suite-#1
    /// authenticated box gave (and that X-Wing's anonymous KEM would otherwise drop): a suite-#3 bundle is honored
    /// only if this signature verifies for the expected admin key (checked on the read path). The AEAD tag still
    /// gives integrity + recipient-binding (X-Wing's combiner binds ct_X + pk_X; ML-KEM is IND-CCA2), so the two
    /// together = sender-authenticated + non-malleable + recipient-bound.
    /// </summary>
    private RoleKeyBundle WrapRoleKeySuite3(
        ReadOnlyMemory<byte> roleKey,
        string role,
        ReadOnlyMemory<byte> memberXWingPublicKey,
        IOperationSigner signer)
    {
        // _xwingBox is non-null (the WrapRoleKey gate required it). BoxXWing length-guards the public key.
        var recipientXWingPub = memberXWingPublicKey.ToArray();
        var (ciphertext, nonce, kemCiphertext) = _xwingBox!.BoxXWing(roleKey.Span, memberXWingPublicKey.Span);

        var signable = BuildRoleKeySignable(role, ciphertext, nonce, kemCiphertext, recipientXWingPub);
        var op = signer
            .SignAsync(signable, RoleKeySignableInstant, DeriveRoleKeySignableNonce(signable))
            .AsTask().GetAwaiter().GetResult();

        return new RoleKeyBundle(
            Role: role,
            MemberPublicKey: recipientXWingPub, // for suite #3 the recipient key on the wire IS the X-Wing pubkey.
            WrappedKey: ciphertext,
            Nonce: nonce)
        {
            Suite = KemSuite.XWingX25519MlKem768_v1,
            KemCiphertext = kemCiphertext,
            SignerPublicKey = signer.IssuerId.ToBase64Url(),
            Signature = op.Signature.ToBase64Url(),
        };
    }

    /// <inheritdoc />
    public ReadOnlyMemory<byte> UnwrapRoleKey(
        RoleKeyBundle bundle,
        ReadOnlyMemory<byte> memberPrivateKey,
        byte[] adminPublicKey,
        ReadOnlyMemory<byte> memberXWingPrivateKeySeed = default,
        IOperationVerifier? signatureVerifier = null,
        byte[]? expectedSignerPublicKey = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(adminPublicKey);

        // KEM-suite policy boundary (ADR 0004 §1 / BL-01 2b / 2c-iii) — fail closed on any suite this build+recipient
        // cannot open, BEFORE touching the box, so an unknown/forward suite is never silently downgraded to the legacy
        // X25519 construction. A legacy untagged bundle carries Suite == LegacyDefault (suite #1) and opens via the
        // X25519 path.
        //
        // READ-BEFORE-WRITE (ADR 0004 Amendment 2 GATE condition 4a): suite #3 (X-Wing) is now ACCEPTED iff the
        // recipient is X-Wing-capable — it supplies a well-formed X-Wing private-key seed AND this manager was
        // composed with the X-Wing box. This is the additive read capability; no production role-key writer emits
        // suite #3 yet (the writer flip + the NIT-1 RoleKeyBundle-KEM-ct-signing decision is 2c-iii-b). An
        // X-Wing-INCAPABLE recipient (no seed) still fails closed on a suite-#3 bundle (per-recipient capability
        // gate — safe degrade). Suite #2 is still denied (D1 picked X-Wing for production; suite #2 is a reference).
        if (!KemSuites.IsRegistered(bundle.Suite))
        {
            throw new CryptographicException(
                $"Role key bundle KEM suite '{bundle.Suite}' is unregistered — fail-closed: no silent downgrade " +
                "to the legacy X25519 construction.");
        }

        var xwingCapable = _xwingBox is not null
            && memberXWingPrivateKeySeed.Length == _xwingBox.PrivateKeySeedLength;

        byte[]? plaintext;
        if (bundle.Suite == KemSuite.XWingX25519MlKem768_v1 && xwingCapable)
        {
            // Suite #3 (X-Wing) read. X-Wing carries no separate sender-ephemeral X25519 public key on the wire
            // (ct_X is INSIDE the KEM ciphertext, in `KemCiphertext`), so the open inputs are {WrappedKey (AEAD),
            // Nonce, KemCiphertext, recipient X-Wing seed}. `_xwingBox` is non-null here (xwingCapable required it).
            if (_xwingBox is null
                || bundle.KemCiphertext is not { } kemCt || kemCt.Length != _xwingBox.KemCiphertextLength
                || bundle.Nonce is not { } nonce || nonce.Length != _xwingBox.NonceLength
                || bundle.WrappedKey is not { Length: > 0 })
            {
                throw new CryptographicException(
                    "Suite-#3 role key bundle is malformed (missing/wrong-length KEM ciphertext, nonce, or wrapped " +
                    "key) — fail-closed.");
            }

            // NIT-1 (2c-iii-b) — VERIFY the outer Ed25519 signature BEFORE opening the box. X-Wing is an
            // anonymous-sender KEM, so the AEAD tag alone proves integrity + recipient-binding but NOT
            // admin-sender authenticity (unlike the suite-#1 authenticated box, opened under adminPublicKey). The
            // outer signature restores it: a suite-#3 bundle is honored ONLY if (a) it carries a signer + signature,
            // (b) the stamped signer equals the EXPECTED admin key (the authority anchor the caller resolved from
            // its roster), and (c) the signature verifies over the canonical signable binding role + KEM ciphertext
            // + recipient X-Wing key + AEAD ciphertext + nonce. A re-pointed recipient key / substituted KEM
            // ciphertext / swapped role / stranger signer all fail closed HERE, before any decapsulation.
            VerifyRoleKeySuite3SignatureOrThrow(bundle, kemCt, signatureVerifier, expectedSignerPublicKey);

            plaintext = _xwingBox.OpenXWing(bundle.WrappedKey, bundle.Nonce, kemCt, memberXWingPrivateKeySeed.Span);
        }
        else if (bundle.Suite == KemSuite.X25519SealedBox_v1)
        {
            plaintext = _kem.OpenBox(
                bundle.WrappedKey,
                bundle.Nonce,
                adminPublicKey,
                memberPrivateKey.Span);
        }
        else
        {
            // Suite #2, or suite #3 for an X-Wing-INCAPABLE recipient → fail closed (no silent downgrade, safe
            // degrade for a recipient with no X-Wing key).
            throw new CryptographicException(
                $"Role key bundle KEM suite '{bundle.Suite}' is not openable by this recipient — it is either the " +
                "suite-#2 reference construction (not the production read path) or a suite-#3 bundle for a recipient " +
                "with no X-Wing key (per-recipient capability gate; the recipient stays suite-#1-only).");
        }

        if (plaintext is null)
        {
            throw new CryptographicException(
                "Role key bundle authentication failed — bundle was not produced by the expected admin " +
                "for this recipient, or it was tampered with in transit.");
        }
        if (plaintext.Length != RoleKeyLength)
        {
            throw new CryptographicException(
                $"Unwrapped role key has unexpected length {plaintext.Length} (expected {RoleKeyLength}).");
        }
        return plaintext;
    }

    /// <inheritdoc />
    public Task StoreRoleKeyAsync(string role, ReadOnlyMemory<byte> roleKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        return _keystore.SetKeyAsync(KeystoreName(role), roleKey, ct);
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>?> GetRoleKeyAsync(string role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(role);
        return _keystore.GetKeyAsync(KeystoreName(role), ct);
    }

    private static string KeystoreName(string role) => KeystoreNamespace + role;

    // ── NIT-1 suite-#3 role-key signing (2c-iii-b) ─────────────────────────────────────────────────────────────
    // The role-key counterpart of the tenant-DEK envelope signing. Deterministic instant + content-derived nonce
    // (the same RosterSigning/TenantDekWrapper discipline) so wrap + unwrap reconstruct byte-identical signable
    // bytes; freshness/replay protection is the recipient-key binding + the fresh KEM nonce, not a wall-clock.

    private static readonly DateTimeOffset RoleKeySignableInstant = DateTimeOffset.UnixEpoch;

    /// <summary>Build the canonical suite-#3 signable (shared by wrap + unwrap so the signed bytes match exactly).</summary>
    private static RoleKeyBundleSignable BuildRoleKeySignable(
        string role,
        ReadOnlySpan<byte> wrappedKey,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> kemCiphertext,
        ReadOnlySpan<byte> recipientXWingPublicKey) =>
        new(
            Role: role,
            WrappedKeyB64Url: ToBase64Url(wrappedKey),
            NonceB64Url: ToBase64Url(nonce),
            KemCiphertextB64Url: ToBase64Url(kemCiphertext),
            RecipientXWingPublicKeyB64Url: ToBase64Url(recipientXWingPublicKey));

    /// <summary>Derive the signable nonce deterministically from the signable content (HKDF-SHA256 → 16-byte Guid).</summary>
    private static Guid DeriveRoleKeySignableNonce(RoleKeyBundleSignable s)
    {
        var material = System.Text.Encoding.UTF8.GetBytes(
            "sunfish-role-key-bundle-nonce-v1:" + s.Role + ":" + s.WrappedKeyB64Url + ":" + s.NonceB64Url + ":"
            + s.KemCiphertextB64Url + ":" + s.RecipientXWingPublicKeyB64Url);
        Span<byte> nonceBytes = stackalloc byte[16];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm: material,
            output: nonceBytes,
            salt: ReadOnlySpan<byte>.Empty,
            info: "sunfish-role-key-bundle-nonce-v1"u8);
        return new Guid(nonceBytes);
    }

    /// <summary>
    /// Verify the NIT-1 outer signature of a suite-#3 bundle, or throw <see cref="CryptographicException"/>
    /// (fail-closed). Confirms: the bundle carries a signer + signature; a verifier + expected signer were
    /// supplied; the stamped signer equals the expected admin key (authority anchor); and the signature verifies
    /// over the reconstructed canonical signable (role + AEAD ciphertext + nonce + KEM ciphertext + recipient
    /// X-Wing key). Called BEFORE the box opens, so an unauthentic suite-#3 bundle never reaches decapsulation.
    /// </summary>
    private static void VerifyRoleKeySuite3SignatureOrThrow(
        RoleKeyBundle bundle,
        byte[] kemCiphertext,
        IOperationVerifier? signatureVerifier,
        byte[]? expectedSignerPublicKey)
    {
        if (signatureVerifier is null || expectedSignerPublicKey is null)
        {
            throw new CryptographicException(
                "Suite-#3 role key bundle requires a signature verifier + expected signer key (the NIT-1 outer " +
                "signature) — fail-closed: a suite-#3 bundle is never opened without verifying its admin-sender " +
                "authentication.");
        }
        if (string.IsNullOrEmpty(bundle.SignerPublicKey) || string.IsNullOrEmpty(bundle.Signature))
        {
            throw new CryptographicException(
                "Suite-#3 role key bundle is missing its outer signer/signature (NIT-1) — fail-closed.");
        }

        PrincipalId stampedSigner;
        Signature signature;
        try
        {
            stampedSigner = PrincipalId.FromBase64Url(bundle.SignerPublicKey);
            signature = Signature.FromBase64Url(bundle.Signature);
        }
        catch (FormatException)
        {
            throw new CryptographicException(
                "Suite-#3 role key bundle has a malformed signer key / signature — fail-closed.");
        }

        // Authority anchor: the stamped signer MUST be the EXPECTED admin key (resolved by the caller from its
        // verified roster). A bundle signed by a stranger — even with an internally-valid signature — is rejected.
        if (!stampedSigner.AsSpan().SequenceEqual(expectedSignerPublicKey))
        {
            throw new CryptographicException(
                "Suite-#3 role key bundle signer is not the expected admin key — fail-closed.");
        }

        var signable = BuildRoleKeySignable(
            bundle.Role, bundle.WrappedKey, bundle.Nonce, kemCiphertext, bundle.MemberPublicKey);
        var op = new SignedOperation<RoleKeyBundleSignable>(
            Payload: signable,
            IssuerId: stampedSigner,
            IssuedAt: RoleKeySignableInstant,
            Nonce: DeriveRoleKeySignableNonce(signable),
            Signature: signature);
        if (!signatureVerifier.Verify(op))
        {
            throw new CryptographicException(
                "Suite-#3 role key bundle outer signature did not verify (a re-pointed recipient key, a substituted " +
                "KEM ciphertext, a swapped role, or a tampered ciphertext breaks it) — fail-closed.");
        }
    }

    /// <summary>base64url (no padding) encode of a raw byte span — for the canonical-JSON-stable signable fields.</summary>
    private static string ToBase64Url(ReadOnlySpan<byte> bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
