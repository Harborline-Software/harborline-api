using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.TenantKey;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// Legacy version-1 tenant-key derivation retained only to migrate existing ciphertext into the stored hierarchy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is the production wire, not the in-memory stub.</b> The recovery substrate ships only
/// <c>InMemoryTenantKeyProvider</c>, which derives keys from a FIXED development salt — every install produces
/// the SAME key material for the same inputs (it documents itself "NOT secure for production"). Sealing the KG
/// per-subject embeddings under that would make the cleartext recoverable by any binary holder (the no-mock-
/// crypto family — a stand-in key provider behind no real boundary). This provider binds the derivation to the
/// node's 32-byte root seed (the SAME seed the SQLCipher store DEK + the Ed25519 node identity derive from), so
/// the per-subject sealing uses install-secret key material. The seed never leaves this provider.
/// </para>
/// <para>
/// <b>Legacy shape.</b> <c>tenant DEK = HKDF(rootSeed, salt = "kg-tenant-dek-v1", info = tenant||purpose)</c>;
/// <c>subject sub-key = HKDF-Expand(prk = tenant DEK, info = "subject-dek-v1:" || subject)</c> (ADR 0118 D4
/// info-prefix domain separation). These keys are derivable and therefore are not a crypto-shred mechanism.
/// <see cref="StoredTenantKeyProvider"/> imports them only when reading version-1 ciphertext.
/// </para>
/// <para>
/// <b>Not production write custody.</b> The host does not register this type as its effective
/// <see cref="ITenantKeyProvider"/>. New writes use random stored version-2 keys.
/// </para>
/// </remarks>
public sealed class RootSeedTenantKeyProvider : ITenantKeyProvider
{
    private static readonly byte[] TenantDekSalt = Encoding.UTF8.GetBytes("kg-tenant-dek-v1");
    private const string SubjectInfoPrefix = "subject-dek-v1:";

    private readonly byte[] _rootSeed;

    /// <summary>Construct from the node's 32-byte root seed (a defensive copy is held; the input is not retained).</summary>
    public RootSeedTenantKeyProvider(ReadOnlySpan<byte> rootSeed)
    {
        if (rootSeed.Length != 32)
        {
            throw new ArgumentException("Root seed must be exactly 32 bytes.", nameof(rootSeed));
        }
        _rootSeed = rootSeed.ToArray();
    }

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>> DeriveKeyAsync(TenantId tenant, string purpose, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        // info = tenant.Value || ":" || purpose — domain-separates each (tenant, purpose) DEK.
        var tenantBytes = Encoding.UTF8.GetByteCount(tenant.Value);
        var purposeBytes = Encoding.UTF8.GetByteCount(purpose);
        var info = new byte[tenantBytes + 1 + purposeBytes];
        Encoding.UTF8.GetBytes(tenant.Value, info.AsSpan(0, tenantBytes));
        info[tenantBytes] = (byte)':';
        Encoding.UTF8.GetBytes(purpose, info.AsSpan(tenantBytes + 1));

        var key = new byte[32];
        // HKDF(ikm = rootSeed, salt = "kg-tenant-dek-v1", info = tenant||purpose) — the root seed is the IKM,
        // so the DEK is install-secret (NOT derivable from public inputs + a shared constant).
        HKDF.DeriveKey(HashAlgorithmName.SHA256, _rootSeed, key, TenantDekSalt, info);
        return Task.FromResult<ReadOnlyMemory<byte>>(key);
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> DeriveSubjectKeyAsync(
        TenantId tenant,
        SubjectId subject,
        string purpose,
        ISubjectErasureRegistry erasure,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(erasure);

        // Compatibility safety: application reads still refuse an erased subject. This check is not the
        // crypto-shred mechanism; the stored provider's physical key deletion is.
        if (await erasure.IsErasedAsync(tenant, subject, ct).ConfigureAwait(false))
        {
            throw new SubjectErasedException(tenant.Value, subject.Value);
        }

        var tenantDek = await DeriveKeyAsync(tenant, purpose, ct).ConfigureAwait(false);

        var infoPrefixBytes = Encoding.UTF8.GetByteCount(SubjectInfoPrefix);
        var subjectBytes = Encoding.UTF8.GetByteCount(subject.Value);
        var info = new byte[infoPrefixBytes + subjectBytes];
        Encoding.UTF8.GetBytes(SubjectInfoPrefix, info.AsSpan(0, infoPrefixBytes));
        Encoding.UTF8.GetBytes(subject.Value, info.AsSpan(infoPrefixBytes));

        var subKey = new byte[32];
        // ADR 0118 D4 — info-prefix domain separation under the tenant DEK (NOT string-suffix manipulation).
        HKDF.Expand(HashAlgorithmName.SHA256, tenantDek.Span, subKey, info);
        return subKey;
    }
}
