using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Payroll.Models;

/// <summary>
/// A payroll filing obligation date — the "when is this due" surface of v1.
/// Tracks what filing is owed and when, without any submission or calculation
/// logic (those are v2 category-provider concerns).
/// </summary>
/// <remarks>
/// Examples: quarterly payroll-tax return, monthly remittance of withheld tax,
/// annual employer declaration. The operator (or a v2 jurisdiction engine)
/// seeds these; the UI surfaces them in a calendar view so nothing goes missed.
/// </remarks>
public sealed record FilingObligation : IMustHaveTenant
{
    /// <summary>Unique identifier for this filing obligation.</summary>
    public FilingObligationId Id { get; }

    /// <summary>Tenant scope. Required per <see cref="IMustHaveTenant"/>.</summary>
    public TenantId TenantId { get; }

    /// <summary>
    /// Short label for the obligation (e.g. "Q2 2026 Payroll Tax Return",
    /// "June 2026 PAYE Remittance").
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// The date by which this obligation must be fulfilled.
    /// </summary>
    public DateOnly DueDate { get; }

    /// <summary>
    /// Optional reference period this obligation covers (e.g. Q2 2026).
    /// Used for filtering and calendar grouping.
    /// </summary>
    public DateOnly? PeriodStart { get; init; }

    /// <summary>
    /// Optional end of the reference period.
    /// </summary>
    public DateOnly? PeriodEnd { get; init; }

    /// <summary>
    /// Jurisdiction or authority code (e.g. "AU-ATO", "US-IRS", "GB-HMRC").
    /// Free-form in v1; a v2 enum will constrain this when jurisdiction
    /// engines are added.
    /// </summary>
    public string? JurisdictionCode { get; init; }

    /// <summary>
    /// Optional reference to the pay run this obligation was generated from.
    /// </summary>
    public PayRunId? SourcePayRunId { get; init; }

    /// <summary>Whether this obligation has been marked as complete by the operator.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Optional operator notes.</summary>
    public string? Notes { get; init; }

    /// <summary>Wall-clock instant at which this obligation was created.</summary>
    public Instant CreatedAtUtc { get; }

    /// <summary>Constructs a filing obligation.</summary>
    public FilingObligation(
        FilingObligationId id,
        TenantId tenantId,
        string label,
        DateOnly dueDate,
        Instant createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("Filing obligation label is required.", nameof(label));

        Id = id;
        TenantId = tenantId;
        Label = label;
        DueDate = dueDate;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Factory method for creating a new filing obligation with a generated id.
    /// </summary>
    public static FilingObligation Create(
        TenantId tenantId,
        string label,
        DateOnly dueDate,
        Instant createdAtUtc,
        PayRunId? sourcePayRunId = null,
        string? jurisdictionCode = null)
    {
        return new FilingObligation(
            id: FilingObligationId.NewId(),
            tenantId: tenantId,
            label: label,
            dueDate: dueDate,
            createdAtUtc: createdAtUtc)
        {
            SourcePayRunId = sourcePayRunId,
            JurisdictionCode = jurisdictionCode,
        };
    }
}
