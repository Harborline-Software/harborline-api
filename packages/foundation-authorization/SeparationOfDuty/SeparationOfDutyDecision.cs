using System.Collections.Immutable;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.Foundation.Authorization.SeparationOfDuty;

/// <summary>The separation-of-duty outcome recorded for an act (ADR 0067 clause 2).</summary>
public enum SeparationOfDutyOutcome
{
    /// <summary>No declared conflict between the principal and the approvers on record.</summary>
    Pass = 0,

    /// <summary>A conflict was allowed with a mandatory recorded reason and a RESOLVED approver.</summary>
    Override = 1,

    /// <summary>A conflict that was not overridden; the act is refused.</summary>
    Conflict = 2,
}

/// <summary>Where an applied approval limit came from (mirrors ADR 0068 clause 1's two sources).</summary>
public enum ApprovalLimitSourceKind
{
    Grant = 0,
    SigningLimitAgreement = 1,
}

/// <summary>Why an approval was refused. <see cref="None"/> is the only approved value.</summary>
public enum ApprovalRefusalReason
{
    None = 0,

    /// <summary>The posting period was not open at the decided instant.</summary>
    ClosedPostingPeriod = 1,

    /// <summary>The principal appears among its own approvers on record.</summary>
    SelfApproval = 2,

    /// <summary>An approver is recorded as an override with no reason (ADR 0067 clause 2 makes it mandatory).</summary>
    MissingOverrideReason = 3,

    /// <summary>Fewer distinct approvers on record than the applicable threshold requires.</summary>
    InsufficientApprovers = 4,

    /// <summary>
    /// The applicable threshold demands fewer than one approver. A threshold below one approves an act
    /// nobody approved, so it is a configuration defect refused as a decision naming its reason — never a
    /// pass, and never a throw at the writer, which would lose the four other approval facts.
    /// </summary>
    InvalidApprovalThreshold = 5,

    /// <summary>
    /// An approver is recorded as an override of a conflict but the caller resolved it to no party (or to a
    /// blank one). ADR 0067
    /// clause 2 requires the override be logged where an auditor reads it, which an unnamed overrider is not.
    /// </summary>
    MissingOverrideApprover = 6,
}

/// <summary>The approval policy identifier and immutable version the decision was made under.</summary>
public sealed record ApprovalPolicyFact(string PolicyId, string Version);

/// <summary>The exact grant or signing-limit agreement that supplied the applied limit.</summary>
public sealed record ApprovalLimitSourceFact(ApprovalLimitSourceKind Kind, string SourceId, string? Version);

/// <summary>The separation-of-duty verdict, with the override reason and approver when one was used.</summary>
public sealed record SeparationOfDutyFact(
    SeparationOfDutyOutcome Outcome,
    string? OverrideReason,
    string? OverrideApprover);

/// <summary>
/// One approver on record for the act. A non-null <see cref="OverrideReason"/> marks the approver as an
/// override of a declared conflict; a present-but-blank reason is the refusable case, not an accepted one.
/// </summary>
/// <param name="Approver">The approver's actor id as CLAIMED by the caller.</param>
/// <param name="OverrideReason">Non-null marks this entry as an override attempt; see the summary.</param>
/// <param name="ResolvedApprover">
/// The party the caller RESOLVED <paramref name="Approver"/> to (roster membership plus the permission for
/// the act), or null when it resolved to nobody. An override is allowed only for a resolved party: a null
/// here is <see cref="ApprovalRefusalReason.MissingOverrideApprover"/>, and the recorded
/// <see cref="SeparationOfDutyFact.OverrideApprover"/> is the resolved party, never the claim.
/// </param>
public sealed record ApproverOnRecord(
    ActorId Approver,
    string? OverrideReason = null,
    ActorId? ResolvedApprover = null);

/// <summary>
/// The applicable threshold, read from configuration or a workflow definition by the caller and handed to
/// the engine. The engine never reads an ambient threshold; this record is the only way one enters a decision.
/// </summary>
public sealed record ApprovalThreshold(
    ApprovalPolicyFact Policy,
    decimal Limit,
    int RequiredApprovers,
    ApprovalLimitSourceFact LimitSource);

