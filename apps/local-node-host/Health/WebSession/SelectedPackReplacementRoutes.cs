using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>Orchestrates immutable draft installation and the existing governed activation.</summary>
internal static class SelectedPackReplacementRoutes
{
    private const int MaximumBytes = 10 * 1024 * 1024;
    internal static ViewRequestDescriptor ReplaceRequest { get; } = new(
        "packs.replace.selected.v1", "POST", "/api/session/packs/{packKey}/replace", "application/octet-stream",
        "selected-session", true, Permission.PackagesOperate,
        [new("packKey", ViewRequestValueKind.Text, ViewRequestPlacement.Path, "packKey"),
            new("artifact", ViewRequestValueKind.Binary, ViewRequestPlacement.BodyRoot, ""),
            new("correlationId", ViewRequestValueKind.Text, ViewRequestPlacement.Header, "X-Correlation-ID")]);

    internal static void Map(IEndpointRouteBuilder app, IPackInstaller installer, IPackInstallStore store,
        IPackTrustStore trust, IPackRevocationList revocation, IWebAntiforgeryPolicy antiforgery,
        TimeProvider time, AuthorizedActAudit? audit) =>
        app.MapPost(ReplaceRequest.RouteTemplate, (string packKey, HttpContext http, CancellationToken ct) =>
            ReplaceAsync(http, packKey, installer, store, trust, revocation, antiforgery, time, audit, ct));

    internal static async Task<IResult> ReplaceAsync(HttpContext http, string packKey,
        IPackInstaller installer, IPackInstallStore store, IPackTrustStore trust, IPackRevocationList revocation,
        IWebAntiforgeryPolicy antiforgery, TimeProvider time, AuthorizedActAudit? audit, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        var principal = http.Features.Get<SelectedSessionRequestPrincipal>();
        var handle = http.Request.Cookies[WebSessionCookieNames.Selected];
        if (principal is null || principal.TenantId.IsSystemSentinel || string.IsNullOrWhiteSpace(handle))
            return Results.Unauthorized();
        if (SelectedRequestCorrelation.Bind(http) is { } invalidCorrelation) return invalidCorrelation;
        if (!await antiforgery.ConsumeSelectedAsync(http, handle).ConfigureAwait(false))
            return Results.Json(new { code = "antiforgery_failed" }, statusCode: 403);
        _ = await antiforgery.RotateSelectedAsync(http, handle).ConfigureAwait(false);
        var authority = RequestAuthorization.Authority(http, principal.TenantId, time);
        AuthorizationDecision? allowed = null;
        if (await RequestAuthorization.RefusalAsync(http, authority, ReplaceRequest.AuthorizationCapability,
            RouteRecord.Of(packKey), ct, decision => allowed = decision).ConfigureAwait(false) is { } denial)
            return denial;
        if (!string.Equals(http.Request.ContentType, ReplaceRequest.ContentType, StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (http.Request.ContentLength > MaximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var body = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await http.Request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (body.Length + count > MaximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            await body.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
        }
        var bytes = body.ToArray();
        var context = new PackInstallContext(principal.TenantId, trust, revocation, authority.At,
            PackInstallRoutes.RevocationMaxAge, Principal: authority.Principal.Value);
        var before = Snapshot(store.GetActive(principal.TenantId, packKey));
        var preview = installer.Preview(bytes, context);
        // Naming comes only from the verified artifact. A path cannot redirect installation to another pack.
        if (!string.Equals(preview.PackKey, packKey, StringComparison.Ordinal))
            return Results.UnprocessableEntity(new { code = "pack_replacement.artifact_identity_refused",
                refusalCodes = preview.RefusalCodes, activeBefore = before, activeAfter = before });
        PackInstallOutcome? installed = null;
        PackActivationOutcome? activation = null;
        object? refusal = null;
        var authorizationRefused = false;
        try
        {
            installed = installer.Install(bytes, context);
            if (installed.Installed) activation = installer.Activate(context, packKey, installed.Version);
        }
        catch (AuthorizationDeniedException denied)
        {
            authorizationRefused = true;
            var result = await RequestAuthorization.RefusedAsync(http, denied, ct).ConfigureAwait(false);
            refusal = (result as IValueHttpResult)?.Value;
        }
        if (activation?.Refusal is { } projectionRefusal)
            refusal = new { code = projectionRefusal.Code, pointer = projectionRefusal.Pointer };
        var projected = activation?.Projected == true &&
            activation.ProjectionResult is not IPackProjectionRefusalReport { ProjectionRefused: true };
        var replaced = activation?.Activated == true && projected;
        Guid? auditId = null;
        if (allowed is not null && audit is not null)
            auditId = await audit.RecordAsync(new AuditEventType("PackReplacementAttempt"), allowed,
                new Dictionary<string, object?> { ["packKey"] = packKey, ["version"] = installed?.Version,
                    ["draftInstalled"] = installed?.Installed == true, ["replaced"] = replaced }, ct).ConfigureAwait(false);
        if (auditId is { } recorded) http.Response.Headers["X-Harborline-Audit-Id"] = recorded.ToString("D");
        var correlationId = auditId is not null ? authority.CorrelationId : null;
        if (correlationId is { } correlated) http.Response.Headers["X-Harborline-Audit-Correlation"] = correlated.ToString("D");
        return Results.Json(new
        {
            status = replaced ? "replaced" : "refused", packKey, auditId, correlationId,
            draftInstall = new { installed = installed?.Installed == true, version = installed?.Version,
                refusalCodes = installed?.RefusalCodes ?? [], action = installed?.Action.ToString() },
            activation = new { attempted = activation is not null, activated = activation?.Activated == true,
                projected, error = activation?.Error, detail = activation?.Detail,
                projection = activation?.ProjectionResult, refusal },
            activeBefore = before, activeAfter = Snapshot(store.GetActive(principal.TenantId, packKey)),
        }, statusCode: replaced ? 200 : authorizationRefused ? 403 : 422);
    }

    // This is the active immutable declaration, not a substitute for runtime-catalogue verification.
    private static object? Snapshot(InstalledPack? pack) => pack is null ? null : new
    {
        version = pack.Version,
        declaredDefinitions = pack.SeedItems.OrderBy(item => item.Kind).ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new { kind = item.Kind.ToString(), key = item.Key, version = item.Version,
                contentAddress = item.ContentAddress.ToString() }).ToArray(),
    };
}
