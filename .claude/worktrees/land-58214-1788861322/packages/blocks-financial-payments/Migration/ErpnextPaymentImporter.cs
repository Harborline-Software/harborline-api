using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using AuthorizationWriteContext = Harborline.Api.Foundation.Authorization.AuthorizationWriteContext;
using Harborline.Api.Foundation.Import.Outcomes;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.FinancialPayments.Migration;

/// <summary>
/// Default <see cref="IErpnextPaymentImporter"/>. Maps an ERPNext
/// <c>Payment Entry</c> onto a canonical <see cref="Payment"/>, posts the
/// source clearing evidence through <see cref="IJournalPostingService"/>, and
/// upserts it via the tenant-scoped <see cref="IPaymentRepository"/>. Idempotent on
/// <c>ExternalRef == "erpnext:pe:{name}"</c> with a <see cref="Payment.ExternalRefVersion"/>
/// version gate (ADR 0100 C1 / C7). Returns the canonical
/// <c>Harborline.Api.Foundation.Import</c> <see cref="ImportOutcome{T}"/> DU
/// (ADR 0100 C2 / OQ-A): every reject is a structured <see cref="ImportFailure"/>
/// arm rather than a thrown exception or a record-less skip.
/// </summary>
public sealed class ErpnextPaymentImporter : IErpnextPaymentImporter
{
    /// <summary>The ERPNext DocType this importer consumes — for census + reject provenance.</summary>
    public const string DocType = "Payment Entry";

    /// <summary>External-ref namespace prefix; mirrors the AP <c>erpnext:pinv:</c> convention.</summary>
    public const string ExternalRefPrefix = "erpnext:pe:";

    private readonly IPaymentRepository _payments;
    private readonly IAccountResolver _accounts;
    private readonly IJournalPostingService _journals;
    private readonly ITenantContext _tenantContext;

    public ErpnextPaymentImporter(
        IPaymentRepository payments,
        IAccountResolver accounts,
        IJournalPostingService journals,
        ITenantContext tenantContext,
        TimeProvider? timeProvider = null)
    {
        _payments = payments ?? throw new ArgumentNullException(nameof(payments));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _journals = journals ?? throw new ArgumentNullException(nameof(journals));
        _tenantContext = tenantContext ?? throw new ArgumentNullException(nameof(tenantContext));
        _ = timeProvider;
    }

