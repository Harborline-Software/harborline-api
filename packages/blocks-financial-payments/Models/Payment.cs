using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.FinancialPayments.Models;

/// <summary>
/// A cash-movement event — either money received from a customer (Inbound)
/// or money sent to a vendor (Outbound). Implements <see cref="IMustHaveTenant"/>
/// so persistence adapters enforce tenant isolation at the row level.
///
/// <para>
/// <b>UnappliedAmount is cached, not computed.</b> It is kept consistent by
/// <c>IPaymentApplicationService</c> (PR 3), which decrements it on apply and
/// increments it on unapply. The invariant
/// <c>UnappliedAmount == Amount - sum(Applications[].AmountApplied)</c>
/// is verified by the test suite.
/// </para>
///
/// <para>
/// <b>PaymentNumber</b> in PR 1 is a ULID-based string supplied by the caller.
/// Sequential per-chart numbering lands in a Phase 2 follow-up — same pattern
/// as <c>Invoice.InvoiceNumber</c> in the AR cluster.
/// </para>
/// </summary>
public sealed record Payment : IMustHaveTenant
{
    /// <summary>Stable identifier.</summary>
    public required PaymentId Id { get; init; }

    /// <summary>Tenant scope. Required — non-default per <see cref="IMustHaveTenant"/>.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The chart-of-accounts under which this payment posts.</summary>
    public required ChartOfAccountsId ChartId { get; init; }

    /// <summary>
    /// Cash direction: <see cref="PaymentDirection.Inbound"/> (customer pays us) or
    /// <see cref="PaymentDirection.Outbound"/> (we pay vendor).
    /// Controls which document types may be targeted in <see cref="PaymentApplication"/>.
    /// </summary>
    public required PaymentDirection Direction { get; init; }

    /// <summary>Human-readable payment number. PR 1 callers supply directly; Phase 2 mints sequentially.</summary>
    public required string PaymentNumber { get; init; }

    /// <summary>The Party (customer or vendor) associated with this payment.</summary>
    public required PartyId PartyId { get; init; }

    /// <summary>
    /// The kind of open item this payment was recorded against before clearing.
    /// Paired with <see cref="IntendedTargetId"/>; both are null for legacy or
    /// target-independent records. This is record-time intent, not an application.
    /// </summary>
    public AppliedTo? IntendedTargetType { get; init; }

    /// <summary>
    /// The invoice or bill identifier this payment was recorded against before
    /// clearing. Paired with <see cref="IntendedTargetType"/>; both are null for
    /// legacy or target-independent records.
    /// </summary>
    public string? IntendedTargetId { get; init; }

    /// <summary>Optional bank account handle for reconciliation.</summary>
    public GLAccountId? BankAccountId { get; init; }

    /// <summary>Date the payment was received or issued.</summary>
    public required DateOnly PaymentDate { get; init; }

    /// <summary>Gross amount of the payment.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code; defaults to USD.</summary>
    public string Currency { get; init; } = "USD";

    /// <summary>How the payment was transacted.</summary>
    public required PaymentMethod Method { get; init; }

    /// <summary>External reference number (e.g., check number, ACH trace ID).</summary>
    public string? Reference { get; init; }

    /// <summary>Lifecycle state.</summary>
    public required PaymentStatus Status { get; init; }

    /// <summary>
    /// Cached: how much of <see cref="Amount"/> has not yet been applied.
    /// Invariant: <c>UnappliedAmount == Amount - sum(Applications[].AmountApplied)</c>;
    /// always <c>&gt;= 0</c>.
    /// </summary>
    public required decimal UnappliedAmount { get; init; }

    /// <summary>Snapshot of applications at last load. Updated by <c>IPaymentApplicationService</c>.</summary>
    public IReadOnlyList<PaymentApplication> Applications { get; init; } = [];

    /// <summary>The GL journal entry from <c>IPaymentPostingService.ClearAsync</c>; null until cleared.</summary>
    public JournalEntryId? JournalEntryId { get; init; }

    /// <summary>The GL reversal journal entry from <c>IPaymentPostingService.BounceAsync</c>; non-null only when bounced.</summary>
    public JournalEntryId? BouncedByEntryId { get; init; }

    /// <summary>Optional free-text notes.</summary>
    public string? Notes { get; init; }

    /// <summary>External system reference (e.g., ERPNext <c>PE-0001</c>) for migration / sync.</summary>
    public string? ExternalRef { get; init; }

