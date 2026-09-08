using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Forms.Engine.Exceptions;
using Harborline.Api.Foundation.Forms.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// ADR 0055 — node-local dynamic-FORMS route mapping (the "wire the
/// built-but-unwired engine" amendment, 2026-06-25). Exposes the
/// <see cref="IFormEngine"/> render / submit surface the React
/// <c>SchemaForm</c> renderer consumes over the Harborline App→local-node-host HTTP
/// channel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Routes:</b>
/// <list type="bullet">
///   <item><c>GET  /api/local-node/forms/{formId}</c> (optional
///     <c>?instance={entityId}</c>) — renders the current published
///     <c>FormDefinition</c> into a localized <see cref="FormView"/>. With an
///     instance, the view is populated from that instance (PII / role-gated per
///     the engine's view-scope rule).</item>
///   <item><c>POST /api/local-node/forms/{formId}/submit</c> — validates the
///     JSON request body against the form's schema + resource bounds, enforces
///     section-level write authorization, encrypts PII at rest, and persists a
///     new form-instance entity; returns the new instance id.</item>
/// </list>
/// </para>
/// <para>
/// <b>Capability anchor (INV-S1).</b> The route does NOT pass an ambient tenant
/// to the engine — it mints a verified <see cref="CapabilityToken"/> for the
/// active-team tenant (<see cref="NodeTenant"/>) and the ACTING MEMBER resolved
/// per request from the request's selected-session principal (#3378),
/// granting <see cref="FormCapabilityAction.Read"/> on GET and
/// <see cref="FormCapabilityAction.Write"/> on POST, then hands the engine that
/// token. Minting goes through the real macaroon issuer→verifier round-trip
/// (the verifier is the ONLY component that can mint a token), so the route
/// never fabricates a capability — it exercises the same fail-closed path a
/// remote caller would.
/// </para>
/// <para>
/// <b>CP-safety (first-party only).</b> This serves <c>FormDefinition</c>s the
/// node itself registered (first-party). A packet-carried / third-party form
/// (which could write a CP-locked field) requires the fail-closed load-time
/// CP-reachability validator shared with ADR-0135 A1 — that gate is a separate,
/// later item (see the ADR 0055 amendment CP-safety follow-up); nothing on this
/// route loads an externally-supplied definition.
/// </para>
/// <para>
/// The engine + issuer + verifier are injected from the OUTER host container and
/// closed over by the route handlers — NOT resolved via <c>[FromServices]</c>,
/// because the routes are mapped on <see cref="SharedHostedWebApp"/>'s inner
/// <see cref="WebApplication"/> whose service provider is a SEPARATE container
/// (bug-2849 trap).
/// </para>
/// </remarks>
public static class FormsRoutes
{
    /// <summary>Canonical route base for the node-local dynamic-forms surface.</summary>
    public const string RouteBase = "/api/local-node/forms";

    /// <summary>The single-operator role granted on the minted capability when the
    /// node has no resolved <c>ICurrentUser</c> role list. First-party forms whose
    /// sections gate on this role are readable/writable by the local operator.</summary>
    public const string NodeOperatorRole = "node:operator";

