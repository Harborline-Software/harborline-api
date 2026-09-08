using System.Collections.Immutable;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Banking;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Packs.Install;

namespace Harborline.Api.LocalNodeHost.Capabilities;

/// <summary>
/// Structural role of one non-route hosted component in the local-node process.
/// </summary>
internal enum LocalNodeHostedComponentKind
{
    StartupGuard,
    Migrator,
    Bootstrap,
    Provisioner,
    Worker,
    JobRunner,
    Projector,
    Seeder,
    Indexer,
    Listener,
}

internal enum LocalNodeHostedActivation
{
    Always,
    WebClient,
}

internal enum LocalNodeEndpointActivation
{
    Always,
    WebClient,
    WebClientWithLlm,
    SchedulingDogfood,
}

/// <summary>
/// Exact composition switches which select hosted registrations. These switches describe the
/// service graph; they do not imply that any selected component is healthy or tenant-safe.
/// </summary>
internal sealed record LocalNodeHostedComponentProfile(
    bool WebClientEnabled,
    bool LlmProxyEnabled,
    bool SchedulingDogfoodEnabled);

/// <summary>Neutral compiled registration metadata for one operational hosted actor.</summary>
internal sealed record LocalNodeHostedComponentDescriptor(
    string ComponentKey,
    int OperationalOrder,
    Type ComponentType,
    LocalNodeHostedComponentKind Kind,
    LocalNodeHostedActivation Activation);

internal sealed record LocalNodeEndpointRegistrarDescriptor(
    Type RegistrarType,
    LocalNodeEndpointActivation Activation);

/// <summary>
/// Code-owned inventory of hosted actors and endpoint registrars compiled into the local-node host.
/// Operational actors are kept distinct from the thin route registrars whose actual route output is
/// measured later by <see cref="LocalNodeExecutableEndpointRegistry"/>. Presence here says only that
/// a reviewed registration exists; it proves neither successful startup nor readiness, durability,
/// tenant isolation, or authorization.
/// </summary>
internal static class LocalNodeHostedComponentCatalog
{
    // This is the one deliberate registrar-total pin. Profile and aggregate counts derive from the
    // catalog so adding or removing an endpoint requires updating one reviewed value, not three test
    // matrices discovered piecemeal through red runs.
    internal const int PinnedEndpointRegistrarCount = 0;

    private static readonly ImmutableArray<LocalNodeHostedComponentDescriptor> s_operational =
        CreateOperationalCatalog();
    private static readonly ImmutableArray<LocalNodeEndpointRegistrarDescriptor> s_endpoints =
        CreateEndpointCatalog();
    private static readonly ImmutableArray<LocalNodeHostedComponentProfile> s_supportedEndpointProfiles =
    [
        new(WebClientEnabled: false, LlmProxyEnabled: false, SchedulingDogfoodEnabled: false),
        new(WebClientEnabled: true, LlmProxyEnabled: false, SchedulingDogfoodEnabled: false),
        new(WebClientEnabled: true, LlmProxyEnabled: true, SchedulingDogfoodEnabled: false),
        new(WebClientEnabled: false, LlmProxyEnabled: false, SchedulingDogfoodEnabled: true),
        new(WebClientEnabled: true, LlmProxyEnabled: false, SchedulingDogfoodEnabled: true),
        new(WebClientEnabled: true, LlmProxyEnabled: true, SchedulingDogfoodEnabled: true),
    ];

    internal static IReadOnlyList<LocalNodeHostedComponentDescriptor> Operational => s_operational;

    internal static IReadOnlyList<LocalNodeEndpointRegistrarDescriptor> EndpointRegistrars => s_endpoints;

    /// <summary>
    /// Every meaningful executable-endpoint graph shape. LLM routes require the web client, so the
    /// two profiles that assert LLM while disabling its owning web surface are not supported shapes.
    /// Scheduling is independent and therefore crosses all three web/LLM shapes.
    /// </summary>
    internal static IReadOnlyList<LocalNodeHostedComponentProfile> SupportedEndpointProfiles =>
        s_supportedEndpointProfiles;

    internal static ImmutableArray<LocalNodeHostedComponentDescriptor> SelectOperational(
        LocalNodeHostedComponentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return s_operational
            .Where(item => IsActive(item.Activation, profile))
            .OrderBy(item => item.OperationalOrder)
            .ToImmutableArray();
    }

    internal static ImmutableArray<LocalNodeEndpointRegistrarDescriptor> SelectEndpointRegistrars(
        LocalNodeHostedComponentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return s_endpoints
            .Where(item => IsActive(item.Activation, profile))
            .ToImmutableArray();
    }

