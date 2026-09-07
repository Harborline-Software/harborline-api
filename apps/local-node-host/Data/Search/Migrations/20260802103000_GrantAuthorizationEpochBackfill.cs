using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Harborline.Api.LocalNodeHost.Data.Search.Migrations;

/// <summary>
/// Data-only backfill — mints the missing per-principal authorization epoch for every grant that predates
/// the epoch table.
/// </summary>
/// <remarks>
/// <para>
/// <c>search_grants</c> was created by <c>20260624134345_VecIndex</c>; the
/// <c>search_grant_authorization_epochs</c> table arrived three weeks later in
/// <c>20260714104253_GrantFreshnessRows</c>, which backfilled <c>owner_version = 1</c> on the existing grant
/// rows but inserted NO epoch rows. Every (tenant, principal) that held a grant before that migration is
/// therefore an ORPHAN: a live grant with no epoch. Both admission paths deny on it — the web-admitted
/// pairing path refuses <c>grant_unavailable</c> when the epoch is null, and the plain path is held closed
/// by the mode selector because a live grant exists. An upgraded installation can end up with no way in.
/// </para>
/// <para>
/// The backfill is a SEPARATE migration rather than an edit to <c>GrantFreshnessRows</c> precisely because
/// the affected installations have already applied that migration — an already-applied <c>Up</c> never runs
/// again, so only a new migration reaches them.
/// </para>
/// <para>
/// <b>Value 1, for every distinct (tenant, principal) with a grant row.</b> One is what
/// <c>NodeEfGrantStore.AdvanceAuthorizationEpochAsync</c> writes when it first mints an epoch for a
/// principal, so a backfilled row is indistinguishable from one minted by that principal's first grant
/// write. Revoked and not-yet-valid grants are included on purpose: revocation ADVANCES the epoch rather
/// than deleting it, so the post-migration world has epoch rows for those principals too, and an epoch is
/// not itself authority — every admission path additionally requires a live grant. Evaluating grant
/// liveness inside a migration would bake the migration-run instant into the data and leave a
/// not-yet-valid grant orphaned once its validity window opens.
/// </para>
/// <para>
/// The <c>NOT EXISTS</c> guard makes the statement idempotent and keeps it off any (tenant, principal)
/// whose epoch has already advanced past 1. Its correlation is per-PRINCIPAL, not per-tenant: dropping
/// <c>e.principal_id = g.principal_id</c> would make one already-epoched principal shadow every other
/// principal in that tenant, and EF would mark the migration applied so it never ran again.
/// </para>
/// <para>
/// <b>What actually protects the existing rows is the PRIMARY KEY, not this guard.</b> Removing
/// <c>NOT EXISTS</c> entirely does not silently overwrite an advanced epoch — it raises
/// <c>SQLite Error 19: UNIQUE constraint failed</c> inside <c>MigrateAsync</c>, which aborts host
/// startup. So the guard's job is to stop a partially-epoched install from failing to boot, and the key
/// is what makes the no-clobber property true. That is a stronger guarantee than "the guard prevents a
/// clobber", and it is worth stating correctly because the two failure modes are loud and silent
/// respectively.
/// </para>
/// <para>
/// Nothing validates a write to <c>search_grant_authorization_epochs</c>. There is no CHECK constraint on
/// <c>authorization_epoch</c>, and <c>NodeEfGrantStore</c> only ever writes <c>1</c> or <c>+1</c> without
/// checking. The <c>&lt;= 0</c> rejections live on the identity-side PINS — the tenant-membership
/// authority store and the web installation session store — which is a different column on a different
/// table. Do not read them as protecting this one. The literal below is the only thing keeping the value
/// legal.
/// </para>
/// <para>
/// Those two stores are named in prose rather than as type references on purpose, and this paragraph
/// carries no <c>&lt;c&gt;</c> reference to either. The session store's own test suite proves it is
/// DORMANT by scanning every non-test <c>.cs</c> file for its type name and asserting exactly one match.
/// That scan cannot tell a doc comment from a call site, so spelling the identifier here — even to say
/// the store does NOT protect this table — registers this migration as a second consumer and turns the
/// test red. It did, once, and this is the correction. The gate is right to be that blunt: a dormancy
/// claim weakened to accommodate prose stops being a dormancy claim.
/// </para>
/// <para>
/// <b>One narrowing worth naming.</b> A row here with no matching epoch row is the torn state
/// <c>WebPlaneAdmissionState</c> deliberately chose to DENY, and this backfill heals it rather than
/// leaving it denied. That reversal is bounded: the grant row and the epoch row are staged into a single
/// <c>SaveChangesAsync</c> inside the execution strategy (<c>NodeEfGrantStore</c>), and that is the only
/// production writer, so a torn state is not reachable in ordinary operation. What this heals is an
/// externally-tampered or partially-restored database — deliberate, but a narrowing of that posture, not
/// a no-op against it.
/// </para>
/// </remarks>
[DbContext(typeof(NodeLocalSearchDbContext))]
[Migration("20260802103000_GrantAuthorizationEpochBackfill")]
public sealed class _20260802103000_GrantAuthorizationEpochBackfill : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO search_grant_authorization_epochs (tenant_id, principal_id, authorization_epoch)
            SELECT DISTINCT g.tenant_id, g.principal_id, 1
            FROM search_grants AS g
            WHERE NOT EXISTS (
                SELECT 1
                FROM search_grant_authorization_epochs AS e
                WHERE e.tenant_id = g.tenant_id AND e.principal_id = g.principal_id);
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally empty. The Up is a data backfill onto rows this migration cannot distinguish from
        // ones a legitimate grant write minted afterwards, so deleting epochs on the way down would destroy
        // live authority state. Reverting the schema is DurableSubjectErasure/GrantFreshnessRows' Down.
    }
}
