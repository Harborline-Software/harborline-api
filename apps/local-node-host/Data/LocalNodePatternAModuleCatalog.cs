using System.Collections.Immutable;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Harborline.Api.Blocks.FinancialAp.Data;
using Harborline.Api.Blocks.FinancialAr.Data;
using Harborline.Api.Blocks.Banking.Data;
using Harborline.Api.Blocks.Docs.Data;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.FinancialPeriods.Data;
using Harborline.Api.Blocks.FinancialPayments.Data;
using Harborline.Api.Blocks.People.Foundation.Data;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;
using Harborline.Api.LocalNodeHost.Data.Workflow;

namespace Harborline.Api.LocalNodeHost.Data;

/// <summary>
/// Single code-owned authority for the entity modules that share <see cref="LocalNodeDbContext"/>.
/// Runtime composition, EF design-time scaffolding, and migration-path tests must all consume this
/// catalog so a module cannot exist in the runtime model without also entering the migration model.
/// </summary>
internal static class LocalNodePatternAModuleCatalog
{
    private static readonly ImmutableArray<LocalNodePatternAModuleDescriptor> s_all = CreateCatalog();

    internal static IReadOnlyList<LocalNodePatternAModuleDescriptor> All => s_all;

    /// <summary>Registers the complete Pattern-A module set idempotently.</summary>
    internal static IServiceCollection AddLocalNodePatternAModules(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (var descriptor in s_all)
        {
            Register(services, descriptor);
        }

        return services;
    }

    /// <summary>
    /// Registers one catalog-owned module idempotently for a standalone composition slice.
    /// The type must already belong to the canonical catalog.
    /// </summary>
    internal static IServiceCollection AddLocalNodePatternAModule<TModule>(this IServiceCollection services)
        where TModule : class, IHarborlineEntityModule, new()
    {
        ArgumentNullException.ThrowIfNull(services);

        var descriptor = s_all.SingleOrDefault(item => item.ImplementationType == typeof(TModule))
            ?? throw new InvalidOperationException(
                $"Pattern-A module '{typeof(TModule).FullName}' is not owned by the local-node catalog.");

        Register(services, descriptor);
        return services;
    }

    /// <summary>
    /// Closes the production composition over the actual final service descriptors. Any missing,
    /// duplicate, opaque, non-singleton, or out-of-catalog module refuses host construction before
    /// <see cref="LocalNodeDbContext"/> can build or migrate a divergent runtime model.
    /// </summary>
    internal static void ValidateLocalNodePatternAModules(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var registrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHarborlineEntityModule))
            .OrderBy(
                descriptor => descriptor.ImplementationType?.FullName ?? string.Empty,
                StringComparer.Ordinal)
            .ToArray();

        var opaque = registrations.FirstOrDefault(descriptor => descriptor.ImplementationType is null);
        if (opaque is not null)
        {
            throw new InvalidOperationException(
                "local-node.pattern-a.opaque: entity-module factories and instances are not admitted.");
        }

        var nonSingleton = registrations.FirstOrDefault(
            descriptor => descriptor.Lifetime != ServiceLifetime.Singleton);
        if (nonSingleton is not null)
        {
            throw new InvalidOperationException(
                $"local-node.pattern-a.lifetime: module '{nonSingleton.ImplementationType!.FullName}' " +
                "must be registered as a singleton.");
        }

        var observedTypes = registrations.Select(descriptor => descriptor.ImplementationType!).ToArray();
        var duplicate = observedTypes
            .GroupBy(type => type)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"local-node.pattern-a.duplicate: module '{duplicate.Key.FullName}' is registered more than once.");
        }

        var expectedTypes = s_all.Select(descriptor => descriptor.ImplementationType).ToHashSet();
        var unexpected = observedTypes.FirstOrDefault(type => !expectedTypes.Contains(type));
        if (unexpected is not null)
        {
            throw new InvalidOperationException(
                $"local-node.pattern-a.unexpected: module '{unexpected.FullName}' is not in the migration catalog.");
        }

        var observedSet = observedTypes.ToHashSet();
        var missing = s_all
            .Select(descriptor => descriptor.ImplementationType)
            .FirstOrDefault(type => !observedSet.Contains(type));
        if (missing is not null)
        {
            throw new InvalidOperationException(
                $"local-node.pattern-a.missing: catalog module '{missing.FullName}' is not registered.");
        }
    }

    /// <summary>Creates fresh module instances for EF design-time and migration verification.</summary>
    internal static IHarborlineEntityModule[] CreateModules() =>
        s_all.Select(descriptor => descriptor.Create()).ToArray();

    private static ImmutableArray<LocalNodePatternAModuleDescriptor> CreateCatalog()
    {
        var catalog = ImmutableArray.Create(
            Describe<FinancialLedgerEntityModule>(),
            Describe<FinancialPeriodsEntityModule>(),
            Describe<ArEntityModule>(),
            Describe<ApEntityModule>(),
            Describe<PaymentsEntityModule>(),
            Describe<BankingEntityModule>(),
            Describe<PeopleEntityModule>(),
            Describe<DocsEntityModule>(),
            Describe<AuditEventEntityModule>(),
            Describe<FormSubmitOutboxEntityModule>(),
            Describe<HomeEpochEntityModule>(),
            Describe<WorkflowEntityModule>(),
            Describe<SpatialFrameEntityModule>());

        if (catalog.Select(item => item.ModuleKey).Distinct(StringComparer.Ordinal).Count() != catalog.Length ||
            catalog.Select(item => item.ImplementationType).Distinct().Count() != catalog.Length)
        {
            throw new InvalidOperationException(
                "The local-node Pattern-A module catalog contains a duplicate key or implementation type.");
        }

        return catalog;
    }

    private static LocalNodePatternAModuleDescriptor Describe<TModule>()
        where TModule : class, IHarborlineEntityModule, new()
    {
        var instance = new TModule();
        if (string.IsNullOrWhiteSpace(instance.ModuleKey) ||
            !string.Equals(instance.ModuleKey, instance.ModuleKey.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pattern-A module '{typeof(TModule).FullName}' has an invalid stable module key.");
        }

        return new LocalNodePatternAModuleDescriptor(instance.ModuleKey, typeof(TModule));
    }

    private static void Register(
        IServiceCollection services,
        LocalNodePatternAModuleDescriptor descriptor) =>
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton(typeof(IHarborlineEntityModule), descriptor.ImplementationType));
}

/// <summary>Neutral assembly-binding evidence for one Pattern-A entity module.</summary>
internal sealed record LocalNodePatternAModuleDescriptor(string ModuleKey, Type ImplementationType)
{
    internal IHarborlineEntityModule Create()
    {
        var module = (IHarborlineEntityModule?)Activator.CreateInstance(ImplementationType)
            ?? throw new InvalidOperationException(
                $"Pattern-A module '{ImplementationType.FullName}' could not be constructed.");

        if (!string.Equals(ModuleKey, module.ModuleKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pattern-A module '{ImplementationType.FullName}' changed its stable module key from " +
                $"'{ModuleKey}' to '{module.ModuleKey}'.");
        }

        return module;
    }
}
