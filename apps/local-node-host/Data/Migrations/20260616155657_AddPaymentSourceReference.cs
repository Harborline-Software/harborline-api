using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations
{
    /// <inheritdoc />
    public partial class _20260616155657_AddPaymentSourceReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceReference",
                table: "payments",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_payments_tenant_source_ref",
                table: "payments",
                columns: new[] { "TenantId", "SourceReference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_payments_tenant_source_ref",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "SourceReference",
                table: "payments");
        }
    }
}
