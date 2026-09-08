using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.FinancialPayments.Models;

/// <summary>
/// The many-to-many link between a <see cref="Payment"/> and its target
/// Invoice or Bill. One <see cref="Payment"/> may have multiple applications
/// (partial payments against separate line-items); one Invoice/Bill may be
/// reduced by multiple payments.
///
/// <para>
/// <b>Direction-matching invariant (§3.10 validation rule 1):</b>
/// <list type="bullet">
///   <item><see cref="PaymentDirection.Inbound"/> payment → <see cref="AppliedTo.Invoice"/> only.</item>
///   <item><see cref="PaymentDirection.Outbound"/> payment → <see cref="AppliedTo.Bill"/> only.</item>
/// </list>
/// This invariant is enforced by <c>IPaymentApplicationService.ApplyAsync</c> (PR 3)
/// and verified in the test suite.
/// </para>
///
/// <para>
/// <b>Amounts:</b> <see cref="AmountApplied"/> + <see cref="DiscountAmount"/> +
/// <see cref="WriteoffAmount"/> must be &lt;= the target's balance at time of
/// application. The in-memory repository does NOT enforce this — enforcement
/// lives in <c>DefaultPaymentApplicationService</c> (PR 3).
/// </para>
///
/// <para>
/// <b>Tenant scope (PR 3 amber-amendment):</b> implements
/// <see cref="IMustHaveTenant"/>. <see cref="TenantId"/> is populated from
/// the owning <see cref="Payment"/>'s <c>TenantId</c> at create-call-site so
/// cross-tenant id-guessing against the application ledger fails closed at
/// the service layer.
/// </para>
/// </summary>
public sealed record PaymentApplication : IMustHaveTenant
{
    /// <summary>Stable identifier.</summary>
    public required PaymentApplicationId Id { get; init; }

    /// <summary>Tenant scope. Required — non-default per <see cref="IMustHaveTenant"/>.</summary>
    public required TenantId TenantId { get; init; }

    /// <summary>The payment being applied.</summary>
    public required PaymentId PaymentId { get; init; }

    /// <summary>Discriminator: which document type <see cref="TargetId"/> refers to.</summary>
    public required AppliedTo AppliedTo { get; init; }

    /// <summary>
    /// The <c>InvoiceId</c> or <c>BillId</c> string value being reduced.
    /// String union per spec §3.10 — the concrete type is discriminated by <see cref="AppliedTo"/>.
    /// </summary>
    public required string TargetId { get; init; }

    /// <summary>Portion of the payment's gross amount credited to the target balance.</summary>
    public required decimal AmountApplied { get; init; }

    /// <summary>Early-pay discount granted; zero if none. GL: Discount Allowed expense line (§6.1).</summary>
    public decimal DiscountAmount { get; init; }

    /// <summary>Short-pay write-off; zero if none. GL: Bad Debt expense line (§6.1).</summary>
    public decimal WriteoffAmount { get; init; }

    /// <summary>Date the funds were applied (may differ from <c>Payment.PaymentDate</c> for back-dated corrections).</summary>
    public required DateOnly AppliedDate { get; init; }

    /// <summary>
    /// Timestamp of the one-way reversal transition, or <see langword="null"/> while this
    /// application remains active. A reversed application is retained as immutable settlement
    /// evidence and excluded from derived positions.
    /// </summary>
    public Instant? ReversedAtUtc { get; init; }

    /// <summary>
    /// Contra-application that reverses this row. Populated together with
    /// <see cref="ReversedAtUtc"/> by the repository's atomic reversal operation.
    /// </summary>
    public PaymentApplicationId? ReversedByApplicationId { get; init; }

    /// <summary>
    /// Original application reversed by this contra row. Non-null only on reversal evidence.
    /// </summary>
    public PaymentApplicationId? ReversesApplicationId { get; init; }

    /// <summary>True only for a currently-effective application row.</summary>
    public bool IsActive => ReversedAtUtc is null
        && ReversedByApplicationId is null
        && ReversesApplicationId is null;

    // ── Audit ──
    public required Instant CreatedAtUtc { get; init; }

    /// <summary>Construct a new application record.</summary>
    public static PaymentApplication Create(
        TenantId tenantId,
        PaymentId paymentId,
        AppliedTo appliedTo,
        string targetId,
        decimal amountApplied,
        DateOnly appliedDate,
        Instant createdAtUtc,
        decimal discountAmount = 0m,
        decimal writeoffAmount = 0m,
        PaymentApplicationId? id = null)
    {
        return new PaymentApplication
        {
            Id = id ?? PaymentApplicationId.NewId(),
            TenantId = tenantId,
            PaymentId = paymentId,
            AppliedTo = appliedTo,
            TargetId = targetId,
            AmountApplied = amountApplied,
            DiscountAmount = discountAmount,
            WriteoffAmount = writeoffAmount,
            AppliedDate = appliedDate,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>
    /// Create the immutable contra row for <paramref name="original"/>. The amounts negate the
    /// original allocation so the evidence pair reconstructs to zero without erasing either row.
    /// </summary>
    public static PaymentApplication CreateReversal(
        PaymentApplication original,
        Instant reversedAtUtc,
        PaymentApplicationId? id = null)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (!original.IsActive)
        {
            throw new InvalidOperationException(
                $"PaymentApplication '{original.Id.Value}' is already reversed or is itself reversal evidence.");
        }

        return new PaymentApplication
        {
            Id = id ?? PaymentApplicationId.NewId(),
            TenantId = original.TenantId,
            PaymentId = original.PaymentId,
            AppliedTo = original.AppliedTo,
            TargetId = original.TargetId,
            AmountApplied = -original.AmountApplied,
            DiscountAmount = -original.DiscountAmount,
            WriteoffAmount = -original.WriteoffAmount,
            AppliedDate = DateOnly.FromDateTime(reversedAtUtc.Value.UtcDateTime),
            ReversesApplicationId = original.Id,
            CreatedAtUtc = reversedAtUtc,
        };
    }

    /// <summary>Return the original row with its irreversible contra linkage stamped.</summary>
    public PaymentApplication MarkReversed(PaymentApplicationId reversalId, Instant reversedAtUtc)
    {
        if (!IsActive)
        {
            throw new InvalidOperationException(
                $"PaymentApplication '{Id.Value}' is already reversed or is itself reversal evidence.");
        }

        return this with
        {
            ReversedAtUtc = reversedAtUtc,
            ReversedByApplicationId = reversalId,
        };
    }
}
