using System.Security.Cryptography;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Security.Crypto;

namespace Harborline.Api.Kernel.Security.KeyDistribution;

/// <summary>
/// Default <see cref="ITenantDekWrapper"/> + <see cref="ITenantDekUnwrapper"/> — the MD-1 tenant-DEK pairing
/// construction (joint ADR 0113+0117 amendment, 2026-06-24). Composes the 0046-A6 X25519 sealed box
/// (<see cref="IX25519KeyAgreement"/>) for confidentiality with an OUTER Ed25519 signature + context binding
/// (the proven <c>RosterSigning</c>-style canonical-JSON signing path) for authenticity — the F-A correction.
/// </summary>
/// <remarks>
/// <para>
/// <b>encrypt-then-sign.</b> Wrap order is: (1) generate a fresh EPHEMERAL X25519 keypair; (2) <c>Box</c> the DEK
/// under the recipient's roster-bound DM public key with the ephemeral private key (the 0046-A6 sealed box);
/// (3) build the canonical <see cref="TenantDekWrapSignable"/> over the resulting ciphertext + nonce + ephemeral
/// public key + the bound context; (4) sign THAT with the pairing admin's Ed25519 signer. The signature covers the
/// CIPHERTEXT, so no plaintext DEK is ever in the signable bytes (SC-3).
/// </para>
/// <para>
/// <b>verify-then-open (fail-closed).</b> Unwrap order is the inverse and short-circuits on the FIRST failure:
/// reconstruct the signable, verify the outer signature against the stamped admin key, confirm the stamped admin key
/// is the EXPECTED roster-bound admin, confirm the bound context equals the EXPECTED context, THEN open the box. A
/// caller never reaches the plaintext DEK unless every authenticity + context gate passed.
/// </para>
/// <para>
/// <b>Deterministic signable instant.</b> Like <c>RosterSigning</c>, the signed envelope pins a FIXED
/// issued-at + a content-derived nonce so the recipient reconstructs byte-identical signable bytes (the freshness /
/// replay protection comes from the bound context + the fresh sealed-box nonce, not from a wall-clock instant).
/// </para>
/// </remarks>
public sealed class TenantDekWrapper : ITenantDekWrapper, ITenantDekUnwrapper
{
    /// <summary>Tenant DEK length in bytes (32 = the SQLCipher raw-key size).</summary>
    public const int TenantDekLength = 32;

    private readonly IX25519KeyAgreement _kem;
    private readonly IXWingSealedBox? _xwingBox;
    private readonly IHybridKemWritePolicy _writePolicy;

    /// <summary>
    /// Construct over the X25519 sealed-box primitive (0046-A6) and, optionally, the suite-#3 X-Wing sealed
    /// box (<see cref="IXWingSealedBox"/>) + the hybrid-write policy (the 2c-iii-b kill-switch). The X-Wing box
    /// equips this type to <b>READ</b> a suite-#3 (<see cref="KemSuite.XWingX25519MlKem768_v1"/>) wrap (2c-iii-a,
    /// read-before-write) AND — as of 2c-iii-b — to <b>WRITE</b> one: <see cref="Wrap"/> emits suite #3 when
    /// <paramref name="writePolicy"/> is ON and a recipient X-Wing public key is supplied (per-recipient
    /// capability gate). Both args are OPTIONAL: with no X-Wing box a suite-#3 wrap can be neither written nor
    /// read (it fails closed); with the default disabled policy the writer stays suite-#1-only. The policy
    /// defaults to <see cref="DisabledHybridKemWritePolicy.Instance"/> — the fail-closed pre-cutover state — so
    /// the writer behaves exactly as before this increment unless a host EXPLICITLY composes an enabled policy.
    /// </summary>
    public TenantDekWrapper(
        IX25519KeyAgreement kem,
        IXWingSealedBox? xwingBox = null,
        IHybridKemWritePolicy? writePolicy = null)
    {
        _kem = kem ?? throw new ArgumentNullException(nameof(kem));
        _xwingBox = xwingBox;
        _writePolicy = writePolicy ?? DisabledHybridKemWritePolicy.Instance;
    }

