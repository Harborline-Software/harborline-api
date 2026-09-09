using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local AP bill surface — the Cohort D Step 2c AP node-flip
/// (ADR 0113 ABSOLUTE local-first). Bills are the primary AUTO-POSTED journal-entry source:
/// creating + recording a bill posts a balanced journal entry (Debit each line's expense/asset
/// account, Credit the AP control account) through the node-resident posting service.
/// </summary>
/// <remarks>
/// <para>
/// <b>The node now owns AP bill data.</b> Writes go through <see cref="IBillPostingService"/> (which
/// posts the auto-JE via the Step-2a node <c>IJournalPostingService</c> over the recoverable
/// <see cref="NodeEfJournalStore"/>); reads come from <see cref="NodeEfBillRepository"/>. So a
/// single-device install no longer depends on signal-bridge for AP. This is ADDITIVE — the Bridge
/// AP path keeps working; the frontend flip and the Rust <c>sc5</c> fail-close are a sequenced
/// follow-up after the security SPOT-CHECK.
/// </para>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET /api/local-node/bills</c> — list bills in a chart. Filters: <c>?chartId=</c>
///     (required), <c>?vendorId=</c>, <c>?status=open</c> (Received/Approved/PartiallyPaid only).</item>
///   <item><c>GET /api/local-node/bills/{id}</c> — full detail incl. lines; opaque 404 if absent /
///     other-tenant.</item>
///   <item><c>POST /api/local-node/bills</c> — create a Draft bill then immediately
///     <see cref="IBillPostingService.RecordAsync"/> it: computes per-line tax, posts the
///     balanced journal entry, transitions the bill to <see cref="BillStatus.Received"/>, and
///     returns the recorded bill incl. its <c>journalEntryId</c>. 201 Created.</item>
///   <item><c>POST /api/local-node/bills/{id}/void</c> — post a reversing journal entry +
///     transition to <see cref="BillStatus.Voided"/>.</item>
///   <item><c>POST /api/local-node/bills/{id}/approve</c> — Received → Approved.</item>
///   <item><c>POST /api/local-node/bills/{id}/dispute</c> — payable → Disputed (no GL change).</item>
///   <item><c>POST /api/local-node/bills/{id}/resolve-dispute</c> — Disputed → Received/Approved.</item>
/// </list>
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Every read + write resolves the active-team-derived tenant
/// via <c>NodeTenant.Resolve(activeTeam)</c> (the same posture as <see cref="JournalEntryRoutes"/>), not
/// a fixed <c>"local"</c> sentinel. The write services read the ambient tenant off the node-resident
/// <see cref="ActiveTeamTenantContext"/> (ADR 0032 identity layer — active-team-bound, retiring the old
/// <c>StaticNodeTenantContext</c> "local" literal); the repository applies a <c>WHERE TenantId</c> — the
/// per-org isolation predicate, so switching the active org switches which org's bills are visible. The
/// bill <c>id</c> is the only caller-supplied identifier the write paths trust.
/// </para>
/// <para>
/// <b>Auto-posted JE is UNSCOPED (no chart).</b> <see cref="BillPostingService.RecordAsync"/> builds
/// the journal entry WITHOUT a <c>ChartId</c> (it carries a <c>bill:&lt;id&gt;</c> source reference),
/// so the posting service's Phase-4 period-gating is SKIPPED — the bill JE posts whenever its
/// referenced GL accounts (the line debit accounts + the AP control account) exist and are postable.
/// This is the faithful cluster-default <see cref="BillPostingService"/> behavior; Step 2c relocates
/// the host, it does not change posting semantics.
/// </para>
/// <para>
/// <b>Financial-cluster audit-envelope durable-layer pattern (ADR 0104 §7).</b> No inline signed
/// audit event — the node IS the durable mutation layer; the bill + JE rows' presence in the keyed
/// SQLCipher store satisfies X-AUDIT, the same deferred posture <see cref="JournalEntryRoutes"/> and
/// the financial masters carry.
/// </para>
/// <para>
/// <b>SC4-C2 recoverability.</b> The whole write path persists ONLY the Store-DEK-enveloped,
/// recoverable <c>local-node.db</c> (bills via <see cref="NodeEfBillRepository"/>, the auto-JE via
/// <see cref="NodeEfJournalStore"/>) — no kernel CRDT / per-team event-log write, and the
/// <c>IDomainEventPublisher</c> is the Noop (no cross-cluster event bus).
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1).</b> These bind the loopback-only Kestrel listener, but a loopback
/// bind authenticates the HOST not the calling PROCESS — so the LISTENER-LEVEL caller-auth
/// middleware (<c>SharedHostedWebApp</c>) gates every non-allowlisted route, including these bill
/// writes, behind the Harborline App's per-boot session token, fail-closed 401, by default. CSRF is N/A
/// (explicit bearer, no cookie/ambient auth).
/// </para>
/// <para>
/// <b>Wiring.</b> The repository accessor + posting-service accessor are injected from the OUTER host
/// container and passed to <see cref="Map"/> as closed-over dependencies — NOT resolved via
/// <c>[FromServices]</c>, which would fail on the inner shared-app container (bug-2849).
/// </para>
/// </remarks>
public static class BillRoutes
{
    /// <summary>Canonical route base for the node-local bills surface.</summary>
    public const string RouteBase = "/api/local-node/bills";

