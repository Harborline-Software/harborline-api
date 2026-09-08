using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace Harborline.Api.Foundation.Authorization.DependencyInjection;

/// <summary>
/// DI helper that wires the canonical principal→party resolution seam: the ambient
/// <see cref="IPartyContext"/> facade over the v1 in-memory
/// <see cref="IPrincipalPartyResolver"/>. Hosts call this in their production scope so every
/// write endpoint resolves the acting party through this ONE seam — never a body-supplied id.
/// </summary>
/// <remarks>
/// The facade (<see cref="PartyContext"/>) is registered scoped because it reads the current
/// principal off the per-request <see cref="ITenantContext"/> (itself wired scoped by
/// <c>AddHarborlineTenantContext</c>). The v1 resolver is registered as a singleton — its seed map
/// is immutable. When a people-foundation-backed resolver lands, only the
/// <see cref="IPrincipalPartyResolver"/> registration changes; the facade and its consumers are
/// untouched (the interface is the swap seam).
/// </remarks>
public static class PartyContextServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory v1 principal→party seam. <paramref name="configureSeed"/>
    /// collects the (tenant, user) → party mappings the host knows about; the resulting
    /// resolver is tenant-scoped by key. Requires <c>AddHarborlineTenantContext</c> to have been
    /// called so the scoped <see cref="ITenantContext"/> the facade depends on is available.
    /// </summary>
    public static IServiceCollection AddHarborlinePartyContext(
        this IServiceCollection services,
        Action<IList<PrincipalPartyMapping>> configureSeed)
    {
        ArgumentNullException.ThrowIfNull(configureSeed);

        var seed = new List<PrincipalPartyMapping>();
        configureSeed(seed);

        services.AddSingleton<IPrincipalPartyResolver>(_ => new InMemoryPrincipalPartyResolver(seed));
        services.AddScoped<IPartyContext, PartyContext>();

        return services;
    }
}
