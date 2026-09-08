using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Forms.Submission;
using Instant = Harborline.Api.Foundation.Assets.Common.Instant;

namespace Harborline.Api.Blocks.Assets.Registry.Projection;

/// <summary>
/// The generic <b>submission → record-link projector</b> (#144 runtime form-fill): when a form submission
/// carries a <see cref="FormSubmitContext.CaseRef"/> that resolves to an existing registry entity under
/// the tenant, this projection writes a <see cref="FormSubmissionRecord"/> linking the submission to that
/// record — so the record's detail panel can list its submitted forms and reopen each read-only.
/// </summary>
/// <remarks>
/// <para>
/// It is the second concrete <see cref="IFormSubmitProjection"/> alongside the
/// <see cref="ConditionAssessmentProjector"/>, and it reuses the exact same seam + fail-closed guards — no
/// new engine plumbing. Unlike the condition projector it is NOT binding-gated: ANY submission that names
/// a resolvable record leaves this generic link (a form need not declare a condition-rating field to be
/// "filled into" a record). It writes through the audited, tenant-scoped
/// <see cref="IFormSubmissionRecordStore"/>, so the link rides the durable X-AUDIT chain, and records at
/// the submit clock (deterministic — never an ambient now).
/// </para>
/// <para>
/// <b>Fail-closed guards (refute targets):</b>
/// <list type="number">
///   <item>the sentinel / default tenant is refused up front — it can own no registry data;</item>
///   <item>an absent / whitespace case ref is a silent no-op (a standalone fill with no record context is
///     legitimate — NOT a skip);</item>
///   <item>a case ref that does NOT resolve to an entity under THIS tenant (via
///     <see cref="IRegistryEntityRepository"/>) is a silent no-op — never a dangling / cross-tenant write.
///     The case ref is an UNTRUSTED projection hint; this existence check is the security boundary.</item>
/// </list>
/// Every one of those is an expected no-op — this projection never surfaces a
/// <see cref="FormSubmitProjectionSkip"/> (an unresolvable link is not a binding-declared capture that
/// "should have" landed; the condition projector owns declared-capture skips). It throws only on an
/// unexpected store fault.
/// </para>
/// <para>
/// <b>F-ATOM — idempotent replay.</b> The record id is DERIVED deterministically from (instance, entity),
/// so the at-least-once outbox can safely re-run this projection after an interrupted first attempt: a
/// replay UPSERTs the same row instead of duplicating it.
/// </para>
/// </remarks>
public sealed class FormSubmissionRecordProjection : IFormSubmitProjection
{
    private readonly IFormSubmissionRecordStore _records;
    private readonly IRegistryEntityRepository _entities;

    /// <summary>Composes the projector over the audited submission-record store + the entity repository.</summary>
    public FormSubmissionRecordProjection(IFormSubmissionRecordStore records, IRegistryEntityRepository entities)
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
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

        // Guard 2: no case ref ⇒ a standalone fill with no record context — a legitimate silent no-op.
        if (string.IsNullOrWhiteSpace(context.CaseRef))
        {
            return Array.Empty<FormSubmitProjectionSkip>();
        }

        var target = new RegistryEntityId(context.CaseRef);

        // Guard 3: the target MUST exist under this tenant — never a dangling / cross-tenant write. The
        // case ref is an untrusted hint; this existence check is the security boundary (a bogus / another
        // tenant's / deleted id resolves to null and the link is silently dropped).
        var entity = await _entities.GetByIdAsync(context.Tenant, target, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Array.Empty<FormSubmitProjectionSkip>();
        }

        var instanceRef = context.InstanceId.ToString();
        var record = new FormSubmissionRecord
        {
            // F-ATOM: deterministic id ⇒ an outbox replay UPSERTs rather than duplicating.
            Id = FormSubmissionRecordId.Derive(instanceRef, target.Value),
            TenantId = context.Tenant,
            Entity = target,
            InstanceId = instanceRef,
            FormId = context.Form.Value,
            SubmittedAt = new Instant(context.SubmittedAt),
            AssessorRef = context.Actor.Value,
        };

        // Rides the audited, tenant-scoped store — the link lands on the durable X-AUDIT chain.
        await _records.RecordAsync(record, context.Actor.Value, cancellationToken).ConfigureAwait(false);

        return Array.Empty<FormSubmitProjectionSkip>();
    }
}
