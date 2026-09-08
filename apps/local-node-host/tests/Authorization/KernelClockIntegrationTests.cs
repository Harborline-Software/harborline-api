using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Services;
using Harborline.Api.Blocks.FinancialPeriods.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.People.Foundation.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.CapabilityAdmission.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.LocalNodeHost.CompromisedDeviceResponse;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Data.Workflow;
using Harborline.Api.LocalNodeHost.Data.Search.Generation;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Security.Crypto;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using KernelEd25519Signer = Harborline.Api.Kernel.Security.Crypto.Ed25519Signer;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>Frozen/advanced-clock proof over the production persistence and dispatch components.</summary>
[Collection("Harborline process environment")]
public sealed class KernelClockIntegrationTests
{
    private static readonly DateTimeOffset FrozenAt = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("ticket-216");
    private static readonly ActorId Principal = new("operator");

    [Theory]
    [InlineData("journal-post")]
    [InlineData("workflow-advance")]
    [InlineData("definition-publish")]
    [InlineData("identity-administration")]
    public async Task ProductionComposition_UsesTheAdmittedInstantAfterClockAdvances(string operation)
    {
        var clock = new MutableHostClock(FrozenAt);
        await using var fixture = await ProductionFixture.CreateAsync(clock);
        await fixture.PrepareAsync(operation);
        clock.ArmAdvancingBoundary(FrozenAt);

        DateTimeOffset[] persisted;
        using (clock.BeginAct())
        {
            persisted = operation switch
            {
                "journal-post" => await fixture.JournalPostAsync(),
                "workflow-advance" => await fixture.WorkflowAdvanceAsync(),
                "definition-publish" => await fixture.DefinitionPublishAsync(),
                "identity-administration" => await fixture.IdentityAdministrationAsync(),
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
        }

        Assert.NotEmpty(persisted);
        Assert.All(persisted, value => Assert.Equal(FrozenAt, value));
        Assert.Equal(1, clock.ActReadCount);
    }

    [Fact]
    public async Task ProductionComposition_AdminRouteRevocation_KillsSessionAndRefusesTransportHello()
    {
        var clock = new MutableHostClock(FrozenAt);
        await using var fixture = await ProductionFixture.CreateAsync(clock);
        await fixture.PrepareAsync("identity-administration");

        await fixture.IdentityAdministrationAsync(assertRevocationEffects: true);
    }

    [Theory]
    [InlineData("roster-projection", true, true, 0, 0)]
    [InlineData("roster-audit", false, true, 0, 0)]
    [InlineData("grant-store", false, true, 1, 0)]
    [InlineData("grant-audit", false, false, 1, 0)]
    public async Task ProductionComposition_AdminRevocation_IsPrefixSafeAndResumable_AfterEveryBoundary(
        string faultStep,
        bool expectedTrustLive,
        bool expectedGrantLive,
        int expectedMemberAudits,
        int expectedCapabilityAudits)
    {
        var clock = new MutableHostClock(FrozenAt);
        await using var fixture = await ProductionFixture.CreateAsync(clock);
        await fixture.PrepareAsync("identity-administration");
        await fixture.InstallRevocationFaultAsync(faultStep);

        await Assert.ThrowsAnyAsync<Exception>(() => fixture.IdentityAdministrationAsync());
        await fixture.AssertRevocationInvariantAsync(
            expectedTrustLive, expectedGrantLive, expectedMemberAudits, expectedCapabilityAudits);
        await fixture.RestartAsync();
        await fixture.AssertRevocationInvariantAsync(expectedTrustLive, expectedGrantLive, 0, 0);

        await fixture.ClearRevocationFaultAsync(faultStep);
        await fixture.IdentityAdministrationAsync();
        await fixture.AssertRevocationInvariantAsync(
            expectedTrustLive: false, expectedGrantLive: false,
            expectedMemberAudits: 1, expectedCapabilityAudits: 1);
    }

    [Theory]
    [InlineData("roster-read", false, false)]
    [InlineData("roster-read", true, false)]
    [InlineData("projection", false, false)]
    [InlineData("projection", true, false)]
    [InlineData("adoption", false, false)]
    [InlineData("adoption", true, false)]
    [InlineData("audit-query", false, false)]
    [InlineData("audit-query", true, false)]
    [InlineData("audit-append", false, false)]
    [InlineData("audit-append", true, false)]
    [InlineData("restart", false, true)]
    [InlineData("restart", true, true)]
    public async Task ProductionComposition_ConcurrentRosterRevocations_LinearizeTheWholeAggregate(
        string interleaving,
        bool distinctTargets,
        bool restart)
    {
        var clock = new MutableHostClock(FrozenAt);
        await using var fixture = await ProductionFixture.CreateAsync(clock);
        await fixture.PrepareAsync("identity-administration");
        if (distinctTargets) await fixture.PrepareSecondRosterTargetAsync();
        var barrier = fixture.InstallConcurrentRevocationBarrier(
            restart ? "roster-read" : interleaving);

        var evidence = await fixture.RevokeRosterConcurrentlyAsync(barrier, distinctTargets);

        await fixture.AssertRosterRevocationPairInvariantAsync(distinctTargets);
        if (distinctTargets) Assert.NotEqual(evidence[0].RecordId, evidence[1].RecordId);
        else Assert.Equal(evidence[0].RecordId, evidence[1].RecordId);
        if (restart)
        {
            await fixture.RestartAsync();
            await fixture.AssertRosterRevocationPairInvariantAsync(distinctTargets, expectedAudits: 0);
            var resumed = await fixture.RevokeRosterPairOnceAsync(distinctTargets);
            Assert.Equal(evidence.Select(item => item.RecordId), resumed.Select(item => item.RecordId));
            await fixture.AssertRosterRevocationPairInvariantAsync(distinctTargets);
        }
    }

    private sealed class ProductionFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly string? _priorInstallRoot;
        private readonly string? _priorRootSeed;
        private readonly string? _priorEventLogLevel;
        private readonly string? _priorWebClientEnabled;
        private IDbContextFactory<LocalNodeDbContext> _nodeFactory;
        private IDbContextFactory<NodeLocalSearchDbContext> _searchFactory;
        private readonly TimeProvider _clock;
        private Uri _baseAddress;
        private string? _kgInstanceId;
        private string? _identityTargetGrantId;
        private string? _identitySecondTargetId;
        private string? _identityHandle;
        private string? _identityTargetHandle;
        private string? _identityTargetPartyId;
        private string? _identitySecondTargetPartyId;
        private NodeIdentity? _identityTargetTransportIdentity;
        private SelectedSessionRequestPrincipal? _identityPrincipal;

        private ProductionFixture(
            string directory,
            string? priorInstallRoot,
            string? priorRootSeed,
            string? priorEventLogLevel,
            string? priorWebClientEnabled,
            Uri baseAddress,
            IServiceProvider services,
            IDbContextFactory<LocalNodeDbContext> nodeFactory,
            IDbContextFactory<NodeLocalSearchDbContext> searchFactory,
            TimeProvider clock)
        {
            _directory = directory;
            _priorInstallRoot = priorInstallRoot;
            _priorRootSeed = priorRootSeed;
            _priorEventLogLevel = priorEventLogLevel;
            _priorWebClientEnabled = priorWebClientEnabled;
            _baseAddress = baseAddress;
            Services = services;
            _nodeFactory = nodeFactory;
            _searchFactory = searchFactory;
            _clock = clock;
        }

        internal IServiceProvider Services { get; private set; }
        internal static async Task<ProductionFixture> CreateAsync(TimeProvider clock)
        {
            var directory = Path.Combine(Path.GetTempPath(), "ticket-216-real-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var priorInstallRoot = Environment.GetEnvironmentVariable("HARBORLINE_TEST_INSTALL_FOOTPRINT_ROOT");
            var priorRootSeed = Environment.GetEnvironmentVariable("LocalNode__RootSeedHex");
            var priorEventLogLevel = Environment.GetEnvironmentVariable("Logging__EventLog__LogLevel__Default");
            var priorWebClientEnabled = Environment.GetEnvironmentVariable("LocalNode__WebClient__Enabled");
            Environment.SetEnvironmentVariable(
                "HARBORLINE_TEST_INSTALL_FOOTPRINT_ROOT",
                Path.Combine(directory, "install-footprint"));
            Environment.SetEnvironmentVariable(
                "LocalNode__RootSeedHex",
                "2162162162162162162162162162162162162162162162162162162162162162");
            Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", "None");
            Environment.SetEnvironmentVariable("LocalNode__WebClient__Enabled", "true");
            var baseAddress = await LocalNodeHostRuntime.StartAsync(
                "ticket-216-kernel-clock",
                directory,
                CancellationToken.None,
                clock);
            var provider = LocalNodeHostRuntime.CurrentServices
                ?? throw new InvalidOperationException("The composed host did not expose its service provider.");
            Assert.Same(clock, provider.GetRequiredService<TimeProvider>());
            Assert.IsType<NodeEfJournalStore>(provider.GetRequiredService<IJournalStore>());
            Assert.IsType<NodeEfWorkflowStore>(provider.GetRequiredService<IWorkflowStore>());
            Assert.IsType<WorkflowTriggerDispatcher>(provider.GetRequiredService<IWorkflowTriggerDispatcher>());
            Assert.IsType<AdminTeamAccessAuthority>(provider.GetRequiredService<IAdminTeamAccessAuthority>());
            Assert.Same(
                provider.GetRequiredService<NodeEfAuthorizationConfigurationStore>(),
                provider.GetRequiredService<IAuthorizationConfigurationStore>());
            var nodeFactory = provider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            var searchFactory = provider.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>();
            return new(
                directory,
                priorInstallRoot,
                priorRootSeed,
                priorEventLogLevel,
                priorWebClientEnabled,
                baseAddress,
                provider,
                nodeFactory,
                searchFactory,
                clock);
        }

        internal async Task PrepareAsync(string operation)
        {
            switch (operation)
            {
                case "journal-post":
                    await SeedOperatorGrantAsync();
                    await SeedFinancialPostingPrerequisitesAsync();
                    break;
                case "definition-publish":
                    await SeedOperatorGrantAsync();
                    break;
                case "workflow-advance":
                {
                    var proposal = new KgGenerationProposal(
                        "Draft the admitted-instant proof JE.",
                        "qwen2.5-7b-instruct",
                        "1.0",
                        ["journal-216"],
                        KgProposalTaint.UntrustedDerived,
                        new KgProposedAction(KgProposedAction.DraftJournalEntry, "Draft proof JE", "{}"));
                    var parked = await Services.GetRequiredService<NodeKgActionApprovalCutover>()
                        .ParkForApprovalAsync(
                            Tenant,
                            "kernel-clock-kg",
                            proposal,
                            new KgActionExecutionInput("1000", "2000", 10m, "clock proof", false));
                    Assert.Equal(WorkflowDispatchResult.Parked, parked.DecideResult);
                    _kgInstanceId = parked.InstanceId;
                    break;
                }
                case "identity-administration":
                    await PrepareIdentityAdministrationAsync();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        internal async Task<DateTimeOffset[]> JournalPostAsync()
        {
            using var client = Client();
            using var response = await client.PostAsJsonAsync(JournalEntryRoutes.RouteBase, new
            {
                postingDate = "2026-07-23",
                memo = "kernel clock proof",
                chartId = "clock-chart",
                lines = new[]
                {
                    new { accountCode = "1000", amount = 10m, direction = "Debit" },
                    new { accountCode = "2000", amount = 10m, direction = "Credit" },
                },
            });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            await using var context = await _nodeFactory.CreateDbContextAsync();
            var saved = await context.Set<JournalEntry>().AsNoTracking()
                .SingleAsync(x => x.Memo == "kernel clock proof");
            var audit = await context.Set<NodeAuditEventRow>().AsNoTracking()
                .SingleAsync(x => x.EventType == "Financial.JournalPosted");
            return [saved.CreatedAtUtc.Value, saved.PostedAtUtc!.Value.Value, audit.OccurredAt];
        }

        internal async Task<DateTimeOffset[]> WorkflowAdvanceAsync()
        {
            var instanceId = Assert.IsType<string>(_kgInstanceId);
            var result = await Services.GetRequiredService<NodeKgActionApprovalCutover>()
                .ResumeAsync(instanceId, "approve", null);
            Assert.Equal(WorkflowDispatchResult.Advanced, result);
            await using var context = await _nodeFactory.CreateDbContextAsync();
            var instance = await context.Set<WorkflowInstanceRecord>().AsNoTracking()
                .SingleAsync(row => row.Id == instanceId);
            var workflowEvent = await context.Set<WorkflowEventRecord>().AsNoTracking()
                .Where(row => row.InstanceId == instanceId)
                .OrderByDescending(row => row.Seq)
                .FirstAsync();
            var idempotency = await context.Set<WorkflowStepIdempotencyRecord>().AsNoTracking()
                .SingleAsync(row => row.InstanceId == instanceId && row.Step == GraphRagProposalSteps.Approve);
            var effect = await context.Set<JournalEntry>().AsNoTracking()
                .SingleAsync(x => x.SourceReference == "kg-action:" + instanceId);
            return [instance.UpdatedAt, workflowEvent.OccurredAt,
                idempotency.CompletedAt, effect.CreatedAtUtc.Value];
        }

        internal async Task<DateTimeOffset[]> DefinitionPublishAsync()
        {
            const string key = "kernel-clock.v1";
            using var client = Client();
            using var response = await client.PutAsJsonAsync(
                $"{WorkflowDefinitionRoutes.RouteBase}/{key}", WorkflowDefinitionBody(key));
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            var tenant = NodeTenant.Resolve(Services.GetRequiredService<IActiveTeamAccessor>());
            var row = await Services.GetRequiredService<IWorkflowDefinitionStore>()
                .GetCurrentPublishedAsync(new DefinitionAddress(tenant, key));
            return [Assert.IsType<WorkflowDefinitionRecord>(row).UpdatedAt];
        }

        private HttpClient Client()
        {
            var client = new HttpClient { BaseAddress = _baseAddress };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", "ticket-216-kernel-clock");
            return client;
        }

        private async Task SeedOperatorGrantAsync()
        {
            var tenant = NodeTenant.Resolve(Services.GetRequiredService<IActiveTeamAccessor>());
            var principal = new ActorId(NodeCallerParty.OperatorParty.Value);
            await Services.GetRequiredService<IGrantStore>().AppendAsync(
                tenant,
                new AccessGrant(
                    GrantId.New(), tenant, principal, RoleReference.Administrator,
                    ScopeExpression.Parse("/"), GrantResidency.Cache,
                    new GrantValidity(FrozenAt.AddMinutes(-1), FrozenAt.AddHours(1)),
                    GranterKind.Person, principal, FrozenAt.AddMinutes(-1),
                    new GrantProvenance(
                        GrantSourceKind.Manual,
                        new GrantReason(GrantReasonCodes.Manual, "ticket-216-kernel-clock"),
                        principal),
                    FrozenAt.AddMinutes(-1)),
                "ticket-216-kernel-clock");
        }

        private async Task SeedFinancialPostingPrerequisitesAsync()
        {
            await using var context = await _nodeFactory.CreateDbContextAsync();
            context.Set<GLAccount>().AddRange(
                GLAccount.Create(
                    new GLAccountId("1000"), new ChartOfAccountsId("clock-chart"),
                    "1000", "Cash", GLAccountType.Asset, AccountSubtype.BankAccount,
                    "USD", new Instant(FrozenAt), isPostable: true),
                GLAccount.Create(
                    new GLAccountId("2000"), new ChartOfAccountsId("clock-chart"),
                    "2000", "Payable", GLAccountType.Liability, AccountSubtype.AccountsPayable,
                    "USD", new Instant(FrozenAt), isPostable: true));
            context.Set<FiscalPeriod>().Add(FiscalPeriod.CreateOpen(
                FiscalPeriodId.NewId(),
                new ChartOfAccountsId("clock-chart"),
                new FiscalYearId("FY-2026"),
                FiscalPeriodKind.Monthly,
                "2026-07",
                new DateOnly(2026, 7, 1),
                new DateOnly(2026, 7, 31),
                new Instant(FrozenAt)));
            await context.SaveChangesAsync();
        }

        private static object WorkflowDefinitionBody(string key) => new
        {
            key,
            version = "0.0.1",
            status = "Draft",
            tenant = "server-owned",
            title = LocalizedText("Clock proof"),
            mutability = "Locked",
            initialState = "Draft",
            states = new object[]
            {
                new { id = "Draft", label = LocalizedText("Draft"), kind = "Normal" },
                new { id = "PendingApproval", label = LocalizedText("Pending"), kind = "Normal" },
                new { id = "Posted", label = LocalizedText("Posted"), kind = "Terminal" },
            },
            triggers = new object[]
            {
                new { id = "issued", kind = "Event", eventType = "Issued" },
                new { id = "approve", kind = "HumanAction", task = "approval" },
            },
            transitions = new object[]
            {
                new { id = "t-issue", from = "Draft", on = "issued", to = "PendingApproval" },
                new { id = "t-approve", from = "PendingApproval", on = "approve", to = "Posted" },
            },
            actions = new object[]
            {
                new
                {
                    id = "a-post-je",
                    on = new { transition = "t-approve" },
                    kind = "CreateRecord",
                    capabilityRef = "ledger.post-journal-entry",
                    classification = "CP",
                },
            },
            guards = Array.Empty<object>(),
        };

        private static object LocalizedText(string value) => new
        {
            defaultLocale = "en",
            values = new Dictionary<string, string> { ["en"] = value },
        };

        private async Task PrepareIdentityAdministrationAsync()
        {
            var tenant = NodeTenant.Resolve(Services.GetRequiredService<IActiveTeamAccessor>());
            var roster = await Services.GetRequiredService<IVerifiedTenantRosterReader>()
                .ReadAsync(tenant, CancellationToken.None);
            var admin = roster.Members.Single(member =>
                roster.PermissionsOf(member.PartyId)!.Contains(TeamRolePermissions.MembersManage));
            var adminParty = new PartyId(admin.PartyId);
            var adminPrincipal = new ActorId("ticket-216-identity-admin");
            var people = Services.GetRequiredService<IPartyReadModel>();
            var peopleWrites = Services.GetRequiredService<IPartyWriteService>();
            if (await people.GetByIdAsync(adminParty, CancellationToken.None) is null)
            {
                await peopleWrites.CreateAsync(
                    tenant,
                    PartyKind.Person,
                    "Ticket 216 identity administrator",
                    adminParty,
                    FrozenAt.AddMinutes(-1),
                    adminParty,
                    CancellationToken.None);
            }
            await peopleWrites.AttachRoleAsync(
                adminParty,
                NodeEfPartyRepository.PrincipalUserBindingRoleName,
                adminPrincipal.Value,
                adminParty,
                FrozenAt.AddMinutes(-1),
                CancellationToken.None);

            const string accountId = "ticket-216-identity-admin-account";
            var identityFactory = Services
                .GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>();
            var founderCredential = Services.GetRequiredService<IWebChosenCredentialFactory>()
                .Create("ticket-238-fixture-founder-password");
            Assert.NotNull(founderCredential);
            var bootstrap = await Services.GetRequiredService<InstallationFounderBootstrapService>()
                .InitializeAsync(new InstallationFounderBootstrapCommand(
                    "ticket-238-fixture-founder",
                    founderCredential!.CredentialHash,
                    founderCredential.CredentialCeremonyId,
                    string.Join(":", Enumerable.Repeat("AB", 32)),
                    "ticket-238-fixture-bootstrap"));
            Assert.Equal(InstallationFounderBootstrapStatus.Created, bootstrap.Status);
            await using (var identity = await identityFactory.CreateDbContextAsync())
            {
                identity.Accounts.Add(new InstallationAccountRecord
                {
                    AccountId = accountId,
                    NormalizedUsername = "TICKET_216_IDENTITY_ADMIN",
                    CredentialHash = "ticket-216-test-digest",
                    CredentialAlgorithm = "test",
                    CredentialCeremonyId = "ticket-216",
                    CredentialVersion = 1,
                    Status = InstallationAccountStatus.Active,
                    SecurityVersion = 1,
                    OwnerVersion = 1,
                    CreatedAtUtc = FrozenAt.AddMinutes(-1),
                    UpdatedAtUtc = FrozenAt.AddMinutes(-1),
                });
                await identity.SaveChangesAsync();
            }

            var grant = await Services.GetRequiredService<IGrantStore>().AppendAsync(
                tenant,
                new AccessGrant(
                    GrantId.New(), tenant, adminPrincipal, RoleReference.Administrator,
                    ScopeExpression.Parse("/"), GrantResidency.Cache,
                    new GrantValidity(FrozenAt.AddMinutes(-1), FrozenAt.AddHours(1)),
                    GranterKind.Person, adminPrincipal, FrozenAt.AddMinutes(-1),
                    new GrantProvenance(
                        GrantSourceKind.Manual,
                        new GrantReason(GrantReasonCodes.Manual, "ticket-216-identity-clock"),
                        adminPrincipal),
                    FrozenAt.AddMinutes(-1)),
                "ticket-216-identity-clock");
            var grantId = grant.GrantId.Value.ToString("D");

            await using var grantsContext = await _searchFactory.CreateDbContextAsync();
            var grantRow = await grantsContext.Grants.AsNoTracking().SingleAsync(row =>
                row.TenantId == tenant.Value && row.GrantId == grantId);
            var epoch = await grantsContext.GrantAuthorizationEpochs.AsNoTracking().SingleAsync(row =>
                row.TenantId == tenant.Value && row.PrincipalId == grant.Subject.Value);

            _identityHandle = "ticket-216-selected-handle";
            var session = new WebUserSessionRecord(
                "ticket-216-selected-session",
                accountId,
                1,
                tenant.Value,
                "ticket-216-identity-admin-membership",
                1,
                grant.Subject.Value,
                admin.PartyId,
                [new PinnedGrantOwnerVersion(grantId, grantRow.OwnerVersion)],
                epoch.AuthorizationEpoch,
                AccountSetupInvitationStore.Digest(_identityHandle),
                "ticket-216-antiforgery",
                "ticket-216-coordination",
                FrozenAt.AddMinutes(-1),
                FrozenAt.AddMinutes(30),
                FrozenAt.AddHours(1),
                1);
            var sessionFactory = Services
                .GetRequiredService<IDbContextFactory<NodeLocalWebSessionDbContext>>();
            await using (var sessions = await sessionFactory.CreateDbContextAsync())
            {
                sessions.UserSessions.Add(session);
                await sessions.SaveChangesAsync();
            }

            _identityPrincipal = new SelectedSessionRequestPrincipal(
                accountId,
                tenant,
                new PrincipalUserId(grant.Subject.Value),
                new CanonicalPartyReference(admin.PartyId),
                "ticket-216-identity-admin-membership",
                1,
                session.PinnedGrantOwnerVersions,
                epoch.AuthorizationEpoch,
                session.SessionCorrelationId,
                session.CoordinationCorrelationId);

            var probe = await Services.GetRequiredService<AuthorizationGate>().DecideAsync(
                new AuthorizationWriteContext(adminPrincipal, tenant, FrozenAt).Request(
                    AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
                    "members",
                    "kernel-clock-identity"));
            if (probe.Verdict != AuthorizationVerdict.Allowed)
                throw new InvalidOperationException(JsonSerializer.Serialize(probe.Resolution));

            var issued = await Services.GetRequiredService<IAdminTeamAccessAuthority>().IssueInvitationAsync(
                _identityHandle,
                tenant.Value,
                PermissionCompositions.Member.Permissions,
                "ticket-238-target-invitation",
                new AuthorizationWriteContext(adminPrincipal, tenant, FrozenAt));
            Assert.NotNull(issued);
            var credential = Services.GetRequiredService<IWebChosenCredentialFactory>()
                .Create("ticket-238-target-password");
            Assert.NotNull(credential);
            var accepted = await Services.GetRequiredService<IAccountSetupAcceptanceAuthority>().AcceptAsync(
                new AccountSetupAcceptCommand(
                    issued!.Code,
                    tenant.Value,
                    "ticket-238-revocation-target",
                    credential!.CredentialHash,
                    credential.CredentialCeremonyId));
            Assert.Equal(AccountSetupAcceptStatus.Accepted, accepted.Status);
            var targetAccountId = Assert.IsType<string>(accepted.AccountId);
            var targetMembership = await Services.GetRequiredService<InstallationIdentityCoordinatorService>()
                .ResolveUsableMembershipAsync(targetAccountId, tenant.Value);
            Assert.NotNull(targetMembership);
            var targetPrincipal = new PrincipalUserId(targetMembership!.CanonicalPrincipalId);
            var targetBinding = await Services.GetRequiredService<ICanonicalPrincipalPartyReader>()
                .ResolveAsync(tenant, targetPrincipal);
            Assert.NotNull(targetBinding);
            var targetParty = new PartyId(targetBinding!.PartyId.Value);
            _identityTargetGrantId = targetMembership.GrantId;

            _identityTargetHandle = "ticket-238-selected-handle";
            var targetSession = new WebUserSessionRecord(
                "ticket-238-selected-session",
                targetAccountId,
                1,
                tenant.Value,
                targetMembership.MembershipId,
                targetMembership.OwnerVersion,
                targetPrincipal.Value,
                targetParty.Value,
                [new PinnedGrantOwnerVersion(_identityTargetGrantId, targetMembership.GrantOwnerVersion)],
                targetMembership.AuthorizationEpoch,
                AccountSetupInvitationStore.Digest(_identityTargetHandle),
                "ticket-238-antiforgery",
                "ticket-238-coordination",
                FrozenAt.AddMinutes(-1),
                FrozenAt.AddMinutes(30),
                FrozenAt.AddHours(1),
                1);
            await using (var targetSessions = await sessionFactory.CreateDbContextAsync())
            {
                targetSessions.UserSessions.Add(targetSession);
                await targetSessions.SaveChangesAsync();
            }

            var transportSigner = new KernelEd25519Signer();
            var (transportPublicKey, transportPrivateKey) = transportSigner.GenerateKeyPair();
            _identityTargetTransportIdentity = new NodeIdentity(
                "23823823823823823823823823823823", transportPublicKey, transportPrivateKey);
            _identityTargetPartyId = targetParty.Value;
            var dmKey = RandomNumberGenerator.GetBytes(PrincipalId.LengthInBytes);
            var xwingKey = RandomNumberGenerator.GetBytes(RosterRecordCrdtState.XWingPublicKeyLength);
            var operationSigner = Services.GetRequiredService<IOperationSigner>();
            var operationVerifier = Services.GetRequiredService<IOperationVerifier>();
            var nodeRoster = Services.GetRequiredService<NodeTeamRoster>();
            var withTarget = nodeRoster.Current.Admit(
                admin.PartyId, operationSigner, targetParty.Value, PrincipalId.FromBytes(transportPublicKey),
                PermissionCompositions.Member, operationVerifier, FrozenAt.AddMinutes(-1), Guid.NewGuid(),
                newDmPublicKey: PrincipalId.FromBytes(dmKey).ToBase64Url(),
                newXWingPublicKey: RawBase64Url(xwingKey));
            var admission = withTarget.EnumerateAdmissions().Single(item => item.PartyId == targetParty.Value);
            await Services.GetRequiredService<RosterCrdtProjection>().PublishLocalAsync(
                RosterRecordCrdtState.FromAdmission(admission, transportPublicKey, dmKey, xwingKey),
                CancellationToken.None);
            nodeRoster.AdoptSyncedRoster(
                withTarget,
                new Dictionary<string, byte[]> { [targetParty.Value] = transportPublicKey },
                new Dictionary<string, byte[]> { [targetParty.Value] = dmKey },
                new Dictionary<string, byte[]> { [targetParty.Value] = xwingKey });

            Assert.NotNull(await Services.GetRequiredService<IWebSelectedSessionPrincipalAuthority>()
                .AuthenticateAsync(_identityTargetHandle));
            Assert.Contains(nodeRoster.TrustedTransportKeys(), key => key.AsSpan().SequenceEqual(transportPublicKey));
            Assert.NotNull(nodeRoster.DmPublicKeyOf(targetParty.Value));
            Assert.NotNull(nodeRoster.XWingPublicKeyOf(targetParty.Value));
        }

        internal async Task<DateTimeOffset[]> IdentityAdministrationAsync(bool assertRevocationEffects = false)
        {
            var context = new DefaultHttpContext { RequestServices = Services };
            context.Request.Headers.Cookie =
                $"{WebSessionCookieNames.Selected}={Assert.IsType<string>(_identityHandle)}";
            context.Features.Set(Assert.IsType<SelectedSessionRequestPrincipal>(_identityPrincipal));
            context.Response.Body = new MemoryStream();
            var result = await AdminTeamAccessRoutes.RevokeMemberAsync(
                Services.GetRequiredService<IAdminTeamAccessAuthority>(),
                new PassingAntiforgeryPolicy(),
                new AdminTeamAccessRoutes.RevokeMemberRequest(_identityTargetGrantId),
                context,
                _clock.GetUtcNow());
            await result.ExecuteAsync(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

            if (assertRevocationEffects)
            {
                Assert.Null(await Services.GetRequiredService<IWebSelectedSessionPrincipalAuthority>()
                    .AuthenticateAsync(Assert.IsType<string>(_identityTargetHandle)));
                await AssertTransportHelloRefusedAsync();
                var targetParty = Assert.IsType<string>(_identityTargetPartyId);
                var roster = Services.GetRequiredService<NodeTeamRoster>();
                Assert.False(roster.Current.Contains(targetParty));
                Assert.DoesNotContain(roster.TrustedTransportKeys(), key => key.AsSpan().SequenceEqual(
                    Assert.IsType<NodeIdentity>(_identityTargetTransportIdentity).PublicKey));
                Assert.Null(roster.DmPublicKeyOf(targetParty));
                Assert.Null(roster.XWingPublicKeyOf(targetParty));
                await AssertRevocationAuditsShareAuthorityAsync();
            }

            await using var grants = await _searchFactory.CreateDbContextAsync();
            var targetId = Assert.IsType<string>(_identityTargetGrantId);
            var row = await grants.Grants.AsNoTracking().SingleAsync(item => item.GrantId == targetId);
            return [DateTimeOffset.FromUnixTimeMilliseconds(Assert.IsType<long>(row.RevokedAtUnixMs))];
        }

        internal async Task InstallRevocationFaultAsync(string step)
        {
            if (step == "roster-projection")
            {
                var factory = Services.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
                await using var context = await factory.CreateDbContextAsync();
                await context.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER fail_ticket_238_roster BEFORE INSERT ON roster_records
                    BEGIN SELECT RAISE(ABORT, 'ticket 238 roster projection fault'); END;
                    """);
                return;
            }

            if (step == "grant-store")
            {
                await using var grants = await _searchFactory.CreateDbContextAsync();
                await grants.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER fail_ticket_238_grant BEFORE UPDATE OF status ON search_grants
                    BEGIN SELECT RAISE(ABORT, 'ticket 238 grant store fault'); END;
                    """);
                return;
            }

            var eventType = step switch
            {
                "roster-audit" => AuditEventType.MemberRevoked,
                "grant-audit" => AuditEventType.CapabilityRevoked,
                _ => throw new ArgumentOutOfRangeException(nameof(step)),
            };
            var fault = new BoundaryFaultingAuditTrail(
                Services.GetRequiredService<IAuthorizedAuditTrail>(), eventType);
            ReplaceAuditTrail(Services.GetRequiredService<IAdminTeamAccessAuthority>(), fault);
            ReplaceAuditTrail(Services.GetRequiredService<INodeRosterMemberRevocationAuthority>(), fault);
        }

        internal async Task ClearRevocationFaultAsync(string step)
        {
            if (step == "roster-projection")
            {
                var factory = Services.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
                await using var context = await factory.CreateDbContextAsync();
                await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS fail_ticket_238_roster;");
            }
            else if (step == "grant-store")
            {
                await using var grants = await _searchFactory.CreateDbContextAsync();
                await grants.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS fail_ticket_238_grant;");
            }
        }

        internal RevocationInterleavingBarrier InstallConcurrentRevocationBarrier(string interleaving)
        {
            var owner = Services.GetRequiredService<INodeRosterMemberRevocationAuthority>();
            var barrier = new RevocationInterleavingBarrier();
            switch (interleaving)
            {
                case "roster-read":
                    ReplaceDependency<IOperationSigner>(owner,
                        new BarrierSigner(Services.GetRequiredService<IOperationSigner>(), barrier));
                    break;
                case "projection":
                case "adoption":
                    ReplaceDependency<IRosterRevocationProjection>(owner,
                        new BarrierProjection(
                            Services.GetRequiredService<IRosterRevocationProjection>(), barrier, interleaving));
                    break;
                case "audit-query":
                case "audit-append":
                    ReplaceDependency<IAuthorizedAuditTrail>(owner,
                        new BarrierAuditTrail(
                            Services.GetRequiredService<IAuthorizedAuditTrail>(), barrier, interleaving));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(interleaving));
            }
            return barrier;
        }

        internal async Task<CompromisedDeviceRevocation[]> RevokeRosterConcurrentlyAsync(
            RevocationInterleavingBarrier barrier,
            bool distinctTargets)
        {
            var firstTarget = Assert.IsType<string>(_identityTargetGrantId);
            var secondTarget = distinctTargets
                ? Assert.IsType<string>(_identitySecondTargetId)
                : firstTarget;
            var firstDecision = await DecideRosterRevocationAsync(firstTarget);
            var secondDecision = distinctTargets
                ? await DecideRosterRevocationAsync(secondTarget)
                : firstDecision;
            var calls = Task.WhenAll(
                Task.Run(() => RevokeRosterAsync(
                    firstTarget, Assert.IsType<string>(_identityTargetPartyId), firstDecision)),
                Task.Run(() => RevokeRosterAsync(
                    secondTarget,
                    distinctTargets
                        ? Assert.IsType<string>(_identitySecondTargetPartyId)
                        : Assert.IsType<string>(_identityTargetPartyId),
                    secondDecision)));
            await barrier.ReleaseAfterInterleavingAsync();
            return await calls;
        }

        internal async Task<CompromisedDeviceRevocation> RevokeRosterOnceAsync() =>
            await RevokeRosterAsync(
                Assert.IsType<string>(_identityTargetGrantId),
                Assert.IsType<string>(_identityTargetPartyId),
                await DecideRosterRevocationAsync(Assert.IsType<string>(_identityTargetGrantId)));

        internal async Task<CompromisedDeviceRevocation[]> RevokeRosterPairOnceAsync(bool distinctTargets)
        {
            var first = await RevokeRosterOnceAsync();
            if (!distinctTargets) return [first, await RevokeRosterOnceAsync()];

            var target = Assert.IsType<string>(_identitySecondTargetId);
            var second = await RevokeRosterAsync(
                target,
                Assert.IsType<string>(_identitySecondTargetPartyId),
                await DecideRosterRevocationAsync(target));
            return [first, second];
        }

        private async Task<AuthorizationDecision> DecideRosterRevocationAsync(string targetId)
        {
            var principal = Assert.IsType<SelectedSessionRequestPrincipal>(_identityPrincipal);
            var decision = await Services.GetRequiredService<AuthorizationGate>().DecideAsync(
                new AuthorizationWriteContext(
                    new ActorId(principal.PrincipalUserId.Value), principal.TenantId, FrozenAt)
                    .Request(
                        AuthorizationOperation.Parse(TeamRolePermissions.MembersManage),
                        "members",
                        targetId));
            return decision.RequireAllowed();
        }

        private async Task<CompromisedDeviceRevocation> RevokeRosterAsync(
            string decisionTargetId,
            string revokedPartyId,
            AuthorizationDecision decision)
        {
            var principal = Assert.IsType<SelectedSessionRequestPrincipal>(_identityPrincipal);
            return Assert.IsType<CompromisedDeviceRevocation>(
                await Services.GetRequiredService<INodeRosterMemberRevocationAuthority>().RevokeAsync(
                    principal.TenantId,
                    decisionTargetId,
                    revokedPartyId,
                    principal.CanonicalParty.Value,
                    MemberRevocationReasons.Offboarding,
                    correlationId: null,
                    decision,
                    CancellationToken.None));
        }

        internal async Task PrepareSecondRosterTargetAsync()
        {
            const string targetId = "ticket-238-revocation-target-two";
            const string targetParty = "ticket-238-roster-target-two";
            var signer = new KernelEd25519Signer();
            var (publicKey, _) = signer.GenerateKeyPair();
            var dmKey = RandomNumberGenerator.GetBytes(PrincipalId.LengthInBytes);
            var xwingKey = RandomNumberGenerator.GetBytes(RosterRecordCrdtState.XWingPublicKeyLength);
            var operationSigner = Services.GetRequiredService<IOperationSigner>();
            var verifier = Services.GetRequiredService<IOperationVerifier>();
            var roster = Services.GetRequiredService<NodeTeamRoster>();
            var adminParty = Assert.IsType<SelectedSessionRequestPrincipal>(_identityPrincipal).CanonicalParty.Value;
            var withTarget = roster.Current.Admit(
                adminParty, operationSigner, targetParty, PrincipalId.FromBytes(publicKey),
                PermissionCompositions.Member, verifier, FrozenAt.AddMinutes(-1), Guid.NewGuid(),
                newDmPublicKey: PrincipalId.FromBytes(dmKey).ToBase64Url(),
                newXWingPublicKey: RawBase64Url(xwingKey));
            var admission = withTarget.EnumerateAdmissions().Single(item => item.PartyId == targetParty);
            await Services.GetRequiredService<RosterCrdtProjection>().PublishLocalAsync(
                RosterRecordCrdtState.FromAdmission(admission, publicKey, dmKey, xwingKey),
                CancellationToken.None);
            roster.AdoptSyncedRoster(
                withTarget,
                new Dictionary<string, byte[]> { [targetParty] = publicKey },
                new Dictionary<string, byte[]> { [targetParty] = dmKey },
                new Dictionary<string, byte[]> { [targetParty] = xwingKey });
            _identitySecondTargetId = targetId;
            _identitySecondTargetPartyId = targetParty;
        }

        internal async Task AssertRosterRevocationPairInvariantAsync(
            bool distinctTargets,
            int expectedAudits = 1)
        {
            var parties = distinctTargets
                ? new[]
                {
                    Assert.IsType<string>(_identityTargetPartyId),
                    Assert.IsType<string>(_identitySecondTargetPartyId),
                }
                : [Assert.IsType<string>(_identityTargetPartyId)];
            var targets = distinctTargets
                ? new[]
                {
                    Assert.IsType<string>(_identityTargetGrantId),
                    Assert.IsType<string>(_identitySecondTargetId),
                }
                : [Assert.IsType<string>(_identityTargetGrantId)];
            List<NodeRosterRecord> durableRecords;
            await using (var context = await Services
                .GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>()
                .CreateDbContextAsync())
            {
                durableRecords = await context.Set<NodeRosterRecord>().AsNoTracking().ToListAsync();
                var durableRevocations = durableRecords
                    .Where(record => record.Kind == (int)RosterRecordKind.Revocation
                        && parties.Contains(record.PartyId))
                    .ToList();
                Assert.Equal(parties.Length, durableRevocations.Count);
                Assert.All(parties, party =>
                    Assert.Single(durableRevocations, record => record.PartyId == party));
            }

            var tenant = NodeTenant.Resolve(Services.GetRequiredService<IActiveTeamAccessor>());
            var auditedTargets = new List<string>();
            await foreach (var record in Services.GetRequiredService<IAuditTrail>()
                               .QueryAsync(new AuditQuery(tenant, AuditEventType.MemberRevoked)))
            {
                if (record.Target?.RecordId is { } target && targets.Contains(target, StringComparer.OrdinalIgnoreCase))
                    auditedTargets.Add(target);
            }
            Assert.Equal(expectedAudits * targets.Length, auditedTargets.Count);
            Assert.All(targets, target => Assert.Equal(
                expectedAudits,
                auditedTargets.Count(actual => string.Equals(actual, target, StringComparison.OrdinalIgnoreCase))));

            var snapshot = durableRecords.Select(NodeRosterRecord.ToCrdtState).ToArray();
            var replay = MemberRoster.FromSyncedRecords(
                snapshot.Select(record => record.ToAdmissionOrNull()).OfType<MemberAdmissionRecord>(),
                snapshot.Select(record => record.ToRevocationOrNull()).OfType<MemberRevocationRecord>(),
                Services.GetRequiredService<IOperationVerifier>());
            var live = Services.GetRequiredService<NodeTeamRoster>().Current;
            Assert.All(parties, party => Assert.False(live.Contains(party)));
            Assert.Equal(replay.TeamId, live.TeamId);
            Assert.Equal(
                replay.Members.Select(member => member.PartyId).Order(StringComparer.Ordinal),
                live.Members.Select(member => member.PartyId).Order(StringComparer.Ordinal));
        }

        private static void ReplaceDependency<T>(object owner, T replacement) where T : class
        {
            var field = owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => typeof(T).IsAssignableFrom(candidate.FieldType));
            field.SetValue(owner, replacement);
        }

        internal async Task AssertRevocationInvariantAsync(
            bool expectedTrustLive,
            bool expectedGrantLive,
            int expectedMemberAudits,
            int expectedCapabilityAudits)
        {
            await using var grants = await _searchFactory.CreateDbContextAsync();
            var targetId = Assert.IsType<string>(_identityTargetGrantId);
            var grantLive = (await grants.Grants.AsNoTracking().SingleAsync(row => row.GrantId == targetId))
                .RevokedAtUnixMs is null;
            var targetKey = Assert.IsType<NodeIdentity>(_identityTargetTransportIdentity).PublicKey;
            var trustLive = Services.GetRequiredService<NodeTeamRoster>().TrustedTransportKeys()
                .Any(key => key.AsSpan().SequenceEqual(targetKey));
            Assert.Equal(expectedTrustLive, trustLive);
            Assert.Equal(expectedGrantLive, grantLive);
            Assert.False(trustLive && !grantLive, "Live transport trust requires a live grant.");

            var tenant = NodeTenant.Resolve(Services.GetRequiredService<IActiveTeamAccessor>());
            var memberAudits = 0;
            var capabilityAudits = 0;
            await foreach (var record in Services.GetRequiredService<IAuditTrail>()
                               .QueryAsync(new AuditQuery(tenant)))
            {
                if (!string.Equals(record.Target?.RecordId, targetId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (record.EventType == AuditEventType.MemberRevoked) memberAudits++;
                if (record.EventType == AuditEventType.CapabilityRevoked) capabilityAudits++;
            }
            Assert.Equal(expectedMemberAudits, memberAudits);
            Assert.Equal(expectedCapabilityAudits, capabilityAudits);
            if (memberAudits > 0) Assert.False(trustLive, "MemberRevoked requires a committed roster revocation.");
            if (capabilityAudits > 0) Assert.False(grantLive, "CapabilityRevoked requires a committed grant revocation.");
        }

        private static void ReplaceAuditTrail(object owner, IAuthorizedAuditTrail replacement)
        {
            var field = owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(candidate => typeof(IAuthorizedAuditTrail).IsAssignableFrom(candidate.FieldType));
            field.SetValue(owner, replacement);
        }

        internal async Task RestartAsync()
        {
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            _baseAddress = await LocalNodeHostRuntime.StartAsync(
                "ticket-216-kernel-clock", _directory, CancellationToken.None, _clock);
            Services = LocalNodeHostRuntime.CurrentServices
                ?? throw new InvalidOperationException("The restarted host did not expose its service provider.");
            _nodeFactory = Services.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            _searchFactory = Services.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>();
        }

        private async Task AssertRevocationAuditsShareAuthorityAsync()
        {
            var tenant = NodeTenant.Resolve(Services.GetRequiredService<IActiveTeamAccessor>());
            var records = new List<AuditRecord>();
            await foreach (var record in Services.GetRequiredService<IAuditTrail>()
                .QueryAsync(new AuditQuery(tenant)))
            {
                if (record.EventType == AuditEventType.CapabilityRevoked ||
                    record.EventType == AuditEventType.MemberRevoked)
                    records.Add(record);
            }
            var grant = Assert.Single(records, record => record.EventType == AuditEventType.CapabilityRevoked);
            var roster = Assert.Single(records, record => record.EventType == AuditEventType.MemberRevoked);
            Assert.NotNull(grant.AuthoritySnapshot);
            Assert.NotNull(roster.AuthoritySnapshot);
            Assert.Equal(JsonSerializer.Serialize(grant.AuthoritySnapshot),
                JsonSerializer.Serialize(roster.AuthoritySnapshot));
        }

        private async Task AssertTransportHelloRefusedAsync()
        {
            var signer = new KernelEd25519Signer();
            var (listenerPublic, listenerPrivate) = signer.GenerateKeyPair();
            var listenerIdentity = new NodeIdentity(
                "11111111111111111111111111111111", listenerPublic, listenerPrivate);
            var targetIdentity = Assert.IsType<NodeIdentity>(_identityTargetTransportIdentity);
            var projection = Services.GetRequiredService<RosterCrdtProjection>();
            var roster = Services.GetRequiredService<NodeTeamRoster>();

            await using var listenerTransport = new TcpSyncDaemonTransport("tcp://127.0.0.1:0");
            await using var listener = BuildRosterDaemon(
                listenerTransport, listenerIdentity, signer, projection,
                new MemberSetTrustPolicy(() => roster.TrustedTransportKeys()));
            using var refused = new SemaphoreSlim(0, 1);
            listener.FrameReceived += (_, frame) =>
            {
                if (frame.FrameType == GossipFrameType.HandshakeFailure) refused.Release();
            };
            await listener.StartListeningAsync(CancellationToken.None);

            await using var targetTransport = new TcpSyncDaemonTransport();
            await using var target = BuildRosterDaemon(
                targetTransport, targetIdentity, signer, projection,
                new MemberSetTrustPolicy([listenerPublic]));
            target.AddPeer(listenerTransport.ListenEndpoint!, listenerPublic);
            await target.StartAsync(CancellationToken.None);
            Assert.True(await refused.WaitAsync(TimeSpan.FromSeconds(15)),
                "MemberSetTrustPolicy must refuse the revoked member's real loopback TCP HELLO.");
            await target.StopAsync(CancellationToken.None);
            await listener.StopListeningAsync(CancellationToken.None);
        }

        private static GossipDaemon BuildRosterDaemon(
            ISyncDaemonTransport transport,
            NodeIdentity identity,
            IEd25519Signer signer,
            RosterCrdtProjection projection,
            IPeerTrustPolicy trust) =>
            new(
                transport,
                new VectorClock(),
                Options.Create(new GossipDaemonOptions
                {
                    RoundIntervalSeconds = 1,
                    PeerPickCount = 1,
                    ConnectTimeoutSeconds = 5,
                    DeadPeerBackoffSeconds = 2,
                }),
                new InMemoryNodeIdentityProvider(identity),
                signer,
                deltaProducer: projection,
                deltaSink: projection,
                trustPolicy: trust,
                timeProvider: TimeProvider.System);

        private static string RawBase64Url(byte[] value) =>
            Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public async ValueTask DisposeAsync()
        {
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            Environment.SetEnvironmentVariable("HARBORLINE_TEST_INSTALL_FOOTPRINT_ROOT", _priorInstallRoot);
            Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", _priorRootSeed);
            Environment.SetEnvironmentVariable("Logging__EventLog__LogLevel__Default", _priorEventLogLevel);
            Environment.SetEnvironmentVariable("LocalNode__WebClient__Enabled", _priorWebClientEnabled);
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }

    }

    private sealed class BoundaryFaultingAuditTrail(
        IAuthorizedAuditTrail inner,
        AuditEventType faultEventType) : IAuthorizedAuditTrail
    {
        private int _armed = 1;

        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) =>
            inner.AppendAsync(record, ct);

        public ValueTask AppendAuthorizedAsync(
            AuditRecord record,
            AuthorizationDecision decision,
            CancellationToken ct = default,
            SeparationOfDutyDecision? approval = null)
        {
            if (record.EventType == faultEventType && Interlocked.Exchange(ref _armed, 0) == 1)
                throw new InvalidOperationException($"ticket 238 {faultEventType.Value} boundary fault");
            return inner.AppendAuthorizedAsync(record, decision, ct, approval);
        }

        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) =>
            inner.QueryAsync(query, ct);
    }

    internal sealed class RevocationInterleavingBarrier
    {
        private readonly TaskCompletionSource _firstArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrivals;

        internal void Arrive(CancellationToken cancellationToken)
        {
            SignalArrival();
            if (!_release.Task.Wait(TimeSpan.FromSeconds(10), cancellationToken))
                throw new TimeoutException("The concurrent revocation interleaving was not released.");
        }

        internal async Task ArriveAsync(CancellationToken cancellationToken)
        {
            SignalArrival();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        internal async Task ReleaseAfterInterleavingAsync()
        {
            await _firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAny(_secondArrived.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            _release.TrySetResult();
        }

        private void SignalArrival()
        {
            var arrival = Interlocked.Increment(ref _arrivals);
            if (arrival == 1) _firstArrived.TrySetResult();
            if (arrival == 2) _secondArrived.TrySetResult();
        }
    }

    private sealed class BarrierSigner(
        IOperationSigner inner,
        RevocationInterleavingBarrier barrier) : IOperationSigner
    {
        public PrincipalId IssuerId => inner.IssuerId;

        public ValueTask<SignedOperation<T>> SignAsync<T>(
            T payload,
            DateTimeOffset issuedAt,
            Guid nonce,
            CancellationToken ct = default)
        {
            if (payload is RevocationRecord) barrier.Arrive(ct);
            return inner.SignAsync(payload, issuedAt, nonce, ct);
        }
    }

    private sealed class BarrierProjection(
        IRosterRevocationProjection inner,
        RevocationInterleavingBarrier barrier,
        string interleaving) : IRosterRevocationProjection
    {
        public async Task PublishLocalAsync(
            RosterRecordCrdtState record,
            CancellationToken cancellationToken)
        {
            if (interleaving == "projection") await barrier.ArriveAsync(cancellationToken);
            await inner.PublishLocalAsync(record, cancellationToken);
            if (interleaving == "adoption") await barrier.ArriveAsync(cancellationToken);
        }

        public IReadOnlyList<RosterRecordCrdtState> Snapshot() => inner.Snapshot();
    }

    private sealed class BarrierAuditTrail(
        IAuthorizedAuditTrail inner,
        RevocationInterleavingBarrier barrier,
        string interleaving) : IAuthorizedAuditTrail
    {
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) =>
            inner.AppendAsync(record, ct);

        public async ValueTask AppendAuthorizedAsync(
            AuditRecord record,
            AuthorizationDecision decision,
            CancellationToken ct = default,
            SeparationOfDutyDecision? approval = null)
        {
            if (interleaving == "audit-append" && record.EventType == AuditEventType.MemberRevoked)
                await barrier.ArriveAsync(ct);
            await inner.AppendAuthorizedAsync(record, decision, ct, approval);
        }

        public async IAsyncEnumerable<AuditRecord> QueryAsync(
            AuditQuery query,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (interleaving == "audit-query" && query.EventType == AuditEventType.MemberRevoked)
                await barrier.ArriveAsync(ct);
            await foreach (var record in inner.QueryAsync(query, ct)) yield return record;
        }
    }

    private sealed class MutableHostClock(DateTimeOffset now) : TimeProvider
    {
        private const string ActBaggageKey = "harborline.api.kernel-clock-act";
        private DateTimeOffset _admittedAt = now;
        private string? _actToken;
        private bool _advanceAfterRead;
        private int _actReadCount;
        public int ActReadCount => Volatile.Read(ref _actReadCount);

        public override DateTimeOffset GetUtcNow()
        {
            var token = Activity.Current?.GetBaggageItem(ActBaggageKey);
            if (!_advanceAfterRead || !string.Equals(token, Volatile.Read(ref _actToken), StringComparison.Ordinal))
                return _admittedAt;

            return Interlocked.Increment(ref _actReadCount) == 1
                ? _admittedAt
                : _admittedAt.AddDays(1);
        }

        public void ArmAdvancingBoundary(DateTimeOffset admittedAt)
        {
            _admittedAt = admittedAt;
            Volatile.Write(ref _actToken, null);
            Interlocked.Exchange(ref _actReadCount, 0);
            _advanceAfterRead = true;
        }

        public Activity BeginAct()
        {
            var token = Guid.NewGuid().ToString("N");
            var activity = new Activity("ticket-216-kernel-clock-act")
                .SetIdFormat(ActivityIdFormat.W3C)
                .AddBaggage(ActBaggageKey, token)
                .Start();
            Volatile.Write(ref _actToken, token);
            return activity;
        }
    }

    private sealed class PassingAntiforgeryPolicy : IWebAntiforgeryPolicy
    {
        public Task<bool> IssueAnonymousAsync(Microsoft.AspNetCore.Http.HttpContext context) => Task.FromResult(true);
        public Task<bool> ConsumeAnonymousAsync(Microsoft.AspNetCore.Http.HttpContext context) => Task.FromResult(true);
        public Task<bool> ConsumeChallengeAsync(Microsoft.AspNetCore.Http.HttpContext context, string challengeHandle) => Task.FromResult(true);
        public Task<bool> ConsumeSelectedAsync(Microsoft.AspNetCore.Http.HttpContext context, string selectedHandle) => Task.FromResult(true);
        public Task<bool> ConsumeInstallationAsync(Microsoft.AspNetCore.Http.HttpContext context, string installationHandle) => Task.FromResult(true);
        public Task<bool> RotateChallengeAsync(Microsoft.AspNetCore.Http.HttpContext context, string challengeHandle) => Task.FromResult(true);
        public Task<bool> RotateSelectedAsync(Microsoft.AspNetCore.Http.HttpContext context, string selectedHandle) => Task.FromResult(true);
        public void EmitToken(Microsoft.AspNetCore.Http.HttpResponse response, string token) { }
        public void ExpireAnonymousBinding(Microsoft.AspNetCore.Http.HttpResponse response) { }
    }

}