    /// <summary>
    /// Maps the bill routes onto <paramref name="app"/>, closing over the <paramref name="bills"/>
    /// repository accessor (reads) and the <paramref name="posting"/> service accessor (writes) from
    /// the outer host container.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        NodeEfBillRepository bills,
        IBillPostingService posting, IActiveTeamAccessor activeTeam, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(bills);
        ArgumentNullException.ThrowIfNull(posting);

        MapList(app, bills, activeTeam);
        MapDetail(app, bills, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapCreate(app, bills, posting, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapVoid(app, posting, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapApprove(app, posting, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapDispute(app, posting, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        MapResolveDispute(app, posting, activeTeam, timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
    }

    // ── GET /api/local-node/bills — list bills in a chart ─────────────────────────
    private static void MapList(IEndpointRouteBuilder app, NodeEfBillRepository bills, IActiveTeamAccessor activeTeam)
    {
        app.MapGet(RouteBase, async (
            string? chartId,
            string? vendorId,
            string? status,
            CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            if (string.IsNullOrWhiteSpace(chartId))
            {
                return Results.BadRequest(new { error = "chart_id_required" });
            }

            var chart = new ChartOfAccountsId(chartId);
            IReadOnlyList<Bill> rows;

            if (string.Equals(status, "open", StringComparison.OrdinalIgnoreCase))
            {
                var vendor = string.IsNullOrWhiteSpace(vendorId) ? (PartyId?)null : new PartyId(vendorId);
                rows = await bills.QueryOpenAsync(LocalTenantId, chart, vendor, propertyId: null, ct)
                    .ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(vendorId))
            {
                rows = await bills.ListByVendorAsync(LocalTenantId, chart, new PartyId(vendorId), ct)
                    .ConfigureAwait(false);
            }
            else
            {
                rows = await bills.ListByChartAsync(LocalTenantId, chart, ct).ConfigureAwait(false);
            }

            // Newest-first by bill date (small single-device set; in-memory sort mirrors PaymentRoutes).
            var ordered = rows.OrderByDescending(b => b.BillDate).ThenByDescending(b => b.CreatedAtUtc.Value);
            return Results.Ok(new BillListResponse(
                Data: ordered.Select(BillSummaryWire.From).ToList()));
        });
    }

    // ── GET /api/local-node/bills/{id} — detail incl. lines ───────────────────────
    private static void MapDetail(IEndpointRouteBuilder app, NodeEfBillRepository bills, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapGet($"{RouteBase}/{{id}}", async (string id, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var bill = await bills.GetAsync(LocalTenantId, new BillId(id), timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
            return bill is null
                ? Results.NotFound()
                : Results.Ok(new BillDetailResponse(BillDetailWire.From(bill)));
        });
    }

    // ── POST /api/local-node/bills — create Draft + immediate Record (posts the JE) ──
    private static void MapCreate(
        IEndpointRouteBuilder app,
        NodeEfBillRepository bills,
        IBillPostingService posting, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost(RouteBase, async (CreateBillRequest body, HttpContext http, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var authority = FinancialRouteWriteAuthority.Create(http, LocalTenantId, timeProvider);
            if (body is null)
            {
                return Results.BadRequest(new { error = "request_null" });
            }
            if (string.IsNullOrWhiteSpace(body.ChartId))
            {
                return Results.BadRequest(new { error = "chart_id_required" });
            }
            if (string.IsNullOrWhiteSpace(body.BillNumber))
            {
                return Results.BadRequest(new { error = "bill_number_required" });
            }
            if (string.IsNullOrWhiteSpace(body.VendorId))
            {
                return Results.BadRequest(new { error = "vendor_id_required" });
            }
            if (string.IsNullOrWhiteSpace(body.ApAccountId))
            {
                return Results.BadRequest(new { error = "ap_account_required" });
            }
            if (body.Lines is null || body.Lines.Count == 0)
            {
                return Results.BadRequest(new { error = "no_lines" });
            }
            if (!DateOnly.TryParse(body.BillDate, out var billDate))
            {
                return Results.BadRequest(new { error = "invalid_bill_date" });
            }
            if (!DateOnly.TryParse(body.DueDate, out var dueDate))
            {
                return Results.BadRequest(new { error = "invalid_due_date" });
            }
            if (body.Lines.Any(l => string.IsNullOrWhiteSpace(l.DebitAccountId)))
            {
                return Results.BadRequest(new { error = "line_debit_account_required" });
            }

            var billId = string.IsNullOrWhiteSpace(body.Id) ? BillId.NewId() : new BillId(body.Id);

            // Build the bill lines (amount = banker's-round(qty * unitPrice) per BillLine.Create).
            var lines = new List<BillLine>(body.Lines.Count);
            var lineNumber = 1;
            foreach (var l in body.Lines)
            {
                lines.Add(BillLine.Create(
                    billId:         billId,
                    lineNumber:     lineNumber++,
                    description:    l.Description ?? string.Empty,
                    quantity:       l.Quantity,
                    unitPrice:      l.UnitPrice,
                    debitAccountId: new GLAccountId(l.DebitAccountId),
                    taxCodeId:      l.TaxCodeId,
                    propertyId:     l.PropertyId,
                    classificationId: l.ClassificationId));
            }

            var bill = Bill.Create(
                tenantId:    LocalTenantId,
                chartId:     new ChartOfAccountsId(body.ChartId),
                billNumber:  body.BillNumber!,
                vendorId:    new PartyId(body.VendorId!),
                billDate:    billDate,
                dueDate:     dueDate,
                lines:       lines,
                apAccountId: new GLAccountId(body.ApAccountId!),
                createdAtUtc: new Instant(authority.At),
                createdBy:   new PartyId(authority.Principal.Value),
                id:          billId,
                propertyId:  body.PropertyId,
                currency:    string.IsNullOrWhiteSpace(body.Currency) ? "USD" : body.Currency!,
                notes:       body.Notes,
                termsId:     body.TermsId,
                externalRef: body.ExternalRef);

            // Persist the Draft, then record it (posts the auto-JE through the node posting service).
            try
            {
                await bills.UpsertAsync(LocalTenantId, bill, authority.At, ct).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                // Cross-tenant id collision — the upsert's tenant-mismatch guard (a bill id that exists
                // under a DIFFERENT active-team tenant). Real once multiple orgs share the node store.
                return Results.Conflict(new { error = "bill_id_conflict" });
            }
            catch (Exception ex) when (NodePersistenceConflict.IsDuplicate(ex))
            {
                // OBS-2: a unique-constraint race against the recoverable store (e.g. a duplicate
                // (tenant, chart, bill-number)) is a conflict, not a server error — 409, not 500.
                return Results.Conflict(new { error = "bill_id_conflict" });
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            RecordResult result;
            try
            {
                result = await posting.RecordAsync(billId, authority, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (!result.IsSuccess)
            {
                return RecordErrorToResult(result);
            }

            // Audit-envelope: satisfied by the durable SQLCipher store presence (durable-layer pattern).
            var recorded = result.Bill!;
            return Results.Created(
                $"{RouteBase}/{recorded.Id.Value}",
                new BillDetailResponse(BillDetailWire.From(recorded)));
        });
    }

    // ── POST /api/local-node/bills/{id}/void — post a reversing JE ────────────────
    private static void MapVoid(IEndpointRouteBuilder app, IBillPostingService posting, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/void", async (
            string id,
            VoidBillRequest? body,
            HttpContext http,
            CancellationToken ct) =>
        {
            var authority = FinancialRouteWriteAuthority.Create(http, activeTeam, timeProvider);
            var reason = body?.Reason ?? string.Empty;
            VoidResult result;
            try
            {
                result = await posting.VoidAsync(new BillId(id), reason, authority, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (!result.IsSuccess)
            {
                return VoidErrorToResult(result);
            }
            return Results.Ok(new BillDetailResponse(BillDetailWire.From(result.Bill!)));
        });
    }

    // ── POST /api/local-node/bills/{id}/approve — Received → Approved ─────────────
    private static void MapApprove(IEndpointRouteBuilder app, IBillPostingService posting, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/approve", async (
            string id,
            ApproveBillRequest? body,
            HttpContext http,
            CancellationToken ct) =>
        {
            // Mirror the invoice actor stamp: resolve the server-revalidated request principal once,
            // with the same explicit operator fallback for a genuine desktop-plane call, and reuse it
            // for both durable attribution fields.
            var authority = FinancialRouteWriteAuthority.Create(http, activeTeam, timeProvider);
            var approver = authority.Principal.Value;
            if (!string.IsNullOrWhiteSpace(body?.ApprovedByUserId) &&
                !string.Equals(body.ApprovedByUserId, approver, StringComparison.Ordinal))
            {
                return Results.BadRequest(new { error = "approver_identity_mismatch" });
            }

            ApproveResult result;
            try
            {
                result = await posting.ApproveAsync(new BillId(id), approver, authority, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (!result.IsSuccess)
            {
                return ApproveErrorToResult(result);
            }
            return Results.Ok(new BillDetailResponse(BillDetailWire.From(result.Bill!)));
        });
    }

    // ── POST /api/local-node/bills/{id}/dispute — payable → Disputed (no GL change) ──
    private static void MapDispute(IEndpointRouteBuilder app, IBillPostingService posting, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/dispute", async (
            string id,
            DisputeBillRequest? body,
            HttpContext http,
            CancellationToken ct) =>
        {
            var authority = FinancialRouteWriteAuthority.Create(http, activeTeam, timeProvider);
            var reason = body?.Reason ?? string.Empty;
            DisputeResult result;
            try
            {
                result = await posting.DisputeAsync(new BillId(id), reason, authority, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (!result.IsSuccess)
            {
                return DisputeErrorToResult(result);
            }
            return Results.Ok(new BillDetailResponse(BillDetailWire.From(result.Bill!)));
        });
    }

    // ── POST /api/local-node/bills/{id}/resolve-dispute — Disputed → Received/Approved ──
    private static void MapResolveDispute(IEndpointRouteBuilder app, IBillPostingService posting, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{RouteBase}/{{id}}/resolve-dispute", async (
            string id,
            ResolveDisputeBillRequest? body,
            HttpContext http,
            CancellationToken ct) =>
        {
            var authority = FinancialRouteWriteAuthority.Create(http, activeTeam, timeProvider);
            // Default resolution target is Received; "Approved" can be requested explicitly.
            var resolveTo = string.Equals(body?.ResolveTo, "Approved", StringComparison.OrdinalIgnoreCase)
                ? BillStatus.Approved
                : BillStatus.Received;

            ResolveDisputeResult result;
            try
            {
                result = await posting.ResolveDisputeAsync(new BillId(id), resolveTo, authority, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Results.StatusCode(StatusCodes.Status500InternalServerError);
            }

            if (!result.IsSuccess)
            {
                return ResolveDisputeErrorToResult(result);
            }
            return Results.Ok(new BillDetailResponse(BillDetailWire.From(result.Bill!)));
        });
    }

    // ── Posting-result → HTTP response mapping ───────────────────────────────────

    private static IResult RecordErrorToResult(RecordResult result) => result.Error switch
    {
        RecordError.UnknownBill            => Results.NotFound(),
        RecordError.InvalidStatusForRecord => Results.BadRequest(new { error = "invalid_status_for_record", detail = result.Detail }),
        RecordError.NoLines                => Results.BadRequest(new { error = "no_lines", detail = result.Detail }),
        RecordError.JournalRejected        => Results.BadRequest(new { error = "journal_rejected", detail = result.Detail }),
        _                                  => Results.BadRequest(new { error = "record_rejected", detail = result.Detail }),
    };

    private static IResult VoidErrorToResult(VoidResult result) => result.Error switch
    {
        VoidError.UnknownBill              => Results.NotFound(),
        VoidError.InvalidStatusForVoid     => Results.BadRequest(new { error = "invalid_status_for_void", detail = result.Detail }),
        VoidError.NoJournalEntryToReverse  => Results.BadRequest(new { error = "no_journal_entry_to_reverse", detail = result.Detail }),
        VoidError.JournalRejected          => Results.BadRequest(new { error = "journal_rejected", detail = result.Detail }),
        _                                  => Results.BadRequest(new { error = "void_rejected", detail = result.Detail }),
    };

    private static IResult ApproveErrorToResult(ApproveResult result) => result.Error switch
    {
        ApproveError.UnknownBill               => Results.NotFound(),
        ApproveError.InvalidStatusForApproval  => Results.BadRequest(new { error = "invalid_status_for_approval", detail = result.Detail }),
        ApproveError.InvalidApproverId         => Results.BadRequest(new { error = "invalid_approver_id", detail = result.Detail }),
        _                                      => Results.BadRequest(new { error = "approve_rejected", detail = result.Detail }),
    };

    private static IResult DisputeErrorToResult(DisputeResult result) => result.Error switch
    {
        DisputeError.UnknownBill               => Results.NotFound(),
        DisputeError.InvalidStatusForDispute   => Results.BadRequest(new { error = "invalid_status_for_dispute", detail = result.Detail }),
        _                                      => Results.BadRequest(new { error = "dispute_rejected", detail = result.Detail }),
    };

    private static IResult ResolveDisputeErrorToResult(ResolveDisputeResult result) => result.Error switch
    {
        ResolveDisputeError.UnknownBill              => Results.NotFound(),
        ResolveDisputeError.InvalidStatusForResolve  => Results.BadRequest(new { error = "invalid_status_for_resolve", detail = result.Detail }),
        ResolveDisputeError.InvalidResolutionTarget  => Results.BadRequest(new { error = "invalid_resolution_target", detail = result.Detail }),
        _                                            => Results.BadRequest(new { error = "resolve_dispute_rejected", detail = result.Detail }),
    };
}

// ── Wire shapes ─────────────────────────────────────────────────────────────────

/// <summary>Summary row in the node bill list response (camelCase JSON).</summary>
public sealed record BillSummaryWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("billNumber")] string BillNumber,
    [property: JsonPropertyName("vendorId")] string VendorId,
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("billDate")] string BillDate,
    [property: JsonPropertyName("dueDate")] string DueDate,
    [property: JsonPropertyName("subtotal")] double Subtotal,
    [property: JsonPropertyName("taxTotal")] double TaxTotal,
    [property: JsonPropertyName("total")] double Total,
    [property: JsonPropertyName("amountPaid")] double AmountPaid,
    [property: JsonPropertyName("balance")] double Balance,
    [property: JsonPropertyName("journalEntryId")] string? JournalEntryId,
    [property: JsonPropertyName("propertyId")] string? PropertyId)
{
    /// <summary>Projects a domain <see cref="Bill"/> onto the summary wire shape.</summary>
    public static BillSummaryWire From(Bill b) => new(
        Id:             b.Id.Value,
        BillNumber:     b.BillNumber,
        VendorId:       b.VendorId.Value,
        ChartId:        b.ChartId.Value,
        Status:         b.Status.ToString(),
        BillDate:       b.BillDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        DueDate:        b.DueDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Subtotal:       (double)b.Subtotal,
        TaxTotal:       (double)b.TaxTotal,
        Total:          (double)b.Total,
        AmountPaid:     (double)b.AmountPaid,
        Balance:        (double)b.Balance,
        JournalEntryId: b.JournalEntryId?.Value,
        PropertyId:     b.PropertyId);
}

/// <summary>Detail wire shape incl. lines (mirrors the bill aggregate).</summary>
public sealed record BillDetailWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("billNumber")] string BillNumber,
    [property: JsonPropertyName("vendorId")] string VendorId,
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("billDate")] string BillDate,
    [property: JsonPropertyName("dueDate")] string DueDate,
    [property: JsonPropertyName("receivedDate")] string ReceivedDate,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("subtotal")] double Subtotal,
    [property: JsonPropertyName("taxTotal")] double TaxTotal,
    [property: JsonPropertyName("total")] double Total,
    [property: JsonPropertyName("amountPaid")] double AmountPaid,
    [property: JsonPropertyName("balance")] double Balance,
    [property: JsonPropertyName("apAccountId")] string ApAccountId,
    [property: JsonPropertyName("journalEntryId")] string? JournalEntryId,
    [property: JsonPropertyName("voidedByEntryId")] string? VoidedByEntryId,
    [property: JsonPropertyName("approvedByUserId")] string? ApprovedByUserId,
    [property: JsonPropertyName("propertyId")] string? PropertyId,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("externalRef")] string? ExternalRef,
    [property: JsonPropertyName("lines")] IReadOnlyList<BillLineWire> Lines)
{
    /// <summary>Projects a domain <see cref="Bill"/> onto the detail wire shape.</summary>
    public static BillDetailWire From(Bill b) => new(
        Id:               b.Id.Value,
        BillNumber:       b.BillNumber,
        VendorId:         b.VendorId.Value,
        ChartId:          b.ChartId.Value,
        Status:           b.Status.ToString(),
        BillDate:         b.BillDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        DueDate:          b.DueDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        ReceivedDate:     b.ReceivedDate.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        Currency:         b.Currency,
        Subtotal:         (double)b.Subtotal,
        TaxTotal:         (double)b.TaxTotal,
        Total:            (double)b.Total,
        AmountPaid:       (double)b.AmountPaid,
        Balance:          (double)b.Balance,
        ApAccountId:      b.ApAccountId.Value,
        JournalEntryId:   b.JournalEntryId?.Value,
        VoidedByEntryId:  b.VoidedByEntryId?.Value,
        ApprovedByUserId: b.ApprovedByUserId,
        PropertyId:       b.PropertyId,
        Notes:            b.Notes,
        ExternalRef:      b.ExternalRef,
        Lines:            b.Lines.Select(BillLineWire.From).ToList());
}

