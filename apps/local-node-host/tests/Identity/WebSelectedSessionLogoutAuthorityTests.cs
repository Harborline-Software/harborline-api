using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.Kernel.Lease;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class WebSelectedSessionLogoutAuthorityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "SES-08C")]
    public async Task Committed_Revocation_Precedes_Audit_Finalization_And_Retry_Rolls_Forward()
    {
        await using var fixture = await LogoutFixture.CreateAsync();
        fixture.Store.ThrowAfterFinalizeOnce = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.LogoutAsync(LogoutFixture.RawHandle));

        Assert.Equal(new[] { true }, fixture.Store.RevocationObservedDuringFinalize);
        Assert.Null(await fixture.SelectedStore.FindActiveAsync(
            Digest(LogoutFixture.RawHandle),
            fixture.AccountSecurityVersion,
            Now.AddMinutes(1)));
        await using (var interrupted = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Committing,
                (await interrupted.Coordinators.AsNoTracking().SingleAsync()).State);
        }

        Assert.True(await fixture.Authority.LogoutAsync(LogoutFixture.RawHandle));
        Assert.True(await fixture.Authority.LogoutAsync(LogoutFixture.RawHandle));

        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var home = await identity.Coordinators.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCoordinatorState.Completed, home.State);
            Assert.Equal(WebSelectedSessionLogoutAuthority.CommandType, home.CommandType);
            Assert.Equal(
                "WebUserSessionLogoutCompleted",
                (await identity.AuditEnvelopes.AsNoTracking().OrderBy(row => row.Sequence).LastAsync())
                    .EventType);
        }
        await using (var sessions = fixture.SessionFactory.CreateDbContext())
        {
            var revocation = await sessions.Revocations.AsNoTracking().SingleAsync();
            Assert.Equal(WebCookieAudience.SelectedSession, revocation.Audience);
            Assert.Equal(WebSelectedSessionLogoutAuthority.ReasonCode, revocation.ReasonCode);
            Assert.Equal("selected-session", revocation.SubjectCorrelationId);
        }
    }

    private sealed class LogoutFixture : IAsyncDisposable
    {
        private readonly string _identityPath;
        private readonly string _sessionPath;

        private LogoutFixture(
            string identityPath,
            string sessionPath,
            InstallationFounderBootstrapServiceTests.IdentityContextFactory identityFactory,
            WebAccountAccessChallengeIssuerTests.SessionContextFactory sessionFactory,
            RecordingMembershipStore store,
            WebSelectedSessionStore selectedStore,
            WebSelectedSessionLogoutAuthority authority,
            long accountSecurityVersion)
        {
            _identityPath = identityPath;
            _sessionPath = sessionPath;
            IdentityFactory = identityFactory;
            SessionFactory = sessionFactory;
            Store = store;
            SelectedStore = selectedStore;
            Authority = authority;
            AccountSecurityVersion = accountSecurityVersion;
        }

        public const string RawHandle = "selected-logout-handle-with-fixture-entropy";
        public InstallationFounderBootstrapServiceTests.IdentityContextFactory IdentityFactory { get; }
        public WebAccountAccessChallengeIssuerTests.SessionContextFactory SessionFactory { get; }
        public RecordingMembershipStore Store { get; }
        public WebSelectedSessionStore SelectedStore { get; }
        public WebSelectedSessionLogoutAuthority Authority { get; }
        public long AccountSecurityVersion { get; }

        public static async Task<LogoutFixture> CreateAsync()
        {
            var identityPath = Path.Combine(Path.GetTempPath(), $"logout-home-{Guid.NewGuid():N}.db");
            var sessionPath = Path.Combine(Path.GetTempPath(), $"logout-session-{Guid.NewGuid():N}.db");
            var identityFactory = new InstallationFounderBootstrapServiceTests.IdentityContextFactory(
                identityPath);
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

            var tenantId = Guid.NewGuid().ToString("D");
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                sessions.UserSessions.Add(new WebUserSessionRecord(
                    SessionCorrelationId: "selected-session",
                    AccountId: account.AccountId,
                    AccountSecurityVersion: account.SecurityVersion,
                    TenantId: tenantId,
                    MembershipId: "membership-1",
                    MembershipOwnerVersion: 3,
                    TenantPrincipalId: "principal-1",
                    CanonicalPartyReference: "party-1",
                    PinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-1", 4)],
                    AuthorizationEpoch: 5,
                    HandleDigest: Digest(RawHandle),
                    AntiforgeryStateId: "antiforgery-1",
                    CoordinationCorrelationId: "selection-correlation",
                    IssuedAtUtc: Now,
                    IdleExpiresAtUtc: Now.AddMinutes(15),
                    AbsoluteExpiresAtUtc: Now.AddHours(1),
                    OwnerVersion: 1));
                await sessions.SaveChangesAsync();
            }

            var store = new RecordingMembershipStore(tenantId, sessionFactory);
            var resolver = new FixedPartitionResolver(new TenantIdentityAuthorityPartition(
                tenantId,
                store,
                new AlwaysLeaseCoordinator()));
            var selectedStore = new WebSelectedSessionStore(sessionFactory);
            var authority = new WebSelectedSessionLogoutAuthority(
                identityFactory,
                selectedStore,
                resolver,
                new FixedTimeProvider(Now.AddMinutes(1)));
            return new LogoutFixture(
                identityPath,
                sessionPath,
                identityFactory,
                sessionFactory,
                store,
                selectedStore,
                authority,
                account.SecurityVersion);
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            File.Delete(_identityPath);
            File.Delete(_sessionPath);
        }
    }

    private sealed class RecordingMembershipStore(
        string tenantId,
        IDbContextFactory<NodeLocalWebSessionDbContext> sessionFactory)
        : ITenantMembershipAuthorityStore
    {
        private TenantSessionRevocationReceipt? _receipt;
        private string? _intentDigest;

        public string TenantId { get; } = tenantId;
        public bool ThrowAfterFinalizeOnce { get; set; }
        public List<bool> RevocationObservedDuringFinalize { get; } = [];

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
            Assert.Equal("membership-1", membershipId);
            Assert.Equal("selected-session", sessionCorrelationId);
            _intentDigest = InstallationAuditIntegrity.Hash(
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
            await using var sessions = await sessionFactory.CreateDbContextAsync(cancellationToken);
            RevocationObservedDuringFinalize.Add(await sessions.Revocations.AsNoTracking().AnyAsync(
                row => row.Audience == WebCookieAudience.SelectedSession &&
                       row.SubjectCorrelationId == "selected-session",
                cancellationToken));
            var receipt = _receipt ??= new TenantSessionRevocationReceipt(
                TenantId,
                DocumentOwnerVersion: 2,
                MembershipId: "membership-1",
                SessionCorrelationId: "selected-session",
                AuditSequence: 1,
                AuditHeadHash: new string('A', 64),
                IntentDigest: _intentDigest ?? throw new InvalidOperationException("prepare missing"),
                HomeDecisionDigest: new string('C', 64));
            if (ThrowAfterFinalizeOnce)
            {
                ThrowAfterFinalizeOnce = false;
                throw new InvalidOperationException("injected response loss after tenant finalization");
            }
            return receipt;
        }

        public Task AbortSessionRevocationAsync(
            string correlationId,
            string commandFingerprint,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;

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
        public Task<TenantMembershipSnapshot?> GetMembershipAsync(
            string accountId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantMembershipSnapshot?>(null);
        public Task<TenantMembershipIntentState?> GetIntentStateAsync(
            string correlationId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantMembershipIntentState?>(null);
        public Task<bool> IsAdmissionBlockedAsync(
            string accountId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task PrepareSessionSelectionAsync(
            string correlationId, string commandFingerprint, string accountId, string membershipId,
            string payloadDigest, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<TenantSessionSelectionReceipt> FinalizeSessionSelectionAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AbortSessionSelectionAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FixedPartitionResolver(TenantIdentityAuthorityPartition partition)
        : ITenantIdentityAuthorityPartitionResolver
    {
        public Task<TenantIdentityAuthorityPartition> ResolveAsync(
            string tenantId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(partition.TenantId, tenantId);
            return Task.FromResult(partition);
        }
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
}
