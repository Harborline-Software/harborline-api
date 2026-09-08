using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.MultiTenancy;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.Audit;

/// <summary>
/// Ticket 272 slice 2 — the decision maps to the stored snapshot at its one seam,
/// <c>Harborline.Api.Kernel.Audit.AuthorizedAuditRecord.CopyFromDecision</c>.
/// <para>
/// The failure this pins is a refusal recorded as an approval. Before this slice the kernel-audit
/// <c>SeparationOfDutyResult</c> had only <c>Pass</c> and <c>Override</c>, so an unoverridden conflict — a
/// refused act — had no faithful spelling at all, and the refusal reason the engine decided had nowhere to
/// go. Every assertion below is on the record the seam returns, so it runs through
/// <c>AuthoritySnapshot.Copy</c> as the persisted value does.
/// </para>
/// </summary>
public sealed class ApprovalFactSnapshotMappingTests
{
    private static readonly TenantId Tenant = new("tenant-272-approval");
    private static readonly DateTimeOffset At = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly ApprovalPolicyFact Policy = new("policy-272", "v3");
    private static readonly ApprovalLimitSourceFact LimitSource =
        new(ApprovalLimitSourceKind.SigningLimitAgreement, "sla-9", "v2");

    [Fact(DisplayName = "A refused conflict is stored as Conflict with the reason it was refused for")]
    public async Task Refused_conflict_maps_to_Conflict_with_its_refusal_reason()
    {
        // The principal is among its own approvers and nothing overrides it: refused, outcome Conflict.
        var (record, decision, principal) = await AuthorizedAsync();
        var approval = Decide(principal, [new ApproverOnRecord(principal)]);
        Assert.False(approval.Approved);

        var stored = AuthorizedAuditRecord.CopyFromDecision(record, decision, approval);

        var sod = stored.AuthoritySnapshot!.SeparationOfDuty!;
        Assert.Equal(SeparationOfDutyResult.Conflict, sod.Result);
        Assert.Equal(AuthorityApprovalRefusalReason.SelfApproval, sod.RefusalReason);
        Assert.Null(sod.OverrideReason);
        Assert.Null(sod.OverrideApprover);
        // The other four approval facts are recorded on a refusal exactly as on an approval.
        Assert.Equal(new AuthorityPolicySnapshot("policy-272", "v3"), stored.AuthoritySnapshot.Policy);
        Assert.Equal(4200m, stored.AuthoritySnapshot.AppliedLimit);
        Assert.Equal(
            new AuthorityLimitSourceSnapshot(AuthorityLimitSourceKind.SigningLimitAgreement, "sla-9", "v2"),
            stored.AuthoritySnapshot.LimitSource);
        Assert.Equal(SeparationOfDutyEngine.OpenPostingPeriod, stored.AuthoritySnapshot.PostingPeriodState);
    }

    [Fact(DisplayName = "An overridden conflict is stored as Override with its reason and its overrider")]
    public async Task Override_maps_to_Override_with_reason_and_approver()
    {
        var (record, decision, principal) = await AuthorizedAsync();
        var approval = Decide(principal,
        [
            new ApproverOnRecord(principal),
            new ApproverOnRecord(
                new ActorId("party:controller"),
                "single-approver night close",
                ResolvedApprover: new ActorId("party:controller")),
        ]);
        Assert.True(approval.Approved);

        var stored = AuthorizedAuditRecord.CopyFromDecision(record, decision, approval);

        var sod = stored.AuthoritySnapshot!.SeparationOfDuty!;
        Assert.Equal(SeparationOfDutyResult.Override, sod.Result);
        Assert.Equal("single-approver night close", sod.OverrideReason);
        Assert.Equal("party:controller", sod.OverrideApprover);
        Assert.Equal(AuthorityApprovalRefusalReason.None, sod.RefusalReason);
    }

