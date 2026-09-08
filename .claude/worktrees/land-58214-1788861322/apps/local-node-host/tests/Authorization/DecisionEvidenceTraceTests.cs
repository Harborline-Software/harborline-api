using System.Reflection;

using Harborline.Api.Blocks.AccessGrant;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Tests.Audit;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>
/// Ticket 212 slice 1 (ledger L648/L650) — every verdict the gate and the separation-of-duty engine
/// produce carries ONE evidence object, and that object projects into exactly four ordered public steps.
/// </summary>
public sealed class DecisionEvidenceTraceTests
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-212");
    private static readonly ActorId Principal = new("principal-212");
    private static readonly ActorId Other = new("principal-212-other");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
    private static readonly RoleReference Member = new(RoleVocabularies.Domain, "member");

    private static readonly string[] ExpectedStages =
    [
        AuthorizationDecisionEvidence.ActStage,
        AuthorizationDecisionEvidence.EffectiveRolesStage,
        AuthorizationDecisionEvidence.StandingsStage,
        AuthorizationDecisionEvidence.VerdictStage,
    ];

    [Fact]
    public async Task Allow_ProjectsFourOrderedStepsNamingTheDecidingGrant()
    {
        var decision = await Gate([Derivation("records:write@/records/a", grantId: "grant-deciding")])
            .DecideAsync(Request());

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        var steps = AssertFourOrderedSteps(decision.Evidence);
        Assert.Equal(AuthorizationRefusalShape.None, decision.Evidence.Refusal);
        Assert.Contains("deciding:grant:grant-deciding@7", steps[1].Facts);
        Assert.Contains(steps[1].Facts, fact =>
            fact.Contains("binding:grant-deciding@7", StringComparison.Ordinal)
            && fact.Contains("scope:/", StringComparison.Ordinal)
            && fact.Contains($"valid:{At.AddHours(-1):O}..{At.AddHours(1):O}", StringComparison.Ordinal)
            && fact.EndsWith(";deciding", StringComparison.Ordinal));
        Assert.Contains("verdict:allowed", steps[3].Facts);
    }

    [Theory]
    // absence
    [InlineData("", AuthorizationRefusalShape.NoEffectiveRole)]
    // the grant no longer reaches the record: narrowing
    [InlineData("narrowed", AuthorizationRefusalShape.GrantNarrowed)]
    // the binding was outside its validity window at the decided instant: lapse
    [InlineData("lapsed", AuthorizationRefusalShape.ValidityLapsed)]
    // the operation is held, but not at a scope covering the act
    [InlineData("wrong-scope", AuthorizationRefusalShape.ScopeMismatch)]
    // roles in force, none carrying the requested operation
    [InlineData("wrong-operation", AuthorizationRefusalShape.OperationNotHeld)]
    public async Task EveryGateRefusalShape_CarriesEvidenceNamingItsShape(
        string shape, AuthorizationRefusalShape expected)
    {
        var derivations = shape switch
        {
            "" => Array.Empty<AuthorizationAtomDerivation>(),
            "narrowed" => [Derivation("records:write@/records/b", grantScope: "/records/b")],
            "lapsed" => [Derivation("records:write@/records/b", validUntil: At.AddHours(-1), inForce: false)],
            "wrong-scope" => [Derivation("records:write@/records/b")],
            _ => (AuthorizationAtomDerivation[])[Derivation("records:read@/records/a")],
        };

        var decision = await Gate(derivations).DecideAsync(Request());

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
        Assert.Equal(expected, decision.Evidence.Refusal);
        var steps = AssertFourOrderedSteps(decision.Evidence);
        Assert.Contains("verdict:denied", steps[3].Facts);
        Assert.Contains($"refusal:{expected}", steps[3].Facts);
        Assert.Contains("deciding:none", steps[1].Facts);
    }

    [Fact]
    public async Task StandingBackedAllow_NamesTheDecidingStandingRule()
    {
        var gate = new AuthorizationGate(
            new StaticReader([]),
            new StaticStandingResolver([new RecordStanding(Member, "reviewer-of-record", "evidence-7")]),
            new StaticDefinitionReader(new Dictionary<RoleReference, IReadOnlyList<PermissionAtom>>
            {
                [Member] = [PermissionAtom.Parse("records:write@/records/a")],
            }));

        var decision = await gate.DecideAsync(Request());

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        var steps = AssertFourOrderedSteps(decision.Evidence);
        Assert.Contains("deciding:standing:reviewer-of-record@evidence-7", steps[1].Facts);
        Assert.Contains("role:tax.roles/member;rule:reviewer-of-record;evidence:evidence-7", steps[2].Facts);
    }

    [Fact]
    public void SeparationOfDutyConflict_CarriesEvidenceNamingTheConflict()
    {
        var decision = new SeparationOfDutyEngine().Decide(Approval([new ApproverOnRecord(Principal)]));

        Assert.False(decision.Approved);
        Assert.Equal(AuthorizationRefusalShape.SeparationOfDutyConflict, decision.Evidence.Refusal);
        var steps = AssertFourOrderedSteps(decision.Evidence);
        Assert.Contains($"kind:{AuthorizationEvidenceKind.SeparationOfDuty}", steps[0].Facts);
        Assert.Contains($"approver:{Principal.Value}", steps[1].Facts);
        Assert.Contains("deciding:limit-source:Grant:limit-grant@v2;policy:policy-212@v9;limit:500", steps[1].Facts);
        Assert.Contains(
            $"role:separation-of-duty:{SeparationOfDutyOutcome.Conflict};rule:{ApprovalRefusalReason.SelfApproval};evidence:Open",
            steps[2].Facts);
        Assert.Contains("verdict:denied", steps[3].Facts);
    }

    [Fact]
    public void SeparationOfDutyApproval_CarriesAnAllowedEvidence()
    {
        var decision = new SeparationOfDutyEngine().Decide(Approval([new ApproverOnRecord(Other)]));

        Assert.True(decision.Approved);
        Assert.Equal(AuthorizationRefusalShape.None, decision.Evidence.Refusal);
        Assert.Contains("verdict:allowed", AssertFourOrderedSteps(decision.Evidence)[3].Facts);
    }

    [Fact]
    public void SeparationOfDutyRefusalThatIsNotAConflict_IsNamedAsAnApprovalRefusal()
    {
        var decision = new SeparationOfDutyEngine().Decide(
            Approval([new ApproverOnRecord(Other)], requiredApprovers: 2));

        Assert.Equal(AuthorizationRefusalShape.ApprovalRefused, decision.Evidence.Refusal);
        Assert.Contains(
            $"role:separation-of-duty:{SeparationOfDutyOutcome.Pass};rule:{ApprovalRefusalReason.InsufficientApprovers};evidence:Open",
            AssertFourOrderedSteps(decision.Evidence)[2].Facts);
    }

    /// <summary>
    /// The projection is a pure function over the evidence: the same evidence projects to the same steps,
    /// and the steps are values rather than a view over mutable decision state.
    /// </summary>
    [Fact]
    public async Task Projection_IsPureAndRepeatable()
    {
        var decision = await Gate([Derivation("records:write@/records/a")]).DecideAsync(Request());

        Assert.Equal(Flatten(decision.Evidence.Project()), Flatten(decision.Evidence.Project()));
        Assert.Equal(AuthorizationDecisionEvidence.CurrentVersion, decision.Evidence.Version);
    }

    /// <summary>
    /// A decision without evidence is impossible at the type level: on both deciding paths the property is
    /// non-nullable, and every constructor that could set it is non-public, so no caller can construct a
    /// verdict that skipped it.
    /// </summary>
    [Theory]
    [InlineData(typeof(AuthorizationDecision))]
    [InlineData(typeof(SeparationOfDutyDecision))]
    public void EveryDecisionType_HasNonNullableEvidenceAndNoPublicConstructor(Type decisionType)
    {
        var evidence = decisionType.GetProperty("Evidence", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(evidence);
        Assert.Equal(typeof(AuthorizationDecisionEvidence), evidence!.PropertyType);
        Assert.Null(evidence.SetMethod);
        Assert.Empty(decisionType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.All(
            decisionType.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance),
            constructor => Assert.False(constructor.IsPublic));
    }

    /// <summary>
    /// Ledger L650 — the audit entry the deciding path already writes carries the four steps, copied from
    /// the decision's evidence. Audit projects nothing of its own.
    /// </summary>
    [Fact]
    public async Task AuditEntry_CarriesTheProjectedFourStepsFromTheDecision()
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var (decision, _) = await AuthoritySnapshotTests.DecisionAsync(signer, Tenant, At, ("grant-212", 3));
        using var fixture = AuditTrailFixture.Create(decorated: true, signer);

        await fixture.Authorized.AppendAuthorizedAsync(
            await AuthoritySnapshotTests.RecordAsync(signer, Tenant, At), decision);

        var stored = new List<AuditRecord>();
        await foreach (var record in fixture.Authorized.QueryAsync(new AuditQuery(Tenant)))
            stored.Add(record);
        var snapshot = Assert.Single(stored).AuthoritySnapshot!;
        Assert.Equal(AuthorizationDecisionEvidence.CurrentVersion, snapshot.TraceVersion);
        Assert.Equal(
            Flatten(decision.Evidence.Project()),
            snapshot.Trace!.Select(step => $"{step.Ordinal}:{step.Stage}:{string.Join("|", step.Facts)}"));
        Assert.Contains("deciding:grant:grant-212@3", snapshot.Trace![1].Facts);
    }

    /// <summary>
    /// At the exact expiry instant the trace must state the in-force fact the DECISION used, not one the
    /// evidence re-derived: production's window is half-open (<see cref="GrantValidity.Contains"/>, via
    /// <see cref="AccessGrant.IsActiveAt"/>), so a grant whose ValidTo equals the decided instant is out of
    /// force, step two must read <c>in-force:no</c>, and the refusal shape must be the lapse. Red before the
    /// evidence stopped recomputing the window with a closed upper bound.
    /// </summary>
    [Fact]
    public async Task AtTheExactExpiryInstant_StepTwoReportsTheInForceFactTheVerdictUsed()
    {
        var grant = ExpiringGrant(validTo: At);
        var inForce = grant.IsActiveAt(At);
        Assert.False(inForce);

        var decision = await Gate(
            [Derivation(
                // Not covering the act: the gate itself is validity-blind, so a binding that reaches it
                // decides the verdict on coverage. What is under test is the FACT the trace states.
                "records:write@/records/b",
                grantId: grant.GrantId.ToString(),
                validUntil: grant.Validity.ValidTo,
                inForce: inForce)])
            .DecideAsync(Request());

        Assert.Equal(AuthorizationVerdict.Denied, decision.Verdict);
        Assert.Equal(AuthorizationRefusalShape.ValidityLapsed, decision.Evidence.Refusal);
        var steps = AssertFourOrderedSteps(decision.Evidence);
        Assert.Contains(steps[1].Facts, fact => fact.Contains("in-force:no", StringComparison.Ordinal));
        Assert.Contains($"refusal:{AuthorizationRefusalShape.ValidityLapsed}", steps[3].Facts);
    }

    /// <summary>A derivation whose producer computed no in-force fact says so rather than guessing.</summary>
    [Fact]
    public async Task ADerivationWithNoComputedInForceFact_SaysNotComputed()
    {
        var derivation = Derivation("records:write@/records/a") with { InForce = null };

        var decision = await Gate([derivation]).DecideAsync(Request());

        Assert.Contains(
            AssertFourOrderedSteps(decision.Evidence)[1].Facts,
            fact => fact.Contains("in-force:not-computed", StringComparison.Ordinal));
    }

    private static AccessGrant ExpiringGrant(DateTimeOffset validTo) =>
        new(
            GrantId.New(),
            Tenant,
            Principal,
            Member,
            ScopeExpression.Parse("/"),
            GrantResidency.Cache,
            new GrantValidity(At.AddHours(-1), validTo),
            GranterKind.Installer,
            Other,
            At.AddHours(-1),
            new GrantProvenance(
                GrantSourceKind.Bootstrap,
                new GrantReason(GrantReasonCodes.Bootstrap, "expiry-boundary"),
                Other),
            At.AddHours(-1));

    /// <summary>
    /// When a separation-of-duty decision also ran, ITS four steps are stored under ordinals 5..8. Without
    /// them a write the gate allowed and self-approval refused would store a trace reading verdict:allowed.
    /// </summary>
    [Fact]
    public async Task AuditEntry_AlsoCarriesTheApprovalSteps_WhenAnApprovalDecided()
    {
        using var keys = KeyPair.Generate();
        var signer = new Ed25519Signer(keys);
        var (decision, _) = await AuthoritySnapshotTests.DecisionAsync(signer, Tenant, At, ("grant-212", 3));
        var approval = new SeparationOfDutyEngine().Decide(Approval([new ApproverOnRecord(Principal)]));
        using var fixture = AuditTrailFixture.Create(decorated: true, signer);

        await fixture.Authorized.AppendAuthorizedAsync(
            await AuthoritySnapshotTests.RecordAsync(signer, Tenant, At), decision, approval: approval);

        var stored = new List<AuditRecord>();
        await foreach (var record in fixture.Authorized.QueryAsync(new AuditQuery(Tenant)))
            stored.Add(record);
        var trace = Assert.Single(stored).AuthoritySnapshot!.Trace!;
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], trace.Select(step => step.Ordinal));
        Assert.Contains("verdict:denied", trace[7].Facts);
        Assert.Contains($"refusal:{AuthorizationRefusalShape.SeparationOfDutyConflict}", trace[7].Facts);
    }

    private static IEnumerable<string> Flatten(IReadOnlyList<AuthorizationTraceStep> steps) =>
        steps.Select(step => $"{step.Ordinal}:{step.Stage}:{string.Join("|", step.Facts)}");

    private static IReadOnlyList<AuthorizationTraceStep> AssertFourOrderedSteps(
        AuthorizationDecisionEvidence evidence)
    {
        Assert.NotNull(evidence);
        var steps = evidence.Project();
        Assert.Equal(AuthorizationDecisionEvidence.StepCount, steps.Count);
        Assert.Equal(ExpectedStages, steps.Select(step => step.Stage));
        Assert.Equal([1, 2, 3, 4], steps.Select(step => step.Ordinal));
        Assert.Contains($"act:{evidence.Act}", steps[0].Facts);
        Assert.Contains(
            $"verdict:{(evidence.Allowed ? "allowed" : "denied")}", steps[3].Facts);
        return steps;
    }

    private static SeparationOfDutyRequest Approval(
        IReadOnlyList<ApproverOnRecord> approvers, int requiredApprovers = 1) =>
        new(
            PermissionAtom.Parse("records:write@/records/a"),
            Principal,
            Tenant,
            approvers,
            new ApprovalThreshold(
                new ApprovalPolicyFact("policy-212", "v9"),
                500m,
                requiredApprovers,
                new ApprovalLimitSourceFact(ApprovalLimitSourceKind.Grant, "limit-grant", "v2")),
            SeparationOfDutyEngine.OpenPostingPeriod,
            At);

    private static AuthorizationGate Gate(IReadOnlyList<AuthorizationAtomDerivation> derivations) => new(
        new StaticReader(derivations),
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
        DateTimeOffset? validUntil = null,
        bool inForce = true) =>
        new(PermissionAtom.Parse(atom), Member, grantId, 7, "definition",
            ScopeExpression.Parse(grantScope), At.AddHours(-1), validUntil ?? At.AddHours(1), inForce);

    private sealed class StaticReader(IReadOnlyList<AuthorizationAtomDerivation> derivations)
        : IAuthorizationClosureSnapshotReader
    {
        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(
            AuthorizationGateRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(new AuthorizationClosureSnapshot(derivations));
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
