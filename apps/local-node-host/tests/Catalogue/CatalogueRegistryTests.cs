using System.Text;
using System.Text.Json;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Banking.Feed;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Services;
using Harborline.Api.Blocks.Reports;
using Harborline.Api.Blocks.Reports.Cartridges.TrialBalance;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.DataExchangeDefinitions;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Documents.Issuance;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.ReportDefinitions;
using Harborline.Api.Foundation.RuleEngine.Standings;
using Harborline.Api.Foundation.ScheduleDefinitions;
using Harborline.Api.Foundation.Taxonomy.Models;
using Harborline.Api.Foundation.Taxonomy.Services;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Catalogue;

public sealed class CatalogueRegistryTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000426");
    private static readonly TenantId OtherTenant = new("bbbbbbbb-0000-0000-0000-000000000426");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Real_projection_rows_supply_bodies_and_pack_provenance_for_every_composed_adapter()
    {
        using var fixture = new Fixture();
        var items = Content();
        fixture.Install(Tenant, "catalogue.receipt", items);
        Assert.Empty((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals);

        var catalogue = fixture.Catalogue();
        var listed = await catalogue.ListAsync(Tenant);
        foreach (var item in items)
        {
            var entry = Assert.Single(listed.Entries, entry => entry.Kind == item.Kind);
            Assert.Equal(item.Key, entry.Id);
            Assert.Equal(item.Version, entry.Version);
            Assert.Equal(new CatalogueProvenance("catalogue.receipt", "4.2.6", "pack"), entry.Provenance);
            Assert.False(entry.Sealed);
            Assert.DoesNotContain(item.Kind, listed.KindsUnavailable);
            var fetched = await catalogue.GetAsync(Tenant, item.Kind, entry.Id, entry.Version);
            Assert.NotNull(fetched);
            Assert.True(JsonElement.DeepEquals(entry.Body, fetched.Body));
            Assert.Null(await catalogue.GetAsync(Tenant, item.Kind, entry.Id, "99.0.0"));
        }
        Assert.Equal("Pump from registry", listed.Entries.Single(entry => entry.Kind == PackContentKind.AssetTypeDefinition).Body.GetProperty("displayName").GetString());
        Assert.Equal("Trial balance receipt", listed.Entries.Single(entry => entry.Kind == PackContentKind.ReportDefinition).Body.GetProperty("title").GetString());
        Assert.Equal("handler", listed.Entries.Single(entry => entry.Kind == PackContentKind.StandingRuleDefinition).Body.GetProperty("standing").GetProperty("name").GetString());
        Assert.Equal("Workflow receipt", listed.Entries.Single(entry => entry.Kind == PackContentKind.WorkflowDefinition).Title!.Values["en"]);
        Assert.Empty((await catalogue.ListAsync(OtherTenant)).Entries);

        fixture.Templates.Remove("receipt.template", "1.0.0");
        await fixture.Standings.RemoveAsync("receipt.handler", "1.0.0");
        await fixture.Schedules.RemoveAsync(Tenant.Value, "receipt.schedule", "1.0.0");
        foreach (var kind in new[] { PackContentKind.TemplateDefinition, PackContentKind.StandingRuleDefinition, PackContentKind.ScheduleDefinition })
        {
            var empty = await catalogue.ListAsync(Tenant, kind);
            Assert.Empty(empty.Entries);
            Assert.Empty(empty.KindsUnavailable);
        }
        fixture.Packs.Deactivate(Tenant, "catalogue.receipt", "4.2.6");
        Assert.Empty((await catalogue.ListAsync(Tenant)).Entries);
    }

    [Fact]
    public async Task Composed_empty_registries_are_available_and_missing_registries_are_unavailable()
    {
        using var fixture = new Fixture();
        var composed = await fixture.Catalogue().ListAsync(Tenant);
        Assert.Empty(composed.Entries);
        foreach (var kind in Content().Select(item => item.Kind)) Assert.DoesNotContain(kind, composed.KindsUnavailable);
        var absent = await new ProjectedCatalogue(fixture.Forms).ListAsync(Tenant);
        Assert.Contains(PackContentKind.ViewDefinition, absent.KindsUnavailable);
        foreach (var kind in Content().Select(item => item.Kind)) Assert.Contains(kind, absent.KindsUnavailable);
        foreach (var kind in new[] { PackContentKind.StandardsCatalog, PackContentKind.CascadeDefaults, PackContentKind.TerminologyOverride })
            Assert.Contains(kind, composed.KindsUnavailable);
    }

    [Fact]
    public async Task Active_seed_coordinates_without_registry_rows_do_not_fabricate_definitions()
    {
        using var fixture = new Fixture();
        fixture.Install(Tenant, "catalogue.unprojected", Content());
        Assert.Empty((await fixture.Catalogue().ListAsync(Tenant)).Entries);
    }

    [Fact]
    public async Task Shared_standing_rows_cannot_borrow_another_tenants_body_at_the_same_coordinate()
    {
        using var fixture = new Fixture();
        var original = Content().Single(item => item.Kind == PackContentKind.StandingRuleDefinition);
        fixture.Install(Tenant, "catalogue.original", [original]);
        Assert.Empty((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals);
        fixture.Install(OtherTenant, "catalogue.other", [original with { CanonicalJson = original.CanonicalJson.Replace("person-a", "person-b", StringComparison.Ordinal) }]);
        Assert.Empty((await fixture.Catalogue().ListAsync(OtherTenant, PackContentKind.StandingRuleDefinition)).Entries);
    }

    [Fact]
    public async Task Shared_template_rows_must_belong_to_the_installing_tenant_and_match_the_active_source()
    {
        using var fixture = new Fixture();
        var original = Content().Single(item => item.Kind == PackContentKind.TemplateDefinition);
        fixture.Install(Tenant, "catalogue.original", [original]);
        Assert.Empty((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals);
        fixture.Install(OtherTenant, "catalogue.other", [original]);
        Assert.Empty((await fixture.Catalogue().ListAsync(OtherTenant, PackContentKind.TemplateDefinition)).Entries);
        var stored = fixture.Templates.Resolve(original.Key, original.Version)!;
        fixture.Templates.Publish(stored with { DocumentType = "Different source" });
        Assert.Empty((await fixture.Catalogue().ListAsync(Tenant, PackContentKind.TemplateDefinition)).Entries);
    }

    [Theory]
    [InlineData(PackContentKind.ReportDefinition)]
    [InlineData(PackContentKind.DataExchangeDefinition)]
    [InlineData(PackContentKind.TaxonomyDefinition)]
    public async Task Divergent_tenant_rows_keep_tenant_provenance_until_claimed_and_never_borrow_pack_provenance(PackContentKind kind)
    {
        using var fixture = new Fixture();
        var item = Content().Single(item => item.Kind == kind);
        switch (kind)
        {
            case PackContentKind.ReportDefinition:
                var report = JsonSerializer.Deserialize<ReportDefinition>(item.CanonicalJson, Json)!;
                await fixture.Reports.RegisterAsync(report with { Title = "Divergent tenant report" });
                break;
            case PackContentKind.DataExchangeDefinition:
                var exchange = JsonSerializer.Deserialize<DataExchangeDefinition>(item.CanonicalJson, Json)!;
                await fixture.Exchanges.RegisterAsync(exchange with { Settings = JsonSerializer.SerializeToElement(new { mapping = "different" }) });
                break;
            case PackContentKind.TaxonomyDefinition:
                var taxonomy = JsonSerializer.Deserialize<TaxonomyDefinition>(item.CanonicalJson, Json)!;
                await fixture.Taxonomies.CreateAsync(Tenant, taxonomy.Id, taxonomy.Version, taxonomy.Governance,
                    "Divergent tenant taxonomy", taxonomy.Owner, taxonomy.DerivedFrom, CancellationToken.None);
                break;
        }
        var catalogue = fixture.Catalogue();
        var authored = Assert.Single((await catalogue.ListAsync(Tenant, kind)).Entries);
        Assert.Equal(new CatalogueProvenance(null, null, "tenant"), authored.Provenance);
        fixture.Install(Tenant, "catalogue.claimant", [item]);
        Assert.Contains((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals,
            refusal => refusal.ContentKind == kind && refusal.Code.EndsWith("pinned_tuple_conflict", StringComparison.Ordinal));
        var refused = await catalogue.ListAsync(Tenant, kind);
        Assert.Empty(refused.Entries);
        Assert.Empty(refused.KindsUnavailable);
        Assert.Null(await catalogue.GetAsync(Tenant, kind, item.Key, item.Version));
    }

    [Fact]
    public async Task Divergent_schedule_at_an_active_coordinate_cannot_borrow_pack_provenance()
    {
        using var fixture = new Fixture();
        var item = Content().Single(item => item.Kind == PackContentKind.ScheduleDefinition);
        var schedule = JsonSerializer.Deserialize<ScheduleDefinition>(item.CanonicalJson, Json)!;
        await fixture.Schedules.RegisterAsync(schedule with { Title = "Divergent tenant schedule" });
        fixture.Install(Tenant, "catalogue.claimant", [item]);
        Assert.Contains((await fixture.Projector.ProjectActivePacksAsync(Tenant)).Refusals,
            refusal => refusal.ContentKind == item.Kind && refusal.Code.EndsWith("pinned_tuple_conflict", StringComparison.Ordinal));
        Assert.Empty((await fixture.Catalogue().ListAsync(Tenant, item.Kind)).Entries);
        Assert.Null(await fixture.Catalogue().GetAsync(Tenant, item.Kind, item.Key, item.Version));
    }

    private static PackSeedItem[] Content()
    {
        var rule = new StandingRuleDefinition("receipt.handler", "1.0.0", new StandingReference("handler"), "matter", ["handler_id"],
            new RuleDefinition(new DefinitionEnvelope<string, string, TenantId, string?>("receipt.handler", "1.0.0", Tenant,
                CascadeLayer.Pack, null, []), RuleTier.JsonLogic, RuleScope.Schema, string.Empty,
                """{"==":[{"var":"handler_id"},"person-a"]}""", RuleActionKind.Validate));
        return
        [
            Item("receipt.asset", PackContentKind.AssetTypeDefinition, new { id = "receipt.asset", displayName = "Pump from registry", traits = new[] { "maintainable" } }),
            Item("receipt.workflow", PackContentKind.WorkflowDefinition, new { key = "receipt.workflow", version = "1.0.0", tenant = Tenant.Value,
                title = new { defaultLocale = "en", values = new { en = "Workflow receipt" } }, initialState = "Open", states = new[] { new { id = "Open", kind = "Terminal" } },
                triggers = Array.Empty<object>(), transitions = Array.Empty<object>(), actions = Array.Empty<object>(), guards = Array.Empty<object>() }),
            Item("receipt.template", PackContentKind.TemplateDefinition, new { key = "receipt.template", version = "1.0.0", documentType = "Invoice receipt",
                recordType = new { type = "invoice", version = "1" }, locale = new { kind = "fixed", tag = "en-US" },
                structure = new[] { new { kind = "header", lines = new[] { new { runs = new[] { new { literal = "Invoice receipt" } } } } } } }),
            Item("Receipt.Domain.Tags", PackContentKind.TaxonomyDefinition, new TaxonomyDefinition { Id = new("Receipt", "Domain", "Tags"),
                Version = TaxonomyVersion.V1_0_0, Governance = TaxonomyGovernanceRegime.Civilian, Description = "Taxonomy receipt", Owner = new ActorId("receipt-author"), PublishedAt = TestAuthorization.At }),
            Item("receipt.report", PackContentKind.ReportDefinition, new ReportDefinition { Key = "receipt.report", Version = "1.0.0", Tenant = Tenant.Value,
                SchemaVersion = 1, ReportKind = "trial-balance", Title = "Trial balance receipt", Parameters = JsonSerializer.SerializeToElement(new { chart = "receipt-chart" }) }),
            Item("receipt.exchange", PackContentKind.DataExchangeDefinition, new DataExchangeDefinition { Key = "receipt.exchange", Version = "1.0.0", Tenant = Tenant.Value,
                SchemaVersion = 1, ExchangeKind = "import.erpnext/maria-db-dump", Title = "Exchange receipt", Settings = JsonSerializer.SerializeToElement(new { mapping = "receipt" }) }),
            Item("receipt.handler", PackContentKind.StandingRuleDefinition, rule),
            Item("receipt.schedule", PackContentKind.ScheduleDefinition, new ScheduleDefinition { Key = "receipt.schedule", Version = "1.0.0", Tenant = Tenant.Value,
                SchemaVersion = 1, ScheduleKind = "harborline.scheduling-definition-draft/v0", Title = "Schedule receipt",
                Body = JsonSerializer.SerializeToElement(new { schema = "harborline.scheduling-definition-draft/v0", title = "Schedule receipt", timezone = "UTC",
                    activities = new[] { new { id = "visit", durationMinutes = 30 } }, resourceRequirements = Array.Empty<string>() }) }),
        ];
    }

    private static PackSeedItem Item(string key, PackContentKind kind, object body)
    {
        var json = JsonSerializer.Serialize(body, Json);
        return new PackSeedItem(key, kind, "1.0.0", json, Cid.FromBytes(Encoding.UTF8.GetBytes(json)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider services = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        private readonly InMemoryFormDefinitionStore formStore = new(TimeProvider.System);
        public readonly InMemoryPackInstallStore Packs = new();
        public readonly InMemoryDocumentTemplateRegistry Templates = new();
        public readonly InMemoryStandingRuleDefinitionStore Standings = new();
        public readonly InMemoryScheduleDefinitionRegistry Schedules = new(new HostScheduleKindDescriptorRegistry());
        public InMemoryReportDefinitionRegistry Reports { get; }
        public InMemoryDataExchangeDefinitionRegistry Exchanges { get; }
        public InMemoryTaxonomyRegistry Taxonomies { get; }
        public AuthorizedFormDefinitionLifecycle Forms { get; }
        public PackSeedProjector Projector { get; }
        private CatalogueRegistries Registries { get; }

        public Fixture()
        {
            Forms = TestAuthorization.FormLifecycle(formStore, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
            var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
            var workflows = new EntityStoreWorkflowDefinitionStore(entities, new WorkflowAdmissionValidator(), TimeProvider.System);
            var authorizedWorkflows = TestAuthorization.WorkflowLifecycle(workflows, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
            var cartridges = new ReportCartridgeRegistry();
            cartridges.Register(new TrialBalanceCartridge(Substitute.For<IChartRepository>(), Substitute.For<IAccountResolver>(),
                Substitute.For<IFiscalPeriodRepository>(), Substitute.For<IGeneralLedgerReadModel>()));
            Reports = new InMemoryReportDefinitionRegistry(new HostReportKindDescriptorRegistry(cartridges));
            Exchanges = new InMemoryDataExchangeDefinitionRegistry(new HostDataExchangeKindDescriptorRegistry([], Substitute.For<IBankFeedProvider>()));
            Taxonomies = new InMemoryTaxonomyRegistry(TimeProvider.System);
            var types = services.GetRequiredService<IEntityTypeRegistry>();
            Registries = new CatalogueRegistries(Packs, types, authorizedWorkflows, Templates, Taxonomies, Reports, Exchanges, Standings, Schedules);
            Projector = new PackSeedProjector(Packs, types, NullLogger<PackSeedProjector>.Instance,
                templates: Templates, workflows: workflows, time: TimeProvider.System, taxonomies: Taxonomies, reportDefinitions: Reports,
                scheduleDefinitions: Schedules, dataExchangeDefinitions: Exchanges, standingRules: Standings, authorizedWorkflows: authorizedWorkflows);
        }

        public ProjectedCatalogue Catalogue() => new(Forms, registries: Registries);
        public void Install(TenantId tenant, string key, IReadOnlyList<PackSeedItem> items)
        {
            var pack = new InstalledPack(key, "4.2.6", PackScopeTier.Horizontal, PackLifecycleState.Draft, items,
                new Dictionary<string, int>(), TestAuthorization.At, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1, TrustScope.OwnRoster, []);
            Packs.Commit(new PackInstallTransaction(tenant, pack, new PackInstallWatermark(key, pack.Version, new Dictionary<string, int>()), []));
            Packs.Activate(tenant, key, pack.Version);
        }
        public void Dispose() { formStore.Dispose(); services.Dispose(); }
    }
}
