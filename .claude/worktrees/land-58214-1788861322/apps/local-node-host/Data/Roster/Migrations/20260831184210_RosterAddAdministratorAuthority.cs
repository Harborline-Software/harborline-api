using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Roster.Migrations;

/// <inheritdoc />
public partial class _20260831184210_RosterAddAdministratorAuthority : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "administrator_authority",
            columns: table => new
            {
                sequence = table.Column<long>(type: "INTEGER", nullable: false),
                team_id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                party_id = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                @event = table.Column<int>(name: "event", type: "INTEGER", nullable: false),
                provenance = table.Column<int>(type: "INTEGER", nullable: false),
                member_public_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, defaultValue: ""),
                admission_signature = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, defaultValue: ""),
                admitted_by_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, defaultValue: ""),
                admitted_by_party = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, defaultValue: ""),
                is_genesis_admission = table.Column<bool>(type: "INTEGER", nullable: false),
                occurred_at = table.Column<long>(type: "INTEGER", nullable: false),
                expires_at = table.Column<long>(type: "INTEGER", nullable: true),
                reason = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, defaultValue: ""),
                prev_hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_administrator_authority", x => x.sequence);
            });

        migrationBuilder.CreateIndex(
            name: "IX_administrator_authority_team_id_party_id",
            table: "administrator_authority",
            columns: new[] { "team_id", "party_id" });
    }

    /// <inheritdoc />
    /// <remarks>
    /// KNOWN AND DELIBERATE, recorded rather than silently accepted: dropping this table clears the
    /// installer seal and re-arms the installer, and repointing <c>LocalNode:DataDirectory</c> at an empty
    /// directory does the same. Both are inside the local-software trust boundary — an actor who can run
    /// <c>ef database update</c> against the store, or rewrite the node's configuration, can already read
    /// every stored credential — so neither is a privilege escalation. What they DO falsify is the claim
    /// that the seal is "a fact about append-only history": it is a fact about THIS store, and the store is
    /// replaceable. Making the seal survive a store swap needs a durable anchor outside the database (the
    /// install identity is the obvious candidate), which is a design change to the install footprint and
    /// belongs in its own ticket, not in a data migration's Down.
    /// </remarks>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "administrator_authority");
    }
}
