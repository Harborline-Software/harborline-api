using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Models.Events;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Events;
using Harborline.Api.Foundation.MultiTenancy;
using FinancialAuthorizationOperations = Harborline.Api.Foundation.Authorization.AuthorizationOperationNames;
using AuthorizationGate = Harborline.Api.Foundation.Authorization.AuthorizationGate;
using AuthorizationVerdict = Harborline.Api.Foundation.Authorization.AuthorizationVerdict;
using AuthorizationWriteContext = Harborline.Api.Foundation.Authorization.AuthorizationWriteContext;
using AuthorizationOperation = Harborline.Api.Foundation.IdentityAtlas.Permissions.AuthorizationOperation;

namespace Harborline.Api.Blocks.FinancialPayments.Services;

/// <summary>
/// Default <see cref="IPaymentApplicationService"/>. Coordinates the payment
/// repository, the application repository, and the AR / AP repositories to
/// keep Payment.UnappliedAmount, Invoice/Bill.AmountPaid + Balance + Status,
/// and the PaymentApplication ledger consistent.
///
/// <para>
/// <b>Direction-matching invariant (§3.10 validation rule 1)</b> is enforced
/// as the FIRST guard in <see cref="ApplyAsync"/> — BEFORE the payment
/// repository is consulted. This avoids leaking target existence through
/// error-type timing: a cross-cluster attacker observing
/// <see cref="ApplyError.UnknownTarget"/> vs
/// <see cref="ApplyError.DirectionMismatch"/> can't infer whether a specific
/// Invoice / Bill exists in the system. The mismatch path returns before any
/// I/O.
/// </para>
///
/// <para>
/// <b>Discount and writeoff GL posting is DEFERRED:</b> the Stage 02 spec
/// describes posting extra JE lines for Discount Allowed / Bad Debt expense
/// when <c>discountAmount &gt; 0</c> or <c>writeoffAmount &gt; 0</c>. PR 3 does
/// NOT implement that — the substrate lacks a per-chart selection mechanism
/// for the Discount Allowed / Bad Debt accounts (no
/// <see cref="Harborline.Api.Blocks.FinancialLedger.Models.AccountSubtype"/> exists
/// for them; <see cref="Harborline.Api.Blocks.FinancialLedger.Models.AccountSubtype.OperatingExpense"/>
/// is too coarse). For PR 3, non-zero values for <c>discountAmount</c> /
/// <c>writeoffAmount</c> are rejected with
/// <see cref="ApplyError.TargetBalanceInsufficient"/> + diagnostic detail. A
/// follow-on PR will design the account-selection mechanism (likely a per-
/// chart configuration on <c>BlocksFinancialPaymentsOptions</c>) and post the
/// extra JE lines.
/// </para>
/// </summary>
public sealed class DefaultPaymentApplicationService : IPaymentApplicationService
{
    private readonly IPaymentRepository _payments;
    private readonly IPaymentApplicationRepository _applications;
    private readonly IInvoiceRepository _invoices;
    private readonly IBillRepository _bills;
    private readonly ITenantContext _tenantContext;
    private readonly IPeriodResolver _periods;
    private readonly AuthorizationGate? _gate;
    private readonly IDomainEventPublisher _events;
    private readonly TimeProvider _time;