    /// <inheritdoc />
    public async Task<ImportOutcome<Payment>> UpsertPaymentAsync(
        TenantId tenantId,
        ErpnextPaymentSource source,
        ChartOfAccountsId chartId,
        PartyId partyPartyId,
        AuthorizationWriteContext writeAuthority,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (writeAuthority.Tenant != tenantId)
            throw new ArgumentException("The ERPNext payment tenant does not match the run authority.", nameof(writeAuthority));

        var ambientTenant = _tenantContext.Tenant?.Id
            ?? throw new InvalidOperationException(
                "ErpnextPaymentImporter requires the ERPNext run's resolved tenant context.");
        if (!ambientTenant.Equals(tenantId))
        {
            throw new InvalidOperationException(
                "ErpnextPaymentImporter tenant argument does not match the ERPNext run tenant context.");
        }

        var externalRef = ExternalRefPrefix + source.Name;

        // ── Validation gates (reject, never throw — ADR 0100 C2/C5) ──

        if (!TryMapDirection(source.PaymentType, out var direction))
        {
            return Reject(
                source.Name,
                ImportRejectReason.InvalidFieldValue,
                fieldName: "payment_type",
                ruleViolated: "payment_type must be 'Receive' (Inbound) or 'Pay' (Outbound)");
        }

        // Non-USD currency is deferred to v2 (ADR 0100 §10.2). A null/blank
        // currency is treated as the USD default (matches Payment.Create).
        var currency = string.IsNullOrWhiteSpace(source.Currency) ? "USD" : source.Currency.Trim();
        if (!string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(
                source.Name,
                ImportRejectReason.UnsupportedCurrency,
                fieldName: "currency",
                ruleViolated: "multi-currency import is deferred to v2; only USD is supported");
        }

        if (source.PaidAmount <= 0m)
        {
            return Reject(
                source.Name,
                ImportRejectReason.InvalidFieldValue,
                fieldName: "paid_amount",
                ruleViolated: "paid_amount must be greater than zero");
        }

        if (source.UnallocatedAmount < 0m || source.UnallocatedAmount > source.PaidAmount)
        {
            return Reject(
                source.Name,
                ImportRejectReason.ConstraintViolation,
                fieldName: "unallocated_amount",
                ruleViolated: "unallocated_amount must be within [0, paid_amount]");
        }

        if (source.DocStatus != 1)
        {
            return Reject(
                source.Name,
                ImportRejectReason.InvalidFieldValue,
                fieldName: "docstatus",
                ruleViolated: "only submitted ERPNext Payment Entries are source-cleared");
        }

        if (string.IsNullOrWhiteSpace(source.PaidFromAccount) ||
            string.IsNullOrWhiteSpace(source.PaidToAccount))
        {
            return Reject(
                source.Name,
                ImportRejectReason.UnresolvedReference,
                fieldName: "paid_from/paid_to",
                ruleViolated: "source-cleared payment must identify both clearing accounts");
        }

        // ── Idempotency / version gate (ADR 0100 C1/C7) ──

        var existing = await _payments
            .GetByExternalRefAsync(tenantId, chartId, externalRef, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            var versionComparison = string.CompareOrdinal(
                source.Modified,
                existing.ExternalRefVersion ?? string.Empty);

            // Same or older source version normally means no write. A record left
            // by the pre-PPI importer is repaired in place instead of remaining a
            // permanently invisible open credit.
            if (versionComparison <= 0 && existing.JournalEntryId is not null)
            {
                return new ImportOutcome<Payment>.Skipped(
                    existing,
                    "already imported at the same or a newer source version");
            }

            if (versionComparison <= 0)
            {
                var repair = await StampClearingJournalAsync(existing, source, writeAuthority, cancellationToken)
                    .ConfigureAwait(false);
                if (repair.Failure is not null)
                {
                    return new ImportOutcome<Payment>.Rejected(repair.Failure);
                }

                var repaired = repair.Payment! with
                {
                    UpdatedAtUtc = new Instant(writeAuthority.At),
                    Version = existing.Version + 1,
                };
                await _payments.UpdateAsync(tenantId, repaired, writeAuthority.At, cancellationToken).ConfigureAwait(false);
                return new ImportOutcome<Payment>.Updated(repaired);
            }

            // A posted ERPNext document is immutable in its accounting dimensions.
            // A newer timestamp may carry metadata edits, but changing the monetary
            // facts under an existing JE would make Payment and GL evidence diverge.
            if (existing.JournalEntryId is not null &&
                (existing.Direction != direction ||
                 existing.PartyId != partyPartyId ||
                 existing.PaymentDate != source.PostingDate ||
                 existing.Amount != source.PaidAmount ||
                 existing.UnappliedAmount != source.UnallocatedAmount ||
                 !string.Equals(existing.Currency, currency, StringComparison.Ordinal)))
            {
                return Reject(
                    source.Name,
                    ImportRejectReason.ConstraintViolation,
                    fieldName: "modified",
                    ruleViolated: "posted payment financial fields are immutable; import an amendment as a distinct entry");
            }

            // Strictly newer source version updates mutable metadata in place,
            // or fully populates a legacy uncleared row before repairing it.
            var updated = existing with
            {
                Direction = existing.JournalEntryId is null ? direction : existing.Direction,
                PartyId = existing.JournalEntryId is null ? partyPartyId : existing.PartyId,
                PaymentDate = existing.JournalEntryId is null ? source.PostingDate : existing.PaymentDate,
                Amount = existing.JournalEntryId is null ? source.PaidAmount : existing.Amount,
                UnappliedAmount = existing.JournalEntryId is null
                    ? source.UnallocatedAmount
                    : existing.UnappliedAmount,
                Method = MapMethod(source.ModeOfPayment),
                Currency = currency,
                Reference = source.ReferenceNo,
                ExternalRefVersion = source.Modified,
                UpdatedAtUtc = new Instant(writeAuthority.At),
                Version = existing.Version + 1,
            };

            if (updated.JournalEntryId is null)
            {
                var clearing = await StampClearingJournalAsync(updated, source, writeAuthority, cancellationToken)
                    .ConfigureAwait(false);
                if (clearing.Failure is not null)
                {
                    return new ImportOutcome<Payment>.Rejected(clearing.Failure);
                }

                updated = clearing.Payment!;
            }
            await _payments.UpdateAsync(tenantId, updated, writeAuthority.At, cancellationToken).ConfigureAwait(false);
            return new ImportOutcome<Payment>.Updated(updated);
        }

        // ── Insert path ──

        var payment = Payment.Create(
            tenantId: tenantId,
            chartId: chartId,
            direction: direction,
            paymentNumber: source.Name,
            partyId: partyPartyId,
            paymentDate: source.PostingDate,
            amount: source.PaidAmount,
            method: MapMethod(source.ModeOfPayment),
            createdAtUtc: new Instant(writeAuthority.At),
            currency: currency,
            reference: source.ReferenceNo,
            externalRef: externalRef,
            externalRefVersion: source.Modified) with
        {
            UnappliedAmount = source.UnallocatedAmount,
            CreatedAtUtc = new Instant(writeAuthority.At),
            UpdatedAtUtc = new Instant(writeAuthority.At),
        };

        var stamped = await StampClearingJournalAsync(payment, source, writeAuthority, cancellationToken)
            .ConfigureAwait(false);
        if (stamped.Failure is not null)
        {
            return new ImportOutcome<Payment>.Rejected(stamped.Failure);
        }

        await _payments.AddAsync(tenantId, stamped.Payment!, writeAuthority.At, cancellationToken).ConfigureAwait(false);
        return new ImportOutcome<Payment>.Inserted(stamped.Payment!);
    }

