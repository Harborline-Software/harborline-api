using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Entities;
using Harborline.Api.Kernel.Runtime.Teams;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

/// <summary>
/// MTW-2 2612-C — the WIRED workflow-effect branch of the node-signed attribution envelope
/// (council-verdict-2026-07-23 finding 1). <see cref="NodeAttributionEnvelopeTests"/> proves the enlister
/// / envelope in isolation over <see cref="NodeEfJournalStore"/>; the OTHER production caller — the
/// over-threshold approve effect that stages the approve-time invoice-issue JE directly onto the workflow
/// advance's unit-of-work (<see cref="NodeLiveInvoiceApprovalContext"/>, composed with the enlister at
/// <c>NodeWorkflowComposition.cs:162</c>) — had ZERO coverage: every workflow test used the
/// null-enlister ctor. This test closes that seam: an over-threshold approve, driven through the LIVE
/// routes over a full node graph WITH the audit enlister wired, must co-commit EXACTLY ONE node-signed
/// <see cref="NodeAuditEventRow"/> atomically with the Issued invoice + its JE, carrying the
/// same attribution as the in-request actor. The genuinely unbound branch retains the ruled operator
/// fallback, and that row's signature must VERIFY under the node key.
/// <para>
/// Like <see cref="NodeAttributionEnvelopeTests"/>, this asserts ATTESTATION INTEGRITY + TAMPER-EVIDENCE
/// and NEVER "impersonation-resistance": the node key attests, and a node can assert any member — true
/// per-member non-repudiation is the deferred passkey/roster-key future (board finding F9).
/// </para>
/// </summary>
[Trait("PlanCard", "MTW-2-2840")]
public sealed class NodeLiveInvoiceApprovalAuditAttributionTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private IDbContextFactory<LocalNodeDbContext> _factory = null!;
    private NodePrincipalSigner _signer = null!;
    private IOperationVerifier _verifier = null!;
    private MutableAttributionSource _attribution = null!;

    // A FROZEN clock — deterministic OccurredAt / JE + invoice stamps across the whole graph.
    private static readonly DateTimeOffset FrozenNow = new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly TimeProvider _clock = new FrozenClock(FrozenNow);

    private static readonly TenantId LocalTenantId =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    /// <summary>A party id this test's authorization gate denies — an override claim that resolves to nobody.</summary>
    private const string UnknownParty = "party-not-on-the-roster";

    private const string InvoicesRoute = "/api/local-node/invoices";
    private const string TasksRoute = "/api/local-node/approval-tasks";

    private static byte[] FixedSeed()
    {
        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++)
        {
            seed[i] = (byte)(0xB0 + i);
        }
        return seed;
    }

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

        _dir = Path.Combine(Path.GetTempPath(), "harborline-approval-audit-attr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "approval-audit-attr.db")};Pooling=False";

        // The production module set the engine + financial routes touch, PLUS the AuditEvent module so the
        // wired enlister's NodeAuditEventRow has its durable table (mirrors NodeAttributionEnvelopeTests).
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, WorkflowEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));

        _app = builder.Build();
        _factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _signer = new NodePrincipalSigner(FixedSeed());
        _verifier = new Ed25519Verifier();

        // Production-faithful composition — the SAME path the route uses (mirrors
        // InvoiceApprovalVerticalFlowTests), except the LIVE approval context is wired WITH the atomic-audit
        // enlister and the same caller-attribution source used by production composition.
        var invoices = new NodeEfInvoiceRepository(_factory);
        var numbering = new NodeEfInvoiceNumberingService(_factory, new ReplicaId("AA"));
        var journalStore = new NodeEfJournalStore(_factory, NodeJournalWriteAdapters.Create());
        // Ticket 272 slice 4: the overriding party is resolved by putting the CLAIM to this same gate, so a
        // party the gate does not allow to post this journal entry resolves to nobody. One party id is
        // denied here (nothing else in this file uses it) so both halves of that resolution are live.
        var gate = TestAuthorization.Gate(
            request => !string.Equals(request.Principal.Value, UnknownParty, StringComparison.Ordinal));
        // ONE resolver instance for both gates: the posting service's period gate and (ticket 272 slice 5)
        // the approve path's fifth approval fact. A period closed for one is closed for the other.
        var periods = new NodeEfPeriodResolver(_factory);
        var jePosting = new JournalPostingService(
            accounts: new NodeEfAccountResolver(_factory),
            periods:  periods,
            store:    journalStore,
            gate:     gate);
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

        var workflowStore = new NodeEfWorkflowStore(_factory);
        _attribution = new MutableAttributionSource();
        // THE SEAM UNDER TEST — the live context and audit enlister share one caller-attribution source,
        // so invoice metadata and its co-committed signed audit row name the same actor.
        var liveContext = new NodeLiveInvoiceApprovalContext(
            new NodeAuditWriteEnlister(_signer.Signer),
            _attribution);
        var approvalHandler = new InvoiceApprovalHandler(
            NodeWorkflowDefinitions.InvoiceApprovalThresholdTable(), liveContext);
        var dispatcher = new WorkflowTriggerDispatcher(workflowStore, new IWorkflowStepHandler[] { approvalHandler });
        var instantiation = new NodeWorkflowInstantiationService(workflowStore, _factory);
        var cutover = new NodeInvoiceApprovalCutover(
            instantiation, dispatcher, workflowStore, gate,
            new SeparationOfDutyEngine(), _approvalAudit, _signer.Signer, periods, invoices);
        var tasksReadModel = new NodeParkedTaskQueryReadModel(_factory);

        await SeedAccountAsync("1100", "Accounts Receivable", GLAccountType.Asset, AccountSubtype.AccountsReceivable);
        await SeedAccountAsync("4000", "Service Income", GLAccountType.Revenue, AccountSubtype.OperatingIncome);
        await SeedOpenPeriodAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            if (_attribution.Current is { } attribution)
            {
                http.Features.Set(new SelectedSessionRequestPrincipal(
                    accountId: "account-invoice-approver",
                    LocalTenantId,
                    new PrincipalUserId("user-invoice-approver"),
                    new CanonicalPartyReference(attribution.MemberPartyId),
                    attribution.MembershipId,
                    attribution.MembershipOwnerVersion,
                    [new PinnedGrantOwnerVersion("grant-invoice-approver", 1)],
                    attribution.AuthorizationEpoch,
                    attribution.SessionCorrelationId,
                    attribution.CoordinationCorrelationId));
            }
            await next(http);
        });
        InvoiceRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            invoiceRepoAccessor,
            invoiceNumberingAccessor,
            invoicePostingAccessor,
            NodeTestActiveTeam.Accessor, cutover, timeProvider: TimeProvider.System);
        InvoiceApprovalTaskRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            tasksReadModel,
            cutover,
            NodeTestActiveTeam.Accessor,
            _clock);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _signer?.Dispose();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    //  The wired detached workflow-effect audit-emission seam (council-verdict finding 1)
    // ═════════════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "WIRED workflow effect: a >$5k approve co-commits EXACTLY ONE node-signed operator-fallback NodeAuditEventRow atomically with the Issued invoice + JE, and the attestation VERIFIES (attestation integrity + tamper-evidence; NOT impersonation-resistance)")]
    public async Task OverThreshold_Approve_CoCommitsOneSignedOperatorFallbackAuditRow()
    {
        // Ticket 272: the issuer and the approver must be different parties — the same party doing both is
        // a self-approval the separation-of-duty engine refuses, which is its own test.
        _attribution.Current = Requester;

        // Park an over-threshold invoice — no JE, no audit row yet (the issue only parks the approval task).
        var invoiceId = await CreateDraftAsync(lineAmount: 7500m);
        Assert.Equal(0, await JournalEntryCountAsync());
        Assert.Equal(0, await AuditRowCountAsync());

        var issueResp = await IssueAsync(invoiceId);
        Assert.Equal(HttpStatusCode.Accepted, issueResp.StatusCode);
        var parked = await issueResp.Content.ReadFromJsonAsync<JsonElement>();
        var instanceId = parked.GetProperty("instanceId").GetString()!;
        Assert.Equal(0, await JournalEntryCountAsync());
        Assert.Equal(0, await AuditRowCountAsync());

        // Approve → the wired effect stages {JE + Issued invoice + node-signed audit row} onto ONE uow.
        _attribution.Current = null;
        var approveResp = await ActionAsync(instanceId, "approve");
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        // The invoice + JE co-committed (the mutation this audit row attests).
        Assert.Equal(1, await JournalEntryCountAsync());
        var issuedDetail = await _client.GetFromJsonAsync<JsonElement>($"{InvoicesRoute}/{invoiceId}");
        Assert.Equal("Issued", issuedDetail.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(NodeCallerParty.OperatorParty, (await InvoiceAsync(invoiceId)).UpdatedBy);

        await using var ctx = await _factory.CreateDbContextAsync();
        var row = Assert.Single(await ctx.Set<NodeAuditEventRow>().ToListAsync());
        Assert.Equal(NodeCallerParty.OperatorParty.Value, row.Actor);
        Assert.NotNull(row.Signature);
        using var payload = JsonDocument.Parse(row.Payload);
        var authority = payload.RootElement.GetProperty("authority");
        Assert.Equal(NodeAuditWriteEnlister.CarriedDecisionAttributionSchema,
            payload.RootElement.GetProperty("attribution").GetProperty("schema").GetString());
        Assert.Equal(NodeCallerParty.OperatorParty.Value, authority.GetProperty("principal").GetString());
        Assert.StartsWith("ledger:post@/records/", authority.GetProperty("act").GetString());
        Assert.Equal("journal-entry", authority.GetProperty("target").GetProperty("record_kind").GetString());
        Assert.True(_verifier.Verify(Assert.IsType<SignedOperation<string>>(
            NodeAuditSignaturePayload.TryReconstruct(row, _signer.Signer.IssuerId, row.Signature!))));
    }

    [Fact(DisplayName = "WIRED workflow effect: a request-bound non-operator member stamps the issued invoice UpdatedBy and its signed audit row")]
    public async Task OverThreshold_Approve_StampsRequestBoundMemberOnIssuedInvoice()
    {
        const string memberParty = "party-invoice-approver";
        // The issuer is a different party from the approver (272 — no self-approval).
        _attribution.Current = Requester;
        var approver = new NodeCallerAttribution(
            MemberPartyId: memberParty,
            MembershipId: "membership-invoice-approver",
            MembershipOwnerVersion: 3,
            SessionCorrelationId: "session-invoice-approver",
            CoordinationCorrelationId: "coordination-invoice-approver",
            AuthorizationEpoch: 7);

        var invoiceId = await CreateDraftAsync(lineAmount: 7500m);
        var issueResp = await IssueAsync(invoiceId);
        Assert.Equal(HttpStatusCode.Accepted, issueResp.StatusCode);
        var parked = await issueResp.Content.ReadFromJsonAsync<JsonElement>();
        var instanceId = parked.GetProperty("instanceId").GetString()!;

        _attribution.Current = approver;
        var approveResp = await ActionAsync(instanceId, "approve");
        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);

        var issued = await InvoiceAsync(invoiceId);
        Assert.Equal(InvoiceStatus.Issued, issued.Status);
        Assert.Equal(new PartyId(memberParty), issued.UpdatedBy);
        Assert.NotEqual(NodeCallerParty.OperatorParty, issued.UpdatedBy);

        await using var ctx = await _factory.CreateDbContextAsync();
        var row = Assert.Single(await ctx.Set<NodeAuditEventRow>().ToListAsync());
        Assert.Equal(memberParty, row.Actor);
        using var payload = JsonDocument.Parse(row.Payload);
        var authority = payload.RootElement.GetProperty("authority");
        Assert.Equal(memberParty, authority.GetProperty("principal").GetString());
        Assert.Equal(LocalTenantId.Value, authority.GetProperty("tenant").GetString());
        Assert.StartsWith("ledger:post@/records/", authority.GetProperty("act").GetString());
        Assert.NotEmpty(authority.GetProperty("grants").EnumerateArray());
        Assert.NotEmpty(authority.GetProperty("resolution").EnumerateArray());
        Assert.True(_verifier.Verify(Assert.IsType<SignedOperation<string>>(
            NodeAuditSignaturePayload.TryReconstruct(row, _signer.Signer.IssuerId, row.Signature!))));
    }

    /// <summary>The party that ISSUES the invoices in these tests — never the party that approves them.</summary>
    private static readonly NodeCallerAttribution Requester = new(
        MemberPartyId: "party-invoice-requester",
        MembershipId: "membership-invoice-requester",
        MembershipOwnerVersion: 2,
        SessionCorrelationId: "session-invoice-requester",
        CoordinationCorrelationId: "coordination-invoice-requester",
        AuthorizationEpoch: 7);

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

    private static object NewInvoiceBody(decimal lineAmount, string issueDate = "2026-03-01") => new
    {
        id = (string?)null,
        chartId = "CH-1",
        customerId = "customer-1",
        arAccountId = "1100",
        issueDate,
        dueDate = "2026-03-31",
        lines = new[]
        {
            new { description = "Consulting", quantity = 1m, unitPrice = lineAmount, incomeAccountId = "4000", taxCodeId = (string?)null },
        },
    };

    private async Task<string> CreateDraftAsync(decimal lineAmount, string issueDate = "2026-03-01")
    {
        var resp = await _client.PostAsJsonAsync(InvoicesRoute, NewInvoiceBody(lineAmount, issueDate));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("data").GetProperty("id").GetString()!;
    }

    private async Task<int> JournalEntryCountAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<JournalEntry>().CountAsync(j => j.TenantId == LocalTenantId);
    }

    private async Task<int> AuditRowCountAsync()
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<NodeAuditEventRow>().CountAsync();
    }

    private async Task<Invoice> InvoiceAsync(string id)
    {
        await using var ctx = await _factory.CreateDbContextAsync();
        return await ctx.Set<Invoice>().AsNoTracking()
            .SingleAsync(i => i.Id == new InvoiceId(id) && i.TenantId == LocalTenantId);
    }

    private async Task<HttpResponseMessage> IssueAsync(string id)
        => await _client.PostAsJsonAsync($"{InvoicesRoute}/{id}/issue", new { });

    private async Task<HttpResponseMessage> ActionAsync(
        string instanceId,
        string decision,
        string? note = null,
        string? overrideReason = null,
        string? overrideApprover = null)
        => await _client.PostAsJsonAsync(
            $"{TasksRoute}/{instanceId}/action",
            new { decision, note, overrideReason, overrideApprover });

    /// <summary>A fixed-instant <see cref="TimeProvider"/> — deterministic audit / JE / invoice stamps.</summary>
    private sealed class FrozenClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The trail the approve path records its separation-of-duty decision on (ticket 272).</summary>
    private readonly InMemoryAuditTrail _approvalAudit = new();

    /// <summary>The single stored separation-of-duty snapshot for this test's one approve attempt.</summary>
    private async Task<SeparationOfDutySnapshot> StoredApprovalAsync()
    {
        var records = new List<AuditRecord>();
        await foreach (var record in _approvalAudit.QueryAsync(
            new AuditQuery(LocalTenantId, NodeInvoiceApprovalCutover.ApprovalDecidedEventType)))
        {
            records.Add(record);
        }

        var snapshot = Assert.Single(records).AuthoritySnapshot;
        Assert.NotNull(snapshot);

        // The other four approval facts ride the same decision — all five come off the engine, and the
        // applied limit is the threshold built once in NodeWorkflowDefinitions, not a number read here.
        Assert.Equal(InvoiceApprovalSteps.DefinitionKey, snapshot.Policy!.PolicyId);
        Assert.Equal(NodeWorkflowDefinitions.InvoiceApprovalV1Version, snapshot.Policy.Version);
        // The literal v1 threshold, not the object under test: comparing the stored limit to the same
        // static it came from would pass however that static drifted.
        Assert.Equal(5000m, snapshot.AppliedLimit);
        Assert.Equal(NodeWorkflowDefinitions.InvoiceApprovalThreshold.Limit, snapshot.AppliedLimit);
        Assert.Equal(AuthorityLimitSourceKind.SigningLimitAgreement, snapshot.LimitSource!.Kind);
        // Ticket 272 slice 5: the fifth fact is READ from the period resolver for the invoice's chart and
        // issue date (2026-03-01, inside the seeded open period), not assumed. The literal, not the
        // engine's constant — comparing to the constant would pass however that constant drifted.
        Assert.Equal("Open", snapshot.PostingPeriodState);
        return snapshot.SeparationOfDuty!;
    }

    [Fact(DisplayName = "272: approving your own over-threshold invoice is REFUSED, and the refusal is stored as a Conflict naming its reason")]
    public async Task SelfApproval_IsRefused_AndStoredAsConflict()
    {
        _attribution.Current = Requester;
        var invoiceId = await CreateDraftAsync(lineAmount: 7500m);
        var issueResp = await IssueAsync(invoiceId);
        Assert.Equal(HttpStatusCode.Accepted, issueResp.StatusCode);
        var instanceId = (await issueResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("instanceId").GetString()!;

        // The SAME party that issued now approves.
        var approveResp = await ActionAsync(instanceId, "approve");

        Assert.Equal(HttpStatusCode.Conflict, approveResp.StatusCode);
        var body = await approveResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("separation_of_duty_refused", body.GetProperty("error").GetString());
        Assert.Equal(nameof(ApprovalRefusalReason.SelfApproval), body.GetProperty("reason").GetString());

        // Nothing posted, and the invoice is still Draft — the refusal is not merely a status code.
        Assert.Equal(0, await JournalEntryCountAsync());
        Assert.Equal(InvoiceStatus.Draft, (await InvoiceAsync(invoiceId)).Status);

        var stored = await StoredApprovalAsync();
        Assert.Equal(SeparationOfDutyResult.Conflict, stored.Result);
        Assert.Equal(AuthorityApprovalRefusalReason.SelfApproval, stored.RefusalReason);
    }

    [Fact(DisplayName = "272: a self-approval allowed by a named, reasoned overrider is stored as an Override with both halves")]
    public async Task OverriddenSelfApproval_IsStoredAsOverride()
    {
        _attribution.Current = Requester;
        var invoiceId = await CreateDraftAsync(lineAmount: 7500m);
        var issueResp = await IssueAsync(invoiceId);
        var instanceId = (await issueResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("instanceId").GetString()!;

        var approveResp = await ActionAsync(
            instanceId, "approve", overrideReason: "sole operator", overrideApprover: "party-overrider");

        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);
        Assert.Equal(1, await JournalEntryCountAsync());

        var stored = await StoredApprovalAsync();
        Assert.Equal(SeparationOfDutyResult.Override, stored.Result);
        Assert.Equal("sole operator", stored.OverrideReason);
        // Ticket 272 slice 4: the claim was put to the authorization gate for THIS journal entry and the
        // gate allowed it, so the record names a resolved party — not a string the caller asserted.
        Assert.Equal("party-overrider", stored.OverrideApprover);
        Assert.Equal(AuthorityApprovalRefusalReason.None, stored.RefusalReason);
    }

    [Fact(DisplayName = "272 s4: an overriding party the gate does not allow resolves to nobody — 409, no JE, and no party on the record")]
    public async Task UnresolvableOverrider_IsRefused_AndNamesNoParty()
    {
        _attribution.Current = Requester;
        var invoiceId = await CreateDraftAsync(lineAmount: 7500m);
        var issueResp = await IssueAsync(invoiceId);
        var instanceId = (await issueResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("instanceId").GetString()!;

        var approveResp = await ActionAsync(
            instanceId, "approve", overrideReason: "sole operator", overrideApprover: UnknownParty);

        Assert.Equal(HttpStatusCode.Conflict, approveResp.StatusCode);
        var body = await approveResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("separation_of_duty_refused", body.GetProperty("error").GetString());
        Assert.Equal(
            nameof(ApprovalRefusalReason.MissingOverrideApprover), body.GetProperty("reason").GetString());

        // The self-approval was NOT cleared by an unverified string: nothing posted, invoice still Draft.
        Assert.Equal(0, await JournalEntryCountAsync());
        Assert.Equal(InvoiceStatus.Draft, (await InvoiceAsync(invoiceId)).Status);

        var stored = await StoredApprovalAsync();
        Assert.Equal(SeparationOfDutyResult.Conflict, stored.Result);
        Assert.Equal(AuthorityApprovalRefusalReason.MissingOverrideApprover, stored.RefusalReason);
        Assert.Null(stored.OverrideApprover);
        // Nothing was recorded as an override at all: an override nobody could be named for is a conflict.
        Assert.Null(stored.OverrideReason);
    }

    [Fact(DisplayName = "272: an approver who is not the requester records a plain Pass with no refusal reason")]
    public async Task ApprovalByAnotherParty_IsStoredAsPass()
    {
        _attribution.Current = Requester;
        var invoiceId = await CreateDraftAsync(lineAmount: 7500m);
        var issueResp = await IssueAsync(invoiceId);
        var instanceId = (await issueResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("instanceId").GetString()!;

        _attribution.Current = null; // the operator party approves — not the requester
        var approveResp = await ActionAsync(instanceId, "approve");

        Assert.Equal(HttpStatusCode.OK, approveResp.StatusCode);
        Assert.Equal(1, await JournalEntryCountAsync());

        var stored = await StoredApprovalAsync();
        Assert.Equal(SeparationOfDutyResult.Pass, stored.Result);
        Assert.Null(stored.OverrideReason);
        Assert.Equal(AuthorityApprovalRefusalReason.None, stored.RefusalReason);
    }

    [Fact(DisplayName = "272 s5: an approve into a CLOSED posting period is refused before any journal entry, and the closed state is the recorded fact")]
    public async Task ClosedPostingPeriod_IsRefused_AndTheClosedStateIsRecorded()
    {
        _attribution.Current = Requester;
        var invoiceId = await CreateDraftAsync(lineAmount: 7500m);
        var issueResp = await IssueAsync(invoiceId);
        Assert.Equal(HttpStatusCode.Accepted, issueResp.StatusCode);
        var instanceId = (await issueResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("instanceId").GetString()!;

        // Close the period covering the invoice's issue date AFTER it parked, so nothing else about the
        // approve changes: a different party approves, which without this would be a plain Pass.
        await LockSeededPeriodAsync();
        _attribution.Current = null;

        var approveResp = await ActionAsync(instanceId, "approve");

        Assert.Equal(HttpStatusCode.Conflict, approveResp.StatusCode);
        var body = await approveResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("separation_of_duty_refused", body.GetProperty("error").GetString());
        Assert.Equal(
            nameof(ApprovalRefusalReason.ClosedPostingPeriod), body.GetProperty("reason").GetString());

        // Refused BEFORE the act, not after it: no journal entry, no co-committed audit row, still Draft.
        Assert.Equal(0, await JournalEntryCountAsync());
        Assert.Equal(0, await AuditRowCountAsync());
        Assert.Equal(InvoiceStatus.Draft, (await InvoiceAsync(invoiceId)).Status);

        // And the recorded fact is the state the resolver actually reported — the refusal is auditable.
        var records = new List<AuditRecord>();
        await foreach (var record in _approvalAudit.QueryAsync(
            new AuditQuery(LocalTenantId, NodeInvoiceApprovalCutover.ApprovalDecidedEventType)))
        {
            records.Add(record);
        }

        var snapshot = Assert.Single(records).AuthoritySnapshot!;
        Assert.Equal(nameof(FiscalPeriodStatus.Locked), snapshot.PostingPeriodState);
        Assert.Equal(AuthorityApprovalRefusalReason.ClosedPostingPeriod, snapshot.SeparationOfDuty!.RefusalReason);
        // The duty verdict itself is unaffected by the period — a non-requester is still a Pass.
        Assert.Equal(SeparationOfDutyResult.Pass, snapshot.SeparationOfDuty.Result);
    }

    [Fact(DisplayName = "272 s5: an approve whose entry date NO period covers is refused, and the recorded fact says so rather than assuming Open")]
    public async Task NoPeriodForTheEntryDate_IsRefused_AndRecordedAsSuch()
    {
        // 2027 is outside the one seeded period (2026-01-01..2026-12-31), so the resolver finds nothing.
        _attribution.Current = Requester;
        var invoiceId = await CreateDraftAsync(lineAmount: 7500m, issueDate: "2027-03-01");
        var issueResp = await IssueAsync(invoiceId);
        Assert.Equal(HttpStatusCode.Accepted, issueResp.StatusCode);
        var instanceId = (await issueResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("instanceId").GetString()!;

        _attribution.Current = null; // a different party approves — the only refusal left is the period
        var approveResp = await ActionAsync(instanceId, "approve");

        Assert.Equal(HttpStatusCode.Conflict, approveResp.StatusCode);
        var body = await approveResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            nameof(ApprovalRefusalReason.ClosedPostingPeriod), body.GetProperty("reason").GetString());
        Assert.Equal(0, await JournalEntryCountAsync());
        Assert.Equal(InvoiceStatus.Draft, (await InvoiceAsync(invoiceId)).Status);

        var records = new List<AuditRecord>();
        await foreach (var record in _approvalAudit.QueryAsync(
            new AuditQuery(LocalTenantId, NodeInvoiceApprovalCutover.ApprovalDecidedEventType)))
        {
            records.Add(record);
        }

        // Not null (which would say nobody looked) and not "Open" (which would be a lie): the resolver
        // looked and found no period for the date.
        Assert.Equal(
            NodeInvoiceApprovalCutover.NoPeriodForDate,
            Assert.Single(records).AuthoritySnapshot!.PostingPeriodState);
    }

    /// <summary>Locks the seeded fiscal period covering the invoices' 2026-03-01 issue date.</summary>
    private async Task LockSeededPeriodAsync()
    {
        var closedAt = new Instant(System.TimeProvider.System.GetUtcNow());
        await using var ctx = await _factory.CreateDbContextAsync();
        var period = await ctx.Set<FiscalPeriod>().AsNoTracking().SingleAsync();
        var locked = period with
        {
            Status = FiscalPeriodStatus.Locked,
            SoftClosedAtUtc = closedAt,
            LockedAtUtc = closedAt,
            Version = period.Version + 1,
        };
        // A valid Locked row, not just a flipped enum — the resolver reads a period the domain accepts.
        Assert.Empty(locked.Validate());
        ctx.Set<FiscalPeriod>().Update(locked);
        await ctx.SaveChangesAsync();
    }

    private sealed class MutableAttributionSource : INodeCallerAttributionSource
    {
        internal NodeCallerAttribution? Current { get; set; }

        public NodeCallerAttribution? TryResolveCurrent() => Current;
    }
}

/// <summary>
/// Composition-level closure for the workflow audit seam. This boots Program's completed provider,
/// resolves the shipping SQLCipher store, and drives only the hosted invoice/approval endpoints.
/// Removing the audit enlister registration from NodeWorkflowComposition leaves no row and kills this test.
/// </summary>
[Collection("Harborline process environment")]
[Trait("PlanCard", "MTW-2-2840")]
public sealed class NodeLiveInvoiceApprovalAuditAttributionRealCompositionTests
{
    private const string SessionToken = "ticket-199-workflow-composition";

    /// <summary>The delegated approver this installation grants — a real, gate-resolvable second party.</summary>
    private const string CompositionOverrider = "party-composition-overrider";
    private const string InvoicesRoute = "/api/local-node/invoices";
    private const string TasksRoute = "/api/local-node/approval-tasks";

    [Fact(DisplayName = "REAL composition: approving an issued invoice co-commits the admitting decision in the shipping audit row")]
    public async Task ApproveThroughShippingEndpoints_CoCommitsCarriedDecisionAuditRow()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(), $"ticket-199-workflow-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        using var environment = new CompositionEnvironment(dataDirectory);
        var started = false;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var baseAddress = await LocalNodeHostRuntime.StartAsync(
                SessionToken, dataDirectory, timeout.Token);
            started = true;

            var services = Assert.IsAssignableFrom<IServiceProvider>(LocalNodeHostRuntime.CurrentServices);
            var factory = services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var activeTeam = services.GetRequiredService<IActiveTeamAccessor>().Active;
            Assert.NotNull(activeTeam);
            var tenant = ActiveTeamTenantContext.ProjectTenantId(activeTeam.TeamId);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            await SeedGrantAsync(services, tenant, NodeCallerParty.OperatorParty.Value);
            // Ticket 272 slice 4: the overriding party is resolved through the authorization gate, so it
            // must be a real party that may itself post this journal entry. A second grant makes this
            // installation's delegated approver an actual second set of eyes rather than a typed string.
            await SeedGrantAsync(services, tenant, CompositionOverrider);
            await SeedFinancialPostingPrerequisitesAsync(factory, today);

            using var client = new HttpClient { BaseAddress = baseAddress };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", SessionToken);

            using var create = await client.PostAsJsonAsync(InvoicesRoute, new
            {
                id = (string?)null,
                chartId = "CH-1",
                customerId = "ticket-199-customer",
                arAccountId = "1100",
                issueDate = today.ToString("yyyy-MM-dd"),
                dueDate = today.AddDays(30).ToString("yyyy-MM-dd"),
                lines = new[]
                {
                    new
                    {
                        description = "Composition audit proof",
                        quantity = 1m,
                        unitPrice = 7500m,
                        incomeAccountId = "4000",
                        taxCodeId = (string?)null,
                    },
                },
            }, timeout.Token);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            var created = await create.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var invoiceId = created.GetProperty("data").GetProperty("id").GetString()!;

            using var issue = await client.PostAsJsonAsync(
                $"{InvoicesRoute}/{invoiceId}/issue", new { }, timeout.Token);
            Assert.Equal(HttpStatusCode.Accepted, issue.StatusCode);
            var parked = await issue.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var instanceId = parked.GetProperty("instanceId").GetString()!;

            // Ticket 272: the composition runs on ONE session principal, so this approve is a self-approval
            // the separation-of-duty engine refuses. It stands only as a clause-2 OVERRIDE — a mandatory
            // recorded reason plus an overriding party who is not the requester AND whom the authorization
            // gate allows to post this entry (slice 4). Without the override the route answers 409
            // separation_of_duty_refused and nothing posts.
            using var approve = await client.PostAsJsonAsync(
                $"{TasksRoute}/{instanceId}/action",
                new
                {
                    decision = "approve",
                    note = "ticket 199",
                    overrideReason = "single-operator installation; approval delegated",
                    overrideApprover = CompositionOverrider,
                },
                timeout.Token);
            Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

            await using var context = await factory.CreateDbContextAsync(timeout.Token);
            Assert.Equal(1, await context.Set<JournalEntry>()
                .CountAsync(entry => entry.TenantId == tenant, timeout.Token));
            var row = Assert.Single(await context.Set<NodeAuditEventRow>()
                .Where(entry => entry.TenantId == tenant.Value)
                .ToListAsync(timeout.Token));
            using var payload = JsonDocument.Parse(row.Payload);
            var root = payload.RootElement;
            var authority = root.GetProperty("authority");
            Assert.Equal(NodeAuditWriteEnlister.CarriedDecisionAttributionSchema,
                root.GetProperty("attribution").GetProperty("schema").GetString());
            Assert.Equal(row.Actor, authority.GetProperty("principal").GetString());
            Assert.Equal(tenant.Value, authority.GetProperty("tenant").GetString());
            Assert.StartsWith("ledger:post@/records/", authority.GetProperty("act").GetString());
            Assert.Equal("journal-entry",
                authority.GetProperty("target").GetProperty("record_kind").GetString());
            Assert.NotEmpty(authority.GetProperty("grants").EnumerateArray());
            Assert.NotEmpty(authority.GetProperty("resolution").EnumerateArray());
        }
        finally
        {
            if (started)
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await LocalNodeHostRuntime.StopAsync(stopTimeout.Token);
            }

            TryDelete(dataDirectory);
        }
    }

    private static async Task SeedFinancialPostingPrerequisitesAsync(
        IDbContextFactory<LocalNodeDbContext> factory,
        DateOnly today)
    {
        await using var context = await factory.CreateDbContextAsync();
        context.Set<GLAccount>().AddRange(
            GLAccount.Create(
                id: new GLAccountId("1100"), chartId: new ChartOfAccountsId("CH-1"),
                code: "1100", name: "Accounts Receivable", type: GLAccountType.Asset,
                subtype: AccountSubtype.AccountsReceivable, currency: "USD", isPostable: true,
                createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow())),
            GLAccount.Create(
                id: new GLAccountId("4000"), chartId: new ChartOfAccountsId("CH-1"),
                code: "4000", name: "Service Income", type: GLAccountType.Revenue,
                subtype: AccountSubtype.OperatingIncome, currency: "USD", isPostable: true,
                createdAtUtc: new Instant(System.TimeProvider.System.GetUtcNow())));
        context.Set<FiscalPeriod>().Add(FiscalPeriod.CreateOpen(
            FiscalPeriodId.NewId(),
            new ChartOfAccountsId("CH-1"),
            new FiscalYearId($"FY-{today.Year}"),
            FiscalPeriodKind.Monthly,
            today.ToString("yyyy-MM"),
            new DateOnly(today.Year, today.Month, 1),
            new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)),
            new Instant(System.TimeProvider.System.GetUtcNow())));
        await context.SaveChangesAsync();
    }

    private static async Task SeedGrantAsync(IServiceProvider services, TenantId tenant, string party)
    {
        var at = DateTimeOffset.UtcNow;
        var principal = new ActorId(party);
        await services.GetRequiredService<IGrantStore>().AppendAsync(
            tenant,
            new AccessGrant(
                GrantId.New(), tenant, principal, RoleReference.Administrator, ScopeExpression.Parse("/"),
                GrantResidency.Cache, new GrantValidity(at.AddMinutes(-1), at.AddHours(1)),
                GranterKind.Person, principal, at.AddMinutes(-1),
                new GrantProvenance(
                    GrantSourceKind.Manual,
                    new GrantReason(GrantReasonCodes.Manual, $"ticket-199-real-composition:{party}"),
                    principal),
                at.AddMinutes(-1)),
            $"ticket-199-real-composition:{party}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort fixture cleanup must not replace the workflow assertion.
        }
    }

    private sealed class CompositionEnvironment : IDisposable
    {
        private static readonly string[] Keys =
        [
            "DOTNET_ENVIRONMENT",
            "ASPNETCORE_ENVIRONMENT",
            "ASPNETCORE_URLS",
            "LocalNode__HealthPort",
            "LocalNode__RootSeedHex",
            "LocalNode__WebClient__Enabled",
            "LocalNode__WebClient__LlmUpstreamBase",
            "LocalNode__SchedulingDogfood__Enabled",
            "LocalNode__MultiTeam__Enabled",
            "HARBORLINE_TEST_INSTALL_FOOTPRINT_ROOT",
            "Logging__EventLog__LogLevel__Default",
        ];

        private readonly Dictionary<string, string?> _previous = Keys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        internal CompositionEnvironment(string dataDirectory)
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
            Environment.SetEnvironmentVariable("LocalNode__HealthPort", "0");
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", new string('7', 64));
            Environment.SetEnvironmentVariable("LocalNode__WebClient__Enabled", "false");
            Environment.SetEnvironmentVariable("LocalNode__WebClient__LlmUpstreamBase", null);
            Environment.SetEnvironmentVariable("LocalNode__SchedulingDogfood__Enabled", "false");
            Environment.SetEnvironmentVariable("LocalNode__MultiTeam__Enabled", "false");
            Environment.SetEnvironmentVariable("HARBORLINE_TEST_INSTALL_FOOTPRINT_ROOT", dataDirectory);
            Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", "None");
        }

        public void Dispose()
        {
            foreach (var pair in _previous)
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }
}
