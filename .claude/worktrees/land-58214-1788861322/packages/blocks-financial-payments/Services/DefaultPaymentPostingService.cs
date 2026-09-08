using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using AuthorizationWriteContext = Harborline.Api.Foundation.Authorization.AuthorizationWriteContext;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.FinancialPayments.Services;

/// <summary>
/// Default <see cref="IPaymentPostingService"/>. Coordinates the payment
/// repository, the application repository, the AR / AP repositories (for
/// bounce-path balance restoration), the chart's account resolver, and the
/// ledger posting service — mirrors AR's <c>InvoicePostingService</c>
/// and AP's <c>BillPostingService</c> shape.
///
/// <para>
/// <b>Control-account resolution:</b> per the Stage 02 spec, the clearing
/// journal entry posts against the chart's default AR control account
/// (Inbound) or default AP control account (Outbound). PR 2 resolves this by
/// enumerating the chart's accounts via <see cref="IAccountResolver"/> and
/// picking the first active postable account whose
/// <see cref="GLAccount.Subtype"/> matches
/// <see cref="AccountSubtype.AccountsReceivable"/> /
/// <see cref="AccountSubtype.AccountsPayable"/>. Charts with multiple AR or
/// AP control accounts (a Phase 2 multi-tenancy refinement) will need an
/// explicit selection mechanism — track via follow-on hand-off.
/// </para>
/// </summary>
public sealed class DefaultPaymentPostingService : IPaymentPostingService
{
    private readonly IPaymentRepository _payments;
    private readonly IPaymentApplicationRepository _applications;
    private readonly IInvoiceRepository _invoices;
    private readonly IBillRepository _bills;
    private readonly IAccountResolver _accountResolver;
    private readonly IJournalPostingService _journals;
    private readonly ITenantContext _tenantContext;
    private readonly TimeProvider _time;

