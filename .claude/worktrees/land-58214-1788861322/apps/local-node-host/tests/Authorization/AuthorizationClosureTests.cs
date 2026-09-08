using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationClosureTests
{
    private static readonly TenantId Tenant = TenantId.FromString("tenant-closure");
    private static readonly ActorId Alice = new("alice");
    private static readonly ActorId Bob = new("bob");
    private static readonly ActorId Admin = new("admin");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
    private static readonly RoleReference Member = AccessGrantAuthorizationSeed.MemberRole;

    [Fact]
    public async Task ClosureSnapshot_ReturnsEveryGrantRoleDefinitionAndOwnerVersionDerivation()
    {
        await using var h = await Harness.CreateAsync();
        var definition = await h.InstallAsync("records:write", "/", Member);
        var grant = await h.AppendAsync(Alice, Member, "/records/a");
        await h.Grants.RecordReviewAsync(Tenant, grant.GrantId, Now, Admin);
        var request = new AuthorizationGateRequest(
            PermissionAtom.Parse("records:write@/records/a"), Alice, Tenant,
            new AuthorizationTarget("record", "a", ScopeExpression.Parse("/records/a")), Now);

        var snapshot = await h.Reader.ReadAsync(request);

        var derivation = Assert.Single(snapshot.Derivations);
        Assert.Equal(grant.GrantId.ToString(), derivation.GrantId);
        Assert.Equal(2, derivation.GrantOwnerVersion);
        Assert.Equal(definition.DefinitionId.Value.ToString(), derivation.DefinitionId);
        Assert.Equal(Member, derivation.Role);
        Assert.Equal("/records/a", derivation.GrantScope.Value);
        Assert.Equal(grant.Validity.ValidFrom, derivation.ValidFrom);
        Assert.Equal(grant.Validity.ValidTo, derivation.ValidUntil);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task ClosureSnapshot_GrantedAtBoundaryIsIndependentOfValidity(
        int minutesFromGrantedAt, bool expected)
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:write", "/", Member);
        await h.AppendAsync(Alice, Member, "/records/a",
            validFrom: Now.AddHours(-2), grantedAt: Now);
        var at = Now.AddMinutes(minutesFromGrantedAt);
        var request = new AuthorizationGateRequest(
            PermissionAtom.Parse("records:write@/records/a"), Alice, Tenant,
            new AuthorizationTarget("record", "a", ScopeExpression.Parse("/records/a")), at);

        var snapshot = await h.Reader.ReadAsync(request);

        Assert.Equal(expected, snapshot.Derivations.Count == 1);
    }

    [Fact]
    public async Task ClosureSnapshot_RejectsStaleMaterializedGrantOwnerVersion()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:write", "/", Member);
        await h.AppendAsync(Alice, Member, "/records/a");
        var request = new AuthorizationGateRequest(
            PermissionAtom.Parse("records:write@/records/a"), Alice, Tenant,
            new AuthorizationTarget("record", "a", ScopeExpression.Parse("/records/a")), Now);
        Assert.Single((await h.Reader.ReadAsync(request)).Derivations);
        await using (var db = h.Store.CreateContext())
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE authorization_principal_atom_closure SET grant_owner_version = 0;");

        Assert.Empty((await h.Reader.ReadAsync(request)).Derivations);
    }

    [Fact]
    public async Task JoinedSnapshot_RecordsAdvancedInMemoryGrantOwnerVersion()
    {
        var grants = TestInMemoryAuthorizationStores.GrantStore();
        var configuration = TestInMemoryAuthorizationStores.ConfigurationStore();
        var vocabulary = new InMemoryRoleVocabulary([AccessGrantAuthorizationSeed.MemberDefinition]);
        var writer = new AuthorizationDefinitionWriter(
            configuration, configuration, new AuthorizationDefinitionAdmission(vocabulary),
            new AuthorizationCapabilityBindingAdmission(), TestAuthorization.AllowGate(), grants);
        var definition = new AuthorizationCapabilityDefinition(
            new AuthorizationCapabilityDefinitionId(Guid.NewGuid()),
            AccessGrantAuthorizationSeed.PackageId, 1,
            AuthorizationOperation.Parse("records:write"),
            PermissionAtom.Parse("records:write@/"), RoleBindingSet.Of(Member));
        await writer.WriteAsync(new InstallAuthorizationDefinition(definition));
        var grant = new AccessGrant(
            GrantId.New(), Tenant, Alice, Member, ScopeExpression.Parse("/records/a"), GrantResidency.Cache,
            new GrantValidity(Now.AddHours(-2), Now.AddHours(2)), GranterKind.Person, Admin,
            Now.AddHours(-1), new GrantProvenance(GrantSourceKind.Manual,
                new GrantReason(GrantReasonCodes.Manual), Admin), Now.AddHours(-1));
        await grants.AppendAsync(Tenant, grant);
        var reader = new DefinitionJoinedAuthorizationReader(grants, configuration);
        var request = new AuthorizationGateRequest(
            PermissionAtom.Parse("records:write@/records/a"), Alice, Tenant,
            new AuthorizationTarget("record", "a", ScopeExpression.Parse("/records/a")), Now);

        await grants.RecordReviewAsync(Tenant, grant.GrantId, Now, Admin);
        Assert.Equal(2, Assert.Single((await reader.ReadAsync(request)).Derivations).GrantOwnerVersion);

        await grants.ChangeValidityAsync(Tenant, grant.GrantId,
            new GrantValidity(Now.AddHours(-1), Now.AddHours(1)), Admin,
            new GrantReason(GrantReasonCodes.Manual));
        Assert.Equal(3, Assert.Single((await reader.ReadAsync(request)).Derivations).GrantOwnerVersion);
    }

    [Fact]
    public async Task ClosureBuild_JoinsDefinitionBindingAndActiveGrant()
    {
        await using var h = await Harness.CreateAsync();
        var definition = await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        var grant = await h.AppendAsync(Alice, Member, "/records/a");

        var permissions = await h.Reader.UserPermissionsAsync(Tenant, Alice, Now);

        Assert.True(permissions.Covers(PermissionAtom.Parse("records:read@/records/a")));
        await using var db = h.Store.CreateContext();
        var row = Assert.Single(db.AuthorizationClosureEntries);
        Assert.Equal(grant.GrantId.ToString(), row.GrantId);
        Assert.Equal(definition.DefinitionId.Value.ToString(), row.DefinitionId);
        Assert.Equal(Member.Vocabulary, row.RoleVocabulary);
        Assert.Equal(Member.Name, row.RoleName);
    }

    [Fact]
    public async Task ClosureBuild_UnreferencedRoleMaterializesNothing()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/", Member);
        await h.AppendAsync(Alice, RoleReference.Administrator, "/");

        Assert.Equal(PermissionAtomSet.Empty, await h.Reader.UserPermissionsAsync(Tenant, Alice, Now));
        await using var db = h.Store.CreateContext();
        Assert.Empty(db.AuthorizationClosureEntries);
    }

    [Fact]
    public async Task ClosureBuild_OutOfScopeGrantMaterializesNothing()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/north", Member, RoleReference.Administrator);
        await h.AppendAsync(Alice, Member, "/south");

        Assert.Equal(PermissionAtomSet.Empty, await h.Reader.UserPermissionsAsync(Tenant, Alice, Now));
        await using var db = h.Store.CreateContext();
        Assert.Empty(db.AuthorizationClosureEntries);
    }

    [Fact]
    public async Task ClosureBuild_MultipleGrantsUnionWithoutDeny()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        var scopes = new[] { "/records/a", "/records/b", "/records/c" };

        for (var mask = 0; mask < 8; mask++)
        {
            var subject = new ActorId($"subject-{mask}");
            for (var bit = 0; bit < scopes.Length; bit++)
                if ((mask & (1 << bit)) != 0)
                    await h.AppendAsync(subject, Member, scopes[bit]);
        }

        var oracle = new DefinitionJoinedAuthorizationReader(h.Grants, h.Configuration);
        for (var mask = 0; mask < 8; mask++)
        {
            var subject = new ActorId($"subject-{mask}");
            Assert.Equal(
                await oracle.UserPermissionsAsync(Tenant, subject, Now),
                await h.Reader.UserPermissionsAsync(Tenant, subject, Now));
        }
    }

    [Fact]
    public async Task FreshSecondReadDoesNotRewriteClosureRows()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        await h.AppendAsync(Alice, Member, "/records/a");
        await using (var db = h.Store.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TABLE closure_write_counter (writes INTEGER NOT NULL);");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO closure_write_counter (writes) VALUES (0);");
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER count_closure_insert
                AFTER INSERT ON authorization_principal_atom_closure
                BEGIN
                    UPDATE closure_write_counter SET writes = writes + 1;
                END;
                """);
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER count_closure_delete
                AFTER DELETE ON authorization_principal_atom_closure
                BEGIN
                    UPDATE closure_write_counter SET writes = writes + 1;
                END;
                """);
        }

        await h.Reader.UserPermissionsAsync(Tenant, Alice, Now);
        var afterRebuild = await h.ClosureWriteCountAsync();
        Assert.True(afterRebuild > 0);

        await h.Reader.UserPermissionsAsync(Tenant, Alice, Now);

        Assert.Equal(afterRebuild, await h.ClosureWriteCountAsync());
    }

    [Fact]
    public async Task NextReadAfterGrantCreateOrRevokeRebuildsTenant()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        var first = await h.AppendAsync(Alice, Member, "/records/a");
        Assert.True((await h.Reader.UserPermissionsAsync(Tenant, Alice, Now)).Covers(
            PermissionAtom.Parse("records:read@/records/a")));

        await h.AppendAsync(Alice, Member, "/records/b");
        Assert.True((await h.Reader.UserPermissionsAsync(Tenant, Alice, Now)).Covers(
            PermissionAtom.Parse("records:read@/records/b")));

        await h.Grants.RevokeAsync(Tenant, first.GrantId,
            new GrantRevocation(Admin, Now, new GrantReason(GrantReasonCodes.RevocationOffboarding)));
        var afterRevoke = await h.Reader.UserPermissionsAsync(Tenant, Alice, Now);
        Assert.False(afterRevoke.Covers(PermissionAtom.Parse("records:read@/records/a")));
        Assert.True(afterRevoke.Covers(PermissionAtom.Parse("records:read@/records/b")));
    }

    [Fact]
    public async Task NextReadAfterGrantValidityChangeRebuildsTenant()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        var grant = await h.AppendAsync(Alice, Member, "/");
        Assert.NotEqual(PermissionAtomSet.Empty, await h.Reader.UserPermissionsAsync(Tenant, Alice, Now));

        await h.Grants.ChangeValidityAsync(Tenant, grant.GrantId,
            new GrantValidity(Now.AddHours(1), Now.AddHours(2)), Admin,
            new GrantReason(GrantReasonCodes.Manual));

        Assert.Equal(PermissionAtomSet.Empty, await h.Reader.UserPermissionsAsync(Tenant, Alice, Now));
    }

    [Fact]
    public async Task FutureDatedClosureRowBecomesEffectiveWithoutScheduledMutation()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        await h.AppendAsync(Alice, Member, "/", validFrom: Now.AddHours(1));

        Assert.Equal(PermissionAtomSet.Empty, await h.Reader.UserPermissionsAsync(Tenant, Alice, Now));
        await using (var db = h.Store.CreateContext()) Assert.Single(db.AuthorizationClosureEntries);
        Assert.NotEqual(PermissionAtomSet.Empty,
            await h.Reader.UserPermissionsAsync(Tenant, Alice, Now.AddHours(1)));
    }

    [Fact]
    public async Task NextReadAfterTenantBindingNarrowingRebuildsTenant()
    {
        await using var h = await Harness.CreateAsync();
        var definition = await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        await h.AppendAsync(Alice, Member, "/");
        Assert.NotEqual(PermissionAtomSet.Empty, await h.Reader.UserPermissionsAsync(Tenant, Alice, Now));

        await h.Writer.WriteAsync(new NarrowCapabilityRoleBinding(
            Tenant, definition.DefinitionId, RoleBindingSet.Of(RoleReference.Administrator),
            Admin, Now, new BindingChangeReason("tenant-policy")));

        Assert.Equal(PermissionAtomSet.Empty, await h.Reader.UserPermissionsAsync(Tenant, Alice, Now));
    }

    [Fact]
    public async Task NextReadAfterDefinitionInstallOrReplacementRebuildsAgainstCatalogVersion()
    {
        await using var h = await Harness.CreateAsync();
        await h.AppendAsync(Alice, Member, "/");
        Assert.Equal(PermissionAtomSet.Empty, await h.Reader.UserPermissionsAsync(Tenant, Alice, Now));

        var definition = await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        Assert.NotEqual(PermissionAtomSet.Empty, await h.Reader.UserPermissionsAsync(Tenant, Alice, Now));

        await h.Writer.WriteAsync(new ReplaceAuthorizationDefinition(definition with
        {
            Revision = 2,
            OfferedRoles = RoleBindingSet.Of(RoleReference.Administrator),
        }));
        Assert.Equal(PermissionAtomSet.Empty, await h.Reader.UserPermissionsAsync(Tenant, Alice, Now));
    }

    [Fact]
    public async Task ConcurrentReconcilersPublishOneCompleteVersion()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        await h.AppendAsync(Alice, Member, "/records/a");
        await h.AppendAsync(Alice, Member, "/records/b");

        var reads = await Task.WhenAll(
            h.Reader.UserPermissionsAsync(Tenant, Alice, Now).AsTask(),
            h.Reader.UserPermissionsAsync(Tenant, Alice, Now).AsTask());

        Assert.All(reads, permissions => Assert.Equal(2, permissions.Atoms.Count));
        await using var db = h.Store.CreateContext();
        Assert.Equal(2, db.AuthorizationClosureEntries.Count());
        var state = Assert.Single(db.AuthorizationClosureStates);
        Assert.Equal(db.AuthorizationCatalogVersions.Single().Version, state.BuiltCatalogVersion);
        Assert.Equal(db.AuthorizationTenantVersions.Single().Version, state.BuiltTenantVersion);
    }

    [Fact]
    public async Task WritersPausedAfterVersionObservationPublishOnlyCompleteGenerations()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        await h.AppendAsync(Alice, Member, "/records/a");
        Assert.Single((await h.Reader.UserPermissionsAsync(Tenant, Alice, Now)).Atoms);
        await h.AppendAsync(Alice, Member, "/records/b");

        var barrier = new ReconcileBarrier();
        var reader = h.ReaderFor(new AuthorizationClosureReconciler(
            barrier.PauseAsync,
            static (_, _) => Task.CompletedTask));
        var reconcilingRead = reader.UserPermissionsAsync(Tenant, Alice, Now).AsTask();
        await barrier.WaitUntilReachedAsync("Reconcile did not pause after observing source versions.");

        var grantWriterStarted = NewSignal();
        var definitionWriterStarted = NewSignal();
        var grantWriter = Task.Run(async () =>
        {
            grantWriterStarted.TrySetResult();
            return await h.AppendAsync(Alice, Member, "/records/c");
        });
        var definitionWriter = Task.Run(async () =>
        {
            definitionWriterStarted.TrySetResult();
            return await h.InstallAsync("records:write", "/", Member);
        });
        await WaitBoundedAsync(
            Task.WhenAll(grantWriterStarted.Task, definitionWriterStarted.Task),
            "Both source writers did not start while reconcile was paused.");
        Assert.False(grantWriter.IsCompleted, "Grant writer escaped the held reconcile fence.");
        Assert.False(definitionWriter.IsCompleted, "Definition writer escaped the held reconcile fence.");

        barrier.Release();
        var observedPermissions = await WaitBoundedAsync(
            reconcilingRead,
            "The version-observing reconcile did not complete after its barrier was released.");
        Assert.Equal(2, observedPermissions.Atoms.Count);
        await WaitBoundedAsync(
            Task.WhenAll(grantWriter, definitionWriter),
            "The blocked source writers did not complete after reconcile released the fence.");

        var oldGeneration = await h.ReadCommittedGenerationAsync();
        Assert.Equal((1L, 2L), (oldGeneration.BuiltCatalog, oldGeneration.BuiltTenant));
        Assert.Equal((2L, 3L), (oldGeneration.Catalog, oldGeneration.TenantVersion));
        Assert.Equal(
            ["records:read@/records/a", "records:read@/records/b"],
            oldGeneration.Rows);

        var newPermissions = await h.Reader.UserPermissionsAsync(Tenant, Alice, Now);
        Assert.Equal(6, newPermissions.Atoms.Count);
        var newGeneration = await h.ReadCommittedGenerationAsync();
        Assert.Equal(
            (newGeneration.Catalog, newGeneration.TenantVersion),
            (newGeneration.BuiltCatalog, newGeneration.BuiltTenant));
        Assert.Equal(
            [
                "records:read@/records/a", "records:read@/records/b", "records:read@/records/c",
                "records:write@/records/a", "records:write@/records/b", "records:write@/records/c",
            ],
            newGeneration.Rows);
    }

    [Fact]
    public async Task ReaderPausedAfterRowsNeverObservesNewStampBeforeCompleteRows()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        await h.AppendAsync(Alice, Member, "/records/a");
        Assert.Single((await h.Reader.UserPermissionsAsync(Tenant, Alice, Now)).Atoms);
        await h.AppendAsync(Alice, Member, "/records/b");

        ClosureGeneration? inFlight = null;
        var barrier = new ReconcileBarrier();
        var reader = h.ReaderFor(new AuthorizationClosureReconciler(
            static () => Task.CompletedTask,
            async (connection, transaction) =>
            {
                inFlight = await ReadGenerationAsync(connection, transaction);
                await barrier.PauseAsync();
            }));
        var reconcilingRead = reader.UserPermissionsAsync(Tenant, Alice, Now).AsTask();
        await barrier.WaitUntilReachedAsync("Reconcile did not pause after inserting closure rows.");

        var committedWhilePaused = await WaitBoundedAsync(
            h.ReadCommittedGenerationAsync(),
            "A separate SQLite reader could not inspect the committed generation while reconcile was paused.");
        var concurrentReaderStarted = NewSignal();
        var concurrentRead = Task.Run(async () =>
        {
            concurrentReaderStarted.TrySetResult();
            return await h.Reader.UserPermissionsAsync(Tenant, Alice, Now);
        });
        await WaitBoundedAsync(
            concurrentReaderStarted.Task,
            "The concurrent fenced reader did not start while row publication was paused.");
        Assert.False(concurrentRead.IsCompleted, "Concurrent reader escaped the held reconcile fence.");

        barrier.Release();
        await WaitBoundedAsync(
            reconcilingRead,
            "The row-publishing reconcile did not complete after its barrier was released.");
        var concurrentPermissions = await WaitBoundedAsync(
            concurrentRead,
            "The concurrent reader did not complete after the full row set and stamp committed.");

        Assert.Equal((1L, 1L), (committedWhilePaused.BuiltCatalog, committedWhilePaused.BuiltTenant));
        Assert.Equal(["records:read@/records/a"], committedWhilePaused.Rows);
        var staged = Assert.IsType<ClosureGeneration>(inFlight);
        Assert.Equal((1L, 1L), (staged.BuiltCatalog, staged.BuiltTenant));
        Assert.Equal((1L, 2L), (staged.Catalog, staged.TenantVersion));
        Assert.Equal(
            ["records:read@/records/a", "records:read@/records/b"],
            staged.Rows);
        Assert.Equal(2, concurrentPermissions.Atoms.Count);

        var published = await h.ReadCommittedGenerationAsync();
        Assert.Equal((1L, 2L), (published.BuiltCatalog, published.BuiltTenant));
        Assert.Equal(published.Rows, staged.Rows);
    }

    [Fact]
    public async Task AssignedUsersAsync_UsesScopeContainmentAndIndexShape()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        await h.AppendAsync(Alice, Member, "/records/a");
        await h.AppendAsync(Bob, Member, "/records/b");

        Assert.Equal([Alice], await h.Reader.AssignedUsersAsync(
            Tenant, PermissionAtom.Parse("records:read@/records/a/child"), Now));

        await using var db = h.Store.CreateContext();
        var names = await db.Database.SqlQueryRaw<string>(
            "SELECT name AS Value FROM pragma_index_list('authorization_principal_atom_closure')")
            .ToArrayAsync();
        Assert.Contains("IX_authorization_principal_atom_closure_tenant_operation_scope_principal", names);
    }

    [Fact]
    public async Task RolePermissionsAsync_DerivesFromDefinitionsNotRoleRow()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/records/a", Member, RoleReference.Administrator);

        var permissions = await h.Reader.RolePermissionsAsync(Tenant, Member);

        Assert.True(permissions.Covers(PermissionAtom.Parse("records:read@/records/a")));
        await using var db = h.Store.CreateContext();
        Assert.Empty(db.Grants);
        Assert.DoesNotContain(db.Model.FindEntityType(typeof(AuthorizationRoleRow))!.GetProperties(),
            property => property.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Atom", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RealProjection_RecordsParentAtomAuthorizesWholeRecordIndex()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/records", Member, RoleReference.Administrator);
        await h.AppendAsync(Alice, Member, "/");
        await h.IndexAsync("a");
        await h.IndexAsync("b");

        var hits = await new NodeSearchReadService(h.Store.Factory, new ClosureAuthorizedRecordSetProjection())
            .SearchAsync(Tenant, Alice, "projection", Now);

        Assert.Equal(["a", "b"], hits.Select(hit => hit.RecordId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RealProjection_DeeperChildScopeMapsToOwningRecord()
    {
        await using var h = await Harness.CreateAsync();
        await h.InstallAsync("records:read", "/", Member, RoleReference.Administrator);
        await h.AppendAsync(Alice, Member, "/records/a/child");
        await h.IndexAsync("a");
        await h.IndexAsync("b");

        var hits = await new NodeSearchReadService(h.Store.Factory, new ClosureAuthorizedRecordSetProjection())
            .SearchAsync(Tenant, Alice, "projection", Now);

        Assert.Equal(["a"], hits.Select(hit => hit.RecordId));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(SearchTestStore store)
        {
            Store = store;
            Grants = new NodeEfGrantStore(store.Factory);
            var vocabulary = new InMemoryRoleVocabulary([AccessGrantAuthorizationSeed.MemberDefinition]);
            Configuration = new NodeEfAuthorizationConfigurationStore(store.Factory, vocabulary);
            Writer = new AuthorizationDefinitionWriter(Configuration, Configuration,
                new AuthorizationDefinitionAdmission(vocabulary), new AuthorizationCapabilityBindingAdmission(),
                TestAuthorization.AllowGate(), Grants);
            Reader = new NodeEfAuthorizationClosureReader(store.Factory);
        }

        public SearchTestStore Store { get; }
        public NodeEfGrantStore Grants { get; }
        public NodeEfAuthorizationConfigurationStore Configuration { get; }
        public AuthorizationDefinitionWriter Writer { get; }
        public NodeEfAuthorizationClosureReader Reader { get; }

        public NodeEfAuthorizationClosureReader ReaderFor(AuthorizationClosureReconciler reconciler) =>
            new(Store.Factory, reconciler);

        public static async Task<Harness> CreateAsync() => new(await SearchTestStore.CreateAsync());

        public async Task<AuthorizationCapabilityDefinition> InstallAsync(
            string operationValue, string scope, params RoleReference[] roles)
        {
            var operation = AuthorizationOperation.Parse(operationValue);
            var definition = new AuthorizationCapabilityDefinition(
                new AuthorizationCapabilityDefinitionId(Guid.NewGuid()),
                AccessGrantAuthorizationSeed.PackageId, 1, operation,
                new PermissionAtom(operation, ScopeExpression.Parse(scope)), RoleBindingSet.From(roles));
            await Writer.WriteAsync(new InstallAuthorizationDefinition(definition));
            return definition;
        }

        public Task<AccessGrant> AppendAsync(
            ActorId subject, RoleReference role, string scope, DateTimeOffset? validFrom = null,
            DateTimeOffset? grantedAt = null) =>
            Grants.AppendAsync(Tenant, new AccessGrant(
                GrantId.New(), Tenant, subject, role, ScopeExpression.Parse(scope), GrantResidency.Cache,
                new GrantValidity(validFrom ?? Now.AddHours(-1), Now.AddDays(1)), GranterKind.Person,
                Admin, grantedAt ?? Now.AddHours(-1), new GrantProvenance(GrantSourceKind.Manual,
                    new GrantReason(GrantReasonCodes.Manual), Admin), Now.AddHours(-1)), Guid.NewGuid().ToString());

        public Task IndexAsync(string recordId) => new NodeSearchIndexer(Store.Factory).IndexNodeAsync(new SearchNodeRow
        {
            RecordId = recordId,
            TenantId = Tenant.Value,
            NodeType = "test",
            Title = $"projection record {recordId}",
            Body = "shared projection search token",
            Residency = SearchResidency.Cache,
        });

        public async Task<long> ClosureWriteCountAsync()
        {
            await using var db = Store.CreateContext();
            return await db.Database.SqlQueryRaw<long>(
                "SELECT writes AS Value FROM closure_write_counter").SingleAsync();
        }

        public async Task<ClosureGeneration> ReadCommittedGenerationAsync()
        {
            await using var context = Store.CreateContext();
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            await using var transaction = connection.BeginTransaction(
                System.Data.IsolationLevel.Serializable,
                deferred: true);
            var generation = await ReadGenerationAsync(connection, transaction);
            await transaction.CommitAsync();
            return generation;
        }

        public ValueTask DisposeAsync() => Store.DisposeAsync();
    }

    private static async Task<ClosureGeneration> ReadGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        long catalog;
        long tenantVersion;
        long builtCatalog;
        long builtTenant;
        await using (var versions = connection.CreateCommand())
        {
            versions.Transaction = transaction;
            versions.CommandText = """
                SELECT
                    COALESCE((SELECT version FROM authorization_catalog_version WHERE id = 1), 0),
                    COALESCE((SELECT version FROM authorization_tenant_versions WHERE tenant_id = $tenant), 0),
                    COALESCE((SELECT built_catalog_version FROM authorization_closure_state WHERE tenant_id = $tenant), -1),
                    COALESCE((SELECT built_tenant_version FROM authorization_closure_state WHERE tenant_id = $tenant), -1);
                """;
            versions.Parameters.AddWithValue("$tenant", Tenant.Value);
            await using var reader = await versions.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            catalog = reader.GetInt64(0);
            tenantVersion = reader.GetInt64(1);
            builtCatalog = reader.GetInt64(2);
            builtTenant = reader.GetInt64(3);
        }

        var rows = new List<string>();
        await using (var closure = connection.CreateCommand())
        {
            closure.Transaction = transaction;
            closure.CommandText = """
                SELECT operation || '@' || scope_value
                FROM authorization_principal_atom_closure
                WHERE tenant_id = $tenant AND principal_id = $principal
                ORDER BY operation, scope_value;
                """;
            closure.Parameters.AddWithValue("$tenant", Tenant.Value);
            closure.Parameters.AddWithValue("$principal", Alice.Value);
            await using var reader = await closure.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        }

        return new ClosureGeneration(catalog, tenantVersion, builtCatalog, builtTenant, rows);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitBoundedAsync(Task task, string failureMessage)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(failureMessage);
        }
    }

    private static async Task<T> WaitBoundedAsync<T>(Task<T> task, string failureMessage)
    {
        await WaitBoundedAsync((Task)task, failureMessage);
        return await task;
    }

    private sealed class ReconcileBarrier
    {
        private readonly TaskCompletionSource _reached = NewSignal();
        private readonly TaskCompletionSource _release = NewSignal();

        public async Task PauseAsync()
        {
            _reached.TrySetResult();
            await _release.Task;
        }

        public Task WaitUntilReachedAsync(string failureMessage) =>
            WaitBoundedAsync(_reached.Task, failureMessage);

        public void Release() => _release.TrySetResult();
    }

    private sealed record ClosureGeneration(
        long Catalog,
        long TenantVersion,
        long BuiltCatalog,
        long BuiltTenant,
        IReadOnlyList<string> Rows);
}
