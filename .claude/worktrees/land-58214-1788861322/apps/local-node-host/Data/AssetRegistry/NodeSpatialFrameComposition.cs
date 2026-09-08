using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services.Spatial;

namespace Harborline.Api.LocalNodeHost.Data.AssetRegistry;

/// <summary>
/// Wires the Wave-5 durable spatial-frame-descriptor store into the node host (ADR 0101 Rev 3.2
/// [A10]/[A13]; ADR 0168 OQ-1 ruling) and HARD-FAILS the composition when any of the three seams
/// resolves wrong — with last-registration-wins <c>AddSingleton</c> composition, a mis-ordered host
/// call would otherwise silently leave a wrong store resolvable, voiding the CP-5 guarantee with no
/// error (the <c>LocalNodePatternAModuleCatalog</c> refusal pattern).
/// </summary>
public static class NodeSpatialFrameComposition
{
    /// <summary>
    /// Registers, in the [A10] order: the host port (<see cref="NodeEfSpatialFrameDescriptorPort"/>,
    /// ordinary registration), the host authority (<see cref="NodeHomeClaimFrameEpochAuthority"/>,
    /// by <c>Replace</c> over the package's refusing default), and the package adapter behind
    /// <c>ISpatialFrameDescriptorStore</c> (via
    /// <see cref="AssetRegistryServiceCollectionExtensions.AddDurableAssetRegistryDescriptorStore"/>),
    /// then validates the final descriptors. <b>Call AFTER <c>AddNodeAssetRegistry()</c></b> (the
    /// durable audit swap must already be in place — the mint audit obligation rides it).
    /// </summary>
    public static IServiceCollection AddNodeSpatialFrameDescriptors(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The Pattern-A entity module for the descriptor + quarantine tables (idempotent,
        // catalog-owned — the module must exist in the migration catalog too).
        services.AddLocalNodePatternAModule<SpatialFrameEntityModule>();

        // CP-4 (Wave-5 precondition 3): the governed-field sealer the port REQUIRES — it seals
        // originDescription + georeference under the tenant DEK at the storage boundary, on both
        // the descriptor and quarantine rows. Host-side by design: a package-side placement would
        // pull foundation-governance/recovery into the pure-domain registry package. Resolves the
        // host's real ITenantKeyProvider (registered by Program before this call; hard-checked in
        // ValidateSpatialFrameComposition so a key-less composition refuses to build rather than
        // silently persisting cleartext).
        services.AddSingleton<SpatialFramePiiFieldSealer>();

        // The foundation port — ordinary host registration ([A13]).
        services.AddSingleton<ISpatialFrameDescriptorPort, NodeEfSpatialFrameDescriptorPort>();

        // The package adapter behind the store interface + the refusing default authority.
        services.AddDurableAssetRegistryDescriptorStore();

        // The host authority swap targets the AUTHORITY seam — NEVER ISpatialFrameDescriptorStore
        // (replacing the store interface would evict the package adapter and with it the internal
        // RegistryTenantGuard call).
        services.Replace(ServiceDescriptor.Singleton<IFrameEpochAuthority, NodeHomeClaimFrameEpochAuthority>());

        services.ValidateSpatialFrameComposition();
        return services;
    }

    /// <summary>
    /// The [A10] composition hard-fail: refuses host construction unless (i) the store resolves to
    /// the package-side adapter, (ii) the foundation port resolves to the host durable
    /// implementation, and (iii) the authority resolves to the host home-claim implementation.
    /// </summary>
    public static void ValidateSpatialFrameComposition(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        RequireResolution(
            services, typeof(ISpatialFrameDescriptorStore),
            typeof(FoundationBackedSpatialFrameDescriptorStore),
            "spatial-frames.store: ISpatialFrameDescriptorStore must resolve to the PACKAGE-SIDE "
            + "adapter (never a host type) — ADR 0101 Rev 3.2 [A10](i)/[A13].");

        RequireResolution(
            services, typeof(ISpatialFrameDescriptorPort),
            typeof(NodeEfSpatialFrameDescriptorPort),
            "spatial-frames.port: the foundation descriptor port must resolve to the host durable "
            + "implementation — ADR 0101 Rev 3.2 [A10](ii).");

        RequireResolution(
            services, typeof(IFrameEpochAuthority),
            typeof(NodeHomeClaimFrameEpochAuthority),
            "spatial-frames.authority: IFrameEpochAuthority must resolve to the host home-claim "
            + "implementation — ADR 0101 Rev 3.2 [A10](iii).");

        // CP-4 hard-fail: the sealer must be registered AND a tenant-key provider must exist to
        // back it — otherwise the first mint would fail (fail-closed) or, worse, a future refactor
        // could quietly drop the sealing. Refuse at composition time instead.
        if (!services.Any(d => d.ServiceType == typeof(SpatialFramePiiFieldSealer)))
        {
            throw new InvalidOperationException(
                "spatial-frames.sealing: SpatialFramePiiFieldSealer is not registered — the CP-4 "
                + "governed-field sealing (ADR 0168 D2-A8) would be absent and the two PII columns "
                + "would persist cleartext.");
        }
        // Last-descriptor + concrete-type ALLOWLIST gate (the NodeBlobEnvelopeKeyPosture PERS-2 F1
        // shape) — presence alone is not posture: a dev stub or an opaque factory registered LAST
        // would silently seal the two governed PII columns under derivable/unverifiable key
        // material (the bug-1312 no-mock-crypto family). Refuse composition instead.
        Harborline.Api.LocalNodeHost.Data.KeyDistribution.NodeBlobEnvelopeKeyPosture.RequireRealTenantKeyProvider(
            services,
            "spatial-frames.sealing key-posture gate",
            "CP-4 spatial-frame governed-field sealing");
    }

    private static void RequireResolution(
        IServiceCollection services, Type serviceType, Type expectedImplementation, string message)
    {
        // Last-registration-wins: the EFFECTIVE registration is the final descriptor for the type.
        var effective = services.LastOrDefault(d => d.ServiceType == serviceType);
        if (effective is null || effective.Lifetime != ServiceLifetime.Singleton
            || effective.ImplementationType != expectedImplementation)
        {
            throw new InvalidOperationException(
                $"{message} Effective registration: "
                + $"{effective?.ImplementationType?.FullName ?? effective?.ImplementationFactory?.ToString() ?? "MISSING"}.");
        }
    }
}
