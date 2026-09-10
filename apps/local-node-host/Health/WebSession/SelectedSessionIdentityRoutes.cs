using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// Selected-audience-only whoami (MTW-2 #3329 step 1) — who the browser is signed in as.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not <c>GET /api/session/me</c>.</b> That route summarizes the v1 audience through
/// <see cref="INodeWebSessionAuthority"/>, whose <see cref="WebSessionSummary"/> carries no account
/// id — the key every v2 identity fact is indexed by — and belongs to the authority version ADR 0160
/// R3-H schedules for wholesale rejection at the authority-version flip. Building the new front door
/// on the type slated for deletion would buy a rewrite and reproduce the gap earlier repository ticket #3178 broke on.
/// </para>
/// <para>
/// <b>Audience.</b> Deliberately NOT in <c>NodeListenerCallerAuthPolicy</c>'s pre-auth allowlist. A
/// request carrying a v1 handle is classified <c>LegacyEligible</c> and may pass the listener gate on
/// that audience's own accept path, so listener admission alone does not separate the two. The
/// separation is here: this handler serves nothing without BOTH the selected cookie AND the
/// <see cref="SelectedSessionRequestPrincipal"/> the gate's selected-cookie accept publishes. A v1
/// handle carries neither, so it is refused before the selected authority is consulted at all — it
/// cannot read this surface and cannot enumerate through it.
/// </para>
/// <para>
/// <b>Refusal.</b> One non-enumerating 401 for every miss — absent cookie, absent principal, or a
/// handle that does not name that principal's session all answer identically.
/// </para>
/// </remarks>
internal static class SelectedSessionIdentityRoutes
{
    internal const string WhoamiPath = "/api/session/whoami";
    internal const string PermissionsPath = "/api/session/permissions";

    /// <summary>The signed-in tenant and its own label (null until the tenant sets one).</summary>
    private sealed record TenantView(string Id, string? DisplayName);

    /// <summary>
    /// Identity plus the current server-derived effective permission snapshot. Epoch, membership and
    /// owner version remain revalidation machinery and never appear on the wire.
    /// </summary>
    /// <param name="AdvisoryExpiresAtUtc">
    /// Named for what it is. Every selected-session request revalidates the full fact set server-side,
    /// so this is a UX affordance — warn a human before their session ends — and never a liveness
    /// gate. A client that decides it still has a session because this value has not passed is making
    /// the same error as one that caches a permission.
    /// </param>
    private sealed record WhoamiResponse(
        string AccountId,
        string PartyId,
        string? DisplayName,
        TenantView Tenant,
        string Standing,
        DateTimeOffset AdvisoryExpiresAtUtc,
        IReadOnlyCollection<string>? Permissions);

    private sealed record ErrorResponse(string Error, string Message);
    private sealed record EffectivePermissionsResponse(
        IReadOnlyCollection<string> Permissions,
        IReadOnlyCollection<string> EffectivePermissions);

    // Maps whoami ONLY. The permissions route lives in HostedEffectivePermissionsApiEndpoint because
    // it must be registered on both deployment profiles; this method is web-plane-scoped. It briefly
    // took an IVerifiedTenantRosterReader it never used, which cost a constructor dependency in the
    // endpoint and a stub in its tests.
    internal static void Map(
        IEndpointRouteBuilder app,
        IWebSelectedSessionIdentityAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(authority);
        // Explicit Delegate cast: a HttpContext-only lambda otherwise binds the RequestDelegate
        // overload, which discards the Task<IResult> (ASP0016).
        app.MapGet(
            WhoamiPath,
            (Delegate)((HttpContext context) => DescribeAsync(authority, context)));
    }

