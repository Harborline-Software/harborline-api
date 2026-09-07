using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Foundation.Forms.DependencyInjection;

/// <summary>
/// DI registration extensions for the Foundation.Forms keystone substrate.
/// </summary>
public static class FormsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory <see cref="IFormDefinitionStore"/> reference
    /// implementation as a singleton. Suitable for tests, single-process
    /// bootstrapping, and the early authoring-UX iteration loop. The
    /// production Postgres-backed registry registers via a separate
    /// extension method on the entity-store package (forthcoming).
    /// </summary>
    /// <remarks>
    /// Registration is idempotent — calling this method twice does not
    /// double-register or throw. The first registration wins, matching the
    /// fleet's convention for substrate DI hygiene.
    /// </remarks>
    public static IServiceCollection AddInMemoryFormDefinitionStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        AuthorizedFormDefinitionLifecycle.InMemoryPersistenceHandle? persistenceHandle = null;
        services.AddSingleton(sp =>
        {
            persistenceHandle = AuthorizedFormDefinitionLifecycle.CreateInMemoryPersistence(
                sp.GetRequiredService<TimeProvider>());
            return new InMemoryFormDefinitionStore(persistenceHandle);
        });
        services.AddSingleton<IFormDefinitionStore>(sp => sp.GetRequiredService<InMemoryFormDefinitionStore>());
        services.AddSingleton(sp => new AuthorizedFormDefinitionLifecycle(
            sp.GetRequiredService<InMemoryFormDefinitionStore>(),
            persistenceHandle ?? throw new InvalidOperationException("The in-memory form persistence handle was not composed."),
            sp.GetRequiredService<AuthorizationGate>(),
            sp.GetRequiredService<IRoleGateAdmission>(),
            sp.GetService<IFormDefinitionLegalHoldValidator>()));
        return services;
    }

    /// <summary>
    /// Registers <see cref="NoopFormDefinitionStore"/> as the default
    /// <see cref="IFormDefinitionStore"/> via <c>TryAddSingleton</c> —
    /// reads return empty / <see cref="Exceptions.FormDefinitionNotFoundException"/>;
    /// domain-specific authoring mutators throw <see cref="NotSupportedException"/> while shared lifecycle
    /// mutations report the ordinary opaque not-found result of an empty store.
    /// Read-side composition (Ship's Office browser, status surfaces) gets
    /// a non-throwing default; host composition overrides with
    /// <see cref="AddInMemoryFormDefinitionStore"/> (or a Postgres-backed
    /// registration) when authoring is wired.
    /// </summary>
    /// <remarks>
    /// Block-tier packages (cf. <c>blocks-ships-office</c>) call this from
    /// their <c>AddHarborline*Defaults()</c> extension so that consumers
    /// composing the block without an explicit forms store still get a
    /// non-throwing read surface. <c>TryAdd</c> ensures a real store
    /// already registered by the host wins.
    /// </remarks>
    public static IServiceCollection TryAddNoopFormDefinitionStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IFormDefinitionStore, NoopFormDefinitionStore>();
        return services;
    }

    /// <summary>
    /// Registers the durable <see cref="EntityStoreFormDefinitionStore"/> as the
    /// <see cref="IFormDefinitionStore"/> singleton, composing whatever
    /// <see cref="IEntityStore"/> the host has registered. Call after an entity-store
    /// registration — a durable adapter for the production path, or
    /// <c>AddHarborlineAssetsInMemory()</c> for tests. The
    /// <see cref="TimeProvider"/> is resolved from DI when present, otherwise
    /// the composition root must supply it.
    /// </summary>
    /// <remarks>
    /// This store is backend-agnostic by design (it composes the
    /// <see cref="IEntityStore"/> abstraction, not the Postgres package), so durability
    /// is determined entirely by which entity store the host registers.
    /// </remarks>
    public static IServiceCollection AddEntityStoreFormDefinitionStore(
        this IServiceCollection services,
        Func<IServiceProvider, IEntityMutationStore> mutationStore)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mutationStore);
        services.AddSingleton(sp =>
            new EntityStoreFormDefinitionStore(
                sp.GetRequiredService<IEntityStore>(),
                mutationStore(sp),
                sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IFormDefinitionStore>(sp => sp.GetRequiredService<EntityStoreFormDefinitionStore>());
        services.AddSingleton(sp => new AuthorizedFormDefinitionLifecycle(
            sp.GetRequiredService<EntityStoreFormDefinitionStore>(),
            mutationStore(sp),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<AuthorizationGate>(),
            sp.GetRequiredService<IRoleGateAdmission>(),
            sp.GetService<IFormDefinitionLegalHoldValidator>()));
        return services;
    }

    /// <summary>
    /// Registers the default <see cref="PrecedenceSubjectResolver"/> as the
    /// <see cref="ISubjectResolver"/> singleton (override ▸ primary ▸ none
    /// precedence; ADR 0139 HYBRID key-model amendment). The resolver is
    /// stateless, so a singleton is appropriate.
    /// </summary>
    /// <remarks>
    /// Uses <c>TryAddSingleton</c> so a host that wants a richer resolver (for
    /// example one that pseudonymises a record's natural key into a surrogate)
    /// can register its own <see cref="ISubjectResolver"/> first and win. The
    /// per-subject-encryption integration (the governance/PEP layer) consumes
    /// this seam later; the keystone only supplies the resolver mechanism.
    /// </remarks>
    public static IServiceCollection AddDefaultSubjectResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ISubjectResolver, PrecedenceSubjectResolver>();
        return services;
    }

    /// <summary>
    /// Registers the in-memory <see cref="IReusableUnitStore"/> reference implementation as a
    /// singleton (D4 reuse-unit primitive; ADR 0135 amendment 2026-07-01). Suitable for tests,
    /// single-process bootstrapping, and the early authoring loop; a durable store composes the
    /// same contract in a follow-up. Idempotent — the first registration wins.
    /// </summary>
    public static IServiceCollection AddInMemoryReusableUnitStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IReusableUnitStore, InMemoryReusableUnitStore>();
        return services;
    }

    /// <summary>
    /// Registers the <see cref="IReuseResolver"/> that expands reference nodes CP-locked over
    /// whatever <see cref="IReusableUnitStore"/> the host has registered (D4). Register a unit
    /// store first (e.g. <see cref="AddInMemoryReusableUnitStore"/>). Idempotent.
    /// </summary>
    public static IServiceCollection AddReuseResolver(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IReuseResolver, ReuseResolver>();
        return services;
    }
}
