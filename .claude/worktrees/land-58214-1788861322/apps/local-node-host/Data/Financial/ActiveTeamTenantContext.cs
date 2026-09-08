using System;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Node-resident <see cref="ITenantContext"/> that derives the ambient data tenant from the
/// <em>active team</em> (ADR 0032 multi-team identity layer; multi-org identity survey #1275 §5).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the A↔B unification — the data-isolation piece.</b> It retires the install-constant
/// <c>StaticNodeTenantContext</c> (<c>TenantId("local")</c>) and instead projects the data
/// <see cref="TenantId"/> deterministically from <see cref="IActiveTeamAccessor.Active"/>'s
/// <see cref="TeamContext.TeamId"/>:
/// <code>TenantId = activeTeam.TeamId.Value.ToString()</code>
/// </para>
/// <para>
/// Why this matters (the worst-case multi-tenant bug it closes): if the runtime <c>TeamId</c>
/// (which org is foreground, which sync daemon + trust roster are active) and the data
/// <c>TenantId</c> (which org's books a financial write lands in) are NOT the same office, a
/// member admitted to org A's <em>sync</em> whose <em>data</em> queries still hit a shared
/// <c>"local"</c> tenant would read another org's books — a silent cross-org data-isolation hole.
/// Binding <c>TenantId</c> to the active <c>TeamId</c> closes that by construction: switching the
/// active team switches the ambient <c>TenantId</c>, so "admitted to the org" and "reads the org's
/// data" can never drift apart. The invariant is arch-test-fenced (see
/// <c>ActiveTeamTenantBindingArchTests</c>) — no route may pin a literal tenant, and a different
/// active team MUST yield a different <c>TenantId</c>.
/// </para>
/// <para>
/// The projection is a <see cref="Guid"/> string (the team id's <c>"D"</c> form), which never
/// collides with the <c>"__"</c>-reserved system sentinels nor with the retired <c>"local"</c>
/// literal — so existing single-tenant <c>"local"</c> data is not silently re-attributed; a fresh
/// install seeds its single default team and that team's id becomes the books' tenant from the
/// first write.
/// </para>
/// <para>
/// Identity-only: this is <c>Harborline.Api.Foundation.MultiTenancy.ITenantContext</c> (the narrow
/// tenant-resolution interface, ADR 0008), NOT the authorization sum-facade — the financial write
/// services consult only <see cref="TenantMetadata.Id"/>.
/// </para>
/// </remarks>
public sealed class ActiveTeamTenantContext : ITenantContext
{
    private readonly IActiveTeamAccessor _activeTeam;

    /// <summary>Construct over the active-team accessor (ADR 0032).</summary>
    public ActiveTeamTenantContext(IActiveTeamAccessor activeTeam)
    {
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
    }

    /// <summary>
    /// Project the active team into a <see cref="TenantId"/>. A team id (Guid) maps deterministically
    /// to a tenant id string; the same team always yields the same tenant.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="teamId"/>'s string form is empty.</exception>
    public static TenantId ProjectTenantId(TeamId teamId) => new(teamId.Value.ToString());

    /// <inheritdoc />
    public TenantMetadata? Tenant
    {
        get
        {
            var active = _activeTeam.Active;
            if (active is null)
            {
                // No team materialized yet (pre-bootstrap window). Unresolved — callers that
                // require a tenant fail loudly rather than silently scoping to a wrong/shared one.
                return null;
            }

            return new TenantMetadata
            {
                Id = ProjectTenantId(active.TeamId),
                Name = active.TeamId.Value.ToString(),
                DisplayName = active.DisplayName,
            };
        }
    }
}
