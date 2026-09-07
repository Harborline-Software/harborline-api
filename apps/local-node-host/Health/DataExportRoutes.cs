using System.Text.Json.Serialization;

using Harborline.Api.Foundation.LocalFirst;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.LocalNodeHost.Data.Identity;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>Maps the active tenant's portability export lifecycle.</summary>
public static class DataExportRoutes
{
    /// <summary>Base path for starting and polling portability exports.</summary>
    public const string RouteBase = "/api/local-node/data-exports";

    /// <summary>Maps the routes in dev mode without per-route caller authentication.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IDataExportService exports,
        ITenantContext tenantContext) =>
        Map(app, exports, tenantContext, new NodeCallerSessionToken(null));

    /// <summary>Maps the portability export routes with caller authentication.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IDataExportService exports,
        ITenantContext tenantContext,
        NodeCallerSessionToken callerAuth)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(callerAuth);

        app.MapPost(RouteBase, async (
            HttpRequest httpRequest,
            StartDataExportRequest request,
            CancellationToken cancellationToken) =>
        {
            if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
            {
                return NodeCallerSessionToken.RejectResult();
            }

            var tenant = httpRequest.HttpContext.Features.Get<SelectedSessionRequestPrincipal>()?.TenantId
                ?? tenantContext.Tenant?.Id
                ?? throw new InvalidOperationException("No tenant is resolved for the export request.");
            var handle = await exports.StartExportAsync(new ExportRequest
            {
                Tenant = TenantSelection.Of(tenant),
                Format = request.Format,
                IncludeScopes = request.IncludeScopes,
            }, cancellationToken).ConfigureAwait(false);

            return Results.Accepted($"{RouteBase}/{handle.ExportId}", handle);
        });

        app.MapGet($"{RouteBase}/{{exportId:guid}}", async (
            HttpRequest httpRequest,
            Guid exportId,
            CancellationToken cancellationToken) =>
        {
            if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
            {
                return NodeCallerSessionToken.RejectResult();
            }

            try
            {
                return Results.Ok(await exports.GetStatusAsync(exportId, cancellationToken).ConfigureAwait(false));
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        app.MapGet($"{RouteBase}/{{exportId:guid}}/download", async (
            HttpRequest httpRequest,
            Guid exportId,
            CancellationToken cancellationToken) =>
        {
            if (callerAuth.Validate(httpRequest) is NodeCallerSessionToken.Decision.Reject)
            {
                return NodeCallerSessionToken.RejectResult();
            }

            try
            {
                var download = await exports.OpenDownloadAsync(exportId, cancellationToken).ConfigureAwait(false);
                return Results.File(
                    download,
                    "application/json",
                    $"harborline-portability-{exportId:D}.json");
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException)
            {
                return Results.Conflict();
            }
        });
    }
}

/// <summary>Wire request for an active-tenant portability export.</summary>
/// <param name="Format">Requested package media type.</param>
/// <param name="IncludeScopes">Coarse local-store scopes to include; empty includes all.</param>
public sealed record StartDataExportRequest(
    [property: JsonPropertyName("format")] string Format = "application/json",
    [property: JsonPropertyName("includeScopes")] IReadOnlyList<string>? Scopes = null)
{
    /// <summary>Normalized requested scopes.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> IncludeScopes => Scopes ?? Array.Empty<string>();
}
