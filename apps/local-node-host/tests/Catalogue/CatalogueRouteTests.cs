using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery.TenantKey;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

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

        var definitions = _app.Services.GetRequiredService<AuthorizedFormDefinitionLifecycle>();
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            http.Features.Set(new SelectedSessionRequestPrincipal(
                "catalogue-account", _requestTenant, new PrincipalUserId("catalogue-principal"),
                new CanonicalPartyReference("catalogue-party"), "catalogue-membership", 1,
                [new PinnedGrantOwnerVersion("catalogue-grant", 1)], 1,
                "catalogue-session", "catalogue-coordination"));
            await next(http);
        });
        CatalogueRoutes.Map(_app.MapSelectedSessionProductGroup(), new ProjectedCatalogue(definitions));
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

        var response = await _client.GetAsync($"{CatalogueBase}?kind=FormDefinition");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AuthorizationRefusalRenderer.PermissionRequiredCode, body.GetProperty("code").GetString());
        Assert.Equal(Permission.CatalogueRead, body.GetProperty("permission").GetString());

        var row = Assert.Single(await RefusalRowsAsync());
        Assert.Equal(false, row.Payload.Payload.Body["preDecision"]);
        Assert.NotNull(row.AuthoritySnapshot);
        Assert.Equal(4, row.AuthoritySnapshot.Trace!.Count);
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
