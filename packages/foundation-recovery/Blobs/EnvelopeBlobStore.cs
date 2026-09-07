using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Recovery.TenantKey;

namespace Harborline.Api.Foundation.Recovery.Blobs;

/// <summary>
/// PERS-1 (ADR 0137 D4 mandatory-envelope-at-rest / C-3 no-side-door; ADR 0127's named-but-undesigned
/// <c>EnvelopeBlobStore : IBlobStore</c> seam). A decorating <see cref="IBlobStore"/> that AES-256-GCM
/// envelope-encrypts every payload BEFORE it reaches the raw backend, so the inner store only ever holds
/// ciphertext — the storage-role node never persists plaintext at rest.
/// </summary>
/// <remarks>
/// <para>
/// <b>Placement.</b> The ADR-0127 seam names this <c>Harborline.Api.Foundation.Blobs.EnvelopeBlobStore</c>, but it
/// depends on <see cref="ITenantKeyProvider"/>, which lives in <c>Harborline.Api.Foundation.Recovery</c> — and recovery
/// already references <c>Harborline.Api.Foundation</c>. Putting it in the foundation project would create a circular
/// project reference (foundation → foundation-recovery → foundation). Its cycle-free home is here, beside
/// <see cref="Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor"/> — the exact
/// AES-256-GCM-over-an-HKDF-tenant-DEK construction this blob store mirrors at the blob layer.
/// </para>
/// <para>
/// <b>C-3 hash-of-ciphertext, by construction.</b> Because the payload is sealed FIRST and then handed to
/// <c>inner.PutAsync(ciphertext)</c>, the inner store hashes the CIPHERTEXT — the returned <see cref="Cid"/> is a
/// ciphertext-CID. There is no plaintext addressing path; a plaintext CID is never written or queried.
/// </para>
/// <para>
/// <b>Random nonce, NOT idempotent at the blob layer (LOCKED, ADR 0137 D4 C-3 / ADR 0127).</b> Each
/// <see cref="PutAsync"/> uses a FRESH random 12-byte nonce, so two puts of identical plaintext produce different
/// ciphertext and therefore DIFFERENT CIDs. This is deliberate: a deterministic ciphertext-CID would reintroduce
/// the known-plaintext equality oracle C-3 exists to prevent, and blob-level dedup is explicitly parked by ADR
/// 0127. The caller addresses a blob by the <see cref="Cid"/> this method returns.
/// </para>
/// <para>
/// <b>Fail-closed.</b> There is no plaintext-passthrough path. If a key cannot be derived (no/derivable-stand-in
/// key provider), key derivation throws and the put/get fails closed — it never falls back to storing or returning
/// plaintext. A tampered envelope fails GCM authentication and throws (never returns garbage). The composition-root
/// posture gate (the storage-role wiring) additionally refuses a dev/derivable <see cref="ITenantKeyProvider"/>.
/// </para>
/// </remarks>
public sealed class EnvelopeBlobStore : IBlobStore
{
    /// <summary>The tenant-key derivation purpose label for the blob envelope DEK (domain-separated from field/HMAC keys).</summary>
    internal const string BlobEnvelopePurpose = "blob-envelope-aes";

    internal const int KeyLength = 32;   // AES-256
    internal const int NonceLength = 12; // GCM standard nonce
    internal const int TagLength = 16;   // GCM standard tag

    private readonly IBlobStore _inner;
    private readonly ITenantKeyProvider _tenantKeys;
    private readonly TenantId _tenant;

    /// <summary>
    /// The ONLY constructor — every dependency is required. There is intentionally no overload that omits the
    /// <paramref name="tenantKeys"/> provider (the arch-fence asserts this): a key-less envelope store would be a
    /// plaintext store wearing the wrong name.
    /// </summary>
    /// <param name="inner">The raw backend the ciphertext envelopes are persisted into (e.g. a <c>FileSystemBlobStore</c>).</param>
    /// <param name="tenantKeys">Derives the install-secret per-tenant blob DEK. MUST be the real provider, never a derivable dev stub.</param>
    /// <param name="tenant">The ONE tenant the blob DEK is derived under. Every blob this store seals shares that
    /// single tenant's DEK — on a multi-team node (e.g. the edge/app composition, which passes the genesis
    /// team's projected tenant) this is ciphertext-at-rest only, NOT per-team key separation, and cannot support a
    /// per-team blob crypto-shred.</param>
    public EnvelopeBlobStore(IBlobStore inner, ITenantKeyProvider tenantKeys, TenantId tenant)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(tenantKeys);
        _inner = inner;
        _tenantKeys = tenantKeys;
        _tenant = tenant;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Seals <paramref name="content"/> into an AES-256-GCM envelope, then stores the ENVELOPE bytes in the inner
    /// backend and returns the ciphertext-CID. NOT idempotent at the blob layer (fresh nonce per call).
    /// </remarks>
    public async ValueTask<Cid> PutAsync(ReadOnlyMemory<byte> content, CancellationToken ct = default)
    {
        var envelope = await SealAsync(content, ct).ConfigureAwait(false);
        return await _inner.PutAsync(envelope, ct).ConfigureAwait(false);
    }

