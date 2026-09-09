using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Services;
using Harborline.Api.Blocks.Leases.Models;
using Harborline.Api.Blocks.Leases.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using PeopleModels = Harborline.Api.Blocks.People.Foundation.Models;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local lease sub-ledger surface — the ADR 0122 §D4 P2 → (b) offline lease payment-history read
/// path (ADR 0113 ABSOLUTE local-first; ADR 0120 the sub-ledger primitive this surfaces). Resolves the
/// chain <c>lease → LeaseSubLedgerLink.GetByLeaseAsync → ISubLedgerReadModel.GetHistoryAsync</c> entirely
/// over the recoverable <c>local-node.db</c>, so a fully-offline install (signal-bridge STOPPED) renders
/// REAL lease payment-history.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now serves lease payment-history.</b> Before P2 the LeaseDetailPage filtered ALL Bridge
/// payments by <c>p.lease === lease.name</c> — a Bridge-dependent read that renders nothing offline.
/// This surface resolves the per-lease sub-ledger history from node-resident invoices + payments via the
/// ADR 0120 read-model, FK-authoritatively (no PartyId-overmatch). ADDITIVE — the Bridge payments path
/// keeps working; the frontend flip + the Rust <c>sc5</c> fail-close are the sequenced PR-B follow-up
/// after the security SPOT-CHECK.
/// </para>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET /api/local-node/leases/{name}/payment-history</c> — the lease's chronological
///     sub-ledger history (charges from issued invoices + payment applications), with running balance.
///     Resolves <c>LeaseId(name)</c> → <see cref="LeaseSubLedgerLink"/> (one lookup) →
///     <see cref="ISubLedgerReadModel.GetHistoryAsync"/> (one history call). N+1-free. Returns an empty
///     <c>{ data: [] }</c> (200, NOT 404) when the lease has no sub-ledger link yet — an un-activated
///     lease has no history, which is a valid empty result, not an error.</item>
///   <item><c>POST /api/local-node/leases/{name}/activate-subledger</c> — mint (or resolve) the lease's
///     Receivable <see cref="SubLedgerAccount"/> + record the <see cref="LeaseSubLedgerLink"/> via
///     <see cref="LeaseSubLedgerService.ActivateAsync"/>. Idempotent (ExternalRef-keyed mint-or-get).
///     Returns the <c>subLedgerAccountId</c> the caller then stamps on the lease's invoices
///     (<c>POST /api/local-node/invoices</c> <c>subLedgerAccountId</c>). The mapping store is in-memory
///     v1 (ADR 0120 posture); the mapping holds NO irreplaceable financial value (it is reconstructable
///     from the recoverable invoices' <c>SubLedgerAccountId</c> FK + <c>ExternalRef</c>), so SC-4
///     recoverability is preserved — the financial value lives in the recoverable invoices/payments/JEs,
///     not here.</item>
/// </list>
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Every read + write resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c> (<c>ActiveTeamTenantContext</c>; ADR 0032 identity layer), not a
/// fixed <c>"local"</c> sentinel (the same posture as <see cref="JournalEntryRoutes"/> /
/// <see cref="InvoiceRoutes"/>). The read-model + link repo receive that active-team tenant, server-set,
/// never frontend-passed — the per-org isolation boundary.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1) + CSRF.</b> inc-4 F1: caller-auth IS enforced — the LISTENER-LEVEL middleware (SharedHostedWebApp) gates every non-allowlisted node route behind the Harborline App's per-boot session token (fail-closed 401) by default; CSRF stays N/A (explicit bearer, no cookie/ambient auth). Historical note (loopback posture): loopback-only Kestrel listener (same posture as every other
/// <c>/api/local-node/*</c> route).
/// </para>
/// <para>
/// <b>SC4-C2 recoverability.</b> The read path touches NO kernel CRDT-writer type
/// (<c>PostingEngine</c>/<c>ILedgerEventStream</c>/<c>FileBackedEventLog</c>/<c>IEventLog</c>) and is
/// read-only over the recoverable <c>local-node.db</c> + the in-memory v1 mapping. It cannot enlarge the
/// orphan surface (<c>Sc4RecoverabilityGuardTests</c> Layer-1 IL scan + Layer-2 DI-graph stay green).
/// </para>
/// <para>
/// <b>Wiring.</b> The link repo + read-model + activation service are injected from the OUTER host
/// container and passed to <see cref="Map"/> as closed-over dependencies — NOT resolved via
/// <c>[FromServices]</c>, which would fail because the routes are mapped onto
/// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c> whose service provider is a SEPARATE
/// container (bug-2849).
/// </para>
/// </remarks>
public static class LeaseSubLedgerRoutes
{
    /// <summary>Canonical route base for the node-local lease surface (mirrors <see cref="LeaseRoutes.RouteBase"/>).</summary>
    public const string RouteBase = "/api/local-node/leases";

    /// <summary>
    /// Maps the lease sub-ledger routes onto <paramref name="app"/>, closing over the
    /// <paramref name="links"/> repository, the <paramref name="readModel"/>, the
    /// <paramref name="activation"/> service, and the <paramref name="activeTeam"/> accessor from the
    /// outer host container. The data tenant is resolved per request from the active team (ADR 0032).
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        ILeaseSubLedgerLinkRepository links,
        ISubLedgerReadModel readModel,
        LeaseSubLedgerService activation,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(readModel);
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(activeTeam);