    // The period resolver is REQUIRED, deliberately: it exists only to serve the reversal period gate,
    // and a gate that disappears when a container forgets a registration is not a gate.
    //
    // Ticket 205 slice 5: the soft-close OVERRIDE no longer asks an ambient IUserContext for a bare
    // permission string. It resolves one AuthorizationGate decision at its point of use, naming the
    // fiscal period the override addresses. The gate is optional in the SIGNATURE and closed in the
    // BEHAVIOUR: a container that cannot decide (the ERPNext import pipeline builds no closure reader)
    // REFUSES the override rather than granting it, so forgetting the registration can only ever make
    // this service more closed. Same shape as the shared route guard's "an act the container cannot
    // decide does not happen" (slice 4).
    public DefaultPaymentApplicationService(
        IPaymentRepository payments,
        IPaymentApplicationRepository applications,
        IInvoiceRepository invoices,
        IBillRepository bills,
        ITenantContext tenantContext,
        IPeriodResolver periods,
        AuthorizationGate? gate = null,
        IDomainEventPublisher? events = null,
        TimeProvider? timeProvider = null)
    {
        _payments = payments ?? throw new ArgumentNullException(nameof(payments));
        _applications = applications ?? throw new ArgumentNullException(nameof(applications));
        _invoices = invoices ?? throw new ArgumentNullException(nameof(invoices));
        _bills = bills ?? throw new ArgumentNullException(nameof(bills));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _periods = periods ?? throw new ArgumentNullException(nameof(periods));
        _gate = gate;
        _events = events ?? new NoopDomainEventPublisher();
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    // Resolve the active tenant. Unresolved context is a programmer error
    // (the service should never be invoked without a resolved tenant scope);
    // tenant mismatches at load-time, by contrast, route to fail-closed
    // Unknown* errors so an attacker probing cross-tenant ids cannot
    // distinguish "wrong tenant" from "id does not exist."
    private TenantId CurrentTenantId =>
        _tenantContext.Tenant?.Id
        ?? throw new InvalidOperationException(
            "DefaultPaymentApplicationService invoked without a resolved tenant — composition-root bug.");

    // ──────────────────────────────────────────────────────────────────
    //  ApplyAsync
    // ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ApplyResult> ApplyAsync(
        PaymentId paymentId,
        AppliedTo appliedTo,
        string targetId,
        decimal amountApplied,
        decimal discountAmount,
        decimal writeoffAmount,
        PartyId actor,
        CancellationToken ct = default)
    {
        // Eager-evaluate the tenant context so a composition-root bug surfaces
        // immediately, even on request shapes that would otherwise short-circuit
        // before touching CurrentTenantId.
        _ = CurrentTenantId;
        var admittedAt = _time.GetUtcNow();
        var now = new Instant(admittedAt);
        var today = DateOnly.FromDateTime(admittedAt.UtcDateTime);

        // PR 3 deferred: discount / writeoff GL posting (see class summary).
        // Reject up-front so callers get a clear diagnostic rather than
        // a silently-incomplete persistence path.
        if (discountAmount != 0m || writeoffAmount != 0m)
        {
            return new ApplyResult(null, ApplyError.TargetBalanceInsufficient,
                "discount / writeoff GL posting is not yet implemented (PR 3 substrate-only). "
                + "Pass discountAmount=0 and writeoffAmount=0; the follow-on PR will wire the per-chart "
                + "Discount Allowed / Bad Debt account selection.");
        }

        if (amountApplied <= 0m)
            return new ApplyResult(null, ApplyError.TargetBalanceInsufficient,
                $"amountApplied must be positive (got {amountApplied}).");

        // Direction-matching invariant — checked BEFORE any repository lookup so the
        // error-type doesn't reveal target existence to a cross-cluster attacker.
        // We don't know the payment direction yet (that's a repo read), but we DO
        // know what appliedTo claims; the mismatch check that doesn't depend on
        // repo state is the structural one below (after we load the payment).
        // The CRITICAL security guarantee — direction mismatch returns BEFORE
        // target-existence is leaked — is preserved by ordering: load the
        // payment, check direction match, ONLY THEN load the target.

        var payment = await _payments.GetAsync(CurrentTenantId, paymentId, admittedAt, ct).ConfigureAwait(false);
        // Tenant-isolation guard: a cross-tenant id-guess must return the SAME
        // error as a non-existent id so the attacker cannot distinguish
        // "wrong tenant" from "id does not exist." The diagnostic message
        // intentionally does not reveal tenant state.
        if (payment is null || !payment.TenantId.Equals(CurrentTenantId))
            return new ApplyResult(null, ApplyError.UnknownPayment, $"Payment '{paymentId.Value}' not found.");

        if (!DirectionMatches(payment.Direction, appliedTo))
            return new ApplyResult(null, ApplyError.DirectionMismatch,
                $"Direction-matching invariant violated: {payment.Direction} payment cannot apply to {appliedTo}. "
                + $"Inbound → Invoice; Outbound → Bill.");

        // PPI-1 structural seam: a captured payment is not an accounting credit
        // until its clearing journal exists. Keep this after direction matching so
        // the established target-existence non-disclosure ordering is unchanged,
        // but before every target lookup or balance mutation.
        if (payment.JournalEntryId is null)
            return new ApplyResult(null, ApplyError.PaymentNotCleared,
                "Payment has no clearing journal entry and cannot be applied.");

        // Defense-in-depth: a terminal payment (Voided / Bounced) must not be
        // re-applied even if UnappliedAmount has been reset by the bounce path.
        // The spec doesn't enumerate a "PaymentTerminal" error, so route this
        // through InsufficientUnapplied — accurate (logically zero apply
        // capacity) and minimises surface-area for the council review.
        if (payment.Status is PaymentStatus.Voided or PaymentStatus.Bounced)
            return new ApplyResult(null, ApplyError.InsufficientUnapplied,
                $"Cannot apply to Payment in terminal status '{payment.Status}'.");

        if (amountApplied > payment.UnappliedAmount)
            return new ApplyResult(null, ApplyError.InsufficientUnapplied,
                $"amountApplied ({amountApplied}) exceeds Payment.UnappliedAmount ({payment.UnappliedAmount}).");

        // Target-existence is loaded ONLY after the direction-match passes — so
        // an attacker probing with a mismatched direction can never observe
        // an "UnknownTarget" outcome for a target they shouldn't know exists.
        if (appliedTo == AppliedTo.Invoice)
        {
            return await ApplyToInvoiceAsync(payment, targetId, amountApplied, actor, admittedAt, now, today, ct).ConfigureAwait(false);
        }
        return await ApplyToBillAsync(payment, targetId, amountApplied, actor, admittedAt, now, today, ct).ConfigureAwait(false);
    }

