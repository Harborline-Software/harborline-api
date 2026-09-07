using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Authorization;

/// <summary>
/// A single (tenant, user) → party mapping used to seed
/// <see cref="InMemoryPrincipalPartyResolver"/>.
/// </summary>
public sealed record PrincipalPartyMapping(TenantId TenantId, string UserId, Guid PartyId);

/// <summary>
/// v1 in-memory implementation of <see cref="IPrincipalPartyResolver"/>. Holds a fixed set of
/// <see cref="PrincipalPartyMapping"/> seeds keyed by (tenant, user). This is the Tier-1
/// domain-block default — concrete DI, never runtime-swapped — to be replaced by a
/// people-foundation-backed implementation behind the same interface once that store lands.
/// </summary>
/// <remarks>
/// Lookups are tenant-scoped: the (tenant, user) composite key means a user in one tenant
/// can never match a mapping seeded for another tenant. <see cref="TenantId"/> has value
/// equality (readonly record struct), so it composes directly into the dictionary key with
/// no string coercion.
/// </remarks>
public sealed class InMemoryPrincipalPartyResolver : IPrincipalPartyResolver
{
    private readonly IReadOnlyDictionary<(TenantId Tenant, string UserId), Guid> _map;

    public InMemoryPrincipalPartyResolver(IEnumerable<PrincipalPartyMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        _map = mappings.ToDictionary(
            m => (m.TenantId, m.UserId),
            m => m.PartyId);
    }

    /// <inheritdoc />
    public ValueTask<Guid?> ResolveAsync(string userId, TenantId tenantId, CancellationToken ct = default)
    {
        var partyId = _map.TryGetValue((tenantId, userId), out var pid) ? pid : (Guid?)null;
        return ValueTask.FromResult(partyId);
    }
}
