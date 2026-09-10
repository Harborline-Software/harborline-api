using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Seeds;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// ADR 0115 gap C4 — route-level tests for the node-local chart-of-accounts
/// seed endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the SAME route handlers <see cref="HostedChartOfAccountsApiEndpoint"/>
/// and <see cref="HostedEntityApiEndpoint"/> register on a real in-process
/// Kestrel listener, backed by a temp SQLite store (plain, unencrypted — SC-1
/// covered separately). Drives them via a real <see cref="HttpClient"/>.
/// </para>
/// <para>
/// The entity endpoint is co-registered so tests can create an entity (offline
/// POST /api/local-node/entities) before seeding the CoA — exactly as the
/// onboarding wizard does.
/// </para>
/// </remarks>
public sealed class ChartOfAccountsRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-coa-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        // Pooling=False: keep this fixture's SQLite handles out of the process-global pool so a sibling test
        // class's ClearAllPools() cannot dispose a handle mid-Open() (the ObjectDisposedException race,
        // bug-20260702-8012e553). See ChartOfAccountsManagementRouteTests for the full note.
        var connectionString = $"Data Source={Path.Combine(_dir, "coa-test.db")};Pooling=False";

        builder.Services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        // Ticket 151: the entity POST is now permission-gated; grant-all keeps these route tests on their pre-gate behaviour.
        builder.Services.AddSingleton<Harborline.Api.Foundation.Authorization.IAuthorizationContext>(Harborline.Api.LocalNodeHost.Tests.Packs.TestAuthorizationContext.AllowAll());
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.AllowAll());
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite(connectionString));

        _app = builder.Build();

        var factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // Register both the entity route (to create a pre-existing entity)
        // and the CoA seed route (the subject under test).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        var deviceReachable = _app.MapDeviceReachableProductDataGroup();
        EntityRoutes.Map(deviceReachable, factory, NodeTestActiveTeam.Accessor,
            new Data.Entities.NodeEntityWriter(factory, Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting, Data.Entities.TestNodeRecordSchemas.Fresh(), Authorization.TestAuthorization.AllowGate()),
            TimeProvider.System);
        ChartOfAccountsRoutes.Map(
            deviceReachable,
            factory,
            NodeTestActiveTeam.Accessor,
            TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        // No ClearAllPools(): Pooling=False already closes/releases the handles on app dispose, and a
        // process-global pool clear is a cross-test race hazard (bug-20260702-8012e553).
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private const string EntityRoute = "/api/local-node/entities";
    private const string SeedRoute = "/api/local-node/chart-of-accounts/seed-from-template";

    /// <summary>
    /// Creates an entity via the entity route and returns its id string.
    /// </summary>
    private async Task<string> CreateEntityAsync(string legalName = "Test Entity LLC")
    {
        var resp = await _client.PostAsJsonAsync(EntityRoute, new { legalName });
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("id").GetString()!;
    }

    [Fact(DisplayName = "CoA seed: rental-real-estate template seeds expected account count")]
    public async Task Seed_RentalRealEstate_SeedsExpectedAccounts()
    {
        var entityId = await CreateEntityAsync();
        var resp = await _client.PostAsJsonAsync(SeedRoute, new
        {
            entityId,
            templateId = "rental-real-estate",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrWhiteSpace(doc.GetProperty("chartId").GetString()));
        Assert.Equal(entityId, doc.GetProperty("entityId").GetString());
        Assert.Equal(
            DefaultChartTemplates.RentalRealEstate.Accounts.Count,
            doc.GetProperty("accountsSeeded").GetInt32());
    }

    [Fact(DisplayName = "CoA seed: pm-pack template seeds accounts and reports template name")]
    public async Task Seed_PmPack_SeedsAccounts()
    {
        var entityId = await CreateEntityAsync("ScorpMgmt LLC");
        var resp = await _client.PostAsJsonAsync(SeedRoute, new
        {
            entityId,
            templateId = "pm-pack",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            DefaultChartTemplates.ScorpManagementCo.Accounts.Count,
            doc.GetProperty("accountsSeeded").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(doc.GetProperty("templateName").GetString()));
    }

    [Fact(DisplayName = "CoA seed: skip template seeds 0 accounts")]
    public async Task Seed_Skip_SeedsZeroAccounts()
    {
        var entityId = await CreateEntityAsync("Manual Corp");
        var resp = await _client.PostAsJsonAsync(SeedRoute, new
        {
            entityId,
            templateId = "skip",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, doc.GetProperty("accountsSeeded").GetInt32());
    }

    [Fact(DisplayName = "CoA seed: chart is linked to the correct entity")]
    public async Task Seed_ChartLinkedToEntity()
    {
        var entityId = await CreateEntityAsync("Linked Entity LLC");
        var resp = await _client.PostAsJsonAsync(SeedRoute, new
        {
            entityId,
            templateId = "rental-real-estate",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(entityId, doc.GetProperty("entityId").GetString());
    }

    [Fact(DisplayName = "CoA seed: default templateId is rental-real-estate when omitted")]
    public async Task Seed_DefaultTemplate_IsRentalRealEstate()
    {
        var entityId = await CreateEntityAsync("Default Template LLC");
        // POST body with no templateId — should default to rental-real-estate
        var resp = await _client.PostAsJsonAsync(SeedRoute, new { entityId });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            DefaultChartTemplates.RentalRealEstate.Accounts.Count,
            doc.GetProperty("accountsSeeded").GetInt32());
    }

    [Fact(DisplayName = "CoA seed: custom chartName appears in response template name")]
    public async Task Seed_CustomChartName_Accepted()
    {
        var entityId = await CreateEntityAsync("Custom Name LLC");
        var resp = await _client.PostAsJsonAsync(SeedRoute, new
        {
            entityId,
            templateId = "rental-real-estate",
            chartName = "My Custom Chart 2026",
        });

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        // Response carries templateName from the resolved template, not chartName —
        // that's by design (chartName is a stored DB field, not echoed in the response).
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact(DisplayName = "CoA seed: unknown entity returns 400")]
    public async Task Seed_UnknownEntity_Returns400()
    {
        var resp = await _client.PostAsJsonAsync(SeedRoute, new
        {
            entityId = Guid.NewGuid().ToString(),
            templateId = "rental-real-estate",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "CoA seed: unknown templateId returns 400")]
    public async Task Seed_UnknownTemplateId_Returns400()
    {
        var entityId = await CreateEntityAsync("Template Error LLC");
        var resp = await _client.PostAsJsonAsync(SeedRoute, new
        {
            entityId,
            templateId = "nonexistent-template",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "CoA seed: missing entityId returns 400")]
    public async Task Seed_MissingEntityId_Returns400()
    {
        var resp = await _client.PostAsJsonAsync(SeedRoute, new
        {
            templateId = "rental-real-estate",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "CoA seed: second seed on same entity creates a second chart (append semantics)")]
    public async Task Seed_SecondSeed_AppendsChart()
    {
        var entityId = await CreateEntityAsync("Double-Seed LLC");

        var resp1 = await _client.PostAsJsonAsync(SeedRoute, new { entityId, templateId = "rental-real-estate" });
        var resp2 = await _client.PostAsJsonAsync(SeedRoute, new { entityId, templateId = "skip" });

        Assert.Equal(HttpStatusCode.Created, resp1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, resp2.StatusCode);

        var doc1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        var doc2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        // The two charts should have distinct IDs.
        Assert.NotEqual(
            doc1.GetProperty("chartId").GetString(),
            doc2.GetProperty("chartId").GetString());
    }
}
