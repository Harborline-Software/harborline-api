using System.Collections.Immutable;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;

namespace Harborline.Api.LocalNodeHost.Capabilities;

internal enum LocalNodeRegistrationScope
{
    Root,
}

internal enum LocalNodeTechnicalActivation
{
    Always,
}

/// <summary>
/// Structural registration evidence for one root-container EF context factory. This record says
/// only that the compiled service graph contains the factory expected by an existing code-owned
/// catalog. It does not describe runtime storage behavior.
/// </summary>
internal sealed record LocalNodeEfStoreRegistrationDescriptor(
    string StoreKey,
    Type ContextType,
    Type FactoryServiceType,
    ServiceLifetime ExpectedLifetime,
    LocalNodeRegistrationScope RegistrationScope,
    LocalNodeTechnicalActivation Activation,
    ImmutableArray<string> SchemaContributorKeys);

/// <summary>
/// Root-container technical registration evidence selected for one compiled host profile. This is
/// deliberately not a product capability registration or an artifact receipt.
/// </summary>
internal sealed record LocalNodeTechnicalRegistrationEvidence(
    LocalNodeHostedComponentProfile Profile,
    ImmutableArray<LocalNodeEfStoreRegistrationDescriptor> RootEfStores,
    ImmutableArray<LocalNodeHostedComponentDescriptor> SelectedOperationalActors,
    ImmutableArray<LocalNodeEndpointRegistrarDescriptor> SelectedEndpointRegistrars);

/// <summary>
/// Derives one exact root-container projection from the validated service graph and its existing EF
/// and hosted-component catalogs. Non-EF registrations and team child containers require separate
/// code-owned evidence.
/// </summary>
internal static class LocalNodeTechnicalRegistrationEvidenceCatalog
{
    private const string PrimaryStoreKey = "local-node.ef.primary";
    private static readonly ImmutableArray<LocalNodeEfStoreRegistrationDescriptor> s_rootEfStores =
        CreateRootEfStores();

    internal static IReadOnlyList<LocalNodeEfStoreRegistrationDescriptor> RootEfStores => s_rootEfStores;

    internal static IServiceCollection AddLocalNodeTechnicalRegistrationEvidence(
        this IServiceCollection services,
        LocalNodeHostedComponentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(profile);

        services.AddSingleton(CreateFromValidatedGraph(services, profile));
        return services;
    }

    internal static void ValidateLocalNodeTechnicalRegistrationEvidence(
        this IServiceCollection services,
        LocalNodeHostedComponentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(profile);

        var expected = CreateFromValidatedGraph(services, profile);

        var registrations = services
            .Where(item => item.ServiceType == typeof(LocalNodeTechnicalRegistrationEvidence))
            .ToArray();
        if (registrations.Length == 0)
        {
            throw new InvalidOperationException(
                "local-node.technical-registration.evidence_missing: one root evidence instance is required.");
        }

        if (registrations.Length != 1)
        {
            throw new InvalidOperationException(
                "local-node.technical-registration.evidence_duplicate: root evidence must be registered once.");
        }

        var registration = registrations[0];
        if (registration.Lifetime != ServiceLifetime.Singleton)
        {
            throw new InvalidOperationException(
                "local-node.technical-registration.evidence_lifetime: root evidence must be singleton.");
        }

        if (registration.ImplementationInstance is not LocalNodeTechnicalRegistrationEvidence observed)
        {
            throw new InvalidOperationException(
                "local-node.technical-registration.evidence_opaque: root evidence must expose its instance.");
        }

        if (!Equivalent(observed, expected))
        {
            throw new InvalidOperationException(
                "local-node.technical-registration.evidence_mismatch: root evidence does not match the " +
                "compiled profile and source catalogs.");
        }
    }

