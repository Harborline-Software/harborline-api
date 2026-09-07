using System.Globalization;
using System.Text.Json;

using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Forms.Submission;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Projection;

/// <summary>
/// The governed <b>submission → condition-assessment projector</b> (ADR 0101 Rev 3.1 Wave 2 / A3,
/// annex D-M): when a form submission carries a condition-rating field declared by a
/// <see cref="ConditionRatingFieldBinding"/>, this projection writes the entity's typed
/// <see cref="ConditionAssessment"/> — "one act, two artifacts" (the submission + the side record).
/// </summary>
/// <remarks>
/// <para>
/// It is the concrete <see cref="IFormSubmitProjection"/> for the one shipped field kind. It runs
/// server-side on the node inside the authorized submit, writes through the audited, tenant-scoped
/// <see cref="IConditionAssessmentStore"/> (so the assessment rides the durable X-AUDIT chain), and
/// records at the submit clock (deterministic — never an ambient now).
/// </para>
/// <para>
/// <b>Fail-closed guards (refute targets):</b>
/// <list type="number">
///   <item>the sentinel / default tenant is refused up front — it can own no registry data;</item>
///   <item>only bindings the tenant declared on THIS form are considered (attacker cannot inject a
///     binding via the submission — bindings are tenant config, not payload);</item>
///   <item>the resolved target entity MUST exist under the SAME tenant (via
///     <see cref="IRegistryEntityRepository"/>) — a bogus / cross-tenant / dangling ref is skipped,
///     never written;</item>
///   <item>an out-of-range grade for the binding's scale is skipped, not thrown.</item>
/// </list>
/// Every one of those is an expected no-op (the contract), so normal operation never throws; a throw
/// is reserved for an unexpected store fault.
/// </para>
/// <para>
/// <b>F-SKIP (Wave 2b) — a binding-declared skip leaves a diagnosable trace.</b> A genuine no-op (no
/// binding for this form; the rating field simply not filled) is silent, as it should be. But when a
/// binding the tenant DECLARED could not land — an out-of-range grade, or a declared target that
/// resolved cross-tenant / dangling / unresolvable — the projector writes a
/// <see cref="RegistryOp.ProjectionSkipped"/> record on the durable audit chain (keyed on the form
/// instance) so an operator can see that an INTENDED capture vanished and why. The record carries the
/// reason + the field pointer + (when known) the target — never a submitted value.
/// </para>
/// <para>
/// <b>F-ATOM (Wave 2b) — idempotent replay.</b> The assessment id is DERIVED deterministically from
/// (instance, field pointer, entity) rather than freshly minted, so the at-least-once outbox
/// (<c>IFormSubmitOutbox</c>) can safely re-run a projection whose first attempt was interrupted after
/// the submission committed: a replay UPSERTs the same record instead of duplicating it.
/// </para>
/// </remarks>
public sealed class ConditionAssessmentProjector : IFormSubmitProjection
{
    private readonly IConditionRatingFieldBindingStore _bindings;
    private readonly IConditionAssessmentStore _assessments;
    private readonly IRegistryEntityRepository _entities;
    private readonly IRegistryAuditLog _audit;

