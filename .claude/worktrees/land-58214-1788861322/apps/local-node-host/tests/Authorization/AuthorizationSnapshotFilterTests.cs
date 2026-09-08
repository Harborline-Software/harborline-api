using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationSnapshotFilterTests
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-filter");
    private static readonly TenantId OtherTenant = TenantId.FromString("tenant-filter-other");
    private static readonly ActorId Principal = new("principal-filter");
    private static readonly ActorId OtherPrincipal = new("principal-filter-other");
    private static readonly ActorId Admin = new("admin-filter");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-02T12:00:00Z");
    private static readonly RoleReference Member = AccessGrantAuthorizationSeed.MemberRole;
    private static readonly RoleReference OldDefinitionRole = new(RoleVocabularies.Domain, "filter-old");

    [Theory]
    [InlineData("ef")]
    [InlineData("joined")]
    public async Task ProductionSnapshots_KeepOnlyValidRowAcrossEveryFilter(string readerKind)
    {
        var validId = GrantId.New();
        var staleId = GrantId.New();
        var vocabulary = new InMemoryRoleVocabulary(
        [
            AccessGrantAuthorizationSeed.MemberDefinition,
            RoleDefinition.CreatePackageRole(RoleDefinitionId.New(), OldDefinitionRole.Name, "Old filter role", "filter-tests"),
        ]);

        if (readerKind == "ef")
        {
            await using var store = await SearchTestStore.CreateAsync();
            var grants = new NodeEfGrantStore(store.Factory);
            var configuration = new NodeEfAuthorizationConfigurationStore(store.Factory, vocabulary);
            await InstallDefinitionsAsync(configuration, configuration, vocabulary);
            var revokedId = await AppendNearMatchesAsync(grants, validId, staleId);
            await grants.RevokeAsync(Tenant, revokedId,
                new GrantRevocation(Admin, At, new GrantReason(GrantReasonCodes.RevocationOffboarding)));
            var reader = new NodeEfAuthorizationClosureReader(store.Factory);
            var request = Request();
            Assert.NotEmpty((await reader.ReadAsync(request)).Derivations);
            await using (var db = store.CreateContext())
                await db.Database.ExecuteSqlAsync(
                    $"UPDATE authorization_principal_atom_closure SET grant_owner_version = 0 WHERE grant_id = {staleId.ToString()}");

            var only = Assert.Single((await reader.ReadAsync(request)).Derivations);
            Assert.Equal(validId.ToString(), only.GrantId);
            return;
        }

        var memoryGrants = TestInMemoryAuthorizationStores.GrantStore();
        var memoryConfiguration = TestInMemoryAuthorizationStores.ConfigurationStore();
        await InstallDefinitionsAsync(memoryConfiguration, memoryConfiguration, vocabulary);
        var memoryRevokedId = await AppendNearMatchesAsync(memoryGrants, validId, staleId);
        await memoryGrants.RevokeAsync(Tenant, memoryRevokedId,
            new GrantRevocation(Admin, At, new GrantReason(GrantReasonCodes.RevocationOffboarding)));
        var staleStore = new StaleListGrantStore(memoryGrants, staleId);
        var joined = new DefinitionJoinedAuthorizationReader(staleStore, memoryConfiguration);

        var joinedOnly = Assert.Single((await joined.ReadAsync(Request())).Derivations);
        Assert.Equal(validId.ToString(), joinedOnly.GrantId);
    }

    /// <summary>
    /// Ticket 212 slice 2 — a lapse, a not-yet-valid window and a revocation reach the gate as an ABSENCE.
    /// Both production snapshot readers now record them beside the derivations, with the reason, so the
    /// counterfactual can name the one change that would have altered the verdict.
    /// </summary>
    [Theory]
    [InlineData("ef")]
    [InlineData("joined")]
    public async Task ProductionSnapshots_RecordWhyEachBindingWasExcluded(string readerKind)
    {
        var validId = GrantId.New();
        var vocabulary = new InMemoryRoleVocabulary(
        [
            AccessGrantAuthorizationSeed.MemberDefinition,
            RoleDefinition.CreatePackageRole(
                RoleDefinitionId.New(), OldDefinitionRole.Name, "Old filter role", "filter-tests"),
        ]);
        AuthorizationClosureSnapshot snapshot;
        if (readerKind == "ef")
        {
            await using var store = await SearchTestStore.CreateAsync();
            var grants = new NodeEfGrantStore(store.Factory);
            var configuration = new NodeEfAuthorizationConfigurationStore(store.Factory, vocabulary);
            await InstallDefinitionsAsync(configuration, configuration, vocabulary);
            await AppendExclusionCasesAsync(grants, validId);
            snapshot = await new NodeEfAuthorizationClosureReader(store.Factory).ReadAsync(Request());
        }
        else
        {
            var grants = TestInMemoryAuthorizationStores.GrantStore();
            var configuration = TestInMemoryAuthorizationStores.ConfigurationStore();
            await InstallDefinitionsAsync(configuration, configuration, vocabulary);
            await AppendExclusionCasesAsync(grants, validId);
            snapshot = await new DefinitionJoinedAuthorizationReader(grants, configuration).ReadAsync(Request());
        }

        // The gate still sees exactly the one effective binding it saw before.
        Assert.Equal(validId.ToString(), Assert.Single(snapshot.Derivations).GrantId);
        // Ticket 212 slice 3: BOTH readers now see all three. The EF rebuild used to drop a revoked grant's
        // row entirely, so on the production path a revocation was indistinguishable from an absence and the
        // revocation counterfactual was silent there; the row is now indexed with its window truncated at
        // the revocation instant, which is what AccessGrant.IsActiveAt already says.
        AuthorizationExclusionReason[] expected =
        [
            AuthorizationExclusionReason.ValidityLapsed,
            AuthorizationExclusionReason.NotYetValid,
            AuthorizationExclusionReason.GrantRevoked,
        ];
        Assert.Equal(expected.Order(), snapshot.Excluded.Select(item => item.Reason).Distinct().Order());
        Assert.All(snapshot.Excluded, item => Assert.NotEqual(validId.ToString(), item.Binding.GrantId));
    }

    /// <summary>
    /// Ticket 212 slice 3 — the same act, the same grants, decided through BOTH production readers with a
    /// revoked grant on record: the projected evidence and the counterfactual must be the same words. The EF
    /// closure index is the reader the node host actually resolves, so a difference here is a difference the
    /// production answer would have had.
    /// </summary>
    [Fact]
    public async Task ARevokedGrant_ReadsTheSameThroughBothProductionReaders()
    {
        var vocabulary = new InMemoryRoleVocabulary(
        [
            AccessGrantAuthorizationSeed.MemberDefinition,
            RoleDefinition.CreatePackageRole(
                RoleDefinitionId.New(), OldDefinitionRole.Name, "Old filter role", "filter-tests"),
        ]);
        var revokedId = GrantId.New();

        await using var store = await SearchTestStore.CreateAsync();
        var efGrants = new NodeEfGrantStore(store.Factory);
        var efConfiguration = new NodeEfAuthorizationConfigurationStore(store.Factory, vocabulary);
        await InstallDefinitionsAsync(efConfiguration, efConfiguration, vocabulary);
        await AppendRevokedOnlyAsync(efGrants, revokedId);
        var efDecision = await Decide(new NodeEfAuthorizationClosureReader(store.Factory), efConfiguration);

        var memoryGrants = TestInMemoryAuthorizationStores.GrantStore();
        var memoryConfiguration = TestInMemoryAuthorizationStores.ConfigurationStore();
        await InstallDefinitionsAsync(memoryConfiguration, memoryConfiguration, vocabulary);
        await AppendRevokedOnlyAsync(memoryGrants, revokedId);
        var joinedDecision = await Decide(
            new DefinitionJoinedAuthorizationReader(memoryGrants, memoryConfiguration), memoryConfiguration);

        Assert.Equal(AuthorizationVerdict.Denied, efDecision.Verdict);
        Assert.Equal(joinedDecision.Verdict, efDecision.Verdict);
        // The same four steps, fact for fact — the excluded binding, its in-force fact and the refusal shape.
        Assert.Equal(
            joinedDecision.Evidence.Project().Select(step => (step.Ordinal, step.Stage, string.Join("|", step.Facts))),
            efDecision.Evidence.Project().Select(step => (step.Ordinal, step.Stage, string.Join("|", step.Facts))));
        // And the same counterfactual: a revocation, named, on both.
        var efCounterfactual = AuthorizationCounterfactual.From(efDecision.Evidence);
        Assert.Equal(AuthorizationCounterfactualKind.GrantRevocation, efCounterfactual.Kind);
        Assert.Equal(AuthorizationCounterfactual.TowardAllow, efCounterfactual.Direction);
        Assert.Equal(AuthorizationCounterfactual.From(joinedDecision.Evidence), efCounterfactual);
    }

    private static ValueTask<AuthorizationDecision> Decide(
        IAuthorizationClosureSnapshotReader reader, IAuthorizationDefinitionAtomReader definitions) =>
        new AuthorizationGate(reader, new EmptyRecordStandingResolver(), definitions).DecideAsync(Request());

    /// <summary>One grant covering the act, revoked before the decided instant, and nothing else.</summary>
    private static async Task AppendRevokedOnlyAsync(IGrantStore grants, GrantId revokedId)
    {
        await grants.AppendAsync(Tenant, Grant(revokedId, Tenant, Principal, Member, "/records/a"));
        await grants.RevokeAsync(Tenant, revokedId,
            new GrantRevocation(Admin, At.AddMinutes(-1), new GrantReason(GrantReasonCodes.RevocationOffboarding)));
    }

    /// <summary>One effective grant and one grant per exclusion reason, all covering the same act.</summary>
    private static async Task AppendExclusionCasesAsync(IGrantStore grants, GrantId validId)
    {
        await grants.AppendAsync(Tenant, Grant(validId, Tenant, Principal, Member, "/records/a"));
        await grants.AppendAsync(Tenant, Grant(GrantId.New(), Tenant, Principal, Member, "/records/a",
            validUntil: At.AddHours(-1)));
        await grants.AppendAsync(Tenant, Grant(GrantId.New(), Tenant, Principal, Member, "/records/a",
            validFrom: At.AddMinutes(1)));
        var revokedId = GrantId.New();
        await grants.AppendAsync(Tenant, Grant(revokedId, Tenant, Principal, Member, "/records/a"));
        await grants.RevokeAsync(Tenant, revokedId,
            new GrantRevocation(Admin, At.AddMinutes(-1), new GrantReason(GrantReasonCodes.RevocationOffboarding)));
    }

    private static async Task InstallDefinitionsAsync(
        IAuthorizationConfigurationStore store,
        AuthorizationConfigurationStateReader states,
        IRoleVocabularyReader vocabulary)
    {
        var writer = new AuthorizationDefinitionWriter(
            store, states, new AuthorizationDefinitionAdmission(vocabulary),
            new AuthorizationCapabilityBindingAdmission(), TestAuthorization.AllowGate(), TestInMemoryAuthorizationStores.GrantStore());
        await writer.WriteAsync(new InstallAuthorizationDefinition(Definition(Guid.NewGuid(), 1, Member)));
        var oldId = Guid.NewGuid();
        await writer.WriteAsync(new InstallAuthorizationDefinition(Definition(oldId, 1, OldDefinitionRole)));
        await writer.WriteAsync(new ReplaceAuthorizationDefinition(Definition(oldId, 2, null)));
    }

    private static AuthorizationCapabilityDefinition Definition(Guid id, long revision, RoleReference? role) =>
        new(new AuthorizationCapabilityDefinitionId(id), role == Member ? AccessGrantAuthorizationSeed.PackageId : "filter-tests", revision,
            AuthorizationOperation.Parse("records:write"), PermissionAtom.Parse("records:write@/"),
            role is null ? RoleBindingSet.Empty : RoleBindingSet.Of(role.Value));

    private static async Task<GrantId> AppendNearMatchesAsync(
        IGrantStore grants, GrantId validId, GrantId staleId)
    {
        await grants.AppendAsync(Tenant, Grant(validId, Tenant, Principal, Member, "/records/a"));
        await grants.AppendAsync(OtherTenant, Grant(GrantId.New(), OtherTenant, Principal, Member, "/records/a"));
        await grants.AppendAsync(Tenant, Grant(GrantId.New(), Tenant, OtherPrincipal, Member, "/records/a"));
        await grants.AppendAsync(Tenant, Grant(GrantId.New(), Tenant, Principal, Member, "/records/b"));
        await grants.AppendAsync(Tenant, Grant(GrantId.New(), Tenant, Principal, Member, "/records/a",
            validFrom: At.AddMinutes(1)));
        await grants.AppendAsync(Tenant, Grant(GrantId.New(), Tenant, Principal, Member, "/records/a",
            validUntil: At));
        var revokedId = GrantId.New();
        await grants.AppendAsync(Tenant, Grant(revokedId, Tenant, Principal, Member, "/records/a"));
        await grants.AppendAsync(Tenant, Grant(GrantId.New(), Tenant, Principal, Member, "/records/a",
            grantedAt: At.AddMinutes(1)));
        await grants.AppendAsync(Tenant, Grant(GrantId.New(), Tenant, Principal, OldDefinitionRole, "/records/a"));
        await grants.AppendAsync(Tenant, Grant(staleId, Tenant, Principal, Member, "/records/a"));
        return revokedId;
    }

    private static AccessGrant Grant(
        GrantId id,
        TenantId tenant,
        ActorId principal,
        RoleReference role,
        string scope,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null,
        DateTimeOffset? grantedAt = null) => new(
            id, tenant, principal, role, ScopeExpression.Parse(scope), GrantResidency.Cache,
            new GrantValidity(validFrom ?? At.AddHours(-2), validUntil ?? At.AddHours(2)),
            GranterKind.Person, Admin, grantedAt ?? At.AddHours(-1),
            new GrantProvenance(GrantSourceKind.Manual, new GrantReason(GrantReasonCodes.Manual), Admin),
            At.AddHours(-1));

    private static AuthorizationGateRequest Request() => new(
        PermissionAtom.Parse("records:write@/records/a"), Principal, Tenant,
        new AuthorizationTarget("record", "a", ScopeExpression.Parse("/records/a")), At);

    private sealed class StaleListGrantStore(IGrantStore inner, GrantId staleId) : IGrantStore
    {
        public Task<AccessGrant> AppendAsync(TenantId tenantId, AccessGrant grant, string? sourceReference = null, CancellationToken ct = default) =>
            inner.AppendAsync(tenantId, grant, sourceReference, ct);
        public Task<AccessGrant?> FindAsync(TenantId tenantId, GrantId grantId, CancellationToken ct = default) =>
            inner.FindAsync(tenantId, grantId, ct);
        public Task<VersionedAccessGrant?> FindVersionedAsync(TenantId tenantId, GrantId grantId, CancellationToken ct = default) =>
            inner.FindVersionedAsync(tenantId, grantId, ct);
        public Task<AccessGrant?> FindBySourceReferenceAsync(TenantId tenantId, string sourceReference, CancellationToken ct = default) =>
            inner.FindBySourceReferenceAsync(tenantId, sourceReference, ct);
        public Task<IReadOnlyList<AccessGrant>> FindByPrincipalAsync(TenantId tenantId, ActorId principal, CancellationToken ct = default) =>
            inner.FindByPrincipalAsync(tenantId, principal, ct);
        public async Task<IReadOnlyList<VersionedAccessGrant>> FindVersionedByPrincipalAsync(
            TenantId tenantId, ActorId principal, CancellationToken ct = default) =>
            (await inner.FindVersionedByPrincipalAsync(tenantId, principal, ct))
                .Select(item => item.Grant.GrantId == staleId
                    ? item with { OwnerVersion = item.OwnerVersion + 1 }
                    : item)
                .ToArray();
        public Task<IReadOnlyList<AccessGrant>> SnapshotAsync(TenantId tenantId, CancellationToken ct = default) =>
            inner.SnapshotAsync(tenantId, ct);
        public Task<bool> HasAdministratorGrantEverAsync(CancellationToken ct = default) =>
            inner.HasAdministratorGrantEverAsync(ct);
        public Task<AccessGrant?> ChangeValidityAsync(TenantId tenantId, GrantId grantId, GrantValidity validity, ActorId changedBy, GrantReason reason, CancellationToken ct = default) =>
            inner.ChangeValidityAsync(tenantId, grantId, validity, changedBy, reason, ct);
        public Task<AccessGrant?> RecordReviewAsync(TenantId tenantId, GrantId grantId, DateTimeOffset reviewedAt, ActorId reviewedBy, CancellationToken ct = default) =>
            inner.RecordReviewAsync(tenantId, grantId, reviewedAt, reviewedBy, ct);
        public Task<AccessGrant?> RevokeAsync(TenantId tenantId, GrantId grantId, GrantRevocation revocation, CancellationToken ct = default) =>
            inner.RevokeAsync(tenantId, grantId, revocation, ct);
        public Task<AdministratorHandover?> HandoverAdministratorAsync(
            TenantId tenantId, GrantId currentGrantId, AccessGrant successor, GrantRevocation revocation, CancellationToken ct = default) =>
            inner.HandoverAdministratorAsync(tenantId, currentGrantId, successor, revocation, ct);
    }
}
