using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Kernel.Events;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.Kernel.SchemaRegistry.Compaction;
using Harborline.Api.Kernel.SchemaRegistry.Epochs;
using Harborline.Api.Kernel.SchemaRegistry.Lenses;
using Harborline.Api.Kernel.SchemaRegistry.Migration;
using Harborline.Api.Kernel.SchemaRegistry.Upcasters;

namespace Harborline.Api.Kernel.Schema.DependencyInjection;

/// <summary>
/// DI extensions for registering the Harborline kernel schema registry (spec §3.4) and
/// the schema-migration surface added in paper §7.3 / §7.4 (bidirectional lenses,
/// upcasters, epoch coordinator) plus the stream-compaction surface from paper §7.2
/// (scheduler, upcaster retirement, stream archive).
/// </summary>
public static class SchemaRegistryServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory schema registry backend as a singleton
    /// <see cref="ISchemaRegistry"/>, along with the migration primitives required by
    /// paper §7 (a <see cref="LensGraph"/>, an <see cref="UpcasterChain"/>, and an
    /// <see cref="IEpochCoordinator"/>). The registry has no <c>IBlobStore</c>
    /// dependency — schema bytes live inline on the <see cref="Schema"/> record
    /// (card 3776 removed the orphan-producing blob side-store).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lens graph, upcaster chain, and epoch coordinator are registered as
    /// shared singletons so downstream components (migrators, rehydrators,
    /// sync-daemon cut-over watchers) can depend on them directly without resolving
    /// the full <see cref="ISchemaRegistry"/>.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddHarborlineKernelSchemaRegistry(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHarborlineSchemaMigration();
        services.TryAddSingleton<ISchemaRegistry>(sp => new InMemorySchemaRegistry(
            sp.GetRequiredService<LensGraph>(),
            sp.GetRequiredService<UpcasterChain>(),
            sp.GetRequiredService<IEpochCoordinator>()));
        return services;
    }

    /// <summary>
    /// Registers the schema-migration primitives (lens graph, upcaster chain, epoch
    /// coordinator) without registering the <see cref="ISchemaRegistry"/> itself.
    /// Intended for callers who want to wire a custom registry implementation but
    /// reuse the standard migration surface.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddHarborlineSchemaMigration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<LensGraph>();
        services.TryAddSingleton<IUpcasterRetirement, UpcasterRetirement>();
        services.TryAddSingleton<UpcasterChain>(sp => new UpcasterChain(sp.GetService<IUpcasterRetirement>()));
        services.TryAddSingleton<IEpochCoordinator, EpochCoordinator>();
        return services;
    }

    /// <summary>
    /// Registers the stream-compaction surface described in paper §7.2: an
    /// <see cref="ICompactionScheduler"/>, an <see cref="IUpcasterRetirement"/>, and
    /// an <see cref="IStreamArchive"/>. All three are registered as singletons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scheduler needs a way to resolve the source/target <see cref="IEventLog"/>
    /// pair for each job. By default this extension registers the scheduler with a
    /// resolver that throws at run time — the caller is expected to replace the
    /// resolver (or use the overload that accepts one) before scheduling any job.
    /// The <see cref="LensGraph"/> is resolved from DI; a singleton is registered
    /// automatically if the caller has not already done so.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddHarborlineStreamCompaction(this IServiceCollection services)
    {
        return AddHarborlineStreamCompaction(
            services,
            logResolver: _ => throw new InvalidOperationException(
                "No compaction log resolver configured. Call AddHarborlineStreamCompaction(logResolver) before scheduling any job."));
    }

    /// <summary>
    /// Registers the stream-compaction surface with a caller-supplied log resolver.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="logResolver">Given a <see cref="CompactionJobDefinition.JobId"/>, return the source and target <see cref="IEventLog"/> for that job.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddHarborlineStreamCompaction(
        this IServiceCollection services,
        Func<string, (IEventLog Source, IEventLog Target)> logResolver)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logResolver);

        services.TryAddSingleton<LensGraph>();
        services.TryAddSingleton<IUpcasterRetirement, UpcasterRetirement>();
        services.TryAddSingleton<IStreamArchive, StreamArchive>();
        services.TryAddSingleton<ICompactionScheduler>(sp => new CompactionScheduler(
            logResolver,
            () => sp.GetRequiredService<LensGraph>(),
            new CopyTransformMigrator(),
            sp.GetRequiredService<TimeProvider>()));

        return services;
    }
}
