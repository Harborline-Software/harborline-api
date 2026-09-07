using Harborline.Api.Blocks.FinancialAp.Models;
using Harborline.Api.Blocks.FinancialAp.Services;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialPayments.Models;
using Harborline.Api.Blocks.FinancialPayments.Services;
using Harborline.Api.Blocks.FinancialSubLedger.Models;
using Harborline.Api.Blocks.FinancialSubLedger.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

// ADR 0120 PR-E2: AgingBucket consolidated — all three packages (AR, AP, subledger) now re-export
// Harborline.Api.Blocks.FinancialLedger.Models.AgingBucket via global using alias. CS0104 ambiguity resolved.
// AgingSummary / AgingRow remain domain-specific (different shapes); subledger types used here.
using SubLedgerAgingSummary = Harborline.Api.Blocks.FinancialSubLedger.Models.AgingSummary;
using SubLedgerAgingRow     = Harborline.Api.Blocks.FinancialSubLedger.Models.AgingRow;

namespace Harborline.Api.Blocks.FinancialSubLedger.Projection.Services;

/// <summary>
/// Concrete implementation of <see cref="ISubLedgerReadModel"/> (ADR 0120 PR-B).
/// All reads are scoped by the caller-supplied <c>TenantId</c> — no read may
/// return data belonging to another tenant (MANDATORY — PR-B SPOT-CHECK requirement).
///
/// <para>
/// <b>Position derivation (C-FIN-1):</b>
/// <c>Balance = Σ(open-item.Balance) − Σ(unapplied credits)</c>.
/// Open-item <c>Balance</c> already nets <c>DiscountAmount</c> and
/// <c>WriteoffAmount</c> via <c>AmountPaid</c> — derived from
/// <see cref="Invoice.Balance"/> / <see cref="Bill.Balance"/>, NOT from
/// <c>Σ AmountApplied</c> of cash applications. Using <c>Σ AmountApplied</c>
/// would produce phantom residuals on discounted/short-paid items and break
/// the R1 reconciliation invariant.
/// </para>
///
/// <para>
/// <b>Open-item set definition (C-FIN-2):</b> only invoices/bills in an open
/// status (<see cref="InvoiceStatus.Issued"/> / <see cref="InvoiceStatus.PartiallyPaid"/>
/// and their AP equivalents) with positive <c>Balance</c> contribute. Voided and
/// written-off items have left the open-item set; the GL reversal / bad-debt JE
/// keeps the R1 reconciliation invariant balanced on both sides simultaneously.
/// </para>
///
/// <para>
/// <b>Aging scope (ADR 0120 §3 "add a scope, not a new service"):</b>
/// <c>GetAgingForSubLedgerAsync</c> is a sub-ledger-scoped view backed by this
/// same projection, not a new aging service.
/// </para>
///
/// <para>
/// <b>Tenant isolation enforcement:</b> every private helper that reads AR / AP /
/// Payment data passes the caller-supplied <c>tenantId</c> to the underlying
/// repository. The <see cref="ISubLedgerAccountRepository"/> uses composite
/// <c>(TenantId, Id)</c> keying; a caller cannot reach another tenant's account
/// by supplying a foreign <c>SubLedgerAccountId</c>.
/// </para>
/// </summary>
public sealed class SubLedgerReadModel : ISubLedgerReadModel
{
    private readonly ISubLedgerAccountRepository _accounts;
    private readonly IInvoiceRepository _invoices;
    private readonly IBillRepository _bills;
    private readonly IPaymentRepository _payments;
    private readonly IPaymentApplicationRepository _applications;

