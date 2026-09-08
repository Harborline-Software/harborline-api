using Harborline.Api.Foundation.Governance.Bridges;

namespace Harborline.Api.Foundation.Governance.Bridges;

/// <summary>
/// Default <see cref="IRetentionShredScheduler"/> — pure, propose-only. Filters candidates
/// whose retention has lapsed and that are under no active legal hold. It returns proposals;
/// it performs NO key destruction (that is the human-released ≥2-approver erasure path).
/// </summary>
public sealed class DefaultRetentionShredScheduler : IRetentionShredScheduler
{
    private readonly ILegalHoldRegistry _holds;

    /// <summary>Construct over the legal-hold registry the scheduler must respect.</summary>
    public DefaultRetentionShredScheduler(ILegalHoldRegistry holds)
        => _holds = holds ?? throw new ArgumentNullException(nameof(holds));

    /// <inheritdoc />
    public IReadOnlyList<ShredProposal> ProposeExpired(IReadOnlyList<ShredCandidate> candidates, DateTimeOffset asOf)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        var proposals = new List<ShredProposal>();
        foreach (var c in candidates)
        {
            if (c.RetainedUntil > asOf) continue;            // retention floor still active
            if (_holds.IsHeld(c.Tenant, c.Subject)) continue; // legal hold blocks the proposal
            proposals.Add(new ShredProposal(c.Tenant, c.Subject,
                $"retention lapsed at {c.RetainedUntil:O}; no legal hold — propose for ≥2-approver release."));
        }
        return proposals;
    }
}
