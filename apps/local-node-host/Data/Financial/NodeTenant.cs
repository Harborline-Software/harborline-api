using System;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Data.Financial;

/// <summary>
/// Resolves the node's ambient data <see cref="TenantId"/> from the <em>active team</em>
/// (ADR 0032 identity layer; multi-org identity survey #1275 §5) — the single resolution point
/// the node-local routes use instead of the retired <c>StaticNodeTenantContext.LocalTenantId</c>
/// (<c>"local"</c>) literal.
/// </summary>
/// <remarks>
/// <para>
/// The node-local routes (contacts / invoices / bills / payments / payroll / …) previously pinned a
/// <c>static readonly TenantId LocalTenantId = new("local")</c>. That literal is the hardcoded-tenant
/// the data-isolation binding retires: the route's tenant MUST follow the active team so that
/// switching the active org switches which org's books a route reads/writes (no silent cross-org
/// data bleed). This helper is the route-side analogue of <see cref="ActiveTeamTenantContext"/>
/// (which is the financial-write-service-side ambient binding).
/// </para>
/// <para>
/// <b>Resolved per call, not cached</b> — so a team switch is reflected immediately and the
/// arch-test fence (a different active team yields a different <c>TenantId</c>) holds at the route
/// layer too. v1 boots a single active default team, so in practice this returns that team's id;
/// the per-call resolution is what keeps it switch-safe (and hardcode-free) rather than a captured
/// constant.
/// </para>
/// </remarks>
public static class NodeTenant
{
    /// <summary>
    /// The active team's projected <see cref="TenantId"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No team is active yet (pre-bootstrap). Routes are mapped after
    /// <c>MultiTeamBootstrapHostedService</c> seeds the active team, so this is a programming error
    /// if it ever fires at request time.
    /// </exception>
    public static TenantId Resolve(IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(activeTeam);
        var active = activeTeam.Active
            ?? throw new InvalidOperationException(
                "No active team — the node has no resolved data tenant. " +
                "MultiTeamBootstrapHostedService must seed the active team before routes are served.");
        return ActiveTeamTenantContext.ProjectTenantId(active.TeamId);
    }
}
