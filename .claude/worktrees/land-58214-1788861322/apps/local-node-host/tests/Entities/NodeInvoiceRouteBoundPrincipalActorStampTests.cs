using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// MTW-2 2612-A hardening (#3037, for council-verdict pr3025 finding 3): the last gap the resolver
/// unit tests (<c>NodeCallerPartyTests</c>) do not close — an END-TO-END route-level proof that a
/// <b>bound selected-session principal's</b> canonical Party is what lands in a DURABLE financial
/// record's actor field, not merely what <c>NodeCallerParty.Resolve</c> returns in-memory.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the SAME production <see cref="InvoiceRoutes"/> handlers on a real in-process Kestrel listener
/// over a temp SQLite store (mirrors <c>InvoiceRouteTests</c>), then inserts one middleware that binds a
/// <see cref="SelectedSessionRequestPrincipal"/> onto <c>HttpContext.Features</c> — exactly the seam the
/// production selected-session authority uses. Creating an invoice through the route stamps
/// <c>createdBy = NodeCallerParty.Resolve(http)</c>; the test then reads the invoice back from the durable
/// repository and asserts the stored <see cref="Invoice.CreatedBy"/> is the bound principal's canonical
/// Party (and is NOT the operator fallback). The create response wire shape does not expose the actor, so
/// the assertion necessarily reads the persisted record — proving the wiring, not the in-memory call.
/// </para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3037")]
public sealed class NodeInvoiceRouteBoundPrincipalActorStampTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodeEfInvoiceRepository _invoices = null!;

    /// <summary>The bound member's canonical Party — a distinct real member, never the operator fallback.</summary>
    private const string BoundCanonicalParty = "party:alice";

    /// <summary>The install-constant tenant the routes filter on (mirrors InvoiceRoutes / StaticNodeTenantContext).</summary>
    private static readonly TenantId LocalTenantId =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    private const string InvoicesRoute = "/api/local-node/invoices";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Authorization.IAuthorizationContext>(
            Harborline.Api.LocalNodeHost.Tests.Packs.TestAuthorizationContext.AllowAll());
        // Ticket 205 slice 4: the record-scoped route guards resolve at the gate now, so the host that
        // registers an allow-all authorization context registers the matching allow-all gate.
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.AllowAll());

        _dir = Path.Combine(Path.GetTempPath(), "harborline-invoice-actor-stamp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "invoice-actor-stamp.db")};Pooling=False";

        // Same module set the production node composes for the invoice + JE model (no test/prod drift).
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.LocalNodeHost.Data.Audit.AuditEventEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));

        _app = builder.Build();

        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // Production-faithful invoice composition (mirrors InvoiceRouteTests). Create yields a Draft with
        // no GL post, so no accounts/periods are seeded; the posting service is composed only because
        // InvoiceRoutes.Map requires its accessor.
        _invoices = new NodeEfInvoiceRepository(_factory);
        var numbering = new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA"));
        var journalStore = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
        var postingService = new Harborline.Api.Blocks.FinancialLedger.Services.JournalPostingService(
            accounts: new NodeEfAccountResolver(_factory),
            periods:  new NodeEfPeriodResolver(_factory),
            store:    journalStore,
            gate:     TestAuthorization.AllowGate());
        var invoicePostingService = new InvoicePostingService(
            tenantContext: new ActiveTeamTenantContext(NodeTestActiveTeam.Accessor),
            invoices:      _invoices,
            numbering:     numbering,
            tax:           new NoOpTaxCalculator(),
            journals:      postingService,
            events:        null,
            journalStore:  journalStore, timeProvider: TimeProvider.System);

        // The middleware that binds the selected-session principal onto the request — the same
        // HttpContext.Features seam the production authority uses. Runs before the endpoint delegate, so
        // NodeCallerParty.Resolve(http) sees the bound principal and stamps its canonical Party.
        _app.Use(async (HttpContext http, RequestDelegate next) =>
        {
            http.Features.Set(BoundPrincipal(BoundCanonicalParty));
            await next(http);
        });

        InvoiceRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _invoices,
            numbering,
            invoicePostingService,
            NodeTestActiveTeam.Accessor, timeProvider: TimeProvider.System);

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
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact(DisplayName = "Route: a bound session principal's canonical Party is the actor stored on the durable invoice record")]
    public async Task Create_WithBoundPrincipal_StampsBoundPartyAsCreatedBy()
    {
        const string invoiceId = "inv-actor-stamp-3037";

        var body = new
        {
            id = invoiceId,
            chartId = "chart-actor-stamp",
            customerId = "party:customer",
            arAccountId = "1100",
            issueDate = "2026-06-16",
            dueDate = "2026-07-16",
            currency = "USD",
            lines = new[]
            {
                new
                {
                    description = "one-line draft",
                    quantity = 1m,
                    unitPrice = 100m,
                    incomeAccountId = "4000",
                },
            },
        };

        var response = await _client.PostAsJsonAsync(InvoicesRoute, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Read the DURABLE record back (the create response wire shape does not expose the actor).
        var persisted = await _invoices.GetAsync(LocalTenantId, new InvoiceId(invoiceId), new Instant(System.TimeProvider.System.GetUtcNow()).Value);
        Assert.NotNull(persisted);

        // The stored actor is the bound principal's canonical Party — end-to-end, through the real route.
        Assert.NotNull(persisted!.CreatedBy);
        Assert.Equal(BoundCanonicalParty, persisted.CreatedBy!.Value.Value);

        // And it is NOT the single-operator fallback: this proves the bound principal drove the stamp,
        // not the unbound bootstrap/desktop path the existing route tests already cover.
        Assert.NotEqual(NodeCallerParty.OperatorParty, persisted.CreatedBy.Value);
    }

    private static SelectedSessionRequestPrincipal BoundPrincipal(string canonicalParty) =>
        new(
            accountId: "account-" + canonicalParty,
            tenantId: LocalTenantId,
            principalUserId: new PrincipalUserId("principal-" + canonicalParty),
            canonicalParty: new CanonicalPartyReference(canonicalParty),
            membershipId: "membership-" + canonicalParty,
            membershipOwnerVersion: 1,
            pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-" + canonicalParty, 1)],
            authorizationEpoch: 1,
            sessionCorrelationId: "session-" + canonicalParty,
            coordinationCorrelationId: "coordination-" + canonicalParty);
}
