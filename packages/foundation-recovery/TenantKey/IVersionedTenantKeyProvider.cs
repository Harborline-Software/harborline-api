using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.TenantKey;

/// <summary>Resolves key material for versioned ciphertext during key-hierarchy migration.</summary>
public interface IVersionedTenantKeyProvider
{
    /// <summary>Resolve a tenant key for the ciphertext key version.</summary>
    Task<ReadOnlyMemory<byte>> ResolveKeyAsync(
        TenantId tenant,
        string purpose,
        int keyVersion,
        CancellationToken ct);

    /// <summary>Resolve a subject key for the ciphertext key version.</summary>
    Task<ReadOnlyMemory<byte>> ResolveSubjectKeyAsync(
        TenantId tenant,
        SubjectId subject,
        string purpose,
        int keyVersion,
        ISubjectErasureRegistry erasure,
        CancellationToken ct);
}
