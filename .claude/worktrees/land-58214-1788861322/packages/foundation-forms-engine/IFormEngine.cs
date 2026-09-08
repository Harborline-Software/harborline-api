using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Engine.Exceptions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>
/// Dynamic-forms rendering engine (ADR 0055 §3.1). Renders a form definition
/// into a localized <see cref="FormView"/>, validates a candidate document
/// against the form's registered JSON Schema (with resource bounds), and
/// persists a validated candidate as a new form-instance entity.
/// </summary>
/// <remarks>
/// <para>
/// The engine composes four substrates: the kernel schema registry
/// (<see cref="Harborline.Api.Kernel.Schema.ISchemaRegistry"/>) for candidate
/// validation, the asset entity store
/// (<see cref="IEntityStore"/>) for instance read / create, the
/// foundation-recovery field-encryption seam
/// (<see cref="Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor"/>) for
/// PII-at-rest, and the asset audit log
/// (<see cref="Harborline.Api.Foundation.Assets.Audit.IAuditLog"/>) for write
/// provenance.
/// </para>
/// <para>
/// <b>Tenant anchor (INV-S1).</b> Every method takes a verified
/// <see cref="CapabilityToken"/>; the token's macaroon-bound
/// <see cref="CapabilityToken.Tenant"/> is the authoritative active tenant
/// for the operation. The engine never accepts an ambient / all-tenant
/// selector — reads of an existing instance assert
/// <c>entity.Tenant == token.Tenant</c> and fail closed (no body) on
/// mismatch.
/// </para>
/// </remarks>
public interface IFormEngine
{
    /// <summary>
    /// Renders <paramref name="form"/> into a localized <see cref="FormView"/>.
    /// When <paramref name="instance"/> is supplied, the view is populated from
    /// that instance's body — subject to INV-S1 (the instance must belong to
    /// the token's tenant) and field-level redaction (PII and role-gated
    /// fields are returned without their values). Requires
    /// <see cref="FormCapabilityAction.Read"/>.
    /// </summary>
    Task<FormView> RenderAsync(FormDefinitionId form, EntityId? instance, CapabilityToken token, CancellationToken ct);

    /// <summary>
    /// Validates <paramref name="candidate"/> against the JSON Schema referenced
    /// by <paramref name="form"/>, after enforcing the INV-S2 resource bounds on
    /// both the schema and the candidate. Returns a structured
    /// <see cref="ValidationResult"/> — it does not throw on an invalid
    /// candidate. Requires <see cref="FormCapabilityAction.Write"/> (this is the
    /// save-candidate validation path).
    /// </summary>
    Task<ValidationResult> ValidateAsync(FormDefinitionId form, JsonDocument candidate, CapabilityToken token, CancellationToken ct);

    /// <summary>
    /// Validates <paramref name="candidate"/> (as <see cref="ValidateAsync"/>),
    /// enforces section-level write authorization, encrypts every PII-classified
    /// field at rest (INV-S3), persists the result as a new form-instance entity
    /// scoped to the token's tenant (OQ-3), and emits a save audit record
    /// (INV-S4). Returns the new instance's <see cref="EntityId"/>. Requires
    /// <see cref="FormCapabilityAction.Write"/>.
    /// </summary>
    /// <exception cref="FormValidationException">The candidate failed schema or resource-bound validation.</exception>
    /// <exception cref="CapabilityDeniedException">The token lacks write capability for one or more candidate fields.</exception>
    Task<EntityId> SaveAsync(FormDefinitionId form, JsonDocument candidate, CapabilityToken token, CancellationToken ct);

    /// <summary>
    /// Saves exactly as <see cref="SaveAsync"/>, additionally returning a <see cref="FormSubmitReceipt"/>
    /// that carries the engine's own submit instant (ADR 0101 Rev 3.1 Wave 2b / F-CLOCK). A post-submit
    /// projection decorator uses the receipt's instant rather than a fresh clock reading, so the
    /// submission and its projected side record share ONE timestamp and replay deterministically.
    /// <see cref="SaveAsync"/> is the same operation returning only the instance id, so existing callers
    /// are unaffected.
    /// </summary>
    /// <param name="form">The form definition to submit against.</param>
    /// <param name="candidate">The candidate field values to validate + persist.</param>
    /// <param name="token">The verified capability the submit is authorized under (tenant + actor + action).</param>
    /// <param name="authority">The server-built live-authority context decided before token and field checks.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="idempotencyKey">
    /// Optional client-supplied idempotency key (ADR 0101 Rev 3.1 Wave 2b / F-ROUTE). When supplied, the
    /// new instance's id is DERIVED deterministically from <c>(tenant, form, key)</c> rather than freshly
    /// minted, so a client retry with the SAME key resolves to the SAME instance — a committed-then-retried
    /// submission never creates a duplicate instance (and, because a condition projection derives its side
    /// record id from the instance id, never double-captures). When null (the default) the id is a fresh
    /// GUID, exactly the prior behaviour — existing callers are byte-for-byte unaffected.
    /// </param>
    /// <param name="caseRef">
    /// Optional "which record this submission is filled into" hint (#144 runtime form-fill). It rides ONLY
    /// the post-submit projection context (<see cref="Submission.FormSubmitContext.CaseRef"/>) — a projection
    /// whose entity-ref source is the visit case resolves the target from it, and the generic
    /// form-submission-record projection links the submission to it. It does NOT change what is persisted or
    /// how the submission is validated / authorized: the inner engine ignores it entirely, so a submit that
    /// carries no case ref is byte-for-byte identical to before. It is an UNTRUSTED projection hint — the
    /// projector fail-closes on a non-existent / cross-tenant target, so the route never authorizes on it.
    /// </param>
    Task<FormSubmitReceipt> SaveWithReceiptAsync(
        FormDefinitionId form,
        JsonDocument candidate,
        CapabilityToken token,
        AuthorizationWriteContext authority,
        CancellationToken ct = default,
        string? idempotencyKey = null,
        string? caseRef = null);
}
