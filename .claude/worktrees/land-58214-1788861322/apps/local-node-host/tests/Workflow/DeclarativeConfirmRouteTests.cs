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
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Blocks.Workflow.Interpreter;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Entities;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// ROUTE-LEVEL end-to-end tests for the generic CP-effect-confirmation surface (PR (c) — the Harborline App "Effect
/// Confirmations" backend). Hosts the REAL <see cref="DeclarativeConfirmRoutes"/> + <see cref="WorkflowRunReportRoutes"/>
/// over the REAL declarative interpreter + broker-PEP + effect factory on an in-process Kestrel listener over a
/// temp SQLite store, driven by a real <see cref="HttpClient"/> — the same composition
/// <see cref="HostedDeclarativeWorkflowApiEndpoint"/> wires (no test/prod drift).
/// <para>
/// <b>FU-2 (the #1709 verdict follow-up).</b> Unlike the W-8 e2e (<see cref="DeclarativeWorkflowExecutionEndToEndTests"/>)
/// which STUBS <see cref="IWorkflowConfirmationContext"/> with a fixed confirmer, this test exercises the REAL
/// server-side confirmer resolution through the confirm ROUTE: the REAL
/// <see cref="NodeWorkflowConfirmationContext"/> resolves the confirmer through its own scope via
/// <see cref="IPartyContext"/> + the REAL <see cref="NodePrincipalKindResolver"/> (human). SoD passes because
/// the resolved confirmer party is a real human distinct from the fixed non-human engine proposer — so a POST
/// approve posts EXACTLY ONE balanced JE through the broker. (The production party seam
/// <c>NodeOperatorPartyResolver</c> is separately covered by the invoice vertical-flow + draft tests; this test
/// pins the confirmation-context + kind-resolver reals the ledger flagged as uncovered.)
/// </para>
/// </summary>
public sealed class DeclarativeConfirmRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private readonly List<AuthorizationGateRequest> _gateRequests = [];

    private static readonly TenantId LocalTenantId =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    /// <summary>A real human confirmer party, distinct from the non-human engine proposer (so SoD passes).</summary>
    private static readonly Guid OperatorPartyId = Guid.Parse("40000000-0000-0000-0000-0000000000c1");

    private const string ConfirmRoute = "/api/local-node/workflow-confirmations";
    private const string ReportRoute = "/api/local-node/workflow-run-report";

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-declarative-confirm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "confirm-test.db")};Pooling=False";

        // The production module set the interpreter + JE effect touch.
        builder.Services.AddTestKernelClock();
        builder.Services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.LocalNodeHost.Data.Audit.AuditEventEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(o => o.UseSqlite(connectionString));
        builder.Services.AddSingleton<IJournalStore>(sp => new NodeEfJournalStore(
            sp.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>(), NodeJournalWriteAdapters.Create()));
        builder.Services.AddNodeFinancialPosting();
        builder.Services.AddSingleton<IAccountResolver, AlwaysPostableAccountResolver>();
        builder.Services.AddSingleton<IPeriodResolver>(new InMemoryPeriodResolver());

        // The durable engine (adds the workflow entity module + NodeEfWorkflowStore + the dispatcher + broker) +
        // the definition store (authoring + the re-validating execution face).
        builder.Services.AddSingleton(TestAuthorization.Gate(true, _gateRequests.Add));
        builder.Services.AddNodeWorkflowEngine();
        var entityStore = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        builder.Services.AddSingleton(entityStore);
        builder.Services.AddSingleton<IEntityStore>(new InMemoryEntityStoreReader(entityStore));
        builder.Services.AddEntityStoreWorkflowDefinitionStore(_ => entityStore);

        // The party seam the REAL NodeWorkflowConfirmationContext resolves inside its own scope. A minimal real
        // IPartyContext (a fixed non-engine human party) — the two reals FU-2 pins are NodeWorkflowConfirmationContext
        // + NodePrincipalKindResolver, both registered by AddNodeDeclarativeWorkflowExecution below.
        builder.Services.AddScoped<IPartyContext>(_ => new FixedPartyContext(OperatorPartyId));

        // The REAL interpreter + confirmation context + kind resolver + effect factory + the confirmation /
        // report read models (this is the exact production slice — no FixedConfirmation stub).
        builder.Services.AddNodeDeclarativeWorkflowExecution();

        _app = builder.Build();
        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // Seed the three-way-match definition + two parked runs (mirrors the dev seeder / W-8 e2e).
        var defStore = _app.Services.GetRequiredService<IWorkflowDefinitionStore>();
        var workflowStore = _app.Services.GetRequiredService<IWorkflowStore>();
        var dispatcher = _app.Services.GetRequiredService<IWorkflowTriggerDispatcher>();
        var lifecycle = TestAuthorization.WorkflowLifecycle(
            defStore, Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.RoleGate());
        var seedAuthority = new Harborline.Api.Foundation.Authorization.AuthorizationWriteContext(
            new ActorId("test-workflow-seed"), LocalTenantId,
            new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero));
        var seedDecision = await lifecycle.DecideAsync(
            NodeThreeWayMatchWorkflowSeed.DefinitionKey, seedAuthority);
        await NodeThreeWayMatchWorkflowSeed.EnsurePublishedAsync(
            lifecycle, LocalTenantId.Value, seedDecision);
        await ParkAsync(workflowStore, dispatcher, billId: "bill-100", amount: 4200m, memo: "Vendor bill (approve)");
        await ParkAsync(workflowStore, dispatcher, billId: "bill-200", amount: 999m, memo: "Vendor bill (reject)");

        // Map the SAME production routes the hosted endpoint wires.
        var confirmations = _app.Services.GetRequiredService<NodeWorkflowConfirmationReadModel>();
        var report = _app.Services.GetRequiredService<NodeWorkflowRunReportReadModel>();
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        var deviceReachable = _app.MapDeviceReachableProductDataGroup();
        DeclarativeConfirmRoutes.Map(
            deviceReachable,
            confirmations,
            dispatcher,
            NodeTestActiveTeam.Accessor,
            _app.Services.GetRequiredService<AuthorizationGate>(),
            _app.Services.GetRequiredService<TimeProvider>());
        WorkflowRunReportRoutes.Map(deviceReachable, report, NodeTestActiveTeam.Accessor);

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

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task ParkAsync(IWorkflowStore store, IWorkflowTriggerDispatcher dispatcher, string billId, decimal amount, string memo)
    {
        var instanceId = await NodeThreeWayMatchWorkflowSeed.InstantiateAsync(
            store, LocalTenantId, billId: billId, amount: amount,
            expenseAccount: "6000", payableAccount: "2000", memo: memo);
        var parked = await dispatcher.DispatchAsync(
            WorkflowTrigger.For(WorkflowTriggerKind.Event, instanceId, NodeThreeWayMatchWorkflowSeed.MatchingState),
            TestAuthorization.AllowedDecision(LocalTenantId, instanceId));
        Assert.Equal(WorkflowDispatchResult.Parked, parked);
    }

    private static string InstanceIdFor(string billId) => NodeThreeWayMatchWorkflowSeed.DefinitionKey + ":" + billId;

    private async Task<JsonElement> GetJsonAsync(string path) => await _client.GetFromJsonAsync<JsonElement>(path);

    private static JsonElement Data(JsonElement doc) => doc.GetProperty("data");

    private async Task<HttpResponseMessage> ActionAsync(string instanceId, string decision, string? note = null)
        => await _client.PostAsJsonAsync($"{ConfirmRoute}/{instanceId}/action", new { decision, note });

    private async Task<int> JournalEntryCountAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId);
    }

    // ═════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "GET lists interpreter-parked CP confirmations WITH the FE-1 cp-approval-basis (capability + definition/state + verbs)")]
    public async Task List_shows_the_parked_confirmation_with_its_basis()
    {
        var listDoc = await GetJsonAsync(ConfirmRoute);
        var confirmation = Data(listDoc).EnumerateArray()
            .Single(c => c.GetProperty("instanceId").GetString() == InstanceIdFor("bill-100"));

        Assert.Equal(NodeThreeWayMatchWorkflowSeed.DefinitionKey, confirmation.GetProperty("definitionKey").GetString());
        Assert.Equal(NodeThreeWayMatchWorkflowSeed.Version, confirmation.GetProperty("definitionVersion").GetString());
        Assert.Equal(NodeThreeWayMatchWorkflowSeed.ReviewState, confirmation.GetProperty("step").GetString());
        Assert.Equal(NodeLedgerPostingEffect.CapabilityRef, confirmation.GetProperty("capabilityRef").GetString());
        Assert.Equal("bill-100", confirmation.GetProperty("subjectId").GetString());
        Assert.Equal(4200d, confirmation.GetProperty("amount").GetDouble());
        Assert.False(string.IsNullOrEmpty(confirmation.GetProperty("postingPreview").GetString()), "FE-1: the basis must carry a posting preview");
        Assert.Equal("workflow-engine", confirmation.GetProperty("proposer").GetString());
        var verbs = confirmation.GetProperty("allowedOutcomes").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(new[] { "approve", "reject", "send-back" }, verbs);
    }

    [Fact(DisplayName = "FU-2: POST approve resolves the REAL confirmer server-side (distinct human) → SoD passes → EXACTLY ONE balanced JE + the run reports as executed")]
    public async Task Approve_through_real_confirmer_posts_exactly_one_balanced_je()
    {
        var instanceId = InstanceIdFor("bill-100");
        Assert.Equal(0, await JournalEntryCountAsync());
        _gateRequests.Clear();

        var approveResp = await ActionAsync(instanceId, "approve");
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);
        var body = await approveResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("advanced", body.GetProperty("outcome").GetString());

        // EXACTLY ONE balanced JE — Debit 6000 / Credit 2000, both 4200 — posted through the SoD-gated broker
        // (the confirmer was resolved by the REAL NodeWorkflowConfirmationContext, distinct human vs the engine).
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var jes = await ctx.Set<JournalEntry>().Where(j => j.TenantId == LocalTenantId).ToListAsync();
            var je = Assert.Single(jes);
            Assert.Equal(4200m, je.Lines.Sum(l => l.Debit));
            Assert.Equal(4200m, je.Lines.Sum(l => l.Credit));
            Assert.Contains(je.Lines, l => l.AccountId == new GLAccountId("6000") && l.Debit == 4200m);
            Assert.Contains(je.Lines, l => l.AccountId == new GLAccountId("2000") && l.Credit == 4200m);
            Assert.Equal(JournalEntryStatus.Posted, je.Status);
        }

        // The confirmation cleared from the surface; the run now appears in the executed-run report.
        var afterList = await GetJsonAsync(ConfirmRoute);
        Assert.DoesNotContain(Data(afterList).EnumerateArray(), c => c.GetProperty("instanceId").GetString() == instanceId);

        var report = await GetJsonAsync(ReportRoute);
        var run = Data(report).EnumerateArray().Single(r => r.GetProperty("instanceId").GetString() == instanceId);
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal(NodeThreeWayMatchWorkflowSeed.PostedState, run.GetProperty("finalStep").GetString());
        Assert.True(run.GetProperty("effectPosted").GetBoolean());
        Assert.Equal(NodeLedgerPostingEffect.CapabilityRef, run.GetProperty("capabilityRef").GetString());

        // The confirmation was recorded for the D-INV-7 override-rate metric.
        Assert.Equal(1, _app.Services.GetRequiredService<CountingWorkflowApprovalDecisionSink>().ConfirmedCount);

        // A redelivered approve on the now-completed instance is an idempotent no-op — the task is gone (404).
        var redeliver = await ActionAsync(instanceId, "approve");
        Assert.Equal(HttpStatusCode.NotFound, redeliver.StatusCode);
        Assert.Equal(1, await JournalEntryCountAsync());
    }

    [Fact]
    public async Task WorkflowEffect_DoesNotReauthorizeSynchronousReaction()
    {
        _gateRequests.Clear();
        var response = await ActionAsync(InstanceIdFor("bill-100"), "approve");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Collection(
            _gateRequests,
            request => Assert.Equal(TeamRolePermissions.RecordsWrite, request.Act.Operation.Value),
            request => Assert.Equal(TeamRolePermissions.LedgerPost, request.Act.Operation.Value));
        Assert.Equal(1, await JournalEntryCountAsync());
    }

    [Fact(DisplayName = "POST reject posts NO JE and records an override; the run reports as executed with no effect")]
    public async Task Reject_posts_no_je_and_records_an_override()
    {
        var instanceId = InstanceIdFor("bill-200");

        var rejectResp = await ActionAsync(instanceId, "reject", note: "duplicate bill");
        Assert.Equal(HttpStatusCode.OK, rejectResp.StatusCode);
        Assert.Equal(0, await JournalEntryCountAsync());
        Assert.Equal(1, _app.Services.GetRequiredService<CountingWorkflowApprovalDecisionSink>().OverriddenCount);

        var report = await GetJsonAsync(ReportRoute);
        var run = Data(report).EnumerateArray().Single(r => r.GetProperty("instanceId").GetString() == instanceId);
        Assert.Equal("Completed", run.GetProperty("status").GetString());
        Assert.Equal("rejected", run.GetProperty("finalStep").GetString());
        Assert.False(run.GetProperty("effectPosted").GetBoolean());
    }

    [Fact(DisplayName = "An unknown / non-parked instance id returns 404 (no existence leak, no dispatch)")]
    public async Task Action_unknown_instance_not_found()
    {
        var resp = await ActionAsync("three-way-match.v1:does-not-exist", "approve");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(0, await JournalEntryCountAsync());
    }

    [Fact(DisplayName = "An invalid decision is rejected with 400 before any dispatch")]
    public async Task Action_invalid_decision_bad_request()
    {
        var resp = await ActionAsync(InstanceIdFor("bill-100"), "yolo");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, await JournalEntryCountAsync());
    }

    /// <summary>A minimal real <see cref="IPartyContext"/> — a fixed non-engine human party (the party seam dependency).</summary>
    private sealed class FixedPartyContext(Guid partyId) : IPartyContext
    {
        public ValueTask<Guid> GetCurrentPartyIdAsync(CancellationToken ct = default) => ValueTask.FromResult(partyId);
    }
}
