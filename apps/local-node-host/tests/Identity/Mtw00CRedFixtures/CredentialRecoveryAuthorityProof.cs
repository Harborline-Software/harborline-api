using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity.Mtw00CRedFixtures;

/// <summary>
/// Production-bound proofs for the three MTW-01G recovery authorities. Every proof uses the real
/// recovery store and, where applicable, the real recovery saga over file-backed SQLite stores.
/// Failures throw independently of <see cref="MissingAuthorityException"/>, so an authority can be
/// marked wired only while its production invariant actually holds.
/// </summary>
internal static class CredentialRecoveryAuthorityProof
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 4, 0, 0, TimeSpan.Zero);

    private static readonly string InitialCredentialHash =
        "$argon2id$v=19$m=19456,t=2,p=1$" +
        Convert.ToBase64String(new byte[16]) + "$" +
        Convert.ToBase64String(new byte[32]);

    private static readonly string RecoveredCredentialHash =
        "$argon2id$v=19$m=19456,t=2,p=1$" +
        Convert.ToBase64String(Enumerable.Repeat((byte)7, 16).ToArray()) + "$" +
        Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());

    internal static void ProvePurposeBinding() =>
        ProvePurposeBindingAsync().GetAwaiter().GetResult();

    internal static void ProveOneTimeConsumeAcrossRestart() =>
        ProveOneTimeConsumeAcrossRestartAsync().GetAwaiter().GetResult();

    internal static void ProveAllAudiencesRevokedBeforeSuccess() =>
        ProveAllAudiencesRevokedBeforeSuccessAsync().GetAwaiter().GetResult();

    private static async Task ProvePurposeBindingAsync()
    {
        await using var proof = await RecoveryProofStore.CreateAsync().ConfigureAwait(false);
        var invitation = await proof.RecoveryStore
            .IssueAsync(proof.RecoverySeed("purpose-binding"))
            .ConfigureAwait(false);
        Require(invitation is not null, "The recovery authority did not issue its purpose-bound code.");

        var setupStore = new AccountSetupInvitationStore(proof.IdentityFactory);

        // ── The purpose fence itself, driven through the production predicate ────────────────────
        // This is the only assertion in this proof that a broken purpose fence can flip. It presents
        // a REAL setup invitation under the WRONG purpose: the row exists, its digest matches, its
        // tenant matches — `expectedPurpose` is the single discriminating input. Deleting either half
        // of AccountSetupInvitationStore.ConsumeAndReadAsync's fence (the
        // `expectedPurpose != AccountSetup` early return AND the `item.Purpose == expectedPurpose`
        // query predicate) makes this consume SUCCEED and this proof throw.
        var setupTenantId = Guid.NewGuid().ToString("D");
        var setupInvitation = await setupStore
            .IssueAsync(SetupSeed(setupTenantId, "purpose-binding-setup"))
            .ConfigureAwait(false);
        Require(
            setupInvitation is not null,
            "The setup authority did not issue the code this fence is proved against.");

        var crossPurpose = await setupStore
            .ConsumeAndReadAsync(
                setupInvitation!.RawCode,
                setupTenantId,
                WebSetupInvitationPurpose.MembershipSetup,
                Now)
            .ConfigureAwait(false);
        Require(
            crossPurpose is null,
            "A setup code was consumed under a purpose it was not issued for.");

        // Positive control for the refusal above. Without it, that assertion would also hold on a
        // store that refuses EVERYTHING — the refusal has to be attributable to the purpose fence and
        // to nothing else. It doubles as an unconsumed check: a fence that leaked would already have
        // consumed the row above, so this second consume would refuse.
        var ownPurpose = await setupStore
            .ConsumeAndReadAsync(
                setupInvitation.RawCode,
                setupTenantId,
                WebSetupInvitationPurpose.AccountSetup,
                Now)
            .ConfigureAwait(false);
        Require(
            ownPurpose is not null,
            "The purpose fence refused a setup code presented under its own purpose.");

        // ── Cross-store separation ───────────────────────────────────────────────────────────────
        // Recovery codes live only in the recovery authority, so the assertions below hold today by
        // TABLE separation rather than by any purpose predicate. They are kept for what they can
        // still catch — a future refactor that merges the two token tables — and are deliberately
        // NOT the fence proof; that is the block above.
        var setupConsumed = await setupStore
            .ConsumeAsync(
                invitation!.RawCode,
                invitation.TenantId,
                WebSetupInvitationPurpose.AccountSetup,
                Now)
            .ConfigureAwait(false);
        Require(!setupConsumed, "A recovery code cross-consumed through the AccountSetup authority.");

        // The inverse fence is equally important: an account-challenge handle is not a recovery code.
        const string challengeHandle = "login-select-challenge-handle";
        await using (var sessions = proof.SessionFactory.CreateDbContext())
        {
            sessions.AccountAccessChallenges.Add(Challenge(
                proof.AccountId,
                "purpose-binding-challenge",
                Digest(challengeHandle)));
            await sessions.SaveChangesAsync().ConfigureAwait(false);
        }

        // Queried AFTER the seeding block above. Ordered the other way round — as it originally was —
        // this ran against an empty challenge table and could never fail. The positive half proves the
        // probe genuinely finds what is present, so the negative half reports a real absence rather
        // than a dead query.
        await using (var sessions = proof.SessionFactory.CreateDbContext())
        {
            var seededDigest = Digest(challengeHandle);
            var seededChallengeVisible = await sessions.AccountAccessChallenges
                .AsNoTracking()
                .AnyAsync(row => row.HandleDigest == seededDigest)
                .ConfigureAwait(false);
            Require(
                seededChallengeVisible,
                "The challenge-audience probe could not see its own seeded challenge.");

            var recoveryDigest = Digest(invitation.RawCode);
            var challengeCollision = await sessions.AccountAccessChallenges
                .AsNoTracking()
                .AnyAsync(row => row.HandleDigest == recoveryDigest)
                .ConfigureAwait(false);
            Require(
                !challengeCollision,
                "A recovery code appeared in the login/select challenge audience.");
        }

        var challengeAsRecovery = await proof.RecoveryStore
            .BeginOrResumeAsync(challengeHandle, Digest("challenge-commitment"), Now)
            .ConfigureAwait(false);
        Require(
            challengeAsRecovery.Status == RecoveryConsumeStatus.Refused,
            "A login/select challenge cross-consumed through the recovery authority.");

        var recovery = await proof.RecoveryStore
            .BeginOrResumeAsync(invitation.RawCode, Digest("recovery-commitment"), Now)
            .ConfigureAwait(false);
        Require(
            recovery.Status == RecoveryConsumeStatus.Consumed,
            "The purpose-bound recovery authority refused its own fresh code.");
    }

    private static async Task ProveOneTimeConsumeAcrossRestartAsync()
    {
        await using var proof = await RecoveryProofStore.CreateAsync().ConfigureAwait(false);
        var invitation = await proof.RecoveryStore
            .IssueAsync(proof.RecoverySeed("restart-consume"))
            .ConfigureAwait(false);
        Require(invitation is not null, "The recovery authority did not issue its one-time code.");

        var commitment = Digest(RecoveredCredentialHash);
        var first = await proof.RecoveryStore
            .BeginOrResumeAsync(invitation!.RawCode, commitment, Now)
            .ConfigureAwait(false);
        Require(
            first.Status == RecoveryConsumeStatus.Consumed,
            "The recovery code was not consumed on its first redemption.");
        await proof.RecoveryStore
            .MarkCompletedAsync(first.RecoveryInvitationId!, Now.AddSeconds(1))
            .ConfigureAwait(false);

        // A fresh factory + store over the same file is the process-restart boundary.
        var restartedFactory = new IdentityContextFactory(proof.IdentityDatabasePath);
        var restartedStore = new RecoveryInvitationStore(restartedFactory);
        var replay = await restartedStore
            .BeginOrResumeAsync(invitation.RawCode, commitment, Now.AddSeconds(2))
            .ConfigureAwait(false);
        Require(
            replay.Status == RecoveryConsumeStatus.Refused,
            "A completed recovery code replayed after the authority store restarted.");

        await using var identity = restartedFactory.CreateDbContext();
        var durable = await identity.RecoveryInvitations
            .AsNoTracking()
            .SingleAsync(row => row.RecoveryInvitationId == invitation.RecoveryInvitationId)
            .ConfigureAwait(false);
        Require(
            durable.ConsumedAtUtc is not null && durable.CompletedAtUtc is not null,
            "The recovery consume/completion markers did not survive restart.");
    }

    private static async Task ProveAllAudiencesRevokedBeforeSuccessAsync()
    {
        await ProveEveryAudienceIsRevokedAsync().ConfigureAwait(false);
        await ProveRevocationStagingPrecedesCompletionAsync().ConfigureAwait(false);
    }

    private static async Task ProveEveryAudienceIsRevokedAsync()
    {
        await using var proof = await RecoveryProofStore.CreateAsync().ConfigureAwait(false);
        var tenantA = Guid.NewGuid().ToString("D");
        var tenantB = Guid.NewGuid().ToString("D");

        await using (var sessions = proof.SessionFactory.CreateDbContext())
        {
            sessions.UserSessions.Add(SelectedSession(
                proof.AccountId,
                tenantA,
                "recovery-selected-a",
                Digest("recovery-selected-handle-a")));
            sessions.UserSessions.Add(SelectedSession(
                proof.AccountId,
                tenantB,
                "recovery-selected-b",
                Digest("recovery-selected-handle-b")));
            sessions.InstallationSessions.Add(InstallationSession(
                proof.AccountId,
                "recovery-installation",
                Digest("recovery-installation-handle")));
            sessions.AccountAccessChallenges.Add(Challenge(
                proof.AccountId,
                "recovery-challenge",
                Digest("recovery-challenge-handle")));
            await sessions.SaveChangesAsync().ConfigureAwait(false);
        }

        var invitation = await proof.RecoveryStore
            .IssueAsync(proof.RecoverySeed("revoke-all", tenantA))
            .ConfigureAwait(false);
        Require(invitation is not null, "The recovery authority did not issue its revoke-all code.");

        var authority = new AccountCredentialRecoveryService(
            proof.RecoveryStore,
            new RecoverySessionRevoker(proof.SessionFactory),
            proof.IdentityFactory,
            new FixedTimeProvider(Now));
        var result = await authority
            .RecoverAsync(new AccountRecoveryCommand(
                invitation!.RawCode,
                RecoveredCredentialHash,
                Guid.NewGuid().ToString("N")))
            .ConfigureAwait(false);
        Require(
            result.Status == AccountRecoveryStatus.Recovered && result.RevokedSessionCount == 4,
            "Recovery reported success without revoking every seeded audience.");

        await using (var sessions = proof.SessionFactory.CreateDbContext())
        {
            var revoked = await sessions.Revocations
                .AsNoTracking()
                .Where(row => row.AccountId == proof.AccountId)
                .ToArrayAsync()
                .ConfigureAwait(false);
            Require(revoked.Length == 4, "Recovery did not persist all four session revocations.");
            Require(
                revoked.Count(row => row.Audience == WebCookieAudience.SelectedSession) == 2,
                "Recovery omitted a selected-session tenant.");
            Require(
                revoked.Count(row => row.Audience == WebCookieAudience.InstallationSession) == 1,
                "Recovery omitted the installation-session audience.");
            Require(
                revoked.Count(row => row.Audience == WebCookieAudience.AccountChallenge) == 1,
                "Recovery omitted the account-challenge audience.");
        }

        await using (var identity = proof.IdentityFactory.CreateDbContext())
        {
            var completed = await identity.RecoveryInvitations
                .AsNoTracking()
                .SingleAsync(row => row.RecoveryInvitationId == invitation.RecoveryInvitationId)
                .ConfigureAwait(false);
            var account = await identity.Accounts
                .AsNoTracking()
                .SingleAsync(row => row.AccountId == proof.AccountId)
                .ConfigureAwait(false);
            Require(
                completed.CompletedAtUtc is not null,
                "Recovery success became visible without a durable completion marker.");
            Require(
                account.SecurityVersion == 2,
                "Recovery did not advance the account security version before success.");
        }
    }

    /// <summary>
    /// Pins the ORDERING the authority name promises — revocation evidence is staged BEFORE the
    /// recovery is marked complete — rather than leaving "before success" as an unenforced adjective.
    /// The end state of a successful recovery is identical either way, so ordering can only be
    /// observed by making the earlier step fail: dropping the session store's revocation table breaks
    /// STEP 3 (stage revocations) deterministically while every read the revoker performs still
    /// succeeds. Swapping STEP 3 and STEP 4 in
    /// <c>AccountCredentialRecoveryService.RecoverAsync</c> marks the recovery complete first, which
    /// this refuses. The consume/rotation assertions are the anti-vacuity control: they prove the
    /// saga genuinely reached revocation staging instead of refusing earlier for an unrelated reason.
    /// </summary>
    private static async Task ProveRevocationStagingPrecedesCompletionAsync()
    {
        await using var proof = await RecoveryProofStore.CreateAsync().ConfigureAwait(false);
        var tenantId = Guid.NewGuid().ToString("D");

        await using (var sessions = proof.SessionFactory.CreateDbContext())
        {
            sessions.UserSessions.Add(SelectedSession(
                proof.AccountId,
                tenantId,
                "ordering-selected",
                Digest("ordering-selected-handle")));
            await sessions.SaveChangesAsync().ConfigureAwait(false);
        }

        var invitation = await proof.RecoveryStore
            .IssueAsync(proof.RecoverySeed("revoke-before-complete", tenantId))
            .ConfigureAwait(false);
        Require(invitation is not null, "The recovery authority did not issue its ordering-proof code.");

        // Break revocation staging, and only revocation staging: the session reads the revoker
        // performs still resolve; the table it must write into is gone.
        await using (var sessions = proof.SessionFactory.CreateDbContext())
        {
            await sessions.Database
                .ExecuteSqlRawAsync("DROP TABLE web_session_revocations;")
                .ConfigureAwait(false);
        }

        var authority = new AccountCredentialRecoveryService(
            proof.RecoveryStore,
            new RecoverySessionRevoker(proof.SessionFactory),
            proof.IdentityFactory,
            new FixedTimeProvider(Now));

        AccountRecoveryResult? outcome = null;
        try
        {
            outcome = await authority
                .RecoverAsync(new AccountRecoveryCommand(
                    invitation!.RawCode,
                    RecoveredCredentialHash,
                    Guid.NewGuid().ToString("N")))
                .ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // Expected: the revocation evidence could not be staged.
        }
        catch (DbUpdateException)
        {
            // Expected: the revocation evidence could not be staged.
        }

        Require(
            outcome is null || outcome.Status != AccountRecoveryStatus.Recovered,
            "Recovery reported success although its revocation staging failed.");

        await using (var identity = proof.IdentityFactory.CreateDbContext())
        {
            var recovery = await identity.RecoveryInvitations
                .AsNoTracking()
                .SingleAsync(row => row.RecoveryInvitationId == invitation!.RecoveryInvitationId)
                .ConfigureAwait(false);
            var account = await identity.Accounts
                .AsNoTracking()
                .SingleAsync(row => row.AccountId == proof.AccountId)
                .ConfigureAwait(false);

            Require(
                recovery.ConsumedAtUtc is not null && account.SecurityVersion == 2,
                "The ordering proof never reached revocation staging, so it proves nothing.");
            Require(
                recovery.CompletedAtUtc is null,
                "Recovery was marked complete before its revocation evidence was staged.");
        }
    }

    private static AccountSetupInvitationSeed SetupSeed(string tenantId, string fingerprint) =>
        new(
            TenantId: tenantId,
            InviterAccountId: "setup-inviter-account",
            InviterPrincipalId: "setup-inviter-principal",
            InviterPartyId: "setup-inviter-party",
            InviterSessionCorrelationId: "setup-inviter-session",
            InviterMembershipId: "setup-inviter-membership",
            InviterMembershipOwnerVersion: 1,
            InviterGrantId: "setup-inviter-grant",
            InviterGrantOwnerVersion: 1,
            InviterAuthorizationEpoch: 1,
            RequestedPermissionsJson: "[]",
            CommandFingerprint: Digest(fingerprint),
            IssuedAtUtc: Now.AddMinutes(-1),
            AbsoluteExpiresAtUtc: Now.AddHours(1));

    private static WebUserSessionRecord SelectedSession(
        string accountId,
        string tenantId,
        string correlationId,
        string handleDigest) =>
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
        string accountId,
        string correlationId,
        string handleDigest) =>
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
        string accountId,
        string challengeId,
        string handleDigest) =>
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

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class RecoveryProofStore : IAsyncDisposable
    {
        private readonly string _directory;

        private RecoveryProofStore(
            string directory,
            string identityDatabasePath,
            IdentityContextFactory identityFactory,
            SessionContextFactory sessionFactory,
            string accountId)
        {
            _directory = directory;
            IdentityDatabasePath = identityDatabasePath;
            IdentityFactory = identityFactory;
            SessionFactory = sessionFactory;
            AccountId = accountId;
            RecoveryStore = new RecoveryInvitationStore(identityFactory);
        }

        internal string IdentityDatabasePath { get; }

        internal IdentityContextFactory IdentityFactory { get; }

        internal SessionContextFactory SessionFactory { get; }

        internal string AccountId { get; }

        internal RecoveryInvitationStore RecoveryStore { get; }

        internal RecoveryInvitationSeed RecoverySeed(string fingerprint, string? tenantId = null) =>
            new(
                TenantId: tenantId ?? Guid.NewGuid().ToString("D"),
                IssuerAccountId: "recovery-issuer",
                IssuerPrincipalId: "recovery-principal",
                TargetAccountId: AccountId,
                TargetNormalizedUsername: "FOUNDER",
                CommandFingerprint: Digest(fingerprint),
                IssuedAtUtc: Now.AddMinutes(-1),
                AbsoluteExpiresAtUtc: Now.AddHours(1));

        internal static async Task<RecoveryProofStore> CreateAsync()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"mtw01g-recovery-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var identityPath = Path.Combine(directory, "identity.db");
                var identityFactory = new IdentityContextFactory(identityPath);
                var sessionFactory = new SessionContextFactory(Path.Combine(directory, "sessions.db"));

                await using (var identity = identityFactory.CreateDbContext())
                {
                    await identity.Database.MigrateAsync().ConfigureAwait(false);
                }
                await using (var sessions = sessionFactory.CreateDbContext())
                {
                    await sessions.Database.MigrateAsync().ConfigureAwait(false);
                }

                var bootstrap = new InstallationFounderBootstrapService(
                    identityFactory,
                    new FixedTimeProvider(Now));
                await bootstrap
                    .InitializeAsync(new InstallationFounderBootstrapCommand(
                        "founder",
                        InitialCredentialHash,
                        Guid.NewGuid().ToString("N"),
                        string.Join(":", Enumerable.Repeat("AB", 32)),
                        "mtw01g-recovery-proof"))
                    .ConfigureAwait(false);

                string accountId;
                await using (var identity = identityFactory.CreateDbContext())
                {
                    accountId = (await identity.Accounts
                            .AsNoTracking()
                            .SingleAsync(row => row.NormalizedUsername == "FOUNDER")
                            .ConfigureAwait(false))
                        .AccountId;
                }

                return new RecoveryProofStore(
                    directory,
                    identityPath,
                    identityFactory,
                    sessionFactory,
                    accountId);
            }
            catch
            {
                // No RecoveryProofStore was handed back, so nothing will ever dispose this directory.
                // Clean it up here and let the original failure propagate untouched.
                TryDeleteDirectory(directory);
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            // Best-effort: this runs on the way out of an `await using`, so a failed delete (a
            // lingering SQLite handle — likelier on Windows than on the CI runner) would REPLACE the
            // InvalidOperationException a failing proof is throwing through it and destroy the
            // diagnosis. A leftover temp directory is never worth that trade.
            TryDeleteDirectory(_directory);
            return ValueTask.CompletedTask;
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup; a leftover temp directory never affects determinism.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort temp cleanup; a leftover temp directory never affects determinism.
            }
        }
    }

    internal sealed class IdentityContextFactory(string databasePath)
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

    internal sealed class SessionContextFactory(string databasePath)
        : IDbContextFactory<NodeLocalWebSessionDbContext>
    {
        public NodeLocalWebSessionDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalWebSessionDbContext>()
                .UseSqlite($"Data Source={databasePath};Default Timeout=30;Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalWebSessionDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalWebSessionDbContext(options);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
