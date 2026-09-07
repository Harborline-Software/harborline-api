using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <summary>
/// Data-only backfill — the one-time <c>forms:author</c> re-grant for durable Admin/Owner edges that still
/// carry the exact pre-widening template bundle (ticket 151; mechanism per the CIC 3789 spatial-read
/// precedent, <see cref="_20260806120000_SpatialReadTemplateBundleBackfill"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> <c>PermissionCompositions</c> are seed TEMPLATES: ticket 151 minted <c>forms:author</c> into
/// the <c>admin</c> and <c>owner</c> bundles, which reaches NEW admissions only (CIC card 3789). An
/// upgraded installation's existing durable edges keep their persisted set — so every pre-upgrade admin
/// and owner would lose form authoring on the permission-snapshot planes the moment the new
/// <c>forms:author</c> route gate ships. This migration performs the one-shot re-grant.
/// </para>
/// <para>
/// <b>What is touched — exact-template edges only</b> (the spatial-read precedent verbatim): a row
/// qualifies iff its <c>permissions_json</c> is EXACTLY the canonical serialized pre-widening
/// <c>admin</c> or <c>owner</c> bundle. Any hand-tuned set differs from the literal and is left
/// untouched; revoked edges and NULL (legacy-role) rows are untouched for the reasons the precedent
/// records. <c>member</c>/<c>viewer</c> never gained <c>forms:author</c>, so their bundles never match.
/// A pre-3784 OWNER edge (one that also predates the spatial-read widening and was never backfilled by
/// the CIC-scoped 3789 migration) does not match the pre-widening literal here either and stays
/// untouched — the conservative direction; widening it is a policy call this data migration does not
/// make.
/// </para>
/// <para>
/// <b>Idempotent by construction</b>, and <b>owner_version / authorization_epoch are deliberately NOT
/// advanced</b> — both properties are inherited from the precedent unchanged (see the F1 discussion on
/// <see cref="_20260806120000_SpatialReadTemplateBundleBackfill"/>): the values are mirrored into the
/// membership pins and any movement here would refuse every backfilled member at sign-in.
/// </para>
/// </remarks>
[DbContext(typeof(NodeLocalSearchDbContext))]
[Migration("20260830130000_FormsAuthorTemplateBundleBackfill")]
public sealed class _20260830130000_FormsAuthorTemplateBundleBackfill : Migration
{
    /// <summary>The permission this backfill adds — frozen as a literal: a migration must never read the
    /// live vocabulary, which keeps moving after this migration is history.</summary>
    internal const string FormsAuthor = "forms:author";

    /// <summary>
    /// The pre-widening <c>admin</c> template bundle, frozen in its canonical persisted form
    /// (ordinal-sorted, compact JSON — exactly what <c>NodeEfGrantStore</c> wrote for a template-seeded
    /// Admin edge after the spatial-read widening and before ticket 151). Frozen LITERALS, not references
    /// to <c>PermissionCompositions</c>: the live templates keep mutating after this migration is history,
    /// and a migration that read them would silently re-target.
    /// </summary>
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
        + "\"scheduling:author\",\"scheduling:operate\",\"scheduling:read\",\"spatial:read\","
        + "\"stories:archive\",\"stories:create\",\"stories:read\",\"stories:write\"]";

    /// <summary>The admin bundle after widening — the frozen pre-widening set plus <c>forms:author</c>,
    /// in the same canonical form.</summary>
    internal const string WidenedAdminBundleJson =
        "[\"audit:read\","
        + "\"calendar:archive\",\"calendar:create\",\"calendar:read\",\"calendar:write\","
        + "\"comms:append\",\"comms:read\","
        + "\"contacts:archive\",\"contacts:create\",\"contacts:read\",\"contacts:write\",\"forms:author\","
        + "\"gl:post\",\"gl:read\",\"ledger:post\","
        + "\"members:admit\",\"members:manage\",\"members:revoke\",\"members:set-role\","
        + "\"org:branding:write\",\"org:manage-settings\","
        + "\"packages:author\",\"packages:operate\",\"provider:read-config\","
        + "\"records:read\",\"records:write\","
        + "\"scheduling:author\",\"scheduling:operate\",\"scheduling:read\",\"spatial:read\","
        + "\"stories:archive\",\"stories:create\",\"stories:read\",\"stories:write\"]";