    /// <inheritdoc />
    public WrappedTenantDek Wrap(
        ReadOnlySpan<byte> tenantDek,
        ReadOnlySpan<byte> recipientDmPublicKey,
        TenantDekPairingContext context,
        string pairingAdminPartyId,
        IOperationSigner pairingAdminSigner,
        ReadOnlySpan<byte> recipientXWingPublicKey = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingAdminPartyId);
        ArgumentNullException.ThrowIfNull(pairingAdminSigner);
        if (tenantDek.Length != TenantDekLength)
        {
            throw new ArgumentException(
                $"Tenant DEK must be {TenantDekLength} bytes (was {tenantDek.Length}).", nameof(tenantDek));
        }
        if (context.Purpose != TenantDekPairingContext.DekPairing)
        {
            throw new ArgumentException(
                $"Pairing context purpose must be '{TenantDekPairingContext.DekPairing}'.", nameof(context));
        }

        // ── 2c-iii-b capability + kill-switch gate ────────────────────────────────────────────────────────────
        // Emit suite #3 (X-Wing) IFF (a) the hybrid-write policy is ON (the kill-switch — GATE condition 5) AND
        // (b) the recipient is X-Wing-CAPABLE: a non-empty recipient X-Wing public key was supplied (the
        // per-recipient capability gate — GATE condition 4b) AND (c) this wrapper has the X-Wing box. ANY of these
        // false ⇒ box suite #1 (the safe degrade — kill-switch off, or an intermittent-edge / all-phone /
        // desert-fleet recipient with no X-Wing key, or a minimal composition with no X-Wing box). We NEVER force
        // suite #3 to a non-capable recipient (that would strand it — read-before-write only prevents stranding
        // CAPABLE recipients). A supplied-but-wrong-length X-Wing key is a call-site programming error caught by
        // BoxXWing's own length guard (a loud ArgumentException) — it is sender-controlled, not an
        // attacker-controlled wrap, so a loud failure is correct.
        var recipientIsXWingCapable = !recipientXWingPublicKey.IsEmpty;
        var emitSuite3 = _writePolicy.HybridWritesEnabled && recipientIsXWingCapable && _xwingBox is not null;

        if (emitSuite3)
        {
            return WrapSuite3(tenantDek, recipientXWingPublicKey, context, pairingAdminPartyId, pairingAdminSigner);
        }

        // ── Suite #1 (the legacy / safe-degrade path — byte-for-byte unchanged) ───────────────────────────────
        if (recipientDmPublicKey.Length != _kem.PublicKeyLength)
        {
            throw new ArgumentException(
                $"Recipient DM public key must be {_kem.PublicKeyLength} bytes (was {recipientDmPublicKey.Length}).",
                nameof(recipientDmPublicKey));
        }

