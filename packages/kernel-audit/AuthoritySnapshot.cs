namespace Harborline.Api.Kernel.Audit;

/// <summary>
/// Authority facts frozen when an audited act is appended. ADR 0068 clause 1
/// forbids reconstructing these facts from a later grant state.
/// </summary>
/// <param name="Grants">
/// Every grant and owner-version the act was authorized under, empty when not grant-backed. This is a
/// list because a request may be authorized under more than one pinned grant; an audit snapshot that
/// recorded only one of them would misstate the authority the act was made under, which is the single
/// question this field exists to answer.
/// </param>
/// <param name="Policy">The approval policy identifier and version used for the decision, when the approval path supplies it.</param>
/// <param name="AppliedLimit">The monetary limit actually applied, copied as a scalar at decision time.</param>
/// <param name="LimitSource">The grant or signing-limit agreement from which the applied limit came.</param>
/// <param name="SeparationOfDuty">The separation-of-duty verdict, when an SoD engine supplies one.</param>
/// <param name="PostingPeriodState">The posting period state at the moment of decision, when a financial approval path supplies it.</param>
/// <param name="DerivationIds">Distinct authorization-definition derivation identifiers copied from the decision.</param>
/// <param name="Principal">The principal carried by the decision.</param>
/// <param name="Tenant">The tenant carried by the decision.</param>
/// <param name="Instant">The instant at which the carried decision was made.</param>
/// <param name="Resolution">The copied gate resolution trace; audit never rebuilds these steps.</param>
/// <param name="TraceVersion">The evidence schema version the four public steps were projected at (ticket 212).</param>
/// <param name="Counterfactual">
/// The minimal verdict-changing counterfactual derived from the SAME evidence (ticket 212, ledger
/// L649/L693). Copied like every other field here; nothing re-derives it at read time, and it names a
/// binding, grant or validity change and never a person.
/// </param>
/// <param name="Trace">
/// The four ordered public steps projected from the decision's evidence object (ticket 212, ledger L648).
/// Copied from the decision like every other field here; audit never projects a second time and never
/// re-evaluates.
/// </param>
public sealed record AuthoritySnapshot(
    IReadOnlyList<AuthorityGrantSnapshot> Grants,
    AuthorityPolicySnapshot? Policy,
    decimal? AppliedLimit,
    AuthorityLimitSourceSnapshot? LimitSource,
    SeparationOfDutySnapshot? SeparationOfDuty,
    string? PostingPeriodState,
    IReadOnlyList<string>? DerivationIds = null,
    string? Principal = null,
    string? Tenant = null,
    DateTimeOffset? Instant = null,
    IReadOnlyList<AuthorityResolutionStepSnapshot>? Resolution = null,
    int? TraceVersion = null,
    IReadOnlyList<AuthorityTraceStepSnapshot>? Trace = null,
    AuthorityCounterfactualSnapshot? Counterfactual = null)
{
    internal AuthoritySnapshot Copy() => new(
        [.. Grants.Select(grant => new AuthorityGrantSnapshot(grant.GrantId, grant.OwnerVersion))],
        Policy is null ? null : new AuthorityPolicySnapshot(Policy.PolicyId, Policy.Version),
        AppliedLimit,
        LimitSource is null ? null : new AuthorityLimitSourceSnapshot(
            LimitSource.Kind, LimitSource.SourceId, LimitSource.Version),
        SeparationOfDuty is null ? null : new SeparationOfDutySnapshot(
            SeparationOfDuty.Result,
            SeparationOfDuty.OverrideReason,
            SeparationOfDuty.OverrideApprover,
            SeparationOfDuty.RefusalReason),
        PostingPeriodState,
        [.. DerivationIds ?? []],
        Principal,
        Tenant,
        Instant,
        [.. (Resolution ?? []).Select(step => new AuthorityResolutionStepSnapshot(
            step.Stage, [.. step.Inputs], [.. step.Outputs]))],
        TraceVersion,
        [.. (Trace ?? []).Select(step => new AuthorityTraceStepSnapshot(
            step.Ordinal, step.Stage, [.. step.Facts]))],
        Counterfactual);
}

