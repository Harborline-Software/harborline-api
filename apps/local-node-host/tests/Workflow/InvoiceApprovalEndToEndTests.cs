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
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// The ADR 0135 invoice-approval vertical flow's SUCCESS-CRITERION proof — ONE explicitly-named,
/// single, green test (<see cref="Approve_PostsExactlyOneJournalEntry_EndToEnd"/>) that walks the
/// FULL demonstrable flow over the LIVE HTTP ROUTES (not the engine / dispatcher), so "click Approve
/// → the JE posts" is real and reproducible:
/// <list type="number">
///   <item>create a Draft invoice &gt; $5k;</item>
///   <item><c>POST .../issue</c> → <b>202 parked</b>, invoice stays <b>Draft</b>, <b>no JE</b>;</item>
///   <item><c>GET .../approval-tasks</c> → the task appears with its <b>FE-1 basis</b> (posting
///     preview + the decision-table row + the pinned version that fired);</item>
///   <item><c>POST .../approval-tasks/{id}/action {approve}</c> → <b>200</b>;</item>
///   <item>query the ledger over HTTP → <b>EXACTLY ONE</b> balanced JournalEntry whose
///     <c>SourceReference == invoice:{id}</c>, and the invoice is now <b>Issued</b>;</item>
///   <item>negative: a second invoice <b>rejected</b> → <b>no JE</b>, stays <b>Draft</b>.</item>
/// </list>
/// Companion to <see cref="InvoiceApprovalVerticalFlowTests"/> (which proves the same properties
/// decomposed across many tests); this consolidates the success criterion into ONE named assertion
/// chain AND adds the two #1357 deep-review fast-follows:
/// <list type="bullet">
///   <item><b>Finding 2 (cross-tenant):</b>
///     <see cref="ParkedTask_OfTenantB_IsInvisibleAndNonActionable_ToTenantA"/> — a task parked by
///     tenant B is neither visible nor actionable to tenant A (GET + action → 404), exercising the
///     read model's <c>WHERE TenantId</c> isolation.</item>
///   <item>Finding 1 (threshold single-source) is an arch-test —
///     <c>InvoiceApprovalThresholdSingleSourceArchTests</c>.</item>
/// </list>
/// </summary>
public sealed class InvoiceApprovalEndToEndTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;

    // Two distinct teams → two distinct projected data tenants (Finding 2). Team A is the default
    // active team (the success-criterion flow runs entirely on it).
    private static readonly TeamId TeamA = new(Guid.Parse("7e570000-0000-0000-0000-0000000000aa"));
    private static readonly TeamId TeamB = new(Guid.Parse("7e570000-0000-0000-0000-0000000000bb"));
    private static readonly TenantId TenantA = ActiveTeamTenantContext.ProjectTenantId(TeamA);

    private const string InvoicesRoute = "/api/local-node/invoices";
    private const string TasksRoute = "/api/local-node/approval-tasks";
    private const string JeRoute = "/api/local-node/journal-entries";

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

        _dir = Path.Combine(Path.GetTempPath(), "harborline-approval-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "approval-e2e.db")};Pooling=False";

        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.LocalNodeHost.Data.Audit.AuditEventEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));

        _app = builder.Build();
        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // Production-faithful composition — the SAME path the route uses (mirrors
        // InvoiceApprovalVerticalFlowTests). The tenant the write services + read models resolve is
        // driven by the MutableActiveTeamAccessor: switching its Active flips the request's data tenant
        // (the per-org WHERE TenantId predicate), which is exactly what the cross-tenant test exercises.
        _activeTeam = new MutableActiveTeamAccessor(TeamA);

        var invoices = new NodeEfInvoiceRepository(_factory);
        var numbering = new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA"));
        var journalStore = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
        var jePosting = new JournalPostingService(
            accounts: new NodeEfAccountResolver(_factory),
            periods:  new NodeEfPeriodResolver(_factory),
            store:    journalStore,
            gate:     TestAuthorization.AllowGate());
        var invoicePosting = new InvoicePostingService(
            tenantContext: new ActiveTeamTenantContext(_activeTeam),
            invoices:      invoices,
            numbering:     numbering,
            tax:           new NoOpTaxCalculator(),
            journals:      jePosting,
            events:        null,
            journalStore:  journalStore, timeProvider: TimeProvider.System);

        var invoiceRepoAccessor = invoices;
        var invoiceNumberingAccessor = numbering;
        var invoicePostingAccessor = invoicePosting;

        var workflowStore = new NodeEfWorkflowStore(_factory);
        var liveContext = new NodeLiveInvoiceApprovalContext();
        var approvalHandler = new InvoiceApprovalHandler(
            NodeWorkflowDefinitions.InvoiceApprovalThresholdTable(), liveContext);
        var dispatcher = new WorkflowTriggerDispatcher(workflowStore, new IWorkflowStepHandler[] { approvalHandler });
        var instantiation = new NodeWorkflowInstantiationService(workflowStore, _factory);
        var cutover = new NodeInvoiceApprovalCutover(instantiation, dispatcher);
        var tasksReadModel = new NodeParkedTaskQueryReadModel(_factory);

        var jeReadModel = new InMemoryJournalEntryQueryReadModel(journalStore);
        var jePostingAccessor = jePosting;

        await SeedAccountAsync("1100", "Accounts Receivable", GLAccountType.Asset, AccountSubtype.AccountsReceivable);
        await SeedAccountAsync("4000", "Service Income", GLAccountType.Revenue, AccountSubtype.OperatingIncome);
        await SeedOpenPeriodAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        var deviceReachable = _app.MapDeviceReachableProductDataGroup();
        InvoiceRoutes.Map(deviceReachable, invoiceRepoAccessor, invoiceNumberingAccessor, invoicePostingAccessor,
            _activeTeam, cutover, timeProvider: TimeProvider.System);
        InvoiceApprovalTaskRoutes.Map(deviceReachable, tasksReadModel, cutover, _activeTeam, TimeProvider.System);
        JournalEntryRoutes.Map(
            deviceReachable, jeReadModel, journalStore, jePostingAccessor, _activeTeam, TimeProvider.System);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  PART A — the SUCCESS CRITERION as ONE named, end-to-end, live-HTTP-routes test
    // ═════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "END-TO-END (live HTTP): >$5k issue parks (202, no JE, Draft) → task carries FE-1 basis → approve (200) → EXACTLY ONE balanced JE (SourceReference=invoice:{id}) + Issued; reject of a second invoice posts no JE and stays Draft")]
    public async Task Approve_PostsExactlyOneJournalEntry_EndToEnd()
    {
        // ── Step 1: create a Draft invoice > $5k ───────────────────────────────────
        var invoiceId = await CreateDraftAsync(lineAmount: 7500m);
        Assert.Equal(0, await JournalEntryCountAsync(TenantA));

        // ── Step 2: POST .../issue → 202 parked, invoice stays Draft, NO JE ────────
        var issueResp = await IssueAsync(invoiceId);
        Assert.Equal(HttpStatusCode.Accepted, issueResp.StatusCode);
        var parked = await issueResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("parked_for_approval", parked.GetProperty("status").GetString());
        var instanceId = parked.GetProperty("instanceId").GetString()!;
        Assert.Equal(0, await JournalEntryCountAsync(TenantA));
        var draftDetail = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/{invoiceId}");
        Assert.Equal("Draft", Data(draftDetail).GetProperty("status").GetString());

        // ── Step 3: GET .../approval-tasks → the task with its FE-1 basis ──────────
        // (the posting preview + the decision-table row + the pinned version that fired).
        var listDoc = await _client.GetFromJsonAsync<JsonElement>(TasksRoute);
        var task = Data(listDoc).EnumerateArray()
            .Single(t => t.GetProperty("instanceId").GetString() == instanceId);
        Assert.Equal(invoiceId, task.GetProperty("invoiceId").GetString());
        Assert.Equal(7500d, task.GetProperty("amount").GetDouble());
        Assert.False(string.IsNullOrEmpty(task.GetProperty("postingPreview").GetString()),
            "FE-1: the basis must carry a posting preview");
        Assert.Equal(NodeWorkflowDefinitions.InvoiceApprovalV1Version,
            task.GetProperty("decisionVersion").GetString());
        Assert.Equal("over-5k", task.GetProperty("decisionRow").GetString());
        Assert.Equal(
            new[] { "approve", "reject", "send-back" },
            task.GetProperty("allowedOutcomes").EnumerateArray().Select(x => x.GetString()).ToArray());

        // ── Step 4: POST .../approval-tasks/{id}/action {approve} → 200 ────────────
        var approveResp = await ActionAsync(instanceId, "approve");
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        // ── Step 5: query the ledger (over HTTP) → EXACTLY ONE balanced JE for this ─
        //            invoice (SourceReference=invoice:{id}); the invoice is now Issued.
        var jeList = await _client.GetFromJsonAsync<JsonElement>(JeRoute);
        var jeRows = Data(jeList).EnumerateArray()
            .Where(j => j.GetProperty("sourceKind").GetString() == "Invoice")
            .ToList();
        Assert.Single(jeRows);                       // EXACTLY ONE invoice-sourced JE
        var jeRow = jeRows[0];
        var journalEntryId = jeRow.GetProperty("id").GetString()!;

        // Balanced over the wire: total debits == total credits, and non-zero.
        var jeDetailDoc = await _client.GetFromJsonAsync<JsonElement>($"{JeRoute}/{journalEntryId}");
        var jeDetail = Data(jeDetailDoc);
        var totalDebits = jeDetail.GetProperty("totalDebits").GetDouble();
        var totalCredits = jeDetail.GetProperty("totalCredits").GetDouble();
        Assert.Equal(totalDebits, totalCredits);
        Assert.True(totalDebits > 0d, "the posted JE must carry the invoice's value");

        // SourceReference == invoice:{id} — the idempotency key. Not exposed on the wire by design
        // (the JE wire omits it); assert it on the durable store so the success criterion is complete.
        Assert.Equal($"invoice:{invoiceId}", await SourceReferenceOfAsync(TenantA, journalEntryId));

        // The invoice is Issued and carries this JE id.
        var issuedDetail = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/{invoiceId}");
        Assert.Equal("Issued", Data(issuedDetail).GetProperty("status").GetString());
        Assert.Equal(journalEntryId, Data(issuedDetail).GetProperty("journalEntryId").GetString());

        // And EXACTLY ONE JE for the whole tenant — no double-post.
        Assert.Equal(1, await JournalEntryCountAsync(TenantA));

        // ── Step 6 (negative): a second invoice, REJECT → no JE, stays Draft ───────
        var rejectId = await CreateDraftAsync(lineAmount: 9000m);
        var rejectParked = await (await IssueAsync(rejectId)).Content.ReadFromJsonAsync<JsonElement>();
        var rejectInstance = rejectParked.GetProperty("instanceId").GetString()!;
        Assert.Equal(1, await JournalEntryCountAsync(TenantA));   // still 1 — the issue parked, no post

        var rejectResp = await ActionAsync(rejectInstance, "reject", note: "out of budget");
        Assert.Equal(HttpStatusCode.OK, rejectResp.StatusCode);
        Assert.Equal(1, await JournalEntryCountAsync(TenantA));   // reject posts NO JE
        var rejectDetail = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/{rejectId}");
        Assert.Equal("Draft", Data(rejectDetail).GetProperty("status").GetString());
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  PART B — Finding 2: cross-tenant isolation (the WHERE TenantId boundary)
    // ═════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "CROSS-TENANT (Finding 2): a >$5k task parked by tenant B is INVISIBLE and NON-ACTIONABLE to tenant A — GET list omits it, GET detail → 404, action → 404; B can still see + approve its own")]
    public async Task ParkedTask_OfTenantB_IsInvisibleAndNonActionable_ToTenantA()
    {
        // Tenant B parks an over-threshold invoice (the routes resolve B's tenant while B is active).
        _activeTeam.SetActive(TeamB);
        var bInvoiceId = await CreateDraftAsync(lineAmount: 8000m);
        var bParked = await (await IssueAsync(bInvoiceId)).Content.ReadFromJsonAsync<JsonElement>();
        var bInstanceId = bParked.GetProperty("instanceId").GetString()!;

        // ── Switch to tenant A. B's parked task must be invisible + non-actionable. ──
        _activeTeam.SetActive(TeamA);

        // GET list (as A) — B's task is NOT present.
        var aList = await _client.GetFromJsonAsync<JsonElement>(TasksRoute);
        Assert.DoesNotContain(Data(aList).EnumerateArray(),
            t => t.GetProperty("instanceId").GetString() == bInstanceId);

        // GET detail (as A) — uniform 404 (no cross-tenant existence leak).
        var aDetail = await _client.GetAsync($"{TasksRoute}/{bInstanceId}");
        Assert.Equal(HttpStatusCode.NotFound, aDetail.StatusCode);

        // POST action approve (as A) — 404, and crucially NO JE posts for B's invoice.
        var aAction = await ActionAsync(bInstanceId, "approve");
        Assert.Equal(HttpStatusCode.NotFound, aAction.StatusCode);
        Assert.Equal(0, await JournalEntryCountAsync(ActiveTeamTenantContext.ProjectTenantId(TeamB)));
        Assert.Equal(0, await JournalEntryCountAsync(TenantA));

        // ── Sanity: tenant B CAN see + approve its OWN task (isolation, not breakage). ──
        _activeTeam.SetActive(TeamB);
        var bSelfList = await _client.GetFromJsonAsync<JsonElement>(TasksRoute);
        Assert.Contains(Data(bSelfList).EnumerateArray(),
            t => t.GetProperty("instanceId").GetString() == bInstanceId);

        var bApprove = await ActionAsync(bInstanceId, "approve");
        Assert.Equal(HttpStatusCode.OK, bApprove.StatusCode);
        Assert.Equal(1, await JournalEntryCountAsync(ActiveTeamTenantContext.ProjectTenantId(TeamB)));
        // Tenant A still has nothing — B's post did not bleed across the boundary.
        Assert.Equal(0, await JournalEntryCountAsync(TenantA));
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private async Task SeedAccountAsync(string code, string name, GLAccountType type, AccountSubtype subtype, string chartId = "CH-1")
    {
        // Seed the account for BOTH tenants (GLAccount carries no TenantId in this store model; the
        // account-resolver reads by code — shared chart of accounts across the seeded fixture).
        var account = GLAccount.Create(
            id: new GLAccountId(code), chartId: new ChartOfAccountsId(chartId), code: code, name: name,
            type: type, subtype: subtype, currency: "USD", isPostable: true, createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()));
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<GLAccount>().Add(account);
        await ctx.SaveChangesAsync();
    }

    private async Task SeedOpenPeriodAsync(DateOnly start, DateOnly end, string chartId = "CH-1")
    {
        var period = FiscalPeriod.CreateOpen(
            id: FiscalPeriodId.NewId(), chartId: new ChartOfAccountsId(chartId),
            fiscalYearId: new FiscalYearId("FY-2026"), kind: FiscalPeriodKind.Monthly,
            label: $"{start:yyyy-MM}", startDate: start, endDate: end, createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()));
        await using var ctx = await _factory.CreateDbContextAsync();
        ctx.Set<FiscalPeriod>().Add(period);
        await ctx.SaveChangesAsync();
    }

    private static object NewInvoiceBody(decimal lineAmount) => new
    {
        id = (string?)null,
        chartId = "CH-1",
        customerId = "customer-1",
        arAccountId = "1100",
        issueDate = "2026-03-01",
        dueDate = "2026-03-31",
        lines = new[]
        {
            new { description = "Consulting", quantity = 1m, unitPrice = lineAmount, incomeAccountId = "4000", taxCodeId = (string?)null },
        },
    };

    private static JsonElement Data(JsonElement doc) => doc.GetProperty("data");

    private async Task<string> CreateDraftAsync(decimal lineAmount)
    {
        var resp = await _client.PostAsJsonAsync(InvoicesRoute, NewInvoiceBody(lineAmount));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return Data(doc).GetProperty("id").GetString()!;
    }

    private async Task<int> JournalEntryCountAsync(TenantId tenantId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == tenantId);
    }

    private async Task<string?> SourceReferenceOfAsync(TenantId tenantId, string journalEntryId)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        var je = await ctx.Set<JournalEntry>()
            .FirstAsync(j => j.TenantId == tenantId && j.Id == new JournalEntryId(journalEntryId));
        return je.SourceReference;
    }

    private async Task<HttpResponseMessage> IssueAsync(string id)
        => await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/issue", new { });

    private async Task<HttpResponseMessage> ActionAsync(string instanceId, string decision, string? note = null)
        => await _client.PostAsJsonAsync($"{TasksRoute}/{instanceId}/action", new { decision, note });

    /// <summary>
    /// A mutable <see cref="IActiveTeamAccessor"/> whose <see cref="Active"/> can be flipped between
    /// teams so a single host can exercise two data tenants (the per-request <c>NodeTenant.Resolve</c>
    /// reads the current Active each call). The route handlers resolve the tenant per-request, so
    /// flipping Active flips which org's data a subsequent request sees — exactly the production
    /// team-switch semantics (ADR 0032), used here to prove the WHERE TenantId isolation.
    /// </summary>
    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamId initial) => SetActive(initial);
        public TeamContext? Active { get; private set; }
        public void SetActive(TeamId teamId) =>
            Active = new TeamContext(teamId, "Test Team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) { SetActive(teamId); return Task.CompletedTask; }
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
