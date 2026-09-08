using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Forms.Submission;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.Foundation.Forms.Engine;

/// <summary>
/// An <see cref="IFormEngine"/> decorator that runs the registered post-submit projections
/// (<see cref="IFormSubmitProjectionRunner"/>) after a successful <see cref="SaveAsync"/> — the
/// node-side wiring of the forms field-kind extension point (ADR 0101 Rev 3.1 Wave 2).
/// </summary>
/// <remarks>
/// <para>
/// The decorator leaves the shipped <see cref="FormEngine"/> untouched (lowest blast radius on a
/// security-critical substrate). Render + validate pass straight through; only <see cref="SaveAsync"/>
/// is augmented: the inner engine validates, prunes, persists, and audits the submission exactly as
/// before, then the runner fires the projections over the just-persisted instance. A form that
/// carries no binding any projection recognizes makes the runner a cheap no-op, so the decorator is
/// safe to wrap every submit path.
/// </para>
/// <para>
/// <b>Ordering + failure.</b> Projection runs strictly AFTER the submission has persisted + audited,
/// so a projection can rely on the instance existing. The projections are handed the SUBMITTED
/// (cleartext, pre-prune) candidate — a projection can read a field the engine hides from the
/// persisted body — and the ENGINE'S OWN submit instant (from the <see cref="FormSubmitReceipt"/>,
/// F-CLOCK) as the deterministic timestamp, so the submission and its side record share one clock. A
/// projection is contracted to no-op on an expected non-match and throw only on a genuine fault; such a
/// throw surfaces to the caller (the submission itself has already committed).
/// </para>
/// </remarks>
public sealed class ProjectingFormEngine : IFormEngine
{
    private readonly IFormEngine _inner;
    private readonly IFormSubmitProjectionRunner _runner;

    /// <summary>Wraps <paramref name="inner"/> so successful saves fire the projection runner.</summary>
    public ProjectingFormEngine(IFormEngine inner, IFormSubmitProjectionRunner runner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    /// <inheritdoc />
    public Task<FormView> RenderAsync(FormDefinitionId form, EntityId? instance, CapabilityToken token, CancellationToken ct)
        => _inner.RenderAsync(form, instance, token, ct);

    /// <inheritdoc />
    public Task<ValidationResult> ValidateAsync(FormDefinitionId form, JsonDocument candidate, CapabilityToken token, CancellationToken ct)
        => _inner.ValidateAsync(form, candidate, token, ct);

    /// <inheritdoc />
    public async Task<EntityId> SaveAsync(FormDefinitionId form, JsonDocument candidate, CapabilityToken token, CancellationToken ct)
        => await _inner.SaveAsync(form, candidate, token, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<FormSubmitReceipt> SaveWithReceiptAsync(
        FormDefinitionId form,
        JsonDocument candidate,
        CapabilityToken token,
        AuthorizationWriteContext authority,
        CancellationToken ct = default,
        string? idempotencyKey = null,
        string? caseRef = null)
    {
        // PRE-commit: validation / capability / not-found / idempotency-conflict all throw HERE and
        // propagate UNCHANGED — there is no committed submission to defer, so the caller keeps its
        // existing error semantics (F-ROUTE: do not blur the pre/post-commit boundary). caseRef is a
        // projection-only hint — it flows to the context below, never into what the inner engine persists.
        var receipt = await _inner.SaveWithReceiptAsync(form, candidate, token, authority, ct, idempotencyKey, caseRef).ConfigureAwait(false);

        // Post-submit projection: the engine already validated + authorized this submit and created
        // the instance. Keyed-by-binding projections turn the submission into their governed side
        // records; a form with no matching binding is a no-op. The submitted (cleartext) candidate is
        // passed so a projection can read a field even if the engine pruned it from the persisted body.
        // F-CLOCK: the submit instant is the engine's own (receipt), not a fresh reading here.
        // #144: caseRef pins the record the submission is filled into — a VisitCase-sourced condition
        // projection resolves its target from it, and the generic submission-record projection links to it.
        var context = new FormSubmitContext(
            Form: form,
            InstanceId: receipt.InstanceId,
            Tenant: token.Tenant,
            Actor: token.Subject,
            SubmittedAt: receipt.SubmittedAt,
            SubmittedValues: candidate,
            CaseRef: caseRef);

        IReadOnlyList<FormSubmitProjectionSkip> skips;
        try
        {
            skips = await _runner.RunAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // POST-commit (F-ROUTE): the submission is already committed + audited, and the durable
            // at-least-once outbox holds a Failed/Pending row the reconcile sweep (F-RECON) will heal.
            // Surface the COMMITTED receipt so a route returns success-with-pending rather than a
            // retry-inviting 500 that — with no submit idempotency — would double-submit. The projection
            // fault is preserved as the inner exception for diagnosis. (A genuine cancellation is left to
            // propagate as cancellation; the outbox already recorded the row for the next sweep.)
            throw new Exceptions.FormSubmitProjectionPendingException(receipt, ex);
        }

        // F3 (Wave 3a): a binding-declared capture that could not land (out-of-range grade, unresolvable
        // target) is audited server-side AND surfaced on the receipt, so the route can honestly tell the
        // user rather than showing silent success. No skips ⇒ the receipt is unchanged (byte-identical).
        return skips.Count > 0 ? receipt with { ProjectionSkips = skips } : receipt;
    }
}