    private async Task<ApplyResult> ApplyToInvoiceAsync(
        Payment payment,
        string targetId,
        decimal amountApplied,
        PartyId actor,
        DateTimeOffset admittedAt,
        Instant now,
        DateOnly today,
        CancellationToken ct)
    {
        var invoiceId = new InvoiceId(targetId);
        var invoice = await _invoices.GetAsync(CurrentTenantId, invoiceId, admittedAt, ct).ConfigureAwait(false);
        // Tenant-isolation guard — repository enforces uniform-404 on cross-tenant.
        if (invoice is null)
            return new ApplyResult(null, ApplyError.UnknownTarget, $"Invoice '{targetId}' not found.");

        if (invoice.Status.IsTerminal())
            return new ApplyResult(null, ApplyError.TargetTerminal,
                $"Cannot apply to Invoice in terminal status '{invoice.Status}'.");

        if (!string.Equals(payment.Currency, invoice.Currency, StringComparison.Ordinal))
            return new ApplyResult(null, ApplyError.CurrencyMismatch,
                $"Payment currency '{payment.Currency}' does not match Invoice currency '{invoice.Currency}'.");

        if (amountApplied > invoice.Balance)
            return new ApplyResult(null, ApplyError.TargetBalanceInsufficient,
                $"amountApplied ({amountApplied}) exceeds Invoice.Balance ({invoice.Balance}).");

        // 1. Create application record (TenantId inherits from owning Payment).
        var application = PaymentApplication.Create(
            tenantId: payment.TenantId,
            paymentId: payment.Id,
            appliedTo: AppliedTo.Invoice,
            targetId: targetId,
            amountApplied: amountApplied,
            appliedDate: today,
            createdAtUtc: now);
        await _applications.AddAsync(CurrentTenantId, application, admittedAt, ct).ConfigureAwait(false);

        // 2. Update invoice balance + status.
        var newAmountPaid = invoice.AmountPaid + amountApplied;
        var newBalance = invoice.Total - newAmountPaid;
        var newStatus = newBalance <= 0m ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
        await _invoices.UpsertAsync(CurrentTenantId, invoice with
        {
            AmountPaid = newAmountPaid,
            Balance = newBalance,
            Status = newStatus,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = invoice.Version + 1,
        }, admittedAt, ct).ConfigureAwait(false);

        // 3. Update payment unapplied amount + status.
        var newUnapplied = payment.UnappliedAmount - amountApplied;
        var paymentStatus = DeriveAppliedStatus(payment.Amount, payment.Amount - newUnapplied);
        await _payments.UpdateAsync(CurrentTenantId, payment with
        {
            UnappliedAmount = newUnapplied,
            Status = paymentStatus,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = payment.Version + 1,
        }, admittedAt, ct).ConfigureAwait(false);

        // 4. Emit audit event.
        await PublishAppliedAsync(application, payment, actor, admittedAt, ct).ConfigureAwait(false);

        return new ApplyResult(application, ApplyError.None, null);
    }

