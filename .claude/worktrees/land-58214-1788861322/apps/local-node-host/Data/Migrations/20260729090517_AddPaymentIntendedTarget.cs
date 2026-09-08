using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations;

/// <inheritdoc />
public partial class _20260729090517_AddPaymentIntendedTarget : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "IntendedTargetId",
            table: "payments",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "IntendedTargetType",
            table: "payments",
            type: "TEXT",
            maxLength: 16,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ix_payments_tenant_intended_target",
            table: "payments",
            columns: new[] { "TenantId", "IntendedTargetType", "IntendedTargetId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_payments_tenant_intended_target",
            table: "payments");

        migrationBuilder.DropColumn(
            name: "IntendedTargetId",
            table: "payments");

        migrationBuilder.DropColumn(
            name: "IntendedTargetType",
            table: "payments");
    }
}
