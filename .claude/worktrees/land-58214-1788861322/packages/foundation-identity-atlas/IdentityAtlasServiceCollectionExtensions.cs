using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;

namespace Harborline.Api.Foundation.IdentityAtlas;

/// <summary>
/// DI registration helpers for the Identity Atlas foundation contracts.
/// </summary>
public static class IdentityAtlasServiceCollectionExtensions
{
    /// <summary>
    /// Registers null-object default implementations for all Identity Atlas backing-service
    /// contracts (<see cref="IKeyStore"/>, <see cref="ITrusteeRegistry"/>,
    /// <see cref="ITeamRegistry"/>).
    /// Use <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace"/>
    /// in the host registration to swap in real implementations when a wallet/keystore
    /// workstream ships.
    /// </summary>
    public static IServiceCollection AddHarborlineIdentityAtlasDefaults(
        this IServiceCollection services)
    {
        services.TryAddSingleton<IKeyStore, NullKeyStore>();
        services.TryAddSingleton<ITrusteeRegistry, NullTrusteeRegistry>();
        services.TryAddSingleton<ITeamRegistry, NullTeamRegistry>();
        // The SoD-compensating-control AUDIT SEAM (enrollment Phase C; project_sod_compensating_controls).
        // The bundled DEFAULT is the no-op NullEnrollmentCompensatingControlRecorder so a fresh single-user / dev / test host still
        // functions with no audit wiring. A SHIPPING host (local-node-host) Replaces this with the
        // kernel-audit-backed KernelAuditEnrollmentCompensatingControlRecorder so the SoD control records LIVE (TryAdd ⇒ the host's
        // explicit registration wins; this is only the fallback floor).
        services.TryAddSingleton<IEnrollmentCompensatingControlRecorder>(NullEnrollmentCompensatingControlRecorder.Instance);
        return services;
    }

    /// <summary>
    /// Registers the real in-memory membership store (<see cref="InMemoryTeamRegistry"/>) as both
    /// <see cref="ITeamRegistry"/> (read side) and <see cref="IMutableTeamRegistry"/> (write side),
    /// completing ADR 0032's identity layer.
    /// </summary>
    /// <remarks>
    /// One singleton instance backs both interface registrations so the read and write views see
    /// the same edges. Hosts that need a persisted membership roster (the synced append-log
    /// doctype, survey #1275 §4) <see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.Replace"/>
    /// this with their implementation behind the same two interfaces.
    /// </remarks>
    public static IServiceCollection AddHarborlineTeamMembershipStore(
        this IServiceCollection services)
    {
        services.TryAddSingleton<InMemoryTeamRegistry>();
        services.TryAddSingleton<ITeamRegistry>(sp => sp.GetRequiredService<InMemoryTeamRegistry>());
        services.TryAddSingleton<IMutableTeamRegistry>(sp => sp.GetRequiredService<InMemoryTeamRegistry>());
        return services;
    }
}
