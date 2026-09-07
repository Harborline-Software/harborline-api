using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations;

/// <inheritdoc />
public partial class _20260716171804_AddPaymentApplicationReversalEvidence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ReversedAtUtc",
            table: "payment_applications",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ReversedByApplicationId",
            table: "payment_applications",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ReversesApplicationId",
            table: "payment_applications",
            type: "TEXT",
            maxLength: 128,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "ux_payment_applications_tenant_reverses",
            table: "payment_applications",
            columns: new[] { "TenantId", "ReversesApplicationId" },
            unique: true,
            filter: ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL"
                ? "\"ReversesApplicationId\" IS NOT NULL"
                : null);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_payment_applications_tenant_reverses",
            table: "payment_applications");

        migrationBuilder.DropColumn(
            name: "ReversedAtUtc",
            table: "payment_applications");

        migrationBuilder.DropColumn(
            name: "ReversedByApplicationId",
            table: "payment_applications");

        migrationBuilder.DropColumn(
            name: "ReversesApplicationId",
            table: "payment_applications");
    }
}
