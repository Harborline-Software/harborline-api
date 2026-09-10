using System.Runtime.CompilerServices;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Calendar.Models;
using Harborline.Api.Blocks.Calendar.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.ArchTests;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Foundation.Recovery.Crypto;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

public sealed class AuthorizationSlice3AcceptanceTests
{
    private static readonly TenantId Tenant = new("tenant-slice-3");
    private static readonly DateTimeOffset At = new(2026, 9, 2, 16, 30, 0, TimeSpan.Zero);

    private static NodeTeamRoster DevelopmentIndexerRoster(NodePrincipalSigner signer) => new(
        MemberRoster.Genesis(
            Guid.Parse("29400000-0000-4000-8000-000000000031"),
            AccessGrantAuthorizationSeed.DevIndexerPrincipal,
            signer.Signer,
            new Ed25519Verifier(),
            DateTimeOffset.UnixEpoch,
            Guid.Parse("29400000-0000-4000-8000-000000000032")));

    [Fact]
    public async Task JournalPosting_DeniedBeforeBeginImmediateOrJournalMutation()
    {
        var accounts = Substitute.For<IAccountResolver>();
        var periods = Substitute.For<IPeriodResolver>();
        var store = Substitute.For<IJournalStore>();
        var service = new JournalPostingService(accounts, periods, store, TestAuthorization.Gate(false));

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            service.PostAsync(Draft(), TestAuthorization.Write(Tenant, at: At)));

