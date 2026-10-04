using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Foundation.PasswordHashing;
using Harborline.Api.Foundation.Session;
using Harborline.Api.Kernel.Lease;
using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// The tenant-switch REAL-SEAM gate (council-verdict-2026-07-28T1021Z, BLOCKER 2).
/// <para>
/// <see cref="WebTenantSwitchAuthorityTests"/> drives the switch through a hand-rolled
/// <c>RecordingMembershipStore</c> that fabricates its own receipts and therefore never crosses the
/// installation home-decision fence. That is precisely why BLOCKER 1 — <c>WebTenantSwitch</c> having
/// no arm in <see cref="InstallationIdentityHomeDecisionAuthority"/>'s <c>CommandType</c> dispatch —
/// shipped CI-green while every real switch threw <c>identity.coordinator_payload_invalid</c> at the
/// first tenant-head finalization.
/// </para>
/// <para>
/// <b>The seam under test.</b> Both tenant partitions here are the REAL
/// <see cref="EncryptedTenantMembershipAuthorityStore"/> over a REAL
/// <see cref="SqlCipherEncryptedStore"/>, constructed with the REAL
/// <see cref="InstallationIdentityHomeDecisionAuthority"/> over the REAL migrated installation
/// identity database — the exact composition <c>Program.cs</c> builds through
/// <see cref="TeamContextTenantIdentityAuthorityPartitionResolver"/>. The old selected session is
/// minted by the REAL <see cref="WebAccountAccessChallengeIssuer"/> +
/// <see cref="WebTenantSelectionAuthority"/> over a REAL Argon2id credential, and both memberships
/// are created by the REAL <see cref="InstallationIdentityCoordinatorService"/>. No session,
/// membership, or receipt row is inserted directly.
/// </para>
/// <para>
/// <b>Substituted (non-teeth, mirroring the Mtw2 recipe):</b> the partition RESOLVER (a fixture
/// resolver over the two real partitions — the production resolver would drag in the whole team
/// context factory; the membership STORE and the fence stay real), the party reader (seeded fixture,
/// recipe-permitted), an always-granting lease coordinator, and an accepting admission seam
/// (admission is <see cref="LiveTenantMembershipAuthorityAdmissionTests"/>' tooth, not this card's).
/// </para>
/// <para>
/// <b>Falsification.</b> Delete the <c>WebTenantSwitchAuthority.CommandType</c> arm from
/// <see cref="InstallationIdentityHomeDecisionAuthority"/> and both tests here go red with
/// <c>identity.coordinator_payload_invalid: command type is not admitted.</c> — the reviewer's probe,
/// made permanent. Nothing in this file can be satisfied by adding a string to a set.
/// </para>
/// </summary>
[Trait("PlanCard", "MTW-01C")]
public sealed class WebTenantSwitchRealSeamTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 10, 0, 0, TimeSpan.Zero);
    private const string OldTenantId = "3244aaaa-0000-0000-0000-0000000000a1";
    private const string TargetTenantId = "3244bbbb-0000-0000-0000-0000000000b2";
    private const string UnrelatedTenantId = "3244cccc-0000-0000-0000-0000000000c3";
    private const string OldPrincipal = "principal-old-3244";
    private const string TargetPrincipal = "principal-target-3244";
    private const string Username = "founder";
    private const string Password = "correct horse battery staple switch";

    [Fact]
    public async Task A_killed_logout_process_is_recovered_by_a_fresh_process_without_the_caller_handle()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"logout-process-loss-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        Process? committed = null;
        Process? recovered = null;
        try
        {
            committed = StartLogoutWorker(directory, "commit");
            var stdout = committed.StandardOutput.ReadToEndAsync();
            var stderr = committed.StandardError.ReadToEndAsync();
            var marker = Path.Combine(directory, "committed.marker");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(marker) && !committed.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(50);
            Assert.True(File.Exists(marker), committed.HasExited ? await stdout + await stderr : "Worker did not reach the committed effect boundary.");
            // Observe the committed session effect while the child is stopped before the next home save.
            var identityFactory = new InstallationFounderBootstrapServiceTests.IdentityContextFactory(Path.Combine(directory, "identity.db"));
            var sessionFactory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(Path.Combine(directory, "sessions.db"));
            await using (var identity = identityFactory.CreateDbContext())
            {
                var home = await identity.Coordinators.SingleAsync(row => row.CommandType == "WebSelectedSessionLogout");
                Assert.Equal(InstallationIdentityCoordinatorState.Preparing, home.State);
                Assert.DoesNotContain(await identity.AuditEnvelopes.ToListAsync(), row => row.EventType == "WebUserSessionLogoutCompleted");
            }
            await using (var sessions = sessionFactory.CreateDbContext())
                Assert.Single(await sessions.Revocations.ToListAsync(), row => row.ReasonCode == "user-logout");
            committed.Kill(entireProcessTree: true);
            await committed.WaitForExitAsync();
            await stdout;
            await stderr;

            recovered = StartLogoutWorker(directory, "recover");
            var recoveredOutput = recovered.StandardOutput.ReadToEndAsync();
            var recoveredError = recovered.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await recovered.WaitForExitAsync(timeout.Token);
            Assert.True(recovered.ExitCode == 0, await recoveredOutput + await recoveredError);
            Assert.True(File.Exists(Path.Combine(directory, "recovered.marker")));
            await using var proof = identityFactory.CreateDbContext();
            Assert.Equal(InstallationIdentityCoordinatorState.Completed,
                (await proof.Coordinators.SingleAsync(row => row.CommandType == "WebSelectedSessionLogout")).State);
            Assert.Single(await proof.AuditEnvelopes.ToListAsync(), row => row.EventType == "WebUserSessionLogoutCompleted");
        }
        finally
        {
            foreach (var child in new[] { committed, recovered })
            {
                if (child is null) continue;
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync();
                }
                child.Dispose();
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Process StartLogoutWorker(string directory, string phase)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(WebTenantSwitchRealSeamTests).Assembly.Location);
        start.ArgumentList.Add("/TestCaseFilter:FullyQualifiedName~Logout_process_loss_worker");
        start.Environment["HARBORLINE_LOGOUT_PROCESS_TEST_DIRECTORY"] = directory;
        start.Environment["HARBORLINE_LOGOUT_PROCESS_TEST_PHASE"] = phase;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated logout worker.");
    }

    [Fact]
    public async Task Logout_process_loss_worker()
    {
        var directory = Environment.GetEnvironmentVariable("HARBORLINE_LOGOUT_PROCESS_TEST_DIRECTORY");
        if (directory is null) return;
        var phase = Environment.GetEnvironmentVariable("HARBORLINE_LOGOUT_PROCESS_TEST_PHASE");
        if (phase == "commit")
        {
            await using var h = await RealSeamHarness.CreateAsync(directory);
            var handle = await h.LoginAndSelectAsync(h.OldTenant);
            var stop = new PauseLogoutSave(Path.Combine(directory, "committed.marker"));
            var identity = new PausingIdentityFactory(Path.Combine(directory, "identity.db"), stop);
            var authority = new WebSelectedSessionLogoutAuthority(identity, new WebSelectedSessionStore(h.SessionFactory),
                new FixturePartitionResolver(h.OldPartition, h.TargetPartition), new FixedTimeProvider(Now));
            await authority.LogoutAsync(handle);
            throw new InvalidOperationException("The committed worker was expected to be killed at its boundary.");
        }
        Assert.Equal("recover", phase);
        var homeFactory = new InstallationFounderBootstrapServiceTests.IdentityContextFactory(Path.Combine(directory, "identity.db"));
        var sessions = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(Path.Combine(directory, "sessions.db"));
        var time = new FixedTimeProvider(Now.AddMinutes(1));
        var (oldStore, oldPartition) = await RealSeamHarness.OpenPartitionAsync(directory, "old", OldTenantId, homeFactory, time);
        var (targetStore, targetPartition) = await RealSeamHarness.OpenPartitionAsync(directory, "target", TargetTenantId, homeFactory, time);
        await using (oldStore)
        await using (targetStore)
        {
            var resolver = new FixturePartitionResolver(oldPartition, targetPartition);
            var logout = new WebSelectedSessionLogoutAuthority(homeFactory, new WebSelectedSessionStore(sessions), resolver, time);
            var recovery = new InstallationIdentityCoordinatorRecoveryService(homeFactory,
                new InstallationIdentityCoordinatorService(homeFactory, resolver, new AcceptingAdmission(), time, TestAuthorization.Gate(true)),
                homeRecoveries: [logout]);
            await recovery.RecoverPendingAsync();
            await recovery.RecoverPendingAsync();
            await using var db = homeFactory.CreateDbContext();
            Assert.Equal(InstallationIdentityCoordinatorState.Completed,
                (await db.Coordinators.SingleAsync(row => row.CommandType == "WebSelectedSessionLogout")).State);
            Assert.Single(await db.AuditEnvelopes.ToListAsync(), row => row.EventType == "WebUserSessionLogoutCompleted");
        }
        File.WriteAllText(Path.Combine(directory, "recovered.marker"), "completed once without a caller handle");
    }

    private sealed class PauseLogoutSave(string marker) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<InstallationIdentityCoordinatorRecord>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.CommandType == "WebSelectedSessionLogout"
                    && entry.Entity.State == InstallationIdentityCoordinatorState.Committing))
            {
                File.WriteAllText(marker, "session revocation committed; home save has not run");
                using var pause = new ManualResetEventSlim();
                pause.Wait(cancellationToken);
            }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class PausingIdentityFactory(string path, IInterceptor pause) : IDbContextFactory<NodeLocalInstallationIdentityDbContext>
    {
        public NodeLocalInstallationIdentityDbContext CreateDbContext() => new(
            new DbContextOptionsBuilder<NodeLocalInstallationIdentityDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False", sqlite => sqlite.MigrationsHistoryTable(NodeLocalInstallationIdentityDbContext.MigrationsHistoryTableName))
                .AddInterceptors(pause).Options);
    }

    [Fact]
    public async Task Switch_Finalizes_Both_Tenant_Heads_Through_The_Real_Home_Decision_Fence()
    {
        await using var h = await RealSeamHarness.CreateAsync();
        var oldHandle = await h.LoginAndSelectAsync(h.OldTenant);

        var switched = await h.Switch.SwitchAsync(oldHandle, h.TargetTenant);

        // Under an unregistered arm this line is never reached: SwitchAsync throws
        // identity.coordinator_payload_invalid out of FinalizeTenantHeadsAsync.
        Assert.NotNull(switched);
        Assert.Equal(h.TargetTenant, switched!.TenantId);

        // The old handle is revoked and the replacement is live — the rotation actually committed.
        var sessions = new WebSelectedSessionStore(h.SessionFactory);
        Assert.Null(await sessions.FindActiveAsync(Digest(oldHandle), h.AccountSecurityVersion, Now));
        Assert.NotNull(await sessions.FindActiveAsync(
            Digest(switched.Handle),
            h.AccountSecurityVersion,
            Now));

        var home = await h.SwitchCoordinatorAsync();
        Assert.Equal(WebTenantSwitchAuthority.CommandType, home.CommandType);
        Assert.Equal(InstallationIdentityCoordinatorState.Completed, home.State);
        Assert.Equal(
            new[] { h.OldTenant, h.TargetTenant }.Order(StringComparer.Ordinal).ToArray(),
            JsonSerializer.Deserialize<string[]>(home.TenantIdsJson, Web));

        // Both durable tenant receipts carry a home-decision digest minted by the REAL fence — the
        // constant 'B'/'D' strings the RecordingMembershipStore double fabricates cannot appear here.
        using var receipts = JsonDocument.Parse(home.FinalReceiptsJson);
        var revocationDigest = HomeDecisionDigest(receipts, "revocation");
        var selectionDigest = HomeDecisionDigest(receipts, "selection");
        foreach (var digest in new[] { revocationDigest, selectionDigest })
        {
            Assert.Equal(64, digest.Length);
            Assert.True(
                digest.All(Uri.IsHexDigit),
                "home-decision digests must be the fence's SHA-256 hex output.");
            Assert.NotEqual(new string('B', 64), digest);
            Assert.NotEqual(new string('D', 64), digest);
        }

        // The decision digest binds the coordinator row's state — not the tenant — so both tenant
        // heads, finalized at the same Committing state, carry the identical decision.
        Assert.Equal(revocationDigest, selectionDigest);

        // The target tenant's REAL authority document holds the finalized selection; replaying the
        // finalize re-enters the fence and must return the identical receipt.
        var replay = await h.TargetPartition.Memberships.FinalizeSessionSelectionAsync(
            home.CorrelationId,
            home.CommandFingerprint,
            Now,
            CancellationToken.None);
        Assert.Equal(h.TargetTenant, replay.TenantId);
        Assert.Equal(selectionDigest, replay.HomeDecisionDigest);
    }

    [Fact]
    public async Task Home_Decision_Admits_Exactly_The_Two_Tenants_Of_A_Durable_Switch()
    {
        await using var h = await RealSeamHarness.CreateAsync();
        var oldHandle = await h.LoginAndSelectAsync(h.OldTenant);
        Assert.NotNull(await h.Switch.SwitchAsync(oldHandle, h.TargetTenant));
        var home = await h.SwitchCoordinatorAsync();
        var fence = new InstallationIdentityHomeDecisionAuthority(h.IdentityFactory);

        // The exact call EncryptedTenantMembershipAuthorityStore.FinalizeSessionRevocationAsync /
        // FinalizeSessionSelectionAsync make. Both tenant ids are admitted by the switch arm.
        foreach (var tenantId in new[] { h.OldTenant, h.TargetTenant })
        {
            var receipt = await fence.RequireFinalizationAsync(
                home.CorrelationId,
                home.CommandFingerprint,
                tenantId,
                CancellationToken.None);
            Assert.Equal(tenantId, receipt.TenantId);
            Assert.Equal(InstallationIdentityCoordinatorState.Completed, receipt.State);
        }

        // …and the arm admits ONLY those two: a third tenant is refused as outside the decision,
        // not accepted by a blanket pass.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fence.RequireFinalizationAsync(
                home.CorrelationId,
                home.CommandFingerprint,
                Guid.Parse(UnrelatedTenantId).ToString("D"),
                CancellationToken.None));
        Assert.StartsWith("identity.home_decision_mismatch:", refused.Message, StringComparison.Ordinal);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string HomeDecisionDigest(JsonDocument receipts, string receiptName) =>
        receipts.RootElement.GetProperty(receiptName).GetProperty("homeDecisionDigest").GetString()
        ?? throw new InvalidOperationException($"{receiptName} receipt has no home-decision digest.");

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private sealed class RealSeamHarness : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly SqlCipherEncryptedStore _oldStore;
        private readonly SqlCipherEncryptedStore _targetStore;

        private RealSeamHarness(
            string directory,
            SqlCipherEncryptedStore oldStore,
            SqlCipherEncryptedStore targetStore,
            InstallationFounderBootstrapServiceTests.IdentityContextFactory identityFactory,
            WebAccountAccessChallengeIssuerTests.SessionContextFactory sessionFactory,
            TenantIdentityAuthorityPartition oldPartition,
            TenantIdentityAuthorityPartition targetPartition,
            IWebAccountAccessChallengeIssuer challengeIssuer,
            IWebTenantSelectionAuthority selection,
            IWebTenantSwitchAuthority tenantSwitch,
            string accountId,
            long accountSecurityVersion)
        {
            _directory = directory;
            _oldStore = oldStore;
            _targetStore = targetStore;
            IdentityFactory = identityFactory;
            SessionFactory = sessionFactory;
            OldPartition = oldPartition;
            TargetPartition = targetPartition;
            ChallengeIssuer = challengeIssuer;
            Selection = selection;
            Switch = tenantSwitch;
            AccountId = accountId;
            AccountSecurityVersion = accountSecurityVersion;
        }

        internal InstallationFounderBootstrapServiceTests.IdentityContextFactory IdentityFactory { get; }
        internal WebAccountAccessChallengeIssuerTests.SessionContextFactory SessionFactory { get; }
        internal TenantIdentityAuthorityPartition OldPartition { get; }
        internal TenantIdentityAuthorityPartition TargetPartition { get; }
        internal IWebAccountAccessChallengeIssuer ChallengeIssuer { get; }
        internal IWebTenantSelectionAuthority Selection { get; }
        internal IWebTenantSwitchAuthority Switch { get; }
        internal string AccountId { get; }
        internal long AccountSecurityVersion { get; }
        internal string OldTenant => OldPartition.TenantId;
        internal string TargetTenant => TargetPartition.TenantId;

        internal static async Task<RealSeamHarness> CreateAsync(string? testDirectory = null)
        {
            var directory = testDirectory ?? Path.Combine(Path.GetTempPath(), $"switch-real-seam-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var time = new FixedTimeProvider(Now);
            var identityFactory = new InstallationFounderBootstrapServiceTests.IdentityContextFactory(
                Path.Combine(directory, "identity.db"));
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

            // REAL founder ceremony over a REAL Argon2id credential — the challenge issuer verifies
            // this password below, so the session cannot be conjured.
            var hasher = new Argon2idPasswordHasher<InstallationAccountRecord>(
                Options.Create(new Argon2idHashOptions()));
            var bootstrap = new InstallationFounderBootstrapService(identityFactory, time);
            var founder = await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                Username,
                hasher.HashPassword(HashUser, Password),
                Guid.NewGuid().ToString("N"),
                string.Join(":", Enumerable.Repeat("AB", 32)),
                "founder-bootstrap-3244"));
            InstallationAccountRecord account;
            await using (var identity = identityFactory.CreateDbContext())
            {
                account = await identity.Accounts.AsNoTracking().SingleAsync();
            }
            Assert.Equal(founder.AccountId, account.AccountId);

            // Two REAL encrypted tenant-authority partitions, each fenced by the REAL installation
            // home-decision authority over the REAL identity database.
            var (oldStore, oldPartition) = await OpenPartitionAsync(
                directory, "old", Guid.Parse(OldTenantId).ToString("D"), identityFactory, time);
            var (targetStore, targetPartition) = await OpenPartitionAsync(
                directory, "target", Guid.Parse(TargetTenantId).ToString("D"), identityFactory, time);
            var resolver = new FixturePartitionResolver(oldPartition, targetPartition);

            var coordinator = new InstallationIdentityCoordinatorService(
                identityFactory, resolver, new AcceptingAdmission(), time, TestAuthorization.Gate(true));
            var candidateLocator = new InstallationTenantCandidateLocator(identityFactory, coordinator);
            var partyReader = new SeededPartyReader();
            var sessionOptions = Options.Create(new SessionOptions());

            // REAL memberships in BOTH tenants, created by the REAL R3-H coordinator (which itself
            // finalizes each tenant head through the same fence).
            var membership = await coordinator.ExecuteAsync(new InstallationIdentityCoordinationCommand(
                CorrelationId: "switch-real-seam-memberships-3244",
                AccountId: account.AccountId,
                ActorAccountId: account.AccountId,
                AuthorityEvidenceDigest: new string('A', 64),
                ExpectedAccountOwnerVersion: account.OwnerVersion,
                ExpectedAccountSecurityVersion: account.SecurityVersion,
                ExpectedActorOwnerVersion: account.OwnerVersion,
                ExpectedActorSecurityVersion: account.SecurityVersion,
                Mutations:
                [
                    Mutation(oldPartition.TenantId, OldPrincipal),
                    Mutation(targetPartition.TenantId, TargetPrincipal),
                ]));
            Assert.Equal(InstallationIdentityCoordinationStatus.Completed, membership.Status);

            var challengeIssuer = new WebAccountAccessChallengeIssuer(
                identityFactory, sessionFactory, hasher, FixtureV1AuthorityGate.Admitting, time);
            var selection = new WebTenantSelectionAuthority(
                identityFactory,
                sessionFactory,
                candidateLocator,
                coordinator,
                resolver,
                partyReader,
                FixtureV1AuthorityGate.Admitting,
                sessionOptions,
                time);
            var tenantSwitch = new WebTenantSwitchAuthority(
                identityFactory,
                sessionFactory,
                new WebSelectedSessionStore(sessionFactory),
                candidateLocator,
                coordinator,
                resolver,
                partyReader,
                sessionOptions,
                time);

            return new RealSeamHarness(
                directory,
                oldStore,
                targetStore,
                identityFactory,
                sessionFactory,
                oldPartition,
                targetPartition,
                challengeIssuer,
                selection,
                tenantSwitch,
                account.AccountId,
                account.SecurityVersion);
        }

        /// <summary>REAL challenge → REAL tenant selection; returns the selected-session handle.</summary>
        internal async Task<string> LoginAndSelectAsync(string tenantId)
        {
            var challenge = (await ChallengeIssuer.IssueAsync(Username, Password)).Challenge;
            Assert.NotNull(challenge);
            var selected = await Selection.SelectAsync(challenge!.Handle, tenantId);
            Assert.NotNull(selected);
            return selected!.Handle;
        }

        internal async Task<InstallationIdentityCoordinatorRecord> SwitchCoordinatorAsync()
        {
            await using var identity = IdentityFactory.CreateDbContext();
            return await identity.Coordinators.AsNoTracking().SingleAsync(
                row => row.CommandType == WebTenantSwitchAuthority.CommandType);
        }

        internal static async Task<(SqlCipherEncryptedStore Store, TenantIdentityAuthorityPartition Partition)>
            OpenPartitionAsync(
                string directory,
                string name,
                string tenantId,
                IDbContextFactory<NodeLocalInstallationIdentityDbContext> identityFactory,
                TimeProvider time)
        {
            var store = new SqlCipherEncryptedStore();
            var keyPath = Path.Combine(directory, $"{name}-test.key");
            if (!File.Exists(keyPath)) await File.WriteAllBytesAsync(keyPath, RandomNumberGenerator.GetBytes(32));
            await store.OpenAsync(
                Path.Combine(directory, $"{name}-tenant.db"),
                await File.ReadAllBytesAsync(keyPath),
                CancellationToken.None);
            var partition = new TenantIdentityAuthorityPartition(
                tenantId,
                new EncryptedTenantMembershipAuthorityStore(
                    store,
                    tenantId,
                    new InstallationIdentityHomeDecisionAuthority(identityFactory),
                    time),
                new AlwaysLeaseCoordinator());
            return (store, partition);
        }

        private static TenantMembershipMutation Mutation(string tenantId, string principalId) =>
            new(
                tenantId,
                principalId,
                $"grant-{principalId}",
                ExpectedGrantOwnerVersion: 1,
                AuthorizationEpoch: 1,
                ExpectedMembershipOwnerVersion: 0,
                TenantMembershipStatus.Active);

        public async ValueTask DisposeAsync()
        {
            await OldPartition.Leases.DisposeAsync();
            await TargetPartition.Leases.DisposeAsync();
            await _oldStore.DisposeAsync();
            await _targetStore.DisposeAsync();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    // A throwaway user for the Argon2id hasher (verification ignores the user object; the salt is
    // random and the artifact is user-type-agnostic).
    private static readonly InstallationAccountRecord HashUser = new()
    {
        AccountId = "hash-user",
        NormalizedUsername = "HASH-USER",
        CredentialHash = "n/a",
        CredentialAlgorithm = "n/a",
        CredentialCeremonyId = "n/a",
        CredentialVersion = 1,
        Status = InstallationAccountStatus.Active,
        SecurityVersion = 1,
        OwnerVersion = 1,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    private sealed class FixturePartitionResolver(params TenantIdentityAuthorityPartition[] partitions)
        : ITenantIdentityAuthorityPartitionResolver
    {
        private readonly IReadOnlyDictionary<string, TenantIdentityAuthorityPartition> _partitions =
            partitions.ToDictionary(item => item.TenantId, StringComparer.Ordinal);

        public Task<TenantIdentityAuthorityPartition> ResolveAsync(
            string tenantId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_partitions[tenantId]);
    }

    private sealed class SeededPartyReader : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant,
            PrincipalUserId user,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(new CanonicalPartyBinding(
                tenant,
                user,
                new CanonicalPartyReference($"party-{user.Value}")));
    }

    private sealed class AcceptingAdmission : ITenantMembershipAuthorityAdmission
    {
        public Task ValidateMutationAsync(
            string actorAccountId,
            string authorityEvidenceDigest,
            string accountId,
            TenantMembershipMutation mutation,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<long> ValidateExistingAsync(
            string accountId,
            TenantMembershipSnapshot membership,
            CancellationToken cancellationToken) => Task.FromResult(membership.AuthorizationEpoch);
    }

    private sealed class AlwaysLeaseCoordinator : ILeaseCoordinator
    {
        private readonly ConcurrentDictionary<string, Lease> _held = new(StringComparer.Ordinal);

        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct)
        {
            var lease = new Lease(
                Guid.NewGuid().ToString("N"), resourceId, "test-node", Now, Now + duration, []);
            _held[lease.LeaseId] = lease;
            return Task.FromResult<Lease?>(lease);
        }

        public Task ReleaseAsync(Lease lease, CancellationToken ct)
        {
            _held.TryRemove(lease.LeaseId, out _);
            return Task.CompletedTask;
        }

        public bool Holds(string resourceId) => _held.Values.Any(item => item.ResourceId == resourceId);

        public IReadOnlyCollection<Lease> HeldLeases => _held.Values.ToArray();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
