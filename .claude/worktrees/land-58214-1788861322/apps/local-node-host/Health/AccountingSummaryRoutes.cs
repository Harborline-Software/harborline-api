using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// T1 local-first sweep — node-local <b>accounting-summary</b> surface (dashboard GL
/// aggregation + outstanding invoices). Replaces the Bridge ERPNext proxy
/// (<c>/api/v1/erpnext/accounting/{summary,outstanding}</c>) so the Accounting dashboard
/// renders fully offline from the now-node-resident GL + AR data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET /api/local-node/accounting/summary</c> — current-calendar-month
///     <c>{ period, income, expenses, net }</c> computed from posted journal entries.</item>
///   <item><c>GET /api/local-node/accounting/outstanding</c> — open invoices with a positive
///     balance, <c>{ data: [{ name, customer, outstandingAmount, dueDate, status }] }</c>.</item>
/// </list>
/// The wire mirrors the Bridge ERPNext proxy shapes the frontend <c>erpnext.ts</c> consumes —
/// same fields, node transport — except the outstanding amount is camelCase (<c>outstandingAmount</c>)
/// and the frontend maps it back; the snake_case ERPNext field name is dropped on the node.
/// </para>
/// <para>
/// <b>Read-only.</b> Both routes are pure projections over <c>local-node.db</c> via
/// <see cref="NodeAccountingSummaryService"/> — no writes, no kernel CRDT / event-log access. The
/// service + routes reference no <c>PostingEngine</c>/<c>FileBackedEventLog</c>/<c>IEventLog</c>, so
/// SC4-T9(b) is unaffected.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> inc-4 F1: caller-auth IS enforced — the LISTENER-LEVEL middleware (SharedHostedWebApp) gates every non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF stays N/A (explicit bearer, no cookie/ambient auth). Historical note (loopback posture): loopback-only node listener. The service is injected from the OUTER host
/// container (bug-2849 — NOT <c>[FromServices]</c>).
/// </para>
/// </remarks>
public static class AccountingSummaryRoutes
{
    /// <summary>Canonical route base for the node-local accounting dashboard surface.</summary>
    public const string RouteBase = "/api/local-node/accounting";

    /// <summary>
    /// Maps the accounting-summary routes onto <paramref name="app"/>, closing over the
    /// <paramref name="service"/> from the outer host container.
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, NodeAccountingSummaryService service)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(service);

        // GET /api/local-node/accounting/summary
        app.MapGet($"{RouteBase}/summary", async (CancellationToken ct) =>
        {
            var s = await service.GetSummaryAsync(ct: ct).ConfigureAwait(false);
            return Results.Ok(new AccountingSummaryWire(s.Period, s.Income, s.Expenses, s.Net));
        });

        // GET /api/local-node/accounting/outstanding
        app.MapGet($"{RouteBase}/outstanding", async (CancellationToken ct) =>
        {
            var open = await service.GetOutstandingAsync(ct).ConfigureAwait(false);
            return Results.Ok(new OutstandingListResponse(
                open.Select(o => new OutstandingInvoiceWire(
                    o.Name, o.Customer, o.OutstandingAmount, o.DueDate, o.Status)).ToList()));
        });
    }
}

// ── Wire shapes (camelCase; mirror the Bridge ERPNext-proxy contract erpnext.ts consumes) ──

/// <summary>Dashboard P&amp;L summary: <c>{ period, income, expenses, net }</c>.</summary>
public sealed record AccountingSummaryWire(
    [property: JsonPropertyName("period")] string Period,
    [property: JsonPropertyName("income")] decimal Income,
    [property: JsonPropertyName("expenses")] decimal Expenses,
    [property: JsonPropertyName("net")] decimal Net);

/// <summary>Outstanding-invoice list envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record OutstandingListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<OutstandingInvoiceWire> Data);

/// <summary>One open invoice in the dashboard outstanding list.</summary>
public sealed record OutstandingInvoiceWire(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("customer")] string Customer,
    [property: JsonPropertyName("outstandingAmount")] decimal OutstandingAmount,
    [property: JsonPropertyName("dueDate")] string DueDate,
    [property: JsonPropertyName("status")] string Status);
