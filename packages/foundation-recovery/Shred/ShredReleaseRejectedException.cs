using System;

namespace Harborline.Api.Foundation.Recovery.Shred;

/// <summary>
/// Thrown by <see cref="IShredReleaseService.ReleaseAsync"/> when a release fails the
/// confirm-time separation-of-duties gate (proposer ∈ approvers) or the ≥2-distinct-
/// approver floor (ADR 0137 §M-3 / ADR 0068 §3.1). Fail-closed: a rejected release
/// destroys NO key. (The erasure substrate ALSO re-enforces the floor + the hold gate,
/// so this exception is the early, SoD-specific rejection, not the only guard.)
/// </summary>
public sealed class ShredReleaseRejectedException : Exception
{
    /// <summary>Construct with the rejection reason.</summary>
    public ShredReleaseRejectedException(string reason)
        : base($"Shred release rejected: {reason}")
    {
        Reason = reason;
    }

    /// <summary>Short machine-stable rejection reason (e.g. <c>"proposer cannot be an approver"</c>, <c>"approval floor not met"</c>).</summary>
    public string Reason { get; }
}
