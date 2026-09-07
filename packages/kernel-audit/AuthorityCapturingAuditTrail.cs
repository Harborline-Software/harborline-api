using System.Runtime.CompilerServices;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;

namespace Harborline.Api.Kernel.Audit;

/// <summary>
/// Write-side decorator that replaces any caller-provided authority with the kernel-captured value before
/// persistence. Its read path delegates to the stored-record reader and never reaches the authority source.
/// </summary>
internal sealed class AuthorityCapturingAuditTrail : IAuthorizedAuditTrail
{
    private readonly EventLogBackedAuditTrail _inner;

    internal AuthorityCapturingAuditTrail(EventLogBackedAuditTrail inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _inner.AppendAsync(record with { AuthoritySnapshot = null }, ct)
            .ConfigureAwait(false);
    }

    public ValueTask AppendAuthorizedAsync(
        AuditRecord record,
        AuthorizationDecision decision,
        CancellationToken ct = default,
        SeparationOfDutyDecision? approval = null)
    {
        var authorized = AuthorizedAuditRecord.CopyFromDecision(record, decision, approval);
        return _inner.AppendAsync(authorized, ct);
    }

    public async IAsyncEnumerable<AuditRecord> QueryAsync(
        AuditQuery query,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var record in _inner.QueryAsync(query, ct).ConfigureAwait(false))
        {
            yield return record;
        }
    }
}

internal static class AuthorizedAuditRecord
{
    /// <summary>
    /// The one seam at which a decision becomes a stored authority snapshot. <paramref name="approval"/> is
    /// the ADR 0067 clause 2 separation-of-duty decision when the writing path has one; the five approval
    /// facts are copied from it and from nothing else. A refused approval is copied just as faithfully as an
    /// approved one — its outcome and its refusal reason are both recorded, never flattened to a pass.
    /// </summary>
    internal static AuditRecord CopyFromDecision(
        AuditRecord record,
        AuthorizationDecision decision,
        SeparationOfDutyDecision? approval = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Verdict is not AuthorizationVerdict.Allowed)
            throw new AuthorizedAuditRefusedException(AuthorizedAuditRefusalCodes.DecisionDenied);
        if (record.TenantId != decision.Request.Tenant)
            throw new AuthorizedAuditRefusedException(AuthorizedAuditRefusalCodes.TenantMismatch);
        if (!string.Equals(
                record.Actor?.Value ?? record.Payload.IssuerId.ToBase64Url(),
                decision.Request.Principal.Value,
                StringComparison.Ordinal))
            throw new AuthorizedAuditRefusedException(AuthorizedAuditRefusalCodes.PrincipalMismatch);
        if (record.OccurredAt != decision.Request.At || record.OccurredAt != decision.DecidedAt)
            throw new AuthorizedAuditRefusedException(AuthorizedAuditRefusalCodes.InstantMismatch);
        if (record.Target is not { } target
            || !string.Equals(target.RecordKind, decision.Request.Target.RecordKind, StringComparison.Ordinal)
            || !string.Equals(target.RecordId, decision.Request.Target.RecordId, StringComparison.Ordinal))
            throw new AuthorizedAuditRefusedException(AuthorizedAuditRefusalCodes.TargetMismatch);
        if (record.Act is not { } act || act != decision.Request.Act)
            throw new AuthorizedAuditRefusedException(AuthorizedAuditRefusalCodes.ActMismatch);

