using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Packs.Migrations;

/// <summary>
/// T-1048: the packs context maps <c>search_audit_outbox</c>, excluded from its migrations (the search context
/// creates and migrates the table), so this migration records the model change and alters no schema.
/// </summary>
[DbContext(typeof(NodeLocalPacksDbContext))]
[Migration("20261002120000_PacksMapsAuditOutbox")]
public sealed class PacksMapsAuditOutbox : Migration
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
