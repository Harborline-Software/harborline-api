using System.Reflection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Ticket 212 slice 2 (ledger L605/L649/L693) — the minimal verdict-changing counterfactual, derived from
/// slice 1's evidence with no second evaluation, limited to binding narrowing, grant revocation and
/// validity lapse. Every case asserts that exactly one change is named and that applying it TO THE EVIDENCE
/// flips step four.
/// </summary>
public sealed class DecisionCounterfactualTests
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-212b");
    private static readonly ActorId Principal = new("principal-212b");
    private static readonly ActorId Other = new("principal-212b-other");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
    private static readonly RoleReference Member = new(RoleVocabularies.Domain, "member");

    /// <summary>An allow whose deciding grant reaches wider than the act: narrowing it would refuse.</summary>
    [Fact]
    public async Task Allow_WhoseDecidingGrantIsWiderThanTheAct_NamesBindingNarrowing()
    {
        var evidence = await EvidenceAsync([Derivation("records:write@/", grantId: "grant-wide")]);

        var counterfactual = AssertFlipsStepFour(evidence);

        Assert.Equal(AuthorizationCounterfactualKind.BindingNarrowing, counterfactual.Kind);
        Assert.Equal(AuthorizationCounterfactual.TowardRefusal, counterfactual.Direction);
        Assert.Equal("grant-wide@7", counterfactual.Binding);
    }

    /// <summary>An allow at the exact act scope with a bounded window: letting it lapse would refuse.</summary>
    [Fact]
    public async Task Allow_AtTheExactScopeWithABoundedWindow_NamesValidityLapse()
    {
        var evidence = await EvidenceAsync(
            [Derivation("records:write@/records/a", grantId: "grant-bounded", validUntil: At.AddHours(1))]);

        var counterfactual = AssertFlipsStepFour(evidence);

        Assert.Equal(AuthorizationCounterfactualKind.ValidityLapse, counterfactual.Kind);
        Assert.Equal("grant-bounded@7", counterfactual.Binding);
    }

    /// <summary>An allow at the exact act scope with an open window: only revoking the grant would refuse.</summary>
    [Fact]
    public async Task Allow_AtTheExactScopeWithAnOpenWindow_NamesGrantRevocation()
    {
        var evidence = await EvidenceAsync(
            [Derivation("records:write@/records/a", grantId: "grant-open", validUntil: null)]);

        var counterfactual = AssertFlipsStepFour(evidence);

        Assert.Equal(AuthorizationCounterfactualKind.GrantRevocation, counterfactual.Kind);
        Assert.Equal("grant-open@7", counterfactual.Binding);
    }

    /// <summary>Two grants independently cover the act, so no SINGLE change of the three refuses it.</summary>
    [Fact]
    public async Task Allow_CoveredByTwoIndependentGrants_NamesNoChange()
    {
        var evidence = await EvidenceAsync(
        [
            Derivation("records:write@/", grantId: "grant-one"),
            Derivation("records:write@/records/a", grantId: "grant-two", validUntil: null),
        ]);

        var counterfactual = AuthorizationCounterfactual.From(evidence);

        Assert.Equal(AuthorizationCounterfactualKind.None, counterfactual.Kind);
        Assert.Equal(AuthorizationCounterfactual.NoDirection, counterfactual.Direction);
        Assert.Contains("2 bindings", counterfactual.Description, StringComparison.Ordinal);
        Assert.Same(evidence, counterfactual.ApplyTo(evidence));
    }

    /// <summary>
    /// The lapse case the gate can only see as an absence: the closure snapshot carries the excluded
    /// binding and its reason, and that is the only reason the counterfactual can name the lapse.
    /// </summary>
    [Fact]
    public async Task Refusal_WhoseBindingLapsed_ReadsTheExcludedBindingFromTheClosureSnapshot()
    {
        var lapsed = Derivation("records:write@/records/a", grantId: "grant-lapsed", validUntil: At.AddHours(-1));
        var decision = await Gate([], [new AuthorizationExcludedBinding(
            lapsed, AuthorizationExclusionReason.ValidityLapsed)]).DecideAsync(Request());

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
        // The gate decided from an absence; the excluded binding is what the reader recorded beside it.
        Assert.Empty(decision.Evidence.Bindings);
        var carried = Assert.Single(decision.Evidence.Excluded);
        Assert.Equal(AuthorizationExclusionReason.ValidityLapsed, carried.Reason);
        Assert.Equal("grant-lapsed", carried.Binding.GrantId);

        var counterfactual = AssertFlipsStepFour(decision.Evidence);
        Assert.Equal(AuthorizationCounterfactualKind.ValidityLapse, counterfactual.Kind);
        Assert.Equal(AuthorizationCounterfactual.TowardAllow, counterfactual.Direction);
        Assert.Equal("grant-lapsed@7", counterfactual.Binding);
    }

    [Fact]
    public async Task Refusal_WhoseGrantWasRevoked_NamesGrantRevocation()
    {
        var revoked = Derivation("records:write@/records/a", grantId: "grant-revoked");
        var decision = await Gate([], [new AuthorizationExcludedBinding(
            revoked, AuthorizationExclusionReason.GrantRevoked)]).DecideAsync(Request());

        var counterfactual = AssertFlipsStepFour(decision.Evidence);

        Assert.Equal(AuthorizationCounterfactualKind.GrantRevocation, counterfactual.Kind);
        Assert.Equal(AuthorizationCounterfactual.TowardAllow, counterfactual.Direction);
        Assert.Equal("grant-revoked@7", counterfactual.Binding);
    }

    [Fact]
    public async Task Refusal_WhoseBindingIsNarrowedAwayFromTheAct_NamesBindingNarrowing()
    {
        var evidence = await EvidenceAsync([Derivation("records:write@/records/b", grantId: "grant-narrow")]);

        var counterfactual = AssertFlipsStepFour(evidence);

        Assert.Equal(AuthorizationCounterfactualKind.BindingNarrowing, counterfactual.Kind);
        Assert.Equal(AuthorizationCounterfactual.TowardAllow, counterfactual.Direction);
        Assert.Equal("grant-narrow@7", counterfactual.Binding);
    }

    /// <summary>Nothing on record: no narrowing, revocation or lapse would have allowed this act.</summary>
    [Fact]
    public async Task Refusal_WithNothingOnRecord_NamesNoChange()
    {
        var counterfactual = AuthorizationCounterfactual.From(await EvidenceAsync([]));

        Assert.Equal(AuthorizationCounterfactualKind.None, counterfactual.Kind);
        Assert.Equal(-1, counterfactual.BindingOrdinal);
        Assert.Contains("no binding narrowing", counterfactual.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A separation-of-duty verdict turns on approvers, and the ticket's three changes do not reach it. The
    /// counterfactual says so rather than proposing a different person (ledger L693).
    /// </summary>
    [Fact]
    public void SeparationOfDutyVerdict_NamesNoChange()
    {
        var decision = new SeparationOfDutyEngine().Decide(new SeparationOfDutyRequest(
            PermissionAtom.Parse("records:write@/records/a"),
            Principal,
            Tenant,
            [new ApproverOnRecord(Principal)],
            new ApprovalThreshold(
                new ApprovalPolicyFact("policy-212b", "v1"),
                500m,
                1,
                new ApprovalLimitSourceFact(ApprovalLimitSourceKind.Grant, "limit-grant", "v2")),
            SeparationOfDutyEngine.OpenPostingPeriod,
            At));

        var counterfactual = AuthorizationCounterfactual.From(decision.Evidence);

        Assert.Equal(AuthorizationCounterfactualKind.None, counterfactual.Kind);
        Assert.Contains("separation-of-duty", counterfactual.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// A standing-backed allow rests on no binding, so the restatement guard refuses to invent a change
    /// rather than naming a binding the verdict did not turn on.
    /// </summary>
    [Fact]
    public async Task StandingBackedAllow_NamesNoChange()
    {
        var gate = new AuthorizationGate(
            new StaticReader([], []),
            new StaticStandingResolver([new RecordStanding(Member, "reviewer-of-record", "evidence-7")]),
            new StaticDefinitionReader(new Dictionary<RoleReference, IReadOnlyList<PermissionAtom>>
            {
                [Member] = [PermissionAtom.Parse("records:write@/records/a")],
            }));
        var decision = await gate.DecideAsync(Request());

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        var counterfactual = AuthorizationCounterfactual.From(decision.Evidence);
        Assert.Equal(AuthorizationCounterfactualKind.None, counterfactual.Kind);
        // The restatement guard, not an empty binding list, is what refuses to answer here.
        Assert.Equal("the verdict does not rest on an effective binding", counterfactual.Description);
    }

    /// <summary>
    /// Ticket 212 slice 3 — the applied evidence's own steps must agree with its own binding set. Step two's
    /// deciding binding and step four's refusal shape used to be literals, so a flip toward allow projected
    /// `deciding:none` beside `verdict:allowed`, and a flip toward refusal always said `NoEffectiveRole`
    /// even when the binding it moved was still on record.
    /// </summary>
    [Fact]
    public async Task AppliedEvidence_ProjectsAStepTwoAndStepFourThatAgreeWithEachOther()
    {
        // Toward refusal: the narrowed binding leaves, another binding stays, so the shape is not "no
        // effective role" -- the principal holds the operation, just not at a scope covering the act.
        var allow = await EvidenceAsync(
        [
            Derivation("records:write@/", grantId: "grant-wide"),
            Derivation("records:read@/records/a", grantId: "grant-read"),
        ]);
        var refused = AuthorizationCounterfactual.From(allow).ApplyTo(allow);

        Assert.False(refused.Allowed);
        Assert.Contains("refusal:OperationNotHeld", refused.Project()[3].Facts);
        Assert.Contains("deciding:none", refused.Project()[1].Facts);

        // Toward allow: the restored binding is the deciding one, and step two must name it.
        var lapsed = Derivation("records:write@/records/a", grantId: "grant-lapsed", validUntil: At.AddHours(-1));
        var refusal = (await Gate([], [new AuthorizationExcludedBinding(
            lapsed, AuthorizationExclusionReason.ValidityLapsed)]).DecideAsync(Request())).Evidence;
        var allowed = AuthorizationCounterfactual.From(refusal).ApplyTo(refusal);

        Assert.True(allowed.Allowed);
        Assert.Contains("deciding:grant:grant-lapsed@7", allowed.Project()[1].Facts);
        Assert.Contains("refusal:None", allowed.Project()[3].Facts);
    }

    /// <summary>
    /// Ticket 212 slice 3 — minimality is over everything the gate read. A covering binding beside a
    /// standing that also covers the act is not the only thing holding the allow up, so no single binding
    /// change may be claimed to refuse it.
    /// </summary>
    [Fact]
    public async Task AllowBackedByBothABindingAndAStanding_NamesNoChange()
    {
        var derivation = Derivation("records:write@/", grantId: "grant-wide");
        var gate = new AuthorizationGate(
            new StaticReader([derivation], []),
            new StaticStandingResolver([new RecordStanding(Member, "reviewer-of-record", "evidence-7")]),
            new StaticDefinitionReader(new Dictionary<RoleReference, IReadOnlyList<PermissionAtom>>
            {
                [Member] = [PermissionAtom.Parse("records:write@/records/a")],
            }));
        var decision = await gate.DecideAsync(Request());

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        var counterfactual = AuthorizationCounterfactual.From(decision.Evidence);
        Assert.Equal(AuthorizationCounterfactualKind.None, counterfactual.Kind);
        Assert.Contains("computed standing", counterfactual.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ledger L693 — the counterfactual's shape cannot express a person substitution: no member of the kind
    /// enum and no property of the record names a principal, actor, person or approver.
    /// </summary>
    [Fact]
    public void CounterfactualShape_CannotNameAPerson()
    {
        string[] forbidden = ["principal", "actor", "person", "user", "subject", "approver"];
        var names = typeof(AuthorizationCounterfactual)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(item => item.Name)
            .Concat(Enum.GetNames<AuthorizationCounterfactualKind>())
            .Select(name => name.ToLowerInvariant())
            .ToArray();

        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, name => forbidden.Any(word => name.Contains(word, StringComparison.Ordinal)));
        Assert.Equal(
            ["None", "BindingNarrowing", "GrantRevocation", "ValidityLapse"],
            Enum.GetNames<AuthorizationCounterfactualKind>());
    }

    /// <summary>The counterfactual is a pure function of the evidence: same evidence, same answer.</summary>
    [Fact]
    public async Task Counterfactual_IsPureAndVersioned()
    {
        var evidence = await EvidenceAsync([Derivation("records:write@/")]);

        Assert.Equal(AuthorizationCounterfactual.From(evidence), AuthorizationCounterfactual.From(evidence));
        // The literal, not the constant: bumping the shape's version must break this test, not follow it.
        Assert.Equal(1, AuthorizationCounterfactual.From(evidence).Version);
        Assert.Equal(1, AuthorizationCounterfactual.CurrentVersion);
    }

    /// <summary>
    /// Names exactly one change, and applying that change to the EVIDENCE — not to the system — flips step
    /// four while leaving the first three steps' stage order intact.
    /// </summary>
    private static AuthorizationCounterfactual AssertFlipsStepFour(AuthorizationDecisionEvidence evidence)
    {
        var counterfactual = AuthorizationCounterfactual.From(evidence);
        Assert.NotEqual(AuthorizationCounterfactualKind.None, counterfactual.Kind);
        Assert.NotEqual(AuthorizationCounterfactual.NoDirection, counterfactual.Direction);
        Assert.True(counterfactual.BindingOrdinal >= 0);

        var changed = counterfactual.ApplyTo(evidence);
        var steps = evidence.Project();
        var changedSteps = changed.Project();
        Assert.Equal(steps.Select(step => step.Stage), changedSteps.Select(step => step.Stage));
        Assert.NotEqual(evidence.Allowed, changed.Allowed);
        Assert.Contains($"verdict:{(evidence.Allowed ? "allowed" : "denied")}", steps[3].Facts);
        Assert.Contains($"verdict:{(changed.Allowed ? "allowed" : "denied")}", changedSteps[3].Facts);
        Assert.NotEqual(steps[3].Facts, changedSteps[3].Facts);

        // Exactly ONE change: one entry of the on-record set left it, and its changed form is the only thing
        // that entered. Narrowing a binding below the act scope has no changed form -- ticket 212 slice 3
        // stopped it materialising a scope this install does not have -- so for that one shape nothing
        // enters, and the assertion says so by name rather than by loosening.
        var before = Entries(evidence);
        var after = Entries(changed);
        var entered = counterfactual.Kind is AuthorizationCounterfactualKind.BindingNarrowing
            && counterfactual.Direction == AuthorizationCounterfactual.TowardRefusal ? 0 : 1;
        Assert.Equal(entered, after.Except(before, StringComparer.Ordinal).Count());
        Assert.Single(before.Except(after, StringComparer.Ordinal));
        return counterfactual;
    }

    /// <summary>Every binding on record, as a value key saying whether it counted and, if not, why.</summary>
    private static string[] Entries(AuthorizationDecisionEvidence evidence) =>
    [
        .. evidence.Bindings.Select(item => $"in:{Key(item)}"),
        .. evidence.Excluded.Select(item => $"out:{item.Reason}:{Key(item.Binding)}"),
    ];

    private static string Key(AuthorizationAtomDerivation binding) =>
        $"{binding.GrantId}@{binding.GrantOwnerVersion};{binding.Atom};{binding.GrantScope};{binding.ValidUntil:O}";

    private static async Task<AuthorizationDecisionEvidence> EvidenceAsync(
        IReadOnlyList<AuthorizationAtomDerivation> derivations) =>
        (await Gate(derivations, []).DecideAsync(Request())).Evidence;

    private static AuthorizationGate Gate(
        IReadOnlyList<AuthorizationAtomDerivation> derivations,
        IReadOnlyList<AuthorizationExcludedBinding> excluded) => new(
        new StaticReader(derivations, excluded),
        new StaticStandingResolver([]),
        new StaticDefinitionReader(derivations
            .GroupBy(item => item.Role)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<PermissionAtom>)group.Select(item => item.Atom).Distinct().ToArray())));

    private static AuthorizationGateRequest Request() =>
        new(PermissionAtom.Parse("records:write@/records/a"), Principal, Tenant,
            new AuthorizationTarget("record", "a", ScopeExpression.Parse("/records/a")), At);

    private static AuthorizationAtomDerivation Derivation(
        string atom,
        string grantId = "grant",
        string grantScope = "/",
        DateTimeOffset? validUntil = null) =>
        new(PermissionAtom.Parse(atom), Member, grantId, 7, "definition",
            ScopeExpression.Parse(grantScope), At.AddHours(-1), validUntil);

    private sealed class StaticReader(
        IReadOnlyList<AuthorizationAtomDerivation> derivations,
        IReadOnlyList<AuthorizationExcludedBinding> excluded) : IAuthorizationClosureSnapshotReader
    {
        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(
            AuthorizationGateRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(new AuthorizationClosureSnapshot(derivations, excluded));
    }

    private sealed class StaticStandingResolver(IReadOnlyList<RecordStanding> standings)
        : IRecordStandingResolver
    {
        public ValueTask<IReadOnlyList<RecordStanding>> ResolveAsync(
            AuthorizationGateRequest request,
            IReadOnlySet<RoleReference> effectiveRecordRoles,
            CancellationToken ct = default) => ValueTask.FromResult(standings);
    }

    private sealed class StaticDefinitionReader(
        IReadOnlyDictionary<RoleReference, IReadOnlyList<PermissionAtom>> atoms)
        : IAuthorizationDefinitionAtomReader
    {
        public ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(
            TenantId tenantId, RoleReference role, CancellationToken ct = default) =>
            ValueTask.FromResult(atoms.GetValueOrDefault(role, []));
    }
}