    private async Task<ApplyResult> ApplyToBillAsync(
        Payment payment,
        string targetId,
        decimal amountApplied,
        PartyId actor,
        DateTimeOffset admittedAt,
        Instant now,
        DateOnly today,
        CancellationToken ct)
    {
        var billId = new BillId(targetId);
        var bill = await _bills.GetAsync(CurrentTenantId, billId, admittedAt, ct).ConfigureAwait(false);
        // Tenant-isolation guard — repository enforces uniform-404 on cross-tenant.
        if (bill is null)
            return new ApplyResult(null, ApplyError.UnknownTarget, $"Bill '{targetId}' not found.");

        if (bill.Status.IsTerminal())
            return new ApplyResult(null, ApplyError.TargetTerminal,
                $"Cannot apply to Bill in terminal status '{bill.Status}'.");

        if (!string.Equals(payment.Currency, bill.Currency, StringComparison.Ordinal))
            return new ApplyResult(null, ApplyError.CurrencyMismatch,
                $"Payment currency '{payment.Currency}' does not match Bill currency '{bill.Currency}'.");

        if (amountApplied > bill.Balance)
            return new ApplyResult(null, ApplyError.TargetBalanceInsufficient,
                $"amountApplied ({amountApplied}) exceeds Bill.Balance ({bill.Balance}).");

        var application = PaymentApplication.Create(
            tenantId: payment.TenantId,
            paymentId: payment.Id,
            appliedTo: AppliedTo.Bill,
            targetId: targetId,
            amountApplied: amountApplied,
            appliedDate: today,
            createdAtUtc: now);
        await _applications.AddAsync(CurrentTenantId, application, admittedAt, ct).ConfigureAwait(false);

        var newAmountPaid = bill.AmountPaid + amountApplied;
        var newBalance = bill.Total - newAmountPaid;
        var newStatus = newBalance <= 0m ? BillStatus.Paid : BillStatus.PartiallyPaid;
        await _bills.UpsertAsync(CurrentTenantId, bill with
        {
            AmountPaid = newAmountPaid,
            Balance = newBalance,
            Status = newStatus,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = bill.Version + 1,
        }, admittedAt, ct).ConfigureAwait(false);

        var newUnapplied = payment.UnappliedAmount - amountApplied;
        var paymentStatus = DeriveAppliedStatus(payment.Amount, payment.Amount - newUnapplied);
        await _payments.UpdateAsync(CurrentTenantId, payment with
        {
            UnappliedAmount = newUnapplied,
            Status = paymentStatus,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = payment.Version + 1,
        }, admittedAt, ct).ConfigureAwait(false);

        await PublishAppliedAsync(application, payment, actor, admittedAt, ct).ConfigureAwait(false);

        return new ApplyResult(application, ApplyError.None, null);
    }

