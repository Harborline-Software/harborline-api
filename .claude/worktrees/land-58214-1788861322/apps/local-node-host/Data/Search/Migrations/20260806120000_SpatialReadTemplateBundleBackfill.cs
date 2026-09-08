using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <summary>
/// Data-only backfill — the one-time <c>spatial:read</c> re-grant for durable Member/Admin edges that still
/// carry the exact pre-widening template bundle (CIC ruling 2026-08-06, card 3789).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> <c>PermissionCompositions</c> are seed TEMPLATES: widening the <c>member</c>/<c>admin</c>
/// bundles with <c>spatial:read</c> (card 3777 / PR 3784) reaches NEW admissions only. An upgraded
/// installation's existing durable edges keep their persisted set and would stay fail-closed on the spatial
/// surface until re-granted — but the CIC ruling named existing Members (partner field crews) as the
/// beneficiary, so this migration performs the one-shot re-grant. It is a DATA migration, not a re-seed
/// rule: "templates are seeds, independently mutable" stays intact.
/// </para>
/// <para>
/// <b>What is touched — exact-template edges only.</b> A row qualifies iff its <c>permissions_json</c> is
/// EXACTLY the canonical serialized pre-widening <c>member</c> or <c>admin</c> bundle. The only production
/// writer (<c>NodeEfGrantStore.ToRow</c>) always persists the canonical form — <c>PermissionSet.Permissions</c>
/// is ordinal-sorted and <c>JsonSerializer</c> emits compact JSON — so every store-written template edge
/// matches, while ANY hand-tuned set (grown, shrunk, or otherwise re-shaped via <c>grant:permissions</c>)
/// differs from the literal and is left untouched. A non-canonically-ordered json blob could only come from
/// out-of-band tampering; leaving it untouched is the conservative direction the ruling requires.
/// </para>
/// <para>
/// <b>What is deliberately NOT touched.</b> (1) REVOKED edges — rewriting a revoked grant's set would alter
/// what the member held at revocation, which is audit-relevant history; a revoked grant confers nothing, so
/// widening it heals nobody. (2) Rows with NULL <c>permissions_json</c> — those are legacy-role rows
/// interpreted through <c>PermissionCompositions.ForLegacyShipRoleNames</c> at READ time, so they follow the
/// live (widened) templates automatically and need no backfill. (3) Viewer-shaped or any other set — only
/// the two named bundles ever match.
/// </para>
/// <para>
/// <b>Idempotent by construction.</b> A widened row no longer equals the pre-widening literal, so a re-run
/// (or a restored database replaying the script) selects nothing and changes nothing. EF additionally runs
/// an applied migration only once; the property holds even without that guard and the test suite proves it
/// by executing <see cref="BackfillSql"/> twice.
/// </para>
/// <para>
/// <b>owner_version and authorization_epoch are deliberately NOT advanced (deep-review F1, ruled).</b>
/// Both values are MIRRORED into the installation-identity membership pins
/// (<c>TenantMembershipDocument.GrantOwnerVersion</c>/<c>AuthorizationEpoch</c>), and
/// <c>LiveTenantMembershipAuthorityAdmission</c> refuses on ANY mismatch — advancing the SQL half alone
/// would break sign-in for every backfilled member (founder and wire enrollees included), with no
/// admin-surface escape hatch because the repair surface authenticates through the same fence.
/// <c>NodeEfGrantStore.SaveAsync</c> never runs alone: its production callers advance the membership
/// copies in the same coordination command, which a migration on ONE context cannot do. The fences exist
/// to detect concurrent writers; a single-threaded startup migration has none, and
/// <c>SelectedSessionPermissionResolver</c> re-reads the grant's permission set per request, so the widened
/// set is live without any epoch movement. The precondition test stands up BOTH contexts and proves a
/// backfilled principal still authenticates.
/// </para>
/// <para>
/// <b>Not a signed-record rewrite.</b> The atlas roster's SIGNED artifact is the admission/revocation chain
/// (identity + key bindings); the live permission set is mutable-by-replacement state OUTSIDE the signed
/// envelope, and the roster's v1 store is in-memory besides. The durable permission edge lives here, in
/// <c>search_grants.permissions_json</c>, which no signature covers — so a data migration through the
/// store's own tables violates no signing discipline.
/// </para>
/// </remarks>
[DbContext(typeof(NodeLocalSearchDbContext))]
[Migration("20260806120000_SpatialReadTemplateBundleBackfill")]
public sealed class _20260806120000_SpatialReadTemplateBundleBackfill : Migration
{
    /// <summary>The permission this backfill adds — frozen as a literal: a migration must never read the
    /// live vocabulary, which keeps moving after this migration is history.</summary>
    internal const string SpatialRead = "spatial:read";

