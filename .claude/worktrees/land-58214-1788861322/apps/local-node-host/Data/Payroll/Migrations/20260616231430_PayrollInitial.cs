using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Payroll.Migrations
{
    /// <inheritdoc />
    public partial class _20260616231430_PayrollInitial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payroll_employees",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    party_id = table.Column<string>(type: "TEXT", nullable: false),
                    display_name = table.Column<string>(type: "TEXT", nullable: false),
                    cost_centre_id = table.Column<string>(type: "TEXT", nullable: true),
                    wage_expense_account_id = table.Column<string>(type: "TEXT", nullable: false),
                    wages_payable_account_id = table.Column<string>(type: "TEXT", nullable: false),
                    is_active = table.Column<bool>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_employees", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_filing_obligations",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: false),
                    due_date = table.Column<string>(type: "TEXT", nullable: false),
                    period_start = table.Column<string>(type: "TEXT", nullable: true),
                    period_end = table.Column<string>(type: "TEXT", nullable: true),
                    jurisdiction_code = table.Column<string>(type: "TEXT", nullable: true),
                    source_pay_run_id = table.Column<string>(type: "TEXT", nullable: true),
                    is_complete = table.Column<bool>(type: "INTEGER", nullable: false),
                    notes = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_filing_obligations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_pay_runs",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", nullable: false),
                    tenant_id = table.Column<string>(type: "TEXT", nullable: false),
                    label = table.Column<string>(type: "TEXT", nullable: false),
                    period_start = table.Column<string>(type: "TEXT", nullable: false),
                    period_end = table.Column<string>(type: "TEXT", nullable: false),
                    posting_date = table.Column<string>(type: "TEXT", nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    default_tax_withheld_account_id = table.Column<string>(type: "TEXT", nullable: false),
                    default_deduction_payable_account_id = table.Column<string>(type: "TEXT", nullable: false),
                    default_employer_liability_expense_account_id = table.Column<string>(type: "TEXT", nullable: false),
                    default_employer_liability_payable_account_id = table.Column<string>(type: "TEXT", nullable: false),
                    journal_entry_id = table.Column<string>(type: "TEXT", nullable: true),
                    reversal_entry_id = table.Column<string>(type: "TEXT", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_pay_runs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "payroll_pay_run_lines",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    pay_run_id = table.Column<string>(type: "TEXT", nullable: false),
                    ordinal = table.Column<int>(type: "INTEGER", nullable: false),
                    employee_id = table.Column<string>(type: "TEXT", nullable: false),
                    gross_wage = table.Column<decimal>(type: "TEXT", nullable: false),
                    tax_withheld = table.Column<decimal>(type: "TEXT", nullable: false),
                    employee_deductions = table.Column<decimal>(type: "TEXT", nullable: false),
                    employer_liability_amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    tax_withheld_account_id = table.Column<string>(type: "TEXT", nullable: false),
                    deduction_payable_account_id = table.Column<string>(type: "TEXT", nullable: false),
                    employer_liability_expense_account_id = table.Column<string>(type: "TEXT", nullable: false),
                    employer_liability_payable_account_id = table.Column<string>(type: "TEXT", nullable: false),
                    notes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payroll_pay_run_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_payroll_pay_run_lines_payroll_pay_runs_pay_run_id",
                        column: x => x.pay_run_id,
                        principalTable: "payroll_pay_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_employees_tenant_id",
                table: "payroll_employees",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_payroll_employees_tenant_id_is_active",
                table: "payroll_employees",
                columns: new[] { "tenant_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_filing_obligations_tenant_id",
                table: "payroll_filing_obligations",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_payroll_filing_obligations_tenant_id_due_date",
                table: "payroll_filing_obligations",
                columns: new[] { "tenant_id", "due_date" });

            migrationBuilder.CreateIndex(
                name: "IX_payroll_pay_run_lines_pay_run_id",
                table: "payroll_pay_run_lines",
                column: "pay_run_id");

            migrationBuilder.CreateIndex(
                name: "IX_payroll_pay_runs_tenant_id",
                table: "payroll_pay_runs",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_payroll_pay_runs_tenant_id_posting_date",
                table: "payroll_pay_runs",
                columns: new[] { "tenant_id", "posting_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payroll_employees");

            migrationBuilder.DropTable(
                name: "payroll_filing_obligations");

            migrationBuilder.DropTable(
                name: "payroll_pay_run_lines");

            migrationBuilder.DropTable(
                name: "payroll_pay_runs");
        }
    }
}
