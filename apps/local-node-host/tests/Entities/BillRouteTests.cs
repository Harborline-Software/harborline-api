using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Route-level tests for the node-local AP bill surface (<see cref="BillRoutes"/>) — the Cohort D
/// Step 2c AP node-flip. Bills are the primary AUTO-POSTED journal-entry source.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the SAME route handlers <see cref="HostedBillApiEndpoint"/> registers (plus the JE routes,
/// so a recorded bill's auto-posted journal entry can be read back), on a real in-process Kestrel
/// listener backed by a temp SQLite store, driven by a real <see cref="HttpClient"/>. The bill
/// posting service is the production <see cref="BillPostingService"/> composed over the SAME
/// node-resident <see cref="JournalPostingService"/> + resolvers the production host wires — so
/// create+record really posts a balanced JE into the node store (no test/prod drift).
/// </para>
/// <para>
/// <b>Create+record posts a balanced JE</b> (verified via the node JE read endpoint, incl.
/// <c>accountIds</c>), <b>void reverses</b>, <b>approve / dispute / resolve-dispute</b> lifecycle,
/// and <b>tenant isolation</b> are exercised — mirroring the JE route-test style.
/// </para>
/// </remarks>
public sealed class BillRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodeEfBillRepository _bills = null!;
    private SelectedSessionRequestPrincipal? _boundPrincipal;

    /// <summary>The install-constant tenant the routes filter on (mirrors BillRoutes / StaticNodeTenantContext).</summary>
    private static readonly TenantId LocalTenantId =
        Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    private const string BillsRoute = "/api/local-node/bills";
    private const string JeRoute = "/api/local-node/journal-entries";
    private const string BoundCanonicalParty = "party:bound-member";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-bill-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "bill-test.db")};Pooling=False";

        // Bill is contributed by ApEntityModule; JournalEntry + GLAccount by FinancialLedgerEntityModule.
        // Register both so LocalNodeDbContext composes the same model the production host does.
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAp.Data.ApEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.LocalNodeHost.Data.Audit.AuditEventEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt =>
            opt.UseSqlite(connectionString));

        _app = builder.Build();

        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // Production-faithful composition: the node bill repo + the node posting service over the
        // node resolvers + the node journal store. RecordAsync posts the bill's auto-JE through this
        // SAME JournalPostingService into the SAME NodeEfJournalStore the JE read routes serve.
        _bills = new NodeEfBillRepository(_factory);
        var journalStore = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
        var postingService = new JournalPostingService(
            accounts: new NodeEfAccountResolver(_factory),
            periods:  new NodeEfPeriodResolver(_factory),
            store:    journalStore,
            gate:     TestAuthorization.AllowGate());
        var billPostingService = new BillPostingService(
            tenantContext: new Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext(NodeTestActiveTeam.Accessor),
            bills:         _bills,
            tax:           new NoOpTaxCalculator(),
            journals:      postingService, timeProvider: TimeProvider.System);

        var billRepoAccessor = _bills;
        var billPostingAccessor = billPostingService;

        // The JE read surface (so a recorded bill's JE can be read back) — same production composition.
        var jeReadModel = new InMemoryJournalEntryQueryReadModel(journalStore);
        var jePostingAccessor = postingService;

        // Seed the GL accounts the bill auto-JE posts to. The bill JE is UNSCOPED (no chart), so
        // period-gating is skipped — only account validity (Phase 3) applies. AP (2000) + two
        // expense accounts (6000/6100) cover the posting tests. Postable accounts.
        await SeedAccountAsync("2000", "Accounts Payable", GLAccountType.Liability, AccountSubtype.AccountsPayable);
        await SeedAccountAsync("6000", "Repairs Expense", GLAccountType.Expense, AccountSubtype.OperatingExpense);
        await SeedAccountAsync("6100", "Utilities Expense", GLAccountType.Expense, AccountSubtype.OperatingExpense);

        // Publish an opted-in server-revalidated request principal through the production seam.
        // Tests default to the genuine desktop plane, where no selected session is bound.
        _app.Use(async (HttpContext http, RequestDelegate next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            if (_boundPrincipal is not null)
            {
                http.Features.Set(_boundPrincipal);
            }
            await next(http);
        });

        // Map the SAME production routes (mirrors the hosted endpoints' wiring).
        var deviceReachable = _app.MapDeviceReachableProductDataGroup();
        BillRoutes.Map(deviceReachable, billRepoAccessor, billPostingAccessor, NodeTestActiveTeam.Accessor, timeProvider: TimeProvider.System);
        JournalEntryRoutes.Map(
            deviceReachable,
            jeReadModel,
            journalStore,
            jePostingAccessor,
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
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private async Task SeedAccountAsync(
        string code,
        string name,
        GLAccountType type,
        AccountSubtype subtype,
        string chartId = "CH-1",
        bool isPostable = true)
    {
        var account = GLAccount.Create(
            id:           new GLAccountId(code),
            chartId:      new ChartOfAccountsId(chartId),
            code:         code,
            name:         name,
            type:         type,
            subtype:      subtype,
            currency:     "USD",
            isPostable:   isPostable,
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()));

        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<GLAccount>().Add(account);
        await ctx.SaveChangesAsync();
    }

    private static object NewBillBody(
        string billNumber = "VND-001",
        string vendorId = "vendor-1",
        string apAccountId = "2000",
        string chartId = "CH-1",
        decimal lineAmount = 250m,
        string lineAccount = "6000",
        string? id = null) => new
    {
        id,
        chartId,
        billNumber,
        vendorId,
        apAccountId,
        billDate = "2026-03-01",
        dueDate = "2026-03-31",
        lines = new[]
        {
            new { description = "Repair work", quantity = 1m, unitPrice = lineAmount, debitAccountId = lineAccount, taxCodeId = (string?)null },
        },
    };

    private static JsonElement Data(JsonElement doc) => doc.GetProperty("data");

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

    // ── Create + record → posts a balanced JE ─────────────────────────────────────

    [Fact(DisplayName = "Bill create+record: a bill is recorded (Received) and returns a journalEntryId")]
    public async Task Create_RecordsBill_AndPostsJournalEntry()
    {
        var resp = await _client.PostAsJsonAsync(BillsRoute, NewBillBody(lineAmount: 250m));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var d = Data(doc);
        Assert.Equal("Received", d.GetProperty("status").GetString());
        Assert.Equal(250d, d.GetProperty("total").GetDouble());
        Assert.Equal(250d, d.GetProperty("balance").GetDouble());
        var jeId = d.GetProperty("journalEntryId").GetString();
        Assert.False(string.IsNullOrEmpty(jeId), "a recorded bill must carry its posted journalEntryId");
    }

    [Fact(DisplayName = "Bill create+record: the auto-posted JE is BALANCED (Debit expense / Credit AP) and readable via the node JE endpoint")]
    public async Task Create_PostsBalancedJournalEntry_ReadableViaJeEndpoint()
    {
        var resp = await _client.PostAsJsonAsync(BillsRoute, NewBillBody(lineAmount: 400m, lineAccount: "6000", apAccountId: "2000"));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var jeId = Data(created).GetProperty("journalEntryId").GetString();

        // The JE is durably persisted on the node and readable via the JE read endpoint.
        var je = await _client.GetFromJsonAsync<JsonElement>($"{JeRoute}/{jeId}");
        var jed = Data(je);
        Assert.Equal("Posted", jed.GetProperty("status").GetString());
        // Balanced: total debits == total credits == 400 (Debit 6000 expense / Credit 2000 AP).
        Assert.Equal(400d, jed.GetProperty("totalDebits").GetDouble());
        Assert.Equal(400d, jed.GetProperty("totalCredits").GetDouble());

        // accountIds surface both the expense line account and the AP control account.
        var accountIds = jed.GetProperty("accountIds").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("6000", accountIds);
        Assert.Contains("2000", accountIds);

        // The single expense line debits 6000; AP (2000) is credited the total.
        var lines = jed.GetProperty("lines").EnumerateArray().ToList();
        var apLine = lines.Single(l => l.GetProperty("accountId").GetString() == "2000");
        Assert.Equal(400d, apLine.GetProperty("credit").GetDouble());
        Assert.Equal(0d, apLine.GetProperty("debit").GetDouble());
        var expenseLine = lines.Single(l => l.GetProperty("accountId").GetString() == "6000");
        Assert.Equal(400d, expenseLine.GetProperty("debit").GetDouble());
    }

    [Fact(DisplayName = "Bill create+record: the recorded bill is durable + readable by id and in the chart list")]
    public async Task Create_RecordedBill_IsDurableAndListed()
    {
        var resp = await _client.PostAsJsonAsync(BillsRoute, NewBillBody(billNumber: "VND-DURABLE"));
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var id = Data(created).GetProperty("id").GetString();

        var detail = await _client.GetFromJsonAsync<JsonElement>($"{BillsRoute}/{id}");
        Assert.Equal("VND-DURABLE", Data(detail).GetProperty("billNumber").GetString());
        Assert.Equal("Received", Data(detail).GetProperty("status").GetString());

        var list = await _client.GetFromJsonAsync<JsonElement>($"{BillsRoute}?chartId=CH-1");
        var ids = Data(list).EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
        Assert.Contains(id, ids);
    }

    [Fact(DisplayName = "Bill create: a line debiting an unknown account is rejected (journal_rejected) — the auto-JE fails account-validity")]
    public async Task Create_UnknownLineAccount_JournalRejected()
    {
        // 9999 was never seeded — the auto-JE's Phase-3 account-validity fails, so RecordAsync
        // returns JournalRejected → 400 journal_rejected.
        var resp = await _client.PostAsJsonAsync(BillsRoute, NewBillBody(lineAccount: "9999"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("journal_rejected", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "Bill create: missing chartId is rejected 400 chart_id_required")]
    public async Task Create_MissingChart_Rejected()
    {
        var body = new
        {
            billNumber = "VND-X",
            vendorId = "vendor-1",
            apAccountId = "2000",
            billDate = "2026-03-01",
            dueDate = "2026-03-31",
            lines = new[] { new { description = "x", quantity = 1m, unitPrice = 10m, debitAccountId = "6000" } },
        };
        var resp = await _client.PostAsJsonAsync(BillsRoute, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("chart_id_required", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "Bill create: no lines is rejected 400 no_lines")]
    public async Task Create_NoLines_Rejected()
    {
        var body = new
        {
            chartId = "CH-1",
            billNumber = "VND-EMPTY",
            vendorId = "vendor-1",
            apAccountId = "2000",
            billDate = "2026-03-01",
            dueDate = "2026-03-31",
            lines = Array.Empty<object>(),
        };
        var resp = await _client.PostAsJsonAsync(BillsRoute, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_lines", doc.GetProperty("error").GetString());
    }

    // ── Void → reversing JE ───────────────────────────────────────────────────────

    [Fact(DisplayName = "Bill void: voids a recorded bill, posts a reversing JE, balance → 0")]
    public async Task Void_RecordedBill_PostsReversingJournalEntry()
    {
        var created = await (await _client.PostAsJsonAsync(BillsRoute, NewBillBody(lineAmount: 300m)))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = Data(created).GetProperty("id").GetString();
        var originalJeId = Data(created).GetProperty("journalEntryId").GetString();

        var voidResp = await _client.PostAsJsonAsync($"{BillsRoute}/{id}/void", new { reason = "duplicate" });
        Assert.Equal(HttpStatusCode.OK, voidResp.StatusCode);
        var voided = await voidResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Voided", Data(voided).GetProperty("status").GetString());
        Assert.Equal(0d, Data(voided).GetProperty("balance").GetDouble());
        var voidedByEntryId = Data(voided).GetProperty("voidedByEntryId").GetString();
        Assert.False(string.IsNullOrEmpty(voidedByEntryId));
        Assert.NotEqual(originalJeId, voidedByEntryId);

        // The reversing JE is durable + balanced (swapped debit/credit vs the original).
        var revJe = await _client.GetFromJsonAsync<JsonElement>($"{JeRoute}/{voidedByEntryId}");
        var revd = Data(revJe);
        Assert.Equal("Posted", revd.GetProperty("status").GetString());
        Assert.Equal(300d, revd.GetProperty("totalDebits").GetDouble());
        Assert.Equal(300d, revd.GetProperty("totalCredits").GetDouble());
        // AP is now DEBITED on the reversal (it was credited on the record).
        var apLine = revd.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("accountId").GetString() == "2000");
        Assert.Equal(300d, apLine.GetProperty("debit").GetDouble());
    }

    [Fact(DisplayName = "Bill void: 404 for an unknown bill id")]
    public async Task Void_UnknownBill_NotFound()
    {
        var resp = await _client.PostAsJsonAsync($"{BillsRoute}/does-not-exist/void", new { reason = "x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Approve / dispute / resolve-dispute ────────────────────────────────────────

    [Fact(DisplayName = "Bill approve: a bound session principal is the approver stored on the durable bill")]
    public async Task Approve_WithBoundPrincipal_StampsBoundPartyOnDurableBill()
    {
        _boundPrincipal = BoundPrincipal(BoundCanonicalParty);

        var created = await (await _client.PostAsJsonAsync(BillsRoute, NewBillBody()))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = Data(created).GetProperty("id").GetString();

        var resp = await _client.PostAsJsonAsync($"{BillsRoute}/{id}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Approved", Data(doc).GetProperty("status").GetString());

        var persisted = await _bills.GetAsync(LocalTenantId, new Harborline.Api.Blocks.FinancialAp.Models.BillId(id!), new Instant(System.TimeProvider.System.GetUtcNow()).Value);
        Assert.NotNull(persisted);
        Assert.Equal(BoundCanonicalParty, persisted!.ApprovedByUserId);
        Assert.Equal(BoundCanonicalParty, persisted.UpdatedBy!.Value.Value);
    }

    [Fact(DisplayName = "Bill approve: no bound principal stamps the operator on the durable bill")]
    public async Task Approve_WithNoBoundPrincipal_StampsOperatorPartyOnDurableBill()
    {
        var created = await (await _client.PostAsJsonAsync(BillsRoute, NewBillBody()))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = Data(created).GetProperty("id").GetString();

        var response = await _client.PostAsJsonAsync($"{BillsRoute}/{id}/approve", new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var persisted = await _bills.GetAsync(LocalTenantId, new Harborline.Api.Blocks.FinancialAp.Models.BillId(id!), new Instant(System.TimeProvider.System.GetUtcNow()).Value);
        Assert.NotNull(persisted);
        Assert.Equal(ActiveTeamAuthorizationContext.LocalUserId, persisted!.ApprovedByUserId);
    }

    [Fact(DisplayName = "Bill approve: no bound principal accepts the operator id in the request body")]
    public async Task Approve_WithNoBoundPrincipalAndOperatorBody_Succeeds()
    {
        var created = await (await _client.PostAsJsonAsync(BillsRoute, NewBillBody()))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = Data(created).GetProperty("id").GetString();

        var response = await _client.PostAsJsonAsync(
            $"{BillsRoute}/{id}/approve",
            new { approvedByUserId = ActiveTeamAuthorizationContext.LocalUserId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact(DisplayName = "Bill approve: a body approver different from the bound principal is refused without changing the durable bill")]
    public async Task Approve_WithDifferentBodyApprover_RefusesAndLeavesDurableBillUnapproved()
    {
        _boundPrincipal = BoundPrincipal(BoundCanonicalParty);

        var created = await (await _client.PostAsJsonAsync(
                BillsRoute,
                NewBillBody(billNumber: "VND-APPROVER-MISMATCH")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = Data(created).GetProperty("id").GetString();

        var response = await _client.PostAsJsonAsync(
            $"{BillsRoute}/{id}/approve",
            new { approvedByUserId = "party:asserted-other-member" });

        // The durable fact is the load-bearing assertion: the forged identity must never be recorded.
        var persisted = await _bills.GetAsync(LocalTenantId, new Harborline.Api.Blocks.FinancialAp.Models.BillId(id!), new Instant(System.TimeProvider.System.GetUtcNow()).Value);
        Assert.NotNull(persisted);
        Assert.Equal(Harborline.Api.Blocks.FinancialAp.Models.BillStatus.Received, persisted!.Status);
        Assert.Null(persisted.ApprovedByUserId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var refusal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("approver_identity_mismatch", refusal.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "Bill dispute → resolve-dispute: round-trips Received → Disputed → Received (no GL change)")]
    public async Task Dispute_ThenResolve_RoundTrips()
    {
        var created = await (await _client.PostAsJsonAsync(BillsRoute, NewBillBody()))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = Data(created).GetProperty("id").GetString();

        var dispute = await _client.PostAsJsonAsync($"{BillsRoute}/{id}/dispute", new { reason = "wrong amount" });
        Assert.Equal(HttpStatusCode.OK, dispute.StatusCode);
        Assert.Equal("Disputed", Data(await dispute.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        var resolve = await _client.PostAsJsonAsync($"{BillsRoute}/{id}/resolve-dispute", new { resolveTo = "Received" });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        Assert.Equal("Received", Data(await resolve.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact(DisplayName = "Bill approve: a Draft/never-recorded id 404s (no such recorded bill)")]
    public async Task Approve_UnknownBill_NotFound()
    {
        var resp = await _client.PostAsJsonAsync($"{BillsRoute}/nope/approve", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── List filters ───────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Bill list: ?status=open returns only open bills (a voided bill is excluded)")]
    public async Task List_OpenStatus_ExcludesVoided()
    {
        var openBill = await (await _client.PostAsJsonAsync(BillsRoute, NewBillBody(billNumber: "VND-OPEN")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var voidedBill = await (await _client.PostAsJsonAsync(BillsRoute, NewBillBody(billNumber: "VND-VOID")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var voidedId = Data(voidedBill).GetProperty("id").GetString();
        await _client.PostAsJsonAsync($"{BillsRoute}/{voidedId}/void", new { reason = "x" });

        var list = await _client.GetFromJsonAsync<JsonElement>($"{BillsRoute}?chartId=CH-1&status=open");
        var ids = Data(list).EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
        Assert.Contains(Data(openBill).GetProperty("id").GetString(), ids);
        Assert.DoesNotContain(voidedId, ids);
    }

    [Fact(DisplayName = "Bill list: requires chartId (400 chart_id_required)")]
    public async Task List_MissingChart_Rejected()
    {
        var resp = await _client.GetAsync(BillsRoute);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("chart_id_required", doc.GetProperty("error").GetString());
    }

    // ── Tenant isolation (financial-cluster discipline; ADR 0092) ───────────────────

    [Fact(DisplayName = "Bill read: a foreign-tenant bill never leaks into the chart list")]
    public async Task List_DoesNotLeakForeignTenant()
    {
        // Record a local bill via the route.
        var local = await (await _client.PostAsJsonAsync(BillsRoute, NewBillBody(billNumber: "VND-LOCAL")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var localId = Data(local).GetProperty("id").GetString();

        // Seed a foreign-tenant bill DIRECTLY into the store (the route always pins "local").
        await SeedForeignTenantBillAsync("foreign-bill", new TenantId("other-tenant"), "CH-1");

        var list = await _client.GetFromJsonAsync<JsonElement>($"{BillsRoute}?chartId=CH-1");
        var ids = Data(list).EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
        Assert.Contains(localId, ids);
        Assert.DoesNotContain("foreign-bill", ids);
    }

    [Fact(DisplayName = "Bill read: by-id 404 for a foreign-tenant bill (no cross-tenant read)")]
    public async Task GetById_ForeignTenant_NotFound()
    {
        await SeedForeignTenantBillAsync("foreign-bill", new TenantId("other-tenant"), "CH-1");
        var resp = await _client.GetAsync($"{BillsRoute}/foreign-bill");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "Bill void: a foreign-tenant bill cannot be voided (opaque 404)")]
    public async Task Void_ForeignTenant_NotFound()
    {
        await SeedForeignTenantBillAsync("foreign-bill", new TenantId("other-tenant"), "CH-1");
        var resp = await _client.PostAsJsonAsync($"{BillsRoute}/foreign-bill/void", new { reason = "x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// Seeds a Received bill under a NON-local tenant directly via the repository (the routes always
    /// pin "local", so this is the only way to plant a foreign-tenant row for the isolation tests).
    /// </summary>
    private async Task SeedForeignTenantBillAsync(string id, TenantId tenant, string chartId)
    {
        var billId = new Harborline.Api.Blocks.FinancialAp.Models.BillId(id);
        var line = Harborline.Api.Blocks.FinancialAp.Models.BillLine.Create(
            billId:         billId,
            lineNumber:     1,
            description:    "foreign",
            quantity:       1m,
            unitPrice:      100m,
            debitAccountId: new GLAccountId("6000"));
        var bill = Harborline.Api.Blocks.FinancialAp.Models.Bill.Create(
            tenantId:    tenant,
            chartId:     new ChartOfAccountsId(chartId),
            billNumber:  "FOREIGN-001",
            vendorId:    new Harborline.Api.Blocks.People.Foundation.Models.PartyId("vendor-x"),
            billDate:    new DateOnly(2026, 3, 1),
            dueDate:     new DateOnly(2026, 3, 31),
            lines:       new[] { line },
            apAccountId: new GLAccountId("2000"),
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()),
            id:          billId);
        await _bills.UpsertAsync(tenant, bill, new Instant(System.TimeProvider.System.GetUtcNow()).Value);
    }
}
