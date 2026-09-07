using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Ticket 272 slice 1 — the ADR 0067 clause 2 engine. One decision object carries the five approval facts an
/// audit entry records, a refusal is that same decision with a reason, and the whole thing is a pure function
/// of its request.
/// </summary>
public sealed class SeparationOfDutyEngineTests
{
    private static readonly ActorId Principal = new("party:preparer");
    private static readonly ActorId Approver = new("party:approver");
    private static readonly ActorId SecondApprover = new("party:second-approver");
    private static readonly DateTimeOffset Instant = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly ApprovalThreshold Threshold = new(
        new ApprovalPolicyFact("invoice-issue-approval", "3"),
        Limit: 5000m,
        RequiredApprovers: 1,
        new ApprovalLimitSourceFact(ApprovalLimitSourceKind.SigningLimitAgreement, "sla:finance-1", "2"));

    private static SeparationOfDutyRequest Request(
        IReadOnlyList<ApproverOnRecord> approvers,
        string? postingPeriodState = SeparationOfDutyEngine.OpenPostingPeriod,
        ApprovalThreshold? threshold = null) => new(
        PermissionAtom.Parse("invoice:issue@/"),
        Principal,
        TenantId.FromString("tenant-a"),
        approvers,
        threshold ?? Threshold,
        postingPeriodState,
        Instant);

    private static SeparationOfDutyDecision Decide(
        IReadOnlyList<ApproverOnRecord> approvers,
        string? postingPeriodState = SeparationOfDutyEngine.OpenPostingPeriod,
        ApprovalThreshold? threshold = null) =>
        new SeparationOfDutyEngine().Decide(Request(approvers, postingPeriodState, threshold));

    // ---- the five approval facts, one test each -------------------------------------------------------

    [Fact(DisplayName = "Fact 1: the decision carries the threshold's policy id and version")]
    public void Decision_CarriesThePolicyFact()
    {
        var decision = Decide([new ApproverOnRecord(Approver)]);

        Assert.Equal(new ApprovalPolicyFact("invoice-issue-approval", "3"), decision.Policy);
    }

    [Fact(DisplayName = "Fact 2: the decision carries the applied limit as a scalar")]
    public void Decision_CarriesTheAppliedLimit()
    {
        var decision = Decide([new ApproverOnRecord(Approver)]);

        Assert.Equal(5000m, decision.AppliedLimit);
    }

    [Fact(DisplayName = "Fact 3: the decision carries the source the limit came from")]
    public void Decision_CarriesTheLimitSource()
    {
        var decision = Decide([new ApproverOnRecord(Approver)]);

        Assert.Equal(
            new ApprovalLimitSourceFact(ApprovalLimitSourceKind.SigningLimitAgreement, "sla:finance-1", "2"),
            decision.LimitSource);
    }

    [Fact(DisplayName = "Fact 4: a clean approver set is a separation-of-duty pass")]
    public void Decision_CarriesTheSeparationOfDutyFact()
    {
        var decision = Decide([new ApproverOnRecord(Approver)]);

        Assert.Equal(new SeparationOfDutyFact(SeparationOfDutyOutcome.Pass, null, null), decision.SeparationOfDuty);
    }

    [Fact(DisplayName = "Fact 5: the decision carries the posting period state at the decided instant")]
    public void Decision_CarriesThePostingPeriodState()
    {
        var decision = Decide([new ApproverOnRecord(Approver)]);

        Assert.Equal(SeparationOfDutyEngine.OpenPostingPeriod, decision.PostingPeriodState);
        Assert.True(decision.Approved);
    }

    [Fact(DisplayName = "A conflict CAN be overridden by a named overrider who is not the requester")]
    public void OverrideWithAReason_IsRecordedOnTheFact()
    {
        // ADR 0067 clause 2's block is SOFT: the conflict is resolved by denying OR by allowing with a
        // mandatory recorded reason. This is the allowing half — the requester is among its own approvers,
        // and a second, named party overrides it on the record.
        var decision = Decide(
        [
            new ApproverOnRecord(Principal),
            new ApproverOnRecord(
                Approver, "CFO accepted the residual risk, ticket FIN-88", ResolvedApprover: Approver),
        ]);

        Assert.Equal(
            new SeparationOfDutyFact(
                SeparationOfDutyOutcome.Override,
                "CFO accepted the residual risk, ticket FIN-88",
                "party:approver"),
            decision.SeparationOfDuty);
        Assert.True(decision.Approved);
    }

