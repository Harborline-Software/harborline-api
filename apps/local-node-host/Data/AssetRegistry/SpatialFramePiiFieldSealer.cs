using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Harborline.Api.Foundation.Recovery;
using Harborline.Api.Foundation.Recovery.TenantKey;


namespace Harborline.Api.LocalNodeHost.Data.AssetRegistry;

/// <summary>
/// CP-4 storage-boundary sealer for the TWO governed spatial-descriptor PII fields —
/// <c>originDescription</c> and <c>georeference</c> — on BOTH the descriptor and quarantine rows
/// (ADR 0101 Rev 3.2 Wave 5 precondition 3; ADR 0168 D2-A8, OQ-1 ruling 2026-08-05). Seals each
/// field into the ADR 0046-A2 <see cref="EncryptedField"/> envelope (AES-256-GCM over an
/// HKDF-derived <b>tenant DEK</b>) and stores the envelope's canonical JSON form in the existing
/// TEXT column, so the column at rest holds ciphertext, never prose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Policy binding.</b> This implements the shipped <c>pii</c> policy binding
/// (<c>PredefinedPolicyBindings.PiiBinding</c>, <c>Encrypt@Store</c> under the tenant DEK,
/// <c>SubjectScoped: false</c> — ADR 0139 hybrid key model). It is applied HOST-SIDE by the durable
/// port: a package-side placement would pull foundation-governance / foundation-recovery into the
/// pure-domain registry package. The tenant DEK is now a random stored key wrapped by ticket 043's
/// at-rest hierarchy, so tenant-grain destruction is real. Subject-grain shredding becomes available
/// when the two fields bind to the subject-scoped <c>identifier</c> class (ADR 0139 D3
/// <c>subjectRef</c>) — the pinned expiry of the accepted-risk posture below.
/// </para>
/// <para>
/// <b>What is NEVER sealed (pinned fleet trap — never MAC or encrypt a signed digest).</b>
/// <c>ContentHash</c> is the Ed25519 mint signature's preimage, computed over the CLEARTEXT
/// originDescription/georeference; it persists cleartext in the exact form the signature covers.
/// Sealing (or MAC'ing) it would make every historical mint signature permanently unverifiable.
/// The identity triple (anchor, frameCode, frameEpoch) and <c>axisConvention</c> also stay
/// cleartext for keying/display (ADR 0168 D2-A8). Read-side re-verification decrypts the two
/// governed fields, recomputes the hash over the recovered cleartext, and compares.
/// </para>
/// <para>
/// <b>Defence-in-depth content prohibition (ADR 0168 D2-A8).</b> <c>originDescription</c> MUST NOT
/// carry occupant or party identity — it describes a physical datum ("aft perpendicular at
/// baseline"), never a person. The prohibition is a documented content contract on the field (see
/// <c>SpatialFrameDescriptor.OriginDescription</c>); it is not mechanically enforceable, which is
/// exactly why this sealing exists as the second layer.
/// </para>
/// <para>
/// <b>Accepted-risk posture, declared (card wording).</b> Single-subject residual is accepted
/// while the content prohibition holds; expiry pinned — bind the two fields to the subject-scoped
/// key class (<c>identifier</c>, <c>SubjectScoped: true</c>) when the ADR 0139 D3
/// <c>subjectRef</c> seam lands. <c>ISubjectErasurePropagator</c> is deliberately NOT registered:
/// the family has no derived cache to purge; it becomes mandatory at the first derived descriptor
/// projection (the ADR 0168 revisit trigger).
/// </para>
/// <para>
/// <b>Fail-closed.</b> There is no plaintext passthrough. If the tenant DEK cannot be derived, the
/// seal/unseal throws and the write/read fails — a mint never persists cleartext, and a read never
/// returns raw column bytes as if they were prose. A stored value that does not parse as an
/// <see cref="EncryptedField"/> envelope, an unsupported key version, or a failed GCM tag all
/// throw (never a silent mis-decrypt). The key version is folded into the HKDF purpose label
/// (the <c>EnvelopeBlobStore</c> PERS-2 F3 rotation seam), and the derived DEK is zeroized after
/// every AEAD operation (PERS-2 F5).
/// </para>
/// </remarks>
public sealed class SpatialFramePiiFieldSealer
{
    /// <summary>Base HKDF purpose label — domain-separated from every other tenant-DEK purpose.</summary>
    internal const string PurposeBase = "spatial-frame-pii-aes";

    internal const int CurrentKeyVersion = 2;
    internal const int KeyLength = 32;   // AES-256
    internal const int NonceLength = 12; // GCM standard nonce
    internal const int TagLength = 16;   // GCM standard tag

    internal const CryptoSuite CurrentSuite = CryptoSuite.AesGcm256Hkdf_v1;

    /// <summary>The key versions this build can derive and read (the rotation seam).</summary>
    internal static readonly HashSet<int> SupportedKeyVersions = new() { 1, CurrentKeyVersion };