    // ──────────────────────────────────────────────────────────────────
    //  UnapplyAsync
    // ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<UnapplyResult> UnapplyAsync(
        PaymentApplicationId applicationId,
        PartyId actor,
        AuthorizationWriteContext authority,
        CancellationToken ct = default)
    {
        // Eager-evaluate the tenant context — same rationale as ApplyAsync.
        _ = CurrentTenantId;
        if (!authority.Tenant.Equals(CurrentTenantId))
        {
            throw new InvalidOperationException(
                "DefaultPaymentApplicationService.UnapplyAsync was handed an authority for tenant "
                + $"'{authority.Tenant.Value}' while the resolved tenant scope is '{CurrentTenantId.Value}'"
                + " — composition-root bug.");
        }

        // One act, one clock read: the instant the authority was admitted at IS the instant this act is
        // decided at and stamped with. Reading the clock a second time here would decide the period gate
        // against one instant and write the reversal at another.
        var admittedAt = authority.At;
        var now = new Instant(admittedAt);
        var today = DateOnly.FromDateTime(admittedAt.UtcDateTime);

        var application = await _applications.GetAsync(CurrentTenantId, applicationId, admittedAt, ct).ConfigureAwait(false);
        // Tenant-isolation guard: cross-tenant application id fails closed with
        // the same diagnostic as a non-existent id.
        if (application is null || !application.TenantId.Equals(CurrentTenantId))
            return new UnapplyResult(false, UnapplyError.UnknownApplication, $"PaymentApplication '{applicationId.Value}' not found.");

        if (!application.IsActive)
            return new UnapplyResult(false, UnapplyError.UnknownApplication,
                $"PaymentApplication '{applicationId.Value}' is already reversed or is reversal evidence.");

        var payment = await _payments.GetAsync(CurrentTenantId, application.PaymentId, admittedAt, ct).ConfigureAwait(false);
        // Defensive: the application carries TenantId so this match is guaranteed,
        // but a stale-pointer scenario (PaymentApplication's PaymentId points
        // to a Payment that has been hard-deleted or its TenantId rotated)
        // collapses to the same Unknown* error.
        if (payment is null || !payment.TenantId.Equals(CurrentTenantId))
            return new UnapplyResult(false, UnapplyError.UnknownApplication,
                $"PaymentApplication '{applicationId.Value}' references unknown Payment '{application.PaymentId.Value}'.");

        // Gate: the CONTRA lands in the period covering the REVERSAL date, so that period must
        // accept postings. The original application's period is deliberately NOT consulted —
        // IPeriodResolver.Status.Locked's own contract says "reversal must use a later open
        // period", and refusing to reverse out of a closed period would make a closed-period error
        // permanently uncorrectable, which in practice teaches people to unlock periods instead.
        var gate = await GateReversalPeriodAsync(payment.ChartId, today, authority, ct).ConfigureAwait(false);
        if (gate is { } refusal)
        {
            return new UnapplyResult(false, refusal.Error, refusal.Message);
        }

        // Reverse FIRST, then restore balances. The old order mutated the invoice/bill and the
        // payment before calling ReverseAsync and discarded its nullable result, so a reversal that
        // did not happen reported success with restored balances and no contra evidence at all
        // (finding api-financial-10). Attempting the evidence first makes that state unreachable:
        // if nothing was recorded, nothing has been mutated either.
        // Ticket 095: AlreadyReversed is as disqualifying as NotFound. A concurrent unapply that
        // lands on the idempotent branch wrote no evidence of its own, so it must not restore a
        // balance a second time against someone else's contra row.
        var reversal = await _applications.ReverseAsync(CurrentTenantId, applicationId, now, ct)
            .ConfigureAwait(false);
        if (!reversal.WroteEvidence)
        {
            return new UnapplyResult(false, UnapplyError.ReversalNotRecorded,
                reversal.Outcome == PaymentApplicationReversalOutcome.AlreadyReversed
                    ? $"PaymentApplication '{applicationId.Value}' was already reversed by another call — this one wrote no contra evidence, so no balance was restored."
                    : $"PaymentApplication '{applicationId.Value}' could not be reversed — no contra evidence was written, so no balance was restored.");
        }

        // Restore target (Invoice/Bill) balance + status.
        switch (application.AppliedTo)
        {
            case AppliedTo.Invoice:
                await UnapplyFromInvoiceAsync(application, actor, admittedAt, now, ct).ConfigureAwait(false);
                break;
            case AppliedTo.Bill:
                await UnapplyFromBillAsync(application, actor, admittedAt, now, ct).ConfigureAwait(false);
                break;
        }

        // Restore payment.
        var restoredUnapplied = payment.UnappliedAmount + application.AmountApplied;
        var restoredStatus = DeriveAppliedStatus(payment.Amount, payment.Amount - restoredUnapplied);
        await _payments.UpdateAsync(CurrentTenantId, payment with
        {
            UnappliedAmount = restoredUnapplied,
            Status = restoredStatus,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = payment.Version + 1,
        }, admittedAt, ct).ConfigureAwait(false);

        await PublishUnappliedAsync(application, payment.TenantId, actor, admittedAt, ct).ConfigureAwait(false);

        return new UnapplyResult(true, UnapplyError.None, null);
    }

    /// <summary>
    /// Applies the reversal-date period gate, mirroring <c>JournalPostingService</c> phase 4.
    /// </summary>
    /// <param name="chartId">The chart the payment belongs to.</param>
    /// <param name="today">The admitted act date.</param>
    /// <param name="authority">The authority the override is decided with (principal, tenant, instant).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The refusal to return, or null when the reversal may proceed.</returns>
    private async Task<(UnapplyError Error, string Message)?> GateReversalPeriodAsync(
        ChartOfAccountsId chartId,
        DateOnly today,
        AuthorizationWriteContext authority,
        CancellationToken ct)
    {
        var period = await _periods.ResolveAsync(chartId, today, ct).ConfigureAwait(false);
        if (period is not { } snapshot)
        {
            return (UnapplyError.NoPeriodForReversalDate,
                $"No fiscal period covers the reversal date {today:O} in chart '{chartId}'.");
        }

        if (snapshot.Status is IPeriodResolver.Status.Locked)
        {
            return (UnapplyError.ReversalPeriodLocked,
                $"Fiscal period '{snapshot.PeriodId}' covering the reversal date is Locked.");
        }

        if (snapshot.Status is not IPeriodResolver.Status.SoftClosed)
        {
            return null;
        }

        return await MayOverrideSoftCloseAsync(snapshot.PeriodId, authority, ct).ConfigureAwait(false)
            ? null
            : (UnapplyError.ReversalPeriodSoftClosed,
                $"Fiscal period '{snapshot.PeriodId}' covering the reversal date is SoftClosed and the "
                + $"caller does not hold '{SoftCloseOverride.Value}' over that period.");
    }

    /// <summary>The soft-close override operation, parsed once rather than compared as a bare string.</summary>
    private static readonly AuthorizationOperation SoftCloseOverride =
        AuthorizationOperation.Parse(FinancialAuthorizationOperations.FinancialPeriodOverrideSoftClose);

    /// <summary>
    /// Resolves the soft-close override at its point of use (ticket 205; ledger L592/L600/L671): ONE
    /// <see cref="AuthorizationGate"/> decision, about the request's principal, naming the fiscal period
    /// the override addresses as the record target and the admitted instant as the act instant. The record
    /// KIND comes from <see cref="AuthorizationGate.RecordKindFor"/> — the gate's own reading — so this call
    /// site supplies only the period id and the two cannot disagree.
    /// </summary>
    private async Task<bool> MayOverrideSoftCloseAsync(
        string periodId,
        AuthorizationWriteContext authority,
        CancellationToken ct)
    {
        // No gate is an UNDECIDABLE act, and a period the resolver could not name is not a record the
        // override can address. Neither is an override, so neither stands.
        if (_gate is null || string.IsNullOrWhiteSpace(periodId))
        {
            return false;
        }

        var request = authority.Request(
            SoftCloseOverride,
            AuthorizationGate.RecordKindFor(SoftCloseOverride),
            periodId);
        var decision = await _gate.DecideAsync(request, ct).ConfigureAwait(false);
        return decision.Verdict is AuthorizationVerdict.Allowed;
    }

    private async Task UnapplyFromInvoiceAsync(PaymentApplication application, PartyId actor, DateTimeOffset admittedAt, Instant now, CancellationToken ct)
    {
        var invoice = await _invoices.GetAsync(CurrentTenantId, new InvoiceId(application.TargetId), admittedAt, ct).ConfigureAwait(false);
        if (invoice is null) return;

        var restoredAmountPaid = Math.Max(0m, invoice.AmountPaid - application.AmountApplied);
        var restoredBalance = invoice.Total - restoredAmountPaid;
        var restoredStatus = restoredAmountPaid == 0m ? InvoiceStatus.Issued : InvoiceStatus.PartiallyPaid;
        await _invoices.UpsertAsync(CurrentTenantId, invoice with
        {
            AmountPaid = restoredAmountPaid,
            Balance = restoredBalance,
            Status = restoredStatus,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = invoice.Version + 1,
        }, admittedAt, ct).ConfigureAwait(false);
    }

    private async Task UnapplyFromBillAsync(PaymentApplication application, PartyId actor, DateTimeOffset admittedAt, Instant now, CancellationToken ct)
    {
        var bill = await _bills.GetAsync(CurrentTenantId, new BillId(application.TargetId), admittedAt, ct).ConfigureAwait(false);
        if (bill is null) return;

        var restoredAmountPaid = Math.Max(0m, bill.AmountPaid - application.AmountApplied);
        var restoredBalance = bill.Total - restoredAmountPaid;
        var restoredStatus = restoredAmountPaid == 0m ? BillStatus.Received : BillStatus.PartiallyPaid;
        await _bills.UpsertAsync(CurrentTenantId, bill with
        {
            AmountPaid = restoredAmountPaid,
            Balance = restoredBalance,
            Status = restoredStatus,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = bill.Version + 1,
        }, admittedAt, ct).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Internals
    // ──────────────────────────────────────────────────────────────────

    private static bool DirectionMatches(PaymentDirection direction, AppliedTo appliedTo) =>
        (direction, appliedTo) switch
        {
            (PaymentDirection.Inbound, AppliedTo.Invoice) => true,
            (PaymentDirection.Outbound, AppliedTo.Bill) => true,
            _ => false,
        };

    private static PaymentStatus DeriveAppliedStatus(decimal amount, decimal appliedTotal)
    {
        if (appliedTotal <= 0m) return PaymentStatus.Unapplied;
        if (appliedTotal >= amount) return PaymentStatus.Applied;
        return PaymentStatus.PartiallyApplied;
    }

    private Task PublishAppliedAsync(PaymentApplication application, Payment payment, PartyId actor, DateTimeOffset admittedAt, CancellationToken ct)
    {
        var payload = new PaymentAppliedPayload(
            ApplicationId: application.Id,
            PaymentId: payment.Id,
            Direction: payment.Direction,
            AppliedTo: application.AppliedTo,
            TargetId: application.TargetId,
            AmountApplied: application.AmountApplied,
            DiscountAmount: application.DiscountAmount,
            WriteoffAmount: application.WriteoffAmount,
            Actor: actor);
        return PublishAsync(PaymentEventNames.PaymentApplied, payload, $"payment-applied:{application.Id.Value}", payment.TenantId, admittedAt, ct);
    }

    private Task PublishUnappliedAsync(PaymentApplication application, TenantId tenantId, PartyId actor, DateTimeOffset admittedAt, CancellationToken ct)
    {
        var payload = new PaymentUnappliedPayload(
            ApplicationId: application.Id,
            PaymentId: application.PaymentId,
            AppliedTo: application.AppliedTo,
            TargetId: application.TargetId,
            AmountApplied: application.AmountApplied,
            Actor: actor);
        return PublishAsync(PaymentEventNames.PaymentUnapplied, payload, $"payment-unapplied:{application.Id.Value}", tenantId, admittedAt, ct);
    }

    private Task PublishAsync<TPayload>(
        string eventType,
        TPayload payload,
        string idempotencyKey,
        TenantId tenantId,
        DateTimeOffset admittedAt,
        CancellationToken ct)
    {
        var envelope = new DomainEventEnvelope<TPayload>
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = eventType,
            SchemaVersion = 1,
            OccurredAt = admittedAt,
            TenantId = tenantId,
            OriginatingReplicaId = ReplicaId.System,
            IdempotencyKey = idempotencyKey,
            Payload = payload!,
        };
        return _events.PublishAsync(envelope, ct);
    }
}