    /// <summary>The pre-widening <c>owner</c> template bundle, canonical persisted form (see
    /// <see cref="PreWideningAdminBundleJson"/> for why these are frozen literals).</summary>
    internal const string PreWideningOwnerBundleJson =
        "[\"audit:read\","
        + "\"calendar:archive\",\"calendar:create\",\"calendar:read\",\"calendar:write\","
        + "\"comms:append\",\"comms:read\","
        + "\"contacts:archive\",\"contacts:create\",\"contacts:read\",\"contacts:write\","
        + "\"gl:post\",\"gl:read\",\"grant:permissions\",\"ledger:post\","
        + "\"members:admit\",\"members:manage\",\"members:revoke\",\"members:set-role\","
        + "\"org:branding:write\",\"org:manage-settings\",\"org:transfer-ownership\","
        + "\"packages:author\",\"packages:operate\","
        + "\"provider:configure-bank-feed\",\"provider:configure-email\",\"provider:configure-identity\","
        + "\"provider:configure-payments\",\"provider:configure-storage\",\"provider:configure-telemetry\","
        + "\"provider:read-config\","
        + "\"records:read\",\"records:write\","
        + "\"scheduling:author\",\"scheduling:operate\",\"scheduling:read\",\"spatial:read\","
        + "\"stories:archive\",\"stories:create\",\"stories:read\",\"stories:write\","
        + "\"telemetry:export\"]";

    /// <summary>The owner bundle after widening — the frozen pre-widening set plus <c>forms:author</c>.</summary>
    internal const string WidenedOwnerBundleJson =
        "[\"audit:read\","
        + "\"calendar:archive\",\"calendar:create\",\"calendar:read\",\"calendar:write\","
        + "\"comms:append\",\"comms:read\","
        + "\"contacts:archive\",\"contacts:create\",\"contacts:read\",\"contacts:write\",\"forms:author\","
        + "\"gl:post\",\"gl:read\",\"grant:permissions\",\"ledger:post\","
        + "\"members:admit\",\"members:manage\",\"members:revoke\",\"members:set-role\","
        + "\"org:branding:write\",\"org:manage-settings\",\"org:transfer-ownership\","
        + "\"packages:author\",\"packages:operate\","
        + "\"provider:configure-bank-feed\",\"provider:configure-email\",\"provider:configure-identity\","
        + "\"provider:configure-payments\",\"provider:configure-storage\",\"provider:configure-telemetry\","
        + "\"provider:read-config\","
        + "\"records:read\",\"records:write\","
        + "\"scheduling:author\",\"scheduling:operate\",\"scheduling:read\",\"spatial:read\","
        + "\"stories:archive\",\"stories:create\",\"stories:read\",\"stories:write\","
        + "\"telemetry:export\"]";

    /// <summary>
    /// The full backfill script. Internal so the test suite can execute it a second time against an
    /// already-backfilled database and prove the idempotency claim by work product, not by prose.
    /// One statement, permissions_json ONLY — no owner_version movement, no epoch movement (see the
    /// precedent's F1 remarks: those values are mirrored into the membership pins, which refuse on any
    /// mismatch).
    /// </summary>
    internal const string BackfillSql = $"""
        UPDATE search_grants
        SET permissions_json = CASE permissions_json
                WHEN '{PreWideningAdminBundleJson}' THEN '{WidenedAdminBundleJson}'
                ELSE '{WidenedOwnerBundleJson}'
            END
        WHERE revoked_at_unix_ms IS NULL
          AND permissions_json IN ('{PreWideningAdminBundleJson}', '{PreWideningOwnerBundleJson}');
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
