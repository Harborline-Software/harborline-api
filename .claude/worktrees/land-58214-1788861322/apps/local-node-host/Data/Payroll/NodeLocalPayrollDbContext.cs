using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Payroll;

/// <summary>
/// Node-exclusive EF Core <see cref="DbContext"/> for the local-authoritative payroll surface
/// (T4 local-first sweep — the payroll node-flip; ADR 0113 ABSOLUTE local-first). Maps the flat
/// <see cref="EmployeeRecord"/> / <see cref="PayRunRecord"/> / <see cref="PayRunLineRecord"/> /
/// <see cref="FilingObligationRecord"/> persistence rows the Node EF payroll repos translate to and
/// from the block domain models.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate context, same encrypted file (Pattern B).</b> Deliberately NOT
/// <see cref="LocalNodeDbContext"/> and NOT a shared <c>IHarborlineEntityModule</c>. The block carries
/// NO Bridge EF persistence for payroll (the Bridge wires <c>AddInMemoryPayroll()</c> only — there is
/// no <c>Ef*Repository</c> to be parity-checked against), so payroll stays node-exclusive and out of
/// the council C2 both-provider parity check. Opens the SAME SQLCipher-encrypted file keyed through
/// the same <c>SqlCipherConnectionInterceptor</c> (SC-1; no plaintext path). Mirrors
/// <c>NodeLocalBankFeedDbContext</c> / <c>NodeLocalLeaseDbContext</c> exactly.
/// </para>
/// <para>
/// <b>Distinct migration-history table.</b> Records its history in <c>__PayrollMigrationsHistory</c>
/// so the financial store's default <c>__EFMigrationsHistory</c> and the other node-local contexts'
/// tables are untouched (the contexts share one SQLite file; their <c>MigrateAsync</c> calls must
/// not clobber each other's applied-migration records).
/// </para>
/// </remarks>
public sealed class NodeLocalPayrollDbContext : DbContext
{
    /// <summary>Dedicated migration-history table name for this context.</summary>
    public const string MigrationsHistoryTableName = "__PayrollMigrationsHistory";

    /// <summary>Initialises a new instance of <see cref="NodeLocalPayrollDbContext"/>.</summary>
    public NodeLocalPayrollDbContext(DbContextOptions<NodeLocalPayrollDbContext> options)
        : base(options)
    {
    }

    /// <summary>The node-local employees.</summary>
    public DbSet<EmployeeRecord> Employees => Set<EmployeeRecord>();

    /// <summary>The node-local pay runs.</summary>
    public DbSet<PayRunRecord> PayRuns => Set<PayRunRecord>();

    /// <summary>The node-local pay-run lines (child of <see cref="PayRuns"/>).</summary>
    public DbSet<PayRunLineRecord> PayRunLines => Set<PayRunLineRecord>();

    /// <summary>The node-local filing obligations.</summary>
    public DbSet<FilingObligationRecord> FilingObligations => Set<FilingObligationRecord>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<EmployeeRecord>(e =>
        {
            e.ToTable("payroll_employees");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(r => r.PartyId).HasColumnName("party_id");
            e.Property(r => r.DisplayName).HasColumnName("display_name");
            e.Property(r => r.CostCentreId).HasColumnName("cost_centre_id");
            e.Property(r => r.WageExpenseAccountId).HasColumnName("wage_expense_account_id");
            e.Property(r => r.WagesPayableAccountId).HasColumnName("wages_payable_account_id");
            e.Property(r => r.IsActive).HasColumnName("is_active");
            e.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(r => r.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.HasIndex(r => r.TenantId);
            e.HasIndex(r => new { r.TenantId, r.IsActive });
        });

        modelBuilder.Entity<PayRunRecord>(e =>
        {
            e.ToTable("payroll_pay_runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(r => r.Label).HasColumnName("label");
            e.Property(r => r.PeriodStart).HasColumnName("period_start");
            e.Property(r => r.PeriodEnd).HasColumnName("period_end");
            e.Property(r => r.PostingDate).HasColumnName("posting_date");
            e.Property(r => r.Status).HasColumnName("status");
            e.Property(r => r.DefaultTaxWithheldAccountId).HasColumnName("default_tax_withheld_account_id");
            e.Property(r => r.DefaultDeductionPayableAccountId).HasColumnName("default_deduction_payable_account_id");
            e.Property(r => r.DefaultEmployerLiabilityExpenseAccountId).HasColumnName("default_employer_liability_expense_account_id");
            e.Property(r => r.DefaultEmployerLiabilityPayableAccountId).HasColumnName("default_employer_liability_payable_account_id");
            e.Property(r => r.JournalEntryId).HasColumnName("journal_entry_id");
            e.Property(r => r.ReversalEntryId).HasColumnName("reversal_entry_id");
            e.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc");
            e.Property(r => r.UpdatedAtUtc).HasColumnName("updated_at_utc");
            e.Property(r => r.Version).HasColumnName("version");
            e.HasIndex(r => r.TenantId);
            e.HasIndex(r => new { r.TenantId, r.PostingDate });
            e.HasMany(r => r.Lines)
                .WithOne()
                .HasForeignKey(l => l.PayRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PayRunLineRecord>(e =>
        {
            e.ToTable("payroll_pay_run_lines");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(r => r.PayRunId).HasColumnName("pay_run_id").IsRequired();
            e.Property(r => r.Ordinal).HasColumnName("ordinal");
            e.Property(r => r.EmployeeId).HasColumnName("employee_id");
            e.Property(r => r.GrossWage).HasColumnName("gross_wage");
            e.Property(r => r.TaxWithheld).HasColumnName("tax_withheld");
            e.Property(r => r.EmployeeDeductions).HasColumnName("employee_deductions");
            e.Property(r => r.EmployerLiabilityAmount).HasColumnName("employer_liability_amount");
            e.Property(r => r.TaxWithheldAccountId).HasColumnName("tax_withheld_account_id");
            e.Property(r => r.DeductionPayableAccountId).HasColumnName("deduction_payable_account_id");
            e.Property(r => r.EmployerLiabilityExpenseAccountId).HasColumnName("employer_liability_expense_account_id");
            e.Property(r => r.EmployerLiabilityPayableAccountId).HasColumnName("employer_liability_payable_account_id");
            e.Property(r => r.Notes).HasColumnName("notes");
            e.HasIndex(r => r.PayRunId);
        });

        modelBuilder.Entity<FilingObligationRecord>(e =>
        {
            e.ToTable("payroll_filing_obligations");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
            e.Property(r => r.Label).HasColumnName("label");
            e.Property(r => r.DueDate).HasColumnName("due_date");
            e.Property(r => r.PeriodStart).HasColumnName("period_start");
            e.Property(r => r.PeriodEnd).HasColumnName("period_end");
            e.Property(r => r.JurisdictionCode).HasColumnName("jurisdiction_code");
            e.Property(r => r.SourcePayRunId).HasColumnName("source_pay_run_id");
            e.Property(r => r.IsComplete).HasColumnName("is_complete");
            e.Property(r => r.Notes).HasColumnName("notes");
            e.Property(r => r.CreatedAtUtc).HasColumnName("created_at_utc");
            e.HasIndex(r => r.TenantId);
            e.HasIndex(r => new { r.TenantId, r.DueDate });
        });
    }
}
