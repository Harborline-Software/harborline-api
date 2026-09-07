namespace Harborline.Api.LocalNodeHost.Data.Payroll;

/// <summary>
/// Node-local-authoritative EF persistence records for the payroll surface
/// (T4 local-first sweep — the payroll node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why flat records, not the block domain models directly.</b> The block
/// <c>Employee</c> / <c>PayRun</c> / <c>FilingObligation</c> domain models are validated
/// records with value-object keys and a private-ctor <c>PayRunLine</c> value-object collection;
/// mapping those as EF owned-collections is fragile (the value-object ctor validates, and EF
/// materialises bypassing it). Following the <c>LeaseRecord</c> / <c>PropertyRecord</c> Cohort-C
/// precedent, the node persists flat mutable records and the Node EF repos translate
/// record↔domain-model at the repository boundary, so <c>PayRunPostingService</c> (which depends on
/// the block repo interfaces returning real domain models) reuses verbatim.
/// </para>
/// <para>
/// <b>Encrypted at rest (SC-1).</b> Mapped by <c>NodeLocalPayrollDbContext</c>, which opens the SAME
/// SQLCipher-encrypted <c>local-node.db</c> as the financial store, keyed by the same
/// <c>SqlCipherConnectionInterceptor</c>. No plaintext path.
/// </para>
/// </remarks>
public sealed class EmployeeRecord
{
    /// <summary>Employee id (<c>EmployeeId.Value</c>) — primary key.</summary>
    public required string Id { get; set; }

    /// <summary>Tenant scope (always <c>"local"</c> on the single-device node).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Backing <c>PartyId.Value</c> from blocks-people-foundation.</summary>
    public string PartyId { get; set; } = string.Empty;

    /// <summary>Display name copied from the party record at creation time.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Optional GL cost-centre dimension (<c>ClassificationId.Value</c>) or null.</summary>
    public string? CostCentreId { get; set; }

    /// <summary>GL expense account for the gross-wage debit (<c>GLAccountId.Value</c>).</summary>
    public string WageExpenseAccountId { get; set; } = string.Empty;

    /// <summary>GL liability account for wages payable (<c>GLAccountId.Value</c>).</summary>
    public string WagesPayableAccountId { get; set; } = string.Empty;

    /// <summary>Whether this employee is active (excluded from new-run selection when false).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Last-update timestamp (UTC) or null.</summary>
    public DateTimeOffset? UpdatedAtUtc { get; set; }
}

/// <summary>Node-local-authoritative EF record for a <c>PayRun</c>. Lines live in a child table.</summary>
public sealed class PayRunRecord
{
    /// <summary>Pay-run id (<c>PayRunId.Value</c>) — primary key.</summary>
    public required string Id { get; set; }

    /// <summary>Tenant scope (always <c>"local"</c> on the single-device node).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Operator label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Pay-period start (ISO date string, wire-faithful).</summary>
    public string PeriodStart { get; set; } = string.Empty;

    /// <summary>Pay-period end (ISO date string).</summary>
    public string PeriodEnd { get; set; } = string.Empty;

    /// <summary>GL effective date (ISO date string).</summary>
    public string PostingDate { get; set; } = string.Empty;

    /// <summary>Lifecycle status — one of <c>Draft | Posted | Reversed</c>.</summary>
    public string Status { get; set; } = "Draft";

    /// <summary>Default tax-withheld liability account (<c>GLAccountId.Value</c>).</summary>
    public string DefaultTaxWithheldAccountId { get; set; } = string.Empty;

    /// <summary>Default deduction-payable liability account.</summary>
    public string DefaultDeductionPayableAccountId { get; set; } = string.Empty;

    /// <summary>Default employer-liability expense account.</summary>
    public string DefaultEmployerLiabilityExpenseAccountId { get; set; } = string.Empty;

    /// <summary>Default employer-liability payable account.</summary>
    public string DefaultEmployerLiabilityPayableAccountId { get; set; } = string.Empty;

    /// <summary>FK to the posted journal entry (<c>JournalEntryId.Value</c>) or null while Draft.</summary>
    public string? JournalEntryId { get; set; }

    /// <summary>FK to the reversal journal entry or null unless Reversed.</summary>
    public string? ReversalEntryId { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>Last status-change timestamp (UTC) or null.</summary>
    public DateTimeOffset? UpdatedAtUtc { get; set; }

    /// <summary>Optimistic concurrency version stamp.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Child line rows.</summary>
    public List<PayRunLineRecord> Lines { get; set; } = new();
}

/// <summary>Node-local-authoritative EF record for a single <c>PayRunLine</c>.</summary>
public sealed class PayRunLineRecord
{
    /// <summary>Surrogate identity for the line row (the domain has no line id).</summary>
    public long Id { get; set; }

    /// <summary>FK to the owning <see cref="PayRunRecord"/>.</summary>
    public string PayRunId { get; set; } = string.Empty;

    /// <summary>Ordinal position within the run (preserves operator line order).</summary>
    public int Ordinal { get; set; }

    /// <summary>Employee id (<c>EmployeeId.Value</c>).</summary>
    public string EmployeeId { get; set; } = string.Empty;

    /// <summary>Gross wage (positive).</summary>
    public decimal GrossWage { get; set; }

    /// <summary>Employee-withheld tax (≥ 0).</summary>
    public decimal TaxWithheld { get; set; }

    /// <summary>Employee-side deductions (≥ 0).</summary>
    public decimal EmployeeDeductions { get; set; }

    /// <summary>Employer-side liability amount (≥ 0).</summary>
    public decimal EmployerLiabilityAmount { get; set; }

    /// <summary>Tax-withheld liability account (<c>GLAccountId.Value</c>).</summary>
    public string TaxWithheldAccountId { get; set; } = string.Empty;

    /// <summary>Deduction-payable liability account.</summary>
    public string DeductionPayableAccountId { get; set; } = string.Empty;

    /// <summary>Employer-liability expense account.</summary>
    public string EmployerLiabilityExpenseAccountId { get; set; } = string.Empty;

    /// <summary>Employer-liability payable account.</summary>
    public string EmployerLiabilityPayableAccountId { get; set; } = string.Empty;

    /// <summary>Optional per-line notes.</summary>
    public string? Notes { get; set; }
}

/// <summary>Node-local-authoritative EF record for a <c>FilingObligation</c>.</summary>
public sealed class FilingObligationRecord
{
    /// <summary>Filing-obligation id (<c>FilingObligationId.Value</c>) — primary key.</summary>
    public required string Id { get; set; }

    /// <summary>Tenant scope (always <c>"local"</c> on the single-device node).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Short label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Due date (ISO date string, wire-faithful).</summary>
    public string DueDate { get; set; } = string.Empty;

    /// <summary>Optional reference-period start (ISO date string) or null.</summary>
    public string? PeriodStart { get; set; }

    /// <summary>Optional reference-period end (ISO date string) or null.</summary>
    public string? PeriodEnd { get; set; }

    /// <summary>Optional jurisdiction/authority code.</summary>
    public string? JurisdictionCode { get; set; }

    /// <summary>Optional source pay-run id (<c>PayRunId.Value</c>).</summary>
    public string? SourcePayRunId { get; set; }

    /// <summary>Whether the obligation is complete.</summary>
    public bool IsComplete { get; set; }

    /// <summary>Optional operator notes.</summary>
    public string? Notes { get; set; }

    /// <summary>Creation timestamp (UTC).</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
}
