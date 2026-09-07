using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// The immutable context a <see cref="IFormSubmitProjection"/> receives once a form submission has
/// been validated, persisted, and audited by the forms engine (ADR 0101 Rev 3.1 Wave 2 — the
/// submission → side-record projection seam / A3).
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>only</b> input a post-submit projection sees. It is deliberately small: the
/// definition that was submitted, the persisted instance, the tenant + actor the engine already
/// resolved and authorized, the submit clock, and the submitted (cleartext) values. A projection
/// never re-opens the capability token or re-authorizes — the engine already did, and the projection
/// runs INSIDE the same authorized submit. The context is not a plugin sandbox; it is a typed hook
/// payload.
/// </para>
/// <para>
/// <b>Determinism.</b> <see cref="SubmittedAt"/> is the submit instant (the "visit clock"); a
/// projection stamps any side record it writes at this instant rather than reading an ambient clock,
/// so a replay of the same submission produces the same records.
/// </para>
/// </remarks>
/// <param name="Form">The form definition that was submitted.</param>
/// <param name="InstanceId">The persisted form-instance entity the engine created.</param>
/// <param name="Tenant">The tenant the engine resolved + authorized the submit under.</param>
/// <param name="Actor">The acting principal the submit was authorized as.</param>
/// <param name="SubmittedAt">The submit instant — the deterministic clock a projection records at.</param>
/// <param name="SubmittedValues">
/// The submitted values as a JSON object keyed by field name (cleartext, as the user submitted them).
/// A projection reads only the specific fields its binding declares; it never persists this document.
/// </param>
/// <param name="CaseRef">
/// The optional visit / case reference the submission is pinned to (used by a projection whose
/// entity-ref source is the visit case). Null when the submit path does not carry a case.
/// </param>
public sealed record FormSubmitContext(
    FormDefinitionId Form,
    EntityId InstanceId,
    TenantId Tenant,
    ActorId Actor,
    DateTimeOffset SubmittedAt,
    JsonDocument SubmittedValues,
    string? CaseRef = null);
