using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;
using Harborline.Api.LocalNodeHost.Data.Identity;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Maps the operator's compromised-device containment and accounting surface.</summary>
public static class CompromisedDeviceResponseRoutes
{
    /// <summary>The route prefix for compromised-device responses.</summary>
    public const string RouteBase = "/api/local-node/compromised-devices";

    /// <summary>Maps the response route without per-route caller authentication.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        ICompromisedDeviceResponseService responses,
        TimeProvider timeProvider) =>
        Map(app, responses, new NodeCallerSessionToken(null), timeProvider);

    /// <summary>Maps the response route with caller authentication.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        ICompromisedDeviceResponseService responses,
        NodeCallerSessionToken callerAuth,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(responses);
        ArgumentNullException.ThrowIfNull(callerAuth);
        ArgumentNullException.ThrowIfNull(timeProvider);

        app.MapPost($"{RouteBase}/{{partyId}}/respond", async (
            HttpContext context,
            string partyId,
            CompromisedDeviceResponseHttpRequest request,
            CancellationToken cancellationToken) =>
        {
            if (callerAuth.Validate(context.Request) is NodeCallerSessionToken.Decision.Reject)
            {
                return NodeCallerSessionToken.RejectResult();
            }

            var principal = context.Features.Get<SelectedSessionRequestPrincipal>();
            if (principal is null) return Results.Unauthorized();

            var result = await responses.RespondAsync(new CompromisedDeviceResponseRequest(
                request.TeamId,
                partyId,
                request.RevokedByPartyId), new AuthorizationWriteContext(
                    NodeGatePrincipal.Of(principal),
                    principal.TenantId,
                    timeProvider.GetUtcNow()), cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        });
    }
}

/// <summary>Wire request for a compromised-device response.</summary>
/// <param name="TeamId">Team whose member device is being revoked.</param>
/// <param name="RevokedByPartyId">Roster administrator signing the revocation.</param>
public sealed record CompromisedDeviceResponseHttpRequest(
    [property: JsonPropertyName("teamId")] string TeamId,
    [property: JsonPropertyName("revokedByPartyId")] string RevokedByPartyId);
