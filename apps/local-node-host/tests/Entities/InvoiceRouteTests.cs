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

using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Route-level tests for the node-local AR invoice surface (<see cref="InvoiceRoutes"/>) — the
/// Cohort D Step 2b AR node-flip. Issuing an invoice posts a balanced journal entry (Debit AR /
/// Credit Income) through the node-resident posting service.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the SAME route handlers <see cref="HostedInvoiceApiEndpoint"/> registers (plus the JE
/// routes, so an issued invoice's auto-posted journal entry can be read back), on a real in-process
/// Kestrel listener backed by a temp SQLite store, driven by a real <see cref="HttpClient"/>. The
/// invoice posting service is the production <see cref="InvoicePostingService"/> composed over the
/// SAME node-resident <see cref="JournalPostingService"/> + resolvers the production host wires — so
/// issuing really posts a balanced JE into the node store (no test/prod drift). The number is minted
/// at create time by the durable <see cref="NodeEfInvoiceNumberingService"/>.
/// </para>
/// <para>
/// <b>Create yields a Draft</b> (no GL post; carries a minted canonical number), <b>issue posts a
/// balanced JE</b> (verified via the node JE read endpoint, incl. <c>accountIds</c>), <b>void
/// reverses</b>, <b>write-off</b> posts a bad-debt JE, and <b>tenant isolation</b> are exercised —
/// mirroring the bill route-test style.
/// </para>
/// </remarks>
public sealed class InvoiceRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodeEfInvoiceRepository _invoices = null!;
    private MutableAuthorizationContext _authorization = null!;

    /// <summary>The install-constant tenant the routes filter on (mirrors InvoiceRoutes / StaticNodeTenantContext).</summary>
    private static readonly TenantId LocalTenantId =
        Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    private const string InvoicesRoute = "/api/local-node/invoices";
    private const string JeRoute = "/api/local-node/journal-entries";

    [Fact(DisplayName = "Invoice route: a member without write permission gets 403 and create does not run")]
    public async Task Create_WithoutRecordsWrite_Returns403_AndDoesNotWrite()
    {
        _authorization.DenyAll();

        var response = await _client.PostAsJsonAsync(InvoicesRoute, NewInvoiceBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Equal(TeamRolePermissions.RecordsWrite, body.GetProperty("permission").GetString());
        Assert.Empty(await _invoices.ListByChartAsync(LocalTenantId, new ChartOfAccountsId("CH-1")));
    }

    [Fact(DisplayName = "Invoice route: read permission narrowing and member revocation deny the next request")]
    public async Task Read_PermissionNarrowedOrRevoked_IsDeniedOnNextRequest()
    {
        _authorization.Allow(TeamRolePermissions.RecordsRead);
        var allowed = await _client.GetAsync($"{InvoicesRoute}?chartId=CH-1");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        _authorization.DenyAll();
        var narrowed = await _client.GetAsync($"{InvoicesRoute}?chartId=CH-1");
        Assert.Equal(HttpStatusCode.Forbidden, narrowed.StatusCode);
        var body = await narrowed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TeamRolePermissions.RecordsRead, body.GetProperty("permission").GetString());

        var revoked = await _client.GetAsync($"{InvoicesRoute}?chartId=CH-1");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _authorization = new MutableAuthorizationContext();
        builder.Services.AddSingleton<IAuthorizationContext>(_authorization);
        // Ticket 205 slice 4: the route guards resolve at the gate. It follows the SAME mutable holding
        // set this host already flips, so a test that narrows the caller's permissions narrows the decision.
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
            permission => _authorization.HasPermission(permission)));

        _dir = Path.Combine(Path.GetTempPath(), "harborline-invoice-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "invoice-test.db")};Pooling=False";

        // Invoice is contributed by ArEntityModule; JournalEntry + GLAccount by FinancialLedgerEntityModule.
        // Register both so LocalNodeDbContext composes the same model the production host does.
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
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

        // Production-faithful composition: the node invoice repo + numbering service + the node posting
        // service over the node resolvers + the node journal store. IssueAsync posts the invoice's JE
        // through this SAME JournalPostingService into the SAME NodeEfJournalStore the JE read routes serve.
        _invoices = new NodeEfInvoiceRepository(_factory);
        var numbering = new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA"));
        var journalStore = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
        var postingService = new JournalPostingService(
            accounts: new NodeEfAccountResolver(_factory),
            periods:  new NodeEfPeriodResolver(_factory),
            store:    journalStore,
            gate:     TestAuthorization.AllowGate());
        var invoicePostingService = new InvoicePostingService(
            tenantContext: new Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamTenantContext(NodeTestActiveTeam.Accessor),
            invoices:      _invoices,
            numbering:     numbering,
            tax:           new NoOpTaxCalculator(),
            journals:      postingService,
            events:        null,
            journalStore:  journalStore, timeProvider: TimeProvider.System);

        var invoiceRepoAccessor = _invoices;
        var invoiceNumberingAccessor = numbering;
        var invoicePostingAccessor = invoicePostingService;

        // The JE read surface (so an issued invoice's JE can be read back) — same production composition.
        var jeReadModel = new InMemoryJournalEntryQueryReadModel(journalStore);
        var jePostingAccessor = postingService;

        // Seed the GL accounts the invoice JEs post to. AR (1100) + Income (4000) cover the issue/void
        // posting tests; BadDebt expense (6900) covers write-off.
        await SeedAccountAsync("1100", "Accounts Receivable", GLAccountType.Asset, AccountSubtype.AccountsReceivable);
        await SeedAccountAsync("4000", "Service Income", GLAccountType.Revenue, AccountSubtype.OperatingIncome);
        await SeedAccountAsync("6900", "Bad Debt Expense", GLAccountType.Expense, AccountSubtype.OperatingExpense);

        // Seed an OPEN fiscal period covering the test issue dates. The invoice JE is SCOPED to the
        // invoice's chart (unlike the AP bill auto-JE, which is unscoped), so the posting service's
        // Phase-4 period-gating runs and REQUIRES an open period for the entry date — faithful to how
        // a real node posts invoice JEs.
        await SeedOpenPeriodAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        // Map the SAME production routes (mirrors the hosted endpoints' wiring).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        var deviceReachable = _app.MapDeviceReachableProductDataGroup();
        InvoiceRoutes.Map(
            deviceReachable,
            invoiceRepoAccessor,
            invoiceNumberingAccessor,
            invoicePostingAccessor,
            NodeTestActiveTeam.Accessor, timeProvider: TimeProvider.System);
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

    /// <summary>
    /// Seeds an OPEN <see cref="FiscalPeriod"/> covering [<paramref name="start"/>, <paramref name="end"/>]
    /// for <paramref name="chartId"/> so the posting service's Phase-4 period-gating passes for the
    /// chart-scoped invoice JEs (mirrors the JE route tests' seed helper).
    /// </summary>
    private async Task SeedOpenPeriodAsync(DateOnly start, DateOnly end, string chartId = "CH-1")
    {
        var period = FiscalPeriod.CreateOpen(
            id:           FiscalPeriodId.NewId(),
            chartId:      new ChartOfAccountsId(chartId),
            fiscalYearId: new FiscalYearId("FY-2026"),
            kind:         FiscalPeriodKind.Monthly,
            label:        $"{start:yyyy-MM}",
            startDate:    start,
            endDate:      end,
            createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()));

        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<FiscalPeriod>().Add(period);
        await ctx.SaveChangesAsync();
    }

    private static object NewInvoiceBody(
        string customerId = "customer-1",
        string arAccountId = "1100",
        string chartId = "CH-1",
        decimal lineAmount = 250m,
        string lineAccount = "4000",
        string? externalRef = null,
        string? id = null) => new
    {
        id,
        chartId,
        customerId,
        arAccountId,
        issueDate = "2026-03-01",
        dueDate = "2026-03-31",
        externalRef,
        lines = new[]
        {
            new { description = "Consulting", quantity = 1m, unitPrice = lineAmount, incomeAccountId = lineAccount, taxCodeId = (string?)null },
        },
    };

    private static JsonElement Data(JsonElement doc) => doc.GetProperty("data");

    private async Task<JsonElement> CreateDraftAsync(object? body = null)
    {
        var resp = await _client.PostAsJsonAsync(InvoicesRoute, body ?? NewInvoiceBody());
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ── Create → Draft (mints number, no GL post) ─────────────────────────────────

    [Fact(DisplayName = "Invoice create: yields a Draft with a minted canonical number and NO journalEntryId")]
    public async Task Create_YieldsDraft_WithMintedNumber_NoJournalEntry()
    {
        var doc = await CreateDraftAsync(NewInvoiceBody(lineAmount: 250m));
        var d = Data(doc);

        Assert.Equal("Draft", d.GetProperty("status").GetString());
        Assert.Equal(250d, d.GetProperty("total").GetDouble());
        Assert.Equal(250d, d.GetProperty("balance").GetDouble());

        // Canonical INV-YYYY-MM-DD-{Replica}-{NNNN} minted at create time.
        var number = d.GetProperty("invoiceNumber").GetString();
        Assert.StartsWith("INV-2026-03-01-AA-", number);

        // No GL post yet — journalEntryId is null on a Draft.
        Assert.True(d.GetProperty("journalEntryId").ValueKind == JsonValueKind.Null);
    }

    [Fact(DisplayName = "Invoice create: two drafts in the same chart get distinct monotonic numbers (no unique-index collision)")]
    public async Task Create_TwoDrafts_GetDistinctNumbers()
    {
        var first = Data(await CreateDraftAsync()).GetProperty("invoiceNumber").GetString();
        var second = Data(await CreateDraftAsync()).GetProperty("invoiceNumber").GetString();

        Assert.NotEqual(first, second);
        Assert.EndsWith("-0001", first);
        Assert.EndsWith("-0002", second);
    }

    [Fact(DisplayName = "Invoice create: missing chartId is rejected 400 chart_id_required")]
    public async Task Create_MissingChart_Rejected()
    {
        var body = new
        {
            customerId = "customer-1",
            arAccountId = "1100",
            issueDate = "2026-03-01",
            dueDate = "2026-03-31",
            lines = new[] { new { description = "x", quantity = 1m, unitPrice = 10m, incomeAccountId = "4000" } },
        };
        var resp = await _client.PostAsJsonAsync(InvoicesRoute, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("chart_id_required", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "Invoice create: no lines is rejected 400 no_lines")]
    public async Task Create_NoLines_Rejected()
    {
        var body = new
        {
            chartId = "CH-1",
            customerId = "customer-1",
            arAccountId = "1100",
            issueDate = "2026-03-01",
            dueDate = "2026-03-31",
            lines = Array.Empty<object>(),
        };
        var resp = await _client.PostAsJsonAsync(InvoicesRoute, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_lines", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "Invoice create: a duplicate externalRef in the same chart 409s")]
    public async Task Create_DuplicateExternalRef_Conflicts()
    {
        await CreateDraftAsync(NewInvoiceBody(externalRef: "OPP-123"));
        var resp = await _client.PostAsJsonAsync(InvoicesRoute, NewInvoiceBody(externalRef: "OPP-123"));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("external_ref_duplicate", doc.GetProperty("error").GetString());
    }

    // ── Issue → posts a balanced JE (Debit AR / Credit Income) ─────────────────────

    [Fact(DisplayName = "Invoice issue: a Draft transitions to Issued and returns a journalEntryId")]
    public async Task Issue_DraftInvoice_PostsJournalEntry()
    {
        var id = Data(await CreateDraftAsync(NewInvoiceBody(lineAmount: 250m))).GetProperty("id").GetString();

        var resp = await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/issue", new { });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var d = Data(await resp.Content.ReadFromJsonAsync<JsonElement>());

        Assert.Equal("Issued", d.GetProperty("status").GetString());
        Assert.Equal(250d, d.GetProperty("total").GetDouble());
        Assert.Equal(250d, d.GetProperty("balance").GetDouble());
        var jeId = d.GetProperty("journalEntryId").GetString();
        Assert.False(string.IsNullOrEmpty(jeId), "an issued invoice must carry its posted journalEntryId");
    }

    [Fact(DisplayName = "Invoice issue: the auto-posted JE is BALANCED (Debit AR / Credit Income) and readable via the node JE endpoint")]
    public async Task Issue_PostsBalancedJournalEntry_ReadableViaJeEndpoint()
    {
        var id = Data(await CreateDraftAsync(NewInvoiceBody(lineAmount: 400m, lineAccount: "4000", arAccountId: "1100")))
            .GetProperty("id").GetString();

        var issued = await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/issue", new { });
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var jeId = Data(await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("journalEntryId").GetString();

        // The JE is durably persisted on the node and readable via the JE read endpoint.
        var je = await _client.GetFromJsonAsync<JsonElement>($"{JeRoute}/{jeId}");
        var jed = Data(je);
        Assert.Equal("Posted", jed.GetProperty("status").GetString());
        // Balanced: total debits == total credits == 400 (Debit 1100 AR / Credit 4000 Income).
        Assert.Equal(400d, jed.GetProperty("totalDebits").GetDouble());
        Assert.Equal(400d, jed.GetProperty("totalCredits").GetDouble());

        // accountIds surface both the AR control account and the income line account.
        var accountIds = jed.GetProperty("accountIds").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("1100", accountIds);
        Assert.Contains("4000", accountIds);

        // AR (1100) is DEBITED the total; the income line (4000) is CREDITED.
        var lines = jed.GetProperty("lines").EnumerateArray().ToList();
        var arLine = lines.Single(l => l.GetProperty("accountId").GetString() == "1100");
        Assert.Equal(400d, arLine.GetProperty("debit").GetDouble());
        Assert.Equal(0d, arLine.GetProperty("credit").GetDouble());
        var incomeLine = lines.Single(l => l.GetProperty("accountId").GetString() == "4000");
        Assert.Equal(400d, incomeLine.GetProperty("credit").GetDouble());
    }

    [Fact(DisplayName = "Invoice issue: an issued invoice is durable + readable by id and listed in the chart")]
    public async Task Issue_IssuedInvoice_IsDurableAndListed()
    {
        var id = Data(await CreateDraftAsync()).GetProperty("id").GetString();
        await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/issue", new { });

        var detail = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/{id}");
        Assert.Equal("Issued", Data(detail).GetProperty("status").GetString());

        var list = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}?chartId=CH-1");
        var ids = Data(list).EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
        Assert.Contains(id, ids);
    }

    [Fact(DisplayName = "Invoice issue: a line crediting an unknown account is rejected (journal_rejected)")]
    public async Task Issue_UnknownLineAccount_JournalRejected()
    {
        // 9999 was never seeded — the JE's Phase-3 account-validity fails on issue.
        var id = Data(await CreateDraftAsync(NewInvoiceBody(lineAccount: "9999"))).GetProperty("id").GetString();
        var resp = await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/issue", new { });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("journal_rejected", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "Invoice issue: 404 for an unknown invoice id")]
    public async Task Issue_UnknownInvoice_NotFound()
    {
        var resp = await _client.PostAsJsonAsync($"{InvoicesRoute}/does-not-exist/issue", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Void → reversing JE ───────────────────────────────────────────────────────

    [Fact(DisplayName = "Invoice void: voids an issued invoice, posts a reversing JE, balance → 0")]
    public async Task Void_IssuedInvoice_PostsReversingJournalEntry()
    {
        var created = await CreateDraftAsync(NewInvoiceBody(lineAmount: 300m));
        var id = Data(created).GetProperty("id").GetString();
        var issued = Data(await (await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/issue", new { }))
            .Content.ReadFromJsonAsync<JsonElement>());
        var originalJeId = issued.GetProperty("journalEntryId").GetString();

        var voidResp = await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/void", new { reason = "billing error" });
        Assert.Equal(HttpStatusCode.OK, voidResp.StatusCode);
        var voided = Data(await voidResp.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("Voided", voided.GetProperty("status").GetString());
        Assert.Equal(0d, voided.GetProperty("balance").GetDouble());
        var voidedByEntryId = voided.GetProperty("voidedByEntryId").GetString();
        Assert.False(string.IsNullOrEmpty(voidedByEntryId));
        Assert.NotEqual(originalJeId, voidedByEntryId);

        // The reversing JE is durable + balanced (swapped debit/credit vs the issue).
        var revJe = await _client.GetFromJsonAsync<JsonElement>($"{JeRoute}/{voidedByEntryId}");
        var revd = Data(revJe);
        Assert.Equal("Posted", revd.GetProperty("status").GetString());
        Assert.Equal(300d, revd.GetProperty("totalDebits").GetDouble());
        Assert.Equal(300d, revd.GetProperty("totalCredits").GetDouble());
        // AR is now CREDITED on the reversal (it was debited on the issue).
        var arLine = revd.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("accountId").GetString() == "1100");
        Assert.Equal(300d, arLine.GetProperty("credit").GetDouble());
    }

    [Fact(DisplayName = "Invoice void: a Draft cannot be voided (invalid_status_for_void)")]
    public async Task Void_DraftInvoice_Rejected()
    {
        var id = Data(await CreateDraftAsync()).GetProperty("id").GetString();
        var resp = await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/void", new { reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_status_for_void", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "Invoice void: 404 for an unknown invoice id")]
    public async Task Void_UnknownInvoice_NotFound()
    {
        var resp = await _client.PostAsJsonAsync($"{InvoicesRoute}/nope/void", new { reason = "x" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Write-off → bad-debt JE ─────────────────────────────────────────────────────

    [Fact(DisplayName = "Invoice write-off: writes off an issued invoice, posts a bad-debt JE (Debit BadDebt / Credit AR), balance → 0")]
    public async Task WriteOff_IssuedInvoice_PostsBadDebtJournalEntry()
    {
        var id = Data(await CreateDraftAsync(NewInvoiceBody(lineAmount: 150m))).GetProperty("id").GetString();
        await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/issue", new { });

        var resp = await _client.PostAsJsonAsync(
            $"{InvoicesRoute}/{id}/write-off",
            new { badDebtAccountId = "6900", reason = "uncollectible" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var wo = Data(await resp.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("WrittenOff", wo.GetProperty("status").GetString());
        Assert.Equal(0d, wo.GetProperty("balance").GetDouble());
        var woEntryId = wo.GetProperty("writtenOffByEntryId").GetString();
        Assert.False(string.IsNullOrEmpty(woEntryId));

        // The bad-debt JE: Debit 6900 BadDebt / Credit 1100 AR for the open balance.
        var je = Data(await _client.GetFromJsonAsync<JsonElement>($"{JeRoute}/{woEntryId}"));
        Assert.Equal("Posted", je.GetProperty("status").GetString());
        Assert.Equal(150d, je.GetProperty("totalDebits").GetDouble());
        Assert.Equal(150d, je.GetProperty("totalCredits").GetDouble());
        var lines = je.GetProperty("lines").EnumerateArray().ToList();
        var badDebtLine = lines.Single(l => l.GetProperty("accountId").GetString() == "6900");
        Assert.Equal(150d, badDebtLine.GetProperty("debit").GetDouble());
        var arLine = lines.Single(l => l.GetProperty("accountId").GetString() == "1100");
        Assert.Equal(150d, arLine.GetProperty("credit").GetDouble());
    }

    [Fact(DisplayName = "Invoice write-off: a missing badDebtAccountId is rejected 400 bad_debt_account_required")]
    public async Task WriteOff_MissingBadDebtAccount_Rejected()
    {
        var id = Data(await CreateDraftAsync()).GetProperty("id").GetString();
        await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/issue", new { });

        var resp = await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/write-off", new { reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("bad_debt_account_required", doc.GetProperty("error").GetString());
    }

    // ── List filters ────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "Invoice list: ?status=open returns only open invoices (Drafts + voided excluded)")]
    public async Task List_OpenStatus_ExcludesDraftAndVoided()
    {
        // An Issued (open) invoice.
        var openId = Data(await CreateDraftAsync(NewInvoiceBody(lineAmount: 100m))).GetProperty("id").GetString();
        await _client.PostAsJsonAsync($"{InvoicesRoute}/{openId}/issue", new { });

        // A Draft (not open).
        var draftId = Data(await CreateDraftAsync(NewInvoiceBody(lineAmount: 120m))).GetProperty("id").GetString();

        // A voided invoice (issued then voided — not open).
        var voidedId = Data(await CreateDraftAsync(NewInvoiceBody(lineAmount: 140m))).GetProperty("id").GetString();
        await _client.PostAsJsonAsync($"{InvoicesRoute}/{voidedId}/issue", new { });
        await _client.PostAsJsonAsync($"{InvoicesRoute}/{voidedId}/void", new { reason = "x" });

        var list = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}?chartId=CH-1&status=open");
        var ids = Data(list).EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
        Assert.Contains(openId, ids);
        Assert.DoesNotContain(draftId, ids);
        Assert.DoesNotContain(voidedId, ids);
    }

    [Fact(DisplayName = "Invoice list: requires chartId (400 chart_id_required)")]
    public async Task List_MissingChart_Rejected()
    {
        var resp = await _client.GetAsync(InvoicesRoute);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("chart_id_required", doc.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "Invoice list: ?customerId filters to that customer's invoices")]
    public async Task List_ByCustomer_Filters()
    {
        var mine = Data(await CreateDraftAsync(NewInvoiceBody(customerId: "cust-A"))).GetProperty("id").GetString();
        var theirs = Data(await CreateDraftAsync(NewInvoiceBody(customerId: "cust-B"))).GetProperty("id").GetString();

        var list = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}?chartId=CH-1&customerId=cust-A");
        var ids = Data(list).EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
        Assert.Contains(mine, ids);
        Assert.DoesNotContain(theirs, ids);
    }

    // ── Tenant isolation (financial-cluster discipline; ADR 0092) ────────────────────

    [Fact(DisplayName = "Invoice read: a foreign-tenant invoice never leaks into the chart list")]
    public async Task List_DoesNotLeakForeignTenant()
    {
        var localId = Data(await CreateDraftAsync(NewInvoiceBody())).GetProperty("id").GetString();

        // Seed a foreign-tenant invoice DIRECTLY into the store (the route always pins "local").
        await SeedForeignTenantInvoiceAsync("foreign-invoice", new TenantId("other-tenant"), "CH-1");

        var list = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}?chartId=CH-1");
        var ids = Data(list).EnumerateArray().Select(b => b.GetProperty("id").GetString()).ToList();
        Assert.Contains(localId, ids);
        Assert.DoesNotContain("foreign-invoice", ids);
    }

    [Fact(DisplayName = "Invoice read: by-id 404 for a foreign-tenant invoice (no cross-tenant read)")]
    public async Task GetById_ForeignTenant_NotFound()
    {
        await SeedForeignTenantInvoiceAsync("foreign-invoice", new TenantId("other-tenant"), "CH-1");
        var resp = await _client.GetAsync($"{InvoicesRoute}/foreign-invoice");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "Invoice issue: a foreign-tenant invoice cannot be issued (opaque 404)")]
    public async Task Issue_ForeignTenant_NotFound()
    {
        await SeedForeignTenantInvoiceAsync("foreign-invoice", new TenantId("other-tenant"), "CH-1");
        var resp = await _client.PostAsJsonAsync($"{InvoicesRoute}/foreign-invoice/issue", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── DELETE /api/local-node/invoices/{id} — hard-delete a Draft invoice ─────

    [Fact(DisplayName = "Invoice delete: Draft invoice is removed from the store (hard-delete, 204)")]
    public async Task DeleteDraft_ExistingDraft_Returns204_AndRowIsGone()
    {
        var doc = await CreateDraftAsync();
        var id = Data(doc).GetProperty("id").GetString()!;

        var resp = await _client.DeleteAsync($"{InvoicesRoute}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // Row must be physically gone — a subsequent GET returns 404.
        var getResp = await _client.GetAsync($"{InvoicesRoute}/{id}");
        Assert.Equal(HttpStatusCode.NotFound, getResp.StatusCode);
    }

    [Fact(DisplayName = "Invoice delete: idempotent — second DELETE on the same id returns 404")]
    public async Task DeleteDraft_SecondCall_Returns404()
    {
        var doc = await CreateDraftAsync();
        var id = Data(doc).GetProperty("id").GetString()!;

        var first = await _client.DeleteAsync($"{InvoicesRoute}/{id}");
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        var second = await _client.DeleteAsync($"{InvoicesRoute}/{id}");
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact(DisplayName = "Invoice delete: unknown id returns 404 (uniform-404)")]
    public async Task DeleteDraft_UnknownId_Returns404()
    {
        var resp = await _client.DeleteAsync($"{InvoicesRoute}/no-such-id");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "Invoice delete: issued invoice returns 422 (not_draft; must void instead)")]
    public async Task DeleteDraft_IssuedInvoice_Returns422()
    {
        var doc = await CreateDraftAsync();
        var id = Data(doc).GetProperty("id").GetString()!;

        // Issue it — this posts the balanced JE, making the invoice Issued (has a GL footprint).
        var issueResp = await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/issue", new { });
        Assert.Equal(HttpStatusCode.OK, issueResp.StatusCode);

        // Attempt hard-delete of an Issued invoice: doctrine requires 422 (not_draft).
        var deleteResp = await _client.DeleteAsync($"{InvoicesRoute}/{id}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, deleteResp.StatusCode);

        var body = await deleteResp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("not_draft", body.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "Invoice delete: foreign-tenant invoice returns 404 (no cross-tenant access)")]
    public async Task DeleteDraft_ForeignTenant_Returns404()
    {
        await SeedForeignTenantInvoiceAsync("foreign-del", new TenantId("other-tenant"), "CH-1");
        var resp = await _client.DeleteAsync($"{InvoicesRoute}/foreign-del");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    /// <summary>
    /// Seeds a Draft invoice under a NON-local tenant directly via the repository (the routes always
    /// pin "local", so this is the only way to plant a foreign-tenant row for the isolation tests).
    /// </summary>
    private async Task SeedForeignTenantInvoiceAsync(string id, TenantId tenant, string chartId)
    {
        var invoiceId = new Harborline.Api.Blocks.FinancialAr.Models.InvoiceId(id);
        var line = Harborline.Api.Blocks.FinancialAr.Models.InvoiceLine.Create(
            invoiceId:       invoiceId,
            lineNumber:      1,
            description:     "foreign",
            quantity:        1m,
            unitPrice:       100m,
            incomeAccountId: new GLAccountId("4000"));
        // Foreign tenant Draft carries an empty number — Drafts are allowed to (the format guard only
        // applies to non-Draft invoices).
        var invoice = Harborline.Api.Blocks.FinancialAr.Models.Invoice.Create(
            tenantId:      tenant,
            chartId:       new ChartOfAccountsId(chartId),
            invoiceNumber: "INV-2026-03-01-ZZ-9999",
            customerId:    new Harborline.Api.Blocks.People.Foundation.Models.PartyId("customer-x"),
            issueDate:     new DateOnly(2026, 3, 1),
            dueDate:       new DateOnly(2026, 3, 31),
            lines:         new[] { line },
            arAccountId:   new GLAccountId("1100"),
            createdAtUtc:  new Instant(System.TimeProvider.System.GetUtcNow()),
            id:            invoiceId);
        await _invoices.UpsertAsync(tenant, invoice, new Instant(System.TimeProvider.System.GetUtcNow()).Value);
    }

}
