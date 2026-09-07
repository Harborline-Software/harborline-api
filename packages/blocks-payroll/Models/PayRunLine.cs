using Harborline.Api.Blocks.FinancialLedger.Models;

namespace Harborline.Api.Blocks.Payroll.Models;

/// <summary>
/// A single employee's payroll amounts within a <see cref="PayRun"/>.
/// The operator enters gross, tax-withheld, deductions, and employer-side
/// amounts; the <see cref="Services.IPayRunPostingService"/> maps these
/// to balanced GL journal entry lines.
/// </summary>
/// <remarks>
/// GL mapping for each <see cref="PayRunLine"/> (debits = credits per line set):
/// <list type="bullet">
///   <item>
///     Debit <see cref="Employee.WageExpenseAccountId"/> for <see cref="GrossWage"/>
///   </item>
///   <item>
///     Credit <see cref="Employee.WagesPayableAccountId"/> for <see cref="NetWage"/>
///     (gross minus employee-side tax and deductions)
///   </item>
///   <item>
///     Credit <see cref="TaxWithheldAccountId"/> for <see cref="TaxWithheld"/>
///     (employee PAYE / withholding tax — a liability until remitted to the authority)
///   </item>
///   <item>
///     Credit <see cref="DeductionPayableAccountId"/> for <see cref="EmployeeDeductions"/>
///     (benefits, garnishments etc. — a liability until remitted to the plan)
///   </item>
///   <item>
///     Debit <see cref="EmployerLiabilityExpenseAccountId"/> for <see cref="EmployerLiabilityAmount"/>
///     and Credit <see cref="EmployerLiabilityPayableAccountId"/> for the same amount
///     (employer-side taxes/contributions — e.g. payroll tax, pension match)
///   </item>
/// </list>
/// Total debits = <see cref="GrossWage"/> + <see cref="EmployerLiabilityAmount"/>.
/// Total credits = <see cref="NetWage"/> + <see cref="TaxWithheld"/> + <see cref="EmployeeDeductions"/>
///                + <see cref="EmployerLiabilityAmount"/>.
/// These must balance to satisfy the double-entry invariant.
/// </remarks>
public sealed record PayRunLine
{
    /// <summary>The employee this line belongs to.</summary>
    public EmployeeId EmployeeId { get; }

    /// <summary>
    /// Total gross wage for the period (before tax and deductions).
    /// Must be positive.
    /// </summary>
    public decimal GrossWage { get; }

    /// <summary>
    /// Amount withheld for employee income tax / PAYE. Must be
    /// non-negative and not exceed <see cref="GrossWage"/>.
    /// </summary>
    public decimal TaxWithheld { get; }

    /// <summary>
    /// Total employee-side deductions (benefits, super/pension, garnishments).
    /// Must be non-negative.
    /// </summary>
    public decimal EmployeeDeductions { get; }

    /// <summary>
    /// Employer-side contributions / payroll taxes (e.g. employer super,
    /// payroll tax, FUTA). These are an ADDITIONAL cost on top of gross wage,
    /// not subtracted from it.
    /// </summary>
    public decimal EmployerLiabilityAmount { get; }

    /// <summary>
    /// Net wage disbursed to the employee (GrossWage - TaxWithheld - EmployeeDeductions).
    /// Computed from the other fields; stored explicitly for downstream ledger lookup.
    /// </summary>
    public decimal NetWage { get; }

    // ── GL account overrides per employee ────────────────────────────────────

    /// <summary>
    /// GL liability account that accumulates employee-withheld tax until
    /// remitted to the tax authority. Resolved from <see cref="PayRun.DefaultTaxWithheldAccountId"/>
    /// unless overridden here.
    /// </summary>
    public GLAccountId TaxWithheldAccountId { get; }

    /// <summary>
    /// GL liability account that accumulates employee deductions until
    /// remitted to the relevant plan. Resolved from <see cref="PayRun.DefaultDeductionPayableAccountId"/>
    /// unless overridden here.
    /// </summary>
    public GLAccountId DeductionPayableAccountId { get; }

    /// <summary>
    /// GL expense account for employer-side liability costs.
    /// Resolved from <see cref="PayRun.DefaultEmployerLiabilityExpenseAccountId"/>
    /// unless overridden here.
    /// </summary>
    public GLAccountId EmployerLiabilityExpenseAccountId { get; }

    /// <summary>
    /// GL liability account for employer-side contributions payable.
    /// Resolved from <see cref="PayRun.DefaultEmployerLiabilityPayableAccountId"/>
    /// unless overridden here.
    /// </summary>
    public GLAccountId EmployerLiabilityPayableAccountId { get; }

    /// <summary>Optional per-line notes visible in the GL entry.</summary>
    public string? Notes { get; init; }

    /// <summary>Constructs and validates a pay run line.</summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="grossWage"/> is zero or negative, when
    /// <paramref name="taxWithheld"/> or <paramref name="employeeDeductions"/> are negative,
    /// or when tax + deductions exceed gross wage.
    /// </exception>
    public PayRunLine(
        EmployeeId employeeId,
        decimal grossWage,
        decimal taxWithheld,
        decimal employeeDeductions,
        decimal employerLiabilityAmount,
        GLAccountId taxWithheldAccountId,
        GLAccountId deductionPayableAccountId,
        GLAccountId employerLiabilityExpenseAccountId,
        GLAccountId employerLiabilityPayableAccountId)
    {
        if (grossWage <= 0m)
            throw new ArgumentException($"Gross wage must be positive (got {grossWage}).", nameof(grossWage));
        if (taxWithheld < 0m)
            throw new ArgumentException($"Tax withheld must be non-negative (got {taxWithheld}).", nameof(taxWithheld));
        if (employeeDeductions < 0m)
            throw new ArgumentException($"Employee deductions must be non-negative (got {employeeDeductions}).", nameof(employeeDeductions));
        if (employerLiabilityAmount < 0m)
            throw new ArgumentException($"Employer liability amount must be non-negative (got {employerLiabilityAmount}).", nameof(employerLiabilityAmount));
        if (taxWithheld + employeeDeductions > grossWage)
            throw new ArgumentException(
                $"Tax withheld ({taxWithheld}) + employee deductions ({employeeDeductions}) " +
                $"cannot exceed gross wage ({grossWage}).",
                nameof(taxWithheld));

        EmployeeId = employeeId;
        GrossWage = grossWage;
        TaxWithheld = taxWithheld;
        EmployeeDeductions = employeeDeductions;
        EmployerLiabilityAmount = employerLiabilityAmount;
        NetWage = grossWage - taxWithheld - employeeDeductions;
        TaxWithheldAccountId = taxWithheldAccountId;
        DeductionPayableAccountId = deductionPayableAccountId;
        EmployerLiabilityExpenseAccountId = employerLiabilityExpenseAccountId;
        EmployerLiabilityPayableAccountId = employerLiabilityPayableAccountId;
    }
}