    [Fact(DisplayName = "A reason with nothing to override is a pass, not a decorative Override")]
    public void ReasonWithoutAConflict_IsAPass()
    {
        var decision = Decide(
            [new ApproverOnRecord(Approver, "documented, but nothing conflicted", ResolvedApprover: Approver)]);

        Assert.Equal(SeparationOfDutyOutcome.Pass, decision.SeparationOfDuty.Outcome);
        Assert.True(decision.Approved);
    }

    // ---- the adversarial table ------------------------------------------------------------------------

    [Fact(DisplayName = "274: a whitespace-padded copy of the requester cannot exist, so it cannot clear the conflict")]
    public void Adversarial_PaddedRequesterCannotEvadeTheConflict()
    {
        // Before 274 this approver was a DIFFERENT ActorId to the requester -- the padded spelling cleared
        // its own separation-of-duty conflict. The canonical form refuses the padded copy outright.
        Assert.Throws<ArgumentException>(() => new ActorId(" party:preparer"));
        Assert.Throws<ArgumentException>(() => new ActorId("party:preparer	"));

        // And an OS-user party id spelled with different case is the SAME requester, so it still conflicts.
        var osPrincipal = new ActorId("os:preparer#deadbeef");
        var decision = new SeparationOfDutyEngine().Decide(new SeparationOfDutyRequest(
            PermissionAtom.Parse("invoice:issue@/"),
            osPrincipal,
            TenantId.FromString("tenant-a"),
            [new ApproverOnRecord(new ActorId("os:Preparer#deadbeef"))],
            Threshold,
            SeparationOfDutyEngine.OpenPostingPeriod,
            Instant));

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.SelfApproval, decision.RefusalReason);
        Assert.Equal(SeparationOfDutyOutcome.Conflict, decision.SeparationOfDuty.Outcome);
    }

    [Fact(DisplayName = "Adversarial 1: the requester approving its own act is refused")]
    public void Adversarial_RequesterApprovesItsOwnAct()
    {
        var decision = Decide([new ApproverOnRecord(Approver), new ApproverOnRecord(Principal)]);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.SelfApproval, decision.RefusalReason);
        Assert.Equal(SeparationOfDutyOutcome.Conflict, decision.SeparationOfDuty.Outcome);
    }

    [Fact(DisplayName = "Adversarial 1b: the requester cannot override its own conflict")]
    public void Adversarial_RequesterOverridesItself()
    {
        // The one overrider on record IS the requester, so clause 2's named-overrider condition fails and
        // the conflict stands. Without the not-the-requester test this self-clears.
        var decision = Decide(
            [new ApproverOnRecord(Principal, "I accept the risk on my own act", ResolvedApprover: Principal)]);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.SelfApproval, decision.RefusalReason);
        Assert.Equal(SeparationOfDutyOutcome.Conflict, decision.SeparationOfDuty.Outcome);
    }

    [Fact(DisplayName = "Adversarial 2: the same person twice under two roles is one approver")]
    public void Adversarial_SamePersonTwiceUnderTwoRoles()
    {
        var twoRequired = Threshold with { RequiredApprovers = 2 };

        var decision = Decide(
            [new ApproverOnRecord(Approver), new ApproverOnRecord(Approver)], threshold: twoRequired);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.InsufficientApprovers, decision.RefusalReason);
        Assert.Single(decision.Approvers);
    }

    [Fact(DisplayName = "Adversarial 3: an override with no named overrider is refused, not recorded")]
    public void Adversarial_OverrideWithoutARecordedOverrider()
    {
        var decision = Decide([new ApproverOnRecord(default, "cfo said so")]);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.MissingOverrideApprover, decision.RefusalReason);
        // And nothing was recorded as an override: an unnamed overrider is not an audit record.
        Assert.NotEqual(SeparationOfDutyOutcome.Override, decision.SeparationOfDuty.Outcome);
    }

    [Fact(DisplayName = "Adversarial 4: a threshold of zero approvers is refused, never a pass")]
    public void Adversarial_ThresholdOfZero()
    {
        var noneRequired = Threshold with { RequiredApprovers = 0 };

        var decision = Decide([], threshold: noneRequired);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.InvalidApprovalThreshold, decision.RefusalReason);
        Assert.Empty(decision.Approvers);
        // The configuration defect is a decision, not a throw: the other four facts survive it.
        Assert.Equal("invoice-issue-approval", decision.Policy.PolicyId);
        Assert.Equal(5000m, decision.AppliedLimit);
        Assert.Equal("sla:finance-1", decision.LimitSource.SourceId);
        Assert.Equal(SeparationOfDutyEngine.OpenPostingPeriod, decision.PostingPeriodState);
    }

    // ---- each refusal, one test each ------------------------------------------------------------------

    [Fact(DisplayName = "Refusal 1: a closed posting period refuses, on the same decision")]
    public void ClosedPostingPeriod_Refuses()
    {
        var decision = Decide([new ApproverOnRecord(Approver)], postingPeriodState: "Closed");

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.ClosedPostingPeriod, decision.RefusalReason);
        // The refusal is recorded from the SAME decision: all five facts are present on it.
        Assert.Equal("invoice-issue-approval", decision.Policy.PolicyId);
        Assert.Equal(5000m, decision.AppliedLimit);
        Assert.Equal("sla:finance-1", decision.LimitSource.SourceId);
        Assert.Equal(SeparationOfDutyOutcome.Pass, decision.SeparationOfDuty.Outcome);
        Assert.Equal("Closed", decision.PostingPeriodState);
    }

    [Fact(DisplayName = "Refusal 2: the principal among its own approvers is a conflict and refuses")]
    public void SelfApproval_Refuses()
    {
        var decision = Decide([new ApproverOnRecord(Approver), new ApproverOnRecord(Principal)]);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.SelfApproval, decision.RefusalReason);
        Assert.Equal(
            new SeparationOfDutyFact(SeparationOfDutyOutcome.Conflict, null, null),
            decision.SeparationOfDuty);
    }

    [Fact(DisplayName = "Refusal 3: an override with a blank reason refuses (the reason is mandatory)")]
    public void OverrideWithoutAReason_Refuses()
    {
        var decision = Decide([new ApproverOnRecord(Approver, "   ")]);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.MissingOverrideReason, decision.RefusalReason);
    }

    [Fact(DisplayName = "Refusal 4: fewer distinct approvers than the threshold requires refuses")]
    public void TooFewApprovers_Refuses()
    {
        var twoRequired = Threshold with { RequiredApprovers = 2 };

        // The same approver twice is ONE approver: distinctness is what the threshold counts.
        var duplicated = Decide(
            [new ApproverOnRecord(Approver), new ApproverOnRecord(Approver)], threshold: twoRequired);
        var satisfied = Decide(
            [new ApproverOnRecord(Approver), new ApproverOnRecord(SecondApprover)], threshold: twoRequired);

        Assert.Equal(ApprovalRefusalReason.InsufficientApprovers, duplicated.RefusalReason);
        Assert.Equal(ApprovalRefusalReason.None, satisfied.RefusalReason);
        Assert.Empty(Decide([], threshold: twoRequired).Approvers);
    }

    // ---- determinism ----------------------------------------------------------------------------------

    [Fact(DisplayName = "The engine is deterministic: the same request yields an identical decision")]
    public void Decide_IsDeterministic()
    {
        var engine = new SeparationOfDutyEngine();
        var request = Request(
            [new ApproverOnRecord(Approver, "documented override", ResolvedApprover: Approver)]);

        var first = engine.Decide(request);
        var second = new SeparationOfDutyEngine().Decide(request);

        Assert.Equal(first.RefusalReason, second.RefusalReason);
        Assert.Equal(first.Policy, second.Policy);
        Assert.Equal(first.AppliedLimit, second.AppliedLimit);
        Assert.Equal(first.LimitSource, second.LimitSource);
        Assert.Equal(first.SeparationOfDuty, second.SeparationOfDuty);
        Assert.Equal(first.PostingPeriodState, second.PostingPeriodState);
        Assert.Equal(first.Approvers, second.Approvers);
        // The decided instant is the request instant, never a clock read.
        Assert.Equal(Instant, first.DecidedAt);
        Assert.Equal(Instant, second.DecidedAt);
    }

    [Fact(DisplayName = "The shipping composition resolves ONE engine behind the host provider seam")]
    public void ShippingComposition_RegistersTheEngineAsASingleton()
    {
        var nodeSigner = new NodePrincipalSigner(Enumerable.Range(1, 32).Select(index => (byte)index).ToArray());
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(nodeSigner)
            .AddSingleton<IOperationSigner>(nodeSigner.Signer)
            .AddEnrollmentCompensatingControlAudit()
            .BuildServiceProvider();

        var engine = provider.GetRequiredService<SeparationOfDutyEngine>();

        Assert.Same(engine, provider.GetRequiredService<SeparationOfDutyEngine>());
    }

    [Fact(DisplayName = "Refusal order is fixed: a closed period outranks a self-approval conflict")]
    public void RefusalOrder_IsStable()
    {
        var decision = Decide(
            [new ApproverOnRecord(Principal)], postingPeriodState: "Closed");

        Assert.Equal(ApprovalRefusalReason.ClosedPostingPeriod, decision.RefusalReason);
        // The conflict is still recorded on the fact, so the refusal loses nothing.
        Assert.Equal(SeparationOfDutyOutcome.Conflict, decision.SeparationOfDuty.Outcome);

        // And the configuration defect outranks the closed period: a threshold below one approver means the
        // request could not be evaluated at all.
        var defective = Decide(
            [new ApproverOnRecord(Principal)],
            postingPeriodState: "Closed",
            threshold: Threshold with { RequiredApprovers = 0 });

        Assert.Equal(ApprovalRefusalReason.InvalidApprovalThreshold, defective.RefusalReason);
    }

    // ---- ticket 272 slice 4: the overriding party is a RESOLVED party -----------------------------------

    [Fact(DisplayName = "272 s4: an override whose party the caller resolved to NOBODY is refused, and names none")]
    public void OverrideResolvedToNobody_IsRefused()
    {
        // The caller claimed a plausible actor id but resolved it to no party (the roster/permission check
        // said no). The claim is not a name: the conflict stands and the record carries no party.
        var decision = Decide(
        [
            new ApproverOnRecord(Principal),
            new ApproverOnRecord(Approver, "night close", ResolvedApprover: null),
        ]);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.MissingOverrideApprover, decision.RefusalReason);
        Assert.Equal(SeparationOfDutyOutcome.Conflict, decision.SeparationOfDuty.Outcome);
        Assert.Null(decision.SeparationOfDuty.OverrideApprover);
    }

    [Fact(DisplayName = "272 s4: the recorded overriding party is the RESOLVED one, never the caller's claim")]
    public void RecordedOverrideParty_IsTheResolvedOne()
    {
        var decision = Decide(
        [
            new ApproverOnRecord(Principal),
            new ApproverOnRecord(
                new ActorId("party:whatever-the-caller-typed"), "night close", ResolvedApprover: Approver),
        ]);

        Assert.True(decision.Approved);
        Assert.Equal(
            new SeparationOfDutyFact(SeparationOfDutyOutcome.Override, "night close", Approver.Value),
            decision.SeparationOfDuty);
    }

    [Fact(DisplayName = "272 s4: a claim that RESOLVES to the requester is still a self-approval")]
    public void OverrideResolvingToTheRequester_IsSelfApproval()
    {
        // The not-the-requester test is applied to the resolved party, so a different-looking claim that
        // resolves back to the requester cannot clear its own conflict.
        var decision = Decide(
        [
            new ApproverOnRecord(Principal),
            new ApproverOnRecord(Approver, "I accept it", ResolvedApprover: Principal),
        ]);

        Assert.False(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.SelfApproval, decision.RefusalReason);
        Assert.Equal(SeparationOfDutyOutcome.Conflict, decision.SeparationOfDuty.Outcome);
    }

    [Fact(DisplayName = "272 s4: an UNRESOLVED posting period refuses nothing and is recorded as the null it is")]
    public void UnresolvedPostingPeriod_RefusesNothing_AndIsRecordedNull()
    {
        var decision = Decide([new ApproverOnRecord(Approver)], postingPeriodState: null);

        Assert.True(decision.Approved);
        Assert.Equal(ApprovalRefusalReason.None, decision.RefusalReason);
        Assert.Null(decision.PostingPeriodState);
    }
}
