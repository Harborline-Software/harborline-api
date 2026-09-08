using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// The MTW-2 #3013 account-recovery saga: redeeming a recovery invitation rotates the target
/// account credential, advances its security version, and revokes EVERY account session across ALL
/// tenants and all three audiences before success (ADR 0160 R3-C/R3-E/R3-F, D3), emitting the
/// installation-audit <c>CredentialRecovered</c> event. Runs the REAL recovery store, revoker, and
/// rotation over real SQLite identity + web-session databases.
/// </summary>
public sealed class AccountCredentialRecoveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 5, 0, 0, TimeSpan.Zero);

    private static readonly string ArgonHash =
        "$argon2id$v=19$m=19456,t=2,p=1$" +
        Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]);

    private static readonly string NewArgonHash =
        "$argon2id$v=19$m=19456,t=2,p=1$" +
        Convert.ToBase64String(Enumerable.Repeat((byte)7, 16).ToArray()) + "$" +
        Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());

    [Fact]
    [Trait("PlanCard", "MTW-2-3013")]
    public async Task Recovery_Rotates_Credential_And_Revokes_All_Sessions_Across_All_Tenants()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"recovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var identityFactory = new IdentityContextFactory(Path.Combine(directory, "identity.db"));
            var sessionFactory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(
                Path.Combine(directory, "sessions.db"));
            await using (var identity = identityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
            }
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                await sessions.Database.MigrateAsync();
            }

            var time = new FixedTimeProvider(Now);
            var bootstrap = new InstallationFounderBootstrapService(identityFactory, time);
            await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                "founder",
                ArgonHash,
                Guid.NewGuid().ToString("N"),
                string.Join(":", Enumerable.Repeat("AB", 32)),
                "founder-bootstrap"));

            string accountId;
            await using (var identity = identityFactory.CreateDbContext())
            {
                var account = await identity.Accounts.AsNoTracking()
                    .SingleAsync(a => a.NormalizedUsername == "FOUNDER");
                accountId = account.AccountId;
                Assert.Equal(1, account.SecurityVersion);
                Assert.Equal(1, account.CredentialVersion);
            }

            // Seed sessions for the account across TWO tenants + an installation session + a challenge,
            // all pinning the current account security version (1).
            var tenantA = Guid.NewGuid().ToString("D");
            var tenantB = Guid.NewGuid().ToString("D");
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                sessions.UserSessions.Add(SelectedSession(accountId, tenantA, "sel-a", Digest("handle-a")));
                sessions.UserSessions.Add(SelectedSession(accountId, tenantB, "sel-b", Digest("handle-b")));
                sessions.InstallationSessions.Add(InstallationSession(accountId, "inst-1", Digest("handle-i")));
                sessions.AccountAccessChallenges.Add(Challenge(accountId, "chal-1", Digest("handle-c")));
                await sessions.SaveChangesAsync();
            }

            // Issue a recovery invitation targeting the founder account.
            var recoveryStore = new RecoveryInvitationStore(identityFactory);
            var issue = await recoveryStore.IssueAsync(new RecoveryInvitationSeed(
                TenantId: tenantA,
                IssuerAccountId: "admin-account",
                IssuerPrincipalId: "admin-principal",
                TargetAccountId: accountId,
                TargetNormalizedUsername: "FOUNDER",
                CommandFingerprint: Digest("recovery-command-1"),
                IssuedAtUtc: Now.AddMinutes(-1),
                AbsoluteExpiresAtUtc: Now.AddHours(1)));
            Assert.NotNull(issue);

            var revoker = new RecoverySessionRevoker(sessionFactory);
            var service = new AccountCredentialRecoveryService(
                recoveryStore, revoker, identityFactory, time);

            var result = await service.RecoverAsync(new AccountRecoveryCommand(
                issue!.RawCode, NewArgonHash, Guid.NewGuid().ToString("N")));

            Assert.Equal(AccountRecoveryStatus.Recovered, result.Status);
            Assert.Equal(accountId, result.AccountId);
            Assert.Equal(4, result.RevokedSessionCount); // 2 selected + 1 installation + 1 challenge

            // Credential rotated + security version advanced.
            await using (var identity = identityFactory.CreateDbContext())
            {
                var account = await identity.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == accountId);
                Assert.Equal(NewArgonHash, account.CredentialHash);
                Assert.Equal(2, account.CredentialVersion);
                Assert.Equal(2, account.SecurityVersion);

                // Audit: exactly one CredentialRecovered envelope on the installation chain.
                var recovered = await identity.AuditEnvelopes.AsNoTracking()
                    .CountAsync(e => e.EventType == InstallationIdentityAuditEventTypes.CredentialRecovered);
                Assert.Equal(1, recovered);
            }

            // Every audience is durably revoked in the SESSION store, across BOTH tenants.
            var selectedStore = new WebSelectedSessionStore(sessionFactory);
            await using (var sessions = sessionFactory.CreateDbContext())
            {
                var revocations = await sessions.Revocations.AsNoTracking()
                    .Where(r => r.AccountId == accountId)
                    .ToArrayAsync();
                Assert.Equal(4, revocations.Length);
                Assert.Equal(2, revocations.Count(r => r.Audience == WebCookieAudience.SelectedSession));
                Assert.Single(revocations, r => r.Audience == WebCookieAudience.InstallationSession);
                Assert.Single(revocations, r => r.Audience == WebCookieAudience.AccountChallenge);
                Assert.All(revocations, r => Assert.Equal("account-credential-recovery", r.ReasonCode));
            }

            // Revoked sessions fail live revalidation on next request: both the tombstone AND the
            // advanced security version (2) make the pinned-version-1 sessions unresolvable.
            Assert.Null(await selectedStore.FindActiveAsync(Digest("handle-a"), 2, Now.AddMinutes(1)));
            Assert.Null(await selectedStore.FindActiveAsync(Digest("handle-b"), 2, Now.AddMinutes(1)));

            // Single-use: replaying the completed recovery code is refused.
            var replay = await service.RecoverAsync(new AccountRecoveryCommand(
                issue.RawCode, NewArgonHash, Guid.NewGuid().ToString("N")));
            Assert.Equal(AccountRecoveryStatus.InvitationRefused, replay.Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WebUserSessionRecord SelectedSession(
        string accountId, string tenantId, string correlationId, string handleDigest) =>
        new(
            SessionCorrelationId: correlationId,
            AccountId: accountId,
            AccountSecurityVersion: 1,
            TenantId: tenantId,
            MembershipId: "membership-" + correlationId,
            MembershipOwnerVersion: 1,
            TenantPrincipalId: "principal-" + correlationId,
            CanonicalPartyReference: "party-" + correlationId,
            PinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-" + correlationId, 1)],
            AuthorizationEpoch: 1,
            HandleDigest: handleDigest,
            AntiforgeryStateId: "antiforgery-" + correlationId,
            CoordinationCorrelationId: "coordination-" + correlationId,
            IssuedAtUtc: Now.AddMinutes(-5),
            IdleExpiresAtUtc: Now.AddMinutes(10),
            AbsoluteExpiresAtUtc: Now.AddHours(1),
            OwnerVersion: 1);

    private static WebInstallationSessionRecord InstallationSession(
        string accountId, string correlationId, string handleDigest) =>
        new(
            SessionCorrelationId: correlationId,
            AccountId: accountId,
            AccountSecurityVersion: 1,
            InstallationGrantId: "installation-grant-1",
            InstallationGrantOwnerVersion: 1,
            AuthorizationEpoch: 1,
            HandleDigest: handleDigest,
            AntiforgeryStateId: "antiforgery-" + correlationId,
            CoordinationCorrelationId: "coordination-" + correlationId,
            IssuedAtUtc: Now.AddMinutes(-5),
            AbsoluteExpiresAtUtc: Now.AddMinutes(10),
            OwnerVersion: 1);

    private static WebAccountAccessChallengeRecord Challenge(
        string accountId, string challengeId, string handleDigest) =>
        new(
            ChallengeId: challengeId,
            AccountId: accountId,
            AccountSecurityVersion: 1,
            HandleDigest: handleDigest,
            CoordinationCorrelationId: "coordination-" + challengeId,
            IssuedAtUtc: Now.AddMinutes(-2),
            AbsoluteExpiresAtUtc: Now.AddMinutes(3),
            ConsumedAtUtc: null,
            RevokedAtUtc: null,
            OwnerVersion: 1);

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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