    /// <summary>
    /// Closes every code-owned service-graph inventory without mutating the collection. This single
    /// call must remain the final service-collection operation before the provider is built.
    /// </summary>
    internal static void ValidateLocalNodeFinalServiceGraph(
        this IServiceCollection services,
        LocalNodeHostedComponentProfile profile)
    {
        services.ValidateLocalNodeTechnicalRegistrationEvidence(profile);
        services.ValidateRoleGateAdmissionRegistration();
        services.ValidateRawMutationPortsAreUnregistered();
    }

    internal static void ValidateRawMutationPortsAreUnregistered(this IServiceCollection services)
    {
        var exposed = services
            .Where(DescriptorExposesRawMutationFace)
            .Select(DescribeDescriptor)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (exposed.Length != 0)
            throw new InvalidOperationException(
                "authorization.raw_mutation_port_registered: " + string.Join(", ", exposed));
    }

    internal static IServiceProvider ValidateResolvedReadersExposeNoRawMutationFace(
        this IServiceProvider provider,
        IEnumerable<ServiceDescriptor>? descriptors = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        var candidateServiceTypes = ReaderPorts
            .Concat((descriptors ?? [])
                .Where(descriptor => descriptor.ImplementationFactory is not null)
                .Select(descriptor => descriptor.ServiceType)
                .Where(service => !service.ContainsGenericParameters
                    && (RawMutationPorts.Any(raw => service.IsAssignableFrom(raw))
                        || RawMutationImplementations.Any(raw => service.IsAssignableFrom(raw)))))
            .Distinct()
            .ToArray();
        var exposed = candidateServiceTypes
            .SelectMany(service => provider.GetServices(service).Select(instance => (Service: service, Instance: instance)))
            .Where(item => RawMutationPorts.Any(port => port.IsInstanceOfType(item.Instance)))
            .Select(item => $"{item.Service.FullName}->{item.Instance!.GetType().FullName}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (exposed.Length != 0)
            throw new InvalidOperationException(
                "authorization.raw_mutation_port_registered: " + string.Join(", ", exposed));
        return provider;
    }

    private static readonly Type[] RawMutationPorts =
    [
        typeof(IEntityMutationStore),
        typeof(IBankAccountMutationRepository),
        typeof(IPackInstallMutationStore),
        typeof(IPackProjectionAdmissionStore),
        typeof(IHierarchyMutationStore),
        typeof(IHierarchyCompositeUnitOfWork),
    ];

    private static readonly Type[] ReaderPorts =
    [
        typeof(IEntityStore),
        typeof(IHierarchyService),
        typeof(IBankAccountRepository),
        typeof(IPackInstallStore),
    ];

    private static readonly Type[] RawMutationImplementations =
    [
        typeof(InMemoryEntityStore),
        typeof(InMemoryHierarchyService),
        typeof(NodeEfBankAccountRepository),
        typeof(DurablePackInstallStore),
    ];

    private static Type? DescriptorImplementationType(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType
        ?? descriptor.ImplementationInstance?.GetType()
        ?? descriptor.ImplementationFactory?.Method.ReturnType;

    private static bool DescriptorExposesRawMutationFace(ServiceDescriptor descriptor)
    {
        if (RawMutationPorts.Any(port => port.IsAssignableFrom(descriptor.ServiceType)))
            return true;

        var implementation = DescriptorImplementationType(descriptor);
        return implementation is not null
            && RawMutationPorts.Any(port => port.IsAssignableFrom(implementation));
    }

    private static string DescribeDescriptor(ServiceDescriptor descriptor)
    {
        var implementation = DescriptorImplementationType(descriptor);
        return implementation is null || implementation == descriptor.ServiceType
            ? descriptor.ServiceType.FullName ?? descriptor.ServiceType.Name
            : $"{descriptor.ServiceType.FullName}->{implementation.FullName}";
    }

    /// <summary>
    /// Validates the final service graph before a provider is built. Missing, duplicate, unexpected,
    /// opaque, non-singleton, or profile-incompatible hosted registrations and misordered operational
    /// actors refuse host construction. Route mapping and the framework-owned web-host service are
    /// composed after this code-owned hosted-service inventory is closed.
    /// </summary>
    internal static void ValidateLocalNodeHostedComponents(
        this IServiceCollection services,
        LocalNodeHostedComponentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(profile);

        var observed = services
            .Select(descriptor => new
            {
                Descriptor = descriptor,
                ImplementationType = ResolveImplementationType(descriptor),
            })
            .Where(item => item.Descriptor.ServiceType == typeof(IHostedService))
            .Where(item => item.ImplementationType?.FullName !=
                "Microsoft.AspNetCore.Hosting.GenericWebHostService")
            .ToArray();

        var opaque = observed.Where(item => item.ImplementationType is null).ToArray();
        if (opaque.Length != 0)
        {
            throw new InvalidOperationException(
                "local-node.hosted-component.registration_opaque: every hosted registration must expose its " +
                "compiled implementation type.");
        }

        if (observed.Any(item => item.Descriptor.Lifetime != ServiceLifetime.Singleton))
        {
            throw new InvalidOperationException(
                "local-node.hosted-component.registration_lifetime: hosted registrations must be singleton.");
        }

        var observedTypes = observed.Select(item => item.ImplementationType!).ToArray();
        if (observedTypes.Distinct().Count() != observedTypes.Length)
        {
            throw new InvalidOperationException(
                "local-node.hosted-component.registration_duplicate: each hosted implementation must be " +
                "registered exactly once.");
        }

        var catalogTypes = s_operational.Select(item => item.ComponentType)
            .Concat(s_endpoints.Select(item => item.RegistrarType))
            .ToHashSet();
        var unexpected = observedTypes.Where(type => !catalogTypes.Contains(type)).ToArray();
        if (unexpected.Length != 0)
        {
            throw new InvalidOperationException(
                $"local-node.hosted-component.registration_unexpected: '{unexpected[0].FullName}'.");
        }

        var expectedOperational = SelectOperational(profile)
            .Select(item => item.ComponentType)
            .ToArray();
        var observedOperational = observedTypes
            .Where(type => s_operational.Any(item => item.ComponentType == type))
            .ToArray();
        if (!observedOperational.SequenceEqual(expectedOperational))
        {
            throw new InvalidOperationException(
                "local-node.hosted-component.operational_coverage: selected operational actors are missing, " +
                "extra, or misordered. expected=[" +
                string.Join(",", expectedOperational.Select(type => type.Name)) + "] observed=[" +
                string.Join(",", observedOperational.Select(type => type.Name)) + "].");
        }

        var expectedEndpoints = SelectEndpointRegistrars(profile)
            .Select(item => item.RegistrarType)
            .ToHashSet();
        var observedEndpoints = observedTypes
            .Where(type => s_endpoints.Any(item => item.RegistrarType == type))
            .ToHashSet();
        if (!observedEndpoints.SetEquals(expectedEndpoints))
        {
            throw new InvalidOperationException(
                "local-node.hosted-component.endpoint_coverage: selected endpoint registrars do not match the " +
                "compiled profile.");
        }

    }

    internal static Type? ResolveImplementationType(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is not null)
        {
            return descriptor.ImplementationType;
        }

        if (descriptor.ImplementationInstance is not null)
        {
            return descriptor.ImplementationInstance.GetType();
        }

        var returnType = descriptor.ImplementationFactory?.Method.ReturnType;
        return returnType is not null &&
            returnType != typeof(IHostedService) &&
            typeof(IHostedService).IsAssignableFrom(returnType)
                ? returnType
                : null;
    }

    private static bool IsActive(
        LocalNodeHostedActivation activation,
        LocalNodeHostedComponentProfile profile) =>
        activation switch
        {
            LocalNodeHostedActivation.Always => true,
            LocalNodeHostedActivation.WebClient => profile.WebClientEnabled,
            _ => throw new ArgumentOutOfRangeException(nameof(activation)),
        };

    private static bool IsActive(
        LocalNodeEndpointActivation activation,
        LocalNodeHostedComponentProfile profile) =>
        activation switch
        {
            LocalNodeEndpointActivation.Always => true,
            LocalNodeEndpointActivation.WebClient => profile.WebClientEnabled,
            LocalNodeEndpointActivation.WebClientWithLlm =>
                profile.WebClientEnabled && profile.LlmProxyEnabled,
            LocalNodeEndpointActivation.SchedulingDogfood => profile.SchedulingDogfoodEnabled,
            _ => throw new ArgumentOutOfRangeException(nameof(activation)),
        };

    private static ImmutableArray<LocalNodeHostedComponentDescriptor> CreateOperationalCatalog()
    {
        var catalog = ImmutableArray.Create(
            Describe<Harborline.Api.Foundation.PasswordHashing.DependencyInjection.MockPasswordHasherProductionGuardAssertion>(
                "local-node.guard.password-mock", 10, LocalNodeHostedComponentKind.StartupGuard,
                LocalNodeHostedActivation.WebClient),
            Describe<Harborline.Api.Foundation.PasswordHashing.DependencyInjection.Argon2idParameterFloorAssertion>(
                "local-node.guard.argon2id-floor", 20, LocalNodeHostedComponentKind.StartupGuard,
                LocalNodeHostedActivation.WebClient),
            Describe<Harborline.Api.Foundation.Forms.Submission.InMemoryFormSubmitOutboxGuardAssertion>(
                "local-node.guard.form-submit-outbox", 25, LocalNodeHostedComponentKind.StartupGuard,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.Data.LocalNodeStoreEncryptionGuard>(
                "local-node.guard.sqlcipher-migrations", 30, LocalNodeHostedComponentKind.StartupGuard,
                LocalNodeHostedActivation.Always),
            // Ordered immediately after the SQLCipher guard because that guard's StartAsync is what
            // applies the installation-identity schema this ceremony writes into (ADR 0160 R3-E).
            Describe<Harborline.Api.LocalNodeHost.Data.Identity.InstallationFounderBootstrapCeremonyHostedService>(
                "local-node.ceremony.installation-founder-bootstrap", 32,
                LocalNodeHostedComponentKind.Bootstrap,
                LocalNodeHostedActivation.WebClient),
            Describe(
                FrameworkHealthPublisherType(),
                "local-node.worker.health-publisher", 35, LocalNodeHostedComponentKind.Worker,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.ContactSyncBootstrapHostedService>(
                "local-node.bootstrap.contacts", 40, LocalNodeHostedComponentKind.Bootstrap,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.CommsSyncBootstrapHostedService>(
                "local-node.bootstrap.comms", 50, LocalNodeHostedComponentKind.Bootstrap,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.RosterSyncBootstrapHostedService>(
                "local-node.bootstrap.roster", 60, LocalNodeHostedComponentKind.Bootstrap,
                LocalNodeHostedActivation.Always),
            // Ordered just before the team bootstrap: the run lock is the liveness evidence the OFFLINE
            // administrator-recovery command checks (ADR 0066 clause 8, "with the node stopped"), so it must
            // be held before the bootstrap can establish or project administrative authority.
            Describe<Harborline.Api.LocalNodeHost.Data.Identity.NodeRunLock>(
                "local-node.guard.run-lock", 65, LocalNodeHostedComponentKind.StartupGuard,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.MultiTeamBootstrapHostedService>(
                "local-node.bootstrap.teams", 70, LocalNodeHostedComponentKind.Bootstrap,
                LocalNodeHostedActivation.Always),
            // Authorization definitions must be seeded after tenant materialization and before founder
            // grant issuance, so the first founder Administrator grant cannot race an unseeded catalogue.
            Describe<Harborline.Api.LocalNodeHost.Data.Authorization.AuthorizationSeedHostedService>(
                "local-node.seeder.authorization-definitions", 72, LocalNodeHostedComponentKind.Seeder,
                LocalNodeHostedActivation.Always),
            // Ordered AFTER the team bootstrap (70) because it attaches the founder to the genesis
            // tenant that bootstrap materializes, and before the sync worker (80). ADR 0160 R3-E.
            Describe<Harborline.Api.LocalNodeHost.Data.Identity.FounderTenantMembershipAttachHostedService>(
                "local-node.bootstrap.founder-membership", 75, LocalNodeHostedComponentKind.Bootstrap,
                LocalNodeHostedActivation.WebClient),
            Describe<Harborline.Api.LocalNodeHost.LocalNodeWorker>(
                "local-node.worker.sync", 80, LocalNodeHostedComponentKind.Worker,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.Data.Identity.InstallationIdentityCoordinatorRecoveryDaemon>(
                "local-node.job.identity-coordinator-recovery", 85,
                LocalNodeHostedComponentKind.JobRunner,
                LocalNodeHostedActivation.WebClient),
            // Ticket 213 slice 2 -- the consent expiry sweep. Ordered HERE because the governance
            // composition that registers it (AddNodeTenantGovernance) runs between the identity-coordinator
            // recovery daemon (85) and the governance genesis provisioner (90), and the catalog order must
            // match the order Program.cs actually registers in.
            Describe<Harborline.Api.LocalNodeHost.Data.Governance.ConsentExpirySweepDaemon>(
                "local-node.job.consent-expiry-sweep", 88, LocalNodeHostedComponentKind.JobRunner,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.Data.Governance.HostedGovernanceGenesisService>(
                "local-node.provisioner.governance-genesis", 90, LocalNodeHostedComponentKind.Provisioner,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.Data.HomeEpoch.HostedHomeEpochGenesisService>(
                "local-node.provisioner.home-epoch-genesis", 95, LocalNodeHostedComponentKind.Provisioner,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.Data.Workflow.WorkflowScheduleDaemon>(
                "local-node.job.workflow-schedule", 100, LocalNodeHostedComponentKind.JobRunner,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.DefaultCalendarProvisioningService>(
                "local-node.provisioner.default-calendar", 110, LocalNodeHostedComponentKind.Provisioner,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.CalendarDevSeeder>(
                "local-node.seeder.calendar", 120, LocalNodeHostedComponentKind.Seeder,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.Data.Drafts.NodeDraftsMigrator>(
                "local-node.migrator.drafts", 130, LocalNodeHostedComponentKind.Migrator,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.Data.AssetRegistry.FormSubmitProjectionReconcilerDaemon>(
                "local-node.job.form-projection-reconcile", 140, LocalNodeHostedComponentKind.JobRunner,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.Data.PackProjection.PackSeedProjectionHostedService>(
                "local-node.projector.pack-seeds", 150, LocalNodeHostedComponentKind.Projector,
                LocalNodeHostedActivation.Always),
            // Ticket 208 slice 1: the Access administration package preload. Ordered immediately after the
            // seed projector so a restart reconciles the installed packs first and this is then a no-op.
            Describe<Harborline.Api.LocalNodeHost.Data.PackProjection.AccessAdministrationPreloadHostedService>(
                "local-node.seeder.access-administration-pack", 155, LocalNodeHostedComponentKind.Seeder,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.ThreeWayMatchDevSeeder>(
                "local-node.seeder.three-way-match", 160, LocalNodeHostedComponentKind.Seeder,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.FormsDevSeeder>(
                "local-node.seeder.forms", 170, LocalNodeHostedComponentKind.Seeder,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.LivingStandardCatalogDevSeeder>(
                "local-node.seeder.living-standard", 180, LocalNodeHostedComponentKind.Seeder,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.FormsShowcaseDevSeeder>(
                "local-node.seeder.forms-showcase", 190, LocalNodeHostedComponentKind.Seeder,
                LocalNodeHostedActivation.Always),
            Describe<Harborline.Api.LocalNodeHost.KgCalendarDevIndexer>(
                "local-node.indexer.kg-calendar", 200, LocalNodeHostedComponentKind.Indexer,
                LocalNodeHostedActivation.Always));

        ValidateCatalog(catalog);
        return catalog;
    }

    private static ImmutableArray<LocalNodeEndpointRegistrarDescriptor> CreateEndpointCatalog() => [];

    private static LocalNodeHostedComponentDescriptor Describe<TComponent>(
        string componentKey,
        int operationalOrder,
        LocalNodeHostedComponentKind kind,
        LocalNodeHostedActivation activation)
        where TComponent : class, IHostedService =>
        Describe(
            typeof(TComponent),
            componentKey,
            operationalOrder,
            kind,
            activation);

    private static LocalNodeHostedComponentDescriptor Describe(
        Type componentType,
        string componentKey,
        int operationalOrder,
        LocalNodeHostedComponentKind kind,
        LocalNodeHostedActivation activation)
    {
        ArgumentNullException.ThrowIfNull(componentType);
        if (!typeof(IHostedService).IsAssignableFrom(componentType))
        {
            throw new InvalidOperationException(
                $"local-node.hosted-component.type_invalid: '{componentType.FullName}'.");
        }

        return new(
            componentKey,
            operationalOrder,
            componentType,
            kind,
            activation);
    }

    private static Type FrameworkHealthPublisherType() =>
        typeof(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckPublisherOptions).Assembly.GetType(
            "Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckPublisherHostedService",
            throwOnError: true,
            ignoreCase: false)!;

    private static LocalNodeEndpointRegistrarDescriptor Endpoint<TRegistrar>(
        LocalNodeEndpointActivation activation)
        where TRegistrar : class, IHostedService =>
        new(typeof(TRegistrar), activation);

    private static void ValidateCatalog(ImmutableArray<LocalNodeHostedComponentDescriptor> catalog)
    {
        if (catalog.Length != 30 ||
            catalog.Select(item => item.ComponentKey)
                .Distinct(StringComparer.Ordinal).Count() != catalog.Length ||
            catalog.Select(item => item.OperationalOrder).Distinct().Count() != catalog.Length ||
            !catalog.Select(item => item.OperationalOrder)
                .SequenceEqual(catalog.Select(item => item.OperationalOrder).Order()) ||
            catalog.Select(item => item.ComponentType).Distinct().Count() != catalog.Length)
        {
            throw new InvalidOperationException(
                "local-node.hosted-component.catalog_invalid: expected 29 unique ordered actors.");
        }
    }
}
