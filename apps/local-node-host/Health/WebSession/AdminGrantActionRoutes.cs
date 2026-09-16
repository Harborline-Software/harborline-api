using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

internal static partial class AdminTeamAccessRoutes
{
    internal static ViewRequestDescriptor RevokeGrantRequest { get; } = new(
        "authorization.grant.revoke.v1", "POST", "/api/session/admin/grants/revoke", "application/json",
        "selected-session", true, TeamRolePermissions.MembersManage,
        [new("grantId", ViewRequestValueKind.Text, ViewRequestPlacement.BodyField, "grantId")]);

    internal static ViewRequestDescriptor NarrowScopeRequest { get; } = new(
        "authorization.grant.narrow-scope.v1", "POST", "/api/session/admin/grants/narrow-scope", "application/json",
        "selected-session", true, TeamRolePermissions.MembersManage,
        [new("grantId", ViewRequestValueKind.Text, ViewRequestPlacement.BodyField, "grantId"),
            new("scope", ViewRequestValueKind.Text, ViewRequestPlacement.BodyField, "scope"),
            new("successorId", ViewRequestValueKind.Text, ViewRequestPlacement.BodyField, "successorId")]);

    internal static ViewRequestDescriptor ReviewGrantRequest { get; } = new(
        "authorization.grant.review.v1", "POST", "/api/session/admin/grants/review", "application/json",
        "selected-session", true, TeamRolePermissions.MembersManage,
        [new("grantId", ViewRequestValueKind.Text, ViewRequestPlacement.BodyField, "grantId")]);

    internal sealed record GrantBody(string? GrantId);
    internal sealed record NarrowScopeBody(string? GrantId, string? Scope, string? SuccessorId);
    private sealed record GrantActionResponse(string Status, string? GrantId, Guid? AuditId,
        Guid? CorrelationId = null, DateTimeOffset? ReviewedAt = null);

    private static void MapGrantActions(IEndpointRouteBuilder app, IAdminTeamAccessAuthority authority,
        IWebAntiforgeryPolicy antiforgery, TimeProvider time)
    {
        AccessHoldersRead.MapSelected(app, time);
        app.MapPost(ReviewGrantRequest.RouteTemplate, (GrantBody? request, HttpContext context) =>
            ReviewGrantAsync(authority, antiforgery, request, context, time.GetUtcNow()));
        app.MapPost(RevokeGrantRequest.RouteTemplate, (GrantBody? request, HttpContext context) =>
            RevokeGrantAsync(authority, antiforgery, request, context, time.GetUtcNow()));
        app.MapPost(NarrowScopeRequest.RouteTemplate, (NarrowScopeBody? request, HttpContext context) =>
            NarrowScopeAsync(authority, antiforgery, request, context, time.GetUtcNow()));
    }

