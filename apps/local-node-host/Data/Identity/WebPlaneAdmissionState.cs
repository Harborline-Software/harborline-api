using Microsoft.EntityFrameworkCore;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data.Search;

namespace Harborline.Api.LocalNodeHost.Data.Identity;

/// <summary>
/// MTW-2 #3167 (R2) — the WEB-PLANE-ADMISSION-ENABLED predicate that makes the plain wire-enrollment path and the
/// web-admitted pairing path MODE-EXCLUSIVE per tenant. Per the identity-acquisition-modes doctrine, a tenant is in
/// EITHER the open/plain enrollment posture OR the web-admitted pairing posture — never both. When web-plane
/// admission is enabled for a tenant, the plain enrollment path for that tenant refuses (structurally, at the same
/// gate depth as the pins check), so there is no tenant state in which both admit.
/// </summary>
/// <remarks>
/// The amendment (admiral-ruling-amendment-2026-07-24T1210Z) pins the predicate: "web-plane admission enabled for
/// tenant T = at least one live web membership grant for T, read via the same grant + authorization-epoch liveness
/// the Option-A path uses (single source)."
///
/// THIS IMPLEMENTATION DEPARTS FROM THAT WORDING, DELIBERATELY, and the departure is stated rather than folded into
/// the quotation. The ruling requires grant AND epoch together; this predicate requires the grant alone.
///
/// Why: requiring both made a PARTIAL write fail OPEN — an orphan live grant left plain enrollment available, which
/// is the wrong direction for an admission gate. And the orphan state is reachable by ordinary UPGRADE, not only by
/// corruption: `search_grants` was created by migration 20260624134345_VecIndex, while the epoch table arrived three
/// weeks later in 20260714104253_GrantFreshnessRows with NO backfill. Every grant row written before that migration
/// is an orphan. An enumeration of grant writers would not have found this; only the predicate closes it.
///
/// SECOND STATED DEPARTURE (#264) — the ruling says "live WEB MEMBERSHIP grant"; this predicate counts only grants
/// ISSUED THROUGH THE WEB PLANE and therefore EXCLUDES the installer's bootstrap grant and the installer
/// authorization seed set: those rows carry the installer's <see cref="GranterKind"/> and the
/// <see cref="GrantSourceKind.Bootstrap"/> source, and this predicate keeps only person-granted, non-bootstrap
/// rows. (The installer granter kind is named here in prose only — BootstrapAuthorityArchTests fences the literal
/// symbol to the one file allowed to MINT such a grant, and a mode selector only READS them.) The founder ceremony writes exactly one such row into this same
/// `search_grants` table on EVERY install (the founder claim-redemption ceremony, ADR 0066 section 4), so counting it made "web-plane admission enabled" true on every
/// founded node and left the doctrine's open/plain arm structurally unreachable in production. A fact true of every
/// install cannot discriminate a per-tenant mode. The narrowing lives HERE and NOT in
/// <see cref="LiveWebMembershipGrantQuery"/>: that expression is also read by
/// <see cref="LiveTenantMembershipAuthorityAdmission"/> and <see cref="WebAdmittedMemberAtlasBridge"/>, which are
/// AUTHORITY reads (may this principal do X) where the bootstrap grant is an ordinary grant per ADR 0066 section 1.
/// This is a MODE selector (which admission posture is this tenant in), and provenance is legitimate evidence only
/// for that question.
///
/// The epoch narrowing is strictly monotone-closing — it cannot open a state that was previously closed. The Option-A path
/// still compares the epoch and refuses on a stale one, so the single-source intent of the ruling survives where it
/// governs authority; what changed is only which side a partial write falls to. This reads the SAME grant
/// store + the SAME grant-liveness rule as
/// <see cref="LiveTenantMembershipAuthorityAdmission"/> and the <see cref="WebAdmittedMemberAtlasBridge"/>'s grant
/// pin — a grant that is not revoked and inside its validity window — and returns true iff any such grant that was
/// issued through the web plane exists for the tenant. Persisted grant coordinates are re-read live on every call (no cached authority).
/// </remarks>
internal interface IWebPlaneAdmissionState
{
    /// <summary>
    /// True iff <paramref name="tenant"/> has web-plane admission ENABLED (≥1 live web membership grant ISSUED
    /// THROUGH THE WEB PLANE — the installer's bootstrap grant and seed set do not count; see the remarks). When true,
    /// the plain enrollment path is mode-exclusively OFF for the tenant and a binding-absent redeem refuses opaque.
    /// </summary>
    Task<bool> IsEnabledAsync(TenantId tenant, CancellationToken ct);
}

/// <summary>
/// The live <see cref="IWebPlaneAdmissionState"/> — reads the grant store (<see cref="NodeLocalSearchDbContext"/>)
/// for any WEB-PLANE-ISSUED grant that is live for the tenant NOW (not revoked, inside its validity window), using
/// the SAME liveness rule the grant-anchored Option-A admission + the atlas bridge use (single source, no drift),
/// plus the #264 provenance narrowing that excludes the installer's bootstrap grant and seed set.
/// </summary>
internal sealed class LiveWebPlaneAdmissionState(
    IDbContextFactory<NodeLocalSearchDbContext> grantFactory,
    TimeProvider timeProvider) : IWebPlaneAdmissionState
{
    private readonly IDbContextFactory<NodeLocalSearchDbContext> _grantFactory = grantFactory
        ?? throw new System.ArgumentNullException(nameof(grantFactory));
    private readonly TimeProvider _timeProvider = timeProvider
        ?? throw new System.ArgumentNullException(nameof(timeProvider));

    /// <inheritdoc />
    public async Task<bool> IsEnabledAsync(TenantId tenant, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        await using var grants = await _grantFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        // ≥1 live web membership grant closes the plain path. The Option-A admission path still requires the
        // matching epoch before it admits; the mode selector intentionally does not, so corrupt or partially
        // written authority state denies both paths instead of silently reopening plain enrollment.
        return await grants.Grants.AsNoTracking()
            .Where(LiveWebMembershipGrantQuery.ForTenantAt(tenant, now))
            // ...ISSUED THROUGH THE WEB PLANE. A person granted it; the installer did not. The founder ceremony's
            // bootstrap grant and the installer authorization seed are on every node and prove no web plane (#264).
            .Where(row => row.GranterKind == (int)GranterKind.Person
                && row.Source != (int)GrantSourceKind.Bootstrap)
            .AnyAsync(ct)
            .ConfigureAwait(false);
    }
}
