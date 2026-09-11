using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Ship.Common;
using Harborline.Api.Kernel.Lease;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Search;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// The MTW-2 invitation-acceptance saga: invitation and inviter gates before mint, grant before
/// membership, consumption last, grant-anchored membership admission, and non-enumerating gate
/// refusals. Runs the REAL invitation store, joiner account minter, grant issuance, and R3-H
/// coordinator (the coordinator over a fake tenant partition + accepting admission, mirroring the
/// tenant-selection tests); the granter authority and the D2 Party binding are faked so the test
/// targets the acceptance orchestration.
/// </summary>
public sealed class AccountSetupAcceptanceServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 9, 25, 0, TimeSpan.Zero);

    private static readonly string ArgonHash =
        "$argon2id$v=19$m=19456,t=2,p=1$" +
        Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]);

    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public async Task Full_Acceptance_Provisions_Account_Grant_Epoch_And_Membership()
    {
        await using var fixture = await AcceptanceFixture.CreateAsync(inviterRoles: new[] { ShipRole.Captain });

        var result = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
            fixture.RawCode, fixture.TenantId, "joiner", ArgonHash, Guid.NewGuid().ToString("N")));

        Assert.Equal(AccountSetupAcceptStatus.Accepted, result.Status);
        Assert.NotNull(result.AccountId);
        var mintedPrincipal = InstallationAuditIntegrity.Hash(
            "web-tenant-principal/v1", fixture.TenantId, "invitation-1");

        // Element (1): the joiner installation account exists, Active, version 1/1.
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var account = await identity.Accounts.AsNoTracking().SingleAsync(a => a.AccountId == result.AccountId);
            Assert.Equal(InstallationAccountStatus.Active, account.Status);
            Assert.Equal(1, account.SecurityVersion);
            Assert.Equal("JOINER", account.NormalizedUsername);

            // Element (2): membership admission completed via the R3-H coordinator.
            var home = await identity.Coordinators.AsNoTracking().SingleAsync();
            Assert.Equal(InstallationIdentityCoordinatorState.Completed, home.State);
            Assert.Equal(result.AccountId, home.AccountId);
            Assert.Equal(result.AccountId, home.ActorAccountId);
        }

        // Element (3): grant + authorization epoch exist by construction, version 1 / epoch 1.
        await using (var grants = fixture.SearchStore.CreateContext())
        {
            var grant = await grants.Grants.AsNoTracking().SingleAsync();
            Assert.Equal(1, grant.OwnerVersion);
            Assert.Null(grant.RevokedAtUnixMs);
            Assert.Equal(mintedPrincipal, grant.SubjectId);
            Assert.Equal(mintedPrincipal, grant.GrantedBy);
            var epoch = await grants.GrantAuthorizationEpochs.AsNoTracking().SingleAsync();
            Assert.Equal(1, epoch.AuthorizationEpoch);
        }

        // Every acceptance mutation is attributed to the canonical principal of the account minted first.
        Assert.Equal(1, fixture.PartyBinding.MintCalls);
        Assert.Equal(mintedPrincipal, fixture.PartyBinding.LastActor.Value);
        var bootstrapDecisions = fixture.GrantWriter.Decisions
            .Concat(fixture.MembershipWriter.Decisions)
            .ToArray();
        Assert.Equal(2, bootstrapDecisions.Length);
        Assert.All(bootstrapDecisions, decision =>
        {
            Assert.Equal(mintedPrincipal, decision.Request.Principal.Value);
            Assert.Equal(Now, decision.Request.At);
            Assert.Equal(TeamRolePermissions.MembersManage, decision.Request.Act.Operation.Value);
            Assert.Equal(result.AccountId, decision.Request.Target.RecordId);
            Assert.Contains(decision.Resolution.SelectMany(step => step.Outputs),
                output => output == $"bootstrap:{InvitationBootstrapAuthorization.Evidence}");
        });
        Assert.Equal(
            ["invitation-initial-grant", "invitation-membership"],
            bootstrapDecisions.Select(decision => decision.Request.Target.RecordKind).Order(StringComparer.Ordinal));

        // The invitation is single-use: a replay of the same code is refused with no new account.
        var replay = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
            fixture.RawCode, fixture.TenantId, "joiner", ArgonHash, Guid.NewGuid().ToString("N")));
        Assert.Equal(AccountSetupAcceptStatus.InvitationRefused, replay.Status);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public void InvitationBootstrapAuthorization_AuthorizesEachActExactlyOnce()
    {
        var capability = new InvitationBootstrapAuthorization(
            "account-1", new TenantId("tenant-1"), Now, "invitation-1");
        var ceremonyDecisions = new List<AuthorizationDecision>();

        void Observe(Func<AuthorizationDecision> authorize) => ceremonyDecisions.Add(authorize());

        Observe(capability.AuthorizeInitialGrantIssuance);
        var initialReplay = Assert.Throws<InvalidOperationException>(() =>
            Observe(capability.AuthorizeInitialGrantIssuance));
        Observe(capability.AuthorizeMembershipAdmission);
        var membershipReplay = Assert.Throws<InvalidOperationException>(() =>
            Observe(capability.AuthorizeMembershipAdmission));

        Assert.Equal(InvitationBootstrapAuthorization.ActAlreadyAuthorizedCode, initialReplay.Message);
        Assert.Equal(InvitationBootstrapAuthorization.ActAlreadyAuthorizedCode, membershipReplay.Message);
        Assert.Equal(2, ceremonyDecisions.Count);
        Assert.Equal(
            ["invitation-initial-grant", "invitation-membership"],
            ceremonyDecisions.Select(decision => decision.Request.Target.RecordKind));
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3666")]
    public async Task InvitationAdmission_IssuesTaxRolesMemberWithPersonGranterAndReason()
    {
        var requested = PermissionCompositions.Member;
        await using var fixture = await AcceptanceFixture.CreateAsync(
            inviterRoles: new[] { ShipRole.Captain },
            requestedPermissions: requested);

        var result = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
            fixture.RawCode, fixture.TenantId, "joiner", ArgonHash, Guid.NewGuid().ToString("N")));

        Assert.Equal(AccountSetupAcceptStatus.Accepted, result.Status);
        await using var grants = fixture.SearchStore.CreateContext();
        var row = await grants.Grants.AsNoTracking().SingleAsync();
        Assert.Equal(RoleVocabularies.Domain, row.RoleVocabulary);
        Assert.Equal("member", row.RoleName);
        Assert.Equal((int)GranterKind.Person, row.GranterKind);
        Assert.Equal("invitation", row.ReasonCode);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3666")]
    public async Task LegacyEmptyInvitationBundle_DoesNotAlterTheMemberRole()
    {
        await using var fixture = await AcceptanceFixture.CreateAsync(
            inviterRoles: new[] { ShipRole.Captain },
            requestedPermissions: PermissionSet.Empty);

        var result = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
            fixture.RawCode, fixture.TenantId, "joiner", ArgonHash, Guid.NewGuid().ToString("N")));

        Assert.Equal(AccountSetupAcceptStatus.Accepted, result.Status);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3666")]
    public async Task Demoted_Roster_Ceiling_Refuses_A_Stale_Active_Grant_Permission()
    {
        var selected = PermissionSet.Of("invitation:selected");
        await using var fixture = await AcceptanceFixture.CreateAsync(
            inviterRoles: new[] { ShipRole.Captain },
            requestedPermissions: selected,
            authorization: new FixedAuthorizationClosure(authorized: false));

        var result = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
            fixture.RawCode, fixture.TenantId, "joiner", ArgonHash, Guid.NewGuid().ToString("N")));

        Assert.Equal(AccountSetupAcceptStatus.AuthorityRefused, result.Status);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public async Task Unknown_Code_Refuses_Non_Enumerating_Before_Any_Mint()
    {
        await using var fixture = await AcceptanceFixture.CreateAsync(inviterRoles: new[] { ShipRole.Captain });

        var result = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
            "wrong-code", fixture.TenantId, "joiner", ArgonHash, Guid.NewGuid().ToString("N")));

        Assert.Equal(AccountSetupAcceptStatus.InvitationRefused, result.Status);
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        Assert.Equal(1, await identity.Accounts.AsNoTracking().CountAsync()); // founder only — no joiner minted
        Assert.Equal(0, fixture.PartyBinding.MintCalls);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public async Task Lapsed_Inviter_Mandate_Refuses_Without_Consume_Or_Mint()
    {
        // GATE 2: the inviter no longer resolves any held role (lost members:manage / grants revoked).
        await using var fixture = await AcceptanceFixture.CreateAsync(inviterRoles: Array.Empty<ShipRole>());

        var result = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
            fixture.RawCode, fixture.TenantId, "joiner", ArgonHash, Guid.NewGuid().ToString("N")));

        Assert.Equal(AccountSetupAcceptStatus.AuthorityRefused, result.Status);
        await using var identity = fixture.IdentityFactory.CreateDbContext();
        Assert.Equal(1, await identity.Accounts.AsNoTracking().CountAsync()); // no joiner account minted
        Assert.Null((await identity.AccountSetupInvitations.AsNoTracking().SingleAsync()).ConsumedAtUtc);
        Assert.Equal(0, fixture.PartyBinding.MintCalls);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public async Task Inviter_That_Cannot_Attenuate_To_Member_Role_Is_Refused()
    {
        // OOD (rank 4) is strictly below the Scribe (rank 3) member role and cannot confer it.
        await using var fixture = await AcceptanceFixture.CreateAsync(inviterRoles: new[] { ShipRole.OOD });

        var result = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
            fixture.RawCode, fixture.TenantId, "joiner", ArgonHash, Guid.NewGuid().ToString("N")));

        Assert.Equal(AccountSetupAcceptStatus.AuthorityRefused, result.Status);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-2614")]
    public async Task Username_Taken_By_A_Different_Account_Leaves_The_Invitation_Redeemable()
    {
        await using var fixture = await AcceptanceFixture.CreateAsync(inviterRoles: new[] { ShipRole.Captain });
        // The founder already owns the normalized username "FOUNDER"; the joiner cannot reuse it.
        var result = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
            fixture.RawCode, fixture.TenantId, "founder", ArgonHash, Guid.NewGuid().ToString("N")));

        Assert.Equal(AccountSetupAcceptStatus.UsernameConflict, result.Status);
        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var invitation = await identity.AccountSetupInvitations.AsNoTracking().SingleAsync();
            Assert.Null(invitation.ConsumedAtUtc);
            Assert.Equal(2, invitation.OwnerVersion);
        }

        var retry = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
            fixture.RawCode, fixture.TenantId, "joiner", ArgonHash, Guid.NewGuid().ToString("N")));
        Assert.Equal(AccountSetupAcceptStatus.Accepted, retry.Status);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3365")]
    public async Task Third_Username_Conflict_Durably_Consumes_The_Invitation()
    {
        await using var fixture = await AcceptanceFixture.CreateAsync(inviterRoles: new[] { ShipRole.Captain });

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
                fixture.RawCode, fixture.TenantId, "founder", ArgonHash, Guid.NewGuid().ToString("N")));
            Assert.Equal(AccountSetupAcceptStatus.UsernameConflict, result.Status);
        }

        await using (var identity = fixture.IdentityFactory.CreateDbContext())
        {
            var invitation = await identity.AccountSetupInvitations.AsNoTracking().SingleAsync();
            Assert.NotNull(invitation.ConsumedAtUtc);
            Assert.Equal(4, invitation.OwnerVersion);
        }

        var afterBound = await fixture.Service.AcceptAsync(new AccountSetupAcceptCommand(
            fixture.RawCode, fixture.TenantId, "joiner", ArgonHash, Guid.NewGuid().ToString("N")));
        Assert.Equal(AccountSetupAcceptStatus.InvitationRefused, afterBound.Status);
    }

    [Fact]
    [Trait("PlanCard", "MTW-2-3365")]
    public async Task Membership_Pending_Copy_Does_Not_Advise_An_Impossible_Retry()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .ConfigureHttpJsonOptions(_ => { })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;

        var result = await AccountSetupAcceptRoutes.AcceptAsync(
            new FixedAcceptanceAuthority(AccountSetupAcceptStatus.MembershipUnavailable),
            new FixedCredentialFactory(),
            new AcceptingAntiforgeryPolicy(),
            // The route has taken a PairingRedeemRateLimiter since before this branch's merge base;
            // omitting it is what failed the whole test PROJECT to compile, so none of this file's
            // tests ran. Permissive limits — this case asserts the membership-pending COPY, not
            // throttling, and a default-constructed limiter would couple it to the shared 20/min bound.
            new PairingRedeemRateLimiter(perSourceMax: 1_000, perTenantMax: 1_000, clock: TimeProvider.System),
            new AccountSetupAcceptRoutes.AcceptRequest(
                "valid-invitation-code",
                Guid.NewGuid().ToString("D"),
                "joiner",
                "chosen password"),
            context);
        await result.ExecuteAsync(context);

        responseBody.Position = 0;
        using var reader = new StreamReader(responseBody);
        var body = await reader.ReadToEndAsync();
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.DoesNotContain("retry", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"error\":\"membership_pending\"", body, StringComparison.Ordinal);
        Assert.Contains(
            "Your account was created, but membership setup did not complete. " +
            "Ask an administrator for help.",
            body,
            StringComparison.Ordinal);
    }

    private sealed class AcceptanceFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly SearchTestStore _searchStore;
        private readonly ServiceProvider _minterProvider;

        private AcceptanceFixture(
            string directory,
            IdentityContextFactory identityFactory,
            SearchTestStore searchStore,
            ServiceProvider minterProvider,
            AccountSetupAcceptanceService service,
            string rawCode,
            string tenantId,
            string inviterPartyId,
            RecordingPartyBindingMinter partyBinding,
            RecordingGrantWriter grantWriter,
            RecordingMembershipWriter membershipWriter)
        {
            _directory = directory;
            IdentityFactory = identityFactory;
            _searchStore = searchStore;
            _minterProvider = minterProvider;
            Service = service;
            RawCode = rawCode;
            TenantId = tenantId;
            InviterPartyId = inviterPartyId;
            PartyBinding = partyBinding;
            GrantWriter = grantWriter;
            MembershipWriter = membershipWriter;
        }

        public IdentityContextFactory IdentityFactory { get; }
        public SearchTestStore SearchStore => _searchStore;
        public AccountSetupAcceptanceService Service { get; }
        public string RawCode { get; }
        public string TenantId { get; }
        public string InviterPartyId { get; }
        public RecordingPartyBindingMinter PartyBinding { get; }
        public RecordingGrantWriter GrantWriter { get; }
        public RecordingMembershipWriter MembershipWriter { get; }

        public static async Task<AcceptanceFixture> CreateAsync(
            IReadOnlyCollection<ShipRole> inviterRoles,
            PermissionSet? requestedPermissions = null,
            IAuthorizationClosureReader? authorization = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"accept-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var identityFactory = new IdentityContextFactory(Path.Combine(directory, "identity.db"));
            await using (var identity = identityFactory.CreateDbContext())
            {
                await identity.Database.MigrateAsync();
            }

            var time = new FixedTimeProvider(Now);
            var bootstrap = new InstallationFounderBootstrapService(identityFactory, time);
            await bootstrap.InitializeAsync(new InstallationFounderBootstrapCommand(
                "founder",
                ArgonHash,
                Guid.NewGuid().ToString("N"),
                string.Join(":", Enumerable.Repeat("AB", 32)),
                "founder-bootstrap"));

            var tenantId = Guid.NewGuid().ToString("D");
            var rawCode = "invitation-code-with-at-least-256-bits-of-fixture-entropy-x";
            var inviterPartyId = "inviter-party-1";
            await using (var identity = identityFactory.CreateDbContext())
            {
                identity.AccountSetupInvitations.Add(new AccountSetupInvitationRecord
                {
                    InvitationId = "invitation-1",
                    TenantId = tenantId,
                    InviterAccountId = "inviter-account-1",
                    InviterPrincipalId = "inviter-principal-1",
                    InviterPartyId = inviterPartyId,
                    InviterSessionCorrelationId = Guid.NewGuid().ToString("N"),
                    InviterMembershipId = "inviter-membership-1",
                    InviterMembershipOwnerVersion = 1,
                    InviterGrantId = "inviter-grant-1",
                    InviterGrantOwnerVersion = 1,
                    InviterAuthorizationEpoch = 1,
                    RequestedPermissionsJson = JsonSerializer.Serialize(
                        (requestedPermissions ?? PermissionCompositions.Member).Permissions),
                    TokenDigest = Digest(rawCode),
                    Purpose = WebSetupInvitationPurpose.AccountSetup,
                    CommandFingerprint = Digest("command-fingerprint-1"),
                    IssuedAtUtc = Now.AddMinutes(-1),
                    AbsoluteExpiresAtUtc = Now.AddHours(24),
                    ConsumedAtUtc = null,
                    RevokedAtUtc = null,
                    OwnerVersion = 1,
                });
                await identity.SaveChangesAsync();
            }

            var searchStore = await SearchTestStore.CreateAsync();
            var invitationStore = new AccountSetupInvitationStore(identityFactory);
            var grantIssuance = new InitialGrantIssuanceService(
                new NodeEfGrantStore(searchStore.Factory), TestAuthorization.AllowGate(), time);
            var grantWriter = new RecordingGrantWriter(grantIssuance);

            var membershipStore = new RecordingMembershipStore(tenantId);
            var resolver = new FixedPartitionResolver(
                new TenantIdentityAuthorityPartition(tenantId, membershipStore, new AlwaysLeaseCoordinator()));
            var coordinator = new InstallationIdentityCoordinatorService(
                identityFactory, resolver, new AcceptingAdmission(), time, TestAuthorization.Gate(true));
            var membershipWriter = new RecordingMembershipWriter(coordinator);

            var minterServices = new ServiceCollection();
            minterServices.AddSingleton<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>(identityFactory);
            minterServices.AddFrozenKernelClock(time);
            minterServices.AddTransient<WebJoinerAccountMinter>();
            var minterProvider = minterServices.BuildServiceProvider();

            var partyBinding = new RecordingPartyBindingMinter();
            var service = new AccountSetupAcceptanceService(
                invitationStore,
                authorization ?? new FixedAuthorizationClosure(inviterRoles.Contains(ShipRole.Captain)),
                partyBinding,
                grantWriter,
                membershipWriter,
                minterProvider.GetRequiredService<IServiceScopeFactory>(),
                time);

            return new AcceptanceFixture(
                directory, identityFactory, searchStore, minterProvider, service, rawCode, tenantId,
                inviterPartyId, partyBinding, grantWriter, membershipWriter);
        }

        public async ValueTask DisposeAsync()
        {
            await _searchStore.DisposeAsync();
            await _minterProvider.DisposeAsync();
            Directory.Delete(_directory, recursive: true);
        }
    }


    private sealed class FixedAcceptanceAuthority(AccountSetupAcceptStatus status)
        : IAccountSetupAcceptanceAuthority
    {
        public Task<AccountSetupAcceptResult> AcceptAsync(
            AccountSetupAcceptCommand command,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountSetupAcceptResult(status, "account-1"));
    }

    private sealed class FixedCredentialFactory : IWebChosenCredentialFactory
    {
        public WebChosenCredential? Create(string? password) =>
            new(ArgonHash, Guid.NewGuid().ToString("N"));
    }

    private sealed class AcceptingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        public Task<bool> IssueAnonymousAsync(HttpContext context) => Task.FromResult(true);
        public Task<bool> ConsumeAnonymousAsync(HttpContext context) => Task.FromResult(true);
        public Task<bool> ConsumeChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);
        public Task<bool> ConsumeSelectedAsync(HttpContext context, string selectedHandle) =>
            Task.FromResult(true);
        public Task<bool> ConsumeInstallationAsync(HttpContext context, string installationHandle) =>
            Task.FromResult(true);
        public Task<bool> RotateChallengeAsync(HttpContext context, string challengeHandle) =>
            Task.FromResult(true);
        public Task<bool> RotateSelectedAsync(HttpContext context, string selectedHandle) =>
            Task.FromResult(true);
        public void EmitToken(HttpResponse response, string token)
        {
        }
        public void ExpireAnonymousBinding(HttpResponse response)
        {
        }
    }

    private sealed class RecordingPartyBindingMinter : IWebJoinerPartyBindingMinter
    {
        public int MintCalls { get; private set; }
        public PartyId LastActor { get; private set; }

        public Task<CanonicalPartyReference> MintAsync(
            TenantId tenant, PrincipalUserId joinerPrincipal, PartyId actor,
            string displayName, DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
        {
            MintCalls++;
            LastActor = actor;
            return Task.FromResult(new CanonicalPartyReference($"party-{joinerPrincipal.Value[..8]}"));
        }
    }

    internal sealed class RecordingGrantWriter(IInvitationAcceptanceGrantWriter inner)
        : IInvitationAcceptanceGrantWriter
    {
        public List<AuthorizationDecision> Decisions { get; } = [];

        public Task<InitialGrantIssuanceResult> WriteAsync(
            AdmissionCompleted admission,
            string mintedAccountId,
            AuthorizationWriteContext authority,
            AuthorizationDecision decision,
            CancellationToken cancellationToken)
        {
            Decisions.Add(decision);
            return inner.WriteAsync(admission, mintedAccountId, authority, decision, cancellationToken);
        }
    }

    internal sealed class RecordingMembershipWriter(IInvitationAcceptanceMembershipWriter inner)
        : IInvitationAcceptanceMembershipWriter
    {
        public List<AuthorizationDecision> Decisions { get; } = [];

        public Task<InstallationIdentityCoordinationResult> WriteAsync(
            InstallationIdentityCoordinationCommand command,
            AuthorizationWriteContext authority,
            AuthorizationDecision decision,
            CancellationToken cancellationToken)
        {
            Decisions.Add(decision);
            return inner.WriteAsync(command, authority, decision, cancellationToken);
        }
    }

    private sealed class FixedPartitionResolver(params TenantIdentityAuthorityPartition[] partitions)
        : ITenantIdentityAuthorityPartitionResolver
    {
        public Task<TenantIdentityAuthorityPartition> ResolveAsync(string tenantId, CancellationToken ct) =>
            Task.FromResult(partitions.Single(p => p.TenantId == tenantId));
    }

    private sealed class AcceptingAdmission : ITenantMembershipAuthorityAdmission
    {
        public Task ValidateMutationAsync(
            string actorAccountId, string authorityEvidenceDigest, string accountId,
            TenantMembershipMutation mutation, CancellationToken ct) => Task.CompletedTask;
        public Task<long> ValidateExistingAsync(
            string accountId, TenantMembershipSnapshot membership, CancellationToken ct) => Task.FromResult(membership.AuthorizationEpoch);
    }

    private sealed class RecordingMembershipStore(string tenantId) : ITenantMembershipAuthorityStore
    {
        private TenantMembershipFinalizationReceipt? _receipt;
        private string? _accountId;
        public string TenantId { get; } = tenantId;

        public Task PrepareAsync(
            string correlationId, string commandFingerprint, string accountId, string actorAccountId,
            string authorityEvidenceDigest, TenantMembershipMutation mutation,
            DateTimeOffset occurredAtUtc, CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, mutation.TenantId);
            Assert.Equal(0, mutation.ExpectedMembershipOwnerVersion); // new membership
            Assert.Equal(1, mutation.ExpectedGrantOwnerVersion);
            Assert.Equal(1, mutation.AuthorizationEpoch);
            Assert.Equal(TenantMembershipStatus.Active, mutation.TargetStatus);
            _accountId = accountId;
            return Task.CompletedTask;
        }

        public Task<TenantMembershipFinalizationReceipt> FinalizeAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken)
        {
            _receipt ??= new TenantMembershipFinalizationReceipt(
                TenantId,
                DocumentOwnerVersion: 1,
                MembershipId: "membership-1",
                MembershipOwnerVersion: 1,
                MembershipDigest: new string('a', 64),
                AuditSequence: 1,
                AuditHeadHash: new string('b', 64),
                IntentDigest: new string('c', 64),
                HomeDecisionDigest: new string('d', 64));
            return Task.FromResult(_receipt);
        }

        public Task AbortAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<TenantMembershipSnapshot?> GetMembershipAsync(string accountId, CancellationToken ct) =>
            Task.FromResult<TenantMembershipSnapshot?>(null);
        public Task<TenantMembershipIntentState?> GetIntentStateAsync(string correlationId, CancellationToken ct) =>
            Task.FromResult<TenantMembershipIntentState?>(null);
        public Task<bool> IsAdmissionBlockedAsync(string accountId, CancellationToken ct) =>
            Task.FromResult(false);

        public Task PrepareSessionSelectionAsync(
            string correlationId, string commandFingerprint, string accountId, string membershipId,
            string payloadDigest, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<TenantSessionSelectionReceipt> FinalizeSessionSelectionAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task AbortSessionSelectionAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task PrepareSessionRevocationAsync(
            string correlationId, string commandFingerprint, string accountId, string membershipId,
            string sessionCorrelationId, string payloadDigest, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<TenantSessionRevocationReceipt> FinalizeSessionRevocationAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task AbortSessionRevocationAsync(
            string correlationId, string commandFingerprint, DateTimeOffset occurredAtUtc, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class AlwaysLeaseCoordinator : ILeaseCoordinator
    {
        private readonly ConcurrentDictionary<string, Lease> _held = new(StringComparer.Ordinal);
        public Task<Lease?> AcquireAsync(string resourceId, TimeSpan duration, CancellationToken ct)
        {
            var lease = new Lease(Guid.NewGuid().ToString("N"), resourceId, "test", Now, Now + duration, []);
            _held[lease.LeaseId] = lease;
            return Task.FromResult<Lease?>(lease);
        }
        public Task ReleaseAsync(Lease lease, CancellationToken ct)
        {
            _held.TryRemove(lease.LeaseId, out _);
            return Task.CompletedTask;
        }
        public bool Holds(string resourceId) => _held.Values.Any(l => l.ResourceId == resourceId);
        public IReadOnlyCollection<Lease> HeldLeases => _held.Values.ToArray();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