    /// <summary>Capability lifetime for a single request's minted token.</summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps the forms routes onto <paramref name="app"/>, closing over the engine,
    /// the capability issuer/verifier, the active-team accessor, and the host-wide
    /// role list from the outer host container. The SUBJECT is deliberately NOT
    /// closed over: it is resolved per request (see <c>ActingSubject</c>), because a
    /// value captured at map time is the same for every member who ever acts.
    /// </summary>
    public static void Map(
        IEndpointRouteBuilder app,
        IFormEngine engine,
        IFormCapabilityIssuer issuer,
        IFormCapabilityVerifier verifier,
        IActiveTeamAccessor activeTeam,
        IReadOnlyList<string> operatorRoles,
        TimeProvider timeProvider,
        IFormSubmissionGate? submissionGate = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(operatorRoles);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var roles = operatorRoles.Count > 0 ? operatorRoles : new[] { NodeOperatorRole };

        // GET /api/local-node/forms/{formId}[?instance=...] — render a form view.
        app.MapGet($"{RouteBase}/{{formId}}", async (
            string formId,
            string? instance,
            HttpContext http,
            CancellationToken ct) =>
        {
            EntityId? instanceId = null;
            if (!string.IsNullOrWhiteSpace(instance))
            {
                if (!EntityId.TryParse(instance, out var parsed))
                {
                    return Results.BadRequest(new { code = "forms.instance_id_malformed" });
                }
                instanceId = parsed;
            }

            var token = await MintTokenAsync(
                issuer, verifier, activeTeam, ActingSubject(http), roles, FormCapabilityAction.Read, timeProvider, ct)
                .ConfigureAwait(false);

            try
            {
                var view = await engine
                    .RenderAsync(new Harborline.Api.Foundation.Forms.Models.FormDefinitionId(formId), instanceId, token, ct)
                    .ConfigureAwait(false);
                return Results.Ok(FormViewDto.From(view));
            }
            catch (FormDefinitionNotFoundException)
            {
                return Results.NotFound(new { code = "form_definition.not_published", detail = new { formId } });
            }
            catch (FormInstanceNotFoundException)
            {
                // INV-S1: not-found / cross-tenant / soft-deleted are indistinguishable.
                return Results.NotFound(new { code = "forms.instance_not_found" });
            }
            catch (CapabilityDeniedException ex)
            {
                return Results.Json(new { code = "forms.capability_denied", detail = new { reason = ex.Reason } }, statusCode: StatusCodes.Status403Forbidden);
            }
        });

        // POST /api/local-node/forms/{formId}/submit — validate + save a candidate.
        app.MapPost($"{RouteBase}/{{formId}}/submit", async (
            string formId,
            JsonElement body,
            HttpRequest request,
            CancellationToken ct) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
            {
                return Results.BadRequest(new { code = "forms.body_must_be_json_object" });
            }

            // F-ROUTE idempotency (ADR 0101 Rev 3.1 Wave 2b) is owned here by the durable forms
            // mechanism. NodeMutationIdempotency deliberately skips this route so the two regimes do not stack.
            // The client sends an OPTIONAL `Idempotency-Key`
            // HTTP header (the industry convention; a body field would pollute the strict candidate schema).
            // Same key ⇒ the engine derives the SAME instance id, so a retry returns the SAME instance and
            // never double-captures. Absent ⇒ a fresh instance, exactly as before.
            if (!TryReadIdempotencyKey(request, out var idempotencyKey, out var keyError))
            {
                return keyError!;
            }

            // #144 runtime form-fill: an OPTIONAL `Into-Case-Ref` header names the RECORD this submission is
            // filled into. It rides ONLY the post-submit projection context — a VisitCase-sourced condition
            // projection resolves its target from it, and the generic submission-record projection links the
            // submission to it. It is an UNTRUSTED projection hint: validated for SHAPE only here (bounded,
            // non-empty — the same discipline as the idempotency key), NEVER authorized on. The registry
            // entity id is an opaque token (a GUID, not a canonical `EntityId`), so the route does not parse
            // it as one; the projector's tenant-existence guard is the security boundary — an absent /
            // malformed / cross-tenant ref simply produces no side record (a silent, fail-closed no-op).
            if (!TryReadCaseRef(request, out var caseRef, out var caseError))
            {
                return caseError!;
            }

            using var candidate = JsonDocument.Parse(body.GetRawText());

            var token = await MintTokenAsync(
                issuer, verifier, activeTeam, ActingSubject(request.HttpContext), roles, FormCapabilityAction.Write,
                timeProvider, ct)
                .ConfigureAwait(false);

            var definition = new Harborline.Api.Foundation.Forms.Models.FormDefinitionId(formId);

            // The pre-save validate and the pack gate live INSIDE the same try as the save: the engine's
            // rule evaluation runs in ValidateAsync too, so a rule-engine timeout raised here must reach
            // the structured 503 below rather than escaping as a bodyless 500.
            try
            {
                var validation = await engine.ValidateAsync(definition, candidate, token, ct).ConfigureAwait(false);
                if (!validation.IsValid)
                {
                    return Results.UnprocessableEntity(ValidationResultDto.From(validation));
                }

                if (submissionGate?.RequiredPermission(definition) is { } permission)
                {
                    var gateAuthority = RequestAuthorization.Authority(request.HttpContext, token.Tenant, timeProvider);
                    var denied = await RequestAuthorization.RefusalAsync(
                        request.HttpContext, gateAuthority, permission, RouteRecord.TheInstall, ct).ConfigureAwait(false);
                    if (denied is not null) return denied;

                    var capabilityRoles = submissionGate.CapabilityRoles(definition);
                    if (capabilityRoles.Count > 0)
                    {
                        token = await MintTokenAsync(
                            issuer, verifier, activeTeam, ActingSubject(request.HttpContext),
                            roles.Concat(capabilityRoles).Distinct(StringComparer.Ordinal).ToArray(),
                            FormCapabilityAction.Write, timeProvider, ct).ConfigureAwait(false);
                    }
                }

                var at = timeProvider.GetUtcNow();
                var authority = new AuthorizationWriteContext(
                    token.Subject,
                    token.Tenant,
                    at);
                var receipt = await engine
                    .SaveWithReceiptAsync(definition, candidate, token, authority, ct, idempotencyKey, caseRef)
                    .ConfigureAwait(false);

                var location =
                    $"{RouteBase}/{Uri.EscapeDataString(formId)}?instance={Uri.EscapeDataString(receipt.InstanceId.ToString())}";

                // F3 (Wave 3a): the submission committed and its projections ran, but one or more
                // binding-declared captures could NOT land (an out-of-range grade, an unresolvable /
                // cross-tenant target). The server already audited each skip (F-SKIP, unchanged); we ALSO
                // return them so the runner tells the user honestly instead of silent success. The wire
                // carries the machine reason + the field (never a submitted value); the client localizes.
                var skips = receipt.Skips;
                if (skips.Count > 0)
                {
                    return Results.Created(location, new FormSubmitResponse(
                        receipt.InstanceId.ToString(),
                        Projection: FormSubmitResponse.ProjectionSkipped,
                        Skips: skips.Select(s => new FormSubmitSkipDto(s.Reason, s.FieldPointer)).ToList()));
                }

                // Committed AND the post-submit projection completed (or the form had no projection). The
                // 201 carries no `projection`/`skips` field (omitted-when-null) so the wire is byte-identical
                // to the pre-Wave-2b success response; the 202-pending path below is the only other shape.
                return Results.Created(location, new FormSubmitResponse(receipt.InstanceId.ToString()));
            }
            catch (FormSubmitProjectionPendingException ex)
            {
                // F-ROUTE: the submission COMMITTED but its post-submit projection did not complete. This is
                // NOT a failure of the submit — the durable at-least-once outbox holds a row the reconcile
                // sweep (F-RECON) heals. Return the committed receipt as success-with-pending (202) rather
                // than a 500 that, on retry, would create a duplicate instance + double-capture.
                return Results.Accepted(
                    $"{RouteBase}/{Uri.EscapeDataString(formId)}?instance={Uri.EscapeDataString(ex.Receipt.InstanceId.ToString())}",
                    new FormSubmitResponse(ex.Receipt.InstanceId.ToString(), Projection: FormSubmitResponse.ProjectionPending));
            }
            catch (FormDefinitionNotFoundException)
            {
                return Results.NotFound(new { code = "form_definition.not_published", detail = new { formId } });
            }
            catch (FormValidationException ex)
            {
                return Results.UnprocessableEntity(ValidationResultDto.From(ex.Result));
            }
            catch (RuleEngineTimeoutException)
            {
                // Ticket 150 / D1: the wall-clock liveness guard is a non-authoritative
                // INFRASTRUCTURE fault, never an evaluation outcome — so it is NOT a 422
                // validation result, and no exception middleware exists to shape the bodyless
                // 500 it would otherwise become. Structured, retryable 503 in the route
                // family's error shape; the write was refused without a synthesized verdict.
                return Results.Json(
                    new
                    {
                        code = RuleEngineCodes.Timeout,
                        detail = new { retryable = true },
                    },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (CapabilityDeniedException ex)
            {
                return Results.Json(new { code = "forms.capability_denied", detail = new { reason = ex.Reason } }, statusCode: StatusCodes.Status403Forbidden);
            }
        });
    }

    /// <summary>The header a client sends to make a submit idempotent (F-ROUTE).</summary>
    public const string IdempotencyKeyHeader = IdempotencyContract.HeaderName;

    /// <summary>Upper bound on the idempotency-key length — a bounded token, not free-form payload.</summary>
    private const int MaxIdempotencyKeyLength = IdempotencyContract.MaxKeyLength;

    /// <summary>
    /// Reads the optional <c>Idempotency-Key</c> header. Absent ⇒ null (fresh-instance behaviour).
    /// A present-but-whitespace or over-long key is rejected 400 (a bounded token, never unbounded input).
    /// </summary>
    private static bool TryReadIdempotencyKey(HttpRequest request, out string? idempotencyKey, out IResult? error)
    {
        idempotencyKey = null;
        error = null;

        if (!request.Headers.TryGetValue(IdempotencyKeyHeader, out var values))
        {
            return true; // header absent — fresh instance
        }

        if (values.Count != 1 || values.Any(value => value?.Contains(',') == true))
        {
            error = Results.BadRequest(new { code = "forms.idempotency_key_repeated", detail = new { header = IdempotencyKeyHeader } });
            return false;
        }

        var raw = values.ToString().Trim();
        if (raw.Length == 0)
        {
            error = Results.BadRequest(new { code = "forms.idempotency_key_blank", detail = new { header = IdempotencyKeyHeader } });
            return false;
        }
        if (raw.Length > MaxIdempotencyKeyLength)
        {
            error = Results.BadRequest(new { code = "forms.idempotency_key_too_long", detail = new { header = IdempotencyKeyHeader, maxLength = MaxIdempotencyKeyLength } });
            return false;
        }

        idempotencyKey = raw;
        return true;
    }

    /// <summary>The header a client sends to link a submission to the record it is filled into (#144).</summary>
    public const string IntoCaseRefHeader = "Into-Case-Ref";

    /// <summary>Upper bound on the case-ref length — a bounded opaque entity token, not free-form payload.</summary>
    private const int MaxCaseRefLength = 200;

    /// <summary>
    /// Reads the optional <c>Into-Case-Ref</c> header (#144). Absent / whitespace ⇒ null (a standalone
    /// fill with no record context — legitimate). A present-but-over-long value is rejected 400 (a bounded
    /// token, never unbounded input). No canonical-id parse: the registry entity id is opaque, and the
    /// projector's tenant-existence guard — not this route — is the authorization boundary.
    /// </summary>
    private static bool TryReadCaseRef(HttpRequest request, out string? caseRef, out IResult? error)
    {
        caseRef = null;
        error = null;

        if (!request.Headers.TryGetValue(IntoCaseRefHeader, out var values))
        {
            return true; // header absent — a standalone fill
        }

        var raw = values.ToString().Trim();
        if (raw.Length == 0)
        {
            return true; // present but empty — treated as absent
        }
        if (raw.Length > MaxCaseRefLength)
        {
            error = Results.BadRequest(new { code = "forms.case_ref_too_long", detail = new { header = IntoCaseRefHeader, maxLength = MaxCaseRefLength } });
            return false;
        }

        caseRef = raw;
        return true;
    }

    /// <summary>
    /// The acting member for this request, read from the same
    /// <c>HttpContext.Features</c> seam nine shipped route files already use
    /// (<see cref="NodeCallerParty.Resolve(HttpContext)"/>): the live
    /// selected-session principal's canonical Party on the web plane, the operator
    /// party on the desktop plane, and a refusal when a web-plane request carries no
    /// principal at all.
    /// </summary>
    /// <remarks>
    /// This is resolved PER REQUEST, which is the whole point of #3378. The subject
    /// used to be a constant captured once in <c>HostedFormsApiEndpoint.StartAsync</c>,
    /// so every member's submission was recorded against the same identity — an
    /// attribution failure, not an authorization one. No PEP is involved and none is
    /// required: attribution flows through the request feature, while the facade's
    /// permission <c>Bind()</c> is a separate, later concern (MTW-3).
    /// </remarks>
    private static ActorId ActingSubject(HttpContext http) =>
        new(NodeCallerParty.Resolve(http).Value);

    /// <summary>
    /// Mints a verified <see cref="CapabilityToken"/> for the active-team tenant +
    /// the acting member, granting <paramref name="action"/>. Goes through the real
    /// issuer→verifier round-trip so the route never fabricates a capability.
    /// </summary>
    private static async Task<CapabilityToken> MintTokenAsync(
        IFormCapabilityIssuer issuer,
        IFormCapabilityVerifier verifier,
        IActiveTeamAccessor activeTeam,
        ActorId subject,
        IReadOnlyList<string> roles,
        FormCapabilityAction action,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var tenant = NodeTenant.Resolve(activeTeam);
        var now = timeProvider.GetUtcNow();
        var bearer = await issuer
            .IssueAsync(tenant, subject, roles, new[] { action }, now + TokenLifetime, ct)
            .ConfigureAwait(false);
        return await verifier.VerifyAsync(bearer, now, ct).ConfigureAwait(false);
    }
}

/// <summary>Declares the route-level authority a pack-bound post-submit projection requires.</summary>
public interface IFormSubmissionGate
{
    string? RequiredPermission(Harborline.Api.Foundation.Forms.Models.FormDefinitionId form);

