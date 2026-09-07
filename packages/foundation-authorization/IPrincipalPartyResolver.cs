using System;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// Lower-level mapping primitive that resolves an authenticated principal's stable
/// user identifier to its <see cref="Guid"/> PartyId, scoped to a tenant. This is the
/// swappable half of the principal→party seam: the v1 in-memory implementation
/// (<see cref="InMemoryPrincipalPartyResolver"/>) is replaced by a
/// people-foundation-backed implementation behind this same interface without touching
/// the ambient <see cref="IPartyContext"/> facade that consumers inject.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant-scoped by signature.</b> <c>tenantId</c> is a required input,
/// not derived inside the implementation — a UserId resolves to a PartyId only within
/// its own tenant. The facade supplies the tenant from the same validated principal that
/// carries the UserId (see <see cref="IPartyContext"/>), so a principal can never resolve
/// a party in another tenant.
/// </para>
/// <para>
/// Consumers that need the current principal's party should inject <see cref="IPartyContext"/>,
/// NOT this primitive — the facade enforces same-token derivation by construction. This
/// interface exists so the data lookup can be swapped independently of that security seam.
/// </para>
/// </remarks>
public interface IPrincipalPartyResolver
{
    /// <summary>
    /// Resolves <paramref name="userId"/> to its PartyId within <paramref name="tenantId"/>.
    /// Returns <see langword="null"/> when no party maps to the (tenant, user) pair.
    /// </summary>
    ValueTask<Guid?> ResolveAsync(string userId, TenantId tenantId, CancellationToken ct = default);
}