    /// <summary>
    /// Canonical version stamp of the external source record this payment was last
    /// imported from — the ERPNext <c>modified</c> timestamp (ADR 0100 C7 / OQ-B:
    /// the external-ref version is a first-class indexed field, NOT smuggled into
    /// <see cref="Notes"/>). An ordinal compare against the inbound source
    /// <c>Modified</c> decides Skipped (same/older) vs Updated (newer) on re-import.
    /// Null for payments created natively (not via import). <see cref="Notes"/> is
    /// reserved for operator free-text and is never overwritten by the importer.
    /// </summary>
    public string? ExternalRefVersion { get; init; }

    /// <summary>
    /// Idempotency key for the payment-WRITE path (ADR 0122 §D4 P2 / T2 payment-write —
    /// <b>SourceReference ALONE</b>, mirroring the JournalEntry posting-idempotency key from P1
    /// and the ADR-0100 <c>ExternalRef</c> precedent). When a payment is recorded against an
    /// invoice/bill, the caller supplies a deterministic per-record source reference (e.g. the
    /// invoice-payment idempotency token) so a re-driven record (network retry, double-submit)
    /// resolves to the EXISTING payment rather than minting a duplicate. The durable backstop is a
    /// tenant-scoped UNIQUE partial index over this column on the recoverable store
    /// (<c>local-node.db</c> / Bridge EfJournalStore) — declared once in the shared
    /// <c>PaymentsEntityModule</c>, mirroring <c>ux_journal_entries_tenant_source_ref</c>. Null for
    /// payments with no idempotent source (manual records carry their own
    /// <c>(TenantId, ChartId, PaymentNumber)</c> uniqueness); SQLite treats NULLs as distinct in a
    /// unique index, so null-SourceReference payments never collide. SC-4-safe by construction: the
    /// dedupe state IS the recoverable payment record + the index, NOT a seed-keyed KV.
    /// </summary>
    public string? SourceReference { get; init; }

    // ── CRDT envelope ──
    public required Instant CreatedAtUtc { get; init; }
    public PartyId? CreatedBy { get; init; }
    public Instant UpdatedAtUtc { get; init; }
    public PartyId? UpdatedBy { get; init; }
    public required long Version { get; init; }

    /// <summary>
    /// Construct a new Draft payment. <see cref="UnappliedAmount"/> is initialised to
    /// <paramref name="amount"/> (nothing applied yet). Status is
    /// <see cref="PaymentStatus.Draft"/>.
    /// </summary>
    public static Payment Create(
        TenantId tenantId,
        ChartOfAccountsId chartId,
        PaymentDirection direction,
        string paymentNumber,
        PartyId partyId,
        DateOnly paymentDate,
        decimal amount,
        PaymentMethod method,
        Instant createdAtUtc,
        PartyId? createdBy = null,
        PaymentId? id = null,
        GLAccountId? bankAccountId = null,
        string currency = "USD",
        string? reference = null,
        string? notes = null,
        string? externalRef = null,
        string? externalRefVersion = null,
        string? sourceReference = null,
        AppliedTo? intendedTargetType = null,
        string? intendedTargetId = null)
    {
        if (intendedTargetType.HasValue != (intendedTargetId is not null)
            || intendedTargetId is not null && string.IsNullOrWhiteSpace(intendedTargetId))
        {
            throw new ArgumentException(
                "Intended target type and id must either both be supplied or both be null.",
                nameof(intendedTargetId));
        }

        var now = createdAtUtc;
        return new Payment
        {
            Id = id ?? PaymentId.NewId(),
            TenantId = tenantId,
            ChartId = chartId,
            Direction = direction,
            PaymentNumber = paymentNumber,
            PartyId = partyId,
            IntendedTargetType = intendedTargetType,
            IntendedTargetId = intendedTargetId,
            BankAccountId = bankAccountId,
            PaymentDate = paymentDate,
            Amount = amount,
            Currency = currency,
            Method = method,
            Reference = reference,
            Status = PaymentStatus.Draft,
            UnappliedAmount = amount,
            Notes = notes,
            ExternalRef = externalRef,
            ExternalRefVersion = externalRefVersion,
            SourceReference = sourceReference,
            CreatedAtUtc = now,
            CreatedBy = createdBy,
            UpdatedAtUtc = now,
            Version = 1,
        };
    }
}
