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
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.FinancialPayments.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Route-level tests for the node-local payments READ surface
/// (<see cref="PaymentRoutes"/>) — the PM-doctype offline rebind for payments.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the SAME route handlers <see cref="HostedPaymentApiEndpoint"/> registers on a
/// real in-process Kestrel listener (ephemeral loopback port), backed by a temp SQLite
/// store (plain, unencrypted — SC-1 encryption is covered separately in
/// SqlCipherFailClosedTests), driven by a real <see cref="HttpClient"/>. The test
/// invokes the production registration helper <see cref="PaymentRoutes.Map"/> (single
/// source of truth, no test/prod drift).
/// </para>
/// <para>
/// <b>Read-plane discipline.</b> The payment rows are seeded DIRECTLY into the
/// kernel-ledger <c>payments</c> table via the DbContext — modelling rows the financial
/// posting path has already durably committed. The read surface itself never writes; it
/// is verified to serve the authoritative ledger rows, scoped correctly. The posting
/// path is NOT exercised here (it is not this surface's concern).
/// </para>
/// </remarks>
public sealed class PaymentRouteTests : IClassFixture<PaymentRouteFixture>, IAsyncLifetime
{
    private readonly PaymentRouteFixture _fixture;
    private HttpClient _client => _fixture.Client;
    private IDbContextFactory<LocalNodeDbContext> _factory => _fixture.Factory;

    /// <summary>The install-constant tenant the route filters on (mirrors PaymentRoutes).</summary>
    private static readonly TenantId LocalTenantId =
        Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    public PaymentRouteTests(PaymentRouteFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        await ctx.Set<Payment>().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string Route = "/api/local-node/payments";

    // ── Fixture seeding (direct durable-row insert — NOT the posting path) ──────

    /// <summary>
    /// Inserts a Payment row directly into the kernel-ledger <c>payments</c> table,
    /// modelling a row the posting path has already committed. The read surface under
    /// test serves these rows; it does not create them.
    /// </summary>
    private async Task SeedPaymentAsync(
        string id,
        TenantId tenant,
        string chartId,
        string partyId,
        string number,
        DateOnly date,
        decimal amount,
        PaymentStatus status = PaymentStatus.Unapplied,
        PaymentDirection direction = PaymentDirection.Inbound,
        PaymentMethod method = PaymentMethod.ACH)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var payment = new Payment
        {
            Id = new PaymentId(id),
            TenantId = tenant,
            ChartId = new ChartOfAccountsId(chartId),
            Direction = direction,
            PaymentNumber = number,
            PartyId = new PartyId(partyId),
            PaymentDate = date,
            Amount = amount,
            UnappliedAmount = amount,
            Currency = "USD",
            Method = method,
            Status = status,
            CreatedAtUtc = new Instant(System.TimeProvider.System.GetUtcNow()),
            UpdatedAtUtc = new Instant(System.TimeProvider.System.GetUtcNow()),
            Version = 1,
        };
        ctx.Set<Payment>().Add(payment);
        await ctx.SaveChangesAsync();
    }

