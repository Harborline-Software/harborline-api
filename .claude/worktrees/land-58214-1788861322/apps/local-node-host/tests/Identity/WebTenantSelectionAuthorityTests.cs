using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Session;
using Harborline.Api.Kernel.Lease;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class WebTenantSelectionAuthorityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Selection_Completes_Both_Audit_Heads_Before_Mint_And_Consumes_Exactly_Once()
    {
        await using var fixture = await SelectionFixture.CreateAsync();

        var selected = await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId);

        Assert.NotNull(selected);
        Assert.Equal(fixture.TenantId, selected!.TenantId);
        Assert.Equal(fixture.TenantId, selected.DisplayName);
        Assert.Equal(new[] { 0, 0 }, fixture.Store.SessionCountsObservedDuringFinalize);
        Assert.Equal(2, fixture.Store.FinalizeCalls);

        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var home = await identity.Coordinators.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCoordinatorState.Completed, home.State);
            Assert.Equal(WebTenantSelectionAuthority.CommandType, home.CommandType);
            Assert.Equal(
                "WebTenantSelectionCompleted",
                (await identity.AuditEnvelopes.AsNoTracking().OrderBy(row => row.Sequence).LastAsync())
                    .EventType);
        }
        await using (var sessions = fixture.SessionFactory.CreateDbContext())
        {
            var durable = await sessions.UserSessions.AsNoTracking().SingleAsync();
            var challenge = await sessions.AccountAccessChallenges.AsNoTracking().SingleAsync();
            var antiforgery = await sessions.AntiforgeryStates.AsNoTracking().SingleAsync();
            Assert.Equal(fixture.TenantId, durable.TenantId);
            Assert.Equal(fixture.Membership.MembershipId, durable.MembershipId);
            Assert.Equal(fixture.Membership.OwnerVersion, durable.MembershipOwnerVersion);
            Assert.Equal(fixture.Membership.AuthorizationEpoch, durable.AuthorizationEpoch);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selected.Handle))),
                durable.HandleDigest);
            Assert.DoesNotContain(selected.Handle, durable.HandleDigest, StringComparison.Ordinal);
            Assert.Equal(durable.AntiforgeryStateId, antiforgery.AntiforgeryStateId);
            Assert.Equal(WebCookieAudience.SelectedSession, antiforgery.Audience);
            Assert.Equal(durable.SessionCorrelationId, antiforgery.SubjectCorrelationId);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(selected.AntiforgeryToken))),
                antiforgery.TokenDigest);
            Assert.NotNull(challenge.ConsumedAtUtc);
        }

        Assert.Null(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));
        await using var replaySessions = fixture.SessionFactory.CreateDbContext();
        Assert.Equal(1, await replaySessions.UserSessions.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-tenant-id")]
    [Trait("PlanCard", "MTW-2")]
    public async Task Invalid_Explicit_Tenant_Refuses_Without_Consuming_Challenge(string tenantId)
    {
        await using var fixture = await SelectionFixture.CreateAsync();

        Assert.Null(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, tenantId));

        await using var sessions = fixture.SessionFactory.CreateDbContext();
        Assert.Null((await sessions.AccountAccessChallenges.AsNoTracking().SingleAsync()).ConsumedAtUtc);
        Assert.Empty(await sessions.UserSessions.AsNoTracking().ToArrayAsync());
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        Assert.Empty(await identity.Coordinators.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Unknown_And_Unusable_Tenants_Have_Equivalent_External_Refusals_And_Documented_Read_Profiles()
    {
        await using var unknown = await SelectionFixture.CreateAsync();
        var unknownTenantId = Guid.Parse("73fc752f-44da-43b6-bf60-ec6797ec527e").ToString("D");

        Assert.NotEqual(unknown.TenantId, unknownTenantId);
        Assert.Null(await unknown.Authority.SelectAsync(unknown.ChallengeHandle, unknownTenantId));
        await AssertRefusalHasNoExternalEffectsAsync(unknown);

        // An unknown GUID exits at candidate lookup. It cannot resolve a partition or perform
        // membership/admission reads, and it must never enter any write/coordination path.
        Assert.Equal(1, unknown.Candidates.ListCalls);
        Assert.Equal(0, unknown.Resolver.ResolveCalls);
        Assert.Equal(0, unknown.Store.ReadCalls);
        Assert.Equal(0, unknown.UnusableStore.ReadCalls);
        Assert.Equal(0, unknown.Store.WriteCalls + unknown.UnusableStore.WriteCalls);

        await using var unusable = await SelectionFixture.CreateAsync();
        Assert.Null(await unusable.Authority.SelectAsync(
            unusable.ChallengeHandle,
            unusable.UnusableTenantId));
        await AssertRefusalHasNoExternalEffectsAsync(unusable);

        // A syntactically valid listed candidate is allowed to resolve its partition and make
        // read-only admission/membership checks before refusing. This intentionally differs from
        // the unknown-GUID path, while the public non-enumerating refusal surface stays identical.
        Assert.Equal(1, unusable.Candidates.ListCalls);
        Assert.Equal(1, unusable.Resolver.ResolveCalls);
        Assert.Equal(0, unusable.Store.ReadCalls);
        Assert.Equal(2, unusable.UnusableStore.ReadCalls);
        Assert.Equal(0, unusable.Store.WriteCalls + unusable.UnusableStore.WriteCalls);
        Assert.Equal(0, unusable.Parties.ResolveCalls);
    }

    private static async Task AssertRefusalHasNoExternalEffectsAsync(SelectionFixture fixture)
    {
        await using (var sessions = fixture.SessionFactory.CreateDbContext())
        {
            Assert.Null((await sessions.AccountAccessChallenges.AsNoTracking().SingleAsync()).ConsumedAtUtc);
            Assert.Empty(await sessions.UserSessions.AsNoTracking().ToArrayAsync());
            Assert.Empty(await sessions.AntiforgeryStates.AsNoTracking().ToArrayAsync());
        }
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        Assert.Empty(await identity.Coordinators.AsNoTracking().ToArrayAsync());
        Assert.Equal(1, await identity.AuditEnvelopes.AsNoTracking().CountAsync());
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Security_Version_Drift_After_Prepare_Aborts_Before_Commit()
    {
        await using var fixture = await SelectionFixture.CreateAsync();
        fixture.Store.AfterPrepare = async () =>
        {
            await using var identity = fixture.IdentityFactory.CreateDbContext();
            var account = await identity.Accounts.SingleAsync();
            account.SecurityVersion++;
            account.OwnerVersion++;
            await identity.SaveChangesAsync();
        };

        Assert.Null(await fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));

        Assert.True(fixture.Store.Aborted);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Aborted,
                (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await using var sessions = fixture.SessionFactory.CreateDbContext();
        Assert.Empty(await sessions.UserSessions.AsNoTracking().ToArrayAsync());
        Assert.Null((await sessions.AccountAccessChallenges.AsNoTracking().SingleAsync()).ConsumedAtUtc);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Retry_Rolls_Committing_Selection_Forward_By_Durable_Correlation()
    {
        await using var fixture = await SelectionFixture.CreateAsync();
        fixture.Store.ThrowAfterFinalizeOnce = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SelectAsync(fixture.ChallengeHandle, fixture.TenantId));
        await using (var interrupted = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Committing,
                (await interrupted.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await using (var sessions = fixture.SessionFactory.CreateDbContext())
        {
            Assert.Empty(await sessions.UserSessions.AsNoTracking().ToArrayAsync());
        }

        var recovered = await fixture.Authority.SelectAsync(
            fixture.ChallengeHandle,
            fixture.TenantId);

        Assert.NotNull(recovered);
        await using var completed = fixture.IdentityFactory.CreateDbContext();
        Assert.Equal(
            InstallationIdentityCoordinatorState.Completed,
            (await completed.Coordinators.AsNoTracking().SingleAsync()).State);
    }

    /// <summary>
    /// #3245 F2: the REAL <see cref="WebTenantSelectionAuthority.SelectAsync"/> stops consuming an
    /// already-issued account challenge once the REAL v2 authority marker commits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by <c>LegacyV1CutoverAuthorityProof.ProvePostCutoverV1BearerIsRejected</c> as well as
    /// by xUnit, so the MTW-00C cutover fixture drives a production accept path rather than only the
    /// audience-insensitive orchestrator method. Looping over the four audiences against
    /// <c>CheckLegacyBearerAdmissionAsync</c> re-runs one stage check four times and cannot observe
    /// whether any real path consults it — this can.
    /// </para>
    /// <para>
    /// Two fixtures, not one: the challenge is one-time, so the ADMITTING half needs its own. The
    /// admitting half is what stops this passing over an authority that refuses unconditionally.
    /// </para>
    /// </remarks>
    [Fact(DisplayName =
        "#3245 F2: tenant selection refuses an already-issued challenge after the REAL v2 marker commits")]
    [Trait("PlanCard", "MTW-01E")]
    public async Task Select_Is_Refused_After_The_Real_V2_Marker_Commits()
    {
        // The ADMITTING state, over an untouched store: the real select path completes.
        await using (var admitting = await SelectionFixture.CreateAsync())
        {
            Assert.NotNull(await admitting.Authority.SelectAsync(
                admitting.ChallengeHandle,
                admitting.TenantId));
        }

        await using var retired = await SelectionFixture.CreateAsync();
        await CutoverAdvance.CommitV2MarkerAsync(retired.IdentityFactory, retired.Cutover, Now);

        // The SAME live, unconsumed, unexpired challenge is now unspendable — the account-challenge
        // audience was retired with the marker, so no selected session can be minted from it.
        Assert.Null(await retired.Authority.SelectAsync(retired.ChallengeHandle, retired.TenantId));

        // And the refusal really was the gate, not a consumed challenge: the challenge row is still
        // unconsumed, so nothing downstream ran.
        await using var sessions = retired.SessionFactory.CreateDbContext();
        var challenge = await sessions.AccountAccessChallenges.AsNoTracking().SingleAsync();
        Assert.Null(challenge.ConsumedAtUtc);
    }

    private sealed class SelectionFixture : IAsyncDisposable
    {
        private readonly string _directory;

        private SelectionFixture(
            string directory,
            IdentityContextFactory identityFactory,
            WebAccountAccessChallengeIssuerTests.SessionContextFactory sessionFactory,
            string challengeHandle,
            string tenantId,
            TenantMembershipSnapshot membership,
            RecordingMembershipStore store,
            RecordingMembershipStore unusableStore,
            string unusableTenantId,
            FixedCandidateLocator candidates,
            FixedPartitionResolver resolver,
            FixedPartyReader parties,
            InstallationIdentityCutoverOrchestrator cutover,
            WebTenantSelectionAuthority authority)
        {
            _directory = directory;
            IdentityFactory = identityFactory;
            SessionFactory = sessionFactory;
            ChallengeHandle = challengeHandle;
            TenantId = tenantId;
            Membership = membership;
            Store = store;
            UnusableStore = unusableStore;
            UnusableTenantId = unusableTenantId;
            Candidates = candidates;
            Resolver = resolver;
            Parties = parties;
            Cutover = cutover;
            Authority = authority;
        }

        public IdentityContextFactory IdentityFactory { get; }
        public WebAccountAccessChallengeIssuerTests.SessionContextFactory SessionFactory { get; }
        public string ChallengeHandle { get; }
        public string TenantId { get; }
        public TenantMembershipSnapshot Membership { get; }
        public RecordingMembershipStore Store { get; }
        public RecordingMembershipStore UnusableStore { get; }
        public string UnusableTenantId { get; }
        public FixedCandidateLocator Candidates { get; }
        public FixedPartitionResolver Resolver { get; }
        public FixedPartyReader Parties { get; }

        /// <summary>The REAL cutover orchestrator the fixture's authority consults as its gate.</summary>
        public InstallationIdentityCutoverOrchestrator Cutover { get; }

        public WebTenantSelectionAuthority Authority { get; }

        public static async Task<SelectionFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"tenant-select-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var identityPath = Path.Combine(directory, "identity.db");
            var sessionPath = Path.Combine(directory, "session.db");
            var identityFactory = new IdentityContextFactory(identityPath);
            var sessionFactory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(sessionPath);
            await using (var identity = identityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
            }
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
            }

            var bootstrap = new InstallationFounderBootstrapService(
                identityFactory,
                new FixedTimeProvider(Now));
            var founder = await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                "founder",
                "$argon2id$v=19$m=19456,t=2,p=1$" +
                Convert.ToBase64String(new byte[16]) + "$" +
                Convert.ToBase64String(new byte[32]),
                Guid.NewGuid().ToString("N"),
                string.Join(":", Enumerable.Repeat("AB", 32)),
                "founder-bootstrap"));
            InstallationAccountRecord account;
            await using (var identity = identityFactory.CreateDbContext())
            {
                account = await identity.Accounts.AsNoTracking().SingleAsync();
            }
            Assert.Equal(founder.AccountId, account.AccountId);

            var challengeHandle = "challenge-handle-with-at-least-256-bits-of-fixture-entropy";
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                sessions.AccountAccessChallenges.Add(new WebAccountAccessChallengeRecord(
                    ChallengeId: Guid.NewGuid().ToString("N"),
                    AccountId: account.AccountId,
                    AccountSecurityVersion: account.SecurityVersion,
                    HandleDigest: Digest(challengeHandle),
                    CoordinationCorrelationId: Guid.NewGuid().ToString("N"),
                    IssuedAtUtc: Now,
                    AbsoluteExpiresAtUtc: Now.AddMinutes(5),
                    ConsumedAtUtc: null,
                    RevokedAtUtc: null,
                    OwnerVersion: 1));
                await sessions.SaveChangesAsync();
            }

            var tenantId = Guid.NewGuid().ToString("D");
            var membership = new TenantMembershipSnapshot(
                "membership-1",
                account.AccountId,
                tenantId,
                "principal-1",
                "grant-1",
                GrantOwnerVersion: 4,
                AuthorizationEpoch: 7,
                TenantMembershipStatus.Active,
                OwnerVersion: 3);
            var store = new RecordingMembershipStore(tenantId, membership, sessionFactory);
            var unusableTenantId = Guid.NewGuid().ToString("D");
            var unusableStore = new RecordingMembershipStore(
                unusableTenantId, membership, sessionFactory, returnsMembership: false);
            var resolver = new FixedPartitionResolver(
                new TenantIdentityAuthorityPartition(tenantId, store, new AlwaysLeaseCoordinator()),
                new TenantIdentityAuthorityPartition(unusableTenantId, unusableStore, new AlwaysLeaseCoordinator()));
            var coordinator = new InstallationIdentityCoordinatorService(
                identityFactory,
                resolver,
                new AcceptingAdmission(),
                new FixedTimeProvider(Now),
                TestAuthorization.Gate(true));
            var candidates = new FixedCandidateLocator(tenantId, unusableTenantId);
            var parties = new FixedPartyReader(tenantId);
            // The REAL cutover orchestrator over this fixture's OWN identity store, not a double. A
            // freshly migrated store sits at LegacyV1Authoritative, so it admits and every existing
            // selection test behaves exactly as before — while a test that drives the real cutover to
            // V2Authoritative gets a genuine post-marker refusal out of the production seam.
            var cutover = new InstallationIdentityCutoverOrchestrator(
                identityFactory,
                new FixedTimeProvider(Now),
                Harborline.Api.LocalNodeHost.Data.Identity.InstallationAuthorityVersionRegistry
                    .CreateDefault([new TestSignInPath("test-successor")]));
            var authority = new WebTenantSelectionAuthority(
                identityFactory,
                sessionFactory,
                candidates,
                coordinator,
                resolver,
                parties,
                cutover,
                Options.Create(new SessionOptions()),
                new FixedTimeProvider(Now));
            return new SelectionFixture(
                directory,
                identityFactory,
                sessionFactory,
                challengeHandle,
                tenantId,
                membership,
                store,
                unusableStore,
                unusableTenantId,
                candidates,
                resolver,
                parties,
                cutover,
                authority);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class IdentityContextFactory(string databasePath)
        : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalInstallationIdentityDbContext(options);
        }
    }

    private sealed class RecordingMembershipStore(
        string tenantId,
        TenantMembershipSnapshot membership,
        IDbContextFactory<NodeLocalWebSessionDbContext> sessionFactory,
        bool returnsMembership = true)
        : ITenantMembershipAuthorityStore
    {
        private TenantSessionSelectionReceipt? _receipt;
        private string? _intentDigest;
        public string TenantId { get; } = tenantId;
        public Func<Task>? AfterPrepare { get; set; }
        public bool Aborted { get; private set; }
        public bool ThrowAfterFinalizeOnce { get; set; }
        public int FinalizeCalls { get; private set; }
        public List<int> SessionCountsObservedDuringFinalize { get; } = [];
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }

        public Task<TenantMembershipSnapshot?> GetMembershipAsync(
            string accountId,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            return Task.FromResult<TenantMembershipSnapshot?>(
                returnsMembership && accountId == membership.AccountId ? membership : null);
        }

        public Task<bool> IsAdmissionBlockedAsync(
            string accountId,
            CancellationToken cancellationToken)
        {
            ReadCalls++;
            return Task.FromResult(false);
        }

        public async Task PrepareSessionSelectionAsync(
            string correlationId,
            string commandFingerprint,
            string accountId,
            string membershipId,
            string payloadDigest,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            Assert.Equal(membership.AccountId, accountId);
            Assert.Equal(membership.MembershipId, membershipId);
            _intentDigest = InstallationAuditIntegrity.Hash(
                correlationId,
                commandFingerprint,
                accountId,
                membershipId,
                payloadDigest);
            if (AfterPrepare is not null)
            {
                await AfterPrepare();
            }
        }

        public async Task<TenantSessionSelectionReceipt> FinalizeSessionSelectionAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            await using var sessions = await sessionFactory.CreateDbContextAsync(cancellationToken);
            SessionCountsObservedDuringFinalize.Add(await sessions.UserSessions.CountAsync(cancellationToken));
            FinalizeCalls++;
            var receipt = _receipt ??= new TenantSessionSelectionReceipt(
                TenantId,
                DocumentOwnerVersion: 2,
                membership.MembershipId,
                AuditSequence: 1,
                AuditHeadHash: new string('A', 64),
                IntentDigest: _intentDigest ?? throw new InvalidOperationException("prepare was not called"),
                HomeDecisionDigest: new string('C', 64));
            if (ThrowAfterFinalizeOnce)
            {
                ThrowAfterFinalizeOnce = false;
                throw new InvalidOperationException("injected response loss after tenant finalization");
            }
            return receipt;
        }

        public Task AbortSessionSelectionAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            Aborted = true;
            return Task.CompletedTask;
        }

        public Task PrepareSessionRevocationAsync(
            string correlationId, string commandFingerprint, string accountId,
            string membershipId, string sessionCorrelationId, string payloadDigest,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TenantSessionRevocationReceipt> FinalizeSessionRevocationAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortSessionRevocationAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task PrepareAsync(
            string correlationId, string commandFingerprint, string accountId, string actorAccountId,
            string authorityEvidenceDigest, TenantMembershipMutation mutation,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TenantMembershipIntentState?> GetIntentStateAsync(
            string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantMembershipIntentState?>(null);
    }

    private sealed class FixedPartitionResolver(params TenantIdentityAuthorityPartition[] partitions)
        : ITenantIdentityAuthorityPartitionResolver
    {
        public int ResolveCalls { get; private set; }
        public Task<TenantIdentityAuthorityPartition> ResolveAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult(partitions.Single(partition => partition.TenantId == tenantId));
        }
    }

    private sealed class FixedCandidateLocator(params string[] tenantIds) : IInstallationTenantCandidateLocator
    {
        private readonly InstallationTenantCandidate[] _candidates =
            tenantIds.Select(tenantId =>
                new InstallationTenantCandidate(new TenantId(tenantId), tenantId, TenantMembershipStatus.Active))
                .ToArray();
        public int ListCalls { get; private set; }
        public Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
            PrincipalUserId accountPrincipal,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<InstallationTenantCandidate>>(_candidates);
        }
        public Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
            PrincipalUserId accountPrincipal,
            string? excludedCorrelationId,
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<InstallationTenantCandidate>>(_candidates);
        }
    }

    private sealed class FixedPartyReader(string tenantId) : ICanonicalPrincipalPartyReader
    {
        public int ResolveCalls { get; private set; }
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant,
            PrincipalUserId user,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
            return ValueTask.FromResult<CanonicalPartyBinding?>(
                tenant.Value == tenantId
                    ? new CanonicalPartyBinding(tenant, user, new CanonicalPartyReference("party-1"))
                    : null);
        }
    }

    private sealed class AcceptingAdmission : ITenantMembershipAuthorityAdmission
    {
        public Task ValidateMutationAsync(
            string actorAccountId, string authorityEvidenceDigest, string accountId,
            TenantMembershipMutation mutation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateExistingAsync(
            string accountId, TenantMembershipSnapshot membership,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AlwaysLeaseCoordinator : ILeaseCoordinator
    {
        private readonly ConcurrentDictionary<string, Lease> _held = new(StringComparer.Ordinal);
        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct)
        {
            var lease = new Lease(
                Guid.NewGuid().ToString("N"), resourceId, "test", Now, Now + duration, []);
            _held[lease.LeaseId] = lease;
            return Task.FromResult<Lease?>(lease);
        }
        public Task ReleaseAsync(Lease lease, CancellationToken ct)
        {
            _held.TryRemove(lease.LeaseId, out _);
            return Task.CompletedTask;
        }
        public bool Holds(string resourceId) => _held.Values.Any(row => row.ResourceId == resourceId);
        public IReadOnlyCollection<Lease> HeldLeases => _held.Values.ToArray();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// Since #3615 the cutover refuses to commit the marker while no v2 sign-in path is registered.
    /// This test drives the REAL marker in order to assert the post-flip refusal, so it declares a
    /// stub successor. The pre-flip refusal is proved separately.
    /// </summary>
    private sealed record TestSignInPath(string SignInPathName)
        : Harborline.Api.LocalNodeHost.Data.Identity.IInstallationAuthorityV2SignInPath;
}