    private static LocalNodeTechnicalRegistrationEvidence CreateFromValidatedGraph(
        IServiceCollection services,
        LocalNodeHostedComponentProfile profile)
    {
        services.ValidateLocalNodePatternAModules();
        services.ValidateLocalNodeExclusiveEfContexts();
        services.ValidateLocalNodeHostedComponents(profile);

        return new(
            profile,
            ProjectRootEfStores(services),
            ProjectOperationalActors(services),
            ProjectEndpointRegistrars(services));
    }

    private static ImmutableArray<LocalNodeEfStoreRegistrationDescriptor> ProjectRootEfStores(
        IServiceCollection services)
    {
        var observedContributors = services
            .Where(item => item.ServiceType == typeof(Harborline.Api.Foundation.Persistence.IHarborlineEntityModule))
            .Select(item => item.ImplementationType!)
            .ToHashSet();
        var contributorKeys = LocalNodePatternAModuleCatalog.All
            .Where(item => observedContributors.Contains(item.ImplementationType))
            .Select(item => item.ModuleKey)
            .ToImmutableArray();

        return s_rootEfStores.Select(template =>
        {
            var registration = services.Single(item => item.ServiceType == template.FactoryServiceType);
            return template with
            {
                ExpectedLifetime = registration.Lifetime,
                SchemaContributorKeys = template.ContextType == typeof(LocalNodeDbContext)
                    ? contributorKeys
                    : ImmutableArray<string>.Empty,
            };
        }).ToImmutableArray();
    }

    private static ImmutableArray<LocalNodeHostedComponentDescriptor> ProjectOperationalActors(
        IServiceCollection services)
    {
        var byType = LocalNodeHostedComponentCatalog.Operational.ToDictionary(item => item.ComponentType);
        return services
            .Where(item => item.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(LocalNodeHostedComponentCatalog.ResolveImplementationType)
            .Where(type => type is not null && byType.ContainsKey(type))
            .Select(type => byType[type!])
            .ToImmutableArray();
    }

    private static ImmutableArray<LocalNodeEndpointRegistrarDescriptor> ProjectEndpointRegistrars(
        IServiceCollection services)
    {
        var observed = services
            .Where(item => item.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService))
            .Select(LocalNodeHostedComponentCatalog.ResolveImplementationType)
            .Where(type => type is not null)
            .ToHashSet();
        return LocalNodeHostedComponentCatalog.EndpointRegistrars
            .Where(item => observed.Contains(item.RegistrarType))
            .ToImmutableArray();
    }

    private static ImmutableArray<LocalNodeEfStoreRegistrationDescriptor> CreateRootEfStores()
    {
        var schemaContributors = LocalNodePatternAModuleCatalog.All
            .Select(item => item.ModuleKey)
            .ToImmutableArray();
        var expectedStoreCount = 1 + LocalNodeExclusiveEfContextCatalog.All.Count;
        var stores = ImmutableArray.CreateBuilder<LocalNodeEfStoreRegistrationDescriptor>(expectedStoreCount);
        stores.Add(Describe(PrimaryStoreKey, typeof(LocalNodeDbContext), schemaContributors));
        stores.AddRange(LocalNodeExclusiveEfContextCatalog.All
            .OrderBy(item => item.ExecutionOrder)
            .Select(item => Describe(
                item.ContextKey,
                item.ContextType,
                ImmutableArray<string>.Empty)));

        var result = stores.MoveToImmutable();
        if (result.Length != expectedStoreCount ||
            result.Select(item => item.StoreKey).Distinct(StringComparer.Ordinal).Count() != result.Length ||
            result.Select(item => item.ContextType).Distinct().Count() != result.Length ||
            schemaContributors.Distinct(StringComparer.Ordinal).Count() != schemaContributors.Length)
        {
            throw new InvalidOperationException(
                "local-node.technical-registration.catalog_invalid: expected the primary EF root plus every " +
                "catalog-owned exclusive EF root, with unique store, context, and schema contributor keys.");
        }

        return result;
    }

