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
    /// Returns permissions from the current verified roster snapshot. A selected web session uses
    /// its revalidated canonical Party exactly as before. A bootstrap-bearer desktop request has no
    /// selected principal, so it uses the active roster's genesis Party. The caller supplies neither
    /// Party id nor role, and every other audience is refused.
    /// </summary>
    /// <remarks>
    /// KNOWN DIVERGENCE on a JOINED node, stated plainly because the sentence above reads as a
    /// reassurance it does not give. Genesis is the local operator ONLY on a node that founded its
    /// own team. After a wire enrollment, <c>NodeEnrollmentJoinService</c> flips the active team to
    /// the ADOPTED one, so <c>roster.GenesisPartyId</c> becomes the ADMITTING node's founder party.
    /// A joined desktop node therefore reports the admitter's owner permissions rather than the
    /// permissions its own admission granted it.
    ///
    /// This is not a regression -- before this change the renderer granted the full set to every
    /// resolved principal unconditionally -- and nav visibility is not the enforcement boundary
    /// (<c>AdminTeamAccessRoutes</c> requires a selected-session principal a bootstrap bearer
    /// cannot hold). But it does relocate the synthesis to the server rather than removing it,
    /// which is not what "the node decides" claims. The node already knows its own party id, so the
    /// information to answer correctly is present and unused.
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

        try
        {
            var tenantId = selectedSession
                ? principal!.TenantId
                : ActiveTeamTenantContext.ProjectTenantId(activeTeam!.Active!.TeamId);
            var roster = await rosterReader
                .ReadAsync(tenantId, context.RequestAborted)
                .ConfigureAwait(false);
            var partyId = selectedSession
                ? principal!.CanonicalParty.Value
                : roster.GenesisPartyId;
            if (string.IsNullOrWhiteSpace(partyId))
            {
                // A degenerate roster has a blank genesis party, and PermissionsOf throws on it —
                // uncaught, that is a 500 on a read the client makes on every boot. Found by the test
                // for this branch, not in production. There is no party to answer about, so say so.
                return Unresolved();
            }
            // The roster UNDER-REPORTS a documented class of member. A web invitee is admitted on the
            // tenant-identity plane — account, Party binding, live grant, Active membership — but the
            // signed atlas admission is DEFERRED to first wire enrollment (Option A, and
            // WebAdmittedMemberAtlasBridge says so in its own summary). The bridge runs only from the
            // device-pairing path, so someone who accepts an invitation in a browser and never pairs a
            // device is authenticated, listed by the admin screen, and absent from the roster forever.
            // PermissionsOf returns null for them, which would project to an empty set and blank the
            // entire product — for exactly the population the invite flow creates.
            //
            // Reaching this line at all means the session authority already revalidated the account,
            // the membership, and the grant owner-version pins, so a grant-anchored member here holds a
            // LIVE grant by construction. The baseline is not a guess either: AdmissionCoordinator's
            // redeemed-invite admission writes PermissionCompositions.Member, so this is the same set
            // the bridge will sign onto the roster when enrollment eventually happens. Answering it now
            // makes the browser and the paired-device views agree instead of differing by a blank app.
            // The selected-session gate has already bound the same request-scoped PEP used by route
            // authorization. Prefer that snapshot whenever this is a production composed request;
            // the direct route tests retain the legacy fixture fallback when no inner scope exists.
            var bound = selectedSession
                ? context.RequestServices.GetService<SelectedSessionTenantContext>()?.EffectivePermissions
                : null;
            var held = bound is not null
                ? PermissionSet.From(bound)
                : roster.PermissionsOf(partyId)
                    ?? (selectedSession ? PermissionCompositions.Member : null);

            // Project the navigation vocabulary separately from the raw atomic snapshot. The
            // navigation asks its questions in a vocabulary that shares only 8 of the 21 strings
            // with the roster's, while mutation affordances need the exact PEP vocabulary. The
            // projected set is for navigation only; neither client value is an authorization input.
            return Results.Ok(new EffectivePermissionsResponse(
                NavigationPermissionProjection.Project(held),
                held?.Permissions ?? Array.Empty<string>()));
        }
        catch (VerifiedTenantRosterRefusedException)
        {
            // The node could not produce a verified roster. That is NOT "this person has no
            // permissions" — it is "no answer", and conflating them shows a refusal as an empty app.
            return Unresolved();
        }
    }

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