    private async Task<(Payment? Payment, ImportFailure? Failure)> StampClearingJournalAsync(
        Payment payment,
        ErpnextPaymentSource source,
        AuthorizationWriteContext authority,
        CancellationToken cancellationToken)
    {
        // includeInactive: true — a multi-year ERPNext book routinely carries `disabled`
        // accounts that historical Payment Entries still reference. Excluding them would
        // fail the `paidFrom is null || paidTo is null` gate below and send the ENTIRE
        // payment to the reject bin, not just its clearing entry. A disabled account is
        // still a valid HISTORICAL posting target; `JournalPostingService` phase 3 keeps
        // the real gate (`GLAccount.IsPostable`), which is independent of `IsActive`.
        var chartAccounts = await _accounts
            .EnumerateForChartAsync(payment.ChartId, includeInactive: true, cancellationToken)
            .ConfigureAwait(false);

        // Single pass over the chart rather than two linear scans (this runs once per
        // payment, twice on the repair path).
        GLAccount? paidFrom = null;
        GLAccount? paidTo = null;
        foreach (var account in chartAccounts)
        {
            if (paidFrom is null &&
                string.Equals(account.ExternalRef, source.PaidFromAccount, StringComparison.Ordinal))
            {
                paidFrom = account;
            }

            if (paidTo is null &&
                string.Equals(account.ExternalRef, source.PaidToAccount, StringComparison.Ordinal))
            {
                paidTo = account;
            }

            if (paidFrom is not null && paidTo is not null)
            {
                break;
            }
        }

        if (paidFrom is null || paidTo is null)
        {
            return (null, ImportFailure.Of(
                externalRef: source.Name,
                docType: DocType,
                reason: ImportRejectReason.UnresolvedReference,
                fieldName: paidFrom is null ? "paid_from" : "paid_to",
                ruleViolated: "clearing account was not imported into the target chart"));
        }

        if (paidFrom.Id == paidTo.Id)
        {
            return (null, ImportFailure.Of(
                externalRef: source.Name,
                docType: DocType,
                reason: ImportRejectReason.ConstraintViolation,
                fieldName: "paid_from/paid_to",
                ruleViolated: "clearing entry must move value between distinct accounts"));
        }

        // Idempotency key — DELIBERATELY a distinct namespace from PPI-1's
        // `payment-clear:{id}` (DefaultPaymentPostingService.ClearAsync). The two do NOT
        // dedupe against each other, and that is correct here:
        //
        //  * `JournalPostingService` phase 1.5 dedupes on (TenantId, SourceReference), so the
        //    key must survive a crash. `Payment.Id` does NOT: it is minted by this importer,
        //    so a run that posts the JE and then fails before `_payments.AddAsync` would mint a
        //    FRESH id on re-run and double-post. The ERPNext document name is the only
        //    import-time-stable key, which is what makes the repair path safe to re-drive.
        //  * The reverse collision cannot happen either — `ClearAsync` no-ops on a payment that
        //    is non-Draft with a non-null `JournalEntryId`, which every record this importer
        //    writes already satisfies.
        //  * The `erpnext:` prefix keeps migration-origin clearing entries greppable, matching
        //    the `erpnext:pe:` / `erpnext:pinv:` external-ref convention.
        //
        // Re-homing this under the broker-PEP's `AccountingPostingRequest` is tracked as
        // tech-debt (see the PR body) — it is not a silent divergence.
        var sourceReference = $"erpnext:payment-clear:{source.Name}";
        var entry = new JournalEntry(
            id: JournalEntryId.NewId(),
            tenantId: payment.TenantId,
            entryDate: payment.PaymentDate,
            memo: $"Import cleared ERPNext payment {source.Name}",
            lines:
            [
                new JournalEntryLine(paidTo.Id, debit: payment.Amount, credit: 0m),
                new JournalEntryLine(paidFrom.Id, debit: 0m, credit: payment.Amount),
            ],
            createdAtUtc: new Instant(authority.At),
            sourceReference: sourceReference) with
        {
            ChartId = payment.ChartId,
            SourceKind = JournalEntrySource.Migration,
            ExternalRef = sourceReference,
        };

        var result = await _journals.PostAsync(
            entry,
            authority,
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Entry is null)
        {
            // REJECT, not skip-with-report. A payment whose clearing journal was refused has no
            // GL evidence, and PPI-1 is precisely that an uncleared payment must not enter the
            // books (`DefaultPaymentApplicationService` rejects applying one). Importing the
            // record anyway would create the permanently-invisible open credit this PR exists to
            // remove. The reject bin is the operator-visible surface: a structured
            // `ImportFailure` naming the field and the PostError, never a silent drop.
            return (null, ImportFailure.Of(
                externalRef: source.Name,
                docType: DocType,
                reason: ImportRejectReason.ConstraintViolation,
                fieldName: "clearing_journal",
                ruleViolated: $"clearing journal rejected with {result.Error}"));
        }

        var status = payment.UnappliedAmount switch
        {
            <= 0m => PaymentStatus.Applied,
            var amount when amount >= payment.Amount => PaymentStatus.Unapplied,
            _ => PaymentStatus.PartiallyApplied,
        };
        var bankAccountId = payment.Direction == PaymentDirection.Inbound ? paidTo.Id : paidFrom.Id;

        return (payment with
        {
            BankAccountId = bankAccountId,
            JournalEntryId = result.Entry.Id,
            Status = status,
            UpdatedAtUtc = new Instant(authority.At),
        }, null);
    }