    internal static void MapPermissions(
        IEndpointRouteBuilder app,
        IVerifiedTenantRosterReader rosterReader,
        IActiveTeamAccessor activeTeam)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(rosterReader);
        ArgumentNullException.ThrowIfNull(activeTeam);
        app.MapGet(
            PermissionsPath,
            (Delegate)((HttpContext context) => PermissionsAsync(context, rosterReader, activeTeam)));
    }

    /// <summary>
    /// Returns the node's own answer for what the caller holds. A selected web session is answered by the
    /// request-scoped PEP snapshot bound by the selected-cookie accept; a bootstrap-bearer desktop request
    /// has no selected principal, so the active roster's genesis Party is read from the grant store through
    /// <c>IRosterAuthority</c>. The caller supplies neither Party id nor role, and every other audience is
    /// refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ticket 293 slice 5 — one authority.</b> Both branches answer from grants decided by
    /// <c>AuthorizationGate</c>. Neither reads a permission set off the roster: since slice 3b2 the wire
    /// record carries none, so the roster's only contributions to authorization are membership and ejection
    /// (<c>EffectiveMemberPermissions.Read</c>), and they reach the gate as inputs rather than as an answer.
    /// An authority that cannot answer yields <see cref="Unresolved"/>, never a composition this node never
    /// granted.
    /// </para>
    /// <para>
    /// KNOWN DIVERGENCE on a JOINED node, stated plainly. Genesis is the local operator ONLY on a node that
    /// founded its own team. After a wire enrollment, <c>NodeEnrollmentJoinService</c> flips the active team
    /// to the ADOPTED one, so <c>roster.GenesisPartyId</c> becomes the ADMITTING node's founder party. A
    /// joined desktop node therefore reports the admitter's grant rather than the one its own admission
    /// conferred. Nav visibility is not the enforcement boundary (<c>AdminTeamAccessRoutes</c> requires a
    /// selected-session principal a bootstrap bearer cannot hold), but the node knows its own party id, so
    /// the information to answer correctly is present and unused.
    /// </para>
    /// </remarks>
    internal static async Task<IResult> PermissionsAsync(
        HttpContext context,
        IVerifiedTenantRosterReader rosterReader,
        IActiveTeamAccessor? activeTeam = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rosterReader);
        context.Response.Headers.CacheControl = "no-store";

        var principal = context.Features.Get<SelectedSessionRequestPrincipal>();
        var handle = context.Request.Cookies[WebSessionCookieNames.Selected];
        var selectedSession = principal is not null && !string.IsNullOrWhiteSpace(handle);
        var bootstrapBearer =
            context.Features.Get<SharedHostedWebApp.BootstrapBearerRequestPrincipal>() is not null;
        if (!selectedSession && NodeCallerAttributionScope.HasBoundWebPrincipal)
        {
            return Refused();
        }
        if (!selectedSession && (!bootstrapBearer || activeTeam?.Active is null))
        {
            return Refused();
        }

        // Ticket 293 slice 5 — ONE read, the gate's. A selected session's answer is the request-scoped PEP
        // snapshot the selected-cookie accept already bound (SelectedSessionPermissionResolver: the grant
        // store's install-root closure for this principal, every atom decided by AuthorizationGate, the
        // signed roster edge contributing membership and ejection only). There is no roster fallback and no
        // client-visible baseline: the roster carries no permission set for a replicated member since slice
        // 3b2, so a fallback could only report an empty set or a composition this node never granted. An
        // unresolved snapshot is "we cannot answer", which is what Unresolved says.
        if (selectedSession)
        {
            var bound = context.RequestServices.GetService<SelectedSessionTenantContext>()?.EffectivePermissions;
            return bound is null
                ? Unresolved()
                : Permissions(PermissionSet.From(bound));
        }

        // The desktop bootstrap bearer binds no selected principal, so there is no request-scoped PEP to
        // read — and the only party it may be answered about is the active roster's CHAIN ROOT. Its
        // authority is the root floor of its own genesis self-admission, which is the same rule the
        // replicated rebuild applies (RosterCrdtProjection.AuthorityFor: genesis keeps the root floor,
        // every other party is read from the grant store) and for the same reason — the root is what makes
        // the chain authoritative, so it cannot be derived from a grant the chain itself authorizes. The
        // roster is asked WHO the root is, never what any member holds: since slice 3b2 it carries no
        // permission set for a replicated member, and reading one here would report an empty set for
        // exactly the members it was asked about.
        try
        {
            var roster = await rosterReader
                .ReadAsync(ActiveTeamTenantContext.ProjectTenantId(activeTeam!.Active!.TeamId), context.RequestAborted)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(roster.GenesisPartyId))
            {
                // A degenerate roster has a blank genesis party: a broken install, not a deferred
                // admission. There is no party to answer about, so say so rather than 500 on a read the
                // client makes on every boot, and rather than invent a set from a fault.
                return Unresolved();
            }
            return Permissions(PermissionCompositions.Owner);
        }
        catch (VerifiedTenantRosterRefusedException)
        {
            // The node could not produce a verified roster. That is NOT "this person has no
            // permissions" — it is "no answer", and conflating them shows a refusal as an empty app.
            return Unresolved();
        }
    }

    /// <summary>
    /// Project ONE resolved set to the wire. The navigation vocabulary is projected separately from the raw
    /// atomic snapshot: navigation asks its questions in a vocabulary that shares only 8 of the 21 strings
    /// with the authorization one, while mutation affordances need the exact PEP vocabulary. Neither client
    /// value is an authorization input.
    /// </summary>
    private static IResult Permissions(PermissionSet held) =>
        Results.Ok(new EffectivePermissionsResponse(
            NavigationPermissionProjection.Project(held),
            held.Permissions));

    internal static async Task<IResult> DescribeAsync(
        IWebSelectedSessionIdentityAuthority authority,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";

        var handle = context.Request.Cookies[WebSessionCookieNames.Selected];
        if (string.IsNullOrWhiteSpace(handle))
        {
            return Refused();
        }

        // The gate publishes this feature before the handler runs. A missing principal means the
        // route was reached off its intended path; treat it identically to no cookie.
        var principal = context.Features.Get<SelectedSessionRequestPrincipal>();
        if (principal is null)
        {
            return Refused();
        }

        var identity = await authority
            .DescribeAsync(handle, principal, context.RequestAborted)
            .ConfigureAwait(false);
        if (identity is null)
        {
            return Refused();
        }

        var permissions = context.RequestServices
            .GetService<SelectedSessionTenantContext>()?
            .EffectivePermissions;

        return Results.Ok(new WhoamiResponse(
            identity.AccountId,
            identity.PartyId.Value,
            identity.DisplayName,
            new TenantView(identity.TenantId.Value, identity.TenantDisplayName),
            Wire(identity.Membership),
            identity.AdvisoryExpiresAtUtc,
            permissions));
    }

    /// <summary>
    /// The wire spelling of a membership. <c>unresolved</c> is a first-class value, not an error: an
    /// installation that has designated no root cannot say whether this account is its founder, and
    /// answering "member" there would label the founder a member.
    /// </summary>
    private static string Wire(SelectedSessionMembership membership) => membership switch
    {
        SelectedSessionMembership.Founder => "founder",
        SelectedSessionMembership.Member => "member",
        _ => "unresolved",
    };

    private static IResult Refused() =>
        Results.Json(
            new ErrorResponse("no_selected_session", "No selected session."),
            statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>
    /// The node authenticated the caller but could not determine what they hold. Distinct from BOTH
    /// a refusal and an empty set: the client must show its unresolved state rather than an app with
    /// no destinations, because "we cannot answer" and "you may do nothing" look identical to a user
    /// and only one of them is a permissions statement.
    /// </summary>
    private static IResult Unresolved() =>
        Results.Json(
            new ErrorResponse("permissions_unresolved", "Effective permissions could not be resolved."),
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
