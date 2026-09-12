using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.AccessGrant.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.FinancialAr.Models;
using Harborline.Api.Blocks.FinancialAr.Services;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.People.Foundation.Models;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Data.People;
using Harborline.Api.LocalNodeHost.Data.Scheduling;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Card #3356 — a hosted-web request must not be authorized as the DESKTOP OPERATOR.
/// </summary>
/// <remarks>
/// <para>
/// <b>The finding (ADR 0160 R3-D).</b> The <c>/packs/*</c>, <c>/scheduling/*</c>, feed and workshop routes
/// take <see cref="IAuthorizationContext"/> as a <c>Map(...)</c> parameter closed over from the OUTER
/// generic host (bug-2849 — the inner <c>WebApplication</c> container cannot resolve those dependencies).
/// That singleton is <see cref="ActiveTeamAuthorizationContext"/>, which resolves the OS OPERATOR's
/// active-team permission set. The inner <see cref="SelectedSessionTenantContext"/> the listener binds per
/// request is read by nobody. So a request carrying a member's selected-session cookie was evaluated
/// against the operator's grants — R3-D's "a hosted-web request cannot consume
/// <c>IActiveTeamAccessor</c>" violated at the authorization seam.
/// </para>
/// <para>
/// <b>What is REAL here.</b> The container topology is production's: the authorization slice is composed
/// by the production <see cref="NodeFinancialPostingComposition.AddNodeFinancialPosting"/> on an OUTER
/// container with no HTTP pipeline, while requests are served by a real <see cref="SharedHostedWebApp"/>
/// and admitted by its real listener caller-auth middleware, whose Accept-2 branch binds a real
/// <see cref="SelectedSessionRequestPrincipal"/>. The route is the real
/// <see cref="PackInstallRoutes.Map"/>, closed over the <see cref="IAuthorizationContext"/> resolved from
/// the outer container exactly as <see cref="HostedPackInstallApiEndpoint"/> does. The pack is really
/// exported and really signed, so an authorized preview really succeeds.
/// </para>
/// <para>
/// <b>What is substituted, and why it is not the seam under test.</b> Only
/// <see cref="IWebSelectedSessionPrincipalAuthority"/> — the handle→principal materializer UPSTREAM of the
/// authorization seam. Its real implementation (fence, revalidation, grant pins) is covered by
/// <c>SelectedSessionRequestPrincipalTests</c> and <c>Mtw2TwoUserAcceptanceE2E</c>; re-driving it here
/// would add a second identity substrate without touching the seam this card is about — the same
/// substitution, for the same reason, as <see cref="NodeServingPipelineAttributionTests"/>.
/// </para>
/// <para>
/// <b>The mutation-proof teeth.</b> (1) <see cref="WebRequest_IsNotAuthorizedByTheOperatorsGrants"/> removes
/// the OPERATOR's own <c>packages:operate</c> holding — since ticket 205 slice 3 that is the seeded
/// node-operator access grant the pack routes read at the gate, revoked through the real grant store, with
/// the membership role flipped alongside it for the route families that still read the flat set — and
/// asserts the MEMBER's request outcome does not move, while the OPERATOR's does. On unfenced main the
/// member's moves from 403 to 200, which is the confused deputy stated as an experiment. (2)
/// <see cref="DesktopPlaneRequest_StillConsultsTheOperatorsGrants"/> asserts the desktop plane still
/// tracks that same removal, so a blanket denial cannot pass tooth 1.
/// </para>
/// <para>
/// <b>Scope (MTW-2, not MTW-3).</b> Refusal is the whole fix. This asserts the web plane is CLOSED, never
/// that a member's real permissions resolve — CIC 2026-07-29 put permission resolution in MTW-3.
/// </para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3356")]
[Collection("Harborline process environment")]
public sealed class WebPlaneAuthorizationFenceTests
{
    private const string CallerToken = "web-plane-authz-fence-caller-token";
    private const string MemberHandle = "handle-member-with-at-least-256-bits-of-fence-entropy-00000000";
    private const string MemberParty = "party-web-plane-fence-member";

