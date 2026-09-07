using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// T1 local-first sweep — node-local <b>accounting-periods</b> surface (list + open/close
/// writes), the node-resident counterpart of the Bridge <c>AccountingPeriodsEndpoints</c>
/// (Track E). Relocates period management off signal-bridge so the Accounting Periods page
/// works offline AND — the load-bearing fix — a fresh install can OPEN a period offline so a
/// new invoice can be ISSUED without a <c>NoPeriodForDate</c> block.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET  /api/local-node/accounting-periods</c> — list periods in the install chart.
///     Returns <c>{ chartId, periods: [...] }</c> (chartId null + empty list pre-seed).</item>
///   <item><c>POST /api/local-node/accounting-periods/open</c> — ensure an OPEN period covers a
///     date (body <c>{ date? }</c>; defaults to today). Creates the covering fiscal year +
///     monthly period if absent, or reopens a SoftClosed covering period. 200 with the open
///     period. The offline "open a period" entry point.</item>
///   <item><c>POST /api/local-node/accounting-periods/{id}/close</c> — close a period: Open →
///     SoftClosed by default, or → Locked with body <c>{ lock: true }</c>. 200 with the closed
///     period; 404 if absent; 400 on an illegal transition.</item>
/// </list>
/// </para>
/// <para>
/// <b>Existing tables, NO new schema/migration</b> (UPF F0) — uses the <c>fiscal_periods</c> +
/// <c>fiscal_years</c> tables already mapped by <c>FinancialLedgerEntityModule</c> into
/// <c>LocalNodeDbContext</c>, the same store <see cref="NodeEfPeriodResolver"/> reads for the
/// posting Phase-4 period gate.
/// </para>
/// <para>
/// <b>SC-4-C2 / SC4-T9(b) GREEN.</b> All writes go through <see cref="NodeAccountingPeriodService"/>,
/// which targets ONLY the recoverable <c>local-node.db</c> via the EF context factory — no kernel
/// CRDT / event-log write. The audit-envelope durable-layer pattern applies (no inline signed event).
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> inc-4 F1: caller-auth IS enforced — the LISTENER-LEVEL middleware (SharedHostedWebApp) gates every non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF stays N/A (explicit bearer, no cookie/ambient auth). Historical note (loopback posture): loopback-only node listener (same posture as every <c>/api/local-node/*</c>
/// route). The service is injected from the OUTER host container (bug-2849 — NOT <c>[FromServices]</c>).
/// </para>
/// </remarks>
public static class AccountingPeriodRoutes
{
    /// <summary>Canonical route base for the node-local accounting-periods surface.</summary>
    public const string RouteBase = "/api/local-node/accounting-periods";

    /// <summary>
    /// Maps the accounting-period routes onto <paramref name="app"/>, closing over the
    /// <paramref name="service"/> from the outer host container.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, NodeAccountingPeriodService service)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(service);

        // GET /api/local-node/accounting-periods
        app.MapGet(RouteBase, async (CancellationToken ct) =>
        {
            var (chartId, periods) = await service.ListAsync(ct).ConfigureAwait(false);
            return Results.Ok(new AccountingPeriodListResponse(
                ChartId: chartId?.Value,
                Periods: periods.Select(AccountingPeriodWire.From).ToList()));
        });

        // POST /api/local-node/accounting-periods/open
        app.MapPost($"{RouteBase}/open", async (OpenPeriodRequest? body, CancellationToken ct) =>
        {
            DateOnly date;
            if (!string.IsNullOrWhiteSpace(body?.Date))
            {
                if (!DateOnly.TryParse(body.Date, out date))
                {
                    return Results.BadRequest(new { error = "invalid_date" });
                }
            }
            else
            {
                date = DateOnly.FromDateTime(DateTime.UtcNow);
            }

            var result = await service.OpenForDateAsync(date, ct).ConfigureAwait(false);
            return result.Outcome switch
            {
                NodeAccountingPeriodService.Outcome.Ok =>
                    Results.Ok(AccountingPeriodWire.From(result.Period!)),
                NodeAccountingPeriodService.Outcome.NoChart =>
                    Results.BadRequest(new { error = "no_chart" }),
                NodeAccountingPeriodService.Outcome.InvalidTransition =>
                    Results.BadRequest(new { error = "period_locked" }),
                _ => Results.BadRequest(new { error = "open_failed" }),
            };
        });

        // POST /api/local-node/accounting-periods/{id}/close
        app.MapPost($"{RouteBase}/{{id}}/close", async (
            string id, ClosePeriodRequest? body, CancellationToken ct) =>
        {
            var result = await service.CloseAsync(id, @lock: body?.Lock ?? false, ct).ConfigureAwait(false);
            return result.Outcome switch
            {
                NodeAccountingPeriodService.Outcome.Ok =>
                    Results.Ok(AccountingPeriodWire.From(result.Period!)),
                NodeAccountingPeriodService.Outcome.PeriodNotFound =>
                    Results.NotFound(),
                NodeAccountingPeriodService.Outcome.NoChart =>
                    Results.NotFound(),
                NodeAccountingPeriodService.Outcome.InvalidTransition =>
                    Results.BadRequest(new { error = "invalid_transition" }),
                _ => Results.BadRequest(new { error = "close_failed" }),
            };
        });
    }
}

// ── Wire shapes (camelCase; mirror the Bridge /api/v1/accounting-periods contract) ───

/// <summary>List envelope: <c>{ "chartId": "...", "periods": [...] }</c> (chartId null pre-seed).</summary>
public sealed record AccountingPeriodListResponse(
    [property: JsonPropertyName("chartId")] string? ChartId,
    [property: JsonPropertyName("periods")] IReadOnlyList<AccountingPeriodWire> Periods);

/// <summary>One accounting period (mirrors the Bridge <c>AccountingPeriodDto</c>).</summary>
public sealed record AccountingPeriodWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("fiscalYearId")] string FiscalYearId,
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("startDate")] string StartDate,
    [property: JsonPropertyName("endDate")] string EndDate,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("softClosedAt")] string? SoftClosedAt,
    [property: JsonPropertyName("lockedAt")] string? LockedAt,
    [property: JsonPropertyName("version")] int Version)
{
    /// <summary>Projects a domain <see cref="FiscalPeriod"/> onto the wire shape.</summary>
    public static AccountingPeriodWire From(FiscalPeriod p) => new(
        Id:           p.Id.Value,
        FiscalYearId: p.FiscalYearId.Value,
        ChartId:      p.ChartId.Value,
        Kind:         p.Kind.ToString(),
        Label:        p.Label,
        StartDate:    p.StartDate.ToString("O"),
        EndDate:      p.EndDate.ToString("O"),
        Status:       p.Status.ToString(),
        SoftClosedAt: p.SoftClosedAtUtc?.Value.ToString("O"),
        LockedAt:     p.LockedAtUtc?.Value.ToString("O"),
        Version:      p.Version);
}

/// <summary>POST body for <c>POST /api/local-node/accounting-periods/open</c> (date optional).</summary>
public sealed record OpenPeriodRequest(
    [property: JsonPropertyName("date")] string? Date = null);

/// <summary>POST body for <c>POST /api/local-node/accounting-periods/{id}/close</c>.</summary>
public sealed record ClosePeriodRequest(
    [property: JsonPropertyName("lock")] bool? Lock = null);
