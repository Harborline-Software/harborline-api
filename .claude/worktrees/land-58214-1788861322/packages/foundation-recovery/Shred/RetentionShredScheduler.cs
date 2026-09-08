using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Harborline.Api.Foundation.Recovery.LegalHold;

namespace Harborline.Api.Foundation.Recovery.Shred;

/// <summary>
/// Reference <see cref="IRetentionShredScheduler"/> — pure, propose-only (ADR 0137
/// §D8). Filters candidates whose retention has lapsed, whose disposition is
/// <see cref="RetentionDisposition.Shred"/>, and that are under no active legal hold.
/// It returns proposals; it performs NO key destruction and takes NO dependency on the
/// erasure substrate (the structural guarantee that the propose step cannot shred).
/// </summary>
public sealed class RetentionShredScheduler : IRetentionShredScheduler
{
    private readonly ILegalHoldRegistry _holds;

    /// <summary>Construct over the fail-closed legal-hold registry the scheduler must respect.</summary>
    public RetentionShredScheduler(ILegalHoldRegistry holds)
        => _holds = holds ?? throw new ArgumentNullException(nameof(holds));

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<ShredProposal>> ProposeExpiredAsync(
        IReadOnlyList<ShredCandidate> candidates,
        DateTimeOffset asOf,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var proposals = new List<ShredProposal>();
        foreach (var c in candidates)
        {
            if (c.Disposition != RetentionDisposition.Shred)
            {
                continue; // disposition is retain/archive — never a shred proposal
            }
            if (c.RetainedUntil > asOf)
            {
                continue; // retention floor still active
            }
            // Fail-closed: a held OR unresolvable subject is never proposed (the registry
            // answers "held" when it cannot be consulted).
            if (await _holds.IsSubjectHeldAsync(c.Tenant, c.Subject, ct).ConfigureAwait(false))
            {
                continue;
            }
            proposals.Add(new ShredProposal(c.Tenant, c.Subject,
                $"retention lapsed at {c.RetainedUntil:O}; disposition=shred; no legal hold "
                + "— propose for >=2-approver release."));
        }
        return proposals;
    }
}
