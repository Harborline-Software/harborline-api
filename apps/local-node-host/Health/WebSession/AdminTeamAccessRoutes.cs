using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// Selected-audience-only admin Team &amp; access surface (MTW-2 #2617): list members (signed roster
/// UNIONed with grant-anchored web members), list + issue pending AccountSetup invitations, edit a live
/// member's PBAC permission bundle, and revoke a grant-anchored web member's access via grant revocation.
/// </summary>
/// <remarks>
/// Deliberately NOT listed in <c>NodeListenerCallerAuthPolicy</c>'s pre-auth allowlist: every route is
/// admitted by the listener gate's selected-cookie accept, so the session gate has already revalidated
/// the owning account, membership, Party, grant, and epoch and published one immutable principal before
/// any handler runs. The tenant is read from that principal — the browser never supplies it. The full
/// gate chain (permission → live session valid → members:manage on THIS tenant → action) is enforced
/// inside <see cref="IAdminTeamAccessAuthority"/>, which independently reloads every authority fact; the
/// handler's principal + cookie checks are defense in depth, never the sole gate. Non-admin callers
/// receive an identical non-enumerating refusal.
/// </remarks>
internal static class AdminTeamAccessRoutes
{
    internal const string MembersPath = "/api/session/admin/members";
    internal const string InvitationsPath = "/api/session/admin/invitations";
    internal const string RevokeMemberPath = "/api/session/admin/members/revoke";
    internal const string UpdateMemberPermissionsPath = "/api/session/admin/members/permissions";

    internal sealed record IssueInvitationRequest(
        IReadOnlyList<string>? RequestedPermissions,
        string? IdempotencyKey);

    /// <param name="SuccessorPrincipalId">
    /// Present only for an Administrator handover (ledger L618): the revocation and the successor's
    /// Administrator grant land as one transaction.
    /// </param>
    internal sealed record RevokeMemberRequest(string? GrantId, string? SuccessorPrincipalId = null);

    internal sealed record UpdateMemberPermissionsRequest(
        string? GrantId,
        IReadOnlyList<string>? RequestedPermissions);

    private sealed record MemberView(
        string PartyId,
        string Source,
        IReadOnlyList<string> Capabilities,
        string? GrantId);

    private sealed record MembersResponse(IReadOnlyList<MemberView> Members);

    private sealed record PendingInvitationView(
        string InvitationId,
        string InviterPartyId,
        IReadOnlyList<string> RequestedPermissions,
        DateTimeOffset IssuedAtUtc,
        DateTimeOffset AbsoluteExpiresAtUtc);

    private sealed record InvitationsResponse(IReadOnlyList<PendingInvitationView> Invitations);

    private sealed record IssuedInvitationResponse(
        string InvitationId,
        string Code,
        string TenantId,
        DateTimeOffset AbsoluteExpiresAtUtc);

    private sealed record RevokeResponse(string Status, string? SuccessorGrantId = null);

    private sealed record ErrorResponse(string Error, string Message);

    internal static void Map(
        IEndpointRouteBuilder app,
        IAdminTeamAccessAuthority authority,
        IWebAntiforgeryPolicy antiforgery,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // Cast to Delegate so the framework treats these single-HttpContext lambdas as route handlers
        // (and writes the returned IResult) rather than as a RequestDelegate that discards it (ASP0016).
        app.MapGet(
            MembersPath,
            (Delegate)((HttpContext context) => ListMembersAsync(authority, context)));
        app.MapGet(
            InvitationsPath,
            (Delegate)((HttpContext context) => ListInvitationsAsync(authority, context)));
        app.MapPost(
            InvitationsPath,
            (IssueInvitationRequest? request, HttpContext context) =>
                IssueInvitationAsync(authority, antiforgery, request, context, timeProvider.GetUtcNow()));
        app.MapPost(
            RevokeMemberPath,
            (RevokeMemberRequest? request, HttpContext context) =>
                RevokeMemberAsync(authority, antiforgery, request, context, timeProvider.GetUtcNow()));
        app.MapPost(
            UpdateMemberPermissionsPath,
            (UpdateMemberPermissionsRequest? request, HttpContext context) =>
                UpdateMemberPermissionsAsync(authority, antiforgery, request, context, timeProvider.GetUtcNow()));
    }