    /// <summary>Composes the projector over the binding config + the audited condition + entity stores.</summary>
    public ConditionAssessmentProjector(
        IConditionRatingFieldBindingStore bindings,
        IConditionAssessmentStore assessments,
        IRegistryEntityRepository entities,
        IRegistryAuditLog audit)
    {
        _bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _assessments = assessments ?? throw new ArgumentNullException(nameof(assessments));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FormSubmitProjectionSkip>> ProjectAsync(
        FormSubmitContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Guard 1: the sentinel / default tenant owns no registry data — refuse fail-closed.
        if (context.Tenant.IsSystemSentinel)
        {
            return Array.Empty<FormSubmitProjectionSkip>();
        }

        // Guard 2: only the tenant's OWN declared bindings on this form activate the projection.
        var bindings = await _bindings.GetForFormAsync(context.Tenant, context.Form, cancellationToken)
            .ConfigureAwait(false);
        if (bindings.Count == 0)
        {
            return Array.Empty<FormSubmitProjectionSkip>(); // no-op: this form carries no condition-rating field
        }

        var observedAt = new Instant(context.SubmittedAt);
        var instanceRef = context.InstanceId.ToString();

        // F3 (Wave 3a): a binding-declared capture that could not land is audited (F-SKIP, unchanged) AND
        // reported so the route can tell the user honestly instead of silent success.
        List<FormSubmitProjectionSkip>? skips = null;

        foreach (var binding in bindings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Read the rating grade from the submission; absent / non-numeric → GENUINE no-op (the field
            // was not filled). Per F-SKIP this is silent — never an audited skip.
            if (!TryReadInt(context.SubmittedValues, binding.FieldPointer, out var grade))
            {
                continue;
            }

            // From here the tenant DECLARED a binding AND the field WAS filled — so any failure to land
            // is a diagnosable skip (F-SKIP), not a silent no-op.

            // Resolve which entity the rating is for, per the binding's declared source policy.
            var target = ResolveEntityRef(binding, context);
            if (target is null)
            {
                RecordSkip(ref skips, context, instanceRef, binding, observedAt, ConditionSkipReasons.UnresolvableEntityRef);
                continue;
            }

            // Guard 3: the target MUST exist under this tenant — never a dangling / cross-tenant write.
            var entity = await _entities.GetByIdAsync(context.Tenant, target.Value, cancellationToken)
                .ConfigureAwait(false);
            if (entity is null)
            {
                RecordSkip(ref skips, context, instanceRef, binding, observedAt, ConditionSkipReasons.TargetMissingOrCrossTenant, target.Value);
                continue;
            }

            // Guard 4: a grade outside the binding scale is skipped, not thrown.
            ConditionRating rating;
            try
            {
                rating = new ConditionRating(grade, binding.ScaleMax);
            }
            catch (ArgumentOutOfRangeException)
            {
                RecordSkip(ref skips, context, instanceRef, binding, observedAt, ConditionSkipReasons.GradeOutOfRange, target.Value);
                continue;
            }

            var assessment = new ConditionAssessment
            {
                // F-ATOM: deterministic id ⇒ an outbox replay UPSERTs rather than duplicating.
                Id = ConditionAssessmentId.Derive(instanceRef, binding.FieldPointer, target.Value.Value),
                TenantId = context.Tenant,
                Entity = target.Value,
                Rating = rating,
                ObservedAt = observedAt,
                AssessorRef = context.Actor.Value,
                SourceBinding = binding,
            };

            // Rides the audited, tenant-scoped store — the assessment lands on the durable X-AUDIT chain.
            await _assessments.RecordAsync(assessment, context.Actor.Value, cancellationToken).ConfigureAwait(false);
        }

        return (IReadOnlyList<FormSubmitProjectionSkip>?)skips ?? Array.Empty<FormSubmitProjectionSkip>();
    }

    /// <summary>
    /// F-SKIP + F3: records a <see cref="RegistryOp.ProjectionSkipped"/> event on the durable audit chain
    /// (keyed on the form instance) so a binding-declared capture that did not land is diagnosable, AND
    /// appends a <see cref="FormSubmitProjectionSkip"/> to <paramref name="skips"/> so the submit route can
    /// surface the skip to the user (never silent success). Both carry the reason, the field pointer, and
    /// (when resolved) the target entity — never a submitted value.
    /// </summary>
    private void RecordSkip(
        ref List<FormSubmitProjectionSkip>? skips,
        FormSubmitContext context,
        string instanceRef,
        ConditionRatingFieldBinding binding,
        Instant at,
        string reason,
        RegistryEntityId? target = null)
    {
        var detail = target is { } t
            ? $"reason={reason};field={binding.FieldPointer};target={t.Value}"
            : $"reason={reason};field={binding.FieldPointer}";
        _audit.Append(context.Tenant, instanceRef, RegistryOp.ProjectionSkipped, at, context.Actor.Value, detail);

        (skips ??= new List<FormSubmitProjectionSkip>())
            .Add(new FormSubmitProjectionSkip(reason, binding.FieldPointer, target?.Value));
    }

    /// <summary>
    /// Resolves the target entity id for a rating per the binding's <see cref="ConditionEntityRefSource"/>:
    /// an explicit key, a value read from another submission field, or the pinned visit case.
    /// Returns null when the ref cannot be resolved (a fail-closed skip, never a write).
    /// </summary>
    private static RegistryEntityId? ResolveEntityRef(ConditionRatingFieldBinding binding, FormSubmitContext context)
        => binding.EntityRefSource switch
        {
            ConditionEntityRefSource.Explicit =>
                string.IsNullOrWhiteSpace(binding.EntityRefKey)
                    ? null
                    : (RegistryEntityId?)new RegistryEntityId(binding.EntityRefKey!),

            ConditionEntityRefSource.SubmissionField =>
                binding.EntityRefKey is { Length: > 0 } key
                && TryReadString(context.SubmittedValues, key, out var fieldValue)
                    ? (RegistryEntityId?)new RegistryEntityId(fieldValue)
                    : null,

            ConditionEntityRefSource.VisitCase =>
                string.IsNullOrWhiteSpace(context.CaseRef)
                    ? null
                    : (RegistryEntityId?)new RegistryEntityId(context.CaseRef!),

            _ => null,
        };

    private static bool TryReadInt(JsonDocument values, string pointer, out int value)
    {
        value = 0;
        if (!TryGetField(values, pointer, out var element))
        {
            return false;
        }
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt32(out value) => true,
            JsonValueKind.String when int.TryParse(
                element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) => true,
            _ => false,
        };
    }

    private static bool TryReadString(JsonDocument values, string pointer, out string value)
    {
        value = string.Empty;
        if (!TryGetField(values, pointer, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    /// <summary>
    /// Reads a field from the submitted values object. Resolution is two-stage, so both a FLAT candidate
    /// (a top-level scalar field) and a NESTED one (ADR 0055 Rev 7 — a grouped / collection-row field
    /// whose value nests under the item key, per <c>SubmitValidationGate</c>) are covered:
    /// <list type="number">
    ///   <item>a full RFC-6901 pointer (<c>/electrical/wiring/electricalWiringRating</c>) is DESCENDED
    ///     segment-by-segment through the nested objects / arrays — the nested-leaf path;</item>
    ///   <item>failing that, the pointer's LAST segment is looked up at the candidate root — the legacy
    ///     flat path, so a plain field name or a <c>/section/field</c> pointer over a flat candidate still
    ///     lands on the root field (byte-identical to the pre-nesting behaviour).</item>
    /// </list>
    /// A single-segment pointer (<c>/field</c> or <c>field</c>) resolves identically under both stages, so
    /// every existing binding is unaffected; only a multi-segment pointer over a genuinely nested candidate
    /// takes the new descent path.
    /// </summary>
    private static bool TryGetField(JsonDocument values, string pointer, out JsonElement element)
    {
        element = default;
        if (values.RootElement.ValueKind != JsonValueKind.Object || string.IsNullOrEmpty(pointer))
        {
            return false;
        }

        // (1) Nested descent — a full RFC-6901 pointer resolves the nested leaf (Rev-7 value nesting).
        if (pointer.StartsWith('/') && TryResolvePointer(values.RootElement, pointer, out element))
        {
            return true;
        }

        // (2) Flat fallback — the last segment at the candidate root (back-compat).
        return values.RootElement.TryGetProperty(FieldName(pointer), out element);
    }

    /// <summary>
    /// Descends a full RFC-6901 JSON pointer through the candidate document — object members by key,
    /// array elements by numeric index — unescaping <c>~1</c>→<c>/</c> then <c>~0</c>→<c>~</c> per the
    /// standard. Returns false (and does not surface a partial match) if any segment cannot be resolved.
    /// </summary>
    private static bool TryResolvePointer(JsonElement root, string pointer, out JsonElement element)
    {
        element = root;
        var segments = pointer.Split('/');
        // Leading '/' ⇒ segments[0] is the empty string; the real tokens start at index 1.
        for (var i = 1; i < segments.Length; i++)
        {
            var token = segments[i].Replace("~1", "/").Replace("~0", "~");
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (!element.TryGetProperty(token, out element))
                {
                    return false;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array
                && int.TryParse(token, out var index)
                && index >= 0
                && index < element.GetArrayLength())
            {
                element = element[index];
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    private static string FieldName(string pointer)
    {
        var slash = pointer.LastIndexOf('/');
        return slash >= 0 && slash < pointer.Length - 1 ? pointer[(slash + 1)..] : pointer;
    }
}

/// <summary>
/// The stable machine reason codes a <see cref="ConditionAssessmentProjector"/> puts on a
/// <see cref="FormSubmitProjectionSkip"/> (and its <c>ProjectionSkipped</c> audit detail). They are the
/// contract the Harborline App runner localizes (ADR 0101 Rev 3.1 Wave 3a / F3) — keep them in lockstep with
/// the Harborline App's <c>forms.captureSkipped.reason.*</c> i18n keys.
/// </summary>
public static class ConditionSkipReasons
{
    /// <summary>The binding's entity-ref could not be resolved from the submission / visit case.</summary>
    public const string UnresolvableEntityRef = "unresolvable-entity-ref";

    /// <summary>The resolved target entity does not exist under this tenant (dangling / cross-tenant).</summary>
    public const string TargetMissingOrCrossTenant = "target-missing-or-cross-tenant";

    /// <summary>The submitted grade is outside the binding's condition scale.</summary>
    public const string GradeOutOfRange = "grade-out-of-range";
}
