using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations;

/// <inheritdoc />
public partial class _20260805220357_AddSpatialFrameTables : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "spatial_frame_descriptors",
            columns: table => new
            {
                TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                AnchorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                FrameCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                FrameEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                AxisConvention = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                OriginDescription = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                LengthUnit = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                GeoreferenceJson = table.Column<string>(type: "TEXT", nullable: true),
                Issuer = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                Nonce = table.Column<Guid>(type: "TEXT", nullable: false),
                IssuedAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false),
                Signature = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                HomeDeviceId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                GrantingHomeEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                PreviousEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_spatial_frame_descriptors", x => new { x.TenantId, x.AnchorId, x.FrameCode, x.FrameEpoch });
            });

        migrationBuilder.CreateTable(
            name: "spatial_frame_quarantine",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                TenantId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                AnchorId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                FrameCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                AttemptedEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                TipEpochAtDetection = table.Column<long>(type: "INTEGER", nullable: false),
                Reason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                AxisConvention = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                OriginDescription = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                LengthUnit = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                GeoreferenceJson = table.Column<string>(type: "TEXT", nullable: true),
                DetectedAt = table.Column<string>(type: "TEXT", maxLength: 33, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_spatial_frame_quarantine", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_spatial_frame_descriptors_series_epoch",
            table: "spatial_frame_descriptors",
            columns: new[] { "TenantId", "AnchorId", "FrameCode", "FrameEpoch" });

        migrationBuilder.CreateIndex(
            name: "ix_spatial_frame_quarantine_series",
            table: "spatial_frame_quarantine",
            columns: new[] { "TenantId", "AnchorId", "FrameCode" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "spatial_frame_descriptors");

        migrationBuilder.DropTable(
            name: "spatial_frame_quarantine");
    }
}
