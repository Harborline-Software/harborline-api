using System;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Recovery.Erasure;

/// <summary>
/// The single, manual, legal-sign-off-gated path that performs a GDPR
/// crypto-shred (ADR 0135 GDPR direction). This is the ONLY way a subject is
/// erased — there is no public "delete" endpoint (ADR 0068 §1.3 / §5.1: Harborline
/// exposes no delete-audit-record affordance; erasure is a multi-actor, audited,
/// operator action).
/// </summary>
/// <remarks>
/// On a valid request the service, in order:
/// <list type="number">
/// <item><description>enforces the multi-actor approval floor (ADR 0068 §3: ≥2 distinct approvers, no self-approval-only);</description></item>
/// <item><description>enforces the mandatory-minimum dwell window between request-filed and execution (ADR 0068 §1.3 minimum window — erasure cannot be a same-instant single step);</description></item>
/// <item><description>destroys the per-subject key by recording the erasure in the <see cref="ISubjectErasureRegistry"/> (after which <c>DeriveSubjectKeyAsync</c> fails closed — the subject's ciphertext is permanently undecryptable);</description></item>
/// <item><description>writes a pseudonymized <see cref="SubjectTombstone"/> in place of the subject's identifying fields;</description></item>
/// <item><description>emits a <c>SubjectErased</c> audit record (the erasure is itself audited; the audit hash-chain stays intact).</description></item>
/// </list>
/// <para>
/// <b>Approval-floor scope (ADR 0068 §3.1).</b> This service enforces ONLY the
/// quantitative part of the floor: <c>≥2</c> approvers that are <em>distinct</em>
/// (Ordinal-distinct actor ids). It does NOT enforce the §3.1 (b) role requirement
/// (at least one Captain/XO-class approver) or the §3.1 (c) separation-of-duties
/// requirement (an approver must not be the proposer). Those are the deployer's
/// responsibility per §GC.1: the host binds operator-supplied <c>ApprovingActors</c>
/// to roles and verifies proposer≠approver in its own authorization layer before
/// invoking <see cref="EraseAsync"/>. The service records the supplied approvers
/// faithfully (they land in the audit record + tombstone) but makes no role or
/// proposer-identity determination. Do not read the count+distinctness check as
/// full §3.1 enforcement.
/// </para>
/// </remarks>
public interface ISubjectErasureService
{
    /// <summary>
    /// Perform the crypto-shred for the requested subject. Idempotent: a second
    /// call for an already-erased subject returns
    /// <see cref="SubjectErasureOutcome.AlreadyErased"/> with the existing
    /// tombstone, without re-emitting the audit event.
    /// </summary>
    /// <exception cref="SubjectErasureRejectedException">
    /// The approval floor or the mandatory-minimum dwell window was not satisfied.
    /// </exception>
    Task<SubjectErasureResult> EraseAsync(SubjectErasureRequest request, CancellationToken ct = default);
}

/// <summary>
/// Thrown by <see cref="ISubjectErasureService.EraseAsync"/> when an erasure
/// request fails the multi-actor approval floor or the mandatory-minimum dwell
/// window. Fail-closed: a rejected request performs NO key destruction, writes NO
/// tombstone, and emits NO erasure audit event.
/// </summary>
public sealed class SubjectErasureRejectedException : Exception
{
    /// <summary>Construct with the rejection reason.</summary>
    public SubjectErasureRejectedException(string reason)
        : base($"Subject erasure rejected: {reason}")
    {
        Reason = reason;
    }

    /// <summary>Short machine-stable rejection reason (e.g. <c>"approval floor not met"</c>, <c>"minimum window not elapsed"</c>).</summary>
    public string Reason { get; }
}
