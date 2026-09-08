using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>Selected-audience-only tenant switch.</summary>
internal static class TenantSwitchRoutes
{
    internal const string SwitchPath = "/api/session/switch";

    internal sealed record SwitchRequest(string? TenantId);

    private sealed record SwitchResponse(
        string TenantId,
        string DisplayName,
        DateTimeOffset ExpiresAt);

    private sealed record ErrorResponse(string Error, string Message);

    internal static void Map(
        IEndpointRouteBuilder app,
        IWebTenantSwitchAuthority authority,
        IWebAntiforgeryPolicy antiforgery)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(antiforgery);
        app.MapPost(
            SwitchPath,
            (SwitchRequest request, HttpContext context) =>
                SwitchAsync(authority, antiforgery, request, context));
    }

    internal static async Task<IResult> SwitchAsync(
        IWebTenantSwitchAuthority authority,
        IWebAntiforgeryPolicy antiforgery,
        SwitchRequest? request,
        HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(antiforgery);
        ArgumentNullException.ThrowIfNull(context);
        context.Response.Headers.CacheControl = "no-store";
        var selectedHandle = context.Request.Cookies[WebSessionCookieNames.Selected];
        if (string.IsNullOrWhiteSpace(selectedHandle))
        {
            return Refused();
        }
        if (!await antiforgery.ConsumeSelectedAsync(context, selectedHandle).ConfigureAwait(false))
        {
            return Results.Json(
                new ErrorResponse("antiforgery_failed", "Antiforgery validation failed."),
                statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await authority.SwitchAsync(
                selectedHandle,
                request?.TenantId,
                context.RequestAborted)
            .ConfigureAwait(false);
        if (result is null)
        {
            _ = await antiforgery.RotateSelectedAsync(context, selectedHandle).ConfigureAwait(false);
            return Refused();
        }

        context.Response.Cookies.Append(
            WebSessionCookieNames.Selected,
            result.Handle,
            WebSessionCookieNames.For(result.ExpiresAtUtc));
        antiforgery.EmitToken(context.Response, result.AntiforgeryToken);
        return Results.Ok(new SwitchResponse(
            result.TenantId,
            result.DisplayName,
            result.ExpiresAtUtc));
    }

    private static IResult Refused() =>
        Results.Json(
            new ErrorResponse("switch_failed", "Tenant switch failed."),
            statusCode: StatusCodes.Status401Unauthorized);
}
