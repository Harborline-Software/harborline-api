using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationGateTests
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-gate");
    private static readonly ActorId Principal = new("principal-gate");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
    private static readonly RoleReference Member = new(RoleVocabularies.Domain, "member");
    private static readonly RoleReference Reviewer = new(RoleVocabularies.Domain, "reviewer");

    [Fact]
    public async Task DecideAsync_ResolvesExactlyFourStagesInL670Order()
    {
        var calls = new List<string>();
        var gate = Gate([Derivation("records:write@/records/a")], calls);

        var decision = await gate.DecideAsync(Request("records:write@/records/a"));

        Assert.Equal(AuthorizationVerdict.Allowed, decision.Verdict);
        Assert.Equal(
            [
                AuthorizationResolutionStage.ActKind,
                AuthorizationResolutionStage.EffectiveRecordRoles,
                AuthorizationResolutionStage.RecordStandings,
                AuthorizationResolutionStage.NamedRoleUnionVerdict,
            ],
            decision.Resolution.Select(step => step.Stage));
        Assert.Equal(["closure", "standings"], calls);
    }

    [Fact]
    public async Task DecideAsync_AllowsExactAndAncestorScopedAtom()
    {
        var exact = await Gate([Derivation("records:write@/records/a")])
            .DecideAsync(Request("records:write@/records/a"));
        var ancestor = await Gate([Derivation("records:write@/records")])
            .DecideAsync(Request("records:write@/records/a"));

        Assert.Equal(AuthorizationVerdict.Allowed, exact.Verdict);
        Assert.Equal(AuthorizationVerdict.Allowed, ancestor.Verdict);
    }

    [Fact]
    public async Task DecideAsync_DeniesSiblingScopeOrWrongOperation()
    {
        var sibling = await Gate([Derivation("records:write@/records/b")])
            .DecideAsync(Request("records:write@/records/a"));
        var wrongOperation = await Gate([Derivation("records:read@/records")])
            .DecideAsync(Request("records:write@/records/a"));

        Assert.Equal(AuthorizationVerdict.Denied, sibling.Verdict);
        Assert.Equal(AuthorizationVerdict.Denied, wrongOperation.Verdict);
    }

    [Fact]
    public async Task DecideAsync_RefusesCrossTargetBeforeSnapshotRead()
    {
        var calls = new List<string>();
        var gate = Gate([Derivation("records:write@/records/other")], calls);
        var request = new AuthorizationGateRequest(
            PermissionAtom.Parse("records:write@/records/other"), Principal, Tenant,
            new AuthorizationTarget("record", "a", ScopeExpression.Parse("/records/other")), At);

        await Assert.ThrowsAsync<ArgumentException>(() => gate.DecideAsync(request).AsTask());

        Assert.Empty(calls);
    }

    [Fact]
    public async Task DecideAsync_RefusesWrongOperationRecordKindBeforeSnapshotRead()
    {
        var calls = new List<string>();
        var gate = Gate([], calls);
        var request = new AuthorizationGateRequest(
            PermissionAtom.Parse("ledger:post@/records/a"), Principal, Tenant,
            new AuthorizationTarget("record", "a", ScopeExpression.Parse("/records/a")), At);

        await Assert.ThrowsAsync<ArgumentException>(() => gate.DecideAsync(request).AsTask());

        Assert.Empty(calls);
    }

    [Fact]
    public async Task Decision_IsImmutableAndUsesRequestInstantWithoutResamplingClock()
    {
        var source = new List<AuthorizationAtomDerivation> { Derivation("records:write@/records") };
        var clock = new ManualTimeProvider(At);
        var request = new AuthorizationGateRequest(
            PermissionAtom.Parse("records:write@/records/a"), Principal, Tenant,
            new AuthorizationTarget("record", "a", ScopeExpression.Parse("/records/a")),
            clock.GetUtcNow());
        var decision = await Gate(source).DecideAsync(request);
        source.Clear();
        clock.Advance(TimeSpan.FromDays(1));

        Assert.Equal(At, decision.DecidedAt);
        Assert.Single(decision.Derivations);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AuthorizationAtomDerivation>)decision.Derivations).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<AuthorizationResolutionStep>)decision.Resolution).Clear());
    }

    [Fact]
    public async Task Decision_RecordsEmptyStandingStageWithoutPersistingStanding()
    {
        var decision = await Gate([Derivation("records:write@/records")])
            .DecideAsync(Request("records:write@/records/a"));

        Assert.Empty(decision.Standings);
        var step = Assert.Single(decision.Resolution,
            item => item.Stage == AuthorizationResolutionStage.RecordStandings);
        Assert.Empty(step.Outputs);
        Assert.DoesNotContain(decision.Derivations,
            derivation => derivation.Role.Name.Contains("standing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AtomAndNamedRoleReadings_ProduceEquivalentVerdict()
    {
        var allowed = await Gate([Derivation("records:write@/records")])
            .DecideAsync(Request("records:write@/records/a"));
        var denied = await Gate([Derivation("records:read@/records")])
            .DecideAsync(Request("records:write@/records/a"));

        AssertReadingsAgree(allowed);
        AssertReadingsAgree(denied);
    }

    [Theory]
    [InlineData("records:write@/records/a", true)]
    [InlineData("records:write@/unrelated", false)]
    [InlineData("records:write@/records", true)]
    [InlineData("records:write@/records/b", false)]
    public async Task StandingDefinition_IsScopedToMatchingTarget(
        string definitionAtom, bool expectedAllowed)
    {
        var standings = new RecordingStandingResolver(null,
            [new RecordStanding(Reviewer, "reviewer-of-record", "evidence-7")]);
        var definitions = new StaticDefinitionReader(new Dictionary<RoleReference, IReadOnlyList<PermissionAtom>>
        {
            [Reviewer] = [PermissionAtom.Parse(definitionAtom)],
        });
        var gate = new AuthorizationGate(new StaticReader([], null), standings, definitions);

        var decision = await gate.DecideAsync(Request("records:write@/records/a"));

        Assert.Equal(expectedAllowed ? AuthorizationVerdict.Allowed : AuthorizationVerdict.Denied, decision.Verdict);
        AssertReadingsAgree(decision);
    }

    [Fact]
    public async Task DecideAsync_ObservesCancellationAfterSnapshotDependency()
    {
        using var cts = new CancellationTokenSource();
        var calls = new List<string>();
        var gate = new AuthorizationGate(
            new CancellingReader(cts, calls),
            new RecordingStandingResolver(calls),
            new StaticDefinitionReader(new Dictionary<RoleReference, IReadOnlyList<PermissionAtom>>()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.DecideAsync(Request("records:write@/records/a"), cts.Token).AsTask());

        Assert.Equal(["closure"], calls);
    }

    [Fact]
    public async Task DecideAsync_ObservesCancellationAfterStandingDependency()
    {
        using var cts = new CancellationTokenSource();
        var calls = new List<string>();
        var gate = new AuthorizationGate(
            new StaticReader([Derivation("records:write@/records/a")], calls),
            new CancellingStandingResolver(cts, calls),
            new StaticDefinitionReader(new Dictionary<RoleReference, IReadOnlyList<PermissionAtom>>()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.DecideAsync(Request("records:write@/records/a"), cts.Token).AsTask());

        Assert.Equal(["closure", "standings"], calls);
    }

    [Fact]
    public async Task DecideAsync_ObservesCancellationAfterDefinitionDependency()
    {
        using var cts = new CancellationTokenSource();
        var calls = new List<string>();
        var gate = new AuthorizationGate(
            new StaticReader([Derivation("records:write@/records/a")], calls),
            new RecordingStandingResolver(calls),
            new CancellingDefinitionReader(cts, calls));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.DecideAsync(Request("records:write@/records/a"), cts.Token).AsTask());

        Assert.Equal(["closure", "standings", "definitions"], calls);
    }

    private static void AssertReadingsAgree(AuthorizationDecision decision)
    {
        var verdict = decision.Verdict.ToString().ToLowerInvariant();
        var step = Assert.Single(decision.Resolution,
            item => item.Stage == AuthorizationResolutionStage.NamedRoleUnionVerdict);
        Assert.Contains($"atom-coverage:{verdict}", step.Outputs);
        Assert.Contains($"named-role-union:{verdict}", step.Outputs);
    }

    private static AuthorizationGate Gate(
        IReadOnlyList<AuthorizationAtomDerivation> derivations,
        List<string>? calls = null) => new(
            new StaticReader(derivations, calls),
            new RecordingStandingResolver(calls),
            new StaticDefinitionReader(derivations
                .GroupBy(item => item.Role)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<PermissionAtom>)group.Select(item => item.Atom).Distinct().ToArray())));

    private static AuthorizationGateRequest Request(string atom) =>
        new(PermissionAtom.Parse(atom), Principal, Tenant,
            new AuthorizationTarget("record", "a", ScopeExpression.Parse("/records/a")), At);

    private static AuthorizationAtomDerivation Derivation(
        string atom,
        string grantId = "grant",
        DateTimeOffset? validFrom = null) =>
        new(PermissionAtom.Parse(atom), Member, grantId, 7, "definition",
            ScopeExpression.Parse("/"), validFrom ?? At.AddHours(-1), At.AddHours(1));

    private sealed class StaticReader(
        IReadOnlyList<AuthorizationAtomDerivation> derivations,
        List<string>? calls) : IAuthorizationClosureSnapshotReader
    {
        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(
            AuthorizationGateRequest request, CancellationToken ct = default)
        {
            calls?.Add("closure");
            return ValueTask.FromResult(new AuthorizationClosureSnapshot(derivations));
        }
    }

    private sealed class RecordingStandingResolver(
        List<string>? calls,
        IReadOnlyList<RecordStanding>? result = null) : IRecordStandingResolver
    {
        public ValueTask<IReadOnlyList<RecordStanding>> ResolveAsync(
            AuthorizationGateRequest request,
            IReadOnlySet<RoleReference> effectiveRecordRoles,
            CancellationToken ct = default)
        {
            calls?.Add("standings");
            return ValueTask.FromResult(result ?? (IReadOnlyList<RecordStanding>)[]);
        }
    }

    private sealed class StaticDefinitionReader(
        IReadOnlyDictionary<RoleReference, IReadOnlyList<PermissionAtom>> atoms)
        : IAuthorizationDefinitionAtomReader
    {
        public ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(
            TenantId tenantId, RoleReference role, CancellationToken ct = default) =>
            ValueTask.FromResult(atoms.GetValueOrDefault(role, []));
    }

    private sealed class CancellingReader(CancellationTokenSource cts, List<string> calls)
        : IAuthorizationClosureSnapshotReader
    {
        public ValueTask<AuthorizationClosureSnapshot> ReadAsync(
            AuthorizationGateRequest request, CancellationToken ct = default)
        {
            calls.Add("closure");
            cts.Cancel();
            return ValueTask.FromResult(new AuthorizationClosureSnapshot([]));
        }
    }

    private sealed class CancellingStandingResolver(CancellationTokenSource cts, List<string> calls)
        : IRecordStandingResolver
    {
        public ValueTask<IReadOnlyList<RecordStanding>> ResolveAsync(
            AuthorizationGateRequest request, IReadOnlySet<RoleReference> effectiveRecordRoles,
            CancellationToken ct = default)
        {
            calls.Add("standings");
            cts.Cancel();
            return ValueTask.FromResult<IReadOnlyList<RecordStanding>>([]);
        }
    }

    private sealed class CancellingDefinitionReader(CancellationTokenSource cts, List<string> calls)
        : IAuthorizationDefinitionAtomReader
    {
        public ValueTask<IReadOnlyList<PermissionAtom>> AtomsForRoleAsync(
            TenantId tenantId, RoleReference role, CancellationToken ct = default)
        {
            calls.Add("definitions");
            cts.Cancel();
            return ValueTask.FromResult<IReadOnlyList<PermissionAtom>>([]);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
