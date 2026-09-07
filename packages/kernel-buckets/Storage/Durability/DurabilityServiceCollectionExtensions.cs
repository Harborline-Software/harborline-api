using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 (ADR 0137 D5b) DI registration for the durability guard and its collaborators.
/// </summary>
public static class DurabilityServiceCollectionExtensions
{
    /// <summary>
    /// Register the durability-guard substrate (fail-closed defaults): the never-evict registry (F0b), the
    /// verify-before-evict possession verifier (ledger + freshness probe), the Phase-1 always-eligible predicate
    /// stub, the no-op eviction observer, and the guard itself (N = <see cref="DurabilityGuardOptions.MinimumSafeReplicas"/>).
    /// Idempotent (<c>TryAdd</c>) so a host may override any collaborator (e.g. Phase 3 swaps the eligibility
    /// predicate, a host wires a real observer, a deployment raises N via <paramref name="configureOptions"/>).
    /// </summary>
    /// <remarks>
    /// The DEFAULT possession ledger is empty, so with no confirmed replicas the guard refuses every shed — the
    /// SAFE direction (a node keeps its only copy rather than lose it). A deployment that wants LRU eviction of
    /// re-fetchable lazy bodies must record + confirm the canonical peer's possession; the guard then permits
    /// shedding exactly those bodies that are provably durable elsewhere.
    /// </remarks>
    public static IServiceCollection AddHarborlineDurabilityGuard(
        this IServiceCollection services,
        Action<DurabilityGuardOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new DurabilityGuardOptions();
        configureOptions?.Invoke(options);
        services.TryAddSingleton(options);

        services.TryAddSingleton<INeverEvictRegistry, InMemoryNeverEvictRegistry>();
        services.TryAddSingleton<IReplicaPossessionLedger, InMemoryReplicaPossessionLedger>();
        services.TryAddSingleton<IReplicaPossessionProbe>(sp => new FreshnessWindowPossessionProbe(
            FreshnessWindowPossessionProbe.DefaultWindow, FreshnessWindowPossessionProbe.DefaultClockSkew,
            sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IReplicaPossessionVerifier, LedgerReplicaPossessionVerifier>();
        services.TryAddSingleton<IDestinationEligibilityPredicate, AlwaysEligibleDestinationPredicate>();
        services.TryAddSingleton<IDurabilityEvictionObserver>(NullDurabilityEvictionObserver.Instance);
        // F-Maj-2: the local node's replica identity (the shed subject the guard excludes). Default is the
        // unconfigured sentinel; a deployment that records self-possession MUST register its real identity.
        services.TryAddSingleton<ILocalReplicaIdentity>(LocalReplicaIdentity.Unconfigured);

        services.TryAddSingleton<IDurabilityGuard>(sp => new DurabilityGuard(
            sp.GetRequiredService<INeverEvictRegistry>(),
            sp.GetRequiredService<IReplicaPossessionVerifier>(),
            sp.GetRequiredService<IDestinationEligibilityPredicate>(),
            sp.GetRequiredService<DurabilityGuardOptions>()));

        return services;
    }

    /// <summary>
    /// Wrap the currently-registered <see cref="IBlobStore"/> with a <see cref="DurabilityGuardedBlobStore"/> so
    /// <see cref="IBlobStore.UnpinAsync"/> is durability-gated. Requires an <see cref="IBlobStore"/> and the
    /// durability guard to already be registered (call <see cref="AddHarborlineDurabilityGuard"/> first). The
    /// existing registration is captured and re-registered wrapped (last-wins <c>AddSingleton</c>).
    /// </summary>
    public static IServiceCollection AddDurabilityGuardedBlobStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Capture the inner blob-store descriptor so we can resolve it and wrap it.
        var innerDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(IBlobStore))
            ?? throw new InvalidOperationException(
                "AddDurabilityGuardedBlobStore requires an IBlobStore to be registered first.");

        services.AddSingleton<IBlobStore>(sp =>
        {
            var inner = ResolveDescriptor(sp, innerDescriptor);
            return new DurabilityGuardedBlobStore(
                inner,
                sp.GetRequiredService<IDurabilityGuard>(),
                sp.GetRequiredService<ILocalReplicaIdentity>());
        });

        return services;
    }

    private static IBlobStore ResolveDescriptor(IServiceProvider sp, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IBlobStore instance)
        {
            return instance;
        }
        if (descriptor.ImplementationFactory is not null)
        {
            return (IBlobStore)descriptor.ImplementationFactory(sp);
        }
        if (descriptor.ImplementationType is not null)
        {
            return (IBlobStore)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType);
        }
        throw new InvalidOperationException("The captured IBlobStore descriptor has no resolvable implementation.");
    }
}
