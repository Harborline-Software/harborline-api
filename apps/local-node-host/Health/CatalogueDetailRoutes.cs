using System.Text.Json;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Harborline.Api.LocalNodeHost.Health;

public static class CatalogueDetailRoutes
{
    public const string Route = "/api/local-node/catalogue/details/{detailId}/{detailVersion}";

    public static IServiceCollection AddCatalogueFieldSourceRuntime(this IServiceCollection services, string platformVersion)
    {
        services.TryAddSingleton<CatalogueDetailTemplates>();
        services.TryAddSingleton(sp => new CatalogueDetailRuntime(
            sp.GetRequiredService<AuthorizedFormDefinitionLifecycle>().CatalogueSources,
            sp.GetRequiredService<CatalogueDetailTemplates>(), sp.GetRequiredService<AuthorizationGate>()));
        services.AddSingleton(sp =>
        {
            _ = sp.GetRequiredService<CatalogueDetailRuntime>();
            return new CatalogueFieldSourceAdmission(CatalogueDetailRuntime.Supports);
        });
        services.AddSingleton<IPackPlatformCompatibility>(sp =>
        {
            // Resolving the implemented reader is mandatory; declaration parsing alone adds no capability.
            _ = sp.GetRequiredService<CatalogueDetailRuntime>();
            return new PackPlatformCompatibility(platformVersion, PackSeedProjector.RegisteredCases.Select(projector =>
                projector.ContentKind == PackContentKind.FormDefinition
                    ? new PackProjectorCase(projector.ContentKind, projector.Capabilities.Append(CatalogueFieldSourceContract.CapabilityId).ToArray())
                    : projector).ToArray());
        });
        return services;
    }

    internal static void Map(IEndpointRouteBuilder app, CatalogueDetailRuntime runtime)
    {
        app.MapPost(Route, async Task<IResult> (string detailId, string detailVersion, HttpContext http, CancellationToken ct) =>
        {
            var principal = http.Features.Get<SelectedSessionRequestPrincipal>();
            var clock = http.RequestServices.GetService<TimeProvider>();
            if (principal is null || clock is null) return RequestAuthorization.Denied(Permission.CatalogueRead);
            try
            {
                using var document = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: ct).ConfigureAwait(false);
                var refusals = new List<object>();
                var projection = await runtime.ProjectAsync(detailId, detailVersion, document.RootElement,
                    RequestAuthorization.Authority(http, principal.TenantId, clock), async (decision, token) =>
                {
                    // Do not ask the gate again to render a denied field or retry its parent target.
                    var refusal = await AuthorizationRefusalRenderer.RenderAsync(decision, [], null, token).ConfigureAwait(false);
                    var request = decision.Request;
                    var audit = http.RequestServices.GetService<AuthorizationRefusalAudit>();
                    var auditId = audit is null ? null : await audit.RecordAsync(refusal, Permission.CatalogueRead,
                        request.Principal, request.Tenant, request.At, decision, token).ConfigureAwait(false);
                    refusals.Add(new { refusal.Code, auditId });
                }, ct).ConfigureAwait(false);
                return Results.Ok(new { projection, refusals });
            }
            catch (CatalogueFieldSourceException exception)
            { return Results.UnprocessableEntity(new { code = exception.Code }); }
            catch (JsonException)
            { return Results.BadRequest(new { code = CatalogueFieldSourceCodes.MalformedCoordinate }); }
        });
    }
}
