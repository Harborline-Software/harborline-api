using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using NSubstitute;

using Harborline.Api.Blocks.Banking.Services;
using Harborline.Api.Foundation.Assets;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Assets.Hierarchy;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Banking;
using Harborline.Api.LocalNodeHost.Data.Packs;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

public sealed class LocalNodeHostedComponentCatalogTests
{
    public static TheoryData<Type> RawMutationPorts() => new()
    {
        typeof(IEntityMutationStore),
        typeof(IBankAccountMutationRepository),
        typeof(IPackInstallMutationStore),
        typeof(IPackProjectionAdmissionStore),
        typeof(IHierarchyMutationStore),
        typeof(IHierarchyCompositeUnitOfWork),
    };

    public static TheoryData<Type, Type> RawReaderImplementations() => new()
    {
        { typeof(IEntityStore), typeof(InMemoryEntityStore) },
        { typeof(IHierarchyService), typeof(InMemoryHierarchyService) },
        { typeof(IBankAccountRepository), typeof(NodeEfBankAccountRepository) },
        { typeof(IPackInstallStore), typeof(DurablePackInstallStore) },
    };

    public static TheoryData<Type> RawConcreteImplementations() => new()
    {
        typeof(InMemoryEntityStore),
        typeof(InMemoryHierarchyService),
        typeof(NodeEfBankAccountRepository),
        typeof(DurablePackInstallStore),
    };

    [Theory]
    [MemberData(nameof(RawMutationPorts))]
    public void FinalGraphRefusesEveryPlantedRawMutationPort(Type rawPort)
    {
        IServiceCollection services = new ServiceCollection();
        services.Add(ServiceDescriptor.Singleton(rawPort, _ => throw new InvalidOperationException("not resolved")));

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.ValidateRawMutationPortsAreUnregistered());

