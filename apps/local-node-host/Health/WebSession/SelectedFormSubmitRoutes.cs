using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Engine.Exceptions;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.RuleEngine;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>Selected-session submissions use the existing form engine without desktop role borrowing.</summary>
internal static class SelectedFormSubmitRoutes
{
    internal static ViewRequestDescriptor SubmitRequest { get; } = new(
        "forms.submit.selected.v1", "POST", "/api/session/forms/{formId}/submit", "application/json",
        "selected-session", true, Permission.FormsAuthor,
        [new("formId", ViewRequestValueKind.Text, ViewRequestPlacement.Path, "formId"),
            new("values", ViewRequestValueKind.Object, ViewRequestPlacement.BodyRoot, ""),
            new("idempotencyKey", ViewRequestValueKind.Text, ViewRequestPlacement.Header, "Idempotency-Key")]);

    internal static void Map(IEndpointRouteBuilder app, IFormEngine engine,
        IFormCapabilityIssuer issuer, IFormCapabilityVerifier verifier, IFormSubmissionGate submissionGate,
        IWebAntiforgeryPolicy antiforgery, TimeProvider time, IAuditTrail? audit = null)
    {
        app.MapPost(SubmitRequest.RouteTemplate, async (string formId, JsonElement body, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var principal = http.Features.Get<SelectedSessionRequestPrincipal>();
            var handle = http.Request.Cookies[WebSessionCookieNames.Selected];
            if (principal is null || principal.TenantId.IsSystemSentinel || string.IsNullOrWhiteSpace(handle))
                return Results.Unauthorized();
            if (!await antiforgery.ConsumeSelectedAsync(http, handle).ConfigureAwait(false))
                return Results.Json(new { code = "antiforgery_failed" }, statusCode: StatusCodes.Status403Forbidden);
            _ = await antiforgery.RotateSelectedAsync(http, handle).ConfigureAwait(false);
            if (body.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { code = "forms.body_must_be_json_object" });
            if (!FormsRoutes.TryReadIdempotencyKey(http.Request, out var key, out var keyError)) return keyError!;
            if (key is null) return Results.BadRequest(new { code = "forms.idempotency_key_required" });

            var form = new FormDefinitionId(formId);
            // Only a host-registered submission gate can mint section capability roles. Pack data cannot
            // choose them; unregistered forms stay unavailable on this surface.
            if (submissionGate.RequiredPermission(form) is not { } permission) return Results.NotFound();
            var authority = RequestAuthorization.Authority(http, principal.TenantId, time);
            if (await RequestAuthorization.RefusalAsync(http, authority, permission, RouteRecord.TheInstall, ct)
                .ConfigureAwait(false) is { } refused) return refused;
            try
            {
                var bearer = await issuer.IssueAsync(principal.TenantId, NodeGatePrincipal.Of(principal),
                    submissionGate.CapabilityRoles(form), [FormCapabilityAction.Write], authority.At.AddMinutes(5), ct)
                    .ConfigureAwait(false);
                var token = await verifier.VerifyAsync(bearer, authority.At, ct).ConfigureAwait(false);
                using var candidate = JsonDocument.Parse(body.GetRawText());
                var receipt = await engine.SaveWithReceiptAsync(form, candidate, token, authority, ct, key)
                    .ConfigureAwait(false);
                var auditReceipt = audit is null ? null : await FormSubmissionAuditReceipt.ReadAsync(
                    audit, form, principal.TenantId, authority.Principal, receipt, ct).ConfigureAwait(false);
                if (auditReceipt is { } recorded)
                {
                    http.Response.Headers["X-Harborline-Audit-Id"] = recorded.AuditId.ToString("D");
                    http.Response.Headers["X-Harborline-Audit-Correlation"] = recorded.CorrelationId.ToString("D");
                }
                var result = submissionGate is IFormSubmissionResultReader reader
                    ? await reader.ReadResultAsync(form, principal.TenantId, receipt.InstanceId, ct).ConfigureAwait(false) : null;
                return Results.Created($"/api/session/forms/{Uri.EscapeDataString(formId)}?instance={Uri.EscapeDataString(receipt.InstanceId.ToString())}",
                    new FormSubmitResponse(receipt.InstanceId.ToString(),
                        receipt.Skips.Count == 0 ? null : FormSubmitResponse.ProjectionSkipped,
                        receipt.Skips.Count == 0 ? null : receipt.Skips.Select(s => new FormSubmitSkipDto(s.Reason, s.FieldPointer)).ToArray(),
                        auditReceipt?.AuditId, auditReceipt?.CorrelationId, result));
            }
            catch (AuthorizationDeniedException denial)
            {
                return await RequestAuthorization.RefusedAsync(http, denial, ct).ConfigureAwait(false);
            }
            catch (FormSubmitProjectionPendingException pending)
            {
                return Results.Json(new FormSubmitResponse(pending.Receipt.InstanceId.ToString(),
                    FormSubmitResponse.ProjectionPending), statusCode: StatusCodes.Status202Accepted);
            }
            catch (FormDefinitionNotFoundException)
            {
                return Results.NotFound(new { code = "form_definition.not_published" });
            }
            catch (FormValidationException invalid)
            {
                return Results.UnprocessableEntity(ValidationResultDto.From(invalid.Result));
            }
            catch (CapabilityDeniedException)
            {
                return Results.Json(new { code = "forms.capability_denied" }, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (RuleEngineTimeoutException)
            {
                return Results.Json(new { code = RuleEngineCodes.Timeout }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