    private static readonly TeamId OperatorTeam = new(Guid.Parse("33560000-0000-0000-0000-0000000000fe"));
    private static readonly TenantId OperatorTenant = ActiveTeamTenantContext.ProjectTenantId(OperatorTeam);

    [Fact(DisplayName =
        "3356: a selected-session web request is NOT authorized by the desktop operator's grants — " +
        "grant evidence replaces the registry-only precondition; revocation does not move the member's outcome")]
    public async Task WebRequest_IsNotAuthorizedByTheOperatorsGrants()
    {
        await using var fixture = await Fixture.CreateAsync();

        // The OS operator holds packages:operate — through the seeded node-operator grant the pack routes
        // read at the gate (ticket 205 slice 3). The registry-only desktop context is not
        // an authority source in this composition. The signed-in member holds no grant.
        await fixture.SetOperatorRoleAsync(TeamRole.Admin);
        await fixture.AssertOperatorGrantDecisionAsync(true);
        Assert.Equal(
            HttpStatusCode.OK,
            (await fixture.PreviewAsDesktopOperatorAsync()).StatusCode);

        using var granted = await fixture.PreviewAsMemberAsync();
        await AssertRefusedByAuthorizationAsync(granted);

        // Now REMOVE the operator's holding, on the substrate the pack routes actually read. If the member
        // were being evaluated as the operator, this is the flip that would change their answer. It must
        // not — and it must genuinely move the OPERATOR's own answer, or the lever proves nothing.
        await fixture.SetOperatorRoleAsync(TeamRole.Member);
        await fixture.RevokeNodeOperatorGrantAsync();
        await fixture.AssertOperatorGrantDecisionAsync(false);
        await AssertRefusedByAuthorizationAsync(await fixture.PreviewAsDesktopOperatorAsync());

        using var revoked = await fixture.PreviewAsMemberAsync();
        await AssertRefusedByAuthorizationAsync(revoked);
    }

    [Fact(DisplayName =
        "3356: a DESKTOP-plane request still consults the operator's grants — the fence is not a blanket denial")]
    public async Task DesktopPlaneRequest_StillConsultsTheOperatorsGrants()
    {
        await using var fixture = await Fixture.CreateAsync();

        // No selected-session cookie: the bootstrap-token desktop plane. It is the operator's own call, so
        // it is authorized by the operator's own grants — that is the two-plane design, not a bug. The
        // holding is the seeded node-operator grant the pack routes now read at the gate, not the flat
        // membership role (ticket 205 slice 3); the role flip below no longer moves this outcome, and the
        // grant revocation is the lever that does.
        await fixture.SetOperatorRoleAsync(TeamRole.Admin);
        using var authorized = await fixture.PreviewAsDesktopOperatorAsync();
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);