    /// <summary>ERPNext <c>payment_type</c> → <see cref="PaymentDirection"/>.</summary>
    private static bool TryMapDirection(string paymentType, out PaymentDirection direction)
    {
        switch (paymentType)
        {
            case "Receive":
                direction = PaymentDirection.Inbound;
                return true;
            case "Pay":
                direction = PaymentDirection.Outbound;
                return true;
            default:
                direction = default;
                return false;
        }
    }

    /// <summary>
    /// ERPNext <c>mode_of_payment</c> → <see cref="PaymentMethod"/>. ERPNext's
    /// mode-of-payment is a free-form master, so unknown values map to
    /// <see cref="PaymentMethod.Other"/> rather than rejecting the record (the
    /// payment is still valid cash movement — only the method classification is
    /// unknown).
    /// </summary>
    private static PaymentMethod MapMethod(string? modeOfPayment) =>
        (modeOfPayment?.Trim().ToLowerInvariant()) switch
        {
            "cash"           => PaymentMethod.Cash,
            "cheque"         => PaymentMethod.Check,
            "check"          => PaymentMethod.Check,
            "ach"            => PaymentMethod.ACH,
            "bank draft"     => PaymentMethod.ACH,
            "wire transfer"  => PaymentMethod.Wire,
            "wire"           => PaymentMethod.Wire,
            "credit card"    => PaymentMethod.Card,
            "debit card"     => PaymentMethod.Card,
            "card"           => PaymentMethod.Card,
            _                => PaymentMethod.Other,
        };

    /// <summary>
    /// Build a <see cref="ImportOutcome{T}.Rejected"/> from a canonical reason.
    /// Only allowlisted, safe identifiers cross the boundary (ADR 0100 C9):
    /// the opaque ERPNext name + field NAME + a rule descriptor — never a
    /// monetary amount, a party name/PII, or the raw source payload.
    /// </summary>
    private static ImportOutcome<Payment>.Rejected Reject(
        string sourceName,
        ImportRejectReason reason,
        string? fieldName = null,
        string? ruleViolated = null) =>
        new(ImportFailure.Of(
            externalRef: sourceName,
            docType: DocType,
            reason: reason,
            fieldName: fieldName,
            ruleViolated: ruleViolated));
}
