using System.Security.Cryptography;
using System.Text;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.TenantKey;

/// <summary>
/// W#20 Phase 0 stub implementation — derives keys deterministically via
/// HKDF-SHA256 over the UTF-8 bytes of <c>(tenantId.Value || purpose)</c>
/// with a fixed development salt. **NOT secure for production** — every
/// installation that runs this stub derives the same keys for the same
/// inputs. ADR 0046 Stage 06 replaces with real tenant-key-hierarchy
/// derivation (per-tenant DEK from KEK; KEK from operator master key).
/// </summary>
public sealed class InMemoryTenantKeyProvider : ITenantKeyProvider
{
    private static readonly byte[] DevelopmentSalt = Encoding.UTF8.GetBytes("sunfish-phase-1-stub-not-for-production");

    /// <inheritdoc />
    public Task<ReadOnlyMemory<byte>> DeriveKeyAsync(TenantId tenant, string purpose, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        // TenantId is a string-wrapper; build IKM as UTF-8(tenant.Value || ":" || purpose).
        var separator = (byte)':';
        var tenantBytes = Encoding.UTF8.GetByteCount(tenant.Value);
        var purposeBytes = Encoding.UTF8.GetByteCount(purpose);
        var ikm = new byte[tenantBytes + 1 + purposeBytes];
        Encoding.UTF8.GetBytes(tenant.Value, ikm.AsSpan(0, tenantBytes));
        ikm[tenantBytes] = separator;
        Encoding.UTF8.GetBytes(purpose, ikm.AsSpan(tenantBytes + 1));

        var key = new byte[32];
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm,
            key,
            DevelopmentSalt,
            ReadOnlySpan<byte>.Empty);

        return Task.FromResult<ReadOnlyMemory<byte>>(key);
    }

    /// <summary>HKDF <c>info</c> prefix for per-subject sub-keys (ADR 0118 D4 info-prefix domain separation).</summary>
    private const string SubjectInfoPrefix = "subject-dek-v1:";

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

        // Fail-closed crypto-shred: a shredded subject's sub-key is permanently
        // un-derivable. This is checked BEFORE any key material is produced, so a
        // shredded subject's ciphertext can never be decrypted again.
        if (await erasure.IsErasedAsync(tenant, subject, ct).ConfigureAwait(false))
        {
            throw new SubjectErasedException(tenant.Value, subject.Value);
        }

        // The per-tenant DEK for this purpose is the HKDF pseudo-random key (PRK)
        // for the subject sub-key. The subject sits BELOW the tenant DEK, derived
        // by HKDF-Expand with a per-subject info prefix — info-prefix domain
        // separation (ADR 0118 D4), never string-suffix manipulation.
        var tenantDek = await DeriveKeyAsync(tenant, purpose, ct).ConfigureAwait(false);

        var infoPrefixBytes = Encoding.UTF8.GetByteCount(SubjectInfoPrefix);
        var subjectBytes = Encoding.UTF8.GetByteCount(subject.Value);
        var info = new byte[infoPrefixBytes + subjectBytes];
        Encoding.UTF8.GetBytes(SubjectInfoPrefix, info.AsSpan(0, infoPrefixBytes));
        Encoding.UTF8.GetBytes(subject.Value, info.AsSpan(infoPrefixBytes));

        var subKey = new byte[32];
        HKDF.Expand(
            HashAlgorithmName.SHA256,
            tenantDek.Span,
            subKey,
            info);

        return subKey;
    }
}
