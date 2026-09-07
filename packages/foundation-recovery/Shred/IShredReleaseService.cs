using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Recovery.Shred;

/// <summary>
/// The retention→shred scheduler's RELEASE half (ADR 0137 §M-3 propose-then-release).
/// Takes a human release request, enforces the confirm-time separation-of-duties gate
/// (proposer ≠ approver) and the ≥2-distinct-approver floor, then executes the shred
/// through <see cref="ISubjectErasureService"/> — the single key-destruction choke point,
/// which independently re-enforces the approval floor, the mandatory dwell window, and
/// the fail-closed legal-hold gate. It holds NO reference to the erasure REGISTRY, so it
/// cannot destroy a key except through that gated service.
/// </summary>
public interface IShredReleaseService
{
    /// <summary>
    /// Release (execute) a shred. Returns the erasure outcome — <see cref="SubjectErasureOutcome.Erased"/>
    /// on success, <see cref="SubjectErasureOutcome.BlockedByLegalHold"/> if a hold was placed after the
    /// proposal (the shred is refused, nothing destroyed), or <see cref="SubjectErasureOutcome.AlreadyErased"/>
    /// if it was already shredded.
    /// </summary>
    /// <exception cref="ShredReleaseRejectedException">Proposer is among the approvers, or the ≥2-approver floor is not met.</exception>
    Task<SubjectErasureResult> ReleaseAsync(ShredReleaseRequest request, CancellationToken ct = default);
}
