using System.Reflection;
using System.Text.Json;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AccessGrantContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Scim_Grant_RoundTrips_All_Authority_And_Provenance_Fields()
    {
        var original = Grant() with
        {
            Revocation = new GrantRevocation(
                new ActorId("security-admin"), Now.AddHours(2),
                new GrantReason(GrantReasonCodes.RevocationCompromise, "INC-42")),
            Status = GrantStatus.Revoked,
        };

        var json = JsonSerializer.Serialize(original);
        var copy = JsonSerializer.Deserialize<AccessGrant>(json);

        Assert.Equal(original, copy);
        Assert.Equal("/records/record-7", copy!.Scope.Value);
        Assert.Equal(GrantSourceKind.Ticket, copy.Grant.Source);
        Assert.Equal("SEC-204", copy.Grant.Reason.Reference);
        Assert.Equal("security-admin", copy.Revocation!.RevokedBy.Value);
    }

    [Fact]
    public void PostBootstrap_Grant_Requires_A_Person_Granter()
    {
        var source = Grant();

        // 274: an empty ActorId is no longer constructible; default(ActorId) is the unset granter now.
        Assert.Throws<ArgumentException>(() => new AccessGrant(
            source.GrantId, source.TenantId, source.Subject, source.Role, source.Scope,
            source.Residency, source.Validity, GranterKind.Installer, default(ActorId), source.GrantedAt,
            source.Grant, source.LastReviewedAt));
    }

    [Fact]
    public void Bootstrap_Is_The_Only_Installer_Granter_Exception()
    {
        var source = Grant();
        var bootstrap = new AccessGrant(
            source.GrantId, source.TenantId, source.Subject, RoleReference.Administrator,
            ScopeExpression.Parse("/"), source.Residency, source.Validity,
            GranterKind.Installer, default(ActorId), source.GrantedAt,
            new GrantProvenance(GrantSourceKind.Bootstrap,
                new GrantReason(GrantReasonCodes.Bootstrap), new ActorId("root-issuer")),
            source.LastReviewedAt);

        Assert.Equal(GranterKind.Installer, bootstrap.GranterKind);
        Assert.Equal(GrantReasonCodes.Bootstrap, bootstrap.Grant.Reason.Code);
    }

    [Fact]
    public void Grant_Reasons_Are_Closed_And_References_Are_Bounded()
    {
        Assert.Equal(8, GrantReasonCodes.All.Count);
        Assert.Throws<ArgumentException>(() => new GrantReason("free-form"));
        Assert.Throws<ArgumentException>(() =>
            new GrantReason(GrantReasonCodes.Ticket, new string('x', 129)));
        Assert.Equal(128, new GrantReason(GrantReasonCodes.Ticket, new string('x', 128)).Reference!.Length);
    }

    [Fact]
    public void Validity_Rejects_Zero_And_Negative_Windows()
    {
        Assert.Throws<ArgumentException>(() => new GrantValidity(Now, Now));
        Assert.Throws<ArgumentException>(() => new GrantValidity(Now, Now.AddTicks(-1)));
    }

    [Fact]
    public void Grant_Model_Has_No_Deny_Representation()
    {
        Assert.Equal([GrantStatus.Active, GrantStatus.Revoked], Enum.GetValues<GrantStatus>());
        Assert.DoesNotContain(typeof(AccessGrant).GetMembers(BindingFlags.Public | BindingFlags.Instance),
            member => member.Name.Contains("Deny", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Grant_Store_And_Live_Closure_Have_Only_The_Approved_Surface()
    {
        Assert.Equal(
            ["AppendAsync", "ChangeValidityAsync", "FindAsync", "FindByPrincipalAsync",
                "FindBySourceReferenceAsync", "FindVersionedAsync", "FindVersionedByPrincipalAsync",
                "HandoverAdministratorAsync", "HasAdministratorGrantEverAsync", "RecordReviewAsync", "RevokeAsync", "SnapshotAsync"],
            typeof(IGrantStore).GetMethods().Select(method => method.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["AssignedUsersAsync", "RolePermissionsAsync", "UserPermissionsAsync"],
            typeof(IAuthorizationClosureReader).GetMethods().Select(method => method.Name)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task InMemoryGrantStore_TracksSourceAndMutationEvidence()
    {
        var store = TestInMemoryAuthorizationStores.GrantStore();
        var grant = await store.AppendAsync(Grant().TenantId, Grant(), "source-a");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendAsync(grant.TenantId, grant, "source-b"));

        var changed = await store.ChangeValidityAsync(
            grant.TenantId, grant.GrantId, new GrantValidity(Now, Now.AddDays(1)),
            new ActorId("changer"), new GrantReason(GrantReasonCodes.Manual));
        Assert.Equal("changer", changed!.ValidityChange!.ChangedBy.Value);

        var reviewed = await store.RecordReviewAsync(
            grant.TenantId, grant.GrantId, Now.AddMinutes(1), new ActorId("reviewer"));
        Assert.Equal("reviewer", reviewed!.LastReviewedBy!.Value.Value);
    }

    [Fact]
    public async Task InMemoryGrantStore_RevokedGrantIsImmutable()
    {
        var store = TestInMemoryAuthorizationStores.GrantStore();
        var grant = await store.AppendAsync(Grant().TenantId, Grant(), "source-a");
        var revocation = new GrantRevocation(
            new ActorId("revoker"), Now, new GrantReason(GrantReasonCodes.RevocationOffboarding));
        var revoked = await store.RevokeAsync(grant.TenantId, grant.GrantId, revocation);
        Assert.Equal(revoked, await store.RevokeAsync(grant.TenantId, grant.GrantId, revocation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RecordReviewAsync(
            grant.TenantId, grant.GrantId, Now.AddMinutes(1), new ActorId("reviewer")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RevokeAsync(
            grant.TenantId, grant.GrantId, revocation with { RevokedAt = Now.AddMinutes(1) }));
    }

    [Fact]
    public async Task Issuance_Refuses_An_Unresolved_Role()
    {
        var request = Request(new RoleReference(RoleVocabularies.Domain, "unknown"), ScopeExpression.Parse("/"));
        var handler = new GrantIssuanceHandler(new NoWriteContext(),
            new InMemoryRoleVocabulary([AccessGrantAuthorizationSeed.MemberDefinition]),
            new IssuanceClosure(PermissionAtom.Parse("records:read@/")));

        var outcome = await handler.DecideAsync(Instance(request),
            WorkflowTrigger.For(WorkflowTriggerKind.Event, "grant-workflow", GrantIssuanceSteps.Decide)
                with { At = Now });

        Assert.Equal(WorkflowStepOutcomeKind.Advance, outcome.Kind);
        Assert.Equal(GrantIssuanceSteps.Rejected, outcome.NextStep);
    }

    [Fact]
    public void Issuance_Request_Is_Role_And_Provenance_Shaped()
    {
        var json = GrantIssuanceHandler.SerializeRequest(
            Request(AccessGrantAuthorizationSeed.MemberRole, ScopeExpression.Parse("/records/7")));

        Assert.Contains("\"role\"", json, StringComparison.Ordinal);
        Assert.Contains("\"scopeExpression\"", json, StringComparison.Ordinal);
        Assert.Contains("\"grant\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("permissions", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Issuance_Attenuates_By_Scoped_Closure_Coverage()
    {
        var closure = new IssuanceClosure(PermissionAtom.Parse("records:read@/"));
        var handler = new GrantIssuanceHandler(new NoWriteContext(),
            new InMemoryRoleVocabulary([AccessGrantAuthorizationSeed.MemberDefinition]),
            closure);

        var covered = await handler.DecideAsync(
            Instance(Request(AccessGrantAuthorizationSeed.MemberRole, ScopeExpression.Parse("/north/site"))),
            WorkflowTrigger.For(WorkflowTriggerKind.Event, "grant-workflow", GrantIssuanceSteps.Decide)
                with { At = Now });
        var disjoint = await handler.DecideAsync(
            Instance(Request(AccessGrantAuthorizationSeed.MemberRole, ScopeExpression.Parse("/south"))),
            WorkflowTrigger.For(WorkflowTriggerKind.Event, "grant-workflow", GrantIssuanceSteps.Decide)
                with { At = Now });

        Assert.Equal(WorkflowStepOutcomeKind.Park, covered.Kind);
        Assert.Equal(GrantIssuanceSteps.Approve, covered.NextStep);
        Assert.Equal(WorkflowStepOutcomeKind.Advance, disjoint.Kind);
        Assert.Equal(GrantIssuanceSteps.Rejected, disjoint.NextStep);
        Assert.Equal(new TenantId("tenant-a"), closure.LastTenant);
        Assert.Equal(new ActorId("security-admin"), closure.LastPrincipal);
        Assert.Equal(Now, closure.LastAt);
    }

    [Theory]
    [InlineData(GrantIssuanceSteps.Decide)]
    [InlineData(GrantIssuanceSteps.Approve)]
    public async Task Issuance_Refuses_Whitespace_Tenant(string step)
    {
        var request = Request(AccessGrantAuthorizationSeed.MemberRole, ScopeExpression.Parse("/north"))
            with { TenantId = " " };
        var handler = new GrantIssuanceHandler(new NoWriteContext(),
            new InMemoryRoleVocabulary([AccessGrantAuthorizationSeed.MemberDefinition]),
            new IssuanceClosure(PermissionAtom.Parse("records:read@/")));
        var trigger = WorkflowTrigger.For(WorkflowTriggerKind.Event, "grant-workflow", step)
            with { At = Now, PayloadJson = "{\"decision\":\"approve\"}" };
        await Assert.ThrowsAsync<ArgumentException>(() => handler.DecideAsync(Instance(request), trigger).AsTask());
    }

    private static AccessGrant Grant() => new(
        new GrantId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
        new TenantId("tenant-a"), new ActorId("subject-a"), AccessGrantAuthorizationSeed.MemberRole,
        ScopeExpression.Parse("/records/record-7"), GrantResidency.Cache,
        new GrantValidity(Now.AddHours(-1), Now.AddDays(30)), GranterKind.Person,
        new ActorId("security-admin"), Now,
        new GrantProvenance(GrantSourceKind.Ticket,
            new GrantReason(GrantReasonCodes.Ticket, "SEC-204"), new ActorId("security-admin")),
        Now);

    private static GrantIssuanceRequest Request(RoleReference role, ScopeExpression scope) => new(
        Guid.Parse("20000000-0000-0000-0000-000000000001"), "tenant-a", "subject-a", role,
        "security-admin", GranterKind.Person, scope, GrantResidency.Cache, Now, Now.AddDays(1),
        new GrantProvenance(GrantSourceKind.Workflow,
            new GrantReason(GrantReasonCodes.Workflow, "workflow-7"), new ActorId("security-admin")), Now);

    private static WorkflowInstanceRecord Instance(GrantIssuanceRequest request) => new()
    {
        Id = "grant-workflow",
        TenantId = request.TenantId,
        DefinitionKey = GrantIssuanceSteps.DefinitionKey,
        DefinitionVersion = "1",
        CurrentStep = GrantIssuanceSteps.Decide,
        Status = WorkflowStatus.Running,
        StateJson = GrantIssuanceHandler.SerializeRequest(request),
    };

    private sealed class NoWriteContext : IGrantIssuanceContext
    {
        public WorkflowEffect BuildGrantWriteEffect(AccessGrant grant, WorkflowStepKey stepKey) =>
            throw new InvalidOperationException("The decide tests never write.");
    }

    private sealed class IssuanceClosure(PermissionAtom roleAtom) : IAuthorizationClosureReader
    {
        public TenantId? LastTenant { get; private set; }
        public ActorId? LastPrincipal { get; private set; }
        public DateTimeOffset? LastAt { get; private set; }

        public ValueTask<PermissionAtomSet> UserPermissionsAsync(
            TenantId tenantId, ActorId principal, DateTimeOffset at, CancellationToken ct = default)
        {
            LastTenant = tenantId;
            LastPrincipal = principal;
            LastAt = at;
            return ValueTask.FromResult(PermissionAtomSet.Of(PermissionAtom.Parse("records:read@/north")));
        }

        public ValueTask<IReadOnlyList<ActorId>> AssignedUsersAsync(
            TenantId tenantId, PermissionAtom required, DateTimeOffset at, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<ActorId>>([]);

        public ValueTask<PermissionAtomSet> RolePermissionsAsync(
            TenantId tenantId, RoleReference role, CancellationToken ct = default) =>
            ValueTask.FromResult(PermissionAtomSet.Of(roleAtom));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
