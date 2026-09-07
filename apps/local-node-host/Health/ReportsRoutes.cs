using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.Reports;
using Harborline.Api.Blocks.Reports.Cartridges.TrialBalance;
using Harborline.Api.Blocks.Reports.Cartridges.ArAgingSummary;
using Harborline.Api.Blocks.Reports.Cartridges.ApAgingSummary;
using Harborline.Api.Blocks.Reports.Cartridges.BalanceSheet;
using Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLoss;
using Harborline.Api.Blocks.Reports.Cartridges.ProfitAndLossByProperty;
using Harborline.Api.Blocks.Reports.Exceptions;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// T5 local-first sweep — node-local <b>reports</b> surface (the read-side report
/// cartridge family), the node-resident counterpart of the Bridge
/// <c>ReportsCartridgeEndpoints</c> (<c>/api/v1/reports/{kind}</c> + <c>/api/v1/charts</c>).
/// Relocates the report run + chart-list reads off signal-bridge so all six report pages —
/// Trial Balance, Balance Sheet, P&amp;L, P&amp;L by Property, AR Aging, AP Aging — compute
/// and render fully offline from the now-node-resident GL (signal-bridge STOPPED).
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes (the wire mirrors the Bridge <c>/api/v1/reports</c> + <c>/api/v1/charts</c>
/// contract the frontend <c>reports.ts</c> consumes — same shapes, node transport):</b>
/// <list type="bullet">
///   <item><c>GET  /api/local-node/charts</c> — the install chart(s) as
///     <c>{ charts: [{ chartId, name, baseCurrency }] }</c> (single-element on the
///     single-device node; empty pre-seed — never an error).</item>
///   <item><c>POST /api/local-node/reports/trial-balance</c></item>
///   <item><c>POST /api/local-node/reports/ar-aging-summary</c></item>
///   <item><c>POST /api/local-node/reports/ap-aging-summary</c></item>
///   <item><c>POST /api/local-node/reports/balance-sheet</c></item>
///   <item><c>POST /api/local-node/reports/profit-and-loss</c></item>
///   <item><c>POST /api/local-node/reports/profit-and-loss-by-property</c></item>
/// </list>
/// Each report kind is run via POST with its typed parameters as the request body
/// (pattern-013-cartridge-read-via-post). The handler returns the
/// <see cref="ReportRunResult{TResult}"/> envelope as JSON — exactly what the frontend
/// <c>runReport</c> reads. CSV is formatted CLIENT-side from this JSON result (T5 CSV
/// decision (a)); the node serves JSON only.
/// </para>
/// <para>
/// <b>NOTE — all 6 cartridges the pages use are registered here</b>, NOT the Bridge's 4.
/// The Bridge registers only TrialBalance / ArAgingSummary / ProfitAndLossByProperty /
/// RentRoll but maps 7 endpoints (a latent Bridge gap — BalanceSheet / ProfitAndLoss /
/// ApAgingSummary endpoints have no registered cartridge); the node does not replicate that
/// gap. RentRoll is NOT a page consumer in T5, so it is not registered node-side.
/// </para>
/// <para>
/// <b>Read-only.</b> Every route is a pure projection over <c>local-node.db</c> via the
/// reports cartridge substrate (<see cref="IReportRunner"/> over the node
/// <c>IGeneralLedgerReadModel</c> / <c>IJournalStore</c> / <c>IAccountResolver</c> /
/// <c>IArAgingService</c> / <c>IApAgingService</c> / <c>IChartRepository</c>) — no writes,
/// no kernel CRDT / event-log access. The runner + cartridges + the node read seams
/// reference no <c>PostingEngine</c>/<c>FileBackedEventLog</c>/<c>IEventLog</c>, so
/// SC4-T9(b) is unaffected (no recoverability sink).
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> inc-4 F1: caller-auth IS enforced — the LISTENER-LEVEL middleware (SharedHostedWebApp) gates every non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF stays N/A (explicit bearer, no cookie/ambient auth). Historical note (loopback posture): loopback-only node listener (same posture as every other
/// <c>/api/local-node/*</c> route). The Bridge equivalents carry AuthenticatedTenantPolicy
/// because they are network-reachable; the node surface is not. The runner +
/// <see cref="IChartRepository"/> are injected from the OUTER host container (bug-2849 —
/// NOT <c>[FromServices]</c> on the inner shared-app container).
/// </para>
/// <para>
/// <b>Tenant + chart.</b> Reports filter on the active-team-derived tenant resolved via
/// <c>NodeTenant.Resolve(activeTeam)</c> (<c>ActiveTeamTenantContext</c>; ADR 0032 identity
/// layer), not a fixed <c>"local"</c> sentinel, against the install chart. The body's
/// <c>ChartId</c> (echoed from <c>GET /api/local-node/charts</c>) MUST resolve to the install
/// chart; a mismatch collapses to a uniform 404. The WHERE clause is the per-org isolation
/// predicate — switching the active org switches which org's books a report reads. The
/// <see cref="ReportExecutionContext.RequestedBy"/> principal is a deterministic provenance
/// principal derived from the ACTING MEMBER (MTW-2 #3381) — resolved through
/// <see cref="NodeCallerParty.Resolve"/> from the selected-session principal on
/// <c>HttpContext.Features</c>, so two signed-in members no longer share one recorded
/// provenance principal. It falls back to the single-operator identity only when no principal
/// is bound (the bootstrap/desktop path). The derivation is unchanged; only its input is. The
/// security boundary remains the node's loopback listener, not the principal — this is
/// ATTRIBUTION, not authorization.
/// </para>
/// </remarks>
public static class ReportsRoutes
{
    /// <summary>Canonical route base for the node-local report run surface.</summary>
    public const string ReportsRouteBase = "/api/local-node/reports";

    /// <summary>Canonical route for the node-local chart-list surface.</summary>
    public const string ChartsRoute = "/api/local-node/charts";


    /// <summary>
    /// Maps the report-run + chart-list routes onto <paramref name="app"/>, closing over the
    /// <paramref name="runner"/>, <paramref name="charts"/>, and the context
    /// <paramref name="factory"/> from the outer host container.
    /// </summary>
    internal static void Map(
        IEndpointRouteBuilder app,
        IReportRunner runner,
        IDbContextFactory<LocalNodeDbContext> factory, IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(factory);

        // ── GET /api/local-node/charts ──────────────────────────────────────────
        app.MapGet(ChartsRoute, async (CancellationToken ct) =>
        {
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var chart = await ResolveInstallChartAsync(ctx, ct).ConfigureAwait(false);
            if (chart is null)
            {
                return Results.Ok(new ChartListResponse([]));
            }
            return Results.Ok(new ChartListResponse(
                [new ChartSummary(chart.Id.Value, chart.Name, chart.BaseCurrency)]));
        });

        // ── POST /api/local-node/reports/{kind} ─────────────────────────────────
        var reports = app.MapGroup(ReportsRouteBase);

        reports.MapPost("/trial-balance", (TrialBalanceParameters p, HttpContext http, CancellationToken ct) =>
            RunAsync<TrialBalanceParameters, TrialBalanceResult>(
                ReportKind.TrialBalance, p, p.ChartId, runner, factory, NodeTenant.Resolve(activeTeam), NodeCallerParty.Resolve(http).Value, ct));

        reports.MapPost("/ar-aging-summary", (ArAgingSummaryParameters p, HttpContext http, CancellationToken ct) =>
            RunAsync<ArAgingSummaryParameters, ArAgingSummaryResult>(
                ReportKind.ArAgingSummary, p, p.ChartId, runner, factory, NodeTenant.Resolve(activeTeam), NodeCallerParty.Resolve(http).Value, ct));

        reports.MapPost("/ap-aging-summary", (ApAgingSummaryParameters p, HttpContext http, CancellationToken ct) =>
            RunAsync<ApAgingSummaryParameters, ApAgingSummaryResult>(
                ReportKind.ApAgingSummary, p, p.ChartId, runner, factory, NodeTenant.Resolve(activeTeam), NodeCallerParty.Resolve(http).Value, ct));

        reports.MapPost("/balance-sheet", (BalanceSheetParameters p, HttpContext http, CancellationToken ct) =>
            RunAsync<BalanceSheetParameters, BalanceSheetResult>(
                ReportKind.BalanceSheet, p, p.ChartId, runner, factory, NodeTenant.Resolve(activeTeam), NodeCallerParty.Resolve(http).Value, ct));

        reports.MapPost("/profit-and-loss", (ProfitAndLossParameters p, HttpContext http, CancellationToken ct) =>
            RunAsync<ProfitAndLossParameters, ProfitAndLossResult>(
                ReportKind.ProfitAndLoss, p, p.ChartId, runner, factory, NodeTenant.Resolve(activeTeam), NodeCallerParty.Resolve(http).Value, ct));

        reports.MapPost("/profit-and-loss-by-property", (ProfitAndLossByPropertyParameters p, HttpContext http, CancellationToken ct) =>
            RunAsync<ProfitAndLossByPropertyParameters, ProfitAndLossByPropertyResult>(
                ReportKind.ProfitAndLossByProperty, p, p.ChartId, runner, factory, NodeTenant.Resolve(activeTeam), NodeCallerParty.Resolve(http).Value, ct));
    }

    // ── Shared run pipeline ─────────────────────────────────────────────────────
    private static async Task<IResult> RunAsync<TParams, TResult>(
        ReportKind kind,
        TParams parameters,
        ChartOfAccountsId requestedChartId,
        IReportRunner runner,
        IDbContextFactory<LocalNodeDbContext> factory,
        TenantId LocalTenantId,
        string actingActorId,
        CancellationToken ct)
        where TParams : class
        where TResult : class
    {
        // Single-device chart guard: the body's ChartId (echoed from GET /api/local-node/charts)
        // MUST resolve to the install chart. On a single-device node there is no foreign tenant to
        // probe, so a mismatch is simply an unknown/stale chart id → uniform 404.
        await using (var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var chart = await ResolveInstallChartAsync(ctx, ct).ConfigureAwait(false);
            if (chart is null || chart.Id != requestedChartId)
            {
                return Results.NotFound();
            }
        }

        // The provenance principal is derived from the ACTING MEMBER (MTW-2 #3381), not a per-install
        // constant. The derivation itself is unchanged — same deterministic hash, different input — so a
        // request with no bound principal still derives the ruled single-operator fallback identity.
        var principal = DeriveProvenancePrincipal(actingActorId);

        try
        {
            var result = await runner.RunAsync<TParams, TResult>(kind, parameters, LocalTenantId, principal, ct)
                .ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (ReportParameterValidationException ex)
        {
            // Mirror the Bridge: RFC 7807 problem+json so the frontend's throwFromResponse
            // surfaces a typed/validation error rather than a generic failure.
            return Results.Problem(
                title: "Report parameters are invalid.",
                detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
        catch (UnknownReportKindException)
        {
            // Unreachable in practice (kind is a compile-time literal), but the substrate
            // contract permits it — map to 500 rather than leak.
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
        catch (ReportCartridgeExecutionException)
        {
            return Results.StatusCode(StatusCodes.Status500InternalServerError);
        }
    }

    // ── Single-device chart resolution ──────────────────────────────────────────

    /// <summary>
    /// Resolves the install chart on the single-device node: the single most-recently-created
    /// <see cref="ChartOfAccounts"/> row. Returns <see langword="null"/> when no chart has been
    /// seeded yet (a legitimate pre-onboarding state). Mirrors
    /// <c>ChartOfAccountsManagementRoutes.ResolveChartIdAsync</c> /
    /// <c>NodeAccountingSummaryService.ResolveChartIdAsync</c> — materialise then order in memory
    /// (SQLite cannot ORDER BY a DateTimeOffset-converted Instant server-side; the single-device
    /// node has at most a handful of charts).
    /// </summary>
    private static async Task<ChartOfAccounts?> ResolveInstallChartAsync(
        LocalNodeDbContext ctx,
        CancellationToken ct)
    {
        var charts = await ctx.Set<ChartOfAccounts>()
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return charts
            .OrderByDescending(c => c.CreatedAtUtc.Value)
            .FirstOrDefault();
    }

    // ── Provenance principal ─────────────────────────────────────────────────────

    /// <summary>
    /// Deterministic provenance principal from the single-operator id. Provenance only — it flows
    /// into <see cref="ReportExecutionContext.RequestedBy"/> ("who ran it"). The security boundary
    /// is the loopback listener, not the principal. Mirrors the Bridge's
    /// <c>ReportsCartridgeEndpoints.DerivePrincipalId</c> (SHA-256 of the user id).
    /// </summary>
    private static PrincipalId DeriveProvenancePrincipal(string userId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(userId ?? string.Empty));
        return PrincipalId.FromBytes(bytes);
    }
}

// ── Wire shapes (camelCase; mirror the Bridge /api/v1/charts contract reports.ts consumes) ──

/// <summary>Response for <c>GET /api/local-node/charts</c>. Single-element on the single-device node.</summary>
public sealed record ChartListResponse(
    [property: JsonPropertyName("charts")] IReadOnlyList<ChartSummary> Charts);

/// <summary>One selectable chart of accounts (mirrors the Bridge <c>ChartSummary</c>).</summary>
public sealed record ChartSummary(
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("baseCurrency")] string BaseCurrency);