    // PutStreamingAsync is intentionally NOT overridden: the IBlobStore default buffers the stream and calls this
    // type's PutAsync (virtual dispatch), so streamed payloads are sealed identically. A chunked streaming cipher is
    // out of Phase-0 scope (ADR 0127 parks it).

    /// <inheritdoc />
    /// <remarks>Fetches the envelope from the inner backend and decrypts it; a failed GCM auth throws (never garbage).</remarks>
    public async ValueTask<ReadOnlyMemory<byte>?> GetAsync(Cid cid, CancellationToken ct = default)
    {
        var stored = await _inner.GetAsync(cid, ct).ConfigureAwait(false);
        if (stored is null)
        {
            return null;
        }

        return await OpenAsync(stored.Value, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<bool> ExistsLocallyAsync(Cid cid, CancellationToken ct = default)
        => _inner.ExistsLocallyAsync(cid, ct);

    /// <inheritdoc />
    public ValueTask PinAsync(Cid cid, CancellationToken ct = default) => _inner.PinAsync(cid, ct);

    /// <inheritdoc />
    public ValueTask UnpinAsync(Cid cid, CancellationToken ct = default) => _inner.UnpinAsync(cid, ct);

    private async ValueTask<byte[]> SealAsync(ReadOnlyMemory<byte> plaintext, CancellationToken ct)
    {
        var suite = BlobEnvelopeFormat.CurrentSuite;
        var keyVersion = BlobEnvelopeFormat.CurrentKeyVersion;

        // PERS-2 F3: the keyVersion is folded INTO the derivation purpose, so keyVersion K derives a DISTINCT key.
        // A future rotation bumps CurrentKeyVersion (and adds to SupportedKeyVersions); the reader derives with the
        // version stamped in each envelope, so old-version blobs stay readable — the field is load-bearing, not a
        // decorative stamp.
        var dek = await _tenantKeys.DeriveKeyAsync(_tenant, BlobEnvelopeFormat.PurposeForKeyVersion(keyVersion), ct)
            .ConfigureAwait(false);

        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        try
        {
            // PERS-2 F-Min-1: the DEK length check is INSIDE the try so the finally still zeroizes the (sensitive)
            // key material on that failure path.
            if (dek.Length != KeyLength)
            {
                throw new InvalidOperationException(
                    $"Blob envelope DEK must be {KeyLength} bytes; the tenant-key provider returned {dek.Length}.");
            }

            // PERS-2 F2: bind the self-describing header descriptor (magic, format version, suite, keyVersion) as
            // GCM ADDITIONAL AUTHENTICATED DATA. Tampering with any of those bytes (e.g. flipping the suite or the
            // keyVersion to coerce a different construction / derivation) now fails GCM authentication on read.
            var aad = BlobEnvelopeFormat.DescriptorAad(suite, keyVersion);
            using var aes = new AesGcm(dek.Span, TagLength);
            aes.Encrypt(nonce, plaintext.Span, ciphertext, tag, aad);
        }
        finally
        {
            // PERS-2 F5: zeroize the derived DEK as soon as the AEAD operation completes (even on exception). The
            // provider hands us a freshly-allocated key we own exclusively; leaving it in a GC-reachable buffer
            // widens the plaintext-recovery window.
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsMemory(dek).Span);
        }

        return BlobEnvelopeFormat.Serialize(suite, keyVersion, nonce, tag, ciphertext);
    }

    private async ValueTask<ReadOnlyMemory<byte>> OpenAsync(ReadOnlyMemory<byte> envelope, CancellationToken ct)
    {
        // Parse + validate the envelope shape BEFORE deriving a key — a malformed/unknown-version/garbage envelope
        // fails closed here (never a silent mis-decrypt under the wrong construction).
        var parsed = BlobEnvelopeFormat.Parse(envelope.Span);

        // PERS-2 F3: derive with the keyVersion the envelope declares (validated by Parse to be a supported
        // version) — this is what lets a rotated deployment read older-version blobs.
        var purpose = BlobEnvelopeFormat.PurposeForKeyVersion(parsed.KeyVersion);
        var dek = _tenantKeys is IVersionedTenantKeyProvider versioned
            ? await versioned.ResolveKeyAsync(_tenant, purpose, parsed.KeyVersion, ct).ConfigureAwait(false)
            : await _tenantKeys.DeriveKeyAsync(_tenant, purpose, ct).ConfigureAwait(false);

        var plaintext = new byte[parsed.Ciphertext.Length];
        try
        {
            // PERS-2 F-Min-1: the DEK length check is INSIDE the try so the finally still zeroizes the key material.
            if (dek.Length != KeyLength)
            {
                throw new InvalidOperationException(
                    $"Blob envelope DEK must be {KeyLength} bytes; the tenant-key provider returned {dek.Length}.");
            }

            // PERS-2 F2: reconstruct the header descriptor AAD from the parsed (untrusted) suite + keyVersion. If an
            // attacker altered either, the reconstructed AAD differs from what was sealed and GCM auth fails.
            var aad = BlobEnvelopeFormat.DescriptorAad(parsed.Suite, parsed.KeyVersion);
            using var aes = new AesGcm(dek.Span, TagLength);
            // Throws AuthenticationTagMismatchException (a CryptographicException) on a tampered envelope / wrong key.
            aes.Decrypt(parsed.Nonce, parsed.Ciphertext, parsed.Tag, plaintext, aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsMemory(dek).Span);   // PERS-2 F5
        }

        return plaintext;
    }
}

/// <summary>
/// The stable, versioned, self-describing on-disk blob-envelope binary format. A reader fails closed on an
/// unknown magic / format version / unregistered crypto suite / unknown key version rather than mis-decrypting.
/// </summary>
/// <remarks>
/// Layout (all multi-byte integers little-endian):
/// <code>
/// offset 0  : magic          (1 byte)  = 0xE5
/// offset 1  : formatVersion  (1 byte)  = 0x01
/// offset 2  : suite          (1 byte)  = (byte)CryptoSuite (the foundation-recovery suite vocabulary)
/// offset 3  : keyVersion     (4 bytes) Int32-LE
/// offset 7  : nonce          (12 bytes)
/// offset 19 : tag            (16 bytes)
/// offset 35 : ciphertext     (remaining bytes)
/// </code>
/// </remarks>
internal static class BlobEnvelopeFormat
{
    internal const byte Magic = 0xE5;
    internal const byte FormatVersion = 0x01;

