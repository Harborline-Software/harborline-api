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
    /// Registers the in-memory backend: shared <see cref="InMemoryAssetStorage"/>, plus the
    /// four primitive services (<see cref="IEntityStore"/>, <see cref="IVersionStore"/>,
    /// <see cref="IAuditLog"/>, <see cref="IHierarchyService"/>) and
    /// <see cref="HierarchyOperations"/> as singletons.
    /// </summary>
    /// <remarks>
    /// Null-object defaults are registered for the three extensibility seams
    /// (<see cref="IEntityValidator"/>, <see cref="IVersionObserver"/>,
    /// <see cref="IAuditContextProvider"/>); consumers can override them via
    /// <c>services.Replace(...)</c> or direct <c>TryAddSingleton</c> / <c>AddSingleton</c>.
    /// </remarks>
    public static IServiceCollection AddHarborlineAssetsInMemory(
        this IServiceCollection services,
        Action<Func<IServiceProvider, IEntityMutationStore>,
            Func<IServiceProvider, IHierarchyCompositeUnitOfWork>>? configureWriters = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<InMemoryAssetStorage>();
        services.TryAddSingleton<IEntityValidator>(NullEntityValidator.Instance);
        services.TryAddSingleton<IVersionObserver>(NullVersionObserver.Instance);
        services.TryAddSingleton<IAuditContextProvider>(NullAuditContextProvider.Instance);

        var backends = new ConditionalWeakTable<IServiceProvider, Lazy<InMemoryAssetBackends>>();
        InMemoryAssetBackends Backends(IServiceProvider provider) => backends.GetValue(
            provider,
            static sp => new Lazy<InMemoryAssetBackends>(() => new InMemoryAssetBackends(
                    sp.GetRequiredService<InMemoryAssetStorage>(),
                    sp.GetRequiredService<TimeProvider>(),
                    sp.GetService<IEntityValidator>(),
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
        IEntityValidator? validator,
        IVersionObserver? observer)
    {
        internal InMemoryEntityStore Entities { get; } = new(storage, timeProvider, validator, observer);

        internal InMemoryHierarchyService Hierarchy { get; } = new(storage);
    }
}