    private readonly ITenantKeyProvider _tenantKeys;

    /// <summary>
    /// The ONLY constructor — the key provider is required. A key-less sealer would be a plaintext
    /// store wearing the wrong name (the <c>EnvelopeBlobStore</c> posture).
    /// </summary>
    public SpatialFramePiiFieldSealer(ITenantKeyProvider tenantKeys)
    {
        ArgumentNullException.ThrowIfNull(tenantKeys);
        _tenantKeys = tenantKeys;
    }

    /// <summary>PERS-2 F3 — the derivation purpose for a given key version (version-folded).</summary>
    internal static string PurposeForKeyVersion(int keyVersion) => $"{PurposeBase}:v{keyVersion}";

    /// <summary>
    /// Seal a governed field's cleartext into the <see cref="EncryptedField"/> envelope's canonical
    /// JSON form under the tenant DEK. Returns <see langword="null"/> for <see langword="null"/>
    /// input (an absent optional georeference stays absent — null is not PII).
    /// </summary>
    public async Task<string?> SealAsync(string? cleartext, TenantId tenant, CancellationToken ct)
    {
        if (cleartext is null)
        {
            return null;
        }

        var dek = await _tenantKeys
            .DeriveKeyAsync(tenant, PurposeForKeyVersion(CurrentKeyVersion), ct)
            .ConfigureAwait(false);

        var plaintext = Encoding.UTF8.GetBytes(cleartext);
        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];
        try
        {
            if (dek.Length != KeyLength)
            {
                throw new InvalidOperationException(
                    $"Spatial-frame PII DEK must be {KeyLength} bytes; the tenant-key provider returned {dek.Length}.");
            }

            using var aes = new AesGcm(dek.Span, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }
        finally
        {
            // PERS-2 F5 — zeroize the derived DEK as soon as the AEAD operation completes.
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsMemory(dek).Span);
            CryptographicOperations.ZeroMemory(plaintext);
        }

        // ct || tag packed — the TenantKeyProviderFieldEncryptor envelope layout, so the stored
        // JSON is a standard EncryptedField ({"ct","nonce","kv","suite"} via its converter).
        var packed = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, packed, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, packed, ciphertext.Length, tag.Length);
        var envelope = new EncryptedField(packed, nonce, CurrentKeyVersion, CurrentSuite);
        return JsonSerializer.Serialize(envelope);
    }

    /// <summary>
    /// Unseal a stored envelope-JSON column value back to the governed field's cleartext.
    /// Fail-closed: a value that is not a well-formed envelope, an unsupported key version /
    /// suite, or a failed GCM authentication all throw — raw column bytes are never returned
    /// as if they were cleartext.
    /// </summary>
    public async Task<string?> UnsealAsync(string? stored, TenantId tenant, CancellationToken ct)
    {
        if (stored is null)
        {
            return null;
        }

        EncryptedField envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<EncryptedField>(stored);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Spatial-frame governed column does not hold a valid EncryptedField envelope — "
                + "refusing to treat raw column bytes as cleartext (fail-closed).", ex);
        }

        if (!SupportedKeyVersions.Contains(envelope.KeyVersion))
        {
            throw new InvalidOperationException(
                $"Spatial-frame PII envelope key version {envelope.KeyVersion} is not supported "
                + $"(this build supports [{string.Join(",", SupportedKeyVersions)}]).");
        }
        if (envelope.Suite != CurrentSuite)
        {
            throw new InvalidOperationException(
                $"Spatial-frame PII envelope crypto suite {envelope.Suite} is not supported by this build.");
        }
        if (envelope.Ciphertext.Length < TagLength || envelope.Nonce.Length != NonceLength)
        {
            throw new InvalidOperationException(
                "Spatial-frame PII envelope is truncated (ciphertext shorter than the GCM tag, or bad nonce).");
        }

        // PERS-2 F3 — derive with the key version the envelope declares (validated above).
        var purpose = PurposeForKeyVersion(envelope.KeyVersion);
        var dek = _tenantKeys is IVersionedTenantKeyProvider versioned
            ? await versioned.ResolveKeyAsync(tenant, purpose, envelope.KeyVersion, ct).ConfigureAwait(false)
            : await _tenantKeys.DeriveKeyAsync(tenant, purpose, ct).ConfigureAwait(false);

        var packed = envelope.Ciphertext.Span;
        var ciphertext = packed[..^TagLength];
        var tag = packed[^TagLength..];
        var plaintext = new byte[ciphertext.Length];
        try
        {
            if (dek.Length != KeyLength)
            {
                throw new InvalidOperationException(
                    $"Spatial-frame PII DEK must be {KeyLength} bytes; the tenant-key provider returned {dek.Length}.");
            }

            using var aes = new AesGcm(dek.Span, TagLength);
            // Throws AuthenticationTagMismatchException on a tampered envelope / wrong key.
            aes.Decrypt(envelope.Nonce.Span, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsMemory(dek).Span);   // PERS-2 F5
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
