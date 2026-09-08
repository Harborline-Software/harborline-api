using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>Challenge-audience-only tenant selection.</summary>
internal static class TenantSelectionRoutes
{
    internal const string SelectPath = "/api/session/select";

    internal sealed record SelectRequest(string? TenantId);

    private sealed record SelectResponse(
        string TenantId,
        string DisplayName,
        DateTimeOffset ExpiresAt);

    private sealed record ErrorResponse(string Error, string Message);

    internal static void Map(
        IEndpointRouteBuilder app,
        IWebTenantSelectionAuthority authority,
        IWebAntiforgeryPolicy antiforgery)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(antiforgery);
        app.MapPost(
            SelectPath,
            (SelectRequest request, HttpContext context) =>
                SelectAsync(authority, antiforgery, request, context));
    }

    internal static async Task<IResult> SelectAsync(
        IWebTenantSelectionAuthority authority,
        IWebAntiforgeryPolicy antiforgery,
        SelectRequest? request,
        HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var challengeHandle = context.Request.Cookies[WebSessionCookieNames.Challenge];
        if (string.IsNullOrWhiteSpace(challengeHandle))
        {
            return Results.Json(
                new ErrorResponse("selection_failed", "Tenant selection failed."),
                statusCode: StatusCodes.Status401Unauthorized);
        }
        if (!await antiforgery.ConsumeChallengeAsync(context, challengeHandle).ConfigureAwait(false))
        {
            return Results.Json(
                new ErrorResponse("antiforgery_failed", "Antiforgery validation failed."),
                statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await authority.SelectAsync(
                challengeHandle,
                request?.TenantId,
                context.RequestAborted)
            .ConfigureAwait(false);
        if (result is null)
        {
            _ = await antiforgery.RotateChallengeAsync(context, challengeHandle).ConfigureAwait(false);
            return Results.Json(
                new ErrorResponse("selection_failed", "Tenant selection failed."),
                statusCode: StatusCodes.Status401Unauthorized);
        }

        context.Response.Cookies.Append(
            WebSessionCookieNames.Selected,
            result.Handle,
            WebSessionCookieNames.For(result.ExpiresAtUtc));
        antiforgery.EmitToken(context.Response, result.AntiforgeryToken);
        context.Response.Cookies.Delete(
            WebSessionCookieNames.Challenge,
            WebSessionCookieNames.ForDeletion());
        return Results.Ok(new SelectResponse(
            result.TenantId,
            result.DisplayName,
            result.ExpiresAtUtc));
    }
}