    internal static async Task<IResult> ListMembersAsync(
        IAdminTeamAccessAuthority authority,
        HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (handle, principal) = ReadSelected(context);
        if (handle is null || principal is null)
        {
            return Refused();
        }

        var result = await authority
            .ListMembersAsync(handle, principal.TenantId.Value, context.RequestAborted)
            .ConfigureAwait(false);
        if (result is null)
        {
            return Refused();
        }

        return Results.Ok(new MembersResponse(result.Members
            .Select(m => new MemberView(
                m.PartyId,
                m.Source == TeamMemberSource.Roster ? "roster" : "grant",
                m.Capabilities,
                m.GrantId))
            .ToArray()));
    }

    internal static async Task<IResult> ListInvitationsAsync(
        IAdminTeamAccessAuthority authority,
        HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (handle, principal) = ReadSelected(context);
        if (handle is null || principal is null)
        {
            return Refused();
        }

        var result = await authority
            .ListPendingInvitationsAsync(handle, principal.TenantId.Value, context.RequestAborted)
            .ConfigureAwait(false);
        if (result is null)
        {
            return Refused();
        }

        return Results.Ok(new InvitationsResponse(result.Invitations
            .Select(i => new PendingInvitationView(
                i.InvitationId,
                i.InviterPartyId,
                i.RequestedPermissions,
                i.IssuedAtUtc,
                i.AbsoluteExpiresAtUtc))
            .ToArray()));
    }

    internal static async Task<IResult> IssueInvitationAsync(
        IAdminTeamAccessAuthority authority,
        IWebAntiforgeryPolicy antiforgery,
        IssueInvitationRequest? request,
        HttpContext context,
        DateTimeOffset at)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (handle, principal) = ReadSelected(context);
        if (handle is null || principal is null)
        {
            return Refused();
        }