    internal const CryptoSuite CurrentSuite = CryptoSuite.AesGcm256Hkdf_v1;
    internal const int CurrentKeyVersion = 2;

    /// <summary>
    /// PERS-2 F3 — the key versions this build can DERIVE + READ. The writer always seals under
    /// <see cref="CurrentKeyVersion"/>; the reader accepts any version in this set (so a rotation that bumps
    /// <see cref="CurrentKeyVersion"/> and adds the prior version here keeps older-version blobs readable). Ships
    /// with a single version until a rotation lands — the SEAM for rotation, without the rotation ceremony.
    /// </summary>
    internal static readonly System.Collections.Generic.HashSet<int> SupportedKeyVersions = new() { 1, CurrentKeyVersion };

    private const int MagicOffset = 0;
    private const int VersionOffset = 1;
    private const int SuiteOffset = 2;
    private const int KeyVersionOffset = 3;
    private const int NonceOffset = 7;
    private const int TagOffset = NonceOffset + EnvelopeBlobStore.NonceLength;     // 19
    internal const int HeaderLength = TagOffset + EnvelopeBlobStore.TagLength;     // 35

    /// <summary>
    /// PERS-2 F3 — the tenant-key derivation purpose for a given key version. The version is folded INTO the
    /// derivation (via the domain-separated purpose label), so keyVersion K yields a distinct DEK. This is what
    /// makes the on-disk keyVersion load-bearing rather than a decorative stamp.
    /// </summary>
    internal static string PurposeForKeyVersion(int keyVersion) => $"{EnvelopeBlobStore.BlobEnvelopePurpose}:v{keyVersion}";

