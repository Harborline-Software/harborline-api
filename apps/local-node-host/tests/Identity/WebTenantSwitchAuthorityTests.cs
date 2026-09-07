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

public sealed class WebTenantSwitchAuthorityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 3, 0, 0, TimeSpan.Zero);
    private const string OldTenantId = "11111111-1111-1111-1111-111111111111";
    private const string TargetTenantId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    [Trait("PlanCard", "MTW-01C")]
    public async Task Switch_Completes_Both_Tenant_Heads_Before_Atomic_Rotation()
    {
        await using var fixture = await SwitchFixture.CreateAsync();

        var switched = await fixture.Authority.SwitchAsync(
            SwitchFixture.OldHandle,
            TargetTenantId);

        Assert.NotNull(switched);
        Assert.Equal(TargetTenantId, switched!.TenantId);
        Assert.Equal("target-tenant", switched.DisplayName);
        Assert.Equal(SwitchFixture.AbsoluteExpiry, switched.ExpiresAtUtc);
        Assert.All(
            fixture.OldStore.VisibilityObservedDuringFinalize,
            observation =>
            {
                Assert.Equal(2, observation.SessionCount);
                Assert.Equal(0, observation.RevocationCount);
            });
        Assert.All(
            fixture.TargetStore.VisibilityObservedDuringFinalize,
            observation =>
            {
                Assert.Equal(2, observation.SessionCount);
                Assert.Equal(0, observation.RevocationCount);
            });

        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var home = await identity.Coordinators.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCoordinatorState.Completed, home.State);
            Assert.Equal(WebTenantSwitchAuthority.CommandType, home.CommandType);
            Assert.Equal(
                "WebTenantSwitchCompleted",
                (await identity.AuditEnvelopes.AsNoTracking()
                    .OrderBy(row => row.Sequence)
                    .LastAsync()).EventType);
        }
        await using (var sessions = fixture.SessionFactory.CreateDbContext())
        {
            var durable = await sessions.UserSessions.AsNoTracking().ToArrayAsync();
            Assert.Equal(3, durable.Length);
            var replacement = Assert.Single(
                durable,
                row => row.CoordinationCorrelationId != "old-selection" &&
                       row.CoordinationCorrelationId != "other-selection");
            Assert.Equal(TargetTenantId, replacement.TenantId);
            Assert.Equal(SwitchFixture.AbsoluteExpiry, replacement.AbsoluteExpiresAtUtc);

            var revocation = await sessions.Revocations.AsNoTracking().SingleAsync();
            Assert.Equal("old-session", revocation.SubjectCorrelationId);
            Assert.Equal(replacement.SessionCorrelationId, revocation.SupersededByCorrelationId);
            Assert.Equal(WebTenantSwitchAuthority.ReasonCode, revocation.ReasonCode);
        }

        var store = new WebSelectedSessionStore(fixture.SessionFactory);
        Assert.Null(await store.FindActiveAsync(
            Digest(SwitchFixture.OldHandle),
            fixture.AccountSecurityVersion,
            Now));
        Assert.NotNull(await store.FindActiveAsync(
            Digest(SwitchFixture.OtherHandle),
            fixture.AccountSecurityVersion,
            Now));
        Assert.NotNull(await store.FindActiveAsync(
            Digest(switched.Handle),
            fixture.AccountSecurityVersion,
            Now));
    }

    [Fact]
    [Trait("PlanCard", "MTW-01C")]
    public async Task Interrupted_Tenant_Finalization_Leaves_Old_Live_And_Retry_Rolls_Forward()
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        fixture.TargetStore.ThrowAfterFinalizeOnce = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Committing,
                (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        await using (var sessions = fixture.SessionFactory.CreateDbContext())
        {
            Assert.Equal(2, await sessions.UserSessions.CountAsync());
            Assert.Empty(await sessions.Revocations.AsNoTracking().ToArrayAsync());
        }
        var store = new WebSelectedSessionStore(fixture.SessionFactory);
        Assert.NotNull(await store.FindActiveAsync(
            Digest(SwitchFixture.OldHandle),
            fixture.AccountSecurityVersion,
            Now));

        var recovered = await fixture.Authority.SwitchAsync(
            SwitchFixture.OldHandle,
            TargetTenantId);

        Assert.NotNull(recovered);
        Assert.Null(await store.FindActiveAsync(
            Digest(SwitchFixture.OldHandle),
            fixture.AccountSecurityVersion,
            Now));
        await using var completed = fixture.IdentityFactory.CreateDbContext();
        Assert.Equal(
            InstallationIdentityCoordinatorState.Completed,
            (await completed.Coordinators.AsNoTracking().SingleAsync()).State);
    }

    private sealed class SwitchFixture : IAsyncDisposable
    {
        internal const string OldHandle =
            "old-selected-handle-with-at-least-256-bits-of-fixture-entropy";
        internal const string OtherHandle =
            "other-browser-handle-with-at-least-256-bits-of-fixture-entropy";
        internal static readonly DateTimeOffset AbsoluteExpiry = Now.AddHours(8);

        private readonly string _directory;

        private SwitchFixture(
            string directory,
            IdentityContextFactory identityFactory,
            WebAccountAccessChallengeIssuerTests.SessionContextFactory sessionFactory,
            long accountSecurityVersion,
            RecordingMembershipStore oldStore,
            RecordingMembershipStore targetStore,
            WebTenantSwitchAuthority authority)
        {
            _directory = directory;
            IdentityFactory = identityFactory;
            SessionFactory = sessionFactory;
            AccountSecurityVersion = accountSecurityVersion;
            OldStore = oldStore;
            TargetStore = targetStore;
            Authority = authority;
        }

        internal IdentityContextFactory IdentityFactory { get; }
        internal WebAccountAccessChallengeIssuerTests.SessionContextFactory SessionFactory { get; }
        internal long AccountSecurityVersion { get; }
        internal RecordingMembershipStore OldStore { get; }
        internal RecordingMembershipStore TargetStore { get; }
        internal WebTenantSwitchAuthority Authority { get; }

        internal static async Task<SwitchFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"tenant-switch-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var identityFactory = new IdentityContextFactory(Path.Combine(directory, "identity.db"));
            var sessionFactory =
                new WebAccountAccessChallengeIssuerTests.SessionContextFactory(
                    Path.Combine(directory, "sessions.db"));
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
                new FixedTimeProvider(Now.AddMinutes(-5)));
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

            var oldMembership = Membership(
                "old-membership",
                account.AccountId,
                OldTenantId,
                "old-principal",
                "old-grant");
            var targetMembership = Membership(
                "target-membership",
                account.AccountId,
                TargetTenantId,
                "target-principal",
                "target-grant");
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                sessions.UserSessions.AddRange(
                    Session(
                        OldHandle,
                        "old-session",
                        oldMembership,
                        "old-party",
                        "old-selection"),
                    Session(
                        OtherHandle,
                        "other-session",
                        oldMembership,
                        "old-party",
                        "other-selection"));
                await sessions.SaveChangesAsync();
            }

            var oldStore = new RecordingMembershipStore(oldMembership, sessionFactory);
            var targetStore = new RecordingMembershipStore(targetMembership, sessionFactory);
            var resolver = new FixedPartitionResolver(
                new TenantIdentityAuthorityPartition(
                    OldTenantId,
                    oldStore,
                    new AlwaysLeaseCoordinator()),
                new TenantIdentityAuthorityPartition(
                    TargetTenantId,
                    targetStore,
                    new AlwaysLeaseCoordinator()));
            var coordinator = new InstallationIdentityCoordinatorService(
                identityFactory,
                resolver,
                new AcceptingAdmission(),
                new FixedTimeProvider(Now),
                TestAuthorization.Gate(true));
            var selectedStore = new WebSelectedSessionStore(sessionFactory);
            var authority = new WebTenantSwitchAuthority(
                identityFactory,
                sessionFactory,
                selectedStore,
                new FixedCandidateLocator(),
                coordinator,
                resolver,
                new FixedPartyReader(),
                Options.Create(new SessionOptions()),
                new FixedTimeProvider(Now));
            return new SwitchFixture(
                directory,
                identityFactory,
                sessionFactory,
                account.SecurityVersion,
                oldStore,
                targetStore,
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
        TenantMembershipSnapshot membership,
        IDbContextFactory<NodeLocalWebSessionDbContext> sessionFactory)
        : ITenantMembershipAuthorityStore
    {
        private TenantSessionSelectionReceipt? _selectionReceipt;
        private TenantSessionRevocationReceipt? _revocationReceipt;
        private string? _selectionIntentDigest;
        private string? _revocationIntentDigest;

        public string TenantId => membership.TenantId;
        public bool ThrowAfterFinalizeOnce { get; set; }
        public List<VisibilityObservation> VisibilityObservedDuringFinalize { get; } = [];

        public Task<TenantMembershipSnapshot?> GetMembershipAsync(
            string accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult<TenantMembershipSnapshot?>(
                accountId == membership.AccountId ? membership : null);

        public Task<bool> IsAdmissionBlockedAsync(
            string accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task PrepareSessionSelectionAsync(
            string correlationId,
            string commandFingerprint,
            string accountId,
            string membershipId,
            string payloadDigest,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            Assert.Equal(membership.AccountId, accountId);
            Assert.Equal(membership.MembershipId, membershipId);
            _selectionIntentDigest = InstallationAuditIntegrity.Hash(
                correlationId,
                commandFingerprint,
                accountId,
                membershipId,
                payloadDigest);
            return Task.CompletedTask;
        }

        public async Task<TenantSessionSelectionReceipt> FinalizeSessionSelectionAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            await RecordVisibilityAsync(cancellationToken);
            if (ThrowAfterFinalizeOnce)
            {
                ThrowAfterFinalizeOnce = false;
                throw new InvalidOperationException("injected target audit interruption");
            }
            return _selectionReceipt ??= new TenantSessionSelectionReceipt(
                TenantId,
                DocumentOwnerVersion: 2,
                membership.MembershipId,
                AuditSequence: 1,
                AuditHeadHash: new string('A', 64),
                IntentDigest: _selectionIntentDigest!,
                HomeDecisionDigest: new string('B', 64));
        }

        public Task PrepareSessionRevocationAsync(
            string correlationId,
            string commandFingerprint,
            string accountId,
            string membershipId,
            string sessionCorrelationId,
            string payloadDigest,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            Assert.Equal(membership.AccountId, accountId);
            Assert.Equal(membership.MembershipId, membershipId);
            _revocationIntentDigest = InstallationAuditIntegrity.Hash(
                correlationId,
                commandFingerprint,
                accountId,
                membershipId,
                sessionCorrelationId,
                payloadDigest);
            return Task.CompletedTask;
        }

        public async Task<TenantSessionRevocationReceipt> FinalizeSessionRevocationAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            await RecordVisibilityAsync(cancellationToken);
            return _revocationReceipt ??= new TenantSessionRevocationReceipt(
                TenantId,
                DocumentOwnerVersion: 2,
                membership.MembershipId,
                SessionCorrelationId: "old-session",
                AuditSequence: 1,
                AuditHeadHash: new string('C', 64),
                IntentDigest: _revocationIntentDigest!,
                HomeDecisionDigest: new string('D', 64));
        }

        private async Task RecordVisibilityAsync(CancellationToken cancellationToken)
        {
            await using var sessions = await sessionFactory.CreateDbContextAsync(cancellationToken);
            VisibilityObservedDuringFinalize.Add(new VisibilityObservation(
                await sessions.UserSessions.CountAsync(cancellationToken),
                await sessions.Revocations.CountAsync(cancellationToken)));
        }

        public Task AbortSessionSelectionAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AbortSessionRevocationAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task PrepareAsync(
            string correlationId,
            string commandFingerprint,
            string accountId,
            string actorAccountId,
            string authorityEvidenceDigest,
            TenantMembershipMutation mutation,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AbortAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantMembershipIntentState?> GetIntentStateAsync(
            string correlationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<TenantMembershipIntentState?>(null);
    }

    private sealed record VisibilityObservation(int SessionCount, int RevocationCount);

    private sealed class FixedPartitionResolver(params TenantIdentityAuthorityPartition[] partitions)
        : ITenantIdentityAuthorityPartitionResolver
    {
        private readonly IReadOnlyDictionary<string, TenantIdentityAuthorityPartition> _partitions =
            partitions.ToDictionary(item => item.TenantId, StringComparer.Ordinal);

        public Task<TenantIdentityAuthorityPartition> ResolveAsync(
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_partitions[tenantId]);
    }

    private sealed class FixedCandidateLocator : IInstallationTenantCandidateLocator
    {
        private static readonly IReadOnlyList<InstallationTenantCandidate> Candidates =
        [
            new(
                new TenantId(OldTenantId),
                "old-tenant",
                TenantMembershipStatus.Active),
            new(
                new TenantId(TargetTenantId),
                "target-tenant",
                TenantMembershipStatus.Active),
        ];

        public Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
            PrincipalUserId accountPrincipal,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Candidates);

        public Task<IReadOnlyList<InstallationTenantCandidate>> ListForAccountAsync(
            PrincipalUserId accountPrincipal,
            string? excludedCorrelationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Candidates);
    }

    private sealed class FixedPartyReader : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant,
            PrincipalUserId user,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(new CanonicalPartyBinding(
                tenant,
                user,
                new CanonicalPartyReference(
                    tenant.Value == OldTenantId ? "old-party" : "target-party")));
    }

    private sealed class AcceptingAdmission : ITenantMembershipAuthorityAdmission
    {
        public Task ValidateMutationAsync(
            string actorAccountId,
            string authorityEvidenceDigest,
            string accountId,
            TenantMembershipMutation mutation,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ValidateExistingAsync(
            string accountId,
            TenantMembershipSnapshot membership,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class AlwaysLeaseCoordinator : ILeaseCoordinator
    {
        private readonly ConcurrentDictionary<string, Lease> _held = new(StringComparer.Ordinal);

        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct)
        {
            var lease = new Lease(
                Guid.NewGuid().ToString("N"),
                resourceId,
                "test",
                Now,
                Now + duration,
                []);
            _held[lease.LeaseId] = lease;
            return Task.FromResult<Lease?>(lease);
        }

        public Task ReleaseAsync(Lease lease, CancellationToken ct)
        {
            _held.TryRemove(lease.LeaseId, out _);
            return Task.CompletedTask;
        }

        public bool Holds(string resourceId) =>
            _held.Values.Any(item => item.ResourceId == resourceId);

        public IReadOnlyCollection<Lease> HeldLeases => _held.Values.ToArray();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static TenantMembershipSnapshot Membership(
        string membershipId,
        string accountId,
        string tenantId,
        string principalId,
        string grantId) =>
        new(
            membershipId,
            accountId,
            tenantId,
            principalId,
            grantId,
            GrantOwnerVersion: 4,
            AuthorizationEpoch: 7,
            TenantMembershipStatus.Active,
            OwnerVersion: 3);

    private static WebUserSessionRecord Session(
        string handle,
        string sessionCorrelationId,
        TenantMembershipSnapshot membership,
        string party,
        string coordinationCorrelationId) =>
        new(
            SessionCorrelationId: sessionCorrelationId,
            AccountId: membership.AccountId,
            AccountSecurityVersion: 1,
            TenantId: membership.TenantId,
            MembershipId: membership.MembershipId,
            MembershipOwnerVersion: membership.OwnerVersion,
            TenantPrincipalId: membership.CanonicalPrincipalId,
            CanonicalPartyReference: party,
            PinnedGrantOwnerVersions:
                [new PinnedGrantOwnerVersion(membership.GrantId, membership.GrantOwnerVersion)],
            AuthorizationEpoch: membership.AuthorizationEpoch,
            HandleDigest: Digest(handle),
            AntiforgeryStateId: $"{sessionCorrelationId}-antiforgery",
            CoordinationCorrelationId: coordinationCorrelationId,
            IssuedAtUtc: Now.AddMinutes(-5),
            IdleExpiresAtUtc: Now.AddMinutes(25),
            AbsoluteExpiresAtUtc: SwitchFixture.AbsoluteExpiry,
            OwnerVersion: 1);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
