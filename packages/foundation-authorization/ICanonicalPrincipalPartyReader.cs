using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>Reads the People pillar's canonical principal-to-Party binding by explicit tenant.</summary>
/// <remarks>
/// This contract deliberately has no dependency on the People package. A null result refuses
/// missing, ambiguous, tombstoned, detached, or wrong-tenant mappings.
/// </remarks>
public interface ICanonicalPrincipalPartyReader
{
    /// <summary>Resolves one verified live Party binding, or null when admission must refuse.</summary>
    ValueTask<CanonicalPartyBinding?> ResolveAsync(
        TenantId tenant,
        PrincipalUserId user,
        CancellationToken cancellationToken = default);
}
