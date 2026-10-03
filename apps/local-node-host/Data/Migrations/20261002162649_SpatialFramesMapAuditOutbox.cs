using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Migrations;

/// <summary>
/// T-1048: the main context maps <c>search_audit_outbox</c>, excluded from its migrations (the search context
/// creates and migrates the table), so this migration records the model change and alters no schema.
/// </summary>
public partial class _20261002162649_SpatialFramesMapAuditOutbox : Migration
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
