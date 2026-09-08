namespace Harborline.Api.Foundation.Forms.Submission;

/// <summary>
/// A <b>registered post-submit projection hook</b> — the forms "field-kind extension point"
/// (ADR 0101 Rev 3.1 Wave 2 / A3). A projection inspects a just-persisted submission
/// (<see cref="FormSubmitContext"/>) and, when the form carries a field kind it owns, writes the
/// governed side record for that kind (e.g. a condition-rating field → a typed condition assessment).
/// </summary>
/// <remarks>
/// <para>
/// <b>Keyed by binding, not a plugin system.</b> This is intentionally narrow: a projection is
/// activated by a specific declared BINDING on a form (the binding names the field + how to resolve
/// the target), not by an open-ended script or a general capability to mutate arbitrary aggregates.
/// A form with no binding a projection recognizes is a cheap no-op. Introducing a general form-plugin
/// runtime was explicitly out of scope — the seam exists only to turn "filling an inspection field"
/// into "one act, two artifacts" (the submission + the side record) for the handful of governed
/// field kinds the platform ships.
/// </para>
/// <para>
/// <b>Server-side + audited + deterministic.</b> A projection runs on the node inside the same
/// authorized submit that produced the instance; it writes through the target aggregate's own
/// audited, tenant-scoped store (so the side record is on the durable audit chain like any other
/// mutation); and it records at <see cref="FormSubmitContext.SubmittedAt"/> (the submit clock), never
/// an ambient now.
/// </para>
/// <para>
/// <b>Fail-safe contract.</b> A projection MUST treat an expected non-match — no binding for this
/// form, an unresolvable / cross-tenant target, an out-of-range value — as a graceful no-op (it does
/// nothing and returns), NOT an exception. It should throw only on an unexpected infrastructure
/// failure. The runner surfaces a throw to the caller (the submission has already persisted), so a
/// throw is reserved for genuine faults an operator must see.
/// </para>
/// <para>
/// <b>Reported skips (F3).</b> When a projection could not land a capture the tenant DECLARED (an
/// out-of-range value, an unresolvable / cross-tenant target) it audits the skip AND returns a
/// <see cref="FormSubmitProjectionSkip"/> describing it, so the submit path can honestly tell the user
/// the side record did not happen rather than showing silent success. A genuine no-op (no binding, an
/// unfilled field) returns no skip. The return is the skips this projection recorded (empty for the
/// common all-landed / no-op case).
/// </para>
/// </remarks>
public interface IFormSubmitProjection
{
    /// <summary>
    /// Projects the submission described by <paramref name="context"/> into this projection's side
    /// record, or does nothing when the form carries no binding this projection owns. Returns the
    /// binding-declared captures it could NOT land (F3) — empty when everything landed or the form
    /// carries no binding this projection owns.
    /// </summary>
    Task<IReadOnlyList<FormSubmitProjectionSkip>> ProjectAsync(
        FormSubmitContext context, CancellationToken cancellationToken = default);
}
