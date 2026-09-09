using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local <b>READ</b> surface over the kernel-ledger <c>payments</c> table —
/// the PM-doctype offline rebind for payments (Admiral ruling 2026-06-14: the
/// frontend reads payments FROM the kernel-ledger, the authoritative C1-durable
/// financial source of truth, NOT a flat duplicate table).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads the authoritative ledger, not a duplicate.</b> The embedded node already
/// owns the financial <see cref="LocalNodeDbContext"/> with the
/// <see cref="Harborline.Api.Blocks.FinancialPayments.Models.Payment"/> table contributed by
/// <c>PaymentsEntityModule</c> (the same module the Bridge's
/// <c>SignalBridgeDbContext</c> composes — "same EF model, shared not forked").
/// This route is a thin <em>projection</em> over <c>LocalNodeDbContext.Set&lt;Payment&gt;()</c>;
/// it creates NO new table. A prior attempt that introduced a flat <c>payments</c>
/// table collided with the kernel-ledger table — never duplicate the financial source
/// of truth.
/// </para>
/// <para>
/// <b>Read-plane only — the posting path is UNTOUCHED.</b> Payments are recorded /
/// cleared / bounced through the financial cluster's posting services
/// (<c>IPaymentPostingService</c> / <c>recordPayment</c>); those write the GL journal
/// entries and own the C1 mutation/posting path. This surface NEVER writes — it only
/// serves the rows the posting path has already durably committed. There is no
/// <c>MapPost</c>/<c>MapPatch</c> here by design.
/// </para>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET /api/local-node/payments</c> — list payments for the local tenant.
///     Optional <c>?chartId=</c> and <c>?partyId=</c> query filters mirror the
///     ledger's native chart-scoped and party-scoped access patterns
///     (<c>ix_payments_tenant_chart_date</c> / <c>ix_payments_tenant_chart_party</c>).
///     Newest-first by <c>PaymentDate</c>.</item>
///   <item><c>GET /api/local-node/payments/{id}</c> — fetch one payment by
///     <see cref="PaymentId"/>; 404 if absent or belonging to another tenant.</item>
/// </list>
/// </para>
/// <para>
/// <b>Tenant / entity scoping (financial-cluster discipline; ADR 0091 / 0092 / 0104).</b>
/// Every read filters on the active-team-derived tenant resolved via
/// <c>NodeTenant.Resolve(activeTeam)</c> (<c>ActiveTeamTenantContext</c>; ADR 0032 identity
/// layer), not a fixed <c>"local"</c> sentinel (the same posture as <see cref="EntityRoutes"/>
/// / <see cref="ChartOfAccountsRoutes"/>) — the WHERE clause is the per-org isolation
/// predicate, so switching the active org switches which org's payments are visible.
/// Entity-scoping within the org is keyed by
/// <see cref="Payment.ChartId"/> (a chart belongs to exactly one
/// <see cref="LegalEntity"/>): callers that want a single entity's payments pass
/// <c>?chartId=</c>. The defence-in-depth tenant WHERE clause is applied explicitly on
/// every query (ADR 0092) rather than relying solely on an ambient filter.
/// </para>
/// <para>
/// <b>Financial-cluster audit-envelope durable-layer pattern.</b> This is a pure read;
/// it emits no events. The ledger rows it serves satisfy the X-AUDIT obligation
/// (ADR 0104 §7) by their presence in the keyed durable store — the same deferred
/// posture the financial masters carry (no inline signed-event on the read plane).
/// </para>
/// <para>
/// <b>Wiring.</b> The factory is injected from the OUTER host container and passed to
/// <see cref="Map"/> as a closed-over dependency — NOT resolved via <c>[FromServices]</c>,
/// which would fail because the routes are mapped onto <see cref="SharedHostedWebApp"/>'s
/// inner <c>WebApplication</c> whose service provider is a SEPARATE container (bug-2849).
/// </para>
/// </remarks>
public static class PaymentRoutes
{
    /// <summary>Canonical route base for the node-local payments read surface.</summary>
    public const string RouteBase = "/api/local-node/payments";

