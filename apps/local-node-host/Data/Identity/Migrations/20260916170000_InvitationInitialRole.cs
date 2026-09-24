using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Harborline.Api.LocalNodeHost.Data.Identity.Migrations;

[DbContext(typeof(NodeLocalInstallationIdentityDbContext))]
[Migration("20260916170000_InvitationInitialRole")]
public sealed class _20260916170000_InvitationInitialRole : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("initial_role", "account_setup_invitations", type: "TEXT",
            maxLength: 256, nullable: false, defaultValue: "tax.roles/member");
        migrationBuilder.AddColumn<string>("initial_role_digest", "account_setup_invitations", type: "TEXT",
            maxLength: 64, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn("initial_role_digest", "account_setup_invitations");
        migrationBuilder.DropColumn("initial_role", "account_setup_invitations");
    }
}