/// <summary>A copied reference to the grant authority used for the act.</summary>
public sealed record AuthorityGrantSnapshot(string GrantId, long? OwnerVersion);

/// <summary>The identifier and immutable version of an approval policy.</summary>
public sealed record AuthorityPolicySnapshot(string PolicyId, string Version);

/// <summary>The supported sources of an approval limit under ADR 0068 clause 1.</summary>
public enum AuthorityLimitSourceKind
{
    Grant = 0,
    SigningLimitAgreement = 1,
}

/// <summary>The exact grant or signing-limit agreement that supplied an applied limit.</summary>
public sealed record AuthorityLimitSourceSnapshot(
    AuthorityLimitSourceKind Kind,
    string SourceId,
    string? Version);

/// <summary>
/// The result of the separation-of-duty decision. Mirrors
/// <c>Harborline.Api.Foundation.Authorization.SeparationOfDuty.SeparationOfDutyOutcome</c> member for member,
/// including the numeric values, so the stored spelling of an outcome cannot drift from the decided one.
/// </summary>
public enum SeparationOfDutyResult
{
    Pass = 0,
    Override = 1,

    /// <summary>A conflict that was not overridden. Ticket 272 slice 2: without this a refusal
    /// had no faithful spelling and would have been stored as a <see cref="Pass"/>.</summary>
    Conflict = 2,
}

/// <summary>
/// Why the approval behind the act was refused, mirroring
/// <c>Harborline.Api.Foundation.Authorization.SeparationOfDuty.ApprovalRefusalReason</c> member for member
/// and value for value. <see cref="None"/> is the only approved value. A refused decision carries all five
/// approval facts, so it is recorded from the same decision it was refused by rather than re-derived.
/// </summary>
public enum AuthorityApprovalRefusalReason
{
    None = 0,
    ClosedPostingPeriod = 1,
    SelfApproval = 2,
    MissingOverrideReason = 3,
    InsufficientApprovers = 4,
    InvalidApprovalThreshold = 5,
    MissingOverrideApprover = 6,
}

/// <summary>
/// A stored SoD pass, an override together with its reason and approver, or an unoverridden conflict with
/// the reason the approval was refused. Null on <see cref="AuthoritySnapshot"/> when the writing path
/// carries no separation-of-duty decision.
/// </summary>
/// <remarks>
/// <b>Read approval off <see cref="RefusalReason"/>, never off <see cref="Result"/>.</b> An act refused for
/// a reason that is not a duty conflict — a closed posting period, too few approvers — really did have a
/// separation-of-duty <see cref="SeparationOfDutyResult.Pass"/>, so <c>Result == Pass</c> means "no duty
/// conflict", not "the act was approved". <c>RefusalReason == None</c> is what means approved.
/// </remarks>
public sealed record SeparationOfDutySnapshot(
    SeparationOfDutyResult Result,
    string? OverrideReason,
    string? OverrideApprover,
    AuthorityApprovalRefusalReason RefusalReason = AuthorityApprovalRefusalReason.None);

/// <summary>
/// One of the four ordered public resolution steps projected from the decision's evidence (ticket 212).
/// Mirrors <c>Harborline.Api.Foundation.Authorization.AuthorizationTraceStep</c> field for field.
/// </summary>
public sealed record AuthorityTraceStepSnapshot(
    int Ordinal,
    string Stage,
    IReadOnlyList<string> Facts);

/// <summary>
/// The stored counterfactual for the decision this entry records. Mirrors
/// <c>Harborline.Api.Foundation.Authorization.AuthorizationCounterfactual</c> field for field, minus the
/// applying behaviour: audit stores what was derived, it never applies anything.
/// </summary>
public sealed record AuthorityCounterfactualSnapshot(
    int Version,
    string Kind,
    string Direction,
    string Binding,
    int BindingOrdinal,
    string Description);

/// <summary>A copied gate-resolution step retained without any audit-time re-evaluation.</summary>
public sealed record AuthorityResolutionStepSnapshot(
    string Stage,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs);