    public DefaultPaymentPostingService(
        ITenantContext tenantContext,
        IPaymentRepository payments,
        IPaymentApplicationRepository applications,
        IInvoiceRepository invoices,
        IBillRepository bills,
        IAccountResolver accountResolver,
        IJournalPostingService journals,
        TimeProvider? timeProvider = null)
    {
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _payments = payments ?? throw new ArgumentNullException(nameof(payments));
        _applications = applications ?? throw new ArgumentNullException(nameof(applications));
        _invoices = invoices ?? throw new ArgumentNullException(nameof(invoices));
        _bills = bills ?? throw new ArgumentNullException(nameof(bills));
        _accountResolver = accountResolver ?? throw new ArgumentNullException(nameof(accountResolver));
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    private TenantId CurrentTenantId =>
        _tenantContext.Tenant?.Id
            ?? throw new InvalidOperationException("DefaultPaymentPostingService requires a resolved tenant on the ambient ITenantContext.");

    // ──────────────────────────────────────────────────────────────────
    //  ClearAsync
    // ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ClearResult> ClearAsync(PaymentId id, AuthorizationWriteContext authority, CancellationToken ct = default)
    {
        RequireTenant(authority);
        var actor = new PartyId(authority.Principal.Value);
        var now = new Instant(authority.At);
        var payment = await _payments.GetAsync(CurrentTenantId, id, authority.At, ct).ConfigureAwait(false);
        if (payment is null)
            return new ClearResult(null, null, ClearError.UnknownPayment, $"Payment '{id.Value}' not found.");

        // Idempotent: any non-Draft status that already has a clearing JE returns success.
        if (payment.Status != PaymentStatus.Draft && payment.JournalEntryId is { } existingEntry)
            return new ClearResult(payment, existingEntry, ClearError.None, "Already cleared; no-op.");

        if (payment.Status != PaymentStatus.Draft)
            return new ClearResult(payment, null, ClearError.InvalidStatusForClear,
                $"Cannot clear payment in status '{payment.Status}' — only Draft is clearable.");

        if (payment.BankAccountId is not { } bankAccountId)
            return new ClearResult(payment, null, ClearError.JournalRejected,
                "Payment has no BankAccountId; cannot post clearing journal entry.");

        var controlAccount = await ResolveControlAccountAsync(payment.ChartId, payment.Direction, ct).ConfigureAwait(false);
        if (controlAccount is null)
            return new ClearResult(payment, null, ClearError.JournalRejected,
                $"Chart '{payment.ChartId.Value}' has no active {(payment.Direction == PaymentDirection.Inbound ? "AccountsReceivable" : "AccountsPayable")} control account configured.");

        var jeLines = BuildClearLines(payment.Direction, bankAccountId, controlAccount.Id, payment.Amount);
        var entry = new JournalEntry(
            id: JournalEntryId.NewId(),
            tenantId: CurrentTenantId,
            entryDate: payment.PaymentDate,
            memo: $"Clear payment {payment.PaymentNumber}",
            lines: jeLines,
            createdAtUtc: now,
            sourceReference: $"payment-clear:{payment.Id.Value}");

        var postResult = await _journals.PostAsync(
            entry,
            authority,
            ct).ConfigureAwait(false);
        if (!postResult.IsSuccess)
            return new ClearResult(payment, null, ClearError.JournalRejected, postResult.Detail);

        // Status after clearing depends on how much has been applied. In the
        // standard flow Applications is empty at clear time so we land on
        // Unapplied; the PartiallyApplied / Applied branches are defensive
        // for callers that pre-stage applications before clearing.
        var appliedTotal = payment.Applications.Sum(a => a.AmountApplied);
        var newStatus = DeriveClearedStatus(payment.Amount, appliedTotal);

        var cleared = payment with
        {
            Status = newStatus,
            JournalEntryId = entry.Id,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = payment.Version + 1,
        };
        await _payments.UpdateAsync(CurrentTenantId, cleared, authority.At, ct).ConfigureAwait(false);

        return new ClearResult(cleared, entry.Id, ClearError.None, null);
    }

    // ──────────────────────────────────────────────────────────────────
    //  BounceAsync
    // ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<BounceResult> BounceAsync(PaymentId id, string reason, AuthorizationWriteContext authority, CancellationToken ct = default)
    {
        RequireTenant(authority);
        var actor = new PartyId(authority.Principal.Value);
        var now = new Instant(authority.At);
        var payment = await _payments.GetAsync(CurrentTenantId, id, authority.At, ct).ConfigureAwait(false);
        if (payment is null)
            return new BounceResult(null, null, BounceError.UnknownPayment, $"Payment '{id.Value}' not found.");

        if (payment.Status is not (PaymentStatus.Unapplied or PaymentStatus.PartiallyApplied or PaymentStatus.Applied))
            return new BounceResult(payment, null, BounceError.InvalidStatusForBounce,
                $"Cannot bounce payment in status '{payment.Status}'. Only Unapplied / PartiallyApplied / Applied are bounceable.");

        if (payment.JournalEntryId is null)
            return new BounceResult(payment, null, BounceError.InvalidStatusForBounce,
                "Payment has no clearing journal entry to reverse.");

        if (payment.BankAccountId is not { } bankAccountId)
            return new BounceResult(payment, null, BounceError.JournalRejected,
                "Payment has no BankAccountId; cannot post reversal journal entry.");

        var controlAccount = await ResolveControlAccountAsync(payment.ChartId, payment.Direction, ct).ConfigureAwait(false);
        if (controlAccount is null)
            return new BounceResult(payment, null, BounceError.JournalRejected,
                $"Chart '{payment.ChartId.Value}' has no active {(payment.Direction == PaymentDirection.Inbound ? "AccountsReceivable" : "AccountsPayable")} control account configured.");

        var reversalLines = BuildReversalLines(payment.Direction, bankAccountId, controlAccount.Id, payment.Amount);
        var reversal = new JournalEntry(
            id: JournalEntryId.NewId(),
            tenantId: CurrentTenantId,
            entryDate: DateOnly.FromDateTime(authority.At.UtcDateTime),
            memo: $"Bounce payment {payment.PaymentNumber}: {reason}",
            lines: reversalLines,
            createdAtUtc: now,
            sourceReference: $"payment-bounce:{payment.Id.Value}");

        var postResult = await _journals.PostAsync(
            reversal,
            authority,
            ct).ConfigureAwait(false);
        if (!postResult.IsSuccess)
            return new BounceResult(payment, null, BounceError.JournalRejected, postResult.Detail);

        // For each active prior application: restore the Invoice/Bill balance and append immutable
        // reversal evidence. Reversed originals and contra rows remain queryable but are never applied
        // to the derived position again.
        // This is the only place in the cluster where the posting service touches Invoice/Bill
        // repositories directly — intentional per the Stage 02 spec so the bounce is atomic from
        // the caller's perspective.
        var priorApplications = await _applications.ListByPaymentAsync(CurrentTenantId, payment.Id, ct).ConfigureAwait(false);
        var reversedAtUtc = now;
        foreach (var application in priorApplications.Where(a => a.IsActive))
        {
            // Reverse FIRST, then restore. The old order restored the balance and then discarded
            // ReverseAsync's nullable result (finding api-financial-10), so a reversal that did not
            // happen left a restored balance with no contra evidence — and here it did so inside a
            // loop, once per application. Evidence-before-mutation makes that unreachable.
            var reversalResult = await _applications.ReverseAsync(CurrentTenantId, application.Id, reversedAtUtc, ct)
                .ConfigureAwait(false);
            if (reversalResult.Outcome == PaymentApplicationReversalOutcome.AlreadyReversed)
            {
                // Ticket 095: benign. Another caller already wrote the contra row and restored this
                // target's balance; restoring again here would double it. Skip the restore and keep
                // bouncing rather than refusing a payment whose evidence already exists.
                continue;
            }
            if (reversalResult.Outcome == PaymentApplicationReversalOutcome.NotFound)
            {
                // KNOWN LIMITATION: no transaction spans the journal store, the application store
                // and the AR/AP stores, so the entry posted above and any earlier applications in
                // this batch stay reversed. Refusing here keeps the invariant that matters —
                // no balance is ever restored without contra evidence — and leaves the payment
                // un-bounced so the partial state is visible rather than papered over.
                return new BounceResult(payment, reversal.Id, BounceError.ReversalNotRecorded,
                    $"PaymentApplication '{application.Id.Value}' could not be reversed; its target balance was left untouched and payment '{payment.Id.Value}' was NOT marked Bounced. Reversal entry '{reversal.Id.Value}' is already posted.");
            }

            switch (application.AppliedTo)
            {
                case AppliedTo.Invoice:
                    await RestoreInvoiceBalanceAsync(application, actor, now, ct).ConfigureAwait(false);
                    break;
                case AppliedTo.Bill:
                    await RestoreBillBalanceAsync(application, actor, now, ct).ConfigureAwait(false);
                    break;
            }
        }

        var bounced = payment with
        {
            Status = PaymentStatus.Bounced,
            BouncedByEntryId = reversal.Id,
            UnappliedAmount = payment.Amount,
            Applications = [],
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = payment.Version + 1,
        };
        await _payments.UpdateAsync(CurrentTenantId, bounced, authority.At, ct).ConfigureAwait(false);

        return new BounceResult(bounced, reversal.Id, BounceError.None, null);
    }

    // ──────────────────────────────────────────────────────────────────
    //  VoidAsync
    // ──────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<VoidResult> VoidAsync(PaymentId id, string reason, AuthorizationWriteContext authority, CancellationToken ct = default)
    {
        RequireTenant(authority);
        var actor = new PartyId(authority.Principal.Value);
        var now = new Instant(authority.At);
        var payment = await _payments.GetAsync(CurrentTenantId, id, authority.At, ct).ConfigureAwait(false);
        if (payment is null)
            return new VoidResult(null, VoidError.UnknownPayment, $"Payment '{id.Value}' not found.");

        if (payment.Status != PaymentStatus.Draft)
            return new VoidResult(payment, VoidError.InvalidStatusForVoid,
                $"Cannot void payment in status '{payment.Status}' — only Draft is voidable.");

        var voided = payment with
        {
            Status = PaymentStatus.Voided,
            Notes = string.IsNullOrWhiteSpace(reason) ? payment.Notes : $"Voided: {reason}",
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = payment.Version + 1,
        };
        await _payments.UpdateAsync(CurrentTenantId, voided, authority.At, ct).ConfigureAwait(false);

        return new VoidResult(voided, VoidError.None, null);
    }

    private void RequireTenant(AuthorizationWriteContext authority)
    {
        if (authority.Tenant != CurrentTenantId)
            throw new ArgumentException("The payment tenant does not match the boundary authority.", nameof(authority));
    }

    // ──────────────────────────────────────────────────────────────────
    //  Internals — JE construction
    // ──────────────────────────────────────────────────────────────────

    private static IReadOnlyList<JournalEntryLine> BuildClearLines(
        PaymentDirection direction,
        GLAccountId bankAccountId,
        GLAccountId controlAccountId,
        decimal amount) => direction switch
        {
            // Inbound: customer pays us → Dr Bank / Cr AR control.
            PaymentDirection.Inbound =>
            [
                new JournalEntryLine(bankAccountId, debit: amount, credit: 0m),
                new JournalEntryLine(controlAccountId, debit: 0m, credit: amount),
            ],
            // Outbound: we pay vendor → Dr AP control / Cr Bank.
            PaymentDirection.Outbound =>
            [
                new JournalEntryLine(controlAccountId, debit: amount, credit: 0m),
                new JournalEntryLine(bankAccountId, debit: 0m, credit: amount),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown payment direction."),
        };

    private static IReadOnlyList<JournalEntryLine> BuildReversalLines(
        PaymentDirection direction,
        GLAccountId bankAccountId,
        GLAccountId controlAccountId,
        decimal amount) => direction switch
        {
            // Reversal of Inbound clearing: Dr AR control / Cr Bank.
            PaymentDirection.Inbound =>
            [
                new JournalEntryLine(controlAccountId, debit: amount, credit: 0m),
                new JournalEntryLine(bankAccountId, debit: 0m, credit: amount),
            ],
            // Reversal of Outbound clearing: Dr Bank / Cr AP control.
            PaymentDirection.Outbound =>
            [
                new JournalEntryLine(bankAccountId, debit: amount, credit: 0m),
                new JournalEntryLine(controlAccountId, debit: 0m, credit: amount),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown payment direction."),
        };

    // ──────────────────────────────────────────────────────────────────
    //  Internals — account resolution
    // ──────────────────────────────────────────────────────────────────

    private async Task<GLAccount?> ResolveControlAccountAsync(
        ChartOfAccountsId chartId,
        PaymentDirection direction,
        CancellationToken ct)
    {
        var subtype = direction switch
        {
            PaymentDirection.Inbound => AccountSubtype.AccountsReceivable,
            PaymentDirection.Outbound => AccountSubtype.AccountsPayable,
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown payment direction."),
        };

        var accounts = await _accountResolver.EnumerateForChartAsync(chartId, includeInactive: false, ct).ConfigureAwait(false);
        return accounts.FirstOrDefault(a => a.Subtype == subtype && a.IsPostable);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Internals — Invoice / Bill balance restoration on bounce
    // ──────────────────────────────────────────────────────────────────

    private async Task RestoreInvoiceBalanceAsync(
        PaymentApplication application, PartyId actor, Instant now, CancellationToken ct)
    {
        var invoice = await _invoices.GetAsync(CurrentTenantId, new InvoiceId(application.TargetId), now.Value, ct).ConfigureAwait(false);
        if (invoice is null) return;

        var restoredAmountPaid = Math.Max(0m, invoice.AmountPaid - application.AmountApplied);
        var restoredBalance = invoice.Total - restoredAmountPaid;
        var restoredStatus = restoredAmountPaid == 0m ? InvoiceStatus.Issued : InvoiceStatus.PartiallyPaid;

        var updated = invoice with
        {
            AmountPaid = restoredAmountPaid,
            Balance = restoredBalance,
            Status = restoredStatus,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = invoice.Version + 1,
        };
        await _invoices.UpsertAsync(CurrentTenantId, updated, now.Value, ct).ConfigureAwait(false);
    }

    private async Task RestoreBillBalanceAsync(
        PaymentApplication application, PartyId actor, Instant now, CancellationToken ct)
    {
        var bill = await _bills.GetAsync(CurrentTenantId, new BillId(application.TargetId), now.Value, ct).ConfigureAwait(false);
        if (bill is null) return;

        var restoredAmountPaid = Math.Max(0m, bill.AmountPaid - application.AmountApplied);
        var restoredBalance = bill.Total - restoredAmountPaid;
        var restoredStatus = restoredAmountPaid == 0m ? BillStatus.Received : BillStatus.PartiallyPaid;

        var updated = bill with
        {
            AmountPaid = restoredAmountPaid,
            Balance = restoredBalance,
            Status = restoredStatus,
            UpdatedAtUtc = now,
            UpdatedBy = actor,
            Version = bill.Version + 1,
        };
        await _bills.UpsertAsync(CurrentTenantId, updated, now.Value, ct).ConfigureAwait(false);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Internals — status derivation
    // ──────────────────────────────────────────────────────────────────

    private static PaymentStatus DeriveClearedStatus(decimal amount, decimal appliedTotal)
    {
        if (appliedTotal <= 0m) return PaymentStatus.Unapplied;
        if (appliedTotal >= amount) return PaymentStatus.Applied;
        return PaymentStatus.PartiallyApplied;
    }
}
