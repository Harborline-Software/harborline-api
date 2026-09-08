using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.Reports;
using Harborline.Api.Blocks.Reports.Cartridges.TrialBalance;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// MTW-2 #3381 — the acting member is the report-run provenance principal.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect.</b> Every report run derived its <see cref="ReportExecutionContext.RequestedBy"/>
/// principal from a per-install operator literal held in a route-local field, so all six report kinds
/// recorded the SAME principal for every member. A member whose report provenance principal is a
/// constant has not been correctly identified — an ATTRIBUTION failure, which CIC's 2026-07-29 ruling
/// places inside MTW-2 (permissions remain MTW-3; nothing here enforces anything, and the route's
/// security boundary remains the node's loopback listener).
/// </para>
/// <para>
/// <b>What "the recorded provenance" is on THIS surface.</b> Unlike the financial write surfaces, a
/// report run is a pure read projection: it persists nothing, so there is no durable row to read back.
/// The provenance principal's terminus is the cartridge's <see cref="ReportExecutionContext"/> — the
/// furthest point the value travels in the system, and the same point the reports substrate's own
/// <c>ReportRunnerTests</c> asserts it at. So this test captures it there, through the REAL
/// <see cref="ReportRunner"/>, rather than asserting an HTTP status (which is 200 either way).
/// </para>
/// <para>
/// <b>Why it bites.</b> It carries the assertion shape of the wave's two-user acceptance E2E
/// (<c>Mtw2TwoUserAcceptanceE2E</c> — two real members act; the recorded actors must be DISTINCT and
/// must not collapse to the single-operator identity) onto this surface. Because the route hashes its
/// input into an opaque principal, the operator identity can never appear as a readable substring; the
/// meaningful negative is therefore the principal DERIVED FROM the operator identity, which this test
/// computes independently. Against the pre-fix constant both members record exactly that value and the
/// assertions fail (observed red before the fix, per FLEET-0007).
/// </para>
/// <para>
/// <b>Real vs substituted.</b> REAL: the production <see cref="ReportsRoutes"/> handlers on a real
/// in-process Kestrel listener, the real <see cref="ReportRunner"/> and cartridge registry, the real
/// single-device chart guard over a real EF store, and the real <c>HttpContext.Features</c>
/// selected-session seam the production authority binds (set here by one test middleware, exactly as
/// <c>NodeInvoiceRouteBoundPrincipalActorStampTests</c> does). SUBSTITUTED: the trial-balance cartridge
/// body, which is replaced by a recording one so the execution context it receives is observable — the
/// ledger computation is not the seam under test and is covered by <c>ReportsRouteTests</c>.
/// </para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3381")]
public sealed class NodeReportsRouteActingMemberProvenanceTests : IAsyncLifetime
{
    /// <summary>Selects which member the binding middleware puts on the request (absent ⇒ no principal bound).</summary>
    private const string ActingMemberHeader = "X-Test-Acting-Member";

    private const string AliceParty = "party:alice-3381";
    private const string BobParty = "party:bob-3381";

    private const string TrialBalanceRoute = "/api/local-node/reports/trial-balance";

