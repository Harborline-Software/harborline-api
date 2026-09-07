using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;

namespace Harborline.Api.Blocks.Payroll.Models;

/// <summary>
/// An employee within the payroll ledger. Carries the party reference
/// from <c>blocks-people-foundation</c> plus the payroll-specific
/// tracking dimension that maps wage postings to the GL cost centre.
/// </summary>
/// <remarks>
/// v1 scope: employee record is a thin envelope over a <see cref="PartyId"/>
/// that adds the cost-centre dimension required for pay-run journal mapping.
/// Employment classification, pay-rate, and withholding configuration are
/// v2 surfaces — not stored here.
/// </remarks>
public sealed record Employee : IMustHaveTenant
{
    /// <summary>Unique employee identifier within this tenant.</summary>
    public EmployeeId Id { get; }

    /// <summary>Tenant scope. Required per <see cref="IMustHaveTenant"/>.</summary>
    public TenantId TenantId { get; }

    /// <summary>
    /// Reference to the underlying party record in
    /// <c>blocks-people-foundation</c>. Uniquely identifies the human
    /// behind this employee record.
    /// </summary>
    public PartyId PartyId { get; }

    /// <summary>
    /// Display name — copied from the party record at creation time so
    /// pay-run UIs can render names without resolving the people store.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Optional GL cost-centre dimension. When set, every pay-run journal
    /// entry line posted for this employee carries this
    /// <see cref="ClassificationId"/> as a dimensional tag so per-department
    /// wage reporting works without account proliferation.
    /// </summary>
    public ClassificationId? CostCentreId { get; init; }

    /// <summary>
    /// The GL expense account that receives the gross-wage debit for this
    /// employee. Typically a Wages Expense account within the tenant's
    /// chart of accounts.
    /// </summary>
    public GLAccountId WageExpenseAccountId { get; }

    /// <summary>
    /// The GL liability account that accumulates wages payable to this
    /// employee between the run date and the bank disbursement date.
    /// </summary>
    public GLAccountId WagesPayableAccountId { get; }

    /// <summary>Wall-clock instant at which this employee record was created.</summary>
    public Instant CreatedAtUtc { get; }

    /// <summary>
    /// Wall-clock instant of the last update, or null if never updated
    /// after initial creation.
    /// </summary>
    public Instant? UpdatedAtUtc { get; init; }

    /// <summary>
    /// Whether this employee record is active. Inactive employees
    /// appear in historical pay runs but are excluded from new pay-run
    /// selection lists.
    /// </summary>
    public bool IsActive { get; init; } = true;

    /// <summary>
    /// Creates a new employee record.
    /// </summary>
    public Employee(
        EmployeeId id,
        TenantId tenantId,
        PartyId partyId,
        string displayName,
        GLAccountId wageExpenseAccountId,
        GLAccountId wagesPayableAccountId,
        Instant createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Employee display name is required.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(wageExpenseAccountId.Value))
            throw new ArgumentException("Wage expense account id is required.", nameof(wageExpenseAccountId));
        if (string.IsNullOrWhiteSpace(wagesPayableAccountId.Value))
            throw new ArgumentException("Wages payable account id is required.", nameof(wagesPayableAccountId));

        Id = id;
        TenantId = tenantId;
        PartyId = partyId;
        DisplayName = displayName;
        WageExpenseAccountId = wageExpenseAccountId;
        WagesPayableAccountId = wagesPayableAccountId;
        CreatedAtUtc = createdAtUtc;
    }

    /// <summary>
    /// Factory method for creating a new employee record with a generated id
    /// and current timestamp.
    /// </summary>
    public static Employee Create(
        TenantId tenantId,
        PartyId partyId,
        string displayName,
        GLAccountId wageExpenseAccountId,
        GLAccountId wagesPayableAccountId,
        Instant createdAtUtc,
        ClassificationId? costCentreId = null)
    {
        return new Employee(
            id: EmployeeId.NewId(),
            tenantId: tenantId,
            partyId: partyId,
            displayName: displayName,
            wageExpenseAccountId: wageExpenseAccountId,
            wagesPayableAccountId: wagesPayableAccountId,
            createdAtUtc: createdAtUtc)
        {
            CostCentreId = costCentreId,
        };
    }
}
