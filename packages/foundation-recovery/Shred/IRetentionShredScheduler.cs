using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.Foundation.Recovery.Shred;

/// <summary>
/// The retention→shred scheduler's PROPOSE half (ADR 0137 §D8 propose-then-release).
/// It returns the subset of candidates eligible for a shred PROPOSAL; it NEVER
/// destroys a key and holds NO reference to the erasure substrate — a proposal is
/// advisory and awaits a human ≥2-approver release (<see cref="IShredReleaseService"/>).
/// This propose/destroy split is what makes "the scheduler auto-shreds" structurally
/// impossible.
/// </summary>
public interface IRetentionShredScheduler
{
    /// <summary>
    /// Return the subset of <paramref name="candidates"/> eligible for a shred proposal
    /// as of <paramref name="asOf"/>: retention floor lapsed AND disposition is
    /// <see cref="RetentionDisposition.Shred"/> AND no active legal hold. PROPOSE-ONLY.
    /// The legal-hold gate is consulted per candidate (fail-closed: a held or
    /// unresolvable subject is never proposed).
    /// </summary>
    ValueTask<IReadOnlyList<ShredProposal>> ProposeExpiredAsync(
        IReadOnlyList<ShredCandidate> candidates,
        DateTimeOffset asOf,
        CancellationToken ct = default);
}
