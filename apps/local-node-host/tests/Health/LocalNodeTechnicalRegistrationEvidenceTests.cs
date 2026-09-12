using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

public sealed class LocalNodeTechnicalRegistrationEvidenceTests
{
    [Fact]
    public void Root_Ef_Projection_Is_Derived_From_The_Two_Code_Owned_Catalogs()
    {
        var stores = LocalNodeTechnicalRegistrationEvidenceCatalog.RootEfStores;
        var expectedStoreCount = 1 + LocalNodeExclusiveEfContextCatalog.All.Count;

        Assert.Equal(expectedStoreCount, stores.Count);
        Assert.Equal(
            expectedStoreCount,
            stores.Select(item => item.StoreKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expectedStoreCount, stores.Select(item => item.ContextType).Distinct().Count());
        Assert.All(stores, item =>
        {
            Assert.Equal(ServiceLifetime.Singleton, item.ExpectedLifetime);
            Assert.Equal(LocalNodeRegistrationScope.Root, item.RegistrationScope);
            Assert.Equal(LocalNodeTechnicalActivation.Always, item.Activation);
            Assert.Equal(
                typeof(IDbContextFactory<>).MakeGenericType(item.ContextType),
                item.FactoryServiceType);
        });

        var primary = stores[0];
        Assert.Equal("local-node.ef.primary", primary.StoreKey);
        Assert.Equal(typeof(LocalNodeDbContext), primary.ContextType);
        Assert.Equal(
            LocalNodePatternAModuleCatalog.All.Select(item => item.ModuleKey),
            primary.SchemaContributorKeys);
        Assert.Equal(LocalNodePatternAModuleCatalog.All.Count, primary.SchemaContributorKeys.Length);
        Assert.Equal(
            LocalNodePatternAModuleCatalog.All.Count,
            primary.SchemaContributorKeys.Distinct(StringComparer.Ordinal).Count());

        var exclusive = stores.Skip(1).ToArray();
        Assert.Equal(
            LocalNodeExclusiveEfContextCatalog.All.OrderBy(item => item.ExecutionOrder)
                .Select(item => item.ContextKey),
            exclusive.Select(item => item.StoreKey));
        Assert.Equal(
            LocalNodeExclusiveEfContextCatalog.All.OrderBy(item => item.ExecutionOrder)
                .Select(item => item.ContextType),
            exclusive.Select(item => item.ContextType));
        Assert.All(exclusive, item => Assert.Empty(item.SchemaContributorKeys));
    }

    [Theory]
    // webClient profiles gain the founder tenant-membership attach (order 75) and the identity
    // recovery drain (order 85). Both are WebClient-activated, so non-web profiles remain unchanged.
    // ADR 0066 step 1 adds the node run lock (order 65), which is Always-activated and therefore
    // raises BOTH columns by one: it is the liveness evidence the offline administrator-recovery
    // command checks, and every profile that can establish authority must hold it.
    // Ticket 208 slice 1 adds the Access administration package preload (order 155), also
    // Always-activated - it installs over the root pack store, which every profile carries - so it
    // raises both columns by one again: 24 non-web, 29 webClient.
    // Ticket 213 slice 2 adds the consent expiry sweep (order 88), Always-activated, so both columns
    // rise by one more: 25 non-web, 30 webClient. Ticket 176 slice 2 then adds the platform-pack
    // preload (order 153), so the regenerated evidence is 26 non-web and 31 webClient.
    [InlineData(false, false, false, 26)]
    [InlineData(true, false, false, 31)]
    [InlineData(false, false, true, 26)]
    [InlineData(true, true, true, 31)]
    public void Evidence_Uses_The_Exact_Profile_Selected_Hosted_Projection(
        bool webClient,
        bool llmProxy,
        bool schedulingDogfood,
        int expectedActors)
    {
        var profile = new LocalNodeHostedComponentProfile(webClient, llmProxy, schedulingDogfood);
        var evidence = RegisterAndRead(profile);
        var expectedEndpointRegistrars =
            LocalNodeHostedComponentCatalog.SelectEndpointRegistrars(profile);

        Assert.Equal(expectedActors, evidence.SelectedOperationalActors.Length);
        Assert.Equal(expectedEndpointRegistrars.Length, evidence.SelectedEndpointRegistrars.Length);
        Assert.True(LocalNodeHostedComponentCatalog.SelectOperational(profile)
            .SequenceEqual(evidence.SelectedOperationalActors));
        Assert.True(expectedEndpointRegistrars.SequenceEqual(evidence.SelectedEndpointRegistrars));

        var services = CompleteServices(profile);
        services.AddSingleton(evidence);
        services.ValidateLocalNodeTechnicalRegistrationEvidence(profile);
    }

    [Fact]
    public void Evidence_Validation_Refuses_Missing_Duplicate_Opaque_Lifetime_And_Profile_Drift()
    {
        var baseProfile = new LocalNodeHostedComponentProfile(false, false, false);
        var maximumProfile = new LocalNodeHostedComponentProfile(true, true, true);
        var evidence = RegisterAndRead(baseProfile);

        var missing = CompleteServices(baseProfile);
        Assert.StartsWith(
            "local-node.technical-registration.evidence_missing:",
            Assert.Throws<InvalidOperationException>(() =>
                missing.ValidateLocalNodeTechnicalRegistrationEvidence(baseProfile)).Message);

        var duplicate = CompleteServices(baseProfile);
        duplicate.AddLocalNodeTechnicalRegistrationEvidence(baseProfile);
        duplicate.AddLocalNodeTechnicalRegistrationEvidence(baseProfile);
        Assert.StartsWith(
            "local-node.technical-registration.evidence_duplicate:",
            Assert.Throws<InvalidOperationException>(() =>
                duplicate.ValidateLocalNodeTechnicalRegistrationEvidence(baseProfile)).Message);

        var opaque = CompleteServices(baseProfile);
        AddDescriptor(
            opaque,
            ServiceDescriptor.Singleton<LocalNodeTechnicalRegistrationEvidence>(_ => evidence));
        Assert.StartsWith(
            "local-node.technical-registration.evidence_opaque:",
            Assert.Throws<InvalidOperationException>(() =>
                opaque.ValidateLocalNodeTechnicalRegistrationEvidence(baseProfile)).Message);

        var wrongLifetime = CompleteServices(baseProfile);
        AddDescriptor(
            wrongLifetime,
            ServiceDescriptor.Transient(
                typeof(LocalNodeTechnicalRegistrationEvidence),
                typeof(LocalNodeTechnicalRegistrationEvidence)));
        Assert.StartsWith(
            "local-node.technical-registration.evidence_lifetime:",
            Assert.Throws<InvalidOperationException>(() =>
                wrongLifetime.ValidateLocalNodeTechnicalRegistrationEvidence(baseProfile)).Message);

        var wrongProfile = CompleteServices(maximumProfile);
        wrongProfile.AddSingleton(evidence);
        Assert.StartsWith(
            "local-node.technical-registration.evidence_mismatch:",
            Assert.Throws<InvalidOperationException>(() =>
                wrongProfile.ValidateLocalNodeTechnicalRegistrationEvidence(maximumProfile)).Message);

        var incomplete = CompleteServices(baseProfile);
        incomplete.AddSingleton(evidence with { RootEfStores = evidence.RootEfStores.RemoveAt(0) });
        Assert.StartsWith(
            "local-node.technical-registration.evidence_mismatch:",
            Assert.Throws<InvalidOperationException>(() =>
                incomplete.ValidateLocalNodeTechnicalRegistrationEvidence(baseProfile)).Message);
    }

    [Fact]
    public void Evidence_Cannot_Be_Registered_Without_The_Actual_Validated_Service_Graph()
    {
        var services = new ServiceCollection();

        Assert.StartsWith(
            "local-node.pattern-a.missing:",
            Assert.Throws<InvalidOperationException>(() =>
                services.AddLocalNodeTechnicalRegistrationEvidence(
                    new LocalNodeHostedComponentProfile(false, false, false))).Message);
        Assert.DoesNotContain(services, item =>
            item.ServiceType == typeof(LocalNodeTechnicalRegistrationEvidence));
    }

    [Fact]
    public void Provider_Factory_Rejects_Graph_Mutation_At_The_Actual_Build_Boundary()
    {
        var profile = new LocalNodeHostedComponentProfile(false, false, false);
        IServiceCollection services = CompleteServices(profile);
        services.AddLocalNodeTechnicalRegistrationEvidence(profile);
        var factory = new LocalNodeFinalGraphServiceProviderFactory(
            profile,
            new ServiceProviderOptions());
        var container = factory.CreateBuilder(services);

        container.Remove(container.Single(item =>
            item.ImplementationType == typeof(Data.Workflow.WorkflowScheduleDaemon)));

        Assert.StartsWith(
            "local-node.hosted-component.operational_coverage:",
            Assert.Throws<InvalidOperationException>(() =>
                factory.CreateServiceProvider(container)).Message);
    }

    [Fact]
    public void Technical_Evidence_Has_No_Product_Capability_Or_Runtime_Behavior_Claims()
    {
        var propertyNames = typeof(LocalNodeTechnicalRegistrationEvidence)
            .GetProperties()
            .Concat(typeof(LocalNodeEfStoreRegistrationDescriptor).GetProperties())
            .Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        var forbiddenProperties = new[]
        {
            "Ready",
            "Healthy",
            "TenantId",
            "Authority",
            "Durable",
            "Encrypted",
            "FailurePolicy",
        };
        Assert.DoesNotContain(forbiddenProperties, propertyNames.Contains);

        var source = File.ReadAllText(Path.Combine(
            LocateHostSourceRoot(),
            "Capabilities",
            "LocalNodeTechnicalRegistrationEvidence.cs"));
        Assert.DoesNotContain("RuntimeCapabilityRegistration", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecutableInventory", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ICapabilityReadinessProbe", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CapabilityKey", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ISessionStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("INodeWebSessionAuthority", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_Registers_Evidence_And_Configures_The_Build_Boundary_Factory_Once()
    {
        var program = File.ReadAllText(Path.Combine(LocateHostSourceRoot(), "Program.cs"));
        const string registration =
            "builder.Services.AddLocalNodeTechnicalRegistrationEvidence(technicalRegistrationProfile);";
        const string boundary =
            "builder.Host.UseServiceProviderFactory(finalServiceProviderFactory);";
        const string build = "var app = builder.Build();";

        var registrationIndex = program.IndexOf(registration, StringComparison.Ordinal);
        var boundaryIndex = program.IndexOf(boundary, StringComparison.Ordinal);
        var buildIndex = program.IndexOf(build, StringComparison.Ordinal);
        Assert.True(registrationIndex >= 0);
        Assert.True(boundaryIndex > registrationIndex);
        Assert.True(buildIndex > boundaryIndex);
        Assert.Equal(registrationIndex, program.LastIndexOf(registration, StringComparison.Ordinal));
        Assert.Equal(boundaryIndex, program.LastIndexOf(boundary, StringComparison.Ordinal));

        var source = File.ReadAllText(Path.Combine(
            LocateHostSourceRoot(),
            "Capabilities",
            "LocalNodeTechnicalRegistrationEvidence.cs"));
        Assert.Contains(
            "containerBuilder.ValidateLocalNodeFinalServiceGraph(_profile);",
            source,
            StringComparison.Ordinal);
    }

    private static LocalNodeTechnicalRegistrationEvidence RegisterAndRead(
        LocalNodeHostedComponentProfile profile)
    {
        var services = CompleteServices(profile);
        services.AddLocalNodeTechnicalRegistrationEvidence(profile);
        return Assert.IsType<LocalNodeTechnicalRegistrationEvidence>(
            Assert.Single(services, item =>
                item.ServiceType == typeof(LocalNodeTechnicalRegistrationEvidence)).ImplementationInstance);
    }

    private static ServiceCollection CompleteServices(LocalNodeHostedComponentProfile profile)
    {
        var services = new ServiceCollection();
        services.AddLocalNodePatternAModules();
        AddDescriptor(
            services,
            ServiceDescriptor.Singleton(
                typeof(IDbContextFactory<LocalNodeDbContext>),
                typeof(FakeDbContextFactory<LocalNodeDbContext>)));
        foreach (var context in LocalNodeExclusiveEfContextCatalog.All)
        {
            AddDescriptor(
                services,
                ServiceDescriptor.Singleton(
                    typeof(IDbContextFactory<>).MakeGenericType(context.ContextType),
                    typeof(FakeDbContextFactory<>).MakeGenericType(context.ContextType)));
            AddDescriptor(
                services,
                ServiceDescriptor.Singleton(
                    typeof(ILocalNodeExclusiveContextMigrator),
                    typeof(LocalNodeExclusiveContextMigrator<>).MakeGenericType(context.ContextType)));
        }

        var selectedOperational = LocalNodeHostedComponentCatalog.SelectOperational(profile);
        foreach (var component in selectedOperational)
        {
            AddDescriptor(
                services,
                ServiceDescriptor.Singleton(
                    typeof(Microsoft.Extensions.Hosting.IHostedService),
                    component.ComponentType));
        }

        foreach (var endpoint in LocalNodeHostedComponentCatalog.SelectEndpointRegistrars(profile))
        {
            AddDescriptor(
                services,
                ServiceDescriptor.Singleton(
                    typeof(Microsoft.Extensions.Hosting.IHostedService),
                    endpoint.RegistrarType));
        }

        return services;
    }

    private static void AddDescriptor(ServiceCollection services, ServiceDescriptor descriptor) =>
        ((ICollection<ServiceDescriptor>)services).Add(descriptor);

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

    private sealed class FakeDbContextFactory<TContext> : IDbContextFactory<TContext>
        where TContext : DbContext
    {
        public TContext CreateDbContext() => throw new NotSupportedException();

        public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
