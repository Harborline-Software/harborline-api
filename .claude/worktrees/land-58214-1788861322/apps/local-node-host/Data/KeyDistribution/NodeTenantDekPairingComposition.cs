using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Security.KeyDistribution;
using Harborline.Api.LocalNodeHost.Enrollment;

namespace Harborline.Api.LocalNodeHost.Data.KeyDistribution;

/// <summary>
/// MD-1 tenant-DEK pairing composition (joint ADR 0113+0117 amendment, 2026-06-24) — the host wiring for the
/// no-mock-crypto <b>G-1</b> seam. Registers the wrap CONSTRUCTION (<see cref="ITenantDekWrapper"/> /
/// <see cref="ITenantDekUnwrapper"/>) and a FAIL-CLOSED default recipient-key resolver; a full host that supplies
/// the verified roster OVERRIDES the default with the roster-bound resolver.
/// </summary>
/// <remarks>
/// <para>
/// <b>The fail-closed default is the point.</b> <see cref="AddNodeTenantDekPairing"/> alone registers
/// <see cref="NoTenantDekPairingResolver"/> — a minimal DI graph (no roster) can wrap NO DEK to anyone. Only
/// <see cref="AddRosterBoundTenantDekPairingResolver"/> (called from the composition root once the verified roster is
/// in scope) deposes it with the real <see cref="RosterBoundTenantDekPairingResolver"/>. This mirrors the comms DM
/// key provider exactly: a host that forgets to wire the roster-bound resolver degrades to fail-closed (no pairing),
/// never to an identity-derivable key — the structural defence against the #1325 false-positive.
/// </para>
/// </remarks>
public static class NodeTenantDekPairingComposition
{
    /// <summary>
    /// Register the MD-1 tenant-DEK pairing surface with the FAIL-CLOSED default resolver. The wrap construction
    /// (<see cref="TenantDekWrapper"/>) is registered idempotently (it depends only on <see cref="IX25519KeyAgreement"/>,
    /// which the caller wires via <c>AddHarborlineKernelSecurity</c>). The recipient-key resolver defaults to the
    /// fail-closed null-object — a full host overrides it via <see cref="AddRosterBoundTenantDekPairingResolver"/>.
    /// </summary>
    public static IServiceCollection AddNodeTenantDekPairing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The wrap construction — idempotent. (kernel-security's AddHarborlineKernelSecurity also registers these; the
        // TryAdd here makes a node-host composition that did not call it still resolve the wrapper over the X25519
        // primitive. The X25519 primitive itself must be registered by the caller.)
        services.TryAddSingleton<IX25519KeyAgreement, X25519KeyAgreement>();
        services.TryAddSingleton<TenantDekWrapper>();
        services.TryAddSingleton<ITenantDekWrapper>(sp => sp.GetRequiredService<TenantDekWrapper>());
        services.TryAddSingleton<ITenantDekUnwrapper>(sp => sp.GetRequiredService<TenantDekWrapper>());

        // FAIL-CLOSED DEFAULT (G-1) — no recipient is resolvable, so a minimal graph wraps no DEK. TryAdd so a full
        // host's AddRosterBoundTenantDekPairingResolver Replace deposes it deterministically; a host that never calls
        // that stays fail-closed (the safe floor).
        services.TryAddSingleton<ITenantDekPairingResolver>(_ => new NoTenantDekPairingResolver());

        return services;
    }

    /// <summary>
    /// MD-1 / G-1 PRODUCTION wiring — OVERRIDE the fail-closed <see cref="NoTenantDekPairingResolver"/> with the real
    /// <see cref="RosterBoundTenantDekPairingResolver"/> that resolves recipient wrap keys SOLELY from the verified
    /// <see cref="NodeTeamRoster"/>. Call from the composition root once the install-level roster is registered.
    /// </summary>
    /// <remarks>
    /// An explicit <see cref="ServiceCollectionDescriptorExtensions.Replace">Replace</see> (not TryAdd) so it deposes
    /// the NoTenant default deterministically — exactly as <c>AddRosterBoundDmKeyProvider</c> deposes
    /// <c>NoDmConversationKeyProvider</c>. Resolved via DI so the <see cref="NodeTeamRoster"/> singleton is the SAME
    /// live roster the merge gate + trust policy use (a freshly admitted party's DM key is visible immediately).
    /// </remarks>
    public static IServiceCollection AddRosterBoundTenantDekPairingResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.Replace(ServiceDescriptor.Singleton<ITenantDekPairingResolver>(sp =>
            new RosterBoundTenantDekPairingResolver(sp.GetRequiredService<NodeTeamRoster>())));

        return services;
    }
}
