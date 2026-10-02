using System.Collections.Concurrent;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NSubstitute;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.People.Foundation.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.LocalFirst.Encryption;
using Harborline.Api.Kernel.Lease;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Behavioural cover for the founder tenant-membership attach (earlier repository ticket #3448).
/// </summary>
public sealed class FounderTenantMembershipAttachTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 31, 15, 8, 0, TimeSpan.Zero);

    /// <summary>
    /// The canonical principal must be a pure function of (tenant, ceremony correlation) — so a
    /// restart derives the SAME principal and the attach replays rather than minting a second
    /// identity — and must NOT vary with the account id, which a governed recovery may change.
    /// </summary>
    [Fact]
    public void Principal_Is_Deterministic_In_Tenant_And_Ceremony_Only()
    {
        var tenantA = new TenantId("11111111-1111-1111-1111-111111111111");
        var tenantB = new TenantId("22222222-2222-2222-2222-222222222222");

        // The single source, not a hand-copied literal: a parallel copy of a value that has one source
        // is the drift canary the review policy names.
        var correlation = InstallationFounderBootstrapCeremony.CorrelationId;

        var first = FounderTenantMembershipAttachService.DerivePrincipal(tenantA, correlation);
        var second = FounderTenantMembershipAttachService.DerivePrincipal(tenantA, correlation);
        var otherTenant = FounderTenantMembershipAttachService.DerivePrincipal(tenantB, correlation);

        Assert.Equal(first.Value, second.Value);          // restart-stable
        Assert.NotEqual(first.Value, otherTenant.Value);  // tenant-scoped, per ADR 0160 R3-A
        Assert.False(string.IsNullOrWhiteSpace(first.Value));

        // Never the v1 constant. ADR 0160 R3-D forbids the web plane falling back to it, and every v1
        // session on the dogfood host carried exactly this value — the defect behind #3383 / #3384.
        Assert.NotEqual("local", first.Value);
    }

    [Fact]
    public async Task FounderAttach_IssuesAdministratorNotOwnerComposition()
    {
        await using var fixture = await AttachFixture.CreateAsync();

        Assert.Equal(
            FounderTenantMembershipAttachStatus.Attached,
            await fixture.Service.RunAsync(CancellationToken.None));

        var grant = Assert.Single(
            await fixture.Grants.SnapshotAsync(fixture.Tenant),
            grant => grant.Subject.Value == fixture.FounderPrincipal.Value);
        Assert.Equal(RoleReference.Administrator, grant.Role);
        Assert.Equal(GrantReasonCodes.Bootstrap, grant.Grant.Reason.Code);
    }

    [Fact]
    public async Task First_Tenant_Grant_Imports_Installation_Claim_Issuer_Authority()
    {
        await using var fixture = await AttachFixture.CreateAsync();

        Assert.Equal(
            FounderTenantMembershipAttachStatus.Attached,
            await fixture.Service.RunAsync(CancellationToken.None));

        var grant = Assert.Single(
            await fixture.Grants.SnapshotAsync(fixture.Tenant),
            grant => grant.Subject.Value == fixture.FounderPrincipal.Value);
        Assert.Equal("desktop-os-session:founder", grant.GrantedBy.Value);
        Assert.Equal("desktop-os-session:founder", grant.Grant.Approver.Value);
        Assert.Equal(GranterKind.Installer, grant.GranterKind);
    }

    [Fact]
    public async Task Restart_Replays_Source_Reference_Without_Duplicating_Grant()
    {
        await using var fixture = await AttachFixture.CreateAsync();

        var first = await fixture.Service.RunAsync(CancellationToken.None);
        var firstGrant = Assert.Single(
            await fixture.Grants.SnapshotAsync(fixture.Tenant),
            grant => grant.Subject.Value == fixture.FounderPrincipal.Value);
        var replay = await fixture.Service.RunAsync(CancellationToken.None);
        var replayedGrant = Assert.Single(
            await fixture.Grants.SnapshotAsync(fixture.Tenant),
            grant => grant.Subject.Value == fixture.FounderPrincipal.Value);

        Assert.Equal(FounderTenantMembershipAttachStatus.Attached, first);
        Assert.Equal(FounderTenantMembershipAttachStatus.AlreadyAttached, replay);
        Assert.Equal(firstGrant, replayedGrant);
    }

    /// <summary>
    /// ck-4 tenant-slice triage (2026-09-30), mutant at FounderTenantMembershipAttachService line 158:
    /// on a host whose active team is not the genesis team, the attach must refuse before it writes a
    /// Party binding, tenant grant, or membership into the genesis tenant. Deleting the guard returned
    /// Attached and left the founder with Administrator authority in a tenant the host is not serving.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task Founder_attach_on_a_host_serving_another_team_refuses_and_writes_no_tenant_authority()
    {
        await using var fixture = await AttachFixture.CreateAsync(activeTeamDiverged: true);

        Assert.Equal(
            FounderTenantMembershipAttachStatus.TenantDiverged,
            await fixture.Service.RunAsync(CancellationToken.None));
        Assert.DoesNotContain(
            await fixture.Grants.SnapshotAsync(fixture.Tenant),
            grant => grant.Subject.Value == fixture.FounderPrincipal.Value);
    }

    /// <summary>
    /// T-1010 (ck-4 tenant-slice triage, G group 7): the attach stops at the first missing input and reports which
    /// kind of stop it was, writing no tenant authority. Without the early returns each path ran on into a later
    /// exception instead of the status the hosted runner keys its retry on.
    /// </summary>
    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task Founder_attach_without_the_founder_account_is_skipped_and_writes_no_tenant_authority()
    {
        await using var fixture = await AttachFixture.CreateAsync();
        await using (var context = fixture.IdentityFactory.CreateDbContext())
        {
            // The rows that reference the account would refuse the delete; the attach must cope with the damage.
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            context.Accounts.RemoveRange(context.Accounts.Where(a => a.AccountId == fixture.RootGrant.AccountId));
            await context.SaveChangesAsync();
        }

        Assert.Equal(
            FounderTenantMembershipAttachStatus.SkippedNoFounder,
            await fixture.Service.RunAsync(CancellationToken.None));
        Assert.DoesNotContain(
            await fixture.Grants.SnapshotAsync(fixture.Tenant),
            grant => grant.Subject.Value == fixture.FounderPrincipal.Value);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task Founder_attach_without_the_installation_identity_is_skipped_and_writes_no_tenant_authority()
    {
        await using var fixture = await AttachFixture.CreateAsync();
        await using (var context = fixture.IdentityFactory.CreateDbContext())
        {
            await context.Database.OpenConnectionAsync();
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            context.InstallationIdentities.RemoveRange(context.InstallationIdentities);
            await context.SaveChangesAsync();
        }

        Assert.Equal(
            FounderTenantMembershipAttachStatus.SkippedNoFounder,
            await fixture.Service.RunAsync(CancellationToken.None));
        Assert.DoesNotContain(
            await fixture.Grants.SnapshotAsync(fixture.Tenant),
            grant => grant.Subject.Value == fixture.FounderPrincipal.Value);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task Founder_attach_whose_party_binding_never_resolves_is_unavailable_and_writes_no_tenant_authority()
    {
        await using var fixture = await AttachFixture.CreateAsync(partyNeverResolves: true);

        Assert.Equal(
            FounderTenantMembershipAttachStatus.Unavailable,
            await fixture.Service.RunAsync(CancellationToken.None));
        Assert.DoesNotContain(
            await fixture.Grants.SnapshotAsync(fixture.Tenant),
            grant => grant.Subject.Value == fixture.FounderPrincipal.Value);
    }

    [Fact]
    [Trait("Holds", "kernel-core-ck-4")]
    public async Task Founder_attach_with_no_issuable_claim_and_nothing_redeemed_is_unavailable_and_writes_no_tenant_authority()
    {
        await using var fixture = await AttachFixture.CreateAsync(founderUsernameClearedAfterCeremony: true);

        Assert.Equal(
            FounderTenantMembershipAttachStatus.Unavailable,
            await fixture.Service.RunAsync(CancellationToken.None));
        Assert.DoesNotContain(
            await fixture.Grants.SnapshotAsync(fixture.Tenant),
            grant => grant.Subject.Value == fixture.FounderPrincipal.Value);
    }

    private sealed class AttachFixture : IAsyncDisposable
    {
        private readonly string _homePath;
        private readonly string _tenantPath;
        private readonly SqlCipherEncryptedStore _tenantStore;
        private readonly AlwaysLeaseCoordinator _leases;

        private AttachFixture(
            InstallationFounderBootstrapServiceTests.IdentityContextFactory identityFactory,
            string homePath,
            string tenantPath,
            SqlCipherEncryptedStore tenantStore,
            AlwaysLeaseCoordinator leases,
            TenantId tenant,
            PrincipalUserId founderPrincipal,
            InstallationAccessGrantRecord rootGrant,
            IGrantStore grants,
            FounderTenantMembershipAttachService service)
        {
            IdentityFactory = identityFactory;
            _homePath = homePath;
            _tenantPath = tenantPath;
            _tenantStore = tenantStore;
            _leases = leases;
            Tenant = tenant;
            FounderPrincipal = founderPrincipal;
            RootGrant = rootGrant;
            Grants = grants;
            Service = service;
        }

        public InstallationFounderBootstrapServiceTests.IdentityContextFactory IdentityFactory { get; }
        public TenantId Tenant { get; }
        public PrincipalUserId FounderPrincipal { get; }
        public InstallationAccessGrantRecord RootGrant { get; }
        public IGrantStore Grants { get; }
        public FounderTenantMembershipAttachService Service { get; }

        public static async Task<AttachFixture> CreateAsync(
            bool activeTeamDiverged = false, bool partyNeverResolves = false, bool founderUsernameClearedAfterCeremony = false)
        {
            var team = new TeamId(Guid.NewGuid());
            var tenant = ActiveTeamTenantContext.ProjectTenantId(team);
            var homePath = Path.Combine(
                Path.GetTempPath(), $"ticket-158-founder-home-{Guid.NewGuid():N}.db");
            var tenantPath = Path.Combine(
                Path.GetTempPath(), $"ticket-158-founder-tenant-{Guid.NewGuid():N}.db");
            var identityFactory =
                new InstallationFounderBootstrapServiceTests.IdentityContextFactory(homePath);
            await using (var context = identityFactory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
            }

            var time = new FixedTimeProvider(Now);
            var bootstrap = new InstallationFounderBootstrapService(identityFactory, time);
            var credential = "$argon2id$v=19$m=19456,t=2,p=1$" +
                 Convert.ToBase64String(new byte[16]) + "$" +
                 Convert.ToBase64String(new byte[32]);
            var rootFingerprint = string.Join(":", Enumerable.Repeat("AB", 32));
            var webClientOptions = new NodeWebClientOptions
            {
                Enabled = true,
                FounderUsername = "founder",
                FounderPasswordHash = credential,
            };
            var founderCeremony = new InstallationFounderBootstrapCeremony(
                bootstrap,
                Options.Create(webClientOptions),
                rootFingerprint,
                identityFactory,
                time,
                new BootstrapClaimRedemptionTests.FixedDesktopEvidence("founder", true),
                homePath);
            Assert.Equal(
                InstallationFounderBootstrapCeremonyStatus.Established,
                (await founderCeremony.RunAsync()).Status);
            await BootstrapClaimRedemptionTests.CreateGrantTablesAsync(homePath);
            if (founderUsernameClearedAfterCeremony)
            {
                // The ceremony established the founder, but no claim can be issued for the tenant any more.
                webClientOptions.FounderUsername = "";
            }

            InstallationAccessGrantRecord rootGrant;
            await using (var context = identityFactory.CreateDbContext())
            {
                rootGrant = await context.InstallationAccessGrants.AsNoTracking().SingleAsync();
            }

            var tenantStore = new SqlCipherEncryptedStore();
            await tenantStore.OpenAsync(
                tenantPath,
                RandomNumberGenerator.GetBytes(32),
                CancellationToken.None);
            var memberships = new EncryptedTenantMembershipAuthorityStore(
                tenantStore,
                tenant.Value,
                new InstallationIdentityHomeDecisionAuthority(identityFactory),
                new FixedTimeProvider(Now));
            var leases = new AlwaysLeaseCoordinator();
            var grants = new Harborline.Api.LocalNodeHost.Data.Search.Vector.NodeEfGrantStore(
                new BootstrapClaimRedemptionTests.SharedSearchFactory(homePath));
            foreach (var evidence in AccessGrantAuthorizationSeed.ExpectedInstallerSeedSet(
                tenant, Now, AuthorizationSeedProfile.Production))
            {
                await grants.AppendAsync(tenant, evidence.Grant, evidence.SourceReference);
            }
            var coordinator = new InstallationIdentityCoordinatorService(
                identityFactory,
                new FixedPartitionResolver(tenant.Value, memberships, leases),
                new AcceptingAdmission(),
                new FixedTimeProvider(Now),
                TestAuthorization.Gate(true),
                grants);
            var founderPrincipal = FounderTenantMembershipAttachService.DerivePrincipal(
                tenant,
                InstallationFounderBootstrapCeremony.CorrelationId);
            const string rosterPartyId = "os:founder#15800001";
            ICanonicalPrincipalPartyReader partyReader = partyNeverResolves
                ? new UnresolvablePartyReader()
                : new FixedPartyReader(tenant, founderPrincipal, rosterPartyId);
            var grantIssuance = new InitialGrantIssuanceService(
                grants, TestAuthorization.AllowGate(), time);
            var redemption = new BootstrapClaimRedemptionService(
                identityFactory,
                grants,
                grantIssuance,
                AuthorizationSeedProfile.Production,
                time,
                new Harborline.Kernel.Core.KernelClock(time));
            var service = new FounderTenantMembershipAttachService(
                identityFactory,
                coordinator,
                founderCeremony,
                redemption,
                partyReader,
                Substitute.For<IPartyWriteService>(),
                activeTeamDiverged ? new DivergedTeamAccessor() : new InactiveTeamAccessor(),
                new GenesisTeamIdProvider(team),
                new FounderRosterPartyProvider(rosterPartyId),
                time);

            return new AttachFixture(
                identityFactory,
                homePath,
                tenantPath,
                tenantStore,
                leases,
                tenant,
                founderPrincipal,
                rootGrant,
                grants,
                service);
        }

        public async ValueTask DisposeAsync()
        {
            await _leases.DisposeAsync();
            await _tenantStore.DisposeAsync();
            if (File.Exists(_tenantPath))
            {
                File.Delete(_tenantPath);
            }
            if (File.Exists(_homePath))
            {
                File.Delete(_homePath);
            }
        }
    }

    private sealed class FixedPartitionResolver(
        string tenantId,
        ITenantMembershipAuthorityStore memberships,
        ILeaseCoordinator leases) : ITenantIdentityAuthorityPartitionResolver
    {
        public Task<TenantIdentityAuthorityPartition> ResolveAsync(
            string requestedTenantId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(tenantId, requestedTenantId);
            return Task.FromResult(new TenantIdentityAuthorityPartition(
                tenantId, memberships, leases));
        }
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

    private sealed class UnresolvablePartyReader : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId requestedTenant,
            PrincipalUserId requestedPrincipal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(null);
    }

    private sealed class FixedPartyReader(
        TenantId tenant,
        PrincipalUserId principal,
        string partyId) : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId requestedTenant,
            PrincipalUserId requestedPrincipal,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(
                requestedTenant == tenant && requestedPrincipal == principal
                    ? new CanonicalPartyBinding(
                        tenant, principal, new CanonicalPartyReference(partyId))
                    : null);
    }

    private sealed class InactiveTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;

        private void KeepEvent() => ActiveChanged?.Invoke(
            this,
            new ActiveTeamChangedEventArgs(null, null));
    }

    /// <summary>A host serving a team that is not the genesis team.</summary>
    private sealed class DivergedTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = new(
            new TeamId(Guid.NewGuid()),
            "Non-genesis team",
            new ServiceCollection().BuildServiceProvider(),
            TimeProvider.System);

        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;

        private void KeepEvent() => ActiveChanged?.Invoke(
            this,
            new ActiveTeamChangedEventArgs(null, null));
    }

    private sealed class AlwaysLeaseCoordinator : ILeaseCoordinator
    {
        private readonly ConcurrentDictionary<string, Lease> _held = new(StringComparer.Ordinal);

        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct)
        {
            var lease = new Lease(
                Guid.NewGuid().ToString("N"), resourceId, "ticket-158", Now, Now + duration, []);
            _held[lease.LeaseId] = lease;
            return Task.FromResult<Lease?>(lease);
        }

        public Task ReleaseAsync(Lease lease, CancellationToken ct)
        {
            _held.TryRemove(lease.LeaseId, out _);
            return Task.CompletedTask;
        }

        public bool Holds(string resourceId) =>
            _held.Values.Any(lease => lease.ResourceId == resourceId);

        public IReadOnlyCollection<Lease> HeldLeases => _held.Values.ToArray();

        public ValueTask DisposeAsync()
        {
            _held.Clear();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
