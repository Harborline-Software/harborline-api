using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Governance.Consent;

namespace Harborline.Api.LocalNodeHost.Data.Governance;

/// <summary>
/// Single source of truth for the node-side tenant-governance composition (the ADR 0144 AD.1 setup-phase +
/// <c>workshop:unlock</c> mechanics, slice B5). Registers the durable <see cref="ITenantGovernanceStateStore"/>
/// over the node's data directory + the <see cref="INodeWorkshopUnlockAuthority"/> over the
/// <see cref="AuthorizationGate"/>.
/// </summary>
/// <remarks>
/// The caller is responsible for having already registered the <see cref="AuthorizationGate"/> (Program.cs,
/// via the access-grant composition, which registers it as a singleton). The
/// store persists to <c>governance-lifecycle.json</c> under <paramref name="dataDirectory"/> (the node's
/// <c>LocalNodeOptions.DataDirectory</c>).
/// </remarks>
public static class NodeGovernanceComposition
{
    /// <summary>
    /// Registers the tenant-governance store (durable, file-backed) + the workshop-unlock authority.
    /// Idempotent (<c>TryAdd</c>) so it composes safely if called from more than one path.
    /// </summary>
    /// <param name="services">The node composition root.</param>
    /// <param name="dataDirectory">The node data directory the lifecycle document lives in
    /// (<c>LocalNodeOptions.DataDirectory</c>).</param>
    public static IServiceCollection AddNodeTenantGovernance(this IServiceCollection services, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(dataDirectory);

        services.TryAddSingleton<ITenantGovernanceStateStore>(
            sp => FileTenantGovernanceStateStore.InDirectory(
                dataDirectory,
                sp.GetRequiredService<TimeProvider>().GetUtcNow));

        // The tenant consent records (ticket 213) live beside the governance state, in the same durable
        // file family. Registering the store BEFORE AddHarborlineGovernance is what makes the one consent
        // gate read real records instead of the fail-closed NoConsentRecordsStore default.
        services.TryAddSingleton<ITenantConsentStore>(_ => FileTenantConsentStore.InDirectory(dataDirectory));

        // The scheduled expiry sweep (ticket 213 slice 2). It changes no authority -- StateAt already
        // reads a past-due record as expired at the point of use -- but it keeps the STORED state honest,
        // so a report over the raw store does not overcount active consents.
        services.AddHostedService<ConsentExpirySweepDaemon>(sp => new ConsentExpirySweepDaemon(
            sp.GetRequiredService<Harborline.Api.Foundation.Governance.Consent.TenantConsentGate>(),
            sp.GetRequiredService<ITenantConsentStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConsentExpirySweepDaemon>>()));

        services.TryAddSingleton<INodeWorkshopUnlockAuthority>(sp =>
            new NodeWorkshopUnlockAuthority(sp.GetRequiredService<AuthorizationGate>()));

        return services;
    }
}