    private static readonly TenantId LocalTenantId =
        ActiveTeamTenantContext.ProjectTenantId(NodeTestActiveTeam.TestTeamId);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private RecordingTrialBalanceCartridge _cartridge = null!;
    private ChartOfAccountsId _chartId;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-reports-provenance-3381-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "reports-provenance.db")};Pooling=False";

        builder.Services.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
        builder.Services.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
        builder.Services.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));

        _app = builder.Build();

        var factory = _app.Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        // The install chart the route's single-device guard resolves the request's chartId against.
        _chartId = await SeedInstallChartAsync(factory);

        // The REAL runner over the REAL registry; only the cartridge BODY is a recorder, so the execution
        // context the route's principal lands in is observable.
        _cartridge = new RecordingTrialBalanceCartridge();
        var registry = new ReportCartridgeRegistry();
        registry.Register<TrialBalanceParameters, TrialBalanceResult>(_cartridge);
        var runner = new ReportRunner(
            registry, new FixedSnapshotMarkerSource(), TimeProvider.System, new ReportRunnerOptions());

        // The seam the production selected-session authority uses: a request-scoped principal on
        // HttpContext.Features. Which member (if any) is bound is chosen per request by a header, so ONE
        // host serves two distinct acting members — the two-real-member shape this card is about.
        _app.Use(async (HttpContext http, RequestDelegate next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            if (http.Request.Headers.TryGetValue(ActingMemberHeader, out var party) &&
                !string.IsNullOrWhiteSpace(party))
            {
                http.Features.Set(BoundPrincipal(party!));
            }

            await next(http);
        });

        ReportsRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            runner,
            factory,
            NodeTestActiveTeam.Accessor);

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
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    [Fact(DisplayName =
        "Reports: two acting members each record their OWN provenance principal on the report run — never one shared constant")]
    public async Task Run_ByTwoActingMembers_RecordsDistinctProvenancePrincipals()
    {
        var alicePrincipal = await RunAsAsync(AliceParty);
        var bobPrincipal = await RunAsAsync(BobParty);

        // The principal the pre-fix route recorded for EVERY member. Derived here independently, by the
        // same rule the route applies, so this is the real negative — the operator identity can never
        // appear as a readable substring of an opaque hashed principal.
        var operatorPrincipal = DeriveProvenancePrincipal(NodeCallerParty.OperatorParty.Value);
        var recorded = new[] { alicePrincipal, bobPrincipal };

        // Scan the WHOLE recorded set for the operator-derived principal FIRST, so a collapse to the
        // constant fails here rather than being masked by a narrower per-member comparison downstream.
        Assert.DoesNotContain(operatorPrincipal, recorded);

        // The positive teeth: each member's own derived principal is on their own run. Asserted as
        // equalities (not merely "not the constant"), so a route that recorded some other non-constant
        // value cannot pass by a vacuous negative.
        Assert.Equal(DeriveProvenancePrincipal(AliceParty), alicePrincipal);
        Assert.Equal(DeriveProvenancePrincipal(BobParty), bobPrincipal);
        Assert.Equal(2, recorded.Distinct().Count());
    }

    [Fact(DisplayName =
        "Reports: an UNBOUND request still records the ruled single-operator fallback principal — the fix must not attribute someone else")]
    public async Task Run_WithNoBoundPrincipal_RecordsTheOperatorFallback()
    {
        var recorded = await RunAsAsync(actingMember: null);

        Assert.Equal(DeriveProvenancePrincipal(NodeCallerParty.OperatorParty.Value), recorded);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs the trial balance as <paramref name="actingMember"/> and returns the RECORDED provenance principal.</summary>
    private async Task<PrincipalId> RunAsAsync(string? actingMember)
    {
        _cartridge.LastContext = null;

        using var request = new HttpRequestMessage(HttpMethod.Post, TrialBalanceRoute)
        {
            Content = JsonContent.Create(new
            {
                chartId = _chartId.Value,
                asOfDate = "2026-07-29",
            }),
        };
        if (actingMember is not null)
        {
            request.Headers.Add(ActingMemberHeader, actingMember);
        }

        using var response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.NotNull(_cartridge.LastContext);
        return _cartridge.LastContext!.RequestedBy;
    }

    /// <summary>The route's own derivation rule, restated independently so the expectation is not read from the code under test.</summary>
    private static PrincipalId DeriveProvenancePrincipal(string actorId) =>
        PrincipalId.FromBytes(SHA256.HashData(Encoding.UTF8.GetBytes(actorId)));

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

    private static async Task<ChartOfAccountsId> SeedInstallChartAsync(
        IDbContextFactory<LocalNodeDbContext> factory)
    {
        var chartId = ChartOfAccountsId.NewId();
        await using var ctx = await factory.CreateDbContextAsync();
        ctx.Set<ChartOfAccounts>().Add(new ChartOfAccounts(
            Id: chartId,
            LegalEntityId: new LegalEntityId("local-entity"),
            Name: "Provenance Test Chart",
            BaseCurrency: "USD",
            FiscalYearStartMonth: 1,
            FiscalYearStartDay: 1,
            RetainedEarningsAccountId: null,
            IsActive: true,
            CreatedAtUtc: new Instant(System.TimeProvider.System.GetUtcNow()),
            UpdatedAtUtc: new Instant(System.TimeProvider.System.GetUtcNow())));
        await ctx.SaveChangesAsync();
        return chartId;
    }

    /// <summary>Records the execution context the REAL runner hands it — the provenance principal's terminus.</summary>
    private sealed class RecordingTrialBalanceCartridge : IReportCartridge<TrialBalanceParameters, TrialBalanceResult>
    {
        public ReportKind Kind => ReportKind.TrialBalance;

        public ReportExecutionContext? LastContext;

        public Task<TrialBalanceResult> ExecuteAsync(
            ReportExecutionContext context, TrialBalanceParameters parameters, CancellationToken ct = default)
        {
            LastContext = context;
            return Task.FromResult(new TrialBalanceResult(
                ChartId: parameters.ChartId,
                AsOf: parameters.AsOfDate ?? new DateOnly(2026, 7, 29),
                PeriodId: null,
                Rows: [],
                TotalDebit: 0m,
                TotalCredit: 0m,
                IsBalanced: true,
                IsProvisional: false,
                Warnings: []));
        }
    }

    private sealed class FixedSnapshotMarkerSource : ISnapshotMarkerSource
    {
        public Task<string> CaptureAsync(TenantId tenantId, CancellationToken ct = default) =>
            Task.FromResult("marker:3381");
    }
}
