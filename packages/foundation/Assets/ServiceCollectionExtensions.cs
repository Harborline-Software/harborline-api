using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Foundation.Assets.Audit;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Assets.Versions;

namespace Harborline.Api.Foundation.Assets;

/// <summary>
/// DI registration helpers for the Harborline asset-modeling kernel primitives.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="EntityBodyAdmission"/> — the only mint of the
    /// <see cref="ValidatedBody"/> every entity write demands. Idempotent, and called by every
    /// registration that composes something which writes through the mutation port, so a
    /// definition store composed on its own (no asset backend) still builds.
    /// </summary>
    /// <remarks>
    /// The <see cref="IEntityValidator"/> is bound lazily and resolved with
    /// <c>GetRequiredService</c> at the first record-write admission, so a composition missing a
    /// real validator raises on that write instead of accepting the body (ticket 151, ledger
    /// L1418). Envelope and engine-validated mints do not touch the validator and therefore do not
    /// force one to be registered.
    /// </remarks>
    public static IServiceCollection AddEntityBodyAdmission(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(sp => new EntityBodyAdmission(
            sp.GetRequiredService<IEntityValidator>));
        return services;
    }

    /// <summary>
    /// Registers the in-memory backend: shared <see cref="InMemoryAssetStorage"/>, plus the
    /// four primitive services (<see cref="IEntityStore"/>, <see cref="IVersionStore"/>,
    /// <see cref="IAuditLog"/>, <see cref="IHierarchyService"/>) and
    /// <see cref="HierarchyOperations"/> as singletons.
    /// </summary>
    /// <remarks>
    /// Null-object defaults are registered for the observer and audit-context seams.
    /// <see cref="IEntityValidator"/> has NO default: the host binds a real validator (the schema
    /// registry's), and <see cref="EntityBodyAdmission"/> — the only mint of the
    /// <see cref="ValidatedBody"/> the store demands — resolves it with
    /// <c>GetRequiredService</c>, so a composition without one fails loudly instead of accepting
    /// every body (ticket 151, ledger L1418).
    /// </remarks>
    public static IServiceCollection AddHarborlineAssetsInMemory(
        this IServiceCollection services,
        Action<Func<IServiceProvider, IEntityMutationStore>,
            Func<IServiceProvider, IHierarchyCompositeUnitOfWork>>? configureWriters = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<InMemoryAssetStorage>();
        services.TryAddSingleton<IVersionObserver>(NullVersionObserver.Instance);
        services.TryAddSingleton<IAuditContextProvider>(NullAuditContextProvider.Instance);
        services.AddEntityBodyAdmission();

        var backends = new ConditionalWeakTable<IServiceProvider, Lazy<InMemoryAssetBackends>>();
        InMemoryAssetBackends Backends(IServiceProvider provider) => backends.GetValue(
            provider,
            static sp => new Lazy<InMemoryAssetBackends>(() => new InMemoryAssetBackends(
                    sp.GetRequiredService<InMemoryAssetStorage>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetService<IVersionObserver>()),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        services.TryAddSingleton<IEntityStore>(sp => new InMemoryEntityStoreReader(
            Backends(sp).Entities));

        services.TryAddSingleton<IVersionStore>(sp => new InMemoryVersionStore(
            sp.GetRequiredService<InMemoryAssetStorage>()));

        services.TryAddSingleton<IAuditLog>(sp => new InMemoryAuditLog(
            sp.GetRequiredService<InMemoryAssetStorage>()));

        services.TryAddSingleton<IHierarchyService>(sp => new InMemoryHierarchyServiceReader(
            Backends(sp).Hierarchy));

        services.TryAddSingleton(sp => new HierarchyOperations(
            sp.GetRequiredService<IHierarchyCompositeCoordinator>()));

        configureWriters?.Invoke(
            sp => Backends(sp).Entities,
            sp => Backends(sp).Hierarchy);

        return services;
    }

    private sealed class InMemoryAssetBackends(
        InMemoryAssetStorage storage,
        TimeProvider timeProvider,
        IVersionObserver? observer)
    {
        internal InMemoryEntityStore Entities { get; } = new(storage, timeProvider, observer);

        internal InMemoryHierarchyService Hierarchy { get; } = new(storage);
    }
}
