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
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Entities;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// ROUTE-LEVEL end-to-end tests for the ADR 0135 invoice-approval VERTICAL FLOW — the backend a UI is built
/// against. Hosts the REAL <see cref="InvoiceRoutes"/> WITH the approval cutover + the
/// <see cref="InvoiceApprovalTaskRoutes"/> (the Ask-bar Inbox query + resume) + the JE read routes, on a real
/// in-process Kestrel listener over a temp SQLite store, driven by a real <see cref="HttpClient"/> — the same
/// production composition <see cref="HostedInvoiceApiEndpoint"/> / <see cref="HostedApprovalTaskApiEndpoint"/>
/// wire (no test/prod drift). Proves the load-bearing properties:
/// <list type="bullet">
///   <item>issuing a &gt;$5k invoice PARKS an approval task (202) with its FE-1 basis payload + NO JE yet;</item>
///   <item>approve → EXACTLY ONE JE + the invoice is Issued with its journalEntryId;</item>
///   <item>reject → NO JE, the invoice stays Draft;</item>
///   <item>send-back → round-trip (the task reappears parked);</item>
///   <item>a redelivered approve is an idempotent no-op (still EXACTLY ONE JE);</item>
///   <item>the ≤$5k path is unchanged (direct inline post, exactly one JE);</item>
///   <item>NO double-post vs the direct path.</item>
/// </list>
/// </summary>
public sealed class InvoiceApprovalVerticalFlowTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;

    private static readonly TenantId LocalTenantId =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

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

        _dir = Path.Combine(Path.GetTempPath(), "harborline-approval-vertical-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "approval-test.db")};Pooling=False";

        // Invoice (ArEntityModule) + JournalEntry/GLAccount (FinancialLedgerEntityModule) + the 3 workflow
        // tables (WorkflowEntityModule) — the production module set the engine + financial routes touch.
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

        // Production-faithful composition: the node invoice repo + numbering + the AR posting service over
        // the node JournalPostingService + the recoverable NodeEfJournalStore — the SAME path the route uses.
        var invoices = new NodeEfInvoiceRepository(_factory);
        var numbering = new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA"));
        var journalStore = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
        var jePosting = new JournalPostingService(
            accounts: new NodeEfAccountResolver(_factory),
            periods:  new NodeEfPeriodResolver(_factory),
            store:    journalStore,
            gate:     TestAuthorization.AllowGate());
        var invoicePosting = new InvoicePostingService(
            tenantContext: new ActiveTeamTenantContext(NodeTestActiveTeam.Accessor),
            invoices:      invoices,
            numbering:     numbering,
            tax:           new NoOpTaxCalculator(),
            journals:      jePosting,
            events:        null,
            journalStore:  journalStore, timeProvider: TimeProvider.System);

        var invoiceRepoAccessor = invoices;
        var invoiceNumberingAccessor = numbering;
        var invoicePostingAccessor = invoicePosting;

        // The durable engine: the recoverable store + the live approval handler (whose post effect issues the
        // REAL invoice via the posting accessor) + the dispatcher + the instantiation service + the cutover +
        // the parked-task read model — the exact slice AddNodeWorkflowEngine/Handlers register.
        var workflowStore = new NodeEfWorkflowStore(_factory);
        var liveContext = new NodeLiveInvoiceApprovalContext();
        var approvalHandler = new InvoiceApprovalHandler(
            NodeWorkflowDefinitions.InvoiceApprovalThresholdTable(), liveContext);
        var dispatcher = new WorkflowTriggerDispatcher(workflowStore, new IWorkflowStepHandler[] { approvalHandler });
        var instantiation = new NodeWorkflowInstantiationService(workflowStore, _factory);
        var cutover = new NodeInvoiceApprovalCutover(instantiation, dispatcher);
        var tasksReadModel = new NodeParkedTaskQueryReadModel(_factory);

        // JE read surface (so an approved invoice's JE can be read back).
        var jeReadModel = new InMemoryJournalEntryQueryReadModel(journalStore);
        var jePostingAccessor = jePosting;

        await SeedAccountAsync("1100", "Accounts Receivable", GLAccountType.Asset, AccountSubtype.AccountsReceivable);
        await SeedAccountAsync("4000", "Service Income", GLAccountType.Revenue, AccountSubtype.OperatingIncome);
        await SeedOpenPeriodAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        // Map the SAME production routes (the cutover is wired onto the invoice routes; the task routes drive
        // the read model + cutover).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        var deviceReachable = _app.MapDeviceReachableProductDataGroup();
        InvoiceRoutes.Map(deviceReachable, invoiceRepoAccessor, invoiceNumberingAccessor, invoicePostingAccessor,
            NodeTestActiveTeam.Accessor, cutover, timeProvider: TimeProvider.System);
        InvoiceApprovalTaskRoutes.Map(
            deviceReachable,
            tasksReadModel,
            cutover,
            NodeTestActiveTeam.Accessor,
            TimeProvider.System);
        JournalEntryRoutes.Map(
            deviceReachable,
            jeReadModel,
            journalStore,
            jePostingAccessor,
            NodeTestActiveTeam.Accessor,
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
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private async Task SeedAccountAsync(string code, string name, GLAccountType type, AccountSubtype subtype, string chartId = "CH-1")
    {
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

    private static object NewInvoiceBody(decimal lineAmount, string? id = null) => new
    {
        id,
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

    private async Task<int> JournalEntryCountAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId);
    }

    private async Task<HttpResponseMessage> IssueAsync(string id)
        => await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/issue", new { });

    private async Task<HttpResponseMessage> ActionAsync(string instanceId, string decision, string? note = null)
        => await _client.PostAsJsonAsync($"{TasksRoute}/{instanceId}/action", new { decision, note });

    // ═════════════════════════════════════════════════════════════════════════════
    //  ≤ $5k — the direct path is UNCHANGED
    // ═════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "≤$5k issue is the UNCHANGED direct post: 200 Issued + journalEntryId + EXACTLY ONE JE, no approval task")]
    public async Task UnderThreshold_DirectPost_Unchanged()
    {
        var id = await CreateDraftAsync(lineAmount: 5000m);   // EXACTLY $5k → strictly-above gate → direct.
        Assert.Equal(0, await JournalEntryCountAsync());

        var resp = await IssueAsync(id);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var d = Data(await resp.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("Issued", d.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(d.GetProperty("journalEntryId").GetString()));
        Assert.Equal(1, await JournalEntryCountAsync());

        // No approval task was created for the direct path.
        var tasks = await _client.GetFromJsonAsync<JsonElement>(TasksRoute);
        Assert.Empty(Data(tasks).EnumerateArray());
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  > $5k — park → approve → EXACTLY ONE JE
    // ═════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = ">$5k issue PARKS (202) with NO JE; the task carries the FE-1 basis; approve → EXACTLY ONE JE + Issued; redelivered approve is a no-op")]
    public async Task OverThreshold_Parks_ApprovePostsExactlyOnce_RedeliveryNoOp()
    {
        var id = await CreateDraftAsync(lineAmount: 7500m);

        // Issue → 202 parked_for_approval, NO JE, invoice stays Draft.
        var issueResp = await IssueAsync(id);
        Assert.Equal(HttpStatusCode.Accepted, issueResp.StatusCode);
        var parked = await issueResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("parked_for_approval", parked.GetProperty("status").GetString());
        var instanceId = parked.GetProperty("instanceId").GetString()!;
        Assert.Equal(0, await JournalEntryCountAsync());
        var draftDetail = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/{id}");
        Assert.Equal("Draft", Data(draftDetail).GetProperty("status").GetString());

        // The task is listed with its FE-1 basis (posting preview + the fired decision row/version) + outcomes.
        var listDoc = await _client.GetFromJsonAsync<JsonElement>(TasksRoute);
        var task = Data(listDoc).EnumerateArray().Single(t => t.GetProperty("instanceId").GetString() == instanceId);
        Assert.Equal(id, task.GetProperty("invoiceId").GetString());
        Assert.Equal(7500d, task.GetProperty("amount").GetDouble());
        Assert.False(string.IsNullOrEmpty(task.GetProperty("postingPreview").GetString()), "FE-1: the basis must carry a posting preview");
        Assert.Equal(NodeWorkflowDefinitions.InvoiceApprovalV1Version, task.GetProperty("decisionVersion").GetString());
        Assert.Equal("over-5k", task.GetProperty("decisionRow").GetString());
        var outcomes = task.GetProperty("allowedOutcomes").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(new[] { "approve", "reject", "send-back" }, outcomes);

        // Approve → EXACTLY ONE JE + the invoice is Issued with its journalEntryId.
        var approveResp = await ActionAsync(instanceId, "approve");
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);
        Assert.Equal(1, await JournalEntryCountAsync());
        var issuedDetail = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/{id}");
        Assert.Equal("Issued", Data(issuedDetail).GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(Data(issuedDetail).GetProperty("journalEntryId").GetString()));

        // The task is gone from the Inbox (instance Completed).
        var afterList = await _client.GetFromJsonAsync<JsonElement>(TasksRoute);
        Assert.DoesNotContain(Data(afterList).EnumerateArray(), t => t.GetProperty("instanceId").GetString() == instanceId);

        // A redelivered approve is an idempotent no-op — still EXACTLY ONE JE.
        var redeliver = await ActionAsync(instanceId, "approve");
        Assert.Equal(HttpStatusCode.NotFound, redeliver.StatusCode);   // the task is no longer parked → 404
        Assert.Equal(1, await JournalEntryCountAsync());
    }

    [Fact(DisplayName = ">$5k REJECT posts NO JE and the invoice stays Draft (closeable/deletable)")]
    public async Task OverThreshold_Reject_PostsNoJe_StaysDraft()
    {
        var id = await CreateDraftAsync(lineAmount: 9000m);
        var parked = await (await IssueAsync(id)).Content.ReadFromJsonAsync<JsonElement>();
        var instanceId = parked.GetProperty("instanceId").GetString()!;

        var rejectResp = await ActionAsync(instanceId, "reject", note: "out of budget");
        Assert.Equal(HttpStatusCode.OK, rejectResp.StatusCode);

        Assert.Equal(0, await JournalEntryCountAsync());   // NO post on reject
        var detail = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/{id}");
        Assert.Equal("Draft", Data(detail).GetProperty("status").GetString());

        // The rejected task is no longer open.
        var afterList = await _client.GetFromJsonAsync<JsonElement>(TasksRoute);
        Assert.DoesNotContain(Data(afterList).EnumerateArray(), t => t.GetProperty("instanceId").GetString() == instanceId);
    }

    [Fact(DisplayName = ">$5k SEND-BACK round-trips: the task re-parks (still open, no JE), then a later approve posts EXACTLY ONE")]
    public async Task OverThreshold_SendBack_RoundTrips_ThenApprovePostsOnce()
    {
        var id = await CreateDraftAsync(lineAmount: 8000m);
        var parked = await (await IssueAsync(id)).Content.ReadFromJsonAsync<JsonElement>();
        var instanceId = parked.GetProperty("instanceId").GetString()!;

        // Send-back → round-trip. NO JE, and the task is BACK in the Inbox parked on the human-task.
        var sendBackResp = await ActionAsync(instanceId, "send-back", note: "please add the PO number");
        Assert.Equal(HttpStatusCode.OK, sendBackResp.StatusCode);
        Assert.Equal(0, await JournalEntryCountAsync());

        var listAfterSendBack = await _client.GetFromJsonAsync<JsonElement>(TasksRoute);
        var stillOpen = Data(listAfterSendBack).EnumerateArray()
            .Any(t => t.GetProperty("instanceId").GetString() == instanceId);
        Assert.True(stillOpen, "send-back must round-trip the task back into the open Inbox");

        // A later approve still posts EXACTLY ONE JE.
        var approveResp = await ActionAsync(instanceId, "approve");
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);
        Assert.Equal(1, await JournalEntryCountAsync());
        var detail = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/{id}");
        Assert.Equal("Issued", Data(detail).GetProperty("status").GetString());
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  no-double-post vs the direct path; mixed traffic
    // ═════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "NO DOUBLE-POST: a >$5k approved invoice + a ≤$5k direct invoice → EXACTLY TWO JEs total (one each)")]
    public async Task NoDoublePost_MixedTraffic_OneJePerInvoice()
    {
        // ≤$5k → direct post (1 JE).
        var underId = await CreateDraftAsync(lineAmount: 1000m);
        Assert.Equal(HttpStatusCode.OK, (await IssueAsync(underId)).StatusCode);
        Assert.Equal(1, await JournalEntryCountAsync());

        // >$5k → park + approve (1 JE) — the over-threshold invoice's ONLY issuer is the approval effect.
        var overId = await CreateDraftAsync(lineAmount: 12000m);
        var parked = await (await IssueAsync(overId)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, await JournalEntryCountAsync());   // still 1 — the over-threshold issue did NOT post
        var instanceId = parked.GetProperty("instanceId").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await ActionAsync(instanceId, "approve")).StatusCode);

        // EXACTLY TWO — one per invoice, no double-post on either path.
        Assert.Equal(2, await JournalEntryCountAsync());
    }

    [Fact(DisplayName = "Re-issuing an already-parked >$5k invoice is idempotent (resolves the SAME instance, posts no JE)")]
    public async Task OverThreshold_ReIssue_IsIdempotent()
    {
        var id = await CreateDraftAsync(lineAmount: 6000m);
        var first = await (await IssueAsync(id)).Content.ReadFromJsonAsync<JsonElement>();
        var second = await (await IssueAsync(id)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(first.GetProperty("instanceId").GetString(), second.GetProperty("instanceId").GetString());
        Assert.Equal(0, await JournalEntryCountAsync());

        // Only one open task for the invoice.
        var list = await _client.GetFromJsonAsync<JsonElement>(TasksRoute);
        var count = Data(list).EnumerateArray().Count(t => t.GetProperty("invoiceId").GetString() == id);
        Assert.Equal(1, count);
    }

    [Fact(DisplayName = "Actioning an unknown / non-parked instance id returns 404 (no existence leak, no dispatch)")]
    public async Task Action_UnknownInstance_NotFound()
    {
        var resp = await ActionAsync("invoice-approval:does-not-exist", "approve");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(0, await JournalEntryCountAsync());
    }

    [Fact(DisplayName = "An invalid decision is rejected with 400 before any dispatch")]
    public async Task Action_InvalidDecision_BadRequest()
    {
        var id = await CreateDraftAsync(lineAmount: 7000m);
        var parked = await (await IssueAsync(id)).Content.ReadFromJsonAsync<JsonElement>();
        var instanceId = parked.GetProperty("instanceId").GetString()!;

        var resp = await ActionAsync(instanceId, "yolo");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, await JournalEntryCountAsync());
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  CRASH-RESUME — a fresh engine over the SAME db re-running the approve trigger
    //  is an idempotent no-op (the SC1 / no-double-post guarantee across a restart)
    // ═════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "CRASH-RESUME: after approve posts EXACTLY ONE JE, a FRESH engine over the same db re-fires the approve → idempotent no-op → still EXACTLY ONE JE")]
    public async Task OverThreshold_Approve_CrashResume_NoDoublePost()
    {
        // Park an over-threshold invoice via the route.
        var id = await CreateDraftAsync(lineAmount: 11000m);
        var parked = await (await IssueAsync(id)).Content.ReadFromJsonAsync<JsonElement>();
        var instanceId = parked.GetProperty("instanceId").GetString()!;

        // First "process boot": a fresh engine over the same db drives the approve → EXACTLY ONE JE.
        var boot1Store = new NodeEfWorkflowStore(_factory);
        var boot1Dispatcher = new WorkflowTriggerDispatcher(
            boot1Store,
            new IWorkflowStepHandler[]
            {
                new InvoiceApprovalHandler(
                    NodeWorkflowDefinitions.InvoiceApprovalThresholdTable(),
                    new NodeLiveInvoiceApprovalContext()),
            });
        var boot1 = await boot1Dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, instanceId, InvoiceApprovalSteps.Approve, "{\"decision\":\"approve\"}"));
        Assert.Equal(WorkflowDispatchResult.Advanced, boot1);
        Assert.Equal(1, await JournalEntryCountAsync());

        // RESTART — a completely fresh store + dispatcher over the SAME db (simulating a crash + resume) re-
        // fires the SAME approve trigger. The instance is Completed → Terminal → NO re-issue. Even if it had
        // re-run the effect, the invoice is already Issued (the effect's early no-op) AND the JE's
        // ux_journal_entries_tenant_source_ref unique index on invoice:{id} is the durable backstop.
        var boot2Store = new NodeEfWorkflowStore(_factory);
        var boot2Dispatcher = new WorkflowTriggerDispatcher(
            boot2Store,
            new IWorkflowStepHandler[]
            {
                new InvoiceApprovalHandler(
                    NodeWorkflowDefinitions.InvoiceApprovalThresholdTable(),
                    new NodeLiveInvoiceApprovalContext()),
            });
        var boot2 = await boot2Dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.HumanAction, instanceId, InvoiceApprovalSteps.Approve, "{\"decision\":\"approve\"}"));
        Assert.Equal(WorkflowDispatchResult.Terminal, boot2);

        Assert.Equal(1, await JournalEntryCountAsync());   // NO double-post across the resume
        var detail = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/{id}");
        Assert.Equal("Issued", Data(detail).GetProperty("status").GetString());
    }
}