        // (1) Fresh EPHEMERAL sender keypair — per-wrap, so a sender-key compromise cannot retro-open prior wraps and
        // the wrap is unlinkable to the admin's long-term key. The private key is used once and zeroed below.
        var (ephemeralPublicKey, ephemeralPrivateKey) = _kem.GenerateKeyPair();
        try
        {
            // (2) 0046-A6 sealed box: ciphertext of the DEK under (recipientPub, ephemeralPriv). Confidentiality only
            // (anonymous-sender, no AAD) — the authenticity + context is the outer signature below (F-A).
            var (ciphertext, nonce) = _kem.Box(tenantDek, recipientDmPublicKey, ephemeralPrivateKey);

            // (3) Canonical signable over the CIPHERTEXT (not the plaintext DEK) + nonce + ephemeral pub + context.
            // Suite #1: the suite-#3 KEM-binding fields are null/omitted, so the suite-#1 signable is unchanged.
            var signable = BuildSignable(
                KemSuites.LegacyDefault, ciphertext, nonce, ephemeralPublicKey, context,
                kemCiphertext: null, recipientMlKemPublicKey: null);

            // (4) Outer Ed25519 signature by the pairing admin (the authority anchor). The admin's IssuerId is stamped
            // so the recipient verifies against — and pins to — the roster-bound admin key.
            var adminKey = pairingAdminSigner.IssuerId;
            var op = pairingAdminSigner
                .SignAsync(signable, FixedIssuedAt, DeriveSignableNonce(signable))
                .AsTask().GetAwaiter().GetResult();

            return new WrappedTenantDek(
                Context: context,
                Ciphertext: ciphertext,
                Nonce: nonce,
                SenderEphemeralPublicKey: ephemeralPublicKey,
                PairingAdminPartyId: pairingAdminPartyId,
                PairingAdminPublicKey: adminKey.ToBase64Url(),
                Signature: op.Signature.ToBase64Url());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeralPrivateKey);
        }
    }

    /// <summary>
    /// The 2c-iii-b suite-#3 (X-Wing) WRITER. Boxes the DEK to the recipient's X-Wing public key via
    /// <see cref="IXWingSealedBox.BoxXWing"/>, then signs the suite-#3 canonical signable — which BINDS the KEM
    /// ciphertext + the recipient X-Wing public key (the 2c-i envelope-signing fix; "the signature IS the
    /// transcript"). The result is exactly what the merged read path (<see cref="VerifyAndUnwrap"/>, 2c-iii-a)
    /// already opens for an X-Wing-capable recipient: a substituted KEM ciphertext or a re-pointed recipient key
    /// breaks the outer signature → fail-closed at the recipient; the box opens only for the recipient's own
    /// root-derived X-Wing seed → no leak on a key-substitution. Unlike suite #1 there is NO separate sender
    /// ephemeral X25519 key on the wire (X-Wing's <c>ct_X</c> rides inside the 1120-byte KEM ciphertext), so the
    /// signable's ephemeral-pubkey field is present-but-empty (signed, unused on the X-Wing open path).
    /// </summary>
    private WrappedTenantDek WrapSuite3(
        ReadOnlySpan<byte> tenantDek,
        ReadOnlySpan<byte> recipientXWingPublicKey,
        TenantDekPairingContext context,
        string pairingAdminPartyId,
        IOperationSigner pairingAdminSigner)
    {
        // _xwingBox is non-null (the Wrap gate required it before calling here).
        var recipientXWingPub = recipientXWingPublicKey.ToArray();
        var (ciphertext, nonce, kemCiphertext) = _xwingBox!.BoxXWing(tenantDek, recipientXWingPublicKey);

        // Suite #3 carries no separate sender-ephemeral X25519 key (ct_X is inside the KEM ciphertext); the
        // signable's ephemeral-pubkey field is present-but-empty (signed, unused on the X-Wing open path). The
        // signable BINDS the KEM ciphertext + recipient X-Wing key — the envelope-signing fix (GATE condition 2).
        var signable = BuildSignable(
            KemSuite.XWingX25519MlKem768_v1, ciphertext, nonce, ReadOnlySpan<byte>.Empty, context,
            kemCiphertext, recipientXWingPub);

        var adminKey = pairingAdminSigner.IssuerId;
        var op = pairingAdminSigner
            .SignAsync(signable, FixedIssuedAt, DeriveSignableNonce(signable))
            .AsTask().GetAwaiter().GetResult();

        return new WrappedTenantDek(
            Context: context,
            Ciphertext: ciphertext,
            Nonce: nonce,
            SenderEphemeralPublicKey: Array.Empty<byte>(),
            PairingAdminPartyId: pairingAdminPartyId,
            PairingAdminPublicKey: adminKey.ToBase64Url(),
            Signature: op.Signature.ToBase64Url())
        {
            Suite = KemSuite.XWingX25519MlKem768_v1,
            KemCiphertext = kemCiphertext,
            RecipientMlKemPublicKey = recipientXWingPub,
        };
    }

    /// <inheritdoc />
    public byte[]? VerifyAndUnwrap(
        WrappedTenantDek wrap,
        PrincipalId expectedAdminPublicKey,
        TenantDekPairingContext expectedContext,
        ReadOnlySpan<byte> recipientDmPrivateKey,
        IOperationVerifier verifier,
        ReadOnlySpan<byte> recipientXWingPrivateKeySeed = default)
    {
        ArgumentNullException.ThrowIfNull(wrap);
        ArgumentNullException.ThrowIfNull(expectedContext);
        ArgumentNullException.ThrowIfNull(verifier);

        // Per-recipient capability gate (ADR 0004 Amendment 2 GATE condition 4b): a recipient "is
        // X-Wing-capable" iff it holds an X-Wing private key AND this unwrapper was composed with the X-Wing
        // box. A recipient that supplies no X-Wing seed (intermittent-edge / all-phone / desert-fleet, or a
        // pre-2c-iii host) stays suite-#1-only and a suite-#3 wrap fails closed below — the safe degrade.
        var xwingCapable = _xwingBox is not null
            && recipientXWingPrivateKeySeed.Length == _xwingBox.PrivateKeySeedLength;

        // GATE 0 (KEM-suite policy boundary, ADR 0004 §1 / BL-01 2b/2c-iii) — the wrap's stamped KEM suite MUST be one
        // this build+recipient can open. A forward-version or garbage suite tag is DENIED here, BEFORE any
        // signature/context work, so an unknown suite can never be silently downgraded to the legacy X25519
        // construction. A legacy untagged wrap carries Suite == LegacyDefault (suite #1) and passes.
        //
        // READ-BEFORE-WRITE (ADR 0004 Amendment 2 GATE condition 4a): suite #3 (X-Wing) is ACCEPTED here iff the
        // recipient is X-Wing-capable (holds an X-Wing private-key seed + this unwrapper has the X-Wing box). This
        // is the additive read capability that ships BEFORE any writer emits suite #3, so a future writer flip can
        // never strand a recipient. Suite #2 is STILL denied (the suite-#2 TLS-concat box is a tested reference,
        // not the production read path — D1 picked X-Wing). An X-Wing-INCAPABLE recipient still fails closed on a
        // suite-#3 wrap (the per-recipient capability gate). The registry check remains the no-silent-downgrade
        // guarantee; production WRITE paths are unchanged (they still box suite #1).
        if (!KemSuites.IsRegistered(wrap.Suite))
        {
            return null;
        }
        var suite3Readable = wrap.Suite == KemSuite.XWingX25519MlKem768_v1 && xwingCapable;
        if (wrap.Suite != KemSuite.X25519SealedBox_v1 && !suite3Readable)
        {
            return null;
        }

        // Validate the recipient key matching the suite to open. Suite #1 needs a well-formed X25519 DM private key;
        // suite #3 needs a well-formed X-Wing seed (already length-checked into `xwingCapable`). A malformed key is a
        // call-site programming error, not an attacker-controlled wrap → fail-closed (null) rather than throw, so a
        // single bad call can never leak via an exception path.
        if (wrap.Suite == KemSuite.X25519SealedBox_v1 && recipientDmPrivateKey.Length != _kem.PrivateKeyLength)
        {
            return null;
        }

        // GATE 1 — the bound context MUST equal what the recipient expects (replay / cross-tenant / cross-recipient /
        // cross-epoch / wrong-purpose defence). Checked BEFORE any crypto so a context-mismatched wrap is rejected
        // cheaply and never reaches OpenBox.
        if (!ContextEquals(wrap.Context, expectedContext))
        {
            return null;
        }

        // GATE 2 — the stamped admin key MUST be the EXPECTED roster-bound admin (resolved by the caller from its own
        // verified roster). A wrap signed by a stranger — even with an internally-valid signature — is rejected here.
        PrincipalId stampedAdmin;
        Signature signature;
        try
        {
            stampedAdmin = PrincipalId.FromBase64Url(wrap.PairingAdminPublicKey);
            signature = Signature.FromBase64Url(wrap.Signature);
        }
        catch (FormatException)
        {
            return null; // malformed stamped key / signature → not authentic.
        }
        if (!stampedAdmin.AsSpan().SequenceEqual(expectedAdminPublicKey.AsSpan()))
        {
            return null;
        }

        // GATE 3 — the OUTER signature MUST verify for the stamped admin key over the reconstructed signable (the
        // ciphertext + nonce + ephemeral pub + context, AND for suite #3 the KEM ciphertext + recipient key). A
        // tampered ciphertext / nonce / context / KEM-ciphertext breaks this. The signable is suite-AWARE so the
        // reconstruction binds exactly the bytes the signer bound — for a suite-#3 wrap that includes the KEM
        // material (the envelope-signing fix), and a suite-#3 wrap that omits it fails closed via the throw below.
        // (As of increment 2c-iii an X-Wing-capable recipient DOES reach GATE 4 for a suite-#3 wrap; the
        // envelope-signing fix binding the KEM ciphertext + recipient key is verified HERE before the box opens —
        // "the signature IS the transcript". A re-pointed recipient key or substituted KEM ciphertext breaks this
        // signature, so the read path is non-malleable, not just AEAD-authenticated.)
        TenantDekWrapSignable signable;
        try
        {
            signable = BuildSignable(
                wrap.Suite, wrap.Ciphertext, wrap.Nonce, wrap.SenderEphemeralPublicKey, wrap.Context,
                wrap.KemCiphertext, wrap.RecipientMlKemPublicKey);
        }
        catch (ArgumentException)
        {
            return null; // malformed wire bytes / a suite-#3 wrap missing its KEM binding → not authentic.
        }
        var op = new SignedOperation<TenantDekWrapSignable>(
            Payload: signable,
            IssuerId: stampedAdmin,
            IssuedAt: FixedIssuedAt,
            Nonce: DeriveSignableNonce(signable),
            Signature: signature);
        if (!verifier.Verify(op))
        {
            return null;
        }

        // GATE 4 — only now OPEN the box with the recipient's node-secret private key. Dispatch on the (already
        // gate-0-validated) suite. Guard the wire-supplied lengths first so a malformed (but somehow
        // signature-valid) wrap fails closed via null rather than throwing out of the open primitive. Both open
        // primitives return null on any AEAD failure (incl. a wrap not actually sealed for this recipient's key).
        if (suite3Readable)
        {
            // Suite #3 (X-Wing) — read-before-write (ADR 0004 Amendment 2 GATE 4a). X-Wing carries no separate
            // sender-ephemeral X25519 public key on the wire (ct_X is INSIDE the KEM ciphertext), so the open
            // inputs are {ciphertext, nonce, kemCiphertext, recipient X-Wing seed}. `_xwingBox` is non-null here
            // (xwingCapable required it). Length-guard the wire fields → fail closed (null) on a malformed wrap
            // rather than throwing out of OpenXWing.
            if (_xwingBox is null
                || wrap.Nonce is not { } xwingNonce || xwingNonce.Length != _xwingBox.NonceLength
                || wrap.KemCiphertext is not { } kemCt || kemCt.Length != _xwingBox.KemCiphertextLength
                || wrap.Ciphertext is not { Length: > 0 })
            {
                return null;
            }
            return _xwingBox.OpenXWing(wrap.Ciphertext, wrap.Nonce, kemCt, recipientXWingPrivateKeySeed);
        }

        // Suite #1 (X25519) — the legacy/default read path, unchanged.
        if (wrap.SenderEphemeralPublicKey is not { } senderPub || senderPub.Length != _kem.PublicKeyLength
            || wrap.Nonce is not { } nonce || nonce.Length != _kem.NonceLength
            || wrap.Ciphertext is not { Length: > 0 })
        {
            return null;
        }
        return _kem.OpenBox(wrap.Ciphertext, wrap.Nonce, wrap.SenderEphemeralPublicKey, recipientDmPrivateKey);
    }

    // ── canonical signable construction (shared by wrap + unwrap so the bytes match exactly) ───────────────────

    /// <summary>
    /// Build the canonical signable for a wrap. Suite-AWARE (ADR 0004 Amendment 2 GATE condition 2 — the
    /// envelope-signing fix): for a suite-#3 (X-Wing) wrap the signable additionally binds the KEM
    /// ciphertext + the recipient X-Wing public key (which embeds the recipient ML-KEM public key), so a
    /// substituted/tampered KEM ciphertext or a re-pointed recipient key breaks the outer signature. For
    /// suites #1/#2 the two extra fields are <c>null</c> → OMITTED from the canonical JSON, so the suite-#1
    /// signable bytes are byte-for-byte unchanged.
    /// </summary>
    /// <remarks>
    /// <b>Fail-closed on a suite-#3 wrap that omits the KEM binding.</b> A suite-#3 wrap MUST carry both
    /// <see cref="WrappedTenantDek.KemCiphertext"/> and <see cref="WrappedTenantDek.RecipientMlKemPublicKey"/>;
    /// if either is absent this throws <see cref="ArgumentException"/>, which the unwrap path catches and
    /// turns into a fail-closed null (a suite-#3 wrap whose signable would not cover the KEM material is
    /// rejected, never honoured).
    /// </remarks>
    internal static TenantDekWrapSignable BuildSignable(
        KemSuite suite,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ephemeralPublicKey,
        TenantDekPairingContext context,
        byte[]? kemCiphertext,
        byte[]? recipientMlKemPublicKey)
    {
        string? kemCiphertextB64Url = null;
        string? recipientMlKemPublicKeyB64Url = null;
        if (suite == KemSuite.XWingX25519MlKem768_v1)
        {
            // The envelope-signing fix is MANDATORY for suite #3: refuse to build (hence sign/verify) a
            // suite-#3 signable that does not bind the KEM ciphertext + recipient key.
            if (kemCiphertext is not { Length: > 0 } || recipientMlKemPublicKey is not { Length: > 0 })
            {
                throw new ArgumentException(
                    "A suite-#3 (X-Wing) wrap MUST carry both KemCiphertext and RecipientMlKemPublicKey so the " +
                    "outer signature covers them (ADR 0004 Amendment 2 GATE condition 2).");
            }
            kemCiphertextB64Url = Base64Url.Encode(kemCiphertext);
            recipientMlKemPublicKeyB64Url = Base64Url.Encode(recipientMlKemPublicKey);
        }

        return new TenantDekWrapSignable(
            CiphertextB64Url: Base64Url.Encode(ciphertext),
            NonceB64Url: Base64Url.Encode(nonce),
            SenderEphemeralPublicKeyB64Url: Base64Url.Encode(ephemeralPublicKey),
            TenantId: context.TenantId,
            RecipientPartyId: context.RecipientPartyId,
            HomeEpoch: context.HomeEpoch,
            Purpose: context.Purpose,
            KemCiphertextB64Url: kemCiphertextB64Url,
            RecipientMlKemPublicKeyB64Url: recipientMlKemPublicKeyB64Url);
    }

    // A FIXED issued-at + a content-derived nonce keep the signed envelope deterministic (the recipient reconstructs
    // byte-identical signable bytes). Freshness/replay protection is the bound context + the fresh sealed-box nonce,
    // not a wall-clock instant — mirrors RosterSigning's fixed-epoch genesis discipline.
    internal static readonly DateTimeOffset FixedIssuedAt = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Derive the signable nonce deterministically from the signable content (so wrap + unwrap agree without carrying
    /// a separate nonce field). HKDF-SHA256 over the canonical fields → a 16-byte Guid.
    /// </summary>
    internal static Guid DeriveSignableNonce(TenantDekWrapSignable s)
    {
        // Suite #1/#2: the KEM-binding fields are null and contribute NOTHING, so the nonce material is
        // byte-for-byte unchanged from before this change. Suite #3 appends the KEM ciphertext + recipient
        // key so the deterministic nonce also depends on them (a substituted KEM ct yields a different nonce).
        var suffix = s.KemCiphertextB64Url is null && s.RecipientMlKemPublicKeyB64Url is null
            ? string.Empty
            : ":" + (s.KemCiphertextB64Url ?? string.Empty) + ":" + (s.RecipientMlKemPublicKeyB64Url ?? string.Empty);
        var material = System.Text.Encoding.UTF8.GetBytes(
            "sunfish-dek-pairing-nonce-v1:" + s.CiphertextB64Url + ":" + s.NonceB64Url + ":"
            + s.SenderEphemeralPublicKeyB64Url + ":" + s.TenantId + ":" + s.RecipientPartyId + ":"
            + s.HomeEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + s.Purpose
            + suffix);
        // Split into Extract-then-Expand rather than calling HKDF.DeriveKey. RFC 5869 defines
        // DeriveKey AS Expand(Extract(salt, ikm), info, L), so this derives byte-for-byte the same
        // nonce and no previously emitted record changes meaning. Verified identical on .NET 10 and
        // .NET 11 across ikm lengths 16..8192.
        //
        // It is written this way because .NET 11 routes DeriveKey through Windows CNG, whose HKDF
        // provider advertises dwMaxLength = 16384 bits (2048 bytes) and rejects a longer secret at
        // import with STATUS_INVALID_PARAMETER, surfacing as 0xc100000d (dotnet/runtime#132542).
        // Suite #3 appends the base64url KEM ciphertext and recipient key to this transcript, which
        // carries it past 2048 bytes, so DeriveKey failed for suite #3 while suite #1/#2 stayed
        // under the limit. Extract remains managed and accepts any length; the PRK it returns is
        // one hash block, far below the limit Expand then imports.
        Span<byte> prk = stackalloc byte[32];
        HKDF.Extract(HashAlgorithmName.SHA256, ikm: material, salt: ReadOnlySpan<byte>.Empty, prk: prk);
        Span<byte> nonceBytes = stackalloc byte[16];
        HKDF.Expand(HashAlgorithmName.SHA256, prk: prk, output: nonceBytes, info: "sunfish-dek-pairing-nonce-v1"u8);
        return new Guid(nonceBytes);
    }

    private static bool ContextEquals(TenantDekPairingContext a, TenantDekPairingContext b) =>
        string.Equals(a.TenantId, b.TenantId, StringComparison.Ordinal)
        && string.Equals(a.RecipientPartyId, b.RecipientPartyId, StringComparison.Ordinal)
        && a.HomeEpoch == b.HomeEpoch
        && string.Equals(a.Purpose, b.Purpose, StringComparison.Ordinal);
}

/// <summary>Minimal base64url codec (no padding) for canonical-JSON-stable wire fields.</summary>
internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        var b64 = Convert.ToBase64String(bytes);
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
