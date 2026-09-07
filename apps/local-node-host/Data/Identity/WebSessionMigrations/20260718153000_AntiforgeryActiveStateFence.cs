using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Identity.WebSessionMigrations;

/// <inheritdoc />
[DbContext(typeof(NodeLocalWebSessionDbContext))]
[Migration("20260718153000_AntiforgeryActiveStateFence")]
public partial class _20260718153000_AntiforgeryActiveStateFence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_web_antiforgery_state_subject",
            table: "web_antiforgery_states");
        migrationBuilder.CreateIndex(
            name: "ux_web_antiforgery_state_active_subject",
            table: "web_antiforgery_states",
            columns: new[] { "audience", "subject_correlation_id" },
            unique: true,
            filter: "consumed_at_utc IS NULL AND revoked_at_utc IS NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ux_web_antiforgery_state_active_subject",
            table: "web_antiforgery_states");
        migrationBuilder.CreateIndex(
            name: "ix_web_antiforgery_state_subject",
            table: "web_antiforgery_states",
            columns: new[] { "audience", "subject_correlation_id" });
    }
}
