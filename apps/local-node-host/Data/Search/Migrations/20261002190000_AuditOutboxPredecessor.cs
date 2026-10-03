using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <summary>Preserves a committed audit ceremony's delivery order even when its entries share an instant.</summary>
[DbContext(typeof(NodeLocalSearchDbContext))]
[Migration("20261002190000_AuditOutboxPredecessor")]
public sealed class AuditOutboxPredecessor : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<string>(
        name: "predecessor_audit_id", table: "search_audit_outbox", type: "TEXT", maxLength: 64, nullable: true);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn(
        name: "predecessor_audit_id", table: "search_audit_outbox");
}