        await accounts.DidNotReceiveWithAnyArgs().GetAsync(default);
        await periods.DidNotReceiveWithAnyArgs().ResolveAsync(default, default);
        await store.DidNotReceiveWithAnyArgs().FindBySourceReferenceAsync(default, default!);
        await store.DidNotReceiveWithAnyArgs().SaveAtomicAsync(default, default!, default!);
    }

    [Fact]
    public async Task JournalPosting_UsesOneInstantForPostAndSoftCloseDecisions()
    {
        var requests = new List<AuthorizationGateRequest>();
        var accounts = Substitute.For<IAccountResolver>();
        accounts.GetAsync(Arg.Any<GLAccountId>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<GLAccount?>(new GLAccount(
                call.Arg<GLAccountId>(), call.Arg<GLAccountId>().Value, "account", GLAccountType.Asset,
                ChartId: new ChartOfAccountsId("chart-1"))));
        var periods = Substitute.For<IPeriodResolver>();
        periods.ResolveAsync(Arg.Any<ChartOfAccountsId>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new IPeriodResolver.PeriodSnapshot("period-1", "chart-1", IPeriodResolver.Status.SoftClosed));
        var store = Substitute.For<IJournalStore>();
        var service = new JournalPostingService(accounts, periods, store,
            TestAuthorization.Gate(true, requests.Add));

        var result = await service.PostAsync(Draft() with { ChartId = new ChartOfAccountsId("chart-1") },
            TestAuthorization.Write(Tenant, at: At));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request => Assert.Equal(At, request.At));
        Assert.Equal(At, result.Entry!.PostedAtUtc!.Value.Value);
        await store.Received(1).SaveAtomicAsync(Tenant, Arg.Is<JournalEntry>(entry =>
            entry.PostedAtUtc!.Value.Value == At), Arg.Any<AuthorizationDecision>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task JournalPosting_CarriedPath_CreatesNoWriteContext_AndNeverCallsGate()
    {
        var gateCalls = 0;
        var accounts = Substitute.For<IAccountResolver>();
        accounts.GetAsync(Arg.Any<GLAccountId>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<GLAccount?>(new GLAccount(
                call.Arg<GLAccountId>(), call.Arg<GLAccountId>().Value, "account", GLAccountType.Asset,
                ChartId: new ChartOfAccountsId("chart-1"))));
        var periods = Substitute.For<IPeriodResolver>();
        periods.ResolveAsync(Arg.Any<ChartOfAccountsId>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new IPeriodResolver.PeriodSnapshot("period-1", "chart-1", IPeriodResolver.Status.SoftClosed));
        var staged = new List<JournalEntry>();
        var service = new JournalPostingService(
            accounts, periods, Substitute.For<IJournalStore>(),
            TestAuthorization.Gate(false, _ => gateCalls++));
        var entry = Draft() with { ChartId = new ChartOfAccountsId("chart-1") };
        var decision = TestAuthorization.AllowedDecision(
            Tenant, entry.Id.Value, "journal-entry", TeamRolePermissions.LedgerPost, at: At);

        var result = await service.PostAsync(
            entry, decision, decision.Request.Principal, At,
            (posted, _) => { staged.Add(posted); return Task.CompletedTask; });

        Assert.Equal(PostError.PeriodSoftClosed, result.Error);
        Assert.Empty(staged);
        Assert.Equal(0, gateCalls);
        var carried = typeof(JournalPostingService).GetMethods(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(method => method.Name == nameof(JournalPostingService.PostAsync) &&
                method.GetParameters().Length > 1 &&
                method.GetParameters()[1].ParameterType == typeof(AuthorizationDecision));
        var reachable = AuthorizationGateArchTests.ReachableMethodsWithinType(
            carried, typeof(JournalPostingService)).ToArray();
        var calls = reachable.SelectMany(AuthorizationGateArchTests.CalledMethods).ToArray();
        Assert.DoesNotContain(calls, method =>
            method is System.Reflection.ConstructorInfo &&
            method.DeclaringType == typeof(AuthorizationWriteContext));
        Assert.DoesNotContain(calls, method =>
            method.Name == nameof(AuthorizationGate.DecideAsync) &&
            method.DeclaringType == typeof(AuthorizationGate));
    }

    [Fact]
    public void NodeLedgerPostingEffect_CannotCallJournalStoreDirectly()
    {
        var source = Source("apps", "local-node-host", "Data", "Workflow", "NodeLedgerPostingEffect.cs");
        Assert.DoesNotContain("NodeEfJournalStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IJournalStore", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorizationDecision_HelperRejectsMismatchedOrDeniedReaction()
    {
        var matching = TestAuthorization.AllowedDecision(Tenant, "workflow-1");
        matching.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite), Tenant, "record", "workflow-1");

        var wrong = TestAuthorization.AllowedDecision(Tenant, "workflow-2");
        Assert.Throws<ArgumentException>(() => wrong.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite), Tenant, "record", "workflow-1"));
        Assert.Throws<ArgumentException>(() => matching.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.MembersManage), Tenant, "record", "workflow-1"));
        Assert.Throws<ArgumentException>(() => matching.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite), new TenantId("other-tenant"),
            "record", "workflow-1"));
        var denied = await TestAuthorization.Gate(false).DecideAsync(
            TestAuthorization.Write(Tenant, at: At).Request(
                AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite), "record", "workflow-1"));
        Assert.Throws<AuthorizationDeniedException>(() => denied.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite), Tenant, "record", "workflow-1"));
    }

    [Fact]
    public void WorkflowBroker_CarriedDecisionPathContainsNoGateCall()
    {
        var source = Source("packages", "blocks-workflow", "src", "durable", "WorkflowEffectBroker.cs");
        var synchronous = source[source.IndexOf("public WorkflowEffect BuildAutonomousEffect(", StringComparison.Ordinal)..
            source.IndexOf("public async ValueTask<WorkflowEffect> BuildAutonomousEffectAsync", StringComparison.Ordinal)];
        Assert.Contains("VerifyOriginatingDecision", synchronous, StringComparison.Ordinal);
        Assert.DoesNotContain("DecideAsync", synchronous, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentityAdministration_CombinedDenialSmokeTest()
    {
        var identityFactory = Substitute.For<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>();
        var sessionFactory = Substitute.For<IDbContextFactory<NodeLocalWebSessionDbContext>>();
        var grantFactory = Substitute.For<IDbContextFactory<NodeLocalSearchDbContext>>();
        var partyReader = Substitute.For<ICanonicalPrincipalPartyReader>();
        var rosterReader = Substitute.For<IVerifiedTenantRosterReader>();
        var invitationIssuer = new AccountSetupInvitationIssuer(
            sessionFactory,
            new WebSelectedSessionStore(sessionFactory),
            identityFactory,
            grantFactory,
            partyReader,
            rosterReader,
            new AccountSetupInvitationStore(identityFactory),
            TestAuthorization.Gate(false),
            TimeProvider.System);
        var authority = TestAuthorization.Write(Tenant, at: At);

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => invitationIssuer.IssueAsync(
            "selected-session",
            new AccountSetupInvitationIssueRequest(
                Tenant.Value,
                [TeamRolePermissions.RecordsRead],
                "invitation-idempotency"),
            authority));

        var partitions = new PartitionResolverSpy();
        var membershipAdmission = new MembershipAdmissionSpy();
        var membership = new InstallationIdentityCoordinatorService(
            identityFactory,
            partitions,
            membershipAdmission,
            TimeProvider.System,
            authorizationGate: TestAuthorization.Gate(false));
        var membershipCommand = new InstallationIdentityCoordinationCommand(
            "membership-correlation",
            "account-1",
            "account-1",
            "authority-evidence",
            1,
            1,
            1,
            1,
            [new TenantMembershipMutation(Tenant.Value, "member-1", "grant-1", 1, 1, 0,
                TenantMembershipStatus.Active)]);

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() =>
            membership.ExecuteAsync(membershipCommand, authority));

        var store = Substitute.For<IGrantStore, IGrantAuthorizationEpochReader>();
        var service = new InitialGrantIssuanceService(store, TestAuthorization.Gate(false), TimeProvider.System);
        var admission = Admission();

        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueAsync(
            admission, TestAuthorization.Write(Tenant, admission.InviterPrincipal.Value, At)));

        Assert.Empty(identityFactory.ReceivedCalls());
        Assert.Empty(sessionFactory.ReceivedCalls());
        Assert.Empty(grantFactory.ReceivedCalls());
        Assert.Empty(partyReader.ReceivedCalls());
        Assert.Empty(rosterReader.ReceivedCalls());
        Assert.Equal(0, partitions.Calls);
        Assert.Equal(0, membershipAdmission.Calls);
        await store.DidNotReceiveWithAnyArgs().AppendAsync(default, default!, default!);
        await ((IGrantAuthorizationEpochReader)store).DidNotReceiveWithAnyArgs()
            .ReadAuthorizationEpochAsync(default, default);
    }

    [Theory]
    [InlineData("team-access")]
    [InlineData("team-update")]
    [InlineData("invitation")]
    [InlineData("initial-grant")]
    [InlineData("node-administrator")]
    [InlineData("recovery-invitation")]
    [InlineData("live-membership")]
    public async Task IdentityAdministration_DenialLeavesInvitationMembershipAndGrantStoresUntouched(
        string administrator)
    {
        var identity = Substitute.For<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>();
        var sessions = Substitute.For<IDbContextFactory<NodeLocalWebSessionDbContext>>();
        var grants = Substitute.For<IDbContextFactory<NodeLocalSearchDbContext>>();
        var party = Substitute.For<ICanonicalPrincipalPartyReader>();
        var roster = Substitute.For<IVerifiedTenantRosterReader>();
        var adminTenant = new TenantId("11111111-1111-1111-1111-111111111111");
        var authority = TestAuthorization.Write(adminTenant, at: At);
        var denied = TestAuthorization.Gate(false);
        using var capture = new RosterDecisionCapture();

        switch (administrator)
        {
            case "team-access":
            case "team-update":
            {
                var grantStore = Substitute.For<IGrantStore>();
                var invitationIssuer = new InvitationIssuerSpy();
                var closure = Substitute.For<IAuthorizationClosureReader>();
                var service = new AdminTeamAccessAuthority(
                    sessions, new WebSelectedSessionStore(sessions), identity, grants, party, roster,
                    new AccountSetupInvitationStore(identity), invitationIssuer, grantStore,
                    new AuthorizedGrantRevocationWriter(grantStore, grants), closure,
                    denied, new FixedTimeProvider(At), new NoopRosterMemberRevocationAuthority(),
                    new Harborline.Api.Kernel.Audit.InMemoryAuditTrail(),
                    new Harborline.Api.Foundation.Crypto.Ed25519Signer(Harborline.Api.Foundation.Crypto.KeyPair.Generate()), refusalAudit: capture.Audit);
                if (administrator == "team-update")
                    await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.UpdateMemberPermissionsAsync(
                        "selected", adminTenant.Value, "grant", [TeamRolePermissions.RecordsRead], authority));
                else await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.RevokeMemberGrantAsync(
                    "selected", adminTenant.Value, "grant", authority));
                Assert.Empty(grantStore.ReceivedCalls());
                Assert.Equal(0, invitationIssuer.Calls);
                Assert.Empty(closure.ReceivedCalls());
                break;
            }
            case "invitation":
            {
                var service = new AccountSetupInvitationIssuer(
                    sessions, new WebSelectedSessionStore(sessions), identity, grants, party, roster,
                    new AccountSetupInvitationStore(identity), denied, new FixedTimeProvider(At), capture.Audit);
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueAsync(
                    "selected", new AccountSetupInvitationIssueRequest(adminTenant.Value, [TeamRolePermissions.RecordsRead], "invite"), authority));
                break;
            }
            case "initial-grant":
            {
                var store = Substitute.For<IGrantStore, IGrantAuthorizationEpochReader>();
                var service = new InitialGrantIssuanceService(store, denied, new FixedTimeProvider(At));
                var admission = Admission();
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueAsync(
                    admission, TestAuthorization.Write(Tenant, admission.InviterPrincipal.Value, At)));
                Assert.Empty(store.ReceivedCalls());
                break;
            }
            case "node-administrator":
            {
                var factory = Substitute.For<IDbContextFactory<NodeLocalRosterDbContext>>();
                var service = new NodeAdministratorAuthority(factory, new FixedTimeProvider(At), denied);
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.AppendRemovalAsync(
                    adminTenant.Value, "party", AdministratorAuthorityEvent.Revoked, "test", authority));
                Assert.Empty(factory.ReceivedCalls());
                break;
            }
            case "recovery-invitation":
            {
                var service = new RecoveryInvitationIssuer(
                    sessions, new WebSelectedSessionStore(sessions), identity, grants, party, roster,
                    new RecoveryInvitationStore(identity), denied, capture.Audit);
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.IssueAsync(
                    "selected", new RecoveryInvitationIssueRequest(adminTenant.Value, "member", "recovery"), authority));
                break;
            }
            case "live-membership":
            {
                var partitions = new PartitionResolverSpy();
                var admission = new MembershipAdmissionSpy();
                var service = new InstallationIdentityCoordinatorService(
                    identity, partitions, admission, new FixedTimeProvider(At), denied);
                var command = new InstallationIdentityCoordinationCommand(
                    "correlation", "account", "actor", "evidence", 1, 1, 1, 1,
                    [new TenantMembershipMutation(adminTenant.Value, "member", "grant", 1, 1, 0,
                        TenantMembershipStatus.Active)]);
                await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.ExecuteAsync(command, authority));
                Assert.Equal(0, partitions.Calls);
                Assert.Equal(0, admission.Calls);
                break;
            }
        }

        if (administrator is "team-access" or "team-update" or "invitation" or "recovery-invitation")
        {
            var evidence = Assert.Single(capture.Evidence);
            Assert.False(evidence.Allowed);
            Assert.Null(evidence.Roster); // Refused before roster or session lookup.
            await capture.AssertAuditAsync(adminTenant);
        }

        Assert.Empty(identity.ReceivedCalls());
        Assert.Empty(sessions.ReceivedCalls());
        Assert.Empty(grants.ReceivedCalls());
        Assert.Empty(party.ReceivedCalls());
        Assert.Empty(roster.ReceivedCalls());
    }

    [Fact]
    public void IdentityCeremony_SourceRetainsPinnedGrantEpochAndVersionChecks()
    {
        var invitation = Source("apps", "local-node-host", "Data", "Identity", "AccountSetupInvitationIssuer.cs");
        var membership = Source("apps", "local-node-host", "Data", "Identity", "LiveTenantMembershipAuthorityAdmission.cs");
        Assert.True(invitation.IndexOf("_gate.DecideAsync", StringComparison.Ordinal) <
            invitation.IndexOf("GrantPinsAreCurrentAsync", StringComparison.Ordinal));
        Assert.Contains("grant.OwnerVersion == pin.OwnerVersion", invitation, StringComparison.Ordinal);
        Assert.Contains("epoch.AuthorizationEpoch == session.AuthorizationEpoch", invitation, StringComparison.Ordinal);
        Assert.Contains("grant.OwnerVersion != grantOwnerVersion", membership, StringComparison.Ordinal);
        Assert.Contains("epoch.AuthorizationEpoch != authorizationEpoch", membership, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchProjection_AcceptsMatchingOriginDecision_AndRejectsAmbientOrWrongTargetDecision()
    {
        var matching = TestAuthorization.AllowedDecision(Tenant, "record-1");
        matching.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite), Tenant, "record", "record-1");
        Assert.Throws<ArgumentException>(() => matching.RequireAllowedReaction(
            AuthorizationOperation.Parse(TeamRolePermissions.RecordsWrite), Tenant, "record", "record-2"));
        var factory = Substitute.For<IDbContextFactory<NodeLocalSearchDbContext>>();
        var indexer = new NodeSearchIndexer(factory);
        await Assert.ThrowsAsync<ArgumentException>(() => indexer.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "record-2",
            TenantId = Tenant.Value,
            NodeType = "slice-3",
            Title = "must not be indexed",
        }, matching));
        Assert.Empty(factory.ReceivedCalls());
        Assert.DoesNotContain("AsyncLocal", Source("apps", "local-node-host", "Data", "Search", "NodeSearchIndexer.cs"),
            StringComparison.Ordinal);

        await using var search = await SearchTestStore.CreateAsync();
        var real = new NodeSearchIndexer(search.Factory);
        var tenantB = new TenantId("tenant-B");
        await real.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "shared-id",
            TenantId = tenantB.Value,
            NodeType = "foreign",
            Title = "must survive tenant-A deletes",
            Residency = SearchResidency.Cache,
        }, TestAuthorization.AllowedDecision(tenantB, "shared-id"));
        var tenantBNodeBefore = await ReadNodeSnapshotAsync(search, tenantB.Value, "shared-id");
        await real.IndexNodeAsync(new SearchNodeRow
        {
            RecordId = "shared-id",
            TenantId = Tenant.Value,
            NodeType = "tenant-a",
            Title = "tenant A projection",
            Body = "tenant A body",
            Residency = SearchResidency.Cache,
        }, TestAuthorization.AllowedDecision(Tenant, "shared-id"));
        Assert.Equal(tenantBNodeBefore, await ReadNodeSnapshotAsync(search, tenantB.Value, "shared-id"));

        await Assert.ThrowsAsync<ArgumentException>(() => real.IndexEdgesAsync(
            Tenant.Value,
            "source-A",
            [new SearchEdgeRow
            {
                Id = Guid.NewGuid().ToString("N"),
                TenantId = tenantB.Value,
                SourceRecordId = "source-A",
                TargetRecordId = "target-B",
                EdgeType = "foreign",
            }],
            TestAuthorization.AllowedDecision(Tenant, "source-A")));

        await real.OnResidencyChangedAsync(
            Tenant.Value,
            "shared-id",
            GrantResidency.OnlineOnly,
            TestAuthorization.AllowedDecision(Tenant, "shared-id"));
        await real.DeleteRecordAsync(
            Tenant.Value,
            "shared-id",
            TestAuthorization.AllowedDecision(Tenant, "shared-id"));

        await using (var db = search.CreateContext())
        {
            Assert.True(await db.Nodes.AnyAsync(row =>
                row.TenantId == tenantB.Value && row.RecordId == "shared-id"));
            db.VecRows.Add(new VecRow
            {
                RecordId = "vec-shared",
                TenantId = tenantB.Value,
                SubjectId = VecIndexConstants.TenantWideSubject,
                Model = "test",
                ModelVersion = "1",
                Dimension = 1,
                EncryptedEmbedding = [1],
                EmbeddingNonce = [2],
                KeyVersion = 1,
            });
            await db.SaveChangesAsync();
        }

        var vec = new NodeVecIndexer(
            search.Factory,
            embedder: null,
            Substitute.For<ISubjectFieldEncryptor>());
        await vec.DeleteRecordAsync(
            Tenant.Value,
            "vec-shared",
            TestAuthorization.AllowedDecision(Tenant, "vec-shared"));
        await using var verify = search.CreateContext();
        Assert.True(await verify.VecRows.AnyAsync(row =>
            row.TenantId == tenantB.Value && row.RecordId == "vec-shared"));

        await using var vectors = await Harborline.Api.LocalNodeHost.Tests.Search.Vector.VecTestHarness.CreateAsync();
        await using (var db = vectors.Store.CreateContext())
        {
            db.VecRows.Add(new VecRow
            {
                RecordId = "upsert-shared",
                TenantId = tenantB.Value,
                SubjectId = "subject-b",
                Model = "foreign-model",
                ModelVersion = "foreign-version",
                Dimension = 1,
                EncryptedEmbedding = [11, 12, 13],
                EmbeddingNonce = [21, 22],
                KeyVersion = 7,
            });
            await db.SaveChangesAsync();
        }
        var tenantBVecBefore = await ReadVecSnapshotAsync(vectors.Store, tenantB.Value, "upsert-shared");
        await vectors.Indexer(dimension: 4).IndexArtifactAsync(
            new KgEmbeddingArtifact(
                "upsert-shared", Tenant.Value, null, [1f, -1f, 1f, -1f], 4,
                KgModelFloorGate.StubModelSentinel, "test-v1"),
            SearchResidency.Cache,
            TestAuthorization.AllowedDecision(Tenant, "upsert-shared"));
        Assert.Equal(tenantBVecBefore, await ReadVecSnapshotAsync(vectors.Store, tenantB.Value, "upsert-shared"));
    }

    [Fact]
    public async Task DevelopmentIndexer_UsesRealSeededClosure_AndRevocationStopsMutation()
    {
        await using var search = await SearchTestStore.CreateAsync();
        await using (var identity = search.CreateInstallationIdentityContext())
            await identity.Database.MigrateAsync();
        await using var authorization = RealAuthorizationProvider(search);
        var teamId = new TeamId(Guid.NewGuid());
        await using var team = new TeamContext(
            teamId,
            "slice-3",
            new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        var active = Substitute.For<IActiveTeamAccessor>();
        active.Active.Returns(team);
        var tenant = NodeTenant.Resolve(active);
        var seed = authorization.GetRequiredService<AccessGrantAuthorizationSeed>();
        await seed.InstallAsync(tenant, At, AuthorizationSeedProfile.Development);
        var eventStore = new InMemoryCalendarEventStore();
        var firstEvent = CalendarEvent.Create(
            tenant,
            "authorization slice 3",
            new DateOnly(2026, 9, 2),
            new DateOnly(2026, 9, 2),
            Guid.NewGuid());
        await eventStore.SaveAsync(firstEvent);
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        using var nodeSigner = new NodePrincipalSigner(Enumerable.Repeat((byte)0x53, 32).ToArray());
        var roster = DevelopmentIndexerRoster(nodeSigner);
        var indexer = new KgCalendarDevIndexer(
            eventStore,
            authorization.GetRequiredService<IGrantStore>(),
            new NodeSearchIndexer(search.Factory),
            active,
            roster,
            nodeSigner,
            environment,
            NullLogger<KgCalendarDevIndexer>.Instance,
            authorization.GetRequiredService<AuthorizationGate>(),
            new FixedTimeProvider(At));

        await indexer.StartAsync(CancellationToken.None);

        await using (var context = search.CreateContext())
            Assert.True(await context.Nodes.AnyAsync(row => row.RecordId == firstEvent.Id.ToString()));

        var grants = authorization.GetRequiredService<IGrantStore>();
        var visible = await grants.FindBySourceReferenceAsync(tenant, AccessGrantAuthorizationSeed.DevIndexerGrantSource);
        Assert.NotNull(visible);
        Assert.Equal(AccessGrantAuthorizationSeed.DevIndexerPrincipal, visible!.Subject.Value);
        await grants.RevokeAsync(tenant, visible.GrantId, new GrantRevocation(
            new ActorId("tenant-administrator"), At.AddMinutes(1),
            new GrantReason(GrantReasonCodes.RevocationOffboarding)));
        await seed.InstallAsync(tenant, At.AddMinutes(2), AuthorizationSeedProfile.Development);
        var stillRevoked = await grants.FindBySourceReferenceAsync(
            tenant, AccessGrantAuthorizationSeed.DevIndexerGrantSource);
        Assert.NotNull(stillRevoked);
        Assert.Equal(GrantStatus.Revoked, stillRevoked!.Status);
        Assert.Equal(visible.GrantId, stillRevoked.GrantId);

        var secondEvent = CalendarEvent.Create(
            tenant, "must remain unindexed", new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 3), Guid.NewGuid());
        await eventStore.SaveAsync(secondEvent);
        var revokedIndexer = new KgCalendarDevIndexer(
            eventStore, grants, new NodeSearchIndexer(search.Factory), active, roster, nodeSigner, environment,
            NullLogger<KgCalendarDevIndexer>.Instance,
            authorization.GetRequiredService<AuthorizationGate>(),
            new FixedTimeProvider(At.AddMinutes(3)));
        await revokedIndexer.StartAsync(CancellationToken.None);

        await using var afterRevocation = search.CreateContext();
        Assert.False(await afterRevocation.Nodes.AnyAsync(row => row.RecordId == secondEvent.Id.ToString()));
        Assert.Equal(1, await afterRevocation.Nodes.CountAsync());
    }

    [Fact]
    public async Task DevelopmentIndexer_HasNoGrantInProductionSeed()
    {
        await using var search = await SearchTestStore.CreateAsync();
        await using (var identity = search.CreateInstallationIdentityContext())
            await identity.Database.MigrateAsync();
        await using var authorization = RealAuthorizationProvider(search);
        var teamId = new TeamId(Guid.NewGuid());
        await using var team = new TeamContext(teamId, "slice-3", new ServiceCollection().BuildServiceProvider(), TimeProvider.System);
        var active = Substitute.For<IActiveTeamAccessor>();
        active.Active.Returns(team);
        var tenant = NodeTenant.Resolve(active);
        await authorization.GetRequiredService<AccessGrantAuthorizationSeed>()
            .InstallAsync(tenant, At, AuthorizationSeedProfile.Production);
        var events = new InMemoryCalendarEventStore();
        await events.SaveAsync(CalendarEvent.Create(
            tenant, "production seed", new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 2), Guid.NewGuid()));
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        using var nodeSigner = new NodePrincipalSigner(Enumerable.Repeat((byte)0x54, 32).ToArray());
        var roster = DevelopmentIndexerRoster(nodeSigner);

        await new KgCalendarDevIndexer(
            events, authorization.GetRequiredService<IGrantStore>(), new NodeSearchIndexer(search.Factory),
            active, roster, nodeSigner, environment, NullLogger<KgCalendarDevIndexer>.Instance,
            authorization.GetRequiredService<AuthorizationGate>(), new FixedTimeProvider(At))
            .StartAsync(CancellationToken.None);

        await using var context = search.CreateContext();
        Assert.Empty(await context.Nodes.ToListAsync());
    }

    [Fact]
    public async Task ThreeWayMatchDevelopmentSeed_UsesRealSeededClosure_AndRevocationStopsDefinitionMutation()
    {
        await using var search = await SearchTestStore.CreateAsync();
        await using (var identity = search.CreateInstallationIdentityContext())
            await identity.Database.MigrateAsync();
        await using var authorization = RealAuthorizationProvider(search);
        var tenant = new TenantId("three-way-development-seed");
        var seed = authorization.GetRequiredService<AccessGrantAuthorizationSeed>();
        await seed.InstallAsync(tenant, At, AuthorizationSeedProfile.Development);

        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var workflowAdmission = new WorkflowAdmissionValidator();
        var store = new EntityStoreWorkflowDefinitionStore(entities, workflowAdmission, Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting, TimeProvider.System);
        var lifecycle = new AuthorizedWorkflowDefinitionLifecycle(
            store,
            entities,
            workflowAdmission, Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting,
            TimeProvider.System,
            authorization.GetRequiredService<AuthorizationGate>(),
            authorization.GetRequiredService<IRoleGateAdmission>());
        var authority = new AuthorizationWriteContext(
            new ActorId(AccessGrantAuthorizationSeed.DevWorkflowSeederPrincipal), tenant, At);
        var decision = await lifecycle.DecideAsync(NodeThreeWayMatchWorkflowSeed.DefinitionKey, authority);

        await NodeThreeWayMatchWorkflowSeed.EnsurePublishedAsync(
            lifecycle, tenant.Value, decision);

        var stored = await store.GetAsync(new DefinitionCoordinates(
            tenant, NodeThreeWayMatchWorkflowSeed.DefinitionKey, NodeThreeWayMatchWorkflowSeed.Version));
        Assert.Equal(WorkflowDefinitionStatus.Published, stored.Status);
        var grants = authorization.GetRequiredService<IGrantStore>();
        var grant = Assert.IsType<AccessGrant>(await grants.FindBySourceReferenceAsync(
            tenant, AccessGrantAuthorizationSeed.DevWorkflowSeederGrantSource));
        Assert.Equal(AccessGrantAuthorizationSeed.DevWorkflowSeederPrincipal, grant.Subject.Value);
        await grants.RevokeAsync(tenant, grant.GrantId, new GrantRevocation(
            new ActorId("tenant-administrator"), At.AddMinutes(1),
            new GrantReason(GrantReasonCodes.RevocationOffboarding)));

        var denied = new AuthorizationWriteContext(
            new ActorId(AccessGrantAuthorizationSeed.DevWorkflowSeederPrincipal), tenant, At.AddMinutes(2));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await lifecycle.DecideAsync("three-way-match.v2", denied));
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    public async Task RealHostedDevelopmentAuthorityAndThreeWaySeederHonorEnvironment(
        string environmentName,
        bool developmentRuns)
    {
        await using var search = await SearchTestStore.CreateAsync();
        await using (var identity = search.CreateInstallationIdentityContext())
            await identity.Database.MigrateAsync();
        await using var authorization = RealAuthorizationProvider(search);
        await using var team = new TeamContext(
            new TeamId(Guid.Parse(developmentRuns
                ? "21800000-0000-0000-0000-000000000001"
                : "21800000-0000-0000-0000-000000000002")),
            "hosted-environment-fence",
            new ServiceCollection().BuildServiceProvider(),
            TimeProvider.System);
        var active = Substitute.For<IActiveTeamAccessor>();
        active.Active.Returns(team);
        var tenant = NodeTenant.Resolve(active);
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);

        var teamContexts = Substitute.For<ITeamContextFactory>();
        teamContexts.Active.Returns([team]);
        var authorizationHosted = new AuthorizationSeedHostedService(
            authorization.GetRequiredService<AccessGrantAuthorizationSeed>(),
            active,
            teamContexts,
            developmentRuns ? AuthorizationSeedProfile.Development : AuthorizationSeedProfile.Production,
            new FixedTimeProvider(At));
        await authorizationHosted.StartAsync(CancellationToken.None);

        var grants = authorization.GetRequiredService<IGrantStore>();
        Assert.Equal(developmentRuns,
            await grants.FindBySourceReferenceAsync(
                tenant, AccessGrantAuthorizationSeed.DevWorkflowSeederGrantSource) is not null);

        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var workflowAdmission = new WorkflowAdmissionValidator();
        var store = new EntityStoreWorkflowDefinitionStore(
            entities, workflowAdmission, Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting, new FixedTimeProvider(At));
        var lifecycle = new AuthorizedWorkflowDefinitionLifecycle(
            store,
            entities,
            workflowAdmission, Harborline.Api.Foundation.Assets.Entities.TestEntityWritePipeline.Accepting,
            new FixedTimeProvider(At),
            TestAuthorization.AllowGate(),
            TestAuthorization.RoleGate());
        var seeder = new ThreeWayMatchDevSeeder(
            lifecycle,
            Substitute.For<IWorkflowStore>(),
            Substitute.For<IWorkflowTriggerDispatcher>(),
            active,
            environment,
            NullLogger<ThreeWayMatchDevSeeder>.Instance,
            TestAuthorization.AllowGate(),
            new FixedTimeProvider(At));
        await seeder.StartAsync(CancellationToken.None);

        var coordinates = new DefinitionCoordinates(
            tenant,
            NodeThreeWayMatchWorkflowSeed.DefinitionKey,
            NodeThreeWayMatchWorkflowSeed.Version);
        if (developmentRuns)
        {
            Assert.Equal(
                WorkflowDefinitionStatus.Published,
                (await store.GetAsync(coordinates)).Status);
        }
        else
        {
            await Assert.ThrowsAsync<WorkflowDefinitionNotFoundException>(
                () => store.GetAsync(coordinates).AsTask());
        }
    }

    private static ServiceProvider RealAuthorizationProvider(SearchTestStore search)
    {
        var services = new ServiceCollection();
        services.AddSingleton(search.Factory);
        services.AddNodeAuthorizationModel();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static async Task<string> ReadNodeSnapshotAsync(SearchTestStore store, string tenant, string record)
    {
        await using var context = store.CreateContext();
        var row = await context.Nodes.AsNoTracking().SingleAsync(item => item.TenantId == tenant && item.RecordId == record);
        return $"{row.TenantId}|{row.RecordId}|{row.NodeType}|{row.Title}|{row.Body}|{(int)row.Residency}";
    }

    private static async Task<string> ReadVecSnapshotAsync(SearchTestStore store, string tenant, string record)
    {
        await using var context = store.CreateContext();
        var row = await context.VecRows.AsNoTracking().SingleAsync(item => item.TenantId == tenant && item.RecordId == record);
        return string.Join('|', row.TenantId, row.RecordId, row.SubjectId, row.Model, row.ModelVersion,
            row.Dimension, Convert.ToHexString(row.EncryptedEmbedding), Convert.ToHexString(row.EmbeddingNonce), row.KeyVersion);
    }

    private static JournalEntry Draft() => new(
        new JournalEntryId("journal-1"), Tenant, new DateOnly(2026, 9, 2), "slice 3",
        [
            new JournalEntryLine(new GLAccountId("1000"), 10m, 0m),
            new JournalEntryLine(new GLAccountId("2000"), 0m, 10m),
        ],
        new Instant(At));

    private static AdmissionCompleted Admission() => new(
        Tenant,
        new PrincipalUserId("new-member"),
        new CanonicalPartyReference("party-new-member"),
        new PrincipalUserId("administrator"),
        "invitation-1",
        AccessGrantAuthorizationSeed.MemberRole,
        new GrantProvenance(
            GrantSourceKind.Invitation,
            new GrantReason(GrantReasonCodes.Invitation, "invitation-1"),
            new ActorId("administrator")));

    private sealed class PartitionResolverSpy : ITenantIdentityAuthorityPartitionResolver
    {
        internal int Calls { get; private set; }

        public Task<TenantIdentityAuthorityPartition> ResolveAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("partition resolver must remain untouched on denial");
        }
    }

    private sealed class MembershipAdmissionSpy : ITenantMembershipAuthorityAdmission
    {
        internal int Calls { get; private set; }

        public Task ValidateMutationAsync(
            string actorAccountId,
            string authorityEvidenceDigest,
            string accountId,
            TenantMembershipMutation mutation,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("membership validator must remain untouched on denial");
        }

        public Task ValidateExistingAsync(
            string accountId,
            TenantMembershipSnapshot membership,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("membership validator must remain untouched on denial");
        }
    }

    private sealed class InvitationIssuerSpy : IAccountSetupInvitationIssuer
    {
        internal int Calls { get; private set; }

        public Task<AccountSetupInvitationIssueResult?> IssueAsync(
            string selectedSessionHandle,
            AccountSetupInvitationIssueRequest request,
            AuthorizationWriteContext authority,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("invitation issuer must remain untouched on denial");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), Path.Combine(parts)));

    private static string RepositoryRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps")) &&
                Directory.Exists(Path.Combine(directory.FullName, "packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