    IReadOnlyList<string> CapabilityRoles(Harborline.Api.Foundation.Forms.Models.FormDefinitionId form);
}

// ── Wire shapes (mirror @harborline-software/api-contracts forms.ts) ───────────────────────────

/// <summary>The rendered form view returned by <c>GET /api/local-node/forms/{formId}</c>.</summary>
public sealed record FormViewDto(
    [property: JsonPropertyName("formId")] string FormId,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("title")] InternationalizedTextDto? Title,
    [property: JsonPropertyName("description")] InternationalizedTextDto? Description,
    [property: JsonPropertyName("sections")] IReadOnlyList<FormViewSectionDto> Sections)
{
    /// <summary>Projects an engine <see cref="FormView"/> onto the wire DTO.</summary>
    public static FormViewDto From(FormView v) => new(
        v.FormId.Value,
        v.Version.ToString(),
        InternationalizedTextDto.From(v.Title),
        InternationalizedTextDto.From(v.Description),
        v.Sections.Select(FormViewSectionDto.From).ToList());
}

/// <summary>A section within a <see cref="FormViewDto"/>.</summary>
/// <remarks>
/// <para><c>layout</c> / <c>fieldPlacement</c> carry the section's presentation
/// layout (ADR 0055 Rev 6 — input-group flex/grid layout) onto the wire so the
/// React <c>SchemaForm</c> renderer can honour it. They are presentation-only
/// (no auth / validation effect) and are omitted from the JSON when null, so a
/// pre-Rev-6 section serialises byte-identically to before.</para>
/// </remarks>
public sealed record FormViewSectionDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] InternationalizedTextDto Title,
    [property: JsonPropertyName("fields")] IReadOnlyList<FormViewFieldDto> Fields,
    [property: JsonPropertyName("layout"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SectionLayoutDto? Layout,
    [property: JsonPropertyName("fieldPlacement"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, FieldPlacementDto>? FieldPlacement,
    // ADR 0055 Rev 7: the section's rendered nested item tree. Omitted-when-null ⇒ a flat (pre-Rev-7)
    // section serialises byte-identically; present ⇒ the SchemaForm runner walks the tree (nested
    // groups / repeatable collections / content / action blocks) instead of the flat `fields` list.
    [property: JsonPropertyName("items"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FormViewItemDto>? Items = null)
{
    /// <summary>Projects an engine <see cref="FormViewSection"/> onto the wire DTO.</summary>
    public static FormViewSectionDto From(FormViewSection s) => new(
        s.Id,
        InternationalizedTextDto.From(s.Title)!,
        s.Fields.Select(FormViewFieldDto.From).ToList(),
        SectionLayoutDto.From(s.Layout),
        s.FieldPlacement?.ToDictionary(kv => kv.Key, kv => FieldPlacementDto.From(kv.Value)),
        s.Items is { Count: > 0 } ? s.Items.Select(FormViewItemDto.From).ToList() : null);
}

/// <summary>
/// One node of a rendered <see cref="FormViewSection"/>'s item tree on the wire (ADR 0055 Rev 7 —
/// nested sub-form items). MIRRORS the TS <c>FormViewItem</c> discriminated union in
/// <c>@harborline-software/api-contracts/forms</c>: <c>kind</c> is the lowercase tag (<c>field</c> / <c>group</c> /
/// <c>collection</c> / <c>content</c> / <c>action</c>) and the payload members are populated per kind —
/// a <c>field</c> node carries a rendered <see cref="FormViewFieldDto"/>; a container carries child
/// <c>items</c>; a collection additionally carries <c>cardinality</c> + the F-24 <c>table</c>; content /
/// action leaves carry their declarative config. Reference nodes are expanded to groups by the reuse
/// resolver before rendering, so none reach the wire. Every payload is omitted-when-null so a node
/// serialises to exactly the members its kind uses (the shape the TS union expects). The sub-DTOs
/// (<see cref="CardinalityDto"/> / <see cref="ContentNodeDto"/> / <see cref="FormActionDto"/> /
/// <see cref="CollectionTableConfigDto"/> / <see cref="SectionLayoutDto"/> / <see cref="FieldPlacementDto"/>)
/// are the SAME records the authoring form-definition route already carries, so the two wire surfaces stay
/// structurally identical.
/// </summary>
public sealed record FormViewItemDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("field"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FormViewFieldDto? Field = null,
    [property: JsonPropertyName("items"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FormViewItemDto>? Items = null,
    [property: JsonPropertyName("cardinality"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CardinalityDto? Cardinality = null,
    [property: JsonPropertyName("title"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InternationalizedTextDto? Title = null,
    [property: JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ContentNodeDto>? Content = null,
    [property: JsonPropertyName("action"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FormActionDto? Action = null,
    [property: JsonPropertyName("layout"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SectionLayoutDto? Layout = null,
    [property: JsonPropertyName("placement"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, FieldPlacementDto>? Placement = null,
    [property: JsonPropertyName("table"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CollectionTableConfigDto? Table = null)
{
    /// <summary>Projects a rendered engine <see cref="FormViewItem"/> onto the wire DTO (recursive).</summary>
    public static FormViewItemDto From(FormViewItem item) => new(
        FormDefinitionDtoExtensions.KindToWire(item.Kind),
        item.Key,
        item.Field is null ? null : FormViewFieldDto.From(item.Field),
        item.Items is { Count: > 0 } ? item.Items.Select(From).ToList() : null,
        CardinalityDto.From(item.Cardinality),
        InternationalizedTextDto.From(item.Title),
        item.Content is { Count: > 0 } ? item.Content.Select(ContentNodeDto.From).ToList() : null,
        FormActionDto.From(item.Action),
        SectionLayoutDto.From(item.Layout),
        item.Placement?.ToDictionary(kv => kv.Key, kv => FieldPlacementDto.From(kv.Value)),
        CollectionTableConfigDto.From(item.Table));
}

/// <summary>
/// A section's 2D layout on the wire (ADR 0055 Rev 6). MIRRORS the
/// <c>SectionLayout</c> interface in <c>@harborline-software/api-contracts/forms</c>. The enum
/// members serialise to the lowercase string unions the TS contract declares
/// (<c>kind</c>: <c>"stack"|"flex"|"grid"</c>, <c>direction</c>: <c>"row"|"column"</c>,
/// <c>wrap</c>: <c>"nowrap"|"wrap"</c>); the numeric fields pass through.
/// </summary>
public sealed record SectionLayoutDto(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("wrap")] string Wrap,
    [property: JsonPropertyName("columns")] int Columns,
    [property: JsonPropertyName("gap")] int Gap,
    // F-23 responsive intents. All nullable + omitted-when-null so a pre-F-23 layout
    // serialises byte-identically to before (back-compat non-negotiable).
    [property: JsonPropertyName("collapseBelow"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CollapseBelow = null,
    [property: JsonPropertyName("density"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Density = null,
    [property: JsonPropertyName("align"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Align = null)
{
    /// <summary>Projects an engine <see cref="SectionLayout"/> onto the wire DTO (null-safe).</summary>
    public static SectionLayoutDto? From(SectionLayout? layout) => layout is null
        ? null
        : new(
            Kind: layout.Kind switch
            {
                SectionLayoutKind.Flex => "flex",
                SectionLayoutKind.Grid => "grid",
                _ => "stack",
            },
            Direction: layout.Direction == FlexDirection.Column ? "column" : "row",
            Wrap: layout.Wrap == FlexWrap.NoWrap ? "nowrap" : "wrap",
            Columns: layout.Columns,
            Gap: layout.Gap,
            CollapseBelow: layout.CollapseBelow,
            Density: layout.Density,
            Align: layout.Align);
}

/// <summary>Per-field placement on the wire (ADR 0055 Rev 6). MIRRORS the
/// <c>FieldPlacement</c> interface in <c>@harborline-software/api-contracts/forms</c>.</summary>
public sealed record FieldPlacementDto(
    [property: JsonPropertyName("colSpan")] int ColSpan,
    [property: JsonPropertyName("grow")] int Grow,
    // F-23 intents — nullable + omitted-when-null (byte-identical pre-F-23 wire).
    [property: JsonPropertyName("width"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Width = null,
    [property: JsonPropertyName("align"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Align = null)
{
    /// <summary>Projects an engine <see cref="FieldPlacement"/> onto the wire DTO.</summary>
    public static FieldPlacementDto From(FieldPlacement p) => new(p.ColSpan, p.Grow, p.Width, p.Align);
}

/// <summary>A field within a <see cref="FormViewSectionDto"/>.</summary>
public sealed record FormViewFieldDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("label")] InternationalizedTextDto Label,
    [property: JsonPropertyName("helpText")] InternationalizedTextDto? HelpText,
    [property: JsonPropertyName("controlHint")] string? ControlHint,
    [property: JsonPropertyName("isSensitive")] bool IsSensitive,
    [property: JsonPropertyName("isReadable")] bool IsReadable,
    [property: JsonPropertyName("value")] JsonElement? Value,
    [property: JsonPropertyName("rules")] FormViewFieldRulesDto? Rules = null,
    [property: JsonPropertyName("options")] IReadOnlyList<string>? Options = null,
    [property: JsonPropertyName("required")] bool Required = false)
{
    /// <summary>Projects an engine <see cref="FormViewField"/> onto the wire DTO.</summary>
    public static FormViewFieldDto From(FormViewField f) => new(
        f.Name,
        InternationalizedTextDto.From(f.Label)!,
        InternationalizedTextDto.From(f.HelpText),
        f.ControlHint,
        f.IsSensitive,
        f.IsReadable,
        f.Value,
        FormViewFieldRulesDto.From(f.Rules), f.Options, f.Required);
}

/// <summary>
/// The SPINE-1 rule outcomes for a field (F-12), projected server-side so the runtime renderer
/// applies the SAME visibility / required / read-only / compute / presentation the builder preview
/// shows. Omitted from the JSON entirely when no rule targets the field.
/// </summary>
public sealed record FormViewFieldRulesDto(
    [property: JsonPropertyName("visible")] bool Visible,
    [property: JsonPropertyName("required")] bool Required,
    [property: JsonPropertyName("readOnly")] bool ReadOnly,
    [property: JsonPropertyName("computed")] JsonElement? Computed,
    [property: JsonPropertyName("presentationSeverity")] string? PresentationSeverity,
    [property: JsonPropertyName("presentationBadge")] InternationalizedTextDto? PresentationBadge,
    [property: JsonPropertyName("presentationStyleToken")] string? PresentationStyleToken)
{
    /// <summary>Projects an engine <see cref="FormViewFieldRules"/> onto the wire DTO (null-safe).</summary>
    public static FormViewFieldRulesDto? From(FormViewFieldRules? r) => r is null ? null : new(
        r.Visible,
        r.Required,
        r.ReadOnly,
        r.Computed,
        r.PresentationSeverity,
        InternationalizedTextDto.From(r.PresentationBadge),
        r.PresentationStyleToken);
}

/// <summary>Locale-keyed text mirroring the .NET <c>InternationalizedText</c>.</summary>
public sealed record InternationalizedTextDto(
    [property: JsonPropertyName("defaultLocale")] string DefaultLocale,
    [property: JsonPropertyName("values")] IReadOnlyDictionary<string, string> Values)
{
    /// <summary>Projects an <see cref="Harborline.Api.Foundation.Forms.Models.InternationalizedText"/> onto the wire DTO (null-safe).</summary>
    public static InternationalizedTextDto? From(Harborline.Api.Foundation.Forms.Models.InternationalizedText? text)
        => text is null ? null : new(text.DefaultLocale, text.Values);
}

/// <summary>
/// Response after a form submission. A 201 means the submission committed and its post-submit projection
/// completed (or the form has no projection) — the <c>projection</c>/<c>skips</c> fields are omitted, so
/// the wire is byte-identical to the pre-Wave-2b success response. A 202 means the submission committed
/// but its projection is deferred to the reconcile sweep (<c>projection: "pending"</c> — F-ROUTE); the
/// durable outbox row will heal, and a retry with the same idempotency key resolves to this SAME instance.
/// A 201 with <c>projection: "skipped"</c> + a <c>skips</c> list means the submission committed but one or
/// more binding-declared captures could NOT land (F3, Wave 3a) — each is audited server-side; the list
/// lets the runner tell the user honestly which field did not record and why.
/// </summary>
public sealed record FormSubmitResponse(
    [property: JsonPropertyName("instanceId")] string InstanceId,
    [property: JsonPropertyName("projection"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Projection = null,
    [property: JsonPropertyName("skips"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<FormSubmitSkipDto>? Skips = null)
{
    /// <summary>The submission committed; its projection is deferred to the reconcile sweep (202, F-ROUTE).</summary>
    public const string ProjectionPending = "pending";

    /// <summary>The submission committed but ≥1 binding-declared capture was skipped (201, F3).</summary>
    public const string ProjectionSkipped = "skipped";
}

/// <summary>
/// One binding-declared capture the post-submit projection could NOT land (F3, Wave 3a). Carries only the
/// stable machine <c>reason</c> code + the schema <c>field</c> pointer (never a submitted value); the
/// Harborline App runner localizes the reason. MIRRORS the <c>FormSubmitSkip</c> shape the Harborline App consumes.
/// </summary>
public sealed record FormSubmitSkipDto(
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("field")] string Field);

/// <summary>422 body mirroring the engine <see cref="ValidationResult"/>.</summary>
public sealed record ValidationResultDto(
    [property: JsonPropertyName("isValid")] bool IsValid,
    [property: JsonPropertyName("errors")] IReadOnlyList<ValidationErrorDto> Errors)
{
    /// <summary>Projects an engine <see cref="ValidationResult"/> onto the wire DTO.</summary>
    public static ValidationResultDto From(ValidationResult r) => new(
        r.IsValid,
        r.Errors.Select(ValidationErrorDto.From).ToList());
}

/// <summary>
/// A single validation failure, located by JSON Pointer. Carries the stable
/// <c>code</c> + structured <c>params</c> (ADR 0055 localizable-validation) so the
/// SchemaForm renderer can resolve a LOCALIZED message; <c>message</c> is the English
/// fallback. <c>code</c>/<c>params</c> are omitted from the JSON when null (a legacy
/// error without them).
/// </summary>
public sealed record ValidationErrorDto(
    [property: JsonPropertyName("jsonPointer")] string JsonPointer,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Code,
    [property: JsonPropertyName("params"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, string>? Params)
{
    /// <summary>Projects an engine <see cref="ValidationError"/> onto the wire DTO.</summary>
    public static ValidationErrorDto From(ValidationError e) => new(
        e.JsonPointer, e.Message, e.Kind.ToString(), e.Code, e.Params);
}
