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

    [Fact]
    [Trait("PlanCard", "MTW-01C")]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task Switch_to_a_tenant_outside_the_accounts_candidates_is_refused_before_any_head_is_written()
    {
        // The target partition would still report a usable membership, so only the candidate
        // check stands between the account and a tenant it was never listed for.
        await using var fixture = await SwitchFixture.CreateAsync(listTarget: false);

        var switched = await fixture.Authority.SwitchAsync(
            SwitchFixture.OldHandle,
            TargetTenantId);

        Assert.Null(switched);
        Assert.Empty(fixture.TargetStore.VisibilityObservedDuringFinalize);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Empty(await identity.Coordinators.AsNoTracking().ToArrayAsync());
        }
        await using (var sessions = fixture.SessionFactory.CreateDbContext())
        {
            Assert.Equal(2, await sessions.UserSessions.CountAsync());
            Assert.Empty(await sessions.Revocations.AsNoTracking().ToArrayAsync());
        }
        Assert.NotNull(await new WebSelectedSessionStore(fixture.SessionFactory).FindActiveAsync(
            Digest(SwitchFixture.OldHandle),
            fixture.AccountSecurityVersion,
            Now));
    }

    /// <summary>
    /// ck-4 G group 1, mutants 16378, 16379 and 16380: a target Party binding verified under another
    /// tenant is refused when the switch resolves its target, before any head is written. The Party
    /// reader echoes the principal, so only the verified-tenant clause can refuse it here.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task Switch_refuses_a_target_party_binding_verified_under_another_tenant()
    {
        await using var fixture = await SwitchFixture.CreateAsync(partyVerifiedTenant: OldTenantId);

        var switched = await fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId);

        Assert.Null(switched);
        Assert.Empty(fixture.OldStore.VisibilityObservedDuringFinalize);
        Assert.Empty(fixture.TargetStore.VisibilityObservedDuringFinalize);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Empty(await identity.Coordinators.AsNoTracking().ToArrayAsync());
        }
        Assert.NotNull(await new WebSelectedSessionStore(fixture.SessionFactory).FindActiveAsync(
            Digest(SwitchFixture.OldHandle),
            fixture.AccountSecurityVersion,
            Now));
    }

    /// <summary>
    /// ck-4 G group 1, mutants 16548 (old-tenant revocation receipt) and 16569 (target-tenant selection
    /// receipt): a tenant receipt that names a third tenant is refused, the home stays Committing and
    /// the old session stays live. Every other receipt field matches, so the receipt tenant clause is
    /// the only check that sees it.
    /// </summary>
    [Theory]
    [Trait("Holds", "kernel-core-ck-4")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Switch_refuses_a_tenant_receipt_naming_another_tenant(bool oldTenantReceipt)
    {
        await using var fixture = await SwitchFixture.CreateAsync();
        (oldTenantReceipt ? fixture.OldStore : fixture.TargetStore).ReceiptTenantId =
            "33333333-3333-3333-3333-333333333333";

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Authority.SwitchAsync(SwitchFixture.OldHandle, TargetTenantId));

        Assert.StartsWith("identity.session_switch_receipt_invalid:", exception.Message);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            Assert.Equal(
                InstallationIdentityCoordinatorState.Committing,
                (await identity.Coordinators.AsNoTracking().SingleAsync()).State);
        }
        Assert.NotNull(await new WebSelectedSessionStore(fixture.SessionFactory).FindActiveAsync(
            Digest(SwitchFixture.OldHandle),
            fixture.AccountSecurityVersion,
            Now));
    }

    /// <summary>
    /// ck-4 G group 1, mutants 16352 and 16354: a stored switch row whose old and target tenant are the
    /// same tenant is refused, even when its digests, fingerprint, correlation id and tenant list are
    /// all recomputed to agree with it. The distinct-tenant row built the same way is admitted, so the
    /// same-tenant clause is what refuses.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public void A_stored_switch_whose_old_and_target_tenant_are_the_same_is_refused()
    {
        Assert.Equal(
            new[] { OldTenantId, TargetTenantId },
            WebTenantSwitchAuthority.ValidateStoredSwitchTenants(StoredSwitch(OldTenantId, TargetTenantId)));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            WebTenantSwitchAuthority.ValidateStoredSwitch(StoredSwitch(TargetTenantId, TargetTenantId)));
        Assert.StartsWith("identity.session_switch_payload_invalid:", exception.Message);
    }

    private static InstallationIdentityCoordinatorRecord StoredSwitch(string oldTenantId, string targetTenantId)
    {
        const string accountId = "account-1";
        var target = new WebTenantSwitchAuthority.SwitchTargetAuthority(
            Membership("target-membership", accountId, targetTenantId, "target-principal", "target-grant"),
            "target-party",
            "target-tenant");
        var payload = new WebTenantSwitchAuthority.SwitchPayload(
            oldTenantId, "old-membership", "old-session", Digest(SwitchFixture.OldHandle), 1, target);
        var digest = InstallationAuditIntegrity.Hash(
            accountId,
            payload.OldTenantId,
            payload.OldMembershipId,
            payload.OldSessionCorrelationId,
            payload.OldHandleDigest,
            "1",
            targetTenantId,
            target.Membership.MembershipId,
            target.Membership.OwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            target.Membership.CanonicalPrincipalId,
            target.CanonicalPartyReference,
            target.DisplayName,
            target.Membership.GrantId,
            target.Membership.GrantOwnerVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            target.Membership.AuthorizationEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var correlationId = InstallationAuditIntegrity.Hash(
            WebTenantSwitchAuthority.CommandType, payload.OldSessionCorrelationId, targetTenantId);
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        return new InstallationIdentityCoordinatorRecord
        {
            CorrelationId = correlationId,
            CommandType = WebTenantSwitchAuthority.CommandType,
            CommandFingerprint = InstallationAuditIntegrity.Hash(
                WebTenantSwitchAuthority.CommandType, correlationId, digest),
            PayloadSchemaVersion = WebTenantSwitchAuthority.PayloadSchemaVersion,
            AccountId = accountId,
            ActorAccountId = accountId,
            AuthorityEvidenceDigest = digest,
            ExpectedAccountSecurityVersion = 1,
            TenantIdsJson = System.Text.Json.JsonSerializer.Serialize(
                new[] { oldTenantId, targetTenantId }.Order(StringComparer.Ordinal).ToArray(), json),
            IntentPayloadJson = System.Text.Json.JsonSerializer.Serialize(payload, json),
            FinalReceiptsJson = "{}",
        };
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

        internal static async Task<SwitchFixture> CreateAsync(
            bool listTarget = true,
            string? partyVerifiedTenant = null)
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
                new FixedCandidateLocator(listTarget),
                coordinator,
                resolver,
                new FixedPartyReader(partyVerifiedTenant),
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
        public string? ReceiptTenantId { get; set; }
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
                ReceiptTenantId ?? TenantId,
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
                ReceiptTenantId ?? TenantId,
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

    private sealed class FixedCandidateLocator(bool listTarget) : IInstallationTenantCandidateLocator
    {
        private readonly IReadOnlyList<InstallationTenantCandidate> Candidates =
        [
            new(
                new TenantId(OldTenantId),
                "old-tenant",
                TenantMembershipStatus.Active),
            .. listTarget
                ? [new InstallationTenantCandidate(
                    new TenantId(TargetTenantId),
                    "target-tenant",
                    TenantMembershipStatus.Active)]
                : Array.Empty<InstallationTenantCandidate>(),
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

    private sealed class FixedPartyReader(string? verifiedTenant) : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant,
            PrincipalUserId user,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(new CanonicalPartyBinding(
                verifiedTenant is null ? tenant : new TenantId(verifiedTenant),
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

        public Task<long> ValidateExistingAsync(
            string accountId,
            TenantMembershipSnapshot membership,
            CancellationToken cancellationToken) =>
            Task.FromResult(membership.AuthorizationEpoch);
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
