using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <summary>
/// T-1048 (DES-0029 ck-6): the approval evidence a subject erasure's recovery pass needs, its completion instant,
/// and its recovery backoff. A row marked before this migration has no evidence and stays retry-completed.
/// </summary>
public partial class _20261002190000_SubjectErasureRecoveryEvidence : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "approved_at_unix_ms",
            table: "search_subject_erasures",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "approving_actors_json",
            table: "search_subject_erasures",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "completed_at_unix_ms",
            table: "search_subject_erasures",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "legal_basis",
            table: "search_subject_erasures",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "next_recovery_at_unix_ms",
            table: "search_subject_erasures",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "recovery_attempts",
            table: "search_subject_erasures",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "approved_at_unix_ms",
            table: "search_subject_erasures");

        migrationBuilder.DropColumn(
            name: "approving_actors_json",
            table: "search_subject_erasures");

        migrationBuilder.DropColumn(
            name: "completed_at_unix_ms",
            table: "search_subject_erasures");

        migrationBuilder.DropColumn(
            name: "legal_basis",
            table: "search_subject_erasures");

        migrationBuilder.DropColumn(
            name: "next_recovery_at_unix_ms",
            table: "search_subject_erasures");

        migrationBuilder.DropColumn(
            name: "recovery_attempts",
            table: "search_subject_erasures");
    }
}
