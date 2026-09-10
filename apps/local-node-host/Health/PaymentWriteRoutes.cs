using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Financial;
using PeopleModels = Harborline.Api.Blocks.People.Foundation.Models;
using PaymentWriteServices = (Harborline.Api.LocalNodeHost.Data.Financial.NodeEfPaymentRepository Payments,
    Harborline.Api.LocalNodeHost.Data.Financial.NodeEfPaymentApplicationRepository ApplicationRepository,
    Harborline.Api.Blocks.FinancialPayments.Services.IPaymentApplicationService Applications);

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Node-local payment-WRITE surface (ADR 0122 §D4 T2 — CIC un-deferred payment-write 2026-06-16). The
/// invoice / bill <c>/payments</c> sub-resources that let a fully-offline Harborline install (signal-bridge
/// STOPPED) RECORD a payment against an AR invoice or AP bill — entirely over the recoverable
/// <c>local-node.db</c>. Uncleared records remain visible in the create response without changing balances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mirrors the Bridge <c>PaymentsEndpoints</c> contract</b> (<c>POST /api/v1/invoices/{id}/payments</c>
/// / <c>POST /api/v1/bills/{id}/payments</c>) so the frontend rebind is a path swap, not a shape change —
/// but routes the write to the node instead of the Bridge. The node flow goes one step further than the
/// Bridge's "record a Draft payment": it records the payment locally. PPI-1 permits application — and
/// therefore a visible paid / partially-paid open item — only after a balanced <c>payment-clear:{id}</c>
/// journal exists. Because this route does not capture a bank account yet, newly recorded payments remain
/// Draft and are returned as <c>recorded_not_cleared</c>; they do not create a payment application.
/// </para>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET  /api/local-node/invoices/{id}/payments</c> — list inbound payments applied to the
///     invoice (FK-authoritative via the application rows). Empty <c>{ data: [] }</c> when none.</item>
///   <item><c>POST /api/local-node/invoices/{id}/payments</c> — record an inbound payment; apply only
///     after its clearing journal exists.</item>
///   <item><c>GET  /api/local-node/bills/{id}/payments</c> — list outbound payments applied to the bill.</item>
///   <item><c>POST /api/local-node/bills/{id}/payments</c> — record an outbound payment; apply only
///     after its clearing journal exists.</item>
/// </list>
/// </para>
/// <para>
/// <b>Idempotency (ADR 0122 §D4 T2 — SourceReference ALONE).</b> The POST body carries an optional
/// <c>sourceReference</c>. When supplied, a re-driven record (network retry / double-submit) resolves to
/// the EXISTING payment (the node repo dedupes on <see cref="Payment.SourceReference"/> + the
/// <c>ux_payments_tenant_source_ref</c> unique index) — no duplicate payment, no double-apply. When the
/// caller omits it, the route derives a deterministic source reference from the target id + supplied
/// reference so the common UI double-submit is still idempotent.
/// </para>
/// <para>
/// <b>Tenant scoping (ADR 0091 / 0092).</b> Every read + write resolves the active-team-derived tenant via
/// <c>NodeTenant.Resolve(activeTeam)</c> (<c>ActiveTeamTenantContext</c>; ADR 0032 identity layer),
/// server-set (never frontend-passed), not a fixed <c>"local"</c> sentinel. The chart + party are resolved
/// server-side FROM the invoice/bill (never client-supplied), so a payment can only ever be recorded
/// against the active org's own documents.
/// </para>
/// <para>
/// <b>Caller-auth (inc-4 F1).</b> Loopback-only Kestrel listener, but a loopback bind authenticates
/// the HOST not the calling PROCESS — so the LISTENER-LEVEL caller-auth middleware
/// (<c>SharedHostedWebApp</c>) gates this payment-write route (and every non-allowlisted route)
/// behind the Harborline App's per-boot session token, fail-closed 401, by default. CSRF is N/A (explicit
/// bearer, no cookie/ambient auth); the Bridge needs CSRF because it is network-exposed.
/// </para>
/// <para>
/// <b>SC4-C2.</b> The write lands ONLY in the recoverable <c>local-node.db</c>. An uncleared payment is
/// persisted without an application or open-item balance change. No GL JE is posted on record while the
/// route has no bank-account capture. Touches no kernel CRDT-writer type.
/// </para>
/// <para>
/// <b>Wiring.</b> The write-service accessor + the node AR/AP repository accessors are injected from the
/// OUTER host container and passed to <see cref="Map"/> as closed-over dependencies — NOT resolved via
/// <c>[FromServices]</c> on <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c> (bug-2849).
/// </para>
/// </remarks>
public static class PaymentWriteRoutes
{
    /// <summary>Canonical route base for the node-local invoice payments sub-resource.</summary>
    public const string InvoiceRouteBase = "/api/local-node/invoices";