        // And it still REFUSES when the operator genuinely lacks the permission — so a fence implemented as
        // an unconditional denial cannot satisfy this pair.
        await fixture.RevokeNodeOperatorGrantAsync();
        using var denied = await fixture.PreviewAsDesktopOperatorAsync();
        await AssertRefusedByAuthorizationAsync(denied);
    }

    [Fact(DisplayName =
        "3356: a gated READ is fenced too — pins the ambient harborline's breadth, so narrowing it to " +
        "mutations turns this red rather than silently unfencing every gated GET")]
    public async Task GatedRead_IsFencedToo_PinningTheAmbientScopesBreadth()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetOperatorRoleAsync(TeamRole.Admin);

        // WHY THIS TEST EXISTS, and why it is not redundant with the POST tests above.
        //
        // The fence's plane signal is the ambient scope SharedHostedWebApp's selected-session branch
        // opens — an attribution scope whose FIRST consumer is the audit envelope. Its second consumer is now an
        // authorization control, and nothing in the audit story requires the scope to cover reads. So a
        // well-intentioned narrowing ("audit only records mutations, skip GET") leaves both POST tests
        // green while unfencing every gated read route on the node. This test is what makes that edit
        // fail instead of ship. It belongs to the attribution scope's breadth, not to this route.
        using var memberRead = await fixture.ListInstalledAsMemberAsync();
        await AssertRefusedByAuthorizationAsync(memberRead);

        // And the same read still succeeds on the desktop plane — so the read fence, like the write
        // fence, cannot be satisfied by refusing everyone.
        using var desktopRead = await fixture.ListInstalledAsDesktopOperatorAsync();
        Assert.Equal(HttpStatusCode.OK, desktopRead.StatusCode);
    }

    [Fact(DisplayName =
        "3356: contacts, invoices, and scheduling routes all refuse a selected web member before handlers")]
    public async Task ChangedRouteFamilies_AreFenced_FromDesktopOperatorGrants()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SetOperatorRoleAsync(TeamRole.Admin);

        using var contacts = await fixture.ListContactsAsMemberAsync();
        await AssertPermissionRefusedAsync(contacts, Permission.ContactsRead);

        using var invoices = await fixture.ListInvoicesAsMemberAsync();
        await AssertPermissionRefusedAsync(invoices, TeamRolePermissions.RecordsRead);

        using var scheduling = await fixture.ListSchedulingDefinitionsAsMemberAsync();
        await AssertPermissionRefusedAsync(scheduling, Permission.SchedulingRead);
    }

    private static async Task AssertRefusedByAuthorizationAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("pack.authz.denied", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(Permission.PackagesOperate, doc.RootElement.GetProperty("permission").GetString());
    }

    private static async Task AssertPermissionRefusedAsync(HttpResponseMessage response, string permission)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("authorization.permission_required", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(permission, doc.RootElement.GetProperty("permission").GetString());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _outerProvider;
        private readonly SharedHostedWebApp _app;
        private readonly IMutableTeamRegistry _memberships;
        private readonly KeyPair _packKey;
        private readonly byte[] _signedPack;
        private readonly HttpClient _client;
        private readonly string _localDatabasePath;
        private readonly string _identityDatabasePath;
        private readonly string _schedulingDatabasePath;

        private Fixture(
            ServiceProvider outerProvider,
            SharedHostedWebApp app,
            IMutableTeamRegistry memberships,
            KeyPair packKey,
            byte[] signedPack,
            HttpClient client,
            Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamAuthorizationContext operatorAuthorization,
            string localDatabasePath,
            string identityDatabasePath,
            string schedulingDatabasePath)
        {
            _outerProvider = outerProvider;
            _app = app;
            _memberships = memberships;
            _packKey = packKey;
            _signedPack = signedPack;
            _client = client;
            _localDatabasePath = localDatabasePath;
            _identityDatabasePath = identityDatabasePath;
            _schedulingDatabasePath = schedulingDatabasePath;
            OperatorAuthorization = operatorAuthorization;
        }

        /// <summary>
        /// The node's own DESKTOP-plane authority as PRODUCTION composes it. Exposed so the test can state
        /// its preconditions against the real resolver rather than assuming a composition.
        /// </summary>
        internal Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamAuthorizationContext OperatorAuthorization { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var activeTeam = new FixedActiveTeamAccessor(
                new TeamContext(OperatorTeam, "Operator Team", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));

            // The REAL membership store, with the OS operator enrolled on the active team exactly as
            // MultiTeamBootstrapHostedService.EnrollOperatorAsync does on a single-office node.
            var memberships = new InMemoryTeamRegistry();
            await memberships.AddMembershipAsync(
                ActiveTeamAuthorizationContext.NodeOperator,
                new TeamMembership(
                    OperatorTeam.Value,
                    "Operator Team",
                    TeamRolePermissions.DisplayName(TeamRole.Admin),
                    KeyFingerprint.FromPublicKey(OperatorTeam.Value.ToByteArray()),
                    TeamRole.Admin));

            // ── The OUTER container: the production authorization composition, on a container with no HTTP
            //    pipeline — the geometry Program.cs builds via Host.CreateApplicationBuilder.
            var outer = new ServiceCollection();
            outer.AddTestKernelClock();
            outer.AddLogging(b => b.ClearProviders());
            outer.AddSingleton<IActiveTeamAccessor>(activeTeam);
            outer.AddSingleton<IMutableTeamRegistry>(memberships);
            outer.AddSingleton<ITeamRegistry>(memberships);
            var localDatabasePath = Path.Combine(
                Path.GetTempPath(), $"web-plane-fence-local-{Guid.NewGuid():N}.db");
            var identityDatabasePath = Path.Combine(
                Path.GetTempPath(), $"web-plane-fence-identity-{Guid.NewGuid():N}.db");
            var schedulingDatabasePath = Path.Combine(
                Path.GetTempPath(), $"web-plane-fence-scheduling-{Guid.NewGuid():N}.db");
            outer.AddSingleton<Harborline.Api.Foundation.Persistence.IHarborlineEntityModule,
                Harborline.Api.Blocks.People.Foundation.Data.PeopleEntityModule>();
            outer.AddSingleton<Harborline.Api.Foundation.Persistence.IHarborlineEntityModule,
                Harborline.Api.Blocks.FinancialAr.Data.ArEntityModule>();
            outer.AddSingleton<Harborline.Api.Foundation.Persistence.IHarborlineEntityModule,
                Harborline.Api.Blocks.FinancialLedger.Data.FinancialLedgerEntityModule>();
            outer.AddSingleton<Harborline.Api.Foundation.Persistence.IHarborlineEntityModule,
                Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
            outer.AddDbContextFactory<Harborline.Api.LocalNodeHost.Data.LocalNodeDbContext>(options =>
                options.UseSqlite($"Data Source={localDatabasePath};Pooling=False"));
            outer.AddDbContextFactory<NodeLocalInstallationIdentityDbContext>(options =>
                options.UseSqlite($"Data Source={identityDatabasePath};Pooling=False"));
            outer.AddDbContextFactory<NodeLocalSchedulingDbContext>(options =>
                options.UseSqlite($"Data Source={schedulingDatabasePath};Pooling=False"));
            outer.AddNodeContacts();
            outer.AddNodeFinancialPosting();

            // Listener prerequisites the shared app resolves from the outer container.
            outer.AddSingleton(new NodeCallerSessionToken(CallerToken));
            outer.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new FixedSelectedSessionAuthority());

            // The pack routes resolve at the gate now, so the outer container carries the production
            // authorization module — the same registration Program.cs makes.
            outer.AddAccessGrantModule();

            var outerProvider = outer.BuildServiceProvider();
            // Ticket 205 slice 5: the outer container no longer registers IAuthorizationContext — the
            // web-plane decorator over it went with the last consumer. The DESKTOP-plane authority this
            // fixture states its preconditions against is the active-team context itself, which the same
            // production composition still registers.
            var authz = outerProvider.GetRequiredService<Harborline.Api.LocalNodeHost.Data.Financial.ActiveTeamAuthorizationContext>();

            // ...seeded the way AuthorizationSeedHostedService seeds it at boot, so the desktop operator's
            // packages:operate holding is the real node-operator grant and not a fixture stub.
            await outerProvider.GetRequiredService<AccessGrantAuthorizationSeed>()
                .InstallAsync(OperatorTenant, TimeProvider.System.GetUtcNow(), AuthorizationSeedProfile.Production);

            await using (var localDb = await outerProvider
                .GetRequiredService<IDbContextFactory<Harborline.Api.LocalNodeHost.Data.LocalNodeDbContext>>()
                .CreateDbContextAsync())
                await localDb.Database.EnsureCreatedAsync();
            await using (var identityDb = await outerProvider
                .GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>()
                .CreateDbContextAsync())
                await identityDb.Database.EnsureCreatedAsync();
            await InstallationIdentityTestBootstrap.BootstrapAsync(
                outerProvider.GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>());
            await using (var schedulingDb = await outerProvider
                .GetRequiredService<IDbContextFactory<NodeLocalSchedulingDbContext>>()
                .CreateDbContextAsync())
                await schedulingDb.Database.MigrateAsync();

            // ── The pack surface: a REALLY exported, REALLY signed pack, so an authorized preview really
            //    succeeds and "not 403" is not standing in for "the route blew up".
            var packKey = KeyPair.Generate();
            var signer = new Ed25519Signer(packKey);
            var codec = new PackFileCodec();
            var exporter = new PackExporter(
                new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
            var verifier = new PackVerifier(new Ed25519Verifier(), codec);
            var trustStore = new InMemoryPackTrustStore(new[]
            {
                new PackTrustRoot(
                    TrustScope.OwnRoster, packKey.PrincipalId,
                    PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
            });
            var store = new InMemoryPackInstallStore();
            var installer = new PackInstaller(
                verifier, store, new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
                new InMemoryPackInstallAudit(),
                Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
            var registryServices = new ServiceCollection()
                .AddLogging(b => b.ClearProviders()).AddInMemoryAssetTypeSystem().BuildServiceProvider();
            var projector = new PackSeedProjector(
                store, registryServices.GetRequiredService<IEntityTypeRegistry>(),
                NullLogger<PackSeedProjector>.Instance, time: TimeProvider.System);

            // ── The INNER serving app: the real production listener.
            var app = new SharedHostedWebApp(
                outerProvider,
                Options.Create(new LocalNodeOptions { HealthPort = 7309 }),
                new LocalNodeExecutableEndpointRegistry(),
                outerProvider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
                outerProvider.GetRequiredService<TimeProvider>());

            // The real route, closed over the outer container's IAuthorizationContext — the exact call
            // HostedPackInstallApiEndpoint.StartAsync makes.
            app.MapApiRoutes(routes => PackInstallRoutes.Map(
                routes, installer, store, trustStore, PackRevocationList.Empty, activeTeam,
                outerProvider.GetRequiredService<AuthorizationGate>(),
                TimeProvider.System, NullLogger.Instance,
                authorizingPrincipal: packKey.PrincipalId.ToBase64Url(), projector: projector));

            var localFactory = outerProvider
                .GetRequiredService<IDbContextFactory<Harborline.Api.LocalNodeHost.Data.LocalNodeDbContext>>();
            var invoiceRepository = new NodeEfInvoiceRepository(localFactory);
            var invoiceNumbering = new InMemoryInvoiceNumberingService(new ReplicaId("FENCE"));
            var schedulingStore = new NodeSchedulingDraftStore(
                outerProvider.GetRequiredService<IDbContextFactory<NodeLocalSchedulingDbContext>>(),
                TimeProvider.System);
            app.MapApiRoutes(routes =>
            {
                ContactRoutes.Map(
                    routes.MapDeviceReachableProductDataGroup(),
                    outerProvider.GetRequiredService<NodeEfPartyRepository>(),
                    outerProvider.GetRequiredService<ContactCrdtProjection>(),
                    activeTeam,
                    outerProvider.GetRequiredService<IDbContextFactory<NodeLocalInstallationIdentityDbContext>>(),
                    TimeProvider.System);
                InvoiceRoutes.Map(
                    routes.MapDeviceReachableProductDataGroup(),
                    invoiceRepository,
                    invoiceNumbering,
                    new StubInvoicePostingService(),
                    activeTeam, timeProvider: TimeProvider.System);
                SchedulingDefinitionRoutes.Map(
                    routes.MapDeviceReachableProductDataGroup(),
                    schedulingStore,
                    new SchedulingDraftValidator(),
                    outerProvider.GetRequiredService<NodeEfPartyRepository>(),
                    activeTeam,
                    outerProvider.GetRequiredService<ICurrentUser>(),
                    bookingService: null!,
                    eventStore: null!,
                    calendarStore: null!,
                    availabilityStore: null!,
                    freeBusyService: null!,
                    TimeProvider.System);
            });

            var signedPack = await ExportSignedPackAsync(exporter, signer);
            var seed = new InstalledPack(
                "surface.seed",
                "1.0.0",
                PackScopeTier.Vertical,
                PackLifecycleState.Draft,
                Array.Empty<PackSeedItem>(),
                new Dictionary<string, int>(),
                TimeProvider.System.GetUtcNow(),
                packKey.PrincipalId,
                PackComposerRoutes.OwnRosterEpoch,
                TrustScope.OwnRoster,
                Array.Empty<PackDependencyRef>());
            store.Commit(new PackInstallTransaction(
                OperatorTenant,
                seed,
                new PackInstallWatermark(seed.PackKey, seed.Version, new Dictionary<string, int>()),
                Array.Empty<PackTenantOverride>()));

            await app.StartAsync(CancellationToken.None);
            var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
            return new Fixture(
                outerProvider,
                app,
                memberships,
                packKey,
                signedPack,
                client,
                authz,
                localDatabasePath,
                identityDatabasePath,
                schedulingDatabasePath);
        }

        /// <summary>
        /// Revoke the desktop operator's seeded node-operator grant — the operator's holding lever on the
        /// gate's substrate, now that the pack routes read the grant closure rather than the flat
        /// membership set. The seed never resurrects a revoked grant, so this is a one-way lever.
        /// </summary>
        internal async Task RevokeNodeOperatorGrantAsync()
        {
            var grants = _outerProvider.GetRequiredService<IGrantStore>();
            var grant = await grants.FindBySourceReferenceAsync(
                OperatorTenant, AccessGrantAuthorizationSeed.NodeOperatorGrantSource);
            Assert.NotNull(grant);
            Assert.NotNull(await grants.RevokeAsync(
                OperatorTenant,
                grant.GrantId,
                new GrantRevocation(
                    new ActorId("fence-test"),
                    TimeProvider.System.GetUtcNow(),
                    new GrantReason(
                        GrantReasonCodes.RevocationReview,
                        "the desktop operator genuinely loses packages:operate"))));
        }

        internal async Task AssertOperatorGrantDecisionAsync(bool allowed)
        {
            var decision = await _outerProvider.GetRequiredService<AuthorizationGate>().DecideAsync(
                new AuthorizationWriteContext(ActiveTeamAuthorizationContext.NodeOperator, OperatorTenant,
                    TimeProvider.System.GetUtcNow()).Request(
                        AuthorizationOperation.Parse(Permission.PackagesOperate), "pack", "fence"));
            Assert.Equal(allowed, decision.Verdict == AuthorizationVerdict.Allowed);
            var grant = await _outerProvider.GetRequiredService<IGrantStore>().FindBySourceReferenceAsync(
                OperatorTenant, AccessGrantAuthorizationSeed.NodeOperatorGrantSource);
            Assert.NotNull(grant);
            var facts = decision.Evidence.Project().SelectMany(step => step.Facts).ToArray();
            if (allowed)
                Assert.Contains(decision.Evidence.Bindings, binding => binding.GrantId == grant.GrantId.ToString());
            else
                Assert.Contains(decision.Evidence.Excluded, binding =>
                    binding.Binding.GrantId == grant.GrantId.ToString()
                    && binding.Reason == AuthorizationExclusionReason.GrantRevoked);
            Assert.Contains($"verdict:{(allowed ? "allowed" : "denied")}", facts);
        }

        /// <summary>Set the OS operator's membership role on the ACTIVE team — the operator's grant lever.</summary>
        internal async Task SetOperatorRoleAsync(TeamRole role) =>
            Assert.True(
                await _memberships.SetRoleAsync(
                    ActiveTeamAuthorizationContext.NodeOperator, OperatorTeam.Value, role),
                "the operator's membership edge must exist for the role flip to mean anything");

        /// <summary>A real request admitted by the listener's Accept-2 selected-session (WEB-plane) branch.</summary>
        internal async Task<HttpResponseMessage> PreviewAsMemberAsync()
        {
            using var request = NewPreviewRequest();
            request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={MemberHandle}");
            return await _client.SendAsync(request);
        }

        /// <summary>A real request admitted by the listener's Accept-1 bootstrap-token (DESKTOP-plane) branch.</summary>
        internal async Task<HttpResponseMessage> PreviewAsDesktopOperatorAsync()
        {
            using var request = NewPreviewRequest();
            request.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);
            return await _client.SendAsync(request);
        }

        /// <summary>A gated READ (<c>packages:operate</c>) on the WEB plane — the probe of the attribution scope across reads.</summary>
        internal async Task<HttpResponseMessage> ListInstalledAsMemberAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, PackInstallRoutes.ListInstalledRoute);
            request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={MemberHandle}");
            return await _client.SendAsync(request);
        }

        /// <summary>The same gated READ on the DESKTOP plane — it must keep working.</summary>
        internal async Task<HttpResponseMessage> ListInstalledAsDesktopOperatorAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, PackInstallRoutes.ListInstalledRoute);
            request.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);
            return await _client.SendAsync(request);
        }

        internal Task<HttpResponseMessage> ListContactsAsMemberAsync() =>
            SendMemberGetAsync(ContactRoutes.RouteBase);

        internal Task<HttpResponseMessage> ListInvoicesAsMemberAsync() =>
            SendMemberGetAsync($"{InvoiceRoutes.RouteBase}?chartId=fence-chart");

        internal Task<HttpResponseMessage> ListSchedulingDefinitionsAsMemberAsync() =>
            SendMemberGetAsync(SchedulingDefinitionRoutes.RouteBase);

        private async Task<HttpResponseMessage> SendMemberGetAsync(string route)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={MemberHandle}");
            return await _client.SendAsync(request);
        }

        private HttpRequestMessage NewPreviewRequest()
        {
            var content = new ByteArrayContent(_signedPack);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return new HttpRequestMessage(HttpMethod.Post, PackInstallRoutes.PreviewRoute)
            {
                Content = content,
            };
        }

        private static async Task<byte[]> ExportSignedPackAsync(PackExporter exporter, Ed25519Signer signer)
        {
            var request = new PackExportRequest(
                Key: "acme.fence",
                Version: "1.0.0",
                Name: "Acme Fence Pack",
                Description: "web-plane authorization fence probe",
                ScopeTier: PackScopeTier.Vertical,
                Contents: new[]
                {
                    new PackContentSource(
                        "intake", PackContentKind.FormDefinition, "1.0.0",
                        System.Text.Json.Nodes.JsonNode.Parse(
                            """{"title":"Intake","assignee":"role:approver"}""")!),
                },
                Dependencies: Array.Empty<PackDependencyRef>(),
                CapabilityRequirements: new[] { "forms.dynamic" },
                Epoch: PackComposerRoutes.OwnRosterEpoch,
                // The same grandfathered `general` profile PackComposerRoutes builds for a client that
                // declares no DCP (ADR 0145 compatibility plan).
                Dcp: DomainComplianceProfile.General(signer.IssuerId.ToBase64Url()));
            var outcome = await exporter.ExportAsync(request, signer, CancellationToken.None);
            Assert.True(
                outcome.Succeeded,
                "the fixture's own pack must export cleanly; validation said: "
                    + string.Join(", ", outcome.Validation.Errors.Select(e => $"{e.Code}@{e.Target}")));
            return outcome.FileBytes!;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            await _outerProvider.DisposeAsync();
            _packKey.Dispose();
            File.Delete(_localDatabasePath);
            File.Delete(_identityDatabasePath);
            File.Delete(_schedulingDatabasePath);
        }
    }

    private sealed class StubInvoicePostingService : IInvoicePostingService
    {
        public Task<IssueResult> IssueAsync(
            InvoiceId invoiceId,
            AuthorizationWriteContext authority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VoidResult> VoidAsync(
            InvoiceId invoiceId,
            string reason,
            AuthorizationWriteContext authority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WriteOffResult> WriteOffAsync(
            InvoiceId invoiceId,
            GLAccountId badDebtAccountId,
            string reason,
            AuthorizationWriteContext authority,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Materializes ONE real <see cref="SelectedSessionRequestPrincipal"/> for the known handle. This is the
    /// only substituted seam and it stands UPSTREAM of the authorization seam under test (class remarks).
    /// </summary>
    private sealed class FixedSelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(selectedHandle, MemberHandle, StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    accountId: "account-web-plane-fence",
                    tenantId: new TenantId(OperatorTeam.Value.ToString("D")),
                    principalUserId: new PrincipalUserId("principal-web-plane-fence"),
                    canonicalParty: new CanonicalPartyReference(MemberParty),
                    membershipId: "membership-web-plane-fence",
                    membershipOwnerVersion: 2,
                    pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-web-plane-fence", 3)],
                    authorizationEpoch: 5,
                    sessionCorrelationId: "session-web-plane-fence",
                    coordinationCorrelationId: "coordination-web-plane-fence")
                : null);
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

}