/// <summary>One line in a bill detail response.</summary>
public sealed record BillLineWire(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("lineNumber")] int LineNumber,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("quantity")] double Quantity,
    [property: JsonPropertyName("unitPrice")] double UnitPrice,
    [property: JsonPropertyName("amount")] double Amount,
    [property: JsonPropertyName("debitAccountId")] string DebitAccountId,
    [property: JsonPropertyName("taxCodeId")] string? TaxCodeId,
    [property: JsonPropertyName("taxAmount")] double TaxAmount,
    [property: JsonPropertyName("propertyId")] string? PropertyId)
{
    /// <summary>Projects a domain <see cref="BillLine"/> onto the line wire shape.</summary>
    public static BillLineWire From(BillLine l) => new(
        Id:             l.Id.Value,
        LineNumber:     l.LineNumber,
        Description:    l.Description,
        Quantity:       (double)l.Quantity,
        UnitPrice:      (double)l.UnitPrice,
        Amount:         (double)l.Amount,
        DebitAccountId: l.DebitAccountId.Value,
        TaxCodeId:      l.TaxCodeId,
        TaxAmount:      (double)l.TaxAmount,
        PropertyId:     l.PropertyId);
}

/// <summary>List response envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record BillListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<BillSummaryWire> Data);

/// <summary>Single-item response envelope: <c>{ "data": {...} }</c>.</summary>
public sealed record BillDetailResponse(
    [property: JsonPropertyName("data")] BillDetailWire Data);

