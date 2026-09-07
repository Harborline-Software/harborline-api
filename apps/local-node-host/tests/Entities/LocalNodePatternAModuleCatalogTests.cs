using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Workflow;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class LocalNodePatternAModuleCatalogTests
{
    private static readonly string[] ExpectedModuleKeys =
    [
        "harborline.blocks.banking",
        "harborline.blocks.docs",
        "harborline.blocks.financial-ap",
        "harborline.blocks.financial-ar",
        "harborline.blocks.financial-ledger",
        "harborline.blocks.financial-payments",
        "harborline.blocks.financial-periods",
        "harborline.blocks.people-foundation",
        "harborline.local-node.audit",
        "harborline.local-node.form-submit-outbox",
        "harborline.local-node.home-epoch",
        "harborline.local-node.spatial-frames",
        "harborline.local-node.workflow",
    ];

    private static readonly string[] ExpectedMigrationIds =
    [
        "20260613194206_InitialSchema",
        "20260615232535_AddSubLedgerAccountId",
        "20260616021727_AddJournalEntrySourceReferenceUniqueIndex",
        "20260616155657_AddPaymentSourceReference",
        "20260616235743_AddNodeAuditTables",
        "20260623210653_AddWorkflowEngineTables",
        "20260624030425_AddWorkflowInstanceIteration",
        "20260713171627_AddHomeEpochTable",
        "20260716171804_AddPaymentApplicationReversalEvidence",
        "20260729090517_AddPaymentIntendedTarget",
        "20260730104243_AddReconciliationVersion",
        "20260805220357_AddSpatialFrameTables",
        "20260806010037_WidenSpatialFrameGovernedColumns",
        "20260818083559_AddFormSubmitOutbox",
    ];

    [Fact]
    public void Runtime_And_DesignTime_Consume_The_Exact_Catalog_Set()
    {
        var services = new ServiceCollection();
        services.AddLocalNodePatternAModules();
        services.AddLocalNodePatternAModules();
        services.AddLocalNodePatternAModule<WorkflowEntityModule>();
        services.ValidateLocalNodePatternAModules();

        using var provider = services.BuildServiceProvider();
        var runtime = provider.GetServices<IHarborlineEntityModule>().ToArray();
        var designTime = DesignTimeLocalNodeDbContextFactory.CreateMigrationModules();

        Assert.Equal(ExpectedModuleKeys, runtime.Select(module => module.ModuleKey).Order(StringComparer.Ordinal));
        Assert.Equal(ExpectedModuleKeys, designTime.Select(module => module.ModuleKey).Order(StringComparer.Ordinal));
        Assert.Equal(
            runtime.Select(module => module.GetType()).OrderBy(type => type.FullName).ToArray(),
            designTime.Select(module => module.GetType()).OrderBy(type => type.FullName).ToArray());
        Assert.Equal(ExpectedModuleKeys.Length, runtime.Select(module => module.GetType()).Distinct().Count());
    }

    [Fact]
    public void Catalog_Produces_Fresh_Modules_And_Order_Independent_Context_Signatures()
    {
        var first = LocalNodePatternAModuleCatalog.CreateModules();
        var second = LocalNodePatternAModuleCatalog.CreateModules();
        var options = new DbContextOptionsBuilder<LocalNodeDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        using var forward = new LocalNodeDbContext(options, first);
        using var reverse = new LocalNodeDbContext(options, second.Reverse());

        Assert.All(first.Zip(second), pair => Assert.NotSame(pair.First, pair.Second));
        Assert.Equal(forward.ModuleSignature, reverse.ModuleSignature);
    }

    [Fact]
    public void Committed_Migration_Snapshot_Matches_The_Catalog_Model()
    {
        using var context = new DesignTimeLocalNodeDbContextFactory().CreateDbContext([]);

        Assert.Equal(ExpectedMigrationIds, context.Database.GetMigrations());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Final_Registration_Validation_Refuses_Unknown_Missing_Duplicate_And_Opaque_Modules()
    {
        var unknown = CompleteServices();
        unknown.AddSingleton<IHarborlineEntityModule, UnknownEntityModule>();
        Assert.StartsWith(
            "local-node.pattern-a.unexpected:",
            Assert.Throws<InvalidOperationException>(unknown.ValidateLocalNodePatternAModules).Message);

        var missing = CompleteServices();
        missing.Remove(missing.Last(descriptor =>
            descriptor.ServiceType == typeof(IHarborlineEntityModule) &&
            descriptor.ImplementationType == typeof(WorkflowEntityModule)));
        Assert.StartsWith(
            "local-node.pattern-a.missing:",
            Assert.Throws<InvalidOperationException>(missing.ValidateLocalNodePatternAModules).Message);

        var duplicate = CompleteServices();
        duplicate.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        Assert.StartsWith(
            "local-node.pattern-a.duplicate:",
            Assert.Throws<InvalidOperationException>(duplicate.ValidateLocalNodePatternAModules).Message);

        var opaque = CompleteServices();
        opaque.AddSingleton<IHarborlineEntityModule>(_ => new UnknownEntityModule());
        Assert.StartsWith(
            "local-node.pattern-a.opaque:",
            Assert.Throws<InvalidOperationException>(opaque.ValidateLocalNodePatternAModules).Message);

        var nonSingleton = CompleteServices();
        nonSingleton.Remove(nonSingleton.Last(descriptor =>
            descriptor.ServiceType == typeof(IHarborlineEntityModule) &&
            descriptor.ImplementationType == typeof(WorkflowEntityModule)));
        nonSingleton.AddScoped<IHarborlineEntityModule, WorkflowEntityModule>();
        Assert.StartsWith(
            "local-node.pattern-a.lifetime:",
            Assert.Throws<InvalidOperationException>(nonSingleton.ValidateLocalNodePatternAModules).Message);
    }

    [Fact]
    public void Production_Composition_Closes_The_Final_Module_Graph_Before_Build()
    {
        var program = File.ReadAllText(Path.Combine(LocateHostSourceRoot(), "Program.cs"));
        const string boundary = "builder.Host.UseServiceProviderFactory(finalServiceProviderFactory);";
        const string build = "var app = builder.Build();";

        var boundaryIndex = program.IndexOf(boundary, StringComparison.Ordinal);
        var buildIndex = program.IndexOf(build, StringComparison.Ordinal);

        Assert.True(boundaryIndex >= 0, "Program must install the final graph provider boundary.");
        Assert.True(buildIndex > boundaryIndex, "Pattern-A validation must run during provider construction.");
        Assert.Equal(boundaryIndex, program.LastIndexOf(boundary, StringComparison.Ordinal));

        var catalog = File.ReadAllText(Path.Combine(
            LocateHostSourceRoot(),
            "Capabilities",
            "LocalNodeTechnicalRegistrationEvidence.cs"));
        Assert.Contains("services.ValidateLocalNodePatternAModules();", catalog, StringComparison.Ordinal);
    }

    private static ServiceCollection CompleteServices()
    {
        var services = new ServiceCollection();
        services.AddLocalNodePatternAModules();
        return services;
    }

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

    private sealed class UnknownEntityModule : IHarborlineEntityModule
    {
        public string ModuleKey => "test.unknown";

        public void Configure(ModelBuilder modelBuilder) => ArgumentNullException.ThrowIfNull(modelBuilder);
    }
}
