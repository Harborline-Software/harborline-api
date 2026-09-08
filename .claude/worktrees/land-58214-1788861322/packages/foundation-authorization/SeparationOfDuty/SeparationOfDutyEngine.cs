namespace Harborline.Api.Foundation.Authorization.SeparationOfDuty;

/// <summary>
/// The one place separation of duty is decided (ADR 0067 clause 2). It takes the act, the principal, the
/// approvers on record and the applicable threshold, and returns exactly one
/// <see cref="SeparationOfDutyDecision"/> carrying the five approval facts an audit entry records. A refusal
/// is that same decision with a <see cref="ApprovalRefusalReason"/>, never a thrown-away evaluation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Which model this is.</b> ANSI INCITS 359-2004 (the ANSI RBAC standard) splits separation of duty into
/// SSD — a constraint evaluated when a principal is assigned into a conflicting duty — and DSD, a constraint
/// on which duties may be active together within one session. This engine takes the SSD shape, with the
/// recorded-override amendment ADR 0067 clause 2 adopts from Dynamics 365 F&amp;O: the conflict is evaluated
/// once against the approvers on record for the act, and a conflict may be allowed only with a mandatory
/// recorded reason and a named approver. DSD is deliberately not implemented: our decision is made once per
/// act and frozen into a tamper-evident audit entry, so there is no session activation state to constrain.
/// </para>
/// <para>
/// <b>Which part of clause 2 this is.</b> Only the soft block at the point of approval: the conflict is
/// evaluated against the approvers on record and may be allowed by a named overrider who is not the
/// requester. The hard block at composition definition and the batch revalidation of existing assignments
/// are clause 2's other two halves and are NOT implemented here. Checking at the point of approval against
/// named excluded parties is nearer WS-HumanTask's excluded-owners rule than to SSD proper, which
/// constrains assignment.
/// </para>
/// <para>
/// <b>Deterministic.</b> The engine reads no clock, no configuration and no ambient scope. Every input
/// arrives on <see cref="SeparationOfDutyRequest"/>, and the decided instant is the request instant, so the
/// same request always yields the same decision.
/// </para>
/// </remarks>
public sealed class SeparationOfDutyEngine
{
    /// <summary>The one posting period state under which an approval may proceed.</summary>
    public const string OpenPostingPeriod = "Open";

    /// <summary>
    /// Decides separation of duty for one act. Refusal reasons are tested in a fixed order — a threshold
    /// below one approver, closed period, unreasoned override, unresolved overrider, an unoverridden
    /// conflict, too few approvers — so the reason recorded for a request that breaks more than one rule
    /// is stable. An unresolved posting period refuses nothing; only a resolved, non-open one does.
    /// </summary>
    public SeparationOfDutyDecision Decide(SeparationOfDutyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Threshold);
        ArgumentNullException.ThrowIfNull(request.ApproversOnRecord);

        var approvers = request.ApproversOnRecord
            .Select(entry => entry.Approver)
            .Distinct()
            .ToArray();
        var conflicted = approvers.Contains(request.Principal);

        // An entry carrying a reason at all is claiming to be an override; the two blank cases below are
        // what makes such a claim refusable rather than accepted.
        var overrideEntries = request.ApproversOnRecord
            .Where(entry => entry.OverrideReason is not null)
            .ToArray();
        var unreasonedOverride = overrideEntries
            .Any(entry => string.IsNullOrWhiteSpace(entry.OverrideReason));
        // An override names a party only if the caller RESOLVED one: an unresolved (or blank-resolved)
        // claim is an unnamed overrider, whatever string the caller typed.
        var unnamedOverride = overrideEntries
            .Any(entry => string.IsNullOrWhiteSpace(entry.ResolvedApprover?.Value));

        // Clause 2's soft block: a conflict may be ALLOWED, but only by an override that names an overrider
        // who is resolvable and is not the requester. Anything less leaves the conflict standing.
        var usedOverride = overrideEntries.FirstOrDefault(entry =>
            !string.IsNullOrWhiteSpace(entry.OverrideReason)
            && !string.IsNullOrWhiteSpace(entry.ResolvedApprover?.Value)
            && entry.ResolvedApprover != request.Principal);

        // Override decorates only a decision that HAD a conflict; with no conflict there is nothing to
        // override, so a reason on a clean approver set records a plain pass.
        var separationOfDuty = (conflicted, usedOverride) switch
        {
            (true, { } used) => new SeparationOfDutyFact(
                SeparationOfDutyOutcome.Override,
                used.OverrideReason,
                used.ResolvedApprover!.Value.Value),
            (true, null) => new SeparationOfDutyFact(SeparationOfDutyOutcome.Conflict, null, null),
            _ => new SeparationOfDutyFact(SeparationOfDutyOutcome.Pass, null, null),
        };

        var refusal = ApprovalRefusalReason.None;
        if (request.Threshold.RequiredApprovers < 1)
            refusal = ApprovalRefusalReason.InvalidApprovalThreshold;
        else if (request.PostingPeriodState is { } period
            && !string.Equals(period, OpenPostingPeriod, StringComparison.Ordinal))
            refusal = ApprovalRefusalReason.ClosedPostingPeriod;
        else if (unreasonedOverride)
            refusal = ApprovalRefusalReason.MissingOverrideReason;
        else if (unnamedOverride)
            refusal = ApprovalRefusalReason.MissingOverrideApprover;
        else if (conflicted && usedOverride is null)
            refusal = ApprovalRefusalReason.SelfApproval;
        else if (approvers.Length < request.Threshold.RequiredApprovers)
            refusal = ApprovalRefusalReason.InsufficientApprovers;

        return new SeparationOfDutyDecision(
            request,
            refusal,
            request.Threshold.Policy,
            request.Threshold.Limit,
            request.Threshold.LimitSource,
            separationOfDuty,
            request.PostingPeriodState,
            approvers);
    }
}
