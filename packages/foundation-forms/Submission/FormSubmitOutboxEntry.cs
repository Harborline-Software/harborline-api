using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// One durable at-least-once outbox record for a submission's post-submit projection (ADR 0101 Rev
/// 3.1 Wave 2b / F-ATOM). It carries exactly what a reconcile sweep needs to re-run the projection
/// deterministically after a crash: the submitted context, serialized so it survives a process death.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed by the form instance.</b> <see cref="Id"/> is the instance's canonical id, so enqueuing
/// the same submission twice is idempotent (a replay never creates a second pending row).
/// </para>
/// <para>
/// <b>PII posture.</b> The entry holds the submitted values only for the pending window; a production
/// durable outbox encrypts the payload at rest exactly as the forms engine encrypts fields, and prunes
/// the row on <see cref="FormSubmitOutboxState.Completed"/>. The in-memory first-slice keeps the
/// payload in process memory only — no worse than the candidate already resident during the request.
/// </para>
/// </remarks>
/// <param name="Id">The outbox key — the form instance's canonical id (idempotent enqueue).</param>
/// <param name="Form">The submitted form definition id (string form).</param>
/// <param name="InstanceId">The persisted instance's canonical id (string form).</param>
/// <param name="Tenant">The tenant the submit was authorized under.</param>
/// <param name="Actor">The acting principal.</param>
/// <param name="SubmittedAt">The engine's submit instant (the deterministic projection clock).</param>
/// <param name="SubmittedValuesJson">The submitted values, serialized (re-parsed on reconcile).</param>
/// <param name="CaseRef">The optional visit/case reference.</param>
/// <param name="State">The row's lifecycle state.</param>
/// <param name="Attempts">How many times the projection has been attempted (diagnosis / backoff input).</param>
/// <param name="LastError">The most recent failure message, when <see cref="State"/> is Failed.</param>
public sealed record FormSubmitOutboxEntry(
    string Id,
    string Form,
    string InstanceId,
    string Tenant,
    string Actor,
    DateTimeOffset SubmittedAt,
    string SubmittedValuesJson,
    string? CaseRef,
    FormSubmitOutboxState State,
    int Attempts,
    string? LastError)
{
    /// <summary>
    /// Rebuilds the <see cref="FormSubmitContext"/> a reconcile sweep re-runs — re-parsing the stored
    /// values into a fresh <see cref="JsonDocument"/>. The caller owns and disposes
    /// <see cref="FormSubmitContext.SubmittedValues"/>.
    /// </summary>
    public FormSubmitContext RebuildContext() => new(
        Form: new FormDefinitionId(Form),
        InstanceId: EntityId.Parse(InstanceId),
        Tenant: new TenantId(Tenant),
        Actor: new ActorId(Actor),
        SubmittedAt: SubmittedAt,
        SubmittedValues: JsonDocument.Parse(SubmittedValuesJson),
        CaseRef: CaseRef);

    /// <summary>Captures the durable enqueue snapshot of a just-persisted submission (state Pending).</summary>
    public static FormSubmitOutboxEntry FromContext(FormSubmitContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new FormSubmitOutboxEntry(
            Id: context.InstanceId.ToString(),
            Form: context.Form.ToString(),
            InstanceId: context.InstanceId.ToString(),
            Tenant: context.Tenant.ToString(),
            Actor: context.Actor.ToString(),
            SubmittedAt: context.SubmittedAt,
            SubmittedValuesJson: context.SubmittedValues.RootElement.GetRawText(),
            CaseRef: context.CaseRef,
            State: FormSubmitOutboxState.Pending,
            Attempts: 0,
            LastError: null);
    }
}