        MapPaymentHistory(app, links, readModel, activeTeam, timeProvider);
        MapActivate(app, activation, activeTeam, timeProvider);
    }

    // ── GET /api/local-node/leases/{name}/payment-history — the offline read chain ──
    private static void MapPaymentHistory(
        IEndpointRouteBuilder app,
        ILeaseSubLedgerLinkRepository links,
        ISubLedgerReadModel readModel,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider)
    {
        app.MapGet($"{RouteBase}/{{name}}/payment-history", async (string name, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var asOf = timeProvider.GetUtcNow();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(new { error = "lease_name_required" });
            }

            // lease name → LeaseId (the flat node lease's `name`, e.g. LEASE-0001, IS the LeaseId).
            var leaseId = new LeaseId(name);

            // One link lookup. A lease with no sub-ledger link yet (never activated) has no history —
            // that is a valid EMPTY result (200 { data: [] }), not a 404. Returning 404 would conflate
            // "no history" with "no such lease".
            var link = await links.GetByLeaseAsync(LocalTenantId, leaseId, ct).ConfigureAwait(false);
            if (link is null)
            {
                return Results.Ok(LeasePaymentHistoryResponse.Empty);
            }

            // One history call over the node-resident read-model (charges from node invoices + payments
            // from node payment applications, FK-authoritative on link.SubLedgerAccountId). N+1-free.
            var entries = await readModel
                .GetHistoryAsync(LocalTenantId, link.SubLedgerAccountId, asOf, ct)
                .ConfigureAwait(false);

            return Results.Ok(new LeasePaymentHistoryResponse(
                SubLedgerAccountId: link.SubLedgerAccountId.Value,
                Data: entries.Select(SubLedgerEntryWire.From).ToList()));
        });
    }

    // ── POST /api/local-node/leases/{name}/activate-subledger — mint account + link (idempotent) ──
    private static void MapActivate(IEndpointRouteBuilder app, LeaseSubLedgerService activation, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{name}}/activate-subledger", async (
            string name,
            ActivateLeaseSubLedgerRequest body,
            CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(new { error = "lease_name_required" });
            }
            if (body is null
                || string.IsNullOrWhiteSpace(body.ChartId)
                || string.IsNullOrWhiteSpace(body.ArControlAccountId)
                || string.IsNullOrWhiteSpace(body.CustomerPartyId))
            {
                return Results.BadRequest(new
                {
                    error = "chart_id, ar_control_account_id and customer_party_id are required.",
                });
            }

            SubLedgerAccountId accountId;
            try
            {
                accountId = await activation.ActivateAsync(
                        tenantId:             LocalTenantId,
                        chartId:              new ChartOfAccountsId(body.ChartId!),
                        leaseId:              new LeaseId(name),
                        arControlAccountId:   new GLAccountId(body.ArControlAccountId!),
                        primaryTenantPartyId: new PeopleModels.PartyId(body.CustomerPartyId!),
                        now:                  new Instant(timeProvider.GetUtcNow()),
                        cancellationToken:    ct)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                // CreateWithKindGuard rejects a mis-bound (non-Asset) AR control account.
                return Results.BadRequest(new { error = "invalid_ar_control_account", detail = ex.Message });
            }

            return Results.Ok(new ActivateLeaseSubLedgerResponse(accountId.Value));
        });
    }
}

// ── Wire shapes ───────────────────────────────────────────────────────────────

/// <summary>
/// One row in the lease payment-history response — a projection of an ADR 0120
/// <see cref="SubLedgerEntry"/> (charge / payment / void / write-off + running balance).
/// </summary>
public sealed record SubLedgerEntryWire(
    [property: JsonPropertyName("entryDate")] string EntryDate,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("sourceId")] string SourceId,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("runningBalance")] decimal RunningBalance)
{
    /// <summary>Projects a domain <see cref="SubLedgerEntry"/> onto the wire shape.</summary>
    public static SubLedgerEntryWire From(SubLedgerEntry e) => new(
        // ISO 8601 calendar date (yyyy-MM-dd) — DateOnly round-trip.
        EntryDate:      e.EntryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        Kind:           e.Kind.ToString(),
        SourceId:       e.SourceId,
        Reference:      e.Reference,
        Amount:         e.Amount,
        RunningBalance: e.RunningBalance);
}

/// <summary>
/// Lease payment-history response: <c>{ "subLedgerAccountId": ..., "data": [...] }</c>.
/// <c>subLedgerAccountId</c> is null when the lease has no sub-ledger link yet (empty history).
/// </summary>
public sealed record LeasePaymentHistoryResponse(
    [property: JsonPropertyName("subLedgerAccountId")] string? SubLedgerAccountId,
    [property: JsonPropertyName("data")] IReadOnlyList<SubLedgerEntryWire> Data)
{
    /// <summary>The empty result for an un-activated lease (no link → no history).</summary>
    public static readonly LeasePaymentHistoryResponse Empty =
        new(SubLedgerAccountId: null, Data: Array.Empty<SubLedgerEntryWire>());
}

/// <summary>POST body for <c>POST /api/local-node/leases/{name}/activate-subledger</c>.</summary>
public sealed record ActivateLeaseSubLedgerRequest(
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("arControlAccountId")] string ArControlAccountId,
    [property: JsonPropertyName("customerPartyId")] string CustomerPartyId);

/// <summary>Response from the activate-subledger route: the minted/resolved sub-ledger account id.</summary>
public sealed record ActivateLeaseSubLedgerResponse(
    [property: JsonPropertyName("subLedgerAccountId")] string SubLedgerAccountId);