    /// <summary>
    /// Maps the payment READ routes onto <paramref name="app"/>, closing over the
    /// <see cref="LocalNodeDbContext"/> <paramref name="factory"/> from the outer
    /// host container.
    /// </summary>
    internal static void Map(
        IEndpointRouteBuilder app,
        IDbContextFactory<LocalNodeDbContext> factory, IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(factory);

        // GET /api/local-node/payments[?chartId=&partyId=] — list for the local tenant.
        app.MapGet(RouteBase, async (
            string? chartId,
            string? partyId,
            CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            // Defence-in-depth tenant WHERE clause (ADR 0092) — never relies solely
            // on an ambient query filter. The local node has exactly one tenant.
            var query = ctx.Set<Payment>()
                .AsNoTracking()
                .Where(p => p.TenantId == LocalTenantId);

            // Entity-scope: a ChartOfAccounts belongs to exactly one LegalEntity, so a
            // chart filter is the entity scope on the payments read.
            if (!string.IsNullOrWhiteSpace(chartId))
            {
                var cid = new ChartOfAccountsId(chartId);
                query = query.Where(p => p.ChartId == cid);
            }

            if (!string.IsNullOrWhiteSpace(partyId))
            {
                var pid = new PartyId(partyId);
                query = query.Where(p => p.PartyId == pid);
            }

            // Materialize then sort in memory: SQLite has no native DateOnly ordering
            // translation parity to rely on across providers, and the single-tenant
            // node-local set is small (same posture as MaintenanceRoutes). Newest first.
            var rows = await query.ToListAsync(ct).ConfigureAwait(false);

            return Results.Ok(new PaymentListResponse(
                rows.OrderByDescending(p => p.PaymentDate)
                    .ThenByDescending(p => p.CreatedAtUtc.Value)
                    .Select(PaymentDto.From)
                    .ToList()));
        });

        // GET /api/local-node/payments/{id} — fetch one; 404 if absent / other-tenant.
        app.MapGet($"{RouteBase}/{{id}}", async (
            string id,
            CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            await using var ctx = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var pid = new PaymentId(id);
            var row = await ctx.Set<Payment>()
                .AsNoTracking()
                .Where(p => p.TenantId == LocalTenantId && p.Id == pid)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return row is null
                ? Results.NotFound()
                : Results.Ok(new PaymentItemResponse(PaymentDto.From(row)));
        });
    }
}

// ── Wire shapes ───────────────────────────────────────────────────────────────

/// <summary>
/// Flat read DTO projected from the kernel-ledger <see cref="Payment"/> record.
/// Surfaces the lifecycle <c>status</c> verbatim from <see cref="PaymentStatus"/>
/// (Draft / Unapplied / PartiallyApplied / Applied / Bounced / Voided) so the read
/// is consistent with the authoritative ledger record — it does not collapse or
/// invent states. The <c>applications_json</c> snapshot is intentionally NOT
/// surfaced (it is a non-authoritative load-time convenience column).
/// </summary>
public sealed record PaymentDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("partyId")] string PartyId,
    [property: JsonPropertyName("paymentNumber")] string PaymentNumber,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("paymentDate")] string PaymentDate,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("unappliedAmount")] decimal UnappliedAmount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reference")] string? Reference)
{
    /// <summary>Projects a kernel-ledger <see cref="Payment"/> onto the read DTO.</summary>
    public static PaymentDto From(Payment p) => new(
        Id:              p.Id.Value,
        ChartId:         p.ChartId.Value,
        PartyId:         p.PartyId.Value,
        PaymentNumber:   p.PaymentNumber,
        Direction:       p.Direction.ToString(),
        // ISO 8601 calendar date (yyyy-MM-dd) — DateOnly round-trip format.
        PaymentDate:     p.PaymentDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        Amount:          p.Amount,
        UnappliedAmount: p.UnappliedAmount,
        Currency:        p.Currency,
        Method:          p.Method.ToString(),
        Status:          p.Status.ToString(),
        Reference:       p.Reference);
}

/// <summary>List response envelope: <c>{ "data": [...] }</c> (mirrors the proxy shape).</summary>
public sealed record PaymentListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<PaymentDto> Data);

/// <summary>Single-item response envelope: <c>{ "data": {...} }</c>.</summary>
public sealed record PaymentItemResponse(
    [property: JsonPropertyName("data")] PaymentDto Data);