    [Fact(DisplayName = "A clean approval is stored as Pass with no refusal reason")]
    public async Task Pass_maps_to_Pass()
    {
        var (record, decision, _) = await AuthorizedAsync();
        var approval = Decide(
            new ActorId("party:preparer"), [new ApproverOnRecord(new ActorId("party:controller"))]);
        Assert.True(approval.Approved);

        var stored = AuthorizedAuditRecord.CopyFromDecision(record, decision, approval);

        var sod = stored.AuthoritySnapshot!.SeparationOfDuty!;
        Assert.Equal(SeparationOfDutyResult.Pass, sod.Result);
        Assert.Equal(AuthorityApprovalRefusalReason.None, sod.RefusalReason);
        Assert.Null(sod.OverrideReason);
        Assert.Null(sod.OverrideApprover);
    }

    [Fact(DisplayName = "A writer that carries no separation-of-duty decision records none of the five facts")]
    public async Task No_approval_decision_leaves_the_five_facts_null()
    {
        // Today's writers (slices 3 to 5 convert them) pass no approval decision. The seam must not invent
        // one: a fact absent from the decision is absent from the entry, never defaulted to a Pass.
        var (record, decision, _) = await AuthorizedAsync();

        var stored = AuthorizedAuditRecord.CopyFromDecision(record, decision);

        Assert.Null(stored.AuthoritySnapshot!.SeparationOfDuty);
        Assert.Null(stored.AuthoritySnapshot.Policy);
        Assert.Null(stored.AuthoritySnapshot.AppliedLimit);
        Assert.Null(stored.AuthoritySnapshot.LimitSource);
        Assert.Null(stored.AuthoritySnapshot.PostingPeriodState);
    }

    [Fact(DisplayName = "Every decided outcome and refusal reason has a stored spelling of the same name and value")]
    public void Every_member_of_both_decision_enums_has_a_faithful_stored_member()
    {
        // The compile-time half of this is the seam's default-arm-free switch expressions: a new member on
        // either decision enum fails the build (CS8509). This is the other half — that the audit enum the
        // switch maps INTO carries the same member under the same name and the same number, so a stored
        // entry read back years later means what the engine decided.
        AssertMirrored<SeparationOfDutyOutcome, SeparationOfDutyResult>();
        AssertMirrored<ApprovalRefusalReason, AuthorityApprovalRefusalReason>();
        AssertMirrored<ApprovalLimitSourceKind, AuthorityLimitSourceKind>();
    }

    private static void AssertMirrored<TDecided, TStored>()
        where TDecided : struct, Enum
        where TStored : struct, Enum
    {
        // Both directions (272 slice 2 review, MINOR-1): iterating only the decided enum leaves a stored
        // member with no decided counterpart invisible, and an unreachable stored spelling is a reader
        // hazard — it looks like a value the engine can produce.
        Assert.Equal(Enum.GetValues<TDecided>().Length, Enum.GetValues<TStored>().Length);

        foreach (var decided in Enum.GetValues<TDecided>())
        {
            var name = decided.ToString();
            Assert.True(
                Enum.TryParse<TStored>(name, ignoreCase: false, out var stored),
                $"{typeof(TStored).FullName} has no member named '{name}'; a decision the engine can make "
                + "would have no faithful stored spelling.");
            Assert.Equal(
                Convert.ToInt32(decided, System.Globalization.CultureInfo.InvariantCulture),
                Convert.ToInt32(stored, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>An engine decision over the given principal and approvers; every input is explicit.</summary>
    private static SeparationOfDutyDecision Decide(ActorId principal, ApproverOnRecord[] approvers) =>
        new SeparationOfDutyEngine().Decide(new SeparationOfDutyRequest(
            new PermissionAtom(
                AuthorizationOperation.Parse("records:write"), ScopeExpression.Parse("/records/audit-record")),
            principal,
            Tenant,
            approvers,
            new ApprovalThreshold(Policy, 4200m, RequiredApprovers: 1, LimitSource),
            SeparationOfDutyEngine.OpenPostingPeriod,
            At));

    /// <summary>The record and carried authorization decision the seam's own guards require.</summary>
    private static async Task<(AuditRecord Record, AuthorizationDecision Decision, ActorId Principal)>
        AuthorizedAsync()
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var (decision, _) = await AuthoritySnapshotTests.DecisionAsync(signer, Tenant, At, ("grant-272", 1));
        var record = await AuthoritySnapshotTests.RecordAsync(signer, Tenant, At);
        return (record, decision, new ActorId(signer.IssuerId.ToBase64Url()));
    }
}