    public SubLedgerReadModel(
        ISubLedgerAccountRepository accounts,
        IInvoiceRepository invoices,
        IBillRepository bills,
        IPaymentRepository payments,
        IPaymentApplicationRepository applications)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _invoices = invoices ?? throw new ArgumentNullException(nameof(invoices));
        _bills = bills ?? throw new ArgumentNullException(nameof(bills));
        _payments = payments ?? throw new ArgumentNullException(nameof(payments));
        _applications = applications ?? throw new ArgumentNullException(nameof(applications));
    }

    // ── ISubLedgerReadModel ──────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<SubLedgerPosition> GetPositionAsync(
        TenantId tenantId,
        SubLedgerAccountId subLedgerAccountId,
        DateOnly asOf,
        CancellationToken cancellationToken = default)
    {
        // Tenant isolation: composite (TenantId, Id) lookup — cross-tenant miss returns null.
        var account = await _accounts.GetAsync(tenantId, subLedgerAccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return SubLedgerPosition.Empty(subLedgerAccountId, default, default, asOf);

        var openItemBalance = await ComputeOpenItemBalanceAsync(
            tenantId, account, asOf, cancellationToken).ConfigureAwait(false);
        var unappliedCredits = await ComputeUnappliedCreditsAsync(
            tenantId, account, cancellationToken).ConfigureAwait(false);

        return new SubLedgerPosition(
            SubLedgerAccountId: account.Id,
            ControlAccountId: account.ControlAccountId,
            Kind: account.Kind,
            AsOf: asOf,
            OpenItemBalance: openItemBalance,
            UnappliedCredits: unappliedCredits,
            Balance: openItemBalance - unappliedCredits);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubLedgerEntry>> GetHistoryAsync(
        TenantId tenantId,
        SubLedgerAccountId subLedgerAccountId,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        var account = await _accounts.GetAsync(tenantId, subLedgerAccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return Array.Empty<SubLedgerEntry>();

        return account.Kind == SubLedgerKind.Receivable
            ? await BuildArHistoryAsync(tenantId, account, asOf, cancellationToken).ConfigureAwait(false)
            : await BuildApHistoryAsync(tenantId, account, asOf, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<SubLedgerAgingSummary> GetAgingForSubLedgerAsync(
        TenantId tenantId,
        SubLedgerAccountId subLedgerAccountId,
        DateOnly asOf,
        CancellationToken cancellationToken = default)
    {
        var account = await _accounts.GetAsync(tenantId, subLedgerAccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account is null)
            return SubLedgerAgingSummary.Empty(subLedgerAccountId, asOf);

        return account.Kind == SubLedgerKind.Receivable
            ? await BuildArAgingSummaryAsync(tenantId, account, asOf, cancellationToken).ConfigureAwait(false)
            : await BuildApAgingSummaryAsync(tenantId, account, asOf, cancellationToken).ConfigureAwait(false);
    }

    // ── Private helpers — position ───────────────────────────────────────

    /// <summary>
    /// Σ(open-item.Balance) for all AR invoices or AP bills linked to this
    /// sub-ledger account with an asOf cutoff.
    ///
    /// <para>
    /// <b>FK-authoritative (ADR 0120 PR-D / PR-C SPOT-CHECK binding carry #1):</b>
    /// Resolution uses ONLY <c>ListBySubLedgerAccountAsync</c> — the per-account FK.
    /// The PartyId party-wide fallback has been REMOVED. Rationale: once PR-D creates
    /// multi-account-per-party shape (one <see cref="SubLedgerAccount"/> per lease),
    /// a zero-FK account that fell back to <c>ListByCustomerAsync</c> would return
    /// ALL leases' open items for that party, producing the exact PartyId-overmatch
    /// the ADR exists to fix. Un-stamped items MUST surface as R1 data-quality
    /// findings via the migration reconcile, never silently swept into a sibling account.
    /// </para>
    ///
    /// <para>
    /// <b>C-FIN-1:</b> uses the cached <c>Balance</c> field, NOT <c>Σ AmountApplied</c>.
    /// </para>
    /// </summary>
    private async Task<decimal> ComputeOpenItemBalanceAsync(
        TenantId tenantId,
        SubLedgerAccount account,
        DateOnly asOf,
        CancellationToken ct)
    {
        // Tenant passed explicitly — no ambient context; defence-in-depth.
        // FK-authoritative: PartyId fallback REMOVED per PR-C SPOT-CHECK binding carry.
        if (account.Kind == SubLedgerKind.Receivable)
        {
            var invoices = await _invoices.ListBySubLedgerAccountAsync(
                tenantId, account.Id, ct).ConfigureAwait(false);
            return invoices
                .Where(i =>
                    i.Status.IsOpen() &&
                    i.Balance > 0m &&
                    i.IssueDate <= asOf)
                .Sum(i => i.Balance);
        }
        else
        {
            var bills = await _bills.ListBySubLedgerAccountAsync(
                tenantId, account.Id, ct).ConfigureAwait(false);
            return bills
                .Where(b =>
                    b.Status.IsOpen() &&
                    b.Balance > 0m &&
                    b.BillDate <= asOf)
                .Sum(b => b.Balance);
        }
    }

    /// <summary>
    /// Σ(Payment.UnappliedAmount) for all cleared Unapplied / PartiallyApplied payments
    /// belonging to the sub-ledger's party in this chart and tenant.
    /// These are open credit items (advance rent, vendor prepayment, etc.). Draft payments whose
    /// clearing journal has not posted are capture records, not balance-reducing credits (PPI-1).
    /// </summary>
    private async Task<decimal> ComputeUnappliedCreditsAsync(
        TenantId tenantId,
        SubLedgerAccount account,
        CancellationToken ct)
    {
        var payments = await _payments.ListByPartyAsync(
            tenantId, account.ChartId, account.PartyId, ct).ConfigureAwait(false);

        var directionFilter = account.Kind == SubLedgerKind.Receivable
            ? PaymentDirection.Inbound
            : PaymentDirection.Outbound;

        return payments
            .Where(p =>
                p.Direction == directionFilter &&
                p.JournalEntryId is not null &&
                p.UnappliedAmount > 0m &&
                p.Status.IsActive())
            .Sum(p => p.UnappliedAmount);
    }

    // ── Private helpers — history ────────────────────────────────────────

    private async Task<IReadOnlyList<SubLedgerEntry>> BuildArHistoryAsync(
        TenantId tenantId,
        SubLedgerAccount account,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        // FK-authoritative: PartyId fallback REMOVED per PR-C SPOT-CHECK binding carry.
        var invoices = await _invoices.ListBySubLedgerAccountAsync(tenantId, account.Id, ct).ConfigureAwait(false);

        var entries = new List<SubLedgerEntry>();

        foreach (var inv in invoices)
        {
            // Charge event — invoice issued.
            entries.Add(new SubLedgerEntry(
                EntryDate: inv.IssueDate,
                Kind: SubLedgerEntryKind.Charge,
                SourceId: inv.Id.Value,
                Reference: inv.InvoiceNumber,
                Amount: inv.Total,
                RunningBalance: 0m)); // running balance computed after sorting

            // Void / write-off events — terminal items that left the open set.
            if (inv.Status == InvoiceStatus.Voided)
            {
                entries.Add(new SubLedgerEntry(
                    EntryDate: inv.IssueDate,
                    Kind: SubLedgerEntryKind.Void,
                    SourceId: inv.Id.Value,
                    Reference: inv.InvoiceNumber,
                    Amount: -inv.Total,
                    RunningBalance: 0m));
            }
            else if (inv.Status == InvoiceStatus.WrittenOff)
            {
                entries.Add(new SubLedgerEntry(
                    EntryDate: inv.IssueDate,
                    Kind: SubLedgerEntryKind.WriteOff,
                    SourceId: inv.Id.Value,
                    Reference: inv.InvoiceNumber,
                    Amount: -inv.Total,
                    RunningBalance: 0m));
            }
        }

        // C-T2-1 (security-engineering ADR-0122-P2 SPOT-CHECK binding condition): the payment-leg MUST be
        // FK-scoped to THIS sub-ledger account's invoices, not party-wide. A party can hold MULTIPLE leases
        // (each a distinct SubLedgerAccount keyed pm:lease:v1:{leaseId}) sharing the same AR control +
        // party; a payment applied to a SIBLING lease's invoice must NOT appear in (and net against) this
        // lease's history. Resolution is now FK-AUTHORITATIVE BY CONSTRUCTION: we read the applications
        // from the authoritative IPaymentApplicationRepository keyed on each FK-matched invoice id
        // (ListByTargetAsync), instead of walking a party-wide payment list's snapshot. So only
        // applications whose TargetId is one of THIS account's invoices contribute — the over-emission
        // is structurally impossible (the party-wide ListByPartyAsync payment scan is gone). This also
        // removes the dependency on the non-authoritative Payment.Applications snapshot, so a payment
        // recorded + applied via the node write path (whose snapshot the EF repo does not rehydrate)
        // still surfaces correctly.
        entries.AddRange(await BuildPaymentLegAsync(
            tenantId, invoices.Select(i => i.Id.Value), AppliedTo.Invoice, PaymentDirection.Inbound, asOf, ct)
            .ConfigureAwait(false));

        return ComputeRunningBalancesAndSort(entries);
    }

    /// <summary>
    /// FK-authoritative payment-leg builder shared by AR / AP history (C-T2-1). For each FK-matched
    /// target (invoice/bill) id, reads the authoritative <see cref="PaymentApplication"/> rows via
    /// <see cref="IPaymentApplicationRepository.ListByTargetAsync"/>, then resolves each distinct owning
    /// <see cref="Payment"/> (for its <see cref="Payment.PaymentNumber"/> + direction). Emits one
    /// <see cref="SubLedgerEntryKind.Payment"/> entry per application whose owning payment matches the
    /// expected direction. Because the query is scoped to the FK-matched target ids, a sibling
    /// account's application can never be reached — no party-wide over-emission.
    /// </summary>
    private async Task<IReadOnlyList<SubLedgerEntry>> BuildPaymentLegAsync(
        TenantId tenantId,
        IEnumerable<string> fkTargetIds,
        AppliedTo expectedAppliedTo,
        PaymentDirection expectedDirection,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        var entries = new List<SubLedgerEntry>();
        var paymentCache = new Dictionary<PaymentId, Payment?>();

        foreach (var targetId in fkTargetIds)
        {
            var applications = await _applications
                .ListByTargetAsync(tenantId, targetId, ct).ConfigureAwait(false);

            foreach (var app in applications)
            {
                // ADR 0122 PPI-2: original + contra reversal rows remain durable evidence, but
                // neither participates in the derived position/history after reversal.
                if (!app.IsActive)
                {
                    continue;
                }

                // Defence-in-depth: the application's discriminator must match the leg under construction.
                if (app.AppliedTo != expectedAppliedTo)
                {
                    continue;
                }

                if (!paymentCache.TryGetValue(app.PaymentId, out var pmt))
                {
                    pmt = await _payments.GetAsync(tenantId, app.PaymentId, asOf, ct).ConfigureAwait(false);
                    paymentCache[app.PaymentId] = pmt;
                }

                // Skip an orphaned application (payment missing / cross-tenant) or a direction mismatch.
                if (pmt is null || pmt.Direction != expectedDirection)
                {
                    continue;
                }

                entries.Add(new SubLedgerEntry(
                    EntryDate: app.AppliedDate,
                    Kind: SubLedgerEntryKind.Payment,
                    SourceId: pmt.Id.Value,
                    Reference: pmt.PaymentNumber,
                    Amount: -(app.AmountApplied + app.DiscountAmount + app.WriteoffAmount),
                    RunningBalance: 0m));
            }
        }

        return entries;
    }

    private async Task<IReadOnlyList<SubLedgerEntry>> BuildApHistoryAsync(
        TenantId tenantId,
        SubLedgerAccount account,
        DateTimeOffset asOf,
        CancellationToken ct)
    {
        // FK-authoritative: PartyId fallback REMOVED per PR-C SPOT-CHECK binding carry.
        var bills = await _bills.ListBySubLedgerAccountAsync(tenantId, account.Id, ct).ConfigureAwait(false);

        var entries = new List<SubLedgerEntry>();

        foreach (var bill in bills)
        {
            entries.Add(new SubLedgerEntry(
                EntryDate: bill.BillDate,
                Kind: SubLedgerEntryKind.Charge,
                SourceId: bill.Id.Value,
                Reference: bill.BillNumber,
                Amount: bill.Total,
                RunningBalance: 0m));

            if (bill.Status == BillStatus.Voided)
            {
                entries.Add(new SubLedgerEntry(
                    EntryDate: bill.BillDate,
                    Kind: SubLedgerEntryKind.Void,
                    SourceId: bill.Id.Value,
                    Reference: bill.BillNumber,
                    Amount: -bill.Total,
                    RunningBalance: 0m));
            }
        }

        // C-T2-1: FK-authoritative payment-leg (AP dual of the AR fix) — applications read from the
        // authoritative repo keyed on each FK-matched bill id, owning payment direction = Outbound.
        entries.AddRange(await BuildPaymentLegAsync(
            tenantId, bills.Select(b => b.Id.Value), AppliedTo.Bill, PaymentDirection.Outbound, asOf, ct)
            .ConfigureAwait(false));

        return ComputeRunningBalancesAndSort(entries);
    }

    /// <summary>
    /// Sort entries chronologically and compute the running balance in place.
    /// Entries on the same date are sorted by kind (Charge before Payment) for
    /// deterministic output.
    /// </summary>
    private static IReadOnlyList<SubLedgerEntry> ComputeRunningBalancesAndSort(
        List<SubLedgerEntry> entries)
    {
        entries.Sort((a, b) =>
        {
            var dateCompare = a.EntryDate.DayNumber.CompareTo(b.EntryDate.DayNumber);
            if (dateCompare != 0) return dateCompare;
            // Charges before payments on the same day (conventional ordering).
            return a.Kind.CompareTo(b.Kind);
        });

        decimal running = 0m;
        for (var i = 0; i < entries.Count; i++)
        {
            running += entries[i].Amount;
            entries[i] = entries[i] with { RunningBalance = running };
        }

        return entries;
    }

    // ── Private helpers — aging ──────────────────────────────────────────

    private async Task<SubLedgerAgingSummary> BuildArAgingSummaryAsync(
        TenantId tenantId,
        SubLedgerAccount account,
        DateOnly asOf,
        CancellationToken ct)
    {
        // FK-authoritative: PartyId fallback REMOVED per PR-C SPOT-CHECK binding carry.
        var invoices = await _invoices.ListBySubLedgerAccountAsync(tenantId, account.Id, ct).ConfigureAwait(false);

        return BuildAgingSummaryFromBucketedItems(
            account.Id,
            asOf,
            invoices
                .Where(i => i.Status.IsOpen() && i.Balance > 0m)
                .Select(i => new AgingItemProjection(
                    SourceId: i.Id.Value,
                    SourceNumber: i.InvoiceNumber,
                    IssueDate: i.IssueDate,
                    DueDate: i.DueDate,
                    Total: i.Total,
                    AmountPaid: i.AmountPaid,
                    Balance: i.Balance)));
    }

    private async Task<SubLedgerAgingSummary> BuildApAgingSummaryAsync(
        TenantId tenantId,
        SubLedgerAccount account,
        DateOnly asOf,
        CancellationToken ct)
    {
        // FK-authoritative: PartyId fallback REMOVED per PR-C SPOT-CHECK binding carry.
        var bills = await _bills.ListBySubLedgerAccountAsync(tenantId, account.Id, ct).ConfigureAwait(false);

        return BuildAgingSummaryFromBucketedItems(
            account.Id,
            asOf,
            bills
                .Where(b => b.Status.IsOpen() && b.Balance > 0m)
                .Select(b => new AgingItemProjection(
                    SourceId: b.Id.Value,
                    SourceNumber: b.BillNumber,
                    IssueDate: b.BillDate,
                    DueDate: b.DueDate,
                    Total: b.Total,
                    AmountPaid: b.AmountPaid,
                    Balance: b.Balance)));
    }

    /// <summary>
    /// Canonical aging aggregation — pure function over the item projection so
    /// the same logic works for AR invoices and AP bills.
    /// Mirrors <c>ArAgingService.Summarize</c> but scoped to a single sub-ledger
    /// and uses <see cref="AgingBucket"/> (canonical in blocks-financial-ledger, ADR 0120 PR-E2)
    /// and <see cref="SubLedgerAgingRow"/> (subledger-specific shape in the LOW identity assembly).
    /// </summary>
    private static SubLedgerAgingSummary BuildAgingSummaryFromBucketedItems(
        SubLedgerAccountId accountId,
        DateOnly asOf,
        IEnumerable<AgingItemProjection> items)
    {
        decimal current = 0m, b0to30 = 0m, b31to60 = 0m, b61to90 = 0m, b90plus = 0m;
        var rows = new List<SubLedgerAgingRow>();

        foreach (var item in items)
        {
            var daysPastDue = asOf.DayNumber - item.DueDate.DayNumber;
            var bucket = ClassifyAgingBucket(daysPastDue);

            rows.Add(new SubLedgerAgingRow(
                SourceId: item.SourceId,
                SourceNumber: item.SourceNumber,
                IssueDate: item.IssueDate,
                DueDate: item.DueDate,
                DaysPastDue: daysPastDue,
                Total: item.Total,
                AmountPaid: item.AmountPaid,
                Balance: item.Balance,
                Bucket: bucket));

            switch (bucket)
            {
                case AgingBucket.Current:    current += item.Balance; break;
                case AgingBucket.Days0To30:  b0to30  += item.Balance; break;
                case AgingBucket.Days31To60: b31to60 += item.Balance; break;
                case AgingBucket.Days61To90: b61to90 += item.Balance; break;
                case AgingBucket.Days90Plus: b90plus += item.Balance; break;
            }
        }

        rows.Sort((a, b) =>
        {
            var d = a.DueDate.DayNumber.CompareTo(b.DueDate.DayNumber);
            return d != 0 ? d : string.CompareOrdinal(a.SourceNumber, b.SourceNumber);
        });

        var total = current + b0to30 + b31to60 + b61to90 + b90plus;
        return new SubLedgerAgingSummary(accountId, asOf, current, b0to30, b31to60, b61to90, b90plus, total, rows);
    }

    private static AgingBucket ClassifyAgingBucket(int daysPastDue) =>
        daysPastDue switch
        {
            <= 0  => AgingBucket.Current,
            <= 30 => AgingBucket.Days0To30,
            <= 60 => AgingBucket.Days31To60,
            <= 90 => AgingBucket.Days61To90,
            _     => AgingBucket.Days90Plus,
        };

    /// <summary>
    /// Projection struct that bridges AR invoices and AP bills to the shared
    /// aging aggregation. The subledger identity assembly's <see cref="AgingRow"/>
    /// type uses generic <c>SourceId</c>/<c>SourceNumber</c> labels rather than
    /// the AR-specific <c>InvoiceId</c>/<c>InvoiceNumber</c> fields.
    /// </summary>
    private readonly record struct AgingItemProjection(
        string SourceId,
        string SourceNumber,
        DateOnly IssueDate,
        DateOnly DueDate,
        decimal Total,
        decimal AmountPaid,
        decimal Balance);
}