/// <summary>Everything the engine decides from. No other input exists.</summary>
/// <param name="Act">The act being authorized.</param>
/// <param name="Principal">The requester whose duty conflict is being checked.</param>
/// <param name="Tenant">The tenant the act is scoped to.</param>
/// <param name="ApproversOnRecord">The approvers on record for the act, including any override entry.</param>
/// <param name="Threshold">The applicable approval threshold.</param>
/// <param name="PostingPeriodState">
/// The posting period state the caller RESOLVED from the real period, or null when it resolved none. A null
/// is not a closed period — it refuses nothing and is recorded as the null it is, so a writer with no
/// resolver (ticket 272 slice 5 threads one) cannot assert an observation nobody made.
/// </param>
/// <param name="At">The instant of the decision.</param>
public sealed record SeparationOfDutyRequest(
    PermissionAtom Act,
    ActorId Principal,
    TenantId Tenant,
    IReadOnlyList<ApproverOnRecord> ApproversOnRecord,
    ApprovalThreshold Threshold,
    string? PostingPeriodState,
    DateTimeOffset At);

/// <summary>
/// The single decision object for ADR 0067 clause 2. It carries the five approval facts ticket 222 needs on
/// the audit entry — <see cref="Policy"/>, <see cref="AppliedLimit"/>, <see cref="LimitSource"/>,
/// <see cref="SeparationOfDuty"/> and <see cref="PostingPeriodState"/> — and it carries them whether the act
/// was approved or refused, so a refusal is recorded from the same decision rather than re-derived.
/// </summary>
public sealed class SeparationOfDutyDecision
{
    internal SeparationOfDutyDecision(
        SeparationOfDutyRequest request,
        ApprovalRefusalReason refusalReason,
        ApprovalPolicyFact policy,
        decimal appliedLimit,
        ApprovalLimitSourceFact limitSource,
        SeparationOfDutyFact separationOfDuty,
        string? postingPeriodState,
        IReadOnlyList<ActorId> approvers)
    {
        Request = request;
        RefusalReason = refusalReason;
        Policy = policy;
        AppliedLimit = appliedLimit;
        LimitSource = limitSource;
        SeparationOfDuty = separationOfDuty;
        PostingPeriodState = postingPeriodState;
        Approvers = approvers.ToImmutableArray();
        DecidedAt = request.At;
        // Ticket 212 slice 1: built HERE, from the facts this decision already carries, so an approval
        // verdict without evidence cannot exist. Nothing is re-decided.
        Evidence = AuthorizationDecisionEvidence.ForApproval(
            request.Act,
            request.Principal,
            request.Tenant,
            request.At,
            Approved,
            separationOfDuty.Outcome is SeparationOfDutyOutcome.Conflict,
            refusalReason.ToString(),
            separationOfDuty.Outcome.ToString(),
            $"{policy.PolicyId}@{policy.Version}",
            appliedLimit,
            $"{limitSource.Kind}:{limitSource.SourceId}@{limitSource.Version ?? "-"}",
            postingPeriodState,
            Approvers);
    }

    /// <summary>The request this decision was made from.</summary>
    public SeparationOfDutyRequest Request { get; }

    /// <summary>True when the act may proceed. Equivalent to <see cref="RefusalReason"/> being None.</summary>
    public bool Approved => RefusalReason is ApprovalRefusalReason.None;

    /// <summary>Why the act was refused; None when approved.</summary>
    public ApprovalRefusalReason RefusalReason { get; }

    /// <summary>Approval fact 1 — the policy identifier and version.</summary>
    public ApprovalPolicyFact Policy { get; }

    /// <summary>Approval fact 2 — the monetary limit actually applied, copied as a scalar.</summary>
    public decimal AppliedLimit { get; }

    /// <summary>Approval fact 3 — the grant or agreement the limit came from.</summary>
    public ApprovalLimitSourceFact LimitSource { get; }

    /// <summary>Approval fact 4 — the separation-of-duty verdict and any recorded override.</summary>
    public SeparationOfDutyFact SeparationOfDuty { get; }

    /// <summary>
    /// Approval fact 5 — the posting period state at the decided instant, as resolved by the caller. Null
    /// when nobody resolved one.
    /// </summary>
    public string? PostingPeriodState { get; }

    /// <summary>The distinct approvers on record, in the order given.</summary>
    public IReadOnlyList<ActorId> Approvers { get; }

    /// <summary>The instant of the decision. Always the request instant; the engine reads no clock.</summary>
    public DateTimeOffset DecidedAt { get; }

    /// <summary>
    /// The one structured evidence object this verdict carries (ticket 212, ledger L650). Never null: it
    /// is built by the only constructor, from the five approval facts above and nothing else.
    /// </summary>
    public AuthorizationDecisionEvidence Evidence { get; }
}