        Assert.StartsWith("authorization.raw_mutation_port_registered:", error.Message, StringComparison.Ordinal);
        Assert.Contains(rawPort.FullName!, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RawReaderImplementations))]
    public void FinalGraphRefusesReaderRegistrationsImplementedByRawStores(Type reader, Type implementation)
    {
        IServiceCollection services = new ServiceCollection();
        services.Add(ServiceDescriptor.Singleton(reader, implementation));

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.ValidateRawMutationPortsAreUnregistered());

        Assert.StartsWith("authorization.raw_mutation_port_registered:", error.Message, StringComparison.Ordinal);
        Assert.Contains(implementation.FullName!, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RawConcreteImplementations))]
    public void FinalGraphRefusesEveryPlantedConcreteRawImplementation(Type implementation)
    {
        IServiceCollection services = new ServiceCollection();
        services.Add(ServiceDescriptor.Singleton(
            implementation,
            _ => throw new InvalidOperationException("must be rejected without resolution")));

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.ValidateRawMutationPortsAreUnregistered());

        Assert.StartsWith("authorization.raw_mutation_port_registered:", error.Message, StringComparison.Ordinal);
        Assert.Contains(implementation.FullName!, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalProviderRefusesReaderFactoriesThatReturnRawStores()
    {
        IServiceCollection services = new ServiceCollection();
        var storage = new InMemoryAssetStorage();
        var nodeFactory = Substitute.For<IDbContextFactory<Data.LocalNodeDbContext>>();
        var packsFactory = Substitute.For<IDbContextFactory<NodeLocalPacksDbContext>>();
        services.AddSingleton<IEntityStore>(_ => new InMemoryEntityStore(storage, TimeProvider.System));
        services.AddSingleton<IHierarchyService>(_ => new InMemoryHierarchyService(storage));
        services.AddSingleton<IBankAccountRepository>(_ => new NodeEfBankAccountRepository(nodeFactory));
        services.AddSingleton<IPackInstallStore>(_ => new DurablePackInstallStore(packsFactory));

        services.ValidateRawMutationPortsAreUnregistered();
        using var provider = services.BuildServiceProvider();
        var error = Assert.Throws<InvalidOperationException>(() =>
            provider.ValidateResolvedReadersExposeNoRawMutationFace(services));

        Assert.StartsWith("authorization.raw_mutation_port_registered:", error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InMemoryEntityStore), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InMemoryHierarchyService), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NodeEfBankAccountRepository), error.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DurablePackInstallStore), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalProviderRefusesOpaqueFactoryResultAssignableToRawFace()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<object>(_ => new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System));
        services.ValidateRawMutationPortsAreUnregistered();
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() =>
            provider.ValidateResolvedReadersExposeNoRawMutationFace(services));

        Assert.Contains(nameof(InMemoryEntityStore), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealHostProviderResolvesOnlyStructurallyReadOnlyReaderFaces()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"s225-reader-composition-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<CompositionProbeCompleteException>(() =>
                global::LocalNodeHostComposition.RunAsync(
                    ["--LocalNode:RootSeedHex=" + new string('2', 64)],
                    sessionTokenOverride: "s225-reader-probe",
                    dataDirectory: dataDirectory,
                    finalServiceProviderProbe: (services, factory) =>
                    {
                        var resolved = factory.CreateServiceProvider(factory.CreateBuilder(services));
                        using var providerLifetime = Assert.IsAssignableFrom<IDisposable>(resolved);
                        object[] readers =
                        [
                            resolved.GetRequiredService<IEntityStore>(),
                            resolved.GetRequiredService<IHierarchyService>(),
                            resolved.GetRequiredService<IBankAccountRepository>(),
                            resolved.GetRequiredService<IPackInstallStore>(),
                        ];
                        Type[] rawFaces =
                        [
                            typeof(IEntityMutationStore),
                            typeof(IBankAccountMutationRepository),
                            typeof(IPackInstallMutationStore),
                            typeof(IPackProjectionAdmissionStore),
                            typeof(IHierarchyMutationStore),
                            typeof(IHierarchyCompositeUnitOfWork),
                        ];
                        Type[] rawImplementations =
                        [
                            typeof(InMemoryEntityStore),
                            typeof(InMemoryHierarchyService),
                            typeof(NodeEfBankAccountRepository),
                            typeof(DurablePackInstallStore),
                        ];
                        Assert.All(readers, reader =>
                            Assert.DoesNotContain(rawFaces, raw => raw.IsInstanceOfType(reader)));
                        Assert.All(rawFaces.Concat(rawImplementations), type =>
                            Assert.Null(resolved.GetService(type)));
                        Assert.All(services, descriptor =>
                        {
                            Type?[] declaredTypes =
                            [
                                descriptor.ServiceType,
                                descriptor.ImplementationType,
                                descriptor.ImplementationInstance?.GetType(),
                                descriptor.ImplementationFactory?.Method.ReturnType,
                            ];
                            Assert.DoesNotContain(declaredTypes, declared => declared is not null
                                && rawFaces.Any(raw => raw.IsAssignableFrom(declared)));
                        });
                        throw new CompositionProbeCompleteException();
                    },
                    installFootprintRootOverride: dataDirectory));
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Fact]
    public void AssetCompositionResolvesReadersButNotTheirRawMutationFaces()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddHarborlineAssetsInMemory();
        services.ValidateRawMutationPortsAreUnregistered();
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IEntityStore>());
        Assert.NotNull(provider.GetRequiredService<IHierarchyService>());
        Assert.Null(provider.GetService<IEntityMutationStore>());
        Assert.Null(provider.GetService<IHierarchyMutationStore>());
        Assert.Null(provider.GetService<IHierarchyCompositeUnitOfWork>());
        Assert.Null(provider.GetService<InMemoryEntityStore>());
        Assert.Null(provider.GetService<InMemoryHierarchyService>());
    }

    [Fact]
    public void Catalog_Separates_Operational_Actors_From_Endpoint_Registrars()
    {
        // 31 since ticket 176 slice 2 added the platform-pack preload (order 153);
        // 30 since ticket 213 slice 2 added the consent expiry sweep (order 88), the scheduled caller
        // that keeps a stored consent record state honest once its effective window closes, and
        // ticket 208 slice 1 added the Access administration package preload (order 155);
        // 28 since ticket 204 slice 3 added the authorization definition seeder (ADR 0070); 27 since ADR 0066
        // step 1 added the node run lock (order 65) — the liveness evidence the offline
        // administrator-recovery command checks before it may establish authority.
        Assert.Equal(31, LocalNodeHostedComponentCatalog.Operational.Count);
        Assert.Equal(0, LocalNodeHostedComponentCatalog.PinnedEndpointRegistrarCount);
        Assert.Empty(LocalNodeHostedComponentCatalog.EndpointRegistrars);
        Assert.Equal(
            LocalNodeHostedComponentCatalog.Operational.Count +
                LocalNodeHostedComponentCatalog.EndpointRegistrars.Count,
            LocalNodeHostedComponentCatalog.Operational.Select(item => item.ComponentType)
                .Concat(LocalNodeHostedComponentCatalog.EndpointRegistrars.Select(item => item.RegistrarType))
                .Distinct()
                .Count());

        Assert.Equal(
            LocalNodeHostedComponentCatalog.Operational.Select(item => item.OperationalOrder).Order(),
            LocalNodeHostedComponentCatalog.Operational.Select(item => item.OperationalOrder));
        Assert.Equal(
            LocalNodeHostedComponentCatalog.Operational.Count,
            LocalNodeHostedComponentCatalog.Operational.Select(item => item.ComponentKey)
                .Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            LocalNodeHostedComponentKind.Indexer,
            LocalNodeHostedComponentCatalog.Operational[^1].Kind);

        Assert.Single(LocalNodeHostedComponentCatalog.Operational, item =>
            item.ComponentKey == "local-node.worker.sync");
    }

    [Fact]
    public void Final_Graph_Validation_Accepts_Exact_Base_Web_Scheduling_And_Maximum_Profiles()
    {
        var baseProfile = new LocalNodeHostedComponentProfile(false, false, false);
        var webProfile = new LocalNodeHostedComponentProfile(true, false, false);
        var schedulingProfile = new LocalNodeHostedComponentProfile(false, false, true);
        var maximumProfile = new LocalNodeHostedComponentProfile(true, true, true);

        // The effective-permissions registrar is part of every profile. The web-only deltas are the
        // two password-hashing startup guards, founder bootstrap, and remaining session registrar.
        Assert.Equal(ExpectedHostedServiceCount(baseProfile), ValidateAndCount(baseProfile));
        Assert.Equal(ExpectedHostedServiceCount(webProfile), ValidateAndCount(webProfile));
        Assert.Equal(ExpectedHostedServiceCount(schedulingProfile), ValidateAndCount(schedulingProfile));
        Assert.Equal(ExpectedHostedServiceCount(maximumProfile), ValidateAndCount(maximumProfile));
    }

    [Fact]
    public void Final_Graph_Validation_Refuses_Drift_And_Opaque_Factories()
    {
        var profile = new LocalNodeHostedComponentProfile(true, true, true);

        var missing = CompleteServices(profile);
        missing.Remove(missing.First(item =>
            item.ImplementationType == typeof(Data.Workflow.WorkflowScheduleDaemon)));
        Assert.StartsWith(
            "local-node.hosted-component.operational_coverage:",
            Assert.Throws<InvalidOperationException>(() =>
                missing.ValidateLocalNodeHostedComponents(profile)).Message);

        var duplicate = CompleteServices(profile);
        AddDescriptor(duplicate, duplicate.First(item =>
            item.ImplementationType == typeof(Data.Workflow.WorkflowScheduleDaemon)));
        Assert.StartsWith(
            "local-node.hosted-component.registration_duplicate:",
            Assert.Throws<InvalidOperationException>(() =>
                duplicate.ValidateLocalNodeHostedComponents(profile)).Message);

        var unexpected = CompleteServices(profile);
        AddDescriptor(
            unexpected,
            ServiceDescriptor.Singleton<IHostedService, UnexpectedHostedService>());
        Assert.StartsWith(
            "local-node.hosted-component.registration_unexpected:",
            Assert.Throws<InvalidOperationException>(() =>
                unexpected.ValidateLocalNodeHostedComponents(profile)).Message);

        var opaque = CompleteServices(profile);
        AddDescriptor(
            opaque,
            ServiceDescriptor.Singleton<IHostedService>(_ => new UnexpectedHostedService()));
        Assert.StartsWith(
            "local-node.hosted-component.registration_opaque:",
            Assert.Throws<InvalidOperationException>(() =>
                opaque.ValidateLocalNodeHostedComponents(profile)).Message);

        var wrongLifetime = CompleteServices(profile);
        var worker = wrongLifetime.First(item =>
            item.ImplementationType == typeof(Data.Workflow.WorkflowScheduleDaemon));
        var workerIndex = wrongLifetime.IndexOf(worker);
        wrongLifetime[workerIndex] = ServiceDescriptor.Transient(
            typeof(IHostedService), typeof(Data.Workflow.WorkflowScheduleDaemon));
        Assert.StartsWith(
            "local-node.hosted-component.registration_lifetime:",
            Assert.Throws<InvalidOperationException>(() =>
                wrongLifetime.ValidateLocalNodeHostedComponents(profile)).Message);

        var wrongOrder = CompleteServices(profile);
        var contactsIndex = IndexOfType<ContactSyncBootstrapHostedService>(wrongOrder);
        var commsIndex = IndexOfType<CommsSyncBootstrapHostedService>(wrongOrder);
        (wrongOrder[contactsIndex], wrongOrder[commsIndex]) =
            (wrongOrder[commsIndex], wrongOrder[contactsIndex]);
        Assert.StartsWith(
            "local-node.hosted-component.operational_coverage:",
            Assert.Throws<InvalidOperationException>(() =>
                wrongOrder.ValidateLocalNodeHostedComponents(profile)).Message);

    }

    [Fact]
    public void Typed_Factory_Registration_Exposes_Its_Implementation_Type()
    {
        var services = new ServiceCollection();
        services.AddHostedService<UnexpectedHostedService>(_ => new UnexpectedHostedService());

        Assert.StartsWith(
            "local-node.hosted-component.registration_unexpected:",
            Assert.Throws<InvalidOperationException>(() =>
                services.ValidateLocalNodeHostedComponents(
                    new LocalNodeHostedComponentProfile(false, false, false))).Message);
    }

    [Fact]
    public void Program_Closes_Hosted_Catalog_Once_Immediately_Before_Build()
    {
        var program = File.ReadAllText(Path.Combine(LocateHostSourceRoot(), "Program.cs"));
        const string boundary = "builder.Host.UseServiceProviderFactory(finalServiceProviderFactory);";
        const string build = "var app = builder.Build();";
        var boundaryIndex = program.IndexOf(boundary, StringComparison.Ordinal);
        var buildIndex = program.IndexOf(build, StringComparison.Ordinal);

        Assert.True(boundaryIndex >= 0);
        Assert.True(buildIndex > boundaryIndex);
        Assert.Equal(boundaryIndex, program.LastIndexOf(boundary, StringComparison.Ordinal));
        Assert.Contains("ServicesStartConcurrently = false", program, StringComparison.Ordinal);
        Assert.Contains("WebApplication.CreateBuilder(args)", program, StringComparison.Ordinal);
        Assert.Contains("LocalNodeEndpointMapping.MapAsync(", program, StringComparison.Ordinal);
        var mapping = File.ReadAllText(Path.Combine(LocateHostSourceRoot(), "Health", "LocalNodeEndpointMapping.cs"));
        Assert.Contains("Add<HostedAuthorizationAdminApiEndpoint>", mapping, StringComparison.Ordinal);
        Assert.DoesNotContain("AddHostedService<SharedHostedWebApp>", program, StringComparison.Ordinal);

        var catalog = File.ReadAllText(Path.Combine(
            LocateHostSourceRoot(),
            "Capabilities",
            "LocalNodeTechnicalRegistrationEvidence.cs"));
        Assert.Contains("services.ValidateLocalNodeHostedComponents(profile);", catalog, StringComparison.Ordinal);
    }

    private static int ValidateAndCount(LocalNodeHostedComponentProfile profile)
    {
        var services = CompleteServices(profile);
        services.ValidateLocalNodeHostedComponents(profile);
        return services.Count(item => item.ServiceType == typeof(IHostedService));
    }

    private static int ExpectedHostedServiceCount(LocalNodeHostedComponentProfile profile) =>
        LocalNodeHostedComponentCatalog.SelectOperational(profile).Length;

    private static ServiceCollection CompleteServices(LocalNodeHostedComponentProfile profile)
    {
        var services = new ServiceCollection();

        foreach (var component in LocalNodeHostedComponentCatalog.Operational
            .Where(item => IsActive(item, profile)))
        {
            AddDescriptor(
                services,
                ServiceDescriptor.Singleton(typeof(IHostedService), component.ComponentType));
        }

        return services;
    }

    private static bool IsActive(
        LocalNodeHostedComponentDescriptor descriptor,
        LocalNodeHostedComponentProfile profile) =>
        descriptor.Activation == LocalNodeHostedActivation.Always || profile.WebClientEnabled;

    private static void AddDescriptor(ServiceCollection services, ServiceDescriptor descriptor) =>
        ((ICollection<ServiceDescriptor>)services).Add(descriptor);

    private static int IndexOfType<TImplementation>(ServiceCollection services) =>
        services.Select((item, index) => new { item, index })
            .Single(entry => entry.item.ImplementationType == typeof(TImplementation))
            .index;

    private static string LocateHostSourceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Harborline.LocalNodeHost.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the local-node host source root.");
    }

    private sealed class UnexpectedHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CompositionProbeCompleteException : Exception;
}
