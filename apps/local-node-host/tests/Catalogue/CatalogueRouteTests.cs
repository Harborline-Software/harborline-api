using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Catalogue;

/// <summary>Ticket 176 fix 2: real listener coverage for the catalogue's tenant and gate boundaries.</summary>
public sealed class CatalogueRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000001760a1"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbb0000-0000-0000-0000-0000001760b1"));

    private const string CatalogueBase = "/api/local-node/catalogue/definitions";
    private const string FormId = "catalogue-tenant-a.v1";

    private bool _holdsCatalogueRead = true;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;
    private TenantId _tenantA;
    private TenantId _requestTenant;
    private bool _withSelectedSession = true;
    private readonly InMemoryPackInstallStore _packStore = new();
    private IPackInstaller _installer = null!;
    private IPackTrustStore _packTrust = null!;
    private PlatformPackPreloadHostedService _platformPreload = null!;
    private CountingCatalogue _catalogue = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();
        builder.Services.AddSingleton<ITenantKeyProvider, InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<IFieldEncryptor, TenantKeyProviderFieldEncryptor>();
        builder.Services.AddSingleton(TestRouteGate.Following(
            operation => _holdsCatalogueRead || operation != Permission.CatalogueRead));
        builder.Services.AddAuthorizationRefusalAudit();

        _app = builder.Build();
        _activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));
        _tenantA = NodeTenant.Resolve(_activeTeam);
        _requestTenant = _tenantA;

        var signer = new NodePrincipalSigner(Enumerable.Repeat((byte)0x42, 32).ToArray());
        var codec = new PackFileCodec();
        _packTrust = new InMemoryPackTrustStore(
        [
            new PackTrustRoot(
                TrustScope.OwnRoster,
                signer.Signer.IssuerId,
                PackComposerRoutes.OwnRosterEpoch,
                TrustRootStatus.Current),
        ]);
        _installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), codec),
            _packStore,
            new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator(), defaults: new ActiveCascadeDefaultsProjection(),
                catalogueFields: new CatalogueFieldSourceAdmission(CatalogueDetailRuntime.Supports)),
            new InMemoryPackInstallAudit(),
            TestAuthorization.AllowGate(), new PackPlatformCompatibility("1.0.0",
                [new PackProjectorCase(PackContentKind.FormDefinition, [CatalogueFieldSourceContract.CapabilityId])]));
        _platformPreload = new PlatformPackPreloadHostedService(
            new PackExporter(
                new PackContentCanonicalizer(),
                new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
                codec,
                timeProvider: TimeProvider.System),
            signer,
            _installer,
            _packStore,
            _packTrust,
            PackRevocationList.Empty,
            _activeTeam,
            TimeProvider.System,
            NullLogger<PlatformPackPreloadHostedService>.Instance);

        var definitions = _app.Services.GetRequiredService<AuthorizedFormDefinitionLifecycle>();
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            if (_withSelectedSession)
            {
                http.Features.Set(new SelectedSessionRequestPrincipal(
                    "catalogue-account", _requestTenant, new PrincipalUserId("catalogue-principal"),
                    new CanonicalPartyReference("catalogue-party"), "catalogue-membership", 1,
                    [new PinnedGrantOwnerVersion("catalogue-grant", 1)], 1,
                    "catalogue-session", "catalogue-coordination"));
            }
            await next(http);
        });
        _catalogue = new CountingCatalogue(new ProjectedCatalogue(definitions));
        CatalogueRoutes.Map(
            _app.MapSelectedSessionProductGroup(),
            _catalogue,
            _packStore,
            new ActiveTeamTenantContext(_activeTeam));
        FormDefinitionRoutes.Map(
            _app,
            definitions,
            _app.Services.GetRequiredService<ISchemaRegistry>(),
            _activeTeam,
            TimeProvider.System);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    [Fact(DisplayName = "M4: the authenticated desktop app reads the active tenant catalogue without a web session")]
    public async Task Desktop_App_Reads_Active_Tenant_Catalogue()
    {
        await SaveTenantAFormAsync();
        _withSelectedSession = false;

        var body = await _client.GetFromJsonAsync<JsonElement>($"{CatalogueBase}?kind=FormDefinition");

        Assert.Contains(FormId, body.GetRawText(), StringComparison.Ordinal);
        Assert.Equal((int)PackContentKind.FormDefinition, body.GetProperty("entries").EnumerateArray()
            .Single(entry => entry.GetProperty("id").GetString() == FormId).GetProperty("kind").GetInt32());
    }

    [Fact(DisplayName = "M4: sealed catalogue types are absent before the platform seed is active")]
    public async Task Sealed_Catalogue_Types_Are_Absent_Before_The_Platform_Seed_Is_Active()
    {
        var types = await _client.GetFromJsonAsync<JsonElement>(CatalogueRoutes.TypesRoute);

        Assert.Empty(types.EnumerateArray());
    }

    [Fact(DisplayName = "M4: activating the platform seed exposes its sealed catalogue types with the active version")]
    public async Task Active_Platform_Seed_Exposes_Its_Sealed_Catalogue_Types_With_The_Active_Version()
    {
        await _platformPreload.PreloadAsync(_tenantA, CancellationToken.None);

        var types = await _client.GetFromJsonAsync<JsonElement>(CatalogueRoutes.TypesRoute);
        var entries = types.EnumerateArray().ToArray();
        Assert.Equal(19, entries.Length);
        Assert.All(entries, entry =>
        {
            Assert.True(entry.GetProperty("sealed").GetBoolean());
            var provenance = entry.GetProperty("provenance");
            Assert.Equal("harborline.platform", provenance.GetProperty("packKey").GetString());
            Assert.Equal(PlatformPackPreloadHostedService.PackVersion, provenance.GetProperty("packVersion").GetString());
            Assert.Equal("platform", provenance.GetProperty("kind").GetString());
        });
    }

    [Fact(DisplayName = "M4: deactivating the platform seed removes its sealed catalogue types")]
    public async Task Deactivated_Platform_Seed_Does_Not_Expose_Sealed_Catalogue_Types()
    {
        await _platformPreload.PreloadAsync(_tenantA, CancellationToken.None);
        var deactivated = _installer.Deactivate(
            PackContext(),
            PlatformPackPreloadHostedService.PackKey,
            PlatformPackPreloadHostedService.PackVersion);
        Assert.True(deactivated.Deactivated, deactivated.Error);

        var types = await _client.GetFromJsonAsync<JsonElement>(CatalogueRoutes.TypesRoute);

        Assert.Empty(types.EnumerateArray());
    }

    [Fact(DisplayName = "M4: invalid active platform seed descriptors fail closed")]
    public async Task Invalid_Active_Platform_Seed_Descriptors_Fail_Closed()
    {
        await _platformPreload.PreloadAsync(_tenantA, CancellationToken.None);
        var active = Assert.IsType<InstalledPack>(
            _packStore.GetActive(_tenantA, PlatformPackPreloadHostedService.PackKey));
        var descriptor = Assert.Single(active.SeedItems, item => item.Kind == PackContentKind.RecordType && item.Key == "FormDefinition");
        var duplicate = active with { SeedItems = [.. active.SeedItems, descriptor] };

        var types = SystemRecordType.FromActivePlatformPack(duplicate);

        Assert.Empty(types);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact(DisplayName = "holds 176.A1: tenant A catalogue entries never appear to tenant B")]
    public async Task Tenant_A_Catalogue_Entries_Are_Invisible_To_Tenant_B()
    {
        await SaveTenantAFormAsync();

        _activeTeam.Active = TeamContextFor(TeamB, "Team B");
        _requestTenant = NodeTenant.Resolve(_activeTeam);
        var tenantB = await _client.GetFromJsonAsync<JsonElement>($"{CatalogueBase}?kind=FormDefinition");
        Assert.Empty(tenantB.GetProperty("entries").EnumerateArray());
        Assert.DoesNotContain(FormId, tenantB.GetRawText(), StringComparison.Ordinal);

        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
        _requestTenant = _tenantA;
        var tenantA = await _client.GetFromJsonAsync<JsonElement>($"{CatalogueBase}?kind=FormDefinition");
        Assert.Contains(FormId, tenantA.GetRawText(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ticket 176: a tenant-authored catalogue definition retains its authored title and tenant provenance")]
    public async Task Tenant_Authored_Definition_Has_Authored_Title_And_Tenant_Provenance()
    {
        await SaveTenantAFormAsync();

        var body = await _client.GetFromJsonAsync<JsonElement>($"{CatalogueBase}?kind=FormDefinition");
        var entry = Assert.Single(body.GetProperty("entries").EnumerateArray());
        Assert.Equal("Catalogue tenant A", entry.GetProperty("title").GetProperty("values").GetProperty("en").GetString());
        var provenance = entry.GetProperty("provenance");
        Assert.Equal("tenant", provenance.GetProperty("kind").GetString());
        Assert.False(provenance.TryGetProperty("packKey", out _));
    }

    [Fact(DisplayName = "holds 176.A1: a principal without catalogue:read is refused with the deciding four-stage trace")]
    public async Task Principal_Without_Catalogue_Read_Is_Refused_With_A_Four_Stage_Trace()
    {
        _holdsCatalogueRead = false;

        var response = await _client.GetAsync($"{CatalogueBase}?kind=StandardsCatalog");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AuthorizationRefusalRenderer.PermissionRequiredCode, body.GetProperty("code").GetString());
        Assert.Equal(Permission.CatalogueRead, body.GetProperty("permission").GetString());

        var row = Assert.Single(await RefusalRowsAsync());
        Assert.Equal(false, row.Payload.Payload.Body["preDecision"]);
        Assert.NotNull(row.AuthoritySnapshot);
        Assert.Equal(4, row.AuthoritySnapshot.Trace!.Count);
        Assert.Equal(0, _catalogue.ListCallCount);
    }

    private sealed class CountingCatalogue(ICatalogue inner) : ICatalogue
    {
        public int ListCallCount { get; private set; }

        public ValueTask<CatalogueList> ListAsync(
            TenantId tenant, PackContentKind? kind = null, CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            return inner.ListAsync(tenant, kind, cancellationToken);
        }

        public ValueTask<CatalogueEntry?> GetAsync(
            TenantId tenant, PackContentKind kind, string id, string? version = null,
            CancellationToken cancellationToken = default)
            => inner.GetAsync(tenant, kind, id, version, cancellationToken);
    }

    [Fact(DisplayName = "holds 176.A1: an uncomposed kind is returned in kindsUnavailable rather than thrown")]
    public async Task Unavailable_Kind_Is_Returned_In_KindsUnavailable()
    {
        var response = await _client.GetAsync($"{CatalogueBase}?kind=ViewDefinition");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(body.GetProperty("entries").EnumerateArray());
        Assert.Equal((int)Harborline.Api.Foundation.Packs.Model.PackContentKind.ViewDefinition,
            body.GetProperty("kindsUnavailable")[0].GetInt32());
    }

    [Theory]
    [InlineData("999")]
    [InlineData("-1")]
    [InlineData("unknown")]
    public async Task Unknown_kind_is_bad_request_for_both_route_shapes(string kind)
    {
        foreach (var path in new[] { $"{CatalogueBase}?kind={kind}", $"{CatalogueBase}/{kind}/anything" })
        {
            var response = await _client.GetAsync(path);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("catalogue.kind_unknown", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task View_only_read_contains_no_forms_and_composed_empty_views_remain_available()
    {
        await SaveTenantAFormAsync();
        using var services = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var views = new InMemoryViewDefinitionRegistry(new HostViewKindDescriptorRegistry(
            services.GetRequiredService<IEntityTypeRegistry>(), _app.Services.GetRequiredService<IFormDefinitionStore>(),
            _app.Services.GetRequiredService<ISchemaRegistry>(),
            Harborline.Blocks.EntityViews.ViewKindRegistry.Platform));
        var catalogue = new ProjectedCatalogue(_app.Services.GetRequiredService<AuthorizedFormDefinitionLifecycle>(), views);
        var empty = await catalogue.ListAsync(_tenantA, PackContentKind.ViewDefinition);
        Assert.Empty(empty.Entries);
        Assert.Empty(empty.KindsUnavailable);
        await views.RegisterAsync(new ViewDefinition { Key = "receipt.view", Version = "1.0.0", Tenant = _tenantA.Value,
            SchemaVersion = 1, Title = "View receipt", ViewKind = HostViewKindDescriptorRegistry.TableKind,
            Parameters = JsonSerializer.SerializeToElement(new { entityType = "FormDefinition" }) });
        Assert.Equal("receipt.view", Assert.Single((await catalogue.ListAsync(_tenantA, PackContentKind.ViewDefinition)).Entries).Id);
        Assert.Equal(FormId, Assert.Single((await catalogue.ListAsync(_tenantA, PackContentKind.FormDefinition)).Entries).Id);
    }

    private async Task SaveTenantAFormAsync()
    {
        var saved = await _client.PutAsJsonAsync($"{FormDefinitionRoutes.RouteBase}/{FormId}", new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new[] { new { id = "main", title = Text("Main"), fields = new[] { "name" } } },
                rules = Array.Empty<object>(),
                title = Text("Catalogue tenant A"),
                description = Text("Catalogue route fixture"),
            },
            fieldsMeta = new Dictionary<string, object>
            {
                ["name"] = new { type = "text", required = false, options = (string[]?)null },
            },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    private async Task<IReadOnlyList<AuditRecord>> RefusalRowsAsync()
    {
        var rows = new List<AuditRecord>();
        await foreach (var row in _app.Services.GetRequiredService<IAuditTrail>().QueryAsync(new AuditQuery(_tenantA)))
        {
            if (row.EventType.Equals(AuthorizationRefusalAudit.AuthorizationRefusedEventType))
                rows.Add(row);
        }

        return rows;
    }

    private PackInstallContext PackContext() => new(
        _tenantA,
        _packTrust,
        PackRevocationList.Empty,
        TimeProvider.System.GetUtcNow(),
        PackInstallRoutes.RevocationMaxAge,
        Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);

    private static object Text(string en) => new
    {
        defaultLocale = "en",
        values = new Dictionary<string, string> { ["en"] = en },
    };

    private static TeamContext TeamContextFor(TeamId teamId, string name) =>
        new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class MutableActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void KeepCompilerHappy() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
