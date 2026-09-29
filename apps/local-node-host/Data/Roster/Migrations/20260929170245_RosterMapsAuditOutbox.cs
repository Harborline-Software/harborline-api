using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations;

/// <summary>
/// T-986: the roster context maps <c>search_audit_outbox</c>, excluded from its migrations (the search context
/// creates and migrates the table), so this migration records the model change and alters no schema.
/// </summary>
[DbContext(typeof(NodeLocalRosterDbContext))]
[Migration("20260929170245_RosterMapsAuditOutbox")]
public sealed class RosterMapsAuditOutbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