    /// <summary>
    /// PERS-2 F2 — the self-describing header DESCRIPTOR bound as GCM additional-authenticated-data: the bytes that
    /// declare HOW to decrypt (magic, format version, suite, keyVersion). Binding these prevents an attacker from
    /// flipping the suite or keyVersion to coerce a different construction/derivation without failing GCM auth. The
    /// nonce is already authenticated as the GCM IV, and the tag is the GCM output, so neither is part of the AAD.
    /// </summary>
    internal static byte[] DescriptorAad(CryptoSuite suite, int keyVersion)
    {
        var aad = new byte[KeyVersionOffset + sizeof(int)];   // magic + version + suite + keyVersion = 7 bytes
        aad[MagicOffset] = Magic;
        aad[VersionOffset] = FormatVersion;
        aad[SuiteOffset] = checked((byte)suite);
        BinaryPrimitives.WriteInt32LittleEndian(aad.AsSpan(KeyVersionOffset, sizeof(int)), keyVersion);
        return aad;
    }

    internal static byte[] Serialize(
        CryptoSuite suite, int keyVersion, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> ciphertext)
    {
        if (nonce.Length != EnvelopeBlobStore.NonceLength)
        {
            throw new ArgumentException($"nonce must be {EnvelopeBlobStore.NonceLength} bytes.", nameof(nonce));
        }
        if (tag.Length != EnvelopeBlobStore.TagLength)
        {
            throw new ArgumentException($"tag must be {EnvelopeBlobStore.TagLength} bytes.", nameof(tag));
        }

        var buffer = new byte[HeaderLength + ciphertext.Length];
        var span = buffer.AsSpan();
        span[MagicOffset] = Magic;
        span[VersionOffset] = FormatVersion;
        span[SuiteOffset] = checked((byte)suite);
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(KeyVersionOffset, sizeof(int)), keyVersion);
        nonce.CopyTo(span.Slice(NonceOffset, EnvelopeBlobStore.NonceLength));
        tag.CopyTo(span.Slice(TagOffset, EnvelopeBlobStore.TagLength));
        ciphertext.CopyTo(span[HeaderLength..]);
        return buffer;
    }

    internal static ParsedEnvelope Parse(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length < HeaderLength)
        {
            throw new FormatException(
                $"Blob envelope is {envelope.Length} bytes; the header alone requires {HeaderLength}.");
        }
        if (envelope[MagicOffset] != Magic)
        {
            throw new FormatException(
                $"Blob envelope magic 0x{envelope[MagicOffset]:X2} != expected 0x{Magic:X2}.");
        }
        if (envelope[VersionOffset] != FormatVersion)
        {
            throw new FormatException(
                $"Blob envelope format version {envelope[VersionOffset]} is not supported (expected {FormatVersion}).");
        }

        var suite = (CryptoSuite)envelope[SuiteOffset];
        if (!CryptoSuites.IsRegistered(suite))
        {
            // Fail closed on a forward-version / garbage suite rather than mis-decrypt under the wrong construction.
            throw new FormatException($"Blob envelope crypto suite {(int)suite} is not registered in this build.");
        }

        var keyVersion = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(KeyVersionOffset, sizeof(int)));
        // PERS-2 F3: accept any SUPPORTED version (not only the current one), so a rotated deployment can still read
        // older-version blobs; an unknown version still fails closed here.
        if (!SupportedKeyVersions.Contains(keyVersion))
        {
            throw new FormatException(
                $"Blob envelope key version {keyVersion} is not supported (this build supports "
                + $"[{string.Join(",", SupportedKeyVersions)}]).");
        }

        var nonce = envelope.Slice(NonceOffset, EnvelopeBlobStore.NonceLength).ToArray();
        var tag = envelope.Slice(TagOffset, EnvelopeBlobStore.TagLength).ToArray();
        var ciphertext = envelope[HeaderLength..].ToArray();
        return new ParsedEnvelope(suite, keyVersion, nonce, tag, ciphertext);
    }

    /// <summary>A parsed envelope's heap-allocated parts (arrays, so they survive the async key-derivation await).</summary>
    internal readonly struct ParsedEnvelope
    {
        internal ParsedEnvelope(CryptoSuite suite, int keyVersion, byte[] nonce, byte[] tag, byte[] ciphertext)
        {
            Suite = suite;
            KeyVersion = keyVersion;
            Nonce = nonce;
            Tag = tag;
            Ciphertext = ciphertext;
        }

        internal CryptoSuite Suite { get; }
        internal int KeyVersion { get; }
        internal byte[] Nonce { get; }
        internal byte[] Tag { get; }
        internal byte[] Ciphertext { get; }
    }
}