    private static LocalNodeEfStoreRegistrationDescriptor Describe(
        string storeKey,
        Type contextType,
        ImmutableArray<string> schemaContributorKeys) =>
        new(
            storeKey,
            contextType,
            typeof(IDbContextFactory<>).MakeGenericType(contextType),
            ServiceLifetime.Singleton,
            LocalNodeRegistrationScope.Root,
            LocalNodeTechnicalActivation.Always,
            schemaContributorKeys);

    private static bool Equivalent(
        LocalNodeTechnicalRegistrationEvidence observed,
        LocalNodeTechnicalRegistrationEvidence expected) =>
        observed.Profile == expected.Profile &&
        observed.RootEfStores.Length == expected.RootEfStores.Length &&
        observed.RootEfStores.Zip(expected.RootEfStores).All(pair => Equivalent(pair.First, pair.Second)) &&
        observed.SelectedOperationalActors.SequenceEqual(expected.SelectedOperationalActors) &&
        observed.SelectedEndpointRegistrars.SequenceEqual(expected.SelectedEndpointRegistrars);

    private static bool Equivalent(
        LocalNodeEfStoreRegistrationDescriptor observed,
        LocalNodeEfStoreRegistrationDescriptor expected) =>
        string.Equals(observed.StoreKey, expected.StoreKey, StringComparison.Ordinal) &&
        observed.ContextType == expected.ContextType &&
        observed.FactoryServiceType == expected.FactoryServiceType &&
        observed.ExpectedLifetime == expected.ExpectedLifetime &&
        observed.RegistrationScope == expected.RegistrationScope &&
        observed.Activation == expected.Activation &&
        observed.SchemaContributorKeys.SequenceEqual(expected.SchemaContributorKeys, StringComparer.Ordinal);
}

/// <summary>
/// Runs the one final graph close inside provider construction, after every service-registration
/// callback has completed and before any service can resolve.
/// </summary>
internal sealed class LocalNodeFinalGraphServiceProviderFactory(
    LocalNodeHostedComponentProfile profile,
    ServiceProviderOptions options) : IServiceProviderFactory<IServiceCollection>
{
    private readonly LocalNodeHostedComponentProfile _profile =
        profile ?? throw new ArgumentNullException(nameof(profile));
    private readonly DefaultServiceProviderFactory _inner =
        new(options ?? throw new ArgumentNullException(nameof(options)));

    public IServiceCollection CreateBuilder(IServiceCollection services) =>
        services ?? throw new ArgumentNullException(nameof(services));

    public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder)
    {
        ArgumentNullException.ThrowIfNull(containerBuilder);
        containerBuilder.ValidateLocalNodeFinalServiceGraph(_profile);
        var provider = _inner.CreateServiceProvider(containerBuilder)
            .ValidateResolvedReadersExposeNoRawMutationFace(containerBuilder);
        EnsureShippingAuditIdentity(provider);
        return provider.ValidateRoleGateAdmissionComposition();
    }

    /// <summary>
    /// Closes the audit identity invariant on the provider produced from Program's final graph. The
    /// shipping reader is deliberately concrete here: a replacement reader or a second backing
    /// trail is a composition failure, not a supported variation of the local-node host.
    /// </summary>
    internal static void EnsureShippingAuditIdentity(IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var trail = provider.GetRequiredService<IAuditTrail>();
        var authorized = provider.GetRequiredService<IAuthorizedAuditTrail>();
        var reader = provider.GetRequiredService<IAuditEventReader>();
        if (reader is not InMemoryAuditEventReader concreteReader)
            throw new InvalidOperationException(
                $"The shipping audit reader must be {nameof(InMemoryAuditEventReader)}, not {reader.GetType().FullName}.");

        var backing = typeof(InMemoryAuditEventReader)
            .GetField("_trail", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.GetValue(concreteReader);
        if (!ReferenceEquals(trail, authorized) || !ReferenceEquals(trail, backing))
            throw new InvalidOperationException(
                "The shipping IAuditTrail, IAuthorizedAuditTrail, and reader backing must be the same object.");
    }
}