    // ── Tests ───────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Payments read: list is empty on a fresh store")]
    public async Task List_Empty_OnFreshStore()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(0, doc.GetProperty("data").GetArrayLength());
    }

    [Fact(DisplayName = "Payments read: serves the kernel-ledger payment rows")]
    public async Task List_ServesLedgerRows()
    {
        await SeedPaymentAsync("PMT-1", LocalTenantId, "CH-1", "PARTY-1", "PE-0001",
            new DateOnly(2026, 1, 10), 1500.00m);

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var data = doc.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());

        var p = data[0];
        Assert.Equal("PMT-1", p.GetProperty("id").GetString());
        Assert.Equal("CH-1", p.GetProperty("chartId").GetString());
        Assert.Equal("PARTY-1", p.GetProperty("partyId").GetString());
        Assert.Equal("PE-0001", p.GetProperty("paymentNumber").GetString());
        Assert.Equal("Inbound", p.GetProperty("direction").GetString());
        Assert.Equal("2026-01-10", p.GetProperty("paymentDate").GetString());
        Assert.Equal(1500.00m, p.GetProperty("amount").GetDecimal());
        Assert.Equal("USD", p.GetProperty("currency").GetString());
        Assert.Equal("ACH", p.GetProperty("method").GetString());
        Assert.Equal("Unapplied", p.GetProperty("status").GetString());
    }

    [Fact(DisplayName = "Payments read: list is newest-first by payment date")]
    public async Task List_NewestFirst()
    {
        await SeedPaymentAsync("PMT-OLD", LocalTenantId, "CH-1", "PARTY-1", "PE-0001",
            new DateOnly(2026, 1, 1), 100m);
        await SeedPaymentAsync("PMT-NEW", LocalTenantId, "CH-1", "PARTY-1", "PE-0002",
            new DateOnly(2026, 3, 1), 200m);

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var data = doc.GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());
        Assert.Equal("PMT-NEW", data[0].GetProperty("id").GetString());
        Assert.Equal("PMT-OLD", data[1].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "Payments read: surfaces the ledger lifecycle status verbatim")]
    public async Task List_SurfacesLedgerStatusVerbatim()
    {
        await SeedPaymentAsync("PMT-B", LocalTenantId, "CH-1", "PARTY-1", "PE-0009",
            new DateOnly(2026, 2, 2), 500m, status: PaymentStatus.Bounced);

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal("Bounced", doc.GetProperty("data")[0].GetProperty("status").GetString());
    }

    [Fact(DisplayName = "Payments read: ?chartId= filters to that chart (entity scope)")]
    public async Task List_FiltersByChart()
    {
        await SeedPaymentAsync("PMT-A", LocalTenantId, "CH-A", "PARTY-1", "PE-0001",
            new DateOnly(2026, 1, 5), 100m);
        await SeedPaymentAsync("PMT-B", LocalTenantId, "CH-B", "PARTY-1", "PE-0002",
            new DateOnly(2026, 1, 6), 200m);

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}?chartId=CH-A");
        var data = doc.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("PMT-A", data[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "Payments read: ?partyId= filters to that party")]
    public async Task List_FiltersByParty()
    {
        await SeedPaymentAsync("PMT-A", LocalTenantId, "CH-1", "PARTY-A", "PE-0001",
            new DateOnly(2026, 1, 5), 100m);
        await SeedPaymentAsync("PMT-B", LocalTenantId, "CH-1", "PARTY-B", "PE-0002",
            new DateOnly(2026, 1, 6), 200m);

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}?partyId=PARTY-B");
        var data = doc.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("PMT-B", data[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "Payments read: by-id fetches one payment")]
    public async Task GetById_ReturnsPayment()
    {
        await SeedPaymentAsync("PMT-X", LocalTenantId, "CH-1", "PARTY-1", "PE-0042",
            new DateOnly(2026, 1, 10), 750m);

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}/PMT-X");
        Assert.Equal("PMT-X", doc.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal("PE-0042", doc.GetProperty("data").GetProperty("paymentNumber").GetString());
    }

    [Fact(DisplayName = "Payments read: by-id 404 for an unknown id")]
    public async Task GetById_NotFound()
    {
        var resp = await _client.GetAsync($"{Route}/PMT-DOES-NOT-EXIST");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Tenant isolation (financial-cluster discipline; ADR 0092) ────────────────

    [Fact(DisplayName = "Payments read: a foreign-tenant payment never leaks into the list")]
    public async Task List_DoesNotLeakForeignTenant()
    {
        await SeedPaymentAsync("PMT-LOCAL", LocalTenantId, "CH-1", "PARTY-1", "PE-0001",
            new DateOnly(2026, 1, 5), 100m);
        await SeedPaymentAsync("PMT-OTHER", new TenantId("some-other-tenant"), "CH-1", "PARTY-1",
            "PE-0002", new DateOnly(2026, 1, 6), 999m);

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var data = doc.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("PMT-LOCAL", data[0].GetProperty("id").GetString());
    }

    [Fact(DisplayName = "Payments read: by-id 404 for a foreign-tenant payment (no cross-tenant read)")]
    public async Task GetById_ForeignTenant_NotFound()
    {
        await SeedPaymentAsync("PMT-OTHER", new TenantId("some-other-tenant"), "CH-1", "PARTY-1",
            "PE-0002", new DateOnly(2026, 1, 6), 999m);

        var resp = await _client.GetAsync($"{Route}/PMT-OTHER");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "Payments read: ?chartId= does not bypass the tenant filter")]
    public async Task List_ChartFilter_StillTenantScoped()
    {
        // Same chart id, different tenants — the chart filter must not surface the
        // foreign-tenant row.
        await SeedPaymentAsync("PMT-LOCAL", LocalTenantId, "CH-SHARED", "PARTY-1", "PE-0001",
            new DateOnly(2026, 1, 5), 100m);
        await SeedPaymentAsync("PMT-OTHER", new TenantId("some-other-tenant"), "CH-SHARED", "PARTY-1",
            "PE-0002", new DateOnly(2026, 1, 6), 999m);

        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}?chartId=CH-SHARED");
        var data = doc.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal("PMT-LOCAL", data[0].GetProperty("id").GetString());
    }
}

public sealed class PaymentRouteFixture : IAsyncLifetime
{
    private WebApplication _app = null!;
    private string _dir = null!;

    public HttpClient Client { get; private set; } = null!;
    public IDbContextFactory<LocalNodeDbContext> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-payment-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "payment-test.db")};Pooling=False";

        // The Payment table is contributed by PaymentsEntityModule; FinancialLedgerEntityModule
        // contributes ChartOfAccounts/LegalEntity (the chart a payment posts under). Register
        // both so LocalNodeDbContext composes the same model the production host does.
        builder.Services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, PaymentsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite(connectionString));

        _app = builder.Build();
        Factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await Factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // Map the SAME production routes (mirrors HostedPaymentApiEndpoint wiring).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PaymentRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            Factory,
            NodeTestActiveTeam.Accessor);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        Client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }
}