    /// <summary>Canonical route base for the node-local bill payments sub-resource.</summary>
    public const string BillRouteBase = "/api/local-node/bills";


    /// <summary>
    /// Maps the invoice/bill payment write + list routes onto <paramref name="app"/>, closing over the
    /// payment write-service accessor + the node AR/AP repository accessors from the outer host container.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        PaymentWriteServices payments,
        NodeEfInvoiceRepository invoices,
        NodeEfBillRepository bills, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(bills);

        MapListInvoicePayments(app, payments, invoices, activeTeam, timeProvider);
        MapRecordInvoicePayment(app, payments, invoices, activeTeam, timeProvider);
        MapListBillPayments(app, payments, bills, activeTeam, timeProvider);
        MapRecordBillPayment(app, payments, bills, activeTeam, timeProvider);
    }

    // ── GET /api/local-node/invoices/{id}/payments ────────────────────────────────
    private static void MapListInvoicePayments(
        IEndpointRouteBuilder app,
        PaymentWriteServices payments,
        NodeEfInvoiceRepository invoices, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapGet($"{InvoiceRouteBase}/{{id}}/payments", async (string id, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var admittedAt = timeProvider.GetUtcNow();
            var invoice = await invoices.GetAsync(LocalTenantId, new InvoiceId(id), admittedAt, ct)
                .ConfigureAwait(false);
            if (invoice is null)
            {
                return Results.NotFound();
            }

            var rows = await BuildAppliedPaymentRowsAsync(
                payments, AppliedTo.Invoice, id, PaymentDirection.Inbound, LocalTenantId, admittedAt, ct).ConfigureAwait(false);
            return Results.Ok(new PaymentWriteListResponse(rows));
        });
    }

    // ── POST /api/local-node/invoices/{id}/payments ───────────────────────────────
    private static void MapRecordInvoicePayment(
        IEndpointRouteBuilder app,
        PaymentWriteServices payments,
        NodeEfInvoiceRepository invoices, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{InvoiceRouteBase}/{{id}}/payments", async (
            string id,
            RecordNodePaymentRequest body,
            HttpContext http,
            CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var admittedAt = timeProvider.GetUtcNow();
            if (!TryValidate(body, out var method, out var paymentDate, out var bad))
            {
                return bad!;
            }

            var invoice = await invoices.GetAsync(LocalTenantId, new InvoiceId(id), admittedAt, ct)
                .ConfigureAwait(false);
            if (invoice is null)
            {
                return Results.NotFound();
            }

            // Only payable statuses (mirrors the Bridge invoice-payment guard).
            if (invoice.Status != InvoiceStatus.Issued && invoice.Status != InvoiceStatus.PartiallyPaid)
            {
                return Results.BadRequest(new { error = "invoice_not_payable", status = invoice.Status.ToString() });
            }

            // Source reference: caller-supplied (idempotency token) or derived deterministically.
            var sourceRef = ResolveSourceReference(body!, "inv", id);

            // Idempotent record: a re-driven submit resolves to the existing payment.
            var existing = await payments.Payments
                .FindBySourceReferenceAsync(LocalTenantId, sourceRef, ct).ConfigureAwait(false);
            Payment payment;
            if (existing is not null)
            {
                payment = existing;
            }
            else
            {
                payment = Payment.Create(
                    tenantId:       LocalTenantId,
                    chartId:        invoice.ChartId,
                    direction:      PaymentDirection.Inbound,
                    paymentNumber:  ResolvePaymentNumber(body!),
                    partyId:        invoice.CustomerId,
                    paymentDate:    paymentDate,
                    amount:         body!.Amount,
                    method:         method,
                    createdAtUtc:   new Instant(admittedAt),
                    createdBy:      NodeCallerParty.Resolve(http),
                    currency:       body.Currency!,
                    reference:      body.Reference,
                    notes:          body.Notes,
                    sourceReference: sourceRef,
                    intendedTargetType: AppliedTo.Invoice,
                    intendedTargetId: invoice.Id.Value);

                try
                {
                    await payments.Payments.AddAsync(LocalTenantId, payment, admittedAt, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (NodePersistenceConflict.IsDuplicate(ex))
                {
                    // OBS-2: a SourceReference unique-constraint race against the recoverable store
                    // (ux_payments_tenant_source_ref) — a concurrent re-driven submit that slipped past
                    // the cooperative FindBySourceReferenceAsync check above. It is a duplicate-submit
                    // conflict, not a server error — 409, not 500.
                    return Results.Conflict(new { error = "duplicate_payment" });
                }
                catch (Exception)
                {
                    return Results.StatusCode(StatusCodes.Status500InternalServerError);
                }

                // PPI-1: an open item may present as paid only when the clearing journal exists.
                // Payment.Create leaves JournalEntryId null because this route does not yet capture the
                // required BankAccountId, so the ordinary record path remains visibly not cleared.
                if (payment.JournalEntryId is not null)
                {
                    var apply = await payments.Applications.ApplyAsync(
                            paymentId:      payment.Id,
                            appliedTo:      AppliedTo.Invoice,
                            targetId:       invoice.Id.Value,
                            amountApplied:  body.Amount,
                            discountAmount: 0m,
                            writeoffAmount: 0m,
                            actor:          NodeCallerParty.Resolve(http),
                            ct:             ct)
                        .ConfigureAwait(false);
                    if (apply.Error != ApplyError.None)
                    {
                        return Results.BadRequest(new { error = "apply_failed", detail = apply.ErrorMessage });
                    }
                }
            }

            return Results.Created(
                $"{InvoiceRouteBase}/{id}/payments/{payment.Id.Value}",
                new PaymentWriteItemResponse(PaymentWireRow.From(payment)));
        });
    }

    // ── GET /api/local-node/bills/{id}/payments ────────────────────────────────────
    private static void MapListBillPayments(
        IEndpointRouteBuilder app,
        PaymentWriteServices payments,
        NodeEfBillRepository bills, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapGet($"{BillRouteBase}/{{id}}/payments", async (string id, CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var admittedAt = timeProvider.GetUtcNow();
            var bill = await bills.GetAsync(LocalTenantId, new BillId(id), admittedAt, ct).ConfigureAwait(false);
            if (bill is null)
            {
                return Results.NotFound();
            }

            var rows = await BuildAppliedPaymentRowsAsync(
                payments, AppliedTo.Bill, id, PaymentDirection.Outbound, LocalTenantId, admittedAt, ct).ConfigureAwait(false);
            return Results.Ok(new PaymentWriteListResponse(rows));
        });
    }

    // ── POST /api/local-node/bills/{id}/payments ───────────────────────────────────
    private static void MapRecordBillPayment(
        IEndpointRouteBuilder app,
        PaymentWriteServices payments,
        NodeEfBillRepository bills, IActiveTeamAccessor activeTeam, TimeProvider timeProvider)
    {
        app.MapPost($"{BillRouteBase}/{{id}}/payments", async (
            string id,
            RecordNodePaymentRequest body,
            HttpContext http,
            CancellationToken ct) =>
        {
            var LocalTenantId = NodeTenant.Resolve(activeTeam);
            var admittedAt = timeProvider.GetUtcNow();
            if (!TryValidate(body, out var method, out var paymentDate, out var bad))
            {
                return bad!;
            }

            var bill = await bills.GetAsync(LocalTenantId, new BillId(id), admittedAt, ct).ConfigureAwait(false);
            if (bill is null)
            {
                return Results.NotFound();
            }

            // Only payable statuses (mirrors the Bridge bill-payment guard).
            if (bill.Status != BillStatus.Received
                && bill.Status != BillStatus.Approved
                && bill.Status != BillStatus.PartiallyPaid)
            {
                return Results.BadRequest(new { error = "bill_not_payable", status = bill.Status.ToString() });
            }

            var sourceRef = ResolveSourceReference(body!, "bill", id);

            var existing = await payments.Payments
                .FindBySourceReferenceAsync(LocalTenantId, sourceRef, ct).ConfigureAwait(false);
            Payment payment;
            if (existing is not null)
            {
                payment = existing;
            }
            else
            {
                payment = Payment.Create(
                    tenantId:       LocalTenantId,
                    chartId:        bill.ChartId,
                    direction:      PaymentDirection.Outbound,
                    paymentNumber:  ResolvePaymentNumber(body!),
                    partyId:        bill.VendorId,
                    paymentDate:    paymentDate,
                    amount:         body!.Amount,
                    method:         method,
                    createdAtUtc:   new Instant(admittedAt),
                    createdBy:      NodeCallerParty.Resolve(http),
                    currency:       body.Currency!,
                    reference:      body.Reference,
                    notes:          body.Notes,
                    sourceReference: sourceRef,
                    intendedTargetType: AppliedTo.Bill,
                    intendedTargetId: bill.Id.Value);

                try
                {
                    await payments.Payments.AddAsync(LocalTenantId, payment, admittedAt, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (NodePersistenceConflict.IsDuplicate(ex))
                {
                    // OBS-2: a SourceReference unique-constraint race (ux_payments_tenant_source_ref)
                    // on the bill-payment path — duplicate-submit conflict, 409 not 500.
                    return Results.Conflict(new { error = "duplicate_payment" });
                }
                catch (Exception)
                {
                    return Results.StatusCode(StatusCodes.Status500InternalServerError);
                }

                // Same PPI-1 gate as the invoice path: without a clearing JE, the bill must remain open.
                if (payment.JournalEntryId is not null)
                {
                    var apply = await payments.Applications.ApplyAsync(
                            paymentId:      payment.Id,
                            appliedTo:      AppliedTo.Bill,
                            targetId:       bill.Id.Value,
                            amountApplied:  body.Amount,
                            discountAmount: 0m,
                            writeoffAmount: 0m,
                            actor:          NodeCallerParty.Resolve(http),
                            ct:             ct)
                        .ConfigureAwait(false);
                    if (apply.Error != ApplyError.None)
                    {
                        return Results.BadRequest(new { error = "apply_failed", detail = apply.ErrorMessage });
                    }
                }
            }

            return Results.Created(
                $"{BillRouteBase}/{id}/payments/{payment.Id.Value}",
                new PaymentWriteItemResponse(PaymentWireRow.From(payment)));
        });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists the payments applied to a target (invoice/bill) FK-authoritatively: the application rows
    /// keyed on the target id, resolved to their owning payments (matching the expected direction).
    /// </summary>
    private static async Task<IReadOnlyList<PaymentWireRow>> BuildAppliedPaymentRowsAsync(
        PaymentWriteServices payments,
        AppliedTo appliedTo,
        string targetId,
        PaymentDirection expectedDirection,
        TenantId localTenant,
        DateTimeOffset admittedAt,
        CancellationToken ct)
    {
        // FK-authoritative: the application rows keyed on the target id (invoice/bill), resolved to their
        // owning payments. Distinct-by-payment (a single payment may have multiple applications to a target).
        var applications = await payments.ApplicationRepository
            .ListByTargetAsync(localTenant, targetId, ct).ConfigureAwait(false);

        var rows = new List<PaymentWireRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var app in applications.Where(a => a.IsActive))
        {
            if (app.AppliedTo != appliedTo || !seen.Add(app.PaymentId.Value))
            {
                continue;
            }
            var pmt = await payments.Payments.GetAsync(localTenant, app.PaymentId, admittedAt, ct).ConfigureAwait(false);
            if (pmt is null || pmt.Direction != expectedDirection)
            {
                continue;
            }
            rows.Add(PaymentWireRow.From(pmt));
        }
        return rows.OrderByDescending(r => r.PaymentDate).ToList();
    }

    private static bool TryValidate(
        RecordNodePaymentRequest? body,
        out PaymentMethod method,
        out DateOnly paymentDate,
        out IResult? bad)
    {
        method = default;
        paymentDate = default;
        bad = null;

        if (body is null || body.Amount <= 0m || string.IsNullOrWhiteSpace(body.Currency))
        {
            bad = Results.BadRequest(new { error = "amount_positive_and_currency_required" });
            return false;
        }
        if (!Enum.TryParse(body.Method, ignoreCase: true, out method))
        {
            bad = Results.BadRequest(new { error = "unknown_payment_method", value = body.Method });
            return false;
        }
        if (!DateOnly.TryParse(body.PaymentDate, out paymentDate))
        {
            bad = Results.BadRequest(new { error = "invalid_payment_date", value = body.PaymentDate });
            return false;
        }
        return true;
    }

    /// <summary>
    /// Deterministic source reference for idempotency. Prefers the caller-supplied
    /// <c>sourceReference</c>; otherwise derives one from the kind + target + caller reference so the
    /// common UI double-submit (same invoice + same reference) dedupes. When the caller supplies neither,
    /// returns a unique value so distinct partial payments are not collapsed.
    /// </summary>
    private static string ResolveSourceReference(RecordNodePaymentRequest body, string kind, string targetId)
    {
        if (!string.IsNullOrWhiteSpace(body.SourceReference))
        {
            return body.SourceReference!;
        }
        if (!string.IsNullOrWhiteSpace(body.Reference))
        {
            return $"node:{kind}:{targetId}:{body.Reference}";
        }
        return $"node:{kind}:{targetId}:{Guid.NewGuid():N}";
    }

    private static string ResolvePaymentNumber(RecordNodePaymentRequest body) =>
        string.IsNullOrWhiteSpace(body.Reference)
            ? $"PAY-{Guid.NewGuid():N}"[..16]
            : body.Reference!;
}

// ── Wire shapes ───────────────────────────────────────────────────────────────

/// <summary>
/// POST body for the node invoice/bill <c>/payments</c> sub-resources. Mirrors the Bridge
/// <c>RecordPaymentBody</c> shape (so the frontend rebind is a path swap) + adds the optional
/// <c>sourceReference</c> idempotency token. <c>PaymentDate</c> is an ISO calendar date string
/// (yyyy-MM-dd) to keep the wire JSON-friendly across the loopback boundary.
/// </summary>
public sealed record RecordNodePaymentRequest(
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("paymentDate")] string PaymentDate,
    [property: JsonPropertyName("reference")] string? Reference = null,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("sourceReference")] string? SourceReference = null);

/// <summary>Flat read row projected from a recorded <see cref="Payment"/>.</summary>
public sealed record PaymentWireRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("paymentDate")] string PaymentDate,
    [property: JsonPropertyName("reference")] string? Reference,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("clearingState")] string ClearingState)
{
    /// <summary>Projects a recorded <see cref="Payment"/> onto the wire row.</summary>
    public static PaymentWireRow From(Payment p) => new(
        Id:          p.Id.Value,
        Amount:      p.Amount,
        Currency:    p.Currency,
        Method:      p.Method.ToString(),
        PaymentDate: p.PaymentDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        Reference:   p.Reference,
        Status:      p.Status.ToString(),
        ClearingState: p.JournalEntryId is null ? "recorded_not_cleared" : "cleared");
}

/// <summary>List response envelope: <c>{ "data": [...] }</c>.</summary>
public sealed record PaymentWriteListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<PaymentWireRow> Data);

/// <summary>Single-item response envelope: <c>{ "data": {...} }</c>.</summary>
public sealed record PaymentWriteItemResponse(
    [property: JsonPropertyName("data")] PaymentWireRow Data);