    internal static async Task<IResult> ReviewGrantAsync(IAdminTeamAccessAuthority authority,
        IWebAntiforgeryPolicy antiforgery, GrantBody? request, HttpContext context, DateTimeOffset at)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (handle, principal) = ReadSelected(context);
        if (handle is null || principal is null) return Refused();
        if (!Guid.TryParse(request?.GrantId, out var grant) || grant == Guid.Empty) return InvalidGrantAction();
        if (!await antiforgery.ConsumeSelectedAsync(context, handle).ConfigureAwait(false)) return AntiforgeryFailed();
        _ = await antiforgery.RotateSelectedAsync(context, handle).ConfigureAwait(false);
        try
        {
            var result = await authority.ReviewGrantAsync(handle, principal.TenantId.Value, grant.ToString("D"),
                WriteAuthority(principal, at), context.RequestAborted).ConfigureAwait(false);
            return result is null ? GrantActionNotFound() : GrantActionSucceeded(context,
                new("reviewed", grant.ToString("D"), result.AuditId, result.CorrelationId, result.ReviewedAt));
        }
        catch (AuthorizationDeniedException denial)
        {
            return await RequestAuthorization.RefusedAsync(context, denial, context.RequestAborted).ConfigureAwait(false);
        }
    }

    internal static async Task<IResult> RevokeGrantAsync(IAdminTeamAccessAuthority authority,
        IWebAntiforgeryPolicy antiforgery, GrantBody? request, HttpContext context, DateTimeOffset at)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (handle, principal) = ReadSelected(context);
        if (handle is null || principal is null) return Refused();
        if (!Guid.TryParse(request?.GrantId, out var grant) || grant == Guid.Empty) return InvalidGrantAction();
        if (!await antiforgery.ConsumeSelectedAsync(context, handle).ConfigureAwait(false)) return AntiforgeryFailed();
        _ = await antiforgery.RotateSelectedAsync(context, handle).ConfigureAwait(false);
        try
        {
            var result = await authority.RevokeGrantAsync(handle, principal.TenantId.Value, grant.ToString("D"),
                WriteAuthority(principal, at), context.RequestAborted).ConfigureAwait(false);
            if (result is null) return Refused();
            return result.Status switch
            {
                AdminRevokeMemberStatus.Revoked => GrantActionSucceeded(context,
                    new("revoked", grant.ToString("D"), result.AuditId)),
                AdminRevokeMemberStatus.SelfRevocationRefused => GrantActionConflict("self_revocation_refused"),
                AdminRevokeMemberStatus.LastAdministratorRefused => GrantActionConflict("last_administrator_refused"),
                _ => GrantActionNotFound(),
            };
        }
        catch (AuthorizationDeniedException denial)
        {
            return await RequestAuthorization.RefusedAsync(context, denial, context.RequestAborted).ConfigureAwait(false);
        }
    }

    internal static async Task<IResult> NarrowScopeAsync(IAdminTeamAccessAuthority authority,
        IWebAntiforgeryPolicy antiforgery, NarrowScopeBody? request, HttpContext context, DateTimeOffset at)
    {
        context.Response.Headers.CacheControl = "no-store";
        var (handle, principal) = ReadSelected(context);
        if (handle is null || principal is null) return Refused();
        if (!Guid.TryParse(request?.GrantId, out var grant) || grant == Guid.Empty
            || !Guid.TryParse(request.SuccessorId, out var successor) || successor == Guid.Empty
            || string.IsNullOrWhiteSpace(request.Scope)) return InvalidGrantAction();
        ScopeExpression scope;
        try { scope = ScopeExpression.Parse(request.Scope); }
        catch (ArgumentException) { return InvalidGrantAction(); }
        if (!await antiforgery.ConsumeSelectedAsync(context, handle).ConfigureAwait(false)) return AntiforgeryFailed();
        _ = await antiforgery.RotateSelectedAsync(context, handle).ConfigureAwait(false);
        try
        {
            var result = await authority.NarrowMemberScopeAsync(handle, principal.TenantId.Value, grant.ToString("D"),
                scope, new GrantId(successor), WriteAuthority(principal, at), context.RequestAborted).ConfigureAwait(false);
            if (result is null) return Refused();
            return result.Status switch
            {
                AdminNarrowMemberGrantStatus.Narrowed => GrantActionSucceeded(context,
                    new("narrowed", result.NarrowedGrantId, result.AuditId, result.CorrelationId)),
                AdminNarrowMemberGrantStatus.NotASubset => GrantActionConflict("not_a_subset"),
                AdminNarrowMemberGrantStatus.SelfNarrowRefused => GrantActionConflict("self_narrow_refused"),
                _ => GrantActionNotFound(),
            };
        }
        catch (AuthorizationDeniedException denial)
        {
            return await RequestAuthorization.RefusedAsync(context, denial, context.RequestAborted).ConfigureAwait(false);
        }
        catch (LastAdministratorRefusedException) { return GrantActionConflict("last_administrator_refused"); }
    }

    private static IResult GrantActionSucceeded(HttpContext context, GrantActionResponse response)
    {
        if (response.AuditId is { } audit) context.Response.Headers["X-Harborline-Audit-Id"] = audit.ToString("D");
        if (response.CorrelationId is { } correlation)
            context.Response.Headers["X-Harborline-Audit-Correlation"] = correlation.ToString("D");
        return Results.Ok(response);
    }

    private static IResult InvalidGrantAction() => Results.Json(
        new ErrorResponse("invalid_request", "Valid grant action inputs are required."), statusCode: 400);
    private static IResult GrantActionConflict(string error) => Results.Json(
        new ErrorResponse(error, "The grant action was refused; no grant was changed."), statusCode: 409);
    private static IResult GrantActionNotFound() => Results.Json(
        new ErrorResponse("grant_not_found", "No eligible grant exists in this tenant."), statusCode: 404);
}