/// <summary>POST body for <c>POST /api/local-node/bills</c> (create + record).</summary>
public sealed record CreateBillRequest(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("chartId")] string ChartId,
    [property: JsonPropertyName("billNumber")] string BillNumber,
    [property: JsonPropertyName("vendorId")] string VendorId,
    [property: JsonPropertyName("apAccountId")] string ApAccountId,
    [property: JsonPropertyName("billDate")] string BillDate,
    [property: JsonPropertyName("dueDate")] string DueDate,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("propertyId")] string? PropertyId,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("termsId")] string? TermsId,
    [property: JsonPropertyName("externalRef")] string? ExternalRef,
    [property: JsonPropertyName("lines")] IReadOnlyList<CreateBillLine> Lines);

/// <summary>One line in a create-bill request.</summary>
public sealed record CreateBillLine(
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("quantity")] decimal Quantity,
    [property: JsonPropertyName("unitPrice")] decimal UnitPrice,
    [property: JsonPropertyName("debitAccountId")] string DebitAccountId,
    [property: JsonPropertyName("taxCodeId")] string? TaxCodeId,
    [property: JsonPropertyName("propertyId")] string? PropertyId,
    [property: JsonPropertyName("classificationId")] string? ClassificationId);

/// <summary>POST body for <c>POST /api/local-node/bills/{id}/void</c>.</summary>
public sealed record VoidBillRequest(
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>POST body for <c>POST /api/local-node/bills/{id}/approve</c> (all optional).</summary>
public sealed record ApproveBillRequest(
    [property: JsonPropertyName("approvedByUserId")] string? ApprovedByUserId);

/// <summary>POST body for <c>POST /api/local-node/bills/{id}/dispute</c>.</summary>
public sealed record DisputeBillRequest(
    [property: JsonPropertyName("reason")] string? Reason);

/// <summary>POST body for <c>POST /api/local-node/bills/{id}/resolve-dispute</c> (all optional).</summary>
public sealed record ResolveDisputeBillRequest(
    [property: JsonPropertyName("resolveTo")] string? ResolveTo);
