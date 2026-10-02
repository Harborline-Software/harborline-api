using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Harborline.Api.Kernel.Lease;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class WebSelectedSessionLogoutAuthorityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Recovery_pages_past_stalled_tied_homes_and_revisits_them_after_delivering_a_later_audit()
    {
        await using var fixture = await LogoutFixture.CreateAsync();
        fixture.IdentityStop.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Authority.LogoutAsync(LogoutFixture.RawHandle));
        await using (var db = fixture.IdentityFactory.CreateDbContext())
        {
            var owed = await db.Coordinators.AsNoTracking().SingleAsync();
            foreach (var id in new[] { "stalled-a", "stalled-b" })
            {
                var stalled = JsonSerializer.Deserialize<InstallationIdentityCoordinatorRecord>(JsonSerializer.Serialize(owed))!;
                stalled.CorrelationId = id;
                stalled.CreatedAtUtc = Now.AddDays(-1);
                db.Coordinators.Add(stalled);
            }
            await db.SaveChangesAsync();
        }
        var attempts = new Dictionary<string, int>();
        var recovery = fixture.Restart(inner => new StalledRecovery(inner, attempts));
        await recovery.RecoverPendingAsync(limit: 2);
        await using (var db = fixture.IdentityFactory.CreateDbContext())
            Assert.DoesNotContain(await db.AuditEnvelopes.ToListAsync(), row => row.EventType == "WebUserSessionLogoutCompleted");
        await recovery.RecoverPendingAsync(limit: 2);
        await recovery.RecoverPendingAsync(limit: 2);
        Assert.Equal(2, attempts["stalled-a"]);
        Assert.Equal(2, attempts["stalled-b"]);
        await using (var db = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Single(await db.AuditEnvelopes.ToListAsync(), row => row.EventType == "WebUserSessionLogoutCompleted");
            Assert.All(await db.Coordinators.Where(row => row.CorrelationId == "stalled-a" || row.CorrelationId == "stalled-b").ToListAsync(),
                row => Assert.Equal(InstallationIdentityCoordinatorState.Preparing, row.State));
        }
    }

    private sealed class StalledRecovery(IInstallationIdentityHomeRecovery inner, Dictionary<string, int> attempts)
        : IInstallationIdentityHomeRecovery
    {
        public string CommandType => inner.CommandType;
        public Task RecoverAsync(InstallationIdentityCoordinatorRecord home, CancellationToken cancellationToken)
        {
            if (!home.CorrelationId.StartsWith("stalled-", StringComparison.Ordinal))
                return inner.RecoverAsync(home, cancellationToken);
            attempts[home.CorrelationId] = attempts.GetValueOrDefault(home.CorrelationId) + 1;
            return home.CorrelationId == "stalled-a" ? throw new InvalidOperationException("stalled recovery") : Task.CompletedTask;
        }
    }

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

    /// <summary>
    /// T-1048 (ck-6): the process stops after the session revocation commits and before the home
    /// leaves Preparing. The client cannot retry (antiforgery refuses a revoked session), so a fresh
    /// authority over the same files, driven only by the startup recovery drain, must write the one
    /// Completed envelope. A second drain adds none.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-6")]
    public async Task A_crash_after_the_revocation_commits_owes_one_envelope_that_the_recovery_drain_writes()
    {
        await using var fixture = await LogoutFixture.CreateAsync();
        fixture.IdentityStop.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.LogoutAsync(LogoutFixture.RawHandle));
        await using (var interrupted = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Preparing,
                (await interrupted.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        Assert.NotNull(await fixture.SelectedStore.FindRevocationAsync("selected-session"));
        Assert.Empty(await CompletedEnvelopesAsync(fixture.IdentityFactory));

        var recovery = fixture.Restart();
        await recovery.RecoverPendingAsync();
        await recovery.RecoverPendingAsync();

        await using var identity = fixture.IdentityFactory.CreateDbContext();
        var home = await identity.Coordinators.AsNoTracking().SingleAsync();
        Assert.Equal(InstallationIdentityCoordinatorState.Completed, home.State);
        var envelope = Assert.Single(await CompletedEnvelopesAsync(fixture.IdentityFactory));
        Assert.Equal(home.CorrelationId, envelope.CorrelationId);
        Assert.Equal(Now.AddMinutes(1), envelope.OccurredAtUtc);
    }

    /// <summary>
    /// T-1048 (ck-6): the process stops before the revocation commits, so the logout never took
    /// effect. The recovery drain leaves the Preparing home alone and writes no envelope.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-6")]
    public async Task A_logout_stopped_before_the_revocation_commits_gets_no_envelope_from_the_recovery_drain()
    {
        await using var fixture = await LogoutFixture.CreateAsync();
        fixture.SessionStop.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.LogoutAsync(LogoutFixture.RawHandle));
        Assert.Null(await fixture.SelectedStore.FindRevocationAsync("selected-session"));

        await fixture.Restart().RecoverPendingAsync();

        Assert.Empty(await CompletedEnvelopesAsync(fixture.IdentityFactory));
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        Assert.Equal(
            InstallationIdentityCoordinatorState.Preparing,
            (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
    }

    private static async Task<InstallationAuditEnvelopeRecord[]> CompletedEnvelopesAsync(
        IDbContextFactory<NodeLocalInstallationIdentityDbContext> factory)
    {
        await using var identity = factory.CreateDbContext();
        return await identity.AuditEnvelopes.AsNoTracking()
            .Where(row => row.EventType == "WebUserSessionLogoutCompleted")
            .ToArrayAsync();
    }

    /// <summary>Stops the process at one SaveChanges, before it commits, once armed.</summary>
    private sealed class StopBeforeSave(Func<DbContext, bool> stopsHere) : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Armed && stopsHere(eventData.Context!))
            {
                Armed = false;
                throw new InvalidOperationException("injected process stop before this commit");
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class StoppableIdentityFactory(string databasePath, IInterceptor stop)
        : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
                .AddInterceptors(stop)
                .Options);
    }

    private sealed class StoppableSessionFactory(string databasePath, IInterceptor stop)
        : IDbContextFactory<NodeLocalWebSessionDbContext>
    {
        public NodeLocalWebSessionDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<NodeLocalWebSessionDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(NodeLocalWebSessionDbContext.MigrationsHistoryTableName))
                .AddInterceptors(stop)
                .Options);
    }

    private sealed class AcceptingAdmission : ITenantMembershipAuthorityAdmission
    {
        public Task ValidateMutationAsync(
            string actorAccountId, string authorityEvidenceDigest, string accountId,
            TenantMembershipMutation mutation, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<long> ValidateExistingAsync(
            string accountId, TenantMembershipSnapshot membership,
            CancellationToken cancellationToken) => Task.FromResult(membership.AuthorizationEpoch);
    }

    private sealed class LogoutFixture : IAsyncDisposable
    {
        private readonly string _identityPath;
        private readonly string _sessionPath;

        private LogoutFixture(
            string identityPath,
            string sessionPath,
            StoppableIdentityFactory identityFactory,
            StoppableSessionFactory sessionFactory,
            StopBeforeSave identityStop,
            StopBeforeSave sessionStop,
            FixedPartitionResolver resolver,
            RecordingMembershipStore store,
            WebSelectedSessionStore selectedStore,
            WebSelectedSessionLogoutAuthority authority,
            long accountSecurityVersion)
        {
            _identityPath = identityPath;
            _sessionPath = sessionPath;
            IdentityFactory = identityFactory;
            SessionFactory = sessionFactory;
            IdentityStop = identityStop;
            SessionStop = sessionStop;
            _resolver = resolver;
            Store = store;
            SelectedStore = selectedStore;
            Authority = authority;
            AccountSecurityVersion = accountSecurityVersion;
        }

        public const string RawHandle = "selected-logout-handle-with-fixture-entropy";
        private readonly FixedPartitionResolver _resolver;

        public StoppableIdentityFactory IdentityFactory { get; }
        public StoppableSessionFactory SessionFactory { get; }

        /// <summary>Armed: stops before the home leaves Preparing, after the revocation commits.</summary>
        public StopBeforeSave IdentityStop { get; }

        /// <summary>Armed: stops before the revocation commits.</summary>
        public StopBeforeSave SessionStop { get; }
        public RecordingMembershipStore Store { get; }
        public WebSelectedSessionStore SelectedStore { get; }
        public WebSelectedSessionLogoutAuthority Authority { get; }
        public long AccountSecurityVersion { get; }

        /// <summary>
        /// A fresh host over the same identity and session files, as its recovery drain. The
        /// recording tenant store stands in for the tenant file, so it carries over.
        /// </summary>
        public InstallationIdentityCoordinatorRecoveryService Restart(
            Func<IInstallationIdentityHomeRecovery, IInstallationIdentityHomeRecovery>? decorate = null)
        {
            var identityFactory = new InstallationFounderBootstrapServiceTests.IdentityContextFactory(
                _identityPath);
            var sessionFactory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(_sessionPath);
            var authority = new WebSelectedSessionLogoutAuthority(
                identityFactory,
                new WebSelectedSessionStore(sessionFactory),
                _resolver,
                new FixedTimeProvider(Now.AddMinutes(1)));
            return new InstallationIdentityCoordinatorRecoveryService(
                identityFactory,
                new InstallationIdentityCoordinatorService(
                    identityFactory,
                    _resolver,
                    new AcceptingAdmission(),
                    new FixedTimeProvider(Now.AddMinutes(1)),
                    TestAuthorization.Gate(true)),
                homeRecoveries: [decorate?.Invoke(authority) ?? authority]);
        }

        public static async Task<LogoutFixture> CreateAsync()
        {
            var identityPath = Path.Combine(Path.GetTempPath(), $"logout-home-{Guid.NewGuid():N}.db");
            var sessionPath = Path.Combine(Path.GetTempPath(), $"logout-session-{Guid.NewGuid():N}.db");
            var identityStop = new StopBeforeSave(context => context.ChangeTracker
                .Entries<InstallationIdentityCoordinatorRecord>()
                .Any(entry => entry.State == EntityState.Modified &&
                              entry.Entity.State == InstallationIdentityCoordinatorState.Committing));
            var sessionStop = new StopBeforeSave(context => context.ChangeTracker
                .Entries<WebSessionRevocationRecord>()
                .Any(entry => entry.State == EntityState.Added));
            var identityFactory = new StoppableIdentityFactory(identityPath, identityStop);
            var sessionFactory = new StoppableSessionFactory(sessionPath, sessionStop);
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
                identityStop,
                sessionStop,
                resolver,
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