        var grants = decision.Derivations
            .Select(item => new AuthorityGrantSnapshot(item.GrantId, item.GrantOwnerVersion))
            .Distinct()
            .ToArray();
        var snapshot = new AuthoritySnapshot(
            grants,
            Policy: approval is null
                ? null
                : new AuthorityPolicySnapshot(approval.Policy.PolicyId, approval.Policy.Version),
            AppliedLimit: approval?.AppliedLimit,
            LimitSource: approval is null
                ? null
                : new AuthorityLimitSourceSnapshot(
                    LimitSourceKind(approval.LimitSource.Kind),
                    approval.LimitSource.SourceId,
                    approval.LimitSource.Version),
            SeparationOfDuty: approval is null
                ? null
                : new SeparationOfDutySnapshot(
                    Outcome(approval.SeparationOfDuty.Outcome),
                    approval.SeparationOfDuty.OverrideReason,
                    approval.SeparationOfDuty.OverrideApprover,
                    RefusalReason(approval.RefusalReason)),
            PostingPeriodState: approval?.PostingPeriodState,
            DerivationIds: decision.Derivations.Select(item => item.DefinitionId).Distinct().ToArray(),
            Principal: decision.Request.Principal.Value,
            Tenant: decision.Request.Tenant.Value,
            Instant: decision.DecidedAt,
            Resolution: decision.Resolution.Select(step => new AuthorityResolutionStepSnapshot(
                step.Stage.ToString(), [.. step.Inputs], [.. step.Outputs])).ToArray(),
            // Ticket 212 slice 1 — the public steps are PROJECTED from the decision's evidence and copied
            // here. Audit stores what the decision decided; it never projects a second reading. When an
            // approval decided too, ITS four steps follow under ordinals 5..8: without them the stored
            // trace of a write the gate allowed and self-approval refused would read verdict:allowed.
            TraceVersion: decision.Evidence.Version,
            Trace: [
                .. Steps(decision.Evidence, 0),
                .. approval is null
                    ? Array.Empty<AuthorityTraceStepSnapshot>()
                    : Steps(approval.Evidence, AuthorizationDecisionEvidence.StepCount),
            ],
            // Ticket 212 slice 3 -- the counterfactual is derived HERE, from the same evidence the steps
            // are projected from, and stored beside them. The read side returns what was stored; it never
            // derives a second one, so the answer cannot drift as grants change after the fact.
            Counterfactual: Counterfactual(AuthorizationCounterfactual.From(decision.Evidence)));

        // Overwrite caller input and sever the persisted snapshot from the decision's source collections.
        return record with { AuthoritySnapshot = snapshot.Copy() };
    }

    private static AuthorityCounterfactualSnapshot Counterfactual(AuthorizationCounterfactual derived) =>
        new(derived.Version, derived.Kind.ToString(), derived.Direction, derived.Binding,
            derived.BindingOrdinal, derived.Description);

    private static AuthorityTraceStepSnapshot[] Steps(AuthorizationDecisionEvidence evidence, int offset) =>
        [.. evidence.Project().Select(step =>
            new AuthorityTraceStepSnapshot(step.Ordinal + offset, step.Stage, [.. step.Facts]))];

    // The three maps below are switch expressions with one arm per member and NO default arm: a new member
    // on any of the three decision enums must be a BUILD failure (CS8509, an error under the repo-wide
    // TreatWarningsAsErrors) rather than a silently wrong audit entry. Only CS8524 is suppressed — that is
    // the separate diagnostic for a value cast in from outside the enum's declared members, which cannot
    // come off an engine-produced decision and which a default arm would swallow along with the real thing.
#pragma warning disable CS8524
    private static SeparationOfDutyResult Outcome(SeparationOfDutyOutcome outcome) => outcome switch
    {
        SeparationOfDutyOutcome.Pass => SeparationOfDutyResult.Pass,
        SeparationOfDutyOutcome.Override => SeparationOfDutyResult.Override,
        SeparationOfDutyOutcome.Conflict => SeparationOfDutyResult.Conflict,
    };

    private static AuthorityApprovalRefusalReason RefusalReason(ApprovalRefusalReason reason) => reason switch
    {
        ApprovalRefusalReason.None => AuthorityApprovalRefusalReason.None,
        ApprovalRefusalReason.ClosedPostingPeriod => AuthorityApprovalRefusalReason.ClosedPostingPeriod,
        ApprovalRefusalReason.SelfApproval => AuthorityApprovalRefusalReason.SelfApproval,
        ApprovalRefusalReason.MissingOverrideReason => AuthorityApprovalRefusalReason.MissingOverrideReason,
        ApprovalRefusalReason.InsufficientApprovers => AuthorityApprovalRefusalReason.InsufficientApprovers,
        ApprovalRefusalReason.InvalidApprovalThreshold =>
            AuthorityApprovalRefusalReason.InvalidApprovalThreshold,
        ApprovalRefusalReason.MissingOverrideApprover =>
            AuthorityApprovalRefusalReason.MissingOverrideApprover,
    };

    private static AuthorityLimitSourceKind LimitSourceKind(ApprovalLimitSourceKind kind) => kind switch
    {
        ApprovalLimitSourceKind.Grant => AuthorityLimitSourceKind.Grant,
        ApprovalLimitSourceKind.SigningLimitAgreement => AuthorityLimitSourceKind.SigningLimitAgreement,
    };
#pragma warning restore CS8524
}
