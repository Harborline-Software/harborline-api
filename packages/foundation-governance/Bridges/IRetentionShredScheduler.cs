using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Recovery.Erasure;

namespace Harborline.Api.Foundation.Governance.Bridges;

/// <summary>
/// The greenfield retention→shred scheduler (ADR 0140 D2 §7 / ADR 0137 §D8 propose-then-release).
/// It PROPOSES subjects whose retention floor has lapsed and that are under no legal hold; it
/// NEVER destroys a key. Humans RELEASE a proposal through the ≥2-approver erasure path
/// (ADR 0068). Kill-trigger: the scheduler proposes; it never auto-calls EraseAsync.
/// </summary>
public interface IRetentionShredScheduler
{
    /// <summary>
    /// Return the subset of <paramref name="candidates"/> eligible for a shred proposal as of
    /// <paramref name="asOf"/> (retention lapsed AND no active legal hold). PROPOSE-ONLY.
    /// </summary>
    IReadOnlyList<ShredProposal> ProposeExpired(IReadOnlyList<ShredCandidate> candidates, DateTimeOffset asOf);
}

/// <summary>A subject whose retention may have lapsed.</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Subject">The data subject.</param>
/// <param name="RetainedUntil">The instant the subject's retention floor lapses.</param>
public sealed record ShredCandidate(TenantId Tenant, SubjectId Subject, DateTimeOffset RetainedUntil);

/// <summary>A propose-only shred recommendation awaiting human release (≥2 approvers).</summary>
/// <param name="Tenant">The tenant.</param>
/// <param name="Subject">The data subject.</param>
/// <param name="Reason">Why the subject is eligible.</param>
public sealed record ShredProposal(TenantId Tenant, SubjectId Subject, string Reason);
