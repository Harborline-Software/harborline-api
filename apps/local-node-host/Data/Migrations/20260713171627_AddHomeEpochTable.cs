using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations;

/// <inheritdoc />
public partial class _20260713171627_AddHomeEpochTable : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "home_epochs",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                EpochNumber = table.Column<long>(type: "INTEGER", nullable: false),
                PreviousEpochNumber = table.Column<long>(type: "INTEGER", nullable: false),
                HomeDeviceId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                PromotionKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                IssuedAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false),
                Nonce = table.Column<Guid>(type: "TEXT", nullable: false),
                IssuerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Signature = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                CoApproverIssuerId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                CoApproverSignature = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_home_epochs", x => new { x.TenantId, x.EpochNumber });
            });

        migrationBuilder.CreateIndex(
            name: "ix_home_epochs_tenant_epoch",
            table: "home_epochs",
            columns: new[] { "TenantId", "EpochNumber" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "home_epochs");
    }
}
