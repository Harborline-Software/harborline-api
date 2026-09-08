using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.TenantKey;

/// <summary>Destroys stored keys at tenant or subject grain.</summary>
public interface ITenantKeyDestroyer
{
    /// <summary>Destroy every stored key belonging to a subject.</summary>
    Task DestroySubjectKeysAsync(TenantId tenant, SubjectId subject, CancellationToken ct);

    /// <summary>Destroy the tenant key hierarchy, making all of its subject keys unreadable.</summary>
    Task DestroyTenantKeysAsync(TenantId tenant, CancellationToken ct);
}