    /// <summary>
    /// The pre-widening <c>member</c> template bundle, frozen in its canonical persisted form (ordinal-sorted,
    /// compact JSON — exactly what <c>NodeEfGrantStore</c> wrote for a template-seeded Member edge before
    /// PR 3784). Frozen LITERALS, not references to <c>PermissionCompositions</c>: the live templates keep
    /// mutating after this migration is history, and a migration that read them would silently re-target.
    /// </summary>
    internal const string PreWideningMemberBundleJson =
        "[\"calendar:archive\",\"calendar:create\",\"calendar:read\",\"calendar:write\","
        + "\"comms:append\",\"comms:read\","
        + "\"contacts:archive\",\"contacts:create\",\"contacts:read\",\"contacts:write\","
        + "\"gl:read\",\"records:read\",\"records:write\",\"scheduling:read\","
        + "\"stories:archive\",\"stories:create\",\"stories:read\",\"stories:write\"]";

    /// <summary>The member bundle after widening — the frozen pre-widening set plus <c>spatial:read</c>, in
    /// the same canonical form.</summary>
    internal const string WidenedMemberBundleJson =
        "[\"calendar:archive\",\"calendar:create\",\"calendar:read\",\"calendar:write\","
        + "\"comms:append\",\"comms:read\","
        + "\"contacts:archive\",\"contacts:create\",\"contacts:read\",\"contacts:write\","
        + "\"gl:read\",\"records:read\",\"records:write\",\"scheduling:read\",\"spatial:read\","
        + "\"stories:archive\",\"stories:create\",\"stories:read\",\"stories:write\"]";

    /// <summary>The pre-widening <c>admin</c> template bundle, canonical persisted form (see
    /// <see cref="PreWideningMemberBundleJson"/> for why these are frozen literals).</summary>
    internal const string PreWideningAdminBundleJson =
        "[\"audit:read\","
        + "\"calendar:archive\",\"calendar:create\",\"calendar:read\",\"calendar:write\","
        + "\"comms:append\",\"comms:read\","
        + "\"contacts:archive\",\"contacts:create\",\"contacts:read\",\"contacts:write\","
        + "\"gl:post\",\"gl:read\",\"ledger:post\","
        + "\"members:admit\",\"members:manage\",\"members:revoke\",\"members:set-role\","
        + "\"org:branding:write\",\"org:manage-settings\","
        + "\"packages:author\",\"packages:operate\",\"provider:read-config\","
        + "\"records:read\",\"records:write\","
        + "\"scheduling:author\",\"scheduling:operate\",\"scheduling:read\","
        + "\"stories:archive\",\"stories:create\",\"stories:read\",\"stories:write\"]";

    /// <summary>The admin bundle after widening — the frozen pre-widening set plus <c>spatial:read</c>.</summary>
    internal const string WidenedAdminBundleJson =
        "[\"audit:read\","
        + "\"calendar:archive\",\"calendar:create\",\"calendar:read\",\"calendar:write\","
        + "\"comms:append\",\"comms:read\","
        + "\"contacts:archive\",\"contacts:create\",\"contacts:read\",\"contacts:write\","
        + "\"gl:post\",\"gl:read\",\"ledger:post\","
        + "\"members:admit\",\"members:manage\",\"members:revoke\",\"members:set-role\","
        + "\"org:branding:write\",\"org:manage-settings\","
        + "\"packages:author\",\"packages:operate\",\"provider:read-config\","
        + "\"records:read\",\"records:write\","
        + "\"scheduling:author\",\"scheduling:operate\",\"scheduling:read\",\"spatial:read\","
        + "\"stories:archive\",\"stories:create\",\"stories:read\",\"stories:write\"]";

    /// <summary>
    /// The full backfill script. Internal so the test suite can execute it a second time against an
    /// already-backfilled database and prove the idempotency claim by work product, not by prose.
    /// One statement, permissions_json ONLY — no owner_version movement, no epoch movement (see the class
    /// remarks: those values are mirrored into the membership pins, which refuse on any mismatch).
    /// </summary>
    internal const string BackfillSql = $"""
        UPDATE search_grants
        SET permissions_json = CASE permissions_json
                WHEN '{PreWideningMemberBundleJson}' THEN '{WidenedMemberBundleJson}'
                ELSE '{WidenedAdminBundleJson}'
            END
        WHERE revoked_at_unix_ms IS NULL
          AND permissions_json IN ('{PreWideningMemberBundleJson}', '{PreWideningAdminBundleJson}');
        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(BackfillSql);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally empty. After the Up, a widened row is indistinguishable from an edge a legitimate
        // grant:permissions write widened afterwards, so narrowing on the way down would destroy live
        // authority state (the 20260802103000 precedent). Schema reversal belongs to earlier migrations;
        // this one has no schema.
    }
}