        if (request?.RequestedPermissions is null || request.RequestedPermissions.Count == 0 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Results.Json(
                new ErrorResponse("invalid_request", "A permission set and idempotency key are required."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!await antiforgery.ConsumeSelectedAsync(context, handle).ConfigureAwait(false))
        {
            return AntiforgeryFailed();
        }

        // earlier repository ticket #3311 — re-issue the spent token HERE, not on the success branch, so every exit
        // path below answers with a usable one. Rationale on IWebAntiforgeryPolicy.RotateSelectedAsync.
        _ = await antiforgery.RotateSelectedAsync(context, handle).ConfigureAwait(false);

        var result = await authority.IssueInvitationAsync(
                handle,
                principal.TenantId.Value,
                request.RequestedPermissions,
                request.IdempotencyKey,
                WriteAuthority(principal, at),
                context.RequestAborted)
            .ConfigureAwait(false);
        if (result is null)
        {
            return Refused();
        }

        return Results.Ok(new IssuedInvitationResponse(
            result.InvitationId,
            result.Code,
            result.TenantId,
            result.AbsoluteExpiresAtUtc));
    }

    internal static async Task<IResult> RevokeMemberAsync(
        IAdminTeamAccessAuthority authority,
        IWebAntiforgeryPolicy antiforgery,
        RevokeMemberRequest? request,
        HttpContext context,
        DateTimeOffset at)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (handle, principal) = ReadSelected(context);
        if (handle is null || principal is null)
        {
            return Refused();
        }

        if (string.IsNullOrWhiteSpace(request?.GrantId))
        {
            return Results.Json(
                new ErrorResponse("invalid_request", "A target grant id is required."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!await antiforgery.ConsumeSelectedAsync(context, handle).ConfigureAwait(false))
        {
            return AntiforgeryFailed();
        }

        // earlier repository ticket #3311 — re-issue the spent token HERE, not on the success branch, so every exit
        // path below answers with a usable one. Rationale on IWebAntiforgeryPolicy.RotateSelectedAsync.
        _ = await antiforgery.RotateSelectedAsync(context, handle).ConfigureAwait(false);

        var result = await authority
            .RevokeMemberGrantAsync(
                handle,
                principal.TenantId.Value,
                request.GrantId,
                WriteAuthority(principal, at),
                request.SuccessorPrincipalId,
                context.RequestAborted)
            .ConfigureAwait(false);
        if (result is null)
        {
            return Refused();
        }

        return result.Status switch
        {
            AdminRevokeMemberStatus.Revoked => Results.Ok(new RevokeResponse("revoked")),
            // Ledger L618: both legs of the handover committed as one transaction.
            AdminRevokeMemberStatus.HandedOver => Results.Ok(
                new RevokeResponse("handed_over", result.SuccessorGrantId)),
            AdminRevokeMemberStatus.SuccessorRefused => Results.Json(
                new ErrorResponse(
                    "successor_refused",
                    "The nominated successor cannot take over administration of this installation."),
                statusCode: StatusCodes.Status409Conflict),
            AdminRevokeMemberStatus.SelfRevocationRefused => Results.Json(
                new ErrorResponse(
                    "self_revocation_refused",
                    "An administrator cannot revoke the access backing their own session."),
                statusCode: StatusCodes.Status409Conflict),
            // Ledger L619: the install may not be left with zero Administrators in force.
            AdminRevokeMemberStatus.LastAdministratorRefused => Results.Json(
                new ErrorResponse(
                    "last_administrator_refused",
                    "The last administrator in force cannot be revoked; hand over administration first."),
                statusCode: StatusCodes.Status409Conflict),
            // NotFound is deliberately indistinguishable from a non-existent grant — no enumeration.
            _ => Results.Json(
                new ErrorResponse("member_not_found", "No live grant with that id exists in this tenant."),
                statusCode: StatusCodes.Status404NotFound),
        };
    }

    internal static async Task<IResult> UpdateMemberPermissionsAsync(
        IAdminTeamAccessAuthority authority,
        IWebAntiforgeryPolicy antiforgery,
        UpdateMemberPermissionsRequest? request,
        HttpContext context,
        DateTimeOffset at)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (handle, principal) = ReadSelected(context);
        if (handle is null || principal is null)
        {
            return Refused();
        }

        if (string.IsNullOrWhiteSpace(request?.GrantId) ||
            request.RequestedPermissions is null || request.RequestedPermissions.Count == 0)
        {
            return Results.Json(
                new ErrorResponse("invalid_request", "A non-empty permission set and target grant id are required."),
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!await antiforgery.ConsumeSelectedAsync(context, handle).ConfigureAwait(false))
        {
            return AntiforgeryFailed();
        }

        _ = await antiforgery.RotateSelectedAsync(context, handle).ConfigureAwait(false);

        var result = await authority.UpdateMemberPermissionsAsync(
                handle,
                principal.TenantId.Value,
                request.GrantId,
                request.RequestedPermissions,
                WriteAuthority(principal, at),
                context.RequestAborted)
            .ConfigureAwait(false);
        if (result is null)
        {
            return Refused();
        }

        return result.Status switch
        {
            AdminUpdateMemberPermissionsStatus.Updated => Results.Ok(new RevokeResponse("updated")),
            AdminUpdateMemberPermissionsStatus.SelfUpdateRefused => Results.Json(
                new ErrorResponse(
                    "self_update_refused",
                    "An administrator cannot change the access backing their own session."),
                statusCode: StatusCodes.Status409Conflict),
            _ => Results.Json(
                new ErrorResponse("member_not_found", "No live grant with that id exists in this tenant."),
                statusCode: StatusCodes.Status404NotFound),
        };
    }

    private static (string? Handle, SelectedSessionRequestPrincipal? Principal) ReadSelected(
        HttpContext context)
    {
        var handle = context.Request.Cookies[WebSessionCookieNames.Selected];
        if (string.IsNullOrWhiteSpace(handle))
        {
            return (null, null);
        }

        // Defense in depth: the listener gate publishes this feature before the handler runs; a missing
        // principal means the route was reached off its intended path. Treat identically to no cookie.
        var principal = context.Features.Get<SelectedSessionRequestPrincipal>();
        return principal is null ? (null, null) : (handle, principal);
    }

    private static AuthorizationWriteContext WriteAuthority(
        SelectedSessionRequestPrincipal principal,
        DateTimeOffset at) =>
        new(NodeGatePrincipal.Of(principal), principal.TenantId, at);

    private static IResult AntiforgeryFailed() =>
        Results.Json(
            new ErrorResponse("antiforgery_failed", "Antiforgery validation failed."),
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult Refused() =>
        Results.Json(
            new ErrorResponse("admin_access_denied", "Admin team access denied."),
            statusCode: StatusCodes.Status401Unauthorized);
}
