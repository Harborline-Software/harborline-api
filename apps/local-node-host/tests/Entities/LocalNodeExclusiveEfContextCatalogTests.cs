using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Drafts;
using Harborline.Api.LocalNodeHost.Data.Scheduling;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class LocalNodeExclusiveEfContextCatalogTests
{
    private static readonly string[] ExpectedContextKeys =
    [
        "local-node.ef.maintenance",
        "local-node.ef.properties",
        "local-node.ef.leases",
        "local-node.ef.bank-feed",
        "local-node.ef.payroll",
        "local-node.ef.comms",
        "local-node.ef.roster",
        "local-node.ef.admission",
        "local-node.ef.search",
        "local-node.ef.calendar",
        "local-node.ef.packs",
        "local-node.ef.org-branding",
        "local-node.ef.scheduling",
        "local-node.ef.installation-identity",
        "local-node.ef.web-session",
        "local-node.ef.drafts",
    ];

    private static readonly string[] ExpectedMigrationIds =
    [
        "20260613223256_MaintenanceInitial",
        "20260614220931_PropertyInitial",
        "20260614220950_LeaseInitial",
        "20260616000000_BankFeedConnectionsInitial",
        "20260616231430_PayrollInitial",
        "20260620133344_CommsInitial",
        "20260621000000_CommsConversationId",
        "20260620201710_RosterInitial",
        "20260621114414_AdmissionTokensInitial",
        "20260621115445_RosterAddTransportPublicKey",
        "20260621212751_RosterAddDmPublicKey",
        "20260624111430_SearchInitial",
        "20260624120000_CalendarInitial",
        "20260624134345_VecIndex",
        "20260624152259_DurableSubjectErasure",
        "20260626215943_RosterAddXWingPublicKey",
        "20260701175903_AddSubmissionDraftTable",
        "20260707044248_PacksInitial",
        "20260707100739_CalendarCollectionInitial",
        "20260707103037_OrgBrandingInitial",
        "20260707140050_AddFeedChannelSequences",
        "20260712120000_AddSchedulingDefinitionDrafts",
        "20260713214619_InstallationIdentityInitial",
        "20260713233947_InstallationIdentityCoordination",
        "20260714103750_InstallationIdentityCutoverRecords",
        "20260714104253_GrantFreshnessRows",
        "20260716214207_WebSessionRecords",
        "20260718110500_InstallationSessionPrimitives",
        "20260718132100_LegacyRenameCheckpointBinding",
        "20260718153000_AntiforgeryActiveStateFence",
        "20260718161500_AccountSetupInvitations",
        "20260723050000_RecoveryInvitations",
        // #3167 layers 1-2 — the admission-provenance columns on the roster context + the durable web-pairing
        // invite-binding table on the admission context (both existing contexts; no new context/owner).
        "20260724122428_RosterAddAdmissionProvenance",
        "20260724123053_AdmissionAddPairingInviteBindings",
        "20260728043828_LegacyBearerCutoverEvidence",
        "20260902140000_BootstrapClaimMarker",
        // Data-only backfill of the per-principal authorization epoch for grants that predate the epoch
        // table (search context; no schema change, so no snapshot change).
        "20260802103000_GrantAuthorizationEpochBackfill",
        "20260805090000_GrantPermissionSets",
        // Data-only spatial:read backfill for exact pre-widening member/admin template edges (search
        // context; permissions_json only — no schema change, so no snapshot change; card 3789).
        "20260806120000_SpatialReadTemplateBundleBackfill",
        "20260818152931_AddCompromisedDeviceResponses",
        // Data-only forms:author backfill for exact pre-widening admin/owner template edges (search
        // context; permissions_json only — no schema change, so no snapshot change; ticket 151).
        "20260830130000_FormsAuthorTemplateBundleBackfill",
        "20260902010100_AuthorizationStoreOfRecord",
        "20260902030355_AuthorizationClosure",
        "20260902084321_HistoricalAuthorizationEffectiveTime",
        "20260902113605_TenantScopedAuthorizationDefinitions",
        "20260902120000_AuthorizationClosureOwnerVersion",
        "20260902202756_TenantScopedSearchProjectionKeys",
        // ADR 0066 migration step 1 — the append-only administrator-authority log on the roster context
        // (an existing context and owner; no new context, so the 16/15+1 counts are unchanged).
        "20260831184210_RosterAddAdministratorAuthority",
        "20260902120000_AddPackProjectionAdmissions",
        "20260908030000_RosterAddRehostGrantBurns",
    ];

    [Fact]
    [Trait("PlanCard", "ADM-01A")]
    public void Catalog_Binds_The_Exact_16_Contexts_50_Migrations_And_15_Plus_1_Owners()
    {
        var catalog = LocalNodeExclusiveEfContextCatalog.All;

        Assert.Equal(ExpectedContextKeys, catalog.Select(item => item.ContextKey));
        Assert.Equal(16, catalog.Select(item => item.ContextType).Distinct().Count());
        Assert.Equal(16, catalog.Select(item => item.MigrationsHistoryTable).Distinct().Count());
        Assert.DoesNotContain(catalog, item => item.MigrationsHistoryTable == "__EFMigrationsHistory");
        Assert.Equal(
            ExpectedMigrationIds.Order(StringComparer.Ordinal),
            catalog.SelectMany(item => item.CompiledMigrations)
                .Select(item => item.MigrationId)
                .Order(StringComparer.Ordinal));
        Assert.Equal(15, catalog.Count(item =>
            item.Owner == LocalNodeExclusiveMigrationOwner.EncryptionGuard));
        Assert.Single(catalog, item => item.Owner == LocalNodeExclusiveMigrationOwner.Drafts);
        var expandedOwnerSequence = catalog
            .Where(item => item.Owner == LocalNodeExclusiveMigrationOwner.EncryptionGuard)
            .OrderBy(item => item.ExecutionOrder)
            .Concat(catalog
                .Where(item => item.Owner == LocalNodeExclusiveMigrationOwner.Drafts)
                .OrderBy(item => item.ExecutionOrder));
        Assert.Equal(catalog.Select(item => item.ContextType), expandedOwnerSequence.Select(item => item.ContextType));
        Assert.All(catalog, item =>
        {
            Assert.True(typeof(DbContext).IsAssignableFrom(item.ContextType));
            Assert.False(item.ModelSnapshotType.IsAbstract);
            Assert.False(item.DesignTimeFactoryType.IsAbstract);
            Assert.NotEmpty(item.CompiledMigrations);
        });
    }

    [Fact]
    public void Every_DesignTime_Model_Matches_Its_Compiled_Migration_Snapshot()
    {
        foreach (var descriptor in LocalNodeExclusiveEfContextCatalog.All)
        {
            var factory = Activator.CreateInstance(descriptor.DesignTimeFactoryType, nonPublic: true)
                ?? throw new InvalidOperationException(descriptor.DesignTimeFactoryType.FullName);
            var create = descriptor.DesignTimeFactoryType.GetMethod("CreateDbContext")
                ?? throw new MissingMethodException(descriptor.DesignTimeFactoryType.FullName, "CreateDbContext");
            using var context = (DbContext?)create.Invoke(factory, new object?[] { Array.Empty<string>() })
                ?? throw new InvalidOperationException(descriptor.ContextType.FullName);

            Assert.Equal(
                descriptor.CompiledMigrations.Select(item => item.MigrationId),
                context.Database.GetMigrations());
            Assert.False(
                context.Database.HasPendingModelChanges(),
                $"{descriptor.ContextKey} has model changes missing from its committed snapshot.");
        }
    }

    [Fact]
    public void Final_Service_Graph_Validation_Refuses_Factory_And_Owner_Drift()
    {
        var missing = CompleteServices();
        RemoveFactory<NodeLocalSchedulingDbContext>(missing);
        Assert.StartsWith(
            "local-node.exclusive-ef.factory_missing:",
            Assert.Throws<InvalidOperationException>(missing.ValidateLocalNodeExclusiveEfContexts).Message);

        var duplicate = CompleteServices();
        var scheduling = FindFactory<NodeLocalSchedulingDbContext>(duplicate);
        AddDescriptor(duplicate, scheduling);
        Assert.StartsWith(
            "local-node.exclusive-ef.factory_duplicate:",
            Assert.Throws<InvalidOperationException>(duplicate.ValidateLocalNodeExclusiveEfContexts).Message);

        var opaque = CompleteServices();
        RemoveFactory<NodeLocalSchedulingDbContext>(opaque);
        AddDescriptor(opaque, ServiceDescriptor.Singleton(
            typeof(IDbContextFactory<NodeLocalSchedulingDbContext>),
            _ => null!));
        Assert.StartsWith(
            "local-node.exclusive-ef.factory_opaque:",
            Assert.Throws<InvalidOperationException>(opaque.ValidateLocalNodeExclusiveEfContexts).Message);

        var wrongLifetime = CompleteServices();
        var observed = FindFactory<NodeLocalSchedulingDbContext>(wrongLifetime);
        RemoveFactory<NodeLocalSchedulingDbContext>(wrongLifetime);
        AddDescriptor(
            wrongLifetime,
            ServiceDescriptor.Scoped(observed.ServiceType, observed.ImplementationType!));
        Assert.StartsWith(
            "local-node.exclusive-ef.factory_lifetime:",
            Assert.Throws<InvalidOperationException>(wrongLifetime.ValidateLocalNodeExclusiveEfContexts).Message);

        var unexpected = CompleteServices();
        unexpected.AddDbContextFactory<UnexpectedDbContext>(options =>
            options.UseSqlite("Data Source=:memory:"));
        Assert.StartsWith(
            "local-node.exclusive-ef.context_unexpected:",
            Assert.Throws<InvalidOperationException>(unexpected.ValidateLocalNodeExclusiveEfContexts).Message);

        var duplicateStep = CompleteServices();
        var step = duplicateStep.First(item =>
            item.ServiceType == typeof(ILocalNodeExclusiveContextMigrator));
        AddDescriptor(duplicateStep, step);
        Assert.StartsWith(
            "local-node.exclusive-ef.owner_duplicate:",
            Assert.Throws<InvalidOperationException>(duplicateStep.ValidateLocalNodeExclusiveEfContexts).Message);

        var wrongOrder = CompleteServices();
        var guard = wrongOrder.Single(item =>
            item.ServiceType == typeof(IHostedService) &&
            item.ImplementationType == typeof(LocalNodeStoreEncryptionGuard));
        wrongOrder.Remove(guard);
        AddDescriptor(wrongOrder, guard);
        Assert.StartsWith(
            "local-node.exclusive-ef.owner_coverage:",
            Assert.Throws<InvalidOperationException>(wrongOrder.ValidateLocalNodeExclusiveEfContexts).Message);
    }

    [Fact]
    public void RootSeed_And_InjectedDek_Compositions_Produce_The_Same_Validated_Inventory()
    {
        var rootSeed = new byte[32];
        var dek = new byte[32];
        Array.Fill(rootSeed, (byte)0x31);
        Array.Fill(dek, (byte)0x73);

        var rootServices = new ServiceCollection();
        rootServices.AddLogging();
        rootServices.AddSqlCipherLocalNodeDbContext(
            rootSeed,
            TempDatabasePath(),
            new Harborline.Api.Kernel.Security.Keys.SqlCipherKeyDerivation());
        rootServices.AddHostedService<NodeDraftsMigrator>();
        rootServices.ValidateLocalNodeExclusiveEfContexts();

        var dekServices = new ServiceCollection();
        dekServices.AddLogging();
        dekServices.AddSqlCipherLocalNodeDbContextWithStoreDek(dek, TempDatabasePath());
        dekServices.AddHostedService<NodeDraftsMigrator>();
        dekServices.ValidateLocalNodeExclusiveEfContexts();

        Assert.Equal(Inventory(rootServices), Inventory(dekServices));
    }

    [Fact]
    public void Catalog_Order_Is_Culture_Independent_And_Program_Validates_Once_Before_Build()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            var turkish = CatalogIdentity();
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal(turkish, CatalogIdentity());
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }

        var program = File.ReadAllText(Path.Combine(LocateHostSourceRoot(), "Program.cs"));
        const string boundary = "builder.Host.UseServiceProviderFactory(finalServiceProviderFactory);";
        const string build = "var app = builder.Build();";
        var boundaryIndex = program.IndexOf(boundary, StringComparison.Ordinal);
        var buildIndex = program.IndexOf(build, StringComparison.Ordinal);

        Assert.True(boundaryIndex >= 0);
        Assert.True(buildIndex > boundaryIndex);
        Assert.Equal(boundaryIndex, program.LastIndexOf(boundary, StringComparison.Ordinal));

        var catalog = File.ReadAllText(Path.Combine(
            LocateHostSourceRoot(),
            "Capabilities",
            "LocalNodeTechnicalRegistrationEvidence.cs"));
        Assert.Contains("services.ValidateLocalNodeExclusiveEfContexts();", catalog, StringComparison.Ordinal);
    }

    private static ServiceCollection CompleteServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSqlCipherLocalNodeDbContextWithStoreDek(new byte[32], TempDatabasePath());
        services.AddHostedService<NodeDraftsMigrator>();
        services.ValidateLocalNodeExclusiveEfContexts();
        return services;
    }

    private static ServiceDescriptor FindFactory<TContext>(ServiceCollection services)
        where TContext : DbContext =>
        services.Single(item => item.ServiceType == typeof(IDbContextFactory<TContext>));

    private static void RemoveFactory<TContext>(ServiceCollection services)
        where TContext : DbContext =>
        services.Remove(FindFactory<TContext>(services));

    private static void AddDescriptor(ServiceCollection services, ServiceDescriptor descriptor) =>
        ((ICollection<ServiceDescriptor>)services).Add(descriptor);

    private static string[] Inventory(ServiceCollection services) =>
        services
            .Where(item => item.ServiceType.IsGenericType &&
                item.ServiceType.GetGenericTypeDefinition() == typeof(IDbContextFactory<>))
            .Select(item => item.ServiceType.GenericTypeArguments[0].FullName!)
            .Order(StringComparer.Ordinal)
            .Concat(services
                .Where(item => item.ServiceType == typeof(ILocalNodeExclusiveContextMigrator))
                .Select(item => item.ImplementationType!.FullName!)
                .Order(StringComparer.Ordinal))
            .ToArray();

    private static string[] CatalogIdentity() =>
        LocalNodeExclusiveEfContextCatalog.All
            .Select(item => $"{item.ExecutionOrder:D3}|{item.ContextKey}|{item.ContextType.FullName}|" +
                $"{item.MigrationsHistoryTable}|{item.Owner}|" +
                string.Join(',', item.CompiledMigrations.Select(migration => migration.MigrationId)))
            .ToArray();

    private static string TempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"exclusive-ef-catalog-{Guid.NewGuid():N}.db");

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

    private sealed class UnexpectedDbContext(DbContextOptions<UnexpectedDbContext> options)
        : DbContext(options);
}
