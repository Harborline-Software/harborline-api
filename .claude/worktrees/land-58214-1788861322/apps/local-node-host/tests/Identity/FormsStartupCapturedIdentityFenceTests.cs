using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.LocalNodeHost.Data.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Search;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Card #3367 — the forms surface must not act as the DESKTOP OPERATOR on a hosted-web request.
/// </summary>
/// <remarks>
/// <para>
/// <b>The finding, as an experiment rather than a reading.</b> <see cref="HostedFormsApiEndpoint"/>
/// captures the operator's ROLE LIST once at startup and mints a per-request form capability from it.
/// (When this was written it captured the subject and definition owner too; #3437 / issue #3378 made
/// those two per-request. The ROLE is what remains captured, and the role is what decides the outcome
/// below — so the experiment's conclusion survives that change and its premise had to be narrowed.) Nothing in that path resolves an
/// <see cref="IAuthorizationContext"/>, so the web-plane fence card #3356 installed at that seam
/// (<see cref="WebPlaneFencedAuthorizationContext"/>) cannot reach it. Measured on
/// <c>origin/main</c> WITH #3356 already merged: a signed-in member's
/// <c>POST /api/local-node/forms/{id}/submit</c> returned <c>201 Created</c> when the OS operator
/// booted <see cref="TeamRole.Admin"/> and <c>403 capability denied</c> when the same operator booted
/// <see cref="TeamRole.Member"/>. The member's write authority was the operator's, frozen at boot.
/// </para>
/// <para>
/// <b>Why the flip happens at boot and not mid-run.</b> #3356's proving shape flips the operator's
/// membership role while the host runs, because the seam it fences re-resolves the operator's grants on
/// every call. This family cannot be probed that way — it reads the operator's roles once, so a mid-run
/// flip changes nothing. The equivalent lever is the role the operator holds when the endpoint's
/// <c>StartAsync</c> runs, which is why each half of <see cref="MemberOutcome_DoesNotTrackTheOperatorsStartupRole"/>
/// builds its own host. The defect is if anything worse stated this way: the member does not merely
/// borrow the operator's authority, they borrow a stale snapshot of it.
/// </para>
/// <para>
/// <b>What is REAL here.</b> The container topology is production's: the authorization and forms slices
/// are composed by the production <see cref="NodeFinancialPostingComposition.AddNodeFinancialPosting"/>
/// and <see cref="NodeFormsComposition.AddNodeForms"/> on an OUTER container with no HTTP pipeline,
/// while requests are served by a real <see cref="SharedHostedWebApp"/> and admitted by its real
/// listener caller-auth middleware, whose Accept-2 branch binds a real
/// <see cref="SelectedSessionRequestPrincipal"/>. The capture under test is performed by the real
/// <see cref="HostedFormsApiEndpoint"/>, driven through its real <c>StartAsync</c> — the fixture does
/// not restate it. The form definition is really registered and really published, so an authorized
/// submit really commits.
/// </para>
/// <para>
/// <b>What is substituted, and why it is not the seam under test.</b> Only
/// <see cref="IWebSelectedSessionPrincipalAuthority"/> — the handle→principal materializer UPSTREAM of
/// the route. Its real implementation is covered by <see cref="SelectedSessionRequestPrincipalTests"/>
/// and <c>Mtw2TwoUserAcceptanceE2E</c>; the same substitution, for the same reason, as
/// <see cref="NodeServingPipelineAttributionTests"/> and <see cref="WebPlaneAuthorizationFenceTests"/>.
/// </para>
/// <para>
/// <b>The mutation-proof teeth.</b> (1) <see cref="MemberOutcome_DoesNotTrackTheOperatorsStartupRole"/>
/// asserts the member is refused BY THE FENCE under both operator boot roles — asserting only "403"
/// would pass for the wrong reason in the Member half, which is already a 403 today
/// (<c>bug-20260729-cabe04e3</c>). (2)
/// <see cref="DesktopPlaneSubmit_StillTracksTheOperatorsGrants"/> asserts the desktop plane still
/// commits under Admin and is still denied under Member, so a blanket refusal cannot satisfy tooth 1.
/// (3) <see cref="GatedRead_IsFencedToo"/> drives a GET, so narrowing the fence to mutations fails
/// rather than ships. (4) <see cref="DefinitionAuthoringFamily_IsFencedToo"/> drives the OTHER route
/// family the same endpoint maps, so fencing one <c>Map</c> call and not the other fails too.
/// </para>
/// <para>
/// <b>Scope (MTW-2, not MTW-3).</b> Refusal is the whole fix. Nothing here asserts that a member's real
/// permissions resolve — CIC 2026-07-29 put permission resolution in MTW-3.
/// </para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3367")]
[Collection("Harborline process environment")]
public sealed class FormsStartupCapturedIdentityFenceTests
{
    private const string CallerToken = "forms-startup-identity-fence-caller-token";
    private const string MemberHandle = "handle-member-with-at-least-256-bits-of-forms-entropy-0000";
    private const string MemberParty = "party-forms-startup-identity-member";
    private const string FormId = "startup.identity.fence.v1";

    private static readonly TeamId OperatorTeam = new(Guid.Parse("33670000-0000-0000-0000-0000000000fe"));

    [Fact(DisplayName =
        "3367: a member's forms outcome does NOT track the role the operator held at boot — the same " +
        "fence refusal either way; signed roster evidence replaces the registry-only boot premise")]
    public async Task MemberOutcome_DoesNotTrackTheOperatorsStartupRole()
    {
        await using (var operatorBootedAdmin = await Fixture.CreateAsync(TeamRole.Admin))
        {
            Assert.Contains(
                "Admin",
                operatorBootedAdmin.CapturedOperatorRoles);

            using var response = await operatorBootedAdmin.SubmitAsMemberAsync();
            await AssertRefusedByTheFenceAsync(response);
        }

        await using (var operatorBootedMember = await Fixture.CreateAsync(TeamRole.Member))
        {
            Assert.Contains(
                "Member",
                operatorBootedMember.CapturedOperatorRoles);

            using var response = await operatorBootedMember.SubmitAsMemberAsync();
            await AssertRefusedByTheFenceAsync(response);
        }
    }

    [Fact(DisplayName =
        "3367: a DESKTOP-plane submit still commits under an Admin operator and is still denied under a " +
        "Member operator; signed roster evidence replaces the registry-only boot premise")]
    public async Task DesktopPlaneSubmit_StillTracksTheOperatorsGrants()
    {
        await using (var operatorBootedAdmin = await Fixture.CreateAsync(TeamRole.Admin))
        {
            using var response = await operatorBootedAdmin.SubmitAsDesktopOperatorAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        }

        await using (var operatorBootedMember = await Fixture.CreateAsync(TeamRole.Member))
        {
            using var response = await operatorBootedMember.SubmitAsDesktopOperatorAsync();
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            // The engine's own capability denial, NOT the fence — the desktop plane still reaches the
            // route and is judged on the operator's captured roles.
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"code\":\"forms.capability_denied\"", body, StringComparison.Ordinal);
            Assert.DoesNotContain(WebPlaneUnavailableRouteFence.UnavailableCode, body, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName =
        "3367: a gated READ is fenced too — narrowing the fence (or the route list that feeds it) to " +
        "mutations turns this red rather than silently unfencing every forms GET")]
    public async Task GatedRead_IsFencedToo()
    {
        await using var fixture = await Fixture.CreateAsync(TeamRole.Admin);

        // WHY THIS TEST EXISTS, and why it is not redundant with the submit tests. The plane signal is
        // the ambient scope the listener opens for every selected-session request — a context holder whose
        // FIRST consumer is the audit envelope, and nothing in the audit story requires it to cover
        // reads. A well-intentioned narrowing ("audit only records mutations, skip GET") would leave the
        // submit tests green while unfencing every forms read. Same reasoning as
        // WebPlaneAuthorizationFenceTests.GatedRead_IsFencedToo_PinningTheAmbientScopesBreadth: this consumer
        // needs its own pin, because that test lives on a different route family and would not catch a
        // GET-shaped hole here.
        using var memberRead = await fixture.RenderAsMemberAsync();
        await AssertRefusedByTheFenceAsync(memberRead);

        // And the same read still serves the desktop plane — the fence is not a blanket 403.
        using var desktopRead = await fixture.RenderAsDesktopOperatorAsync();
        Assert.Equal(HttpStatusCode.OK, desktopRead.StatusCode);
    }

    [Fact(DisplayName =
        "3367: the definition-AUTHORING family is fenced too — the fence belongs to the endpoint's route " +
        "group, so fencing one Map call and not the other fails here")]
    public async Task DefinitionAuthoringFamily_IsFencedToo()
    {
        await using var fixture = await Fixture.CreateAsync(TeamRole.Admin);

        // FormDefinitionRoutes mints nothing from IAuthorizationContext either. It no longer closes over
        // a startup owner -- #3437 made the owner per-request -- but the ROLE it authorizes against is
        // still the operator's boot-time role, so a member authoring here is judged by someone else's
        // authority. Correct attribution, wrong authorization: that is the gap this fence covers.
        using var memberList = await fixture.ListDefinitionsAsMemberAsync();
        await AssertRefusedByTheFenceAsync(memberList);

        using var desktopList = await fixture.ListDefinitionsAsDesktopOperatorAsync();
        Assert.Equal(HttpStatusCode.OK, desktopList.StatusCode);
    }

    private static async Task AssertRefusedByTheFenceAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(
            WebPlaneUnavailableRouteFence.UnavailableCode,
            doc.RootElement.GetProperty("code").GetString());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _outerProvider;
        private readonly SearchTestStore _grantStore;
        private readonly SharedHostedWebApp _app;
        private readonly HttpClient _client;

        private Fixture(
            ServiceProvider outerProvider,
            SharedHostedWebApp app,
            HttpClient client,
            IReadOnlyList<string> capturedOperatorRoles,
            SearchTestStore grantStore)
        {
            _outerProvider = outerProvider;
            _grantStore = grantStore;
            _app = app;
            _client = client;
            CapturedOperatorRoles = capturedOperatorRoles;
        }

        /// <summary>
        /// The operator role list as production's <see cref="ICurrentUser"/> resolved it at the moment
        /// the hosted endpoint captured it. Exposed so each half of the experiment states its
        /// precondition against the real resolver rather than assuming a composition.
        /// </summary>
        internal IReadOnlyList<string> CapturedOperatorRoles { get; }

        internal static async Task<Fixture> CreateAsync(TeamRole operatorRoleAtStartup)
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
                    TeamRolePermissions.DisplayName(operatorRoleAtStartup),
                    KeyFingerprint.FromPublicKey(OperatorTeam.Value.ToByteArray()),
                    operatorRoleAtStartup));

            // ── The OUTER container: the production compositions, on a container with no HTTP pipeline
            //    — the geometry Program.cs builds via Host.CreateApplicationBuilder.
            var outer = new ServiceCollection();
            outer.AddLogging(b => b.ClearProviders());
            outer.AddTestKernelClock();
            outer.AddSingleton<IActiveTeamAccessor>(activeTeam);
            outer.AddSingleton<IMutableTeamRegistry>(memberships);
            outer.AddSingleton<ITeamRegistry>(memberships);
            outer.AddNodeFinancialPosting();

            // The field encryptor the recovery coordinator supplies in the live host (INV-S3).
            outer.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
                Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
            outer.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
                Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
            var grantStore = await SearchTestStore.CreateAsync();
            outer.AddSingleton(grantStore.Factory);
            outer.AddNodeAuthorizationModel();
            outer.AddTestNodeForms();
            outer.AddSingleton(sp => SignedOperatorRoster(
                sp.GetRequiredService<IOperationSigner>(), operatorRoleAtStartup));

            // Listener prerequisites the shared app resolves from the outer container.
            outer.AddSingleton(new NodeCallerSessionToken(CallerToken));
            outer.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new FixedSelectedSessionAuthority());

            var outerProvider = outer.BuildServiceProvider();

            // Both boot roles can reach the engine's gate; the form capability's Admin
            // role requirement remains the independent allow/refuse lever under test.
            await DesktopGrantSourceTests.SeedAsync(outerProvider,
                ActiveTeamTenantContext.ProjectTenantId(OperatorTeam), TimeProvider.System.GetUtcNow(), Permission.FormsAuthor);
            await SeedFormDefinitionAsync(outerProvider);

            // ── The INNER serving app: the real production listener.
            var app = new SharedHostedWebApp(
                outerProvider,
                Options.Create(new LocalNodeOptions { HealthPort = 7309 }),
                new LocalNodeExecutableEndpointRegistry(),
                outerProvider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
                outerProvider.GetRequiredService<TimeProvider>());

            // The REAL hosted endpoint. Its StartAsync IS the startup capture under test — the fixture
            // never restates the subject/roles/owner derivation, so a change to it is visible here.
            var endpoint = new HostedFormsApiEndpoint(
                app,
                outerProvider.GetRequiredService<IFormEngine>(),
                outerProvider.GetRequiredService<IFormCapabilityIssuer>(),
                outerProvider.GetRequiredService<IFormCapabilityVerifier>(),
                activeTeam,
                outerProvider.GetRequiredService<AuthorizedFormDefinitionLifecycle>(),
                outerProvider.GetRequiredService<ISchemaRegistry>(),
                outerProvider.GetRequiredService<ILogger<HostedFormsApiEndpoint>>(),
                outerProvider.GetRequiredService<ICurrentUser>(),
                TimeProvider.System);
            await endpoint.StartAsync(CancellationToken.None);

            using var capture = new RosterDecisionCapture();
            var capturedOperatorRoles = outerProvider.GetRequiredService<ICurrentUser>().Roles;
            var evidence = capture.AssertSingle(true);
            Assert.True(evidence.Roster!.Member);
            Assert.True(evidence.Roster.RegistryMember);
            Assert.Equal(PermissionCompositions.ForRole(operatorRoleAtStartup).Permissions.Order(),
                evidence.Roster.Permissions!.Permissions.Order());

            await app.StartAsync(CancellationToken.None);
            var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };

            return new Fixture(outerProvider, app, client, capturedOperatorRoles, grantStore);
        }

        /// <summary>
        /// Registers + publishes one first-party definition whose only section is gated on the ADMIN
        /// display role — the role <see cref="ActiveTeamAuthorizationContext"/> projects for an operator
        /// enrolled <see cref="TeamRole.Admin"/> on the active team. That gate is the lever the
        /// experiment reads: it is what made the member's outcome move with the operator's boot role.
        /// </summary>
        private static async Task SeedFormDefinitionAsync(IServiceProvider provider)
        {
            var registry = provider.GetRequiredService<ISchemaRegistry>();
            var store = provider.GetRequiredService<IFormDefinitionStore>();

            var schema = await registry.RegisterAsync(
                """
                {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "type": "object",
                  "properties": {
                    "station": { "type": "string" },
                    "result": { "type": "string", "enum": ["PASS", "FAIL"] }
                  },
                  "required": ["station", "result"],
                  "additionalProperties": false
                }
                """);

            var tenant = ActiveTeamTenantContext.ProjectTenantId(OperatorTeam);
            var def = new FormDefinition(
                Id: new FormDefinitionId(FormId),
                Version: new SemanticVersion(1, 0, 0),
                Status: FormDefinitionStatus.Draft,
                Tenant: tenant,
                Owner: IdentityRef.System,
                SchemaRef: schema.Id,
                Overlay: new HarborlineOverlay(
                    Fields: new Dictionary<string, FieldOverlay>
                    {
                        ["station"] = new(InternationalizedText.FromInvariant("Station"), ControlHint: "text"),
                        ["result"] = new(InternationalizedText.FromInvariant("Result"), ControlHint: "text"),
                    },
                    Sections: new[]
                    {
                        new FormSection(
                            Id: "main",
                            Title: InternationalizedText.FromInvariant("Inspection"),
                            Fields: new[] { "station", "result" },
                            Access: new SectionAccess(
                                ReadRoles: new[] { "Admin" },
                                WriteRoles: new[] { "Admin" })),
                    },
                    Rules: Array.Empty<RuleDefinition>(),
                    Title: InternationalizedText.FromInvariant("Startup Identity Fence Probe")),
                Lineage: null,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow);

            await store.RegisterAsync(def);
            await store.PublishAsync(new DefinitionCoordinates(tenant, def.Id.Value, def.Version.ToString()));
        }

        /// <summary>A real request admitted by the listener's Accept-2 selected-session (WEB-plane) branch.</summary>
        internal Task<HttpResponseMessage> SubmitAsMemberAsync() => SendAsMemberAsync(NewSubmitRequest());

        /// <summary>A real request admitted by the listener's Accept-1 bootstrap-token (DESKTOP-plane) branch.</summary>
        internal Task<HttpResponseMessage> SubmitAsDesktopOperatorAsync() =>
            SendAsDesktopOperatorAsync(NewSubmitRequest());

        /// <summary>The gated form RENDER on the WEB plane.</summary>
        internal Task<HttpResponseMessage> RenderAsMemberAsync() => SendAsMemberAsync(NewRenderRequest());

        /// <summary>The same gated RENDER on the DESKTOP plane — it must keep working.</summary>
        internal Task<HttpResponseMessage> RenderAsDesktopOperatorAsync() =>
            SendAsDesktopOperatorAsync(NewRenderRequest());

        /// <summary>The definition-AUTHORING family's list route on the WEB plane.</summary>
        internal Task<HttpResponseMessage> ListDefinitionsAsMemberAsync() =>
            SendAsMemberAsync(NewListDefinitionsRequest());

        /// <summary>The same authoring route on the DESKTOP plane.</summary>
        internal Task<HttpResponseMessage> ListDefinitionsAsDesktopOperatorAsync() =>
            SendAsDesktopOperatorAsync(NewListDefinitionsRequest());

        private async Task<HttpResponseMessage> SendAsMemberAsync(HttpRequestMessage request)
        {
            using (request)
            {
                request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={MemberHandle}");
                return await _client.SendAsync(request);
            }
        }

        private async Task<HttpResponseMessage> SendAsDesktopOperatorAsync(HttpRequestMessage request)
        {
            using (request)
            {
                request.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);
                return await _client.SendAsync(request);
            }
        }

        private static HttpRequestMessage NewSubmitRequest() =>
            new(HttpMethod.Post, $"{FormsRoutes.RouteBase}/{FormId}/submit")
            {
                Content = JsonContent.Create(new { station = "Burj Khalifa", result = "PASS" }),
            };

        private static HttpRequestMessage NewRenderRequest() =>
            new(HttpMethod.Get, $"{FormsRoutes.RouteBase}/{FormId}");

        private static HttpRequestMessage NewListDefinitionsRequest() =>
            new(HttpMethod.Get, FormDefinitionRoutes.RouteBase);

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            await _outerProvider.DisposeAsync();
            await _grantStore.DisposeAsync();
        }
    }

    /// <summary>
    /// Materializes ONE real <see cref="SelectedSessionRequestPrincipal"/> for the known handle. This is
    /// the only substituted seam and it stands UPSTREAM of the route family under test.
    /// </summary>
    private sealed class FixedSelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(selectedHandle, MemberHandle, StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    accountId: "account-forms-startup-fence",
                    tenantId: new TenantId(OperatorTeam.Value.ToString("D")),
                    principalUserId: new PrincipalUserId("principal-forms-startup-fence"),
                    canonicalParty: new CanonicalPartyReference(MemberParty),
                    membershipId: "membership-forms-startup-fence",
                    membershipOwnerVersion: 2,
                    pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-forms-startup-fence", 3)],
                    authorizationEpoch: 5,
                    sessionCorrelationId: "session-forms-startup-fence",
                    coordinationCorrelationId: "coordination-forms-startup-fence")
                : null);
    }

    private static NodeTeamRoster SignedOperatorRoster(IOperationSigner signer, TeamRole role)
    {
        using var founderKey = KeyPair.Generate();
        var founder = new Ed25519Signer(founderKey);
        const string founderParty = "forms-fixture-founder";
        var roster = MemberRoster.StableGenesis(OperatorTeam.Value, founderParty, founder, new Ed25519Verifier())
            .Admit(founderParty, founder, ActiveTeamAuthorizationContext.LocalUserId, signer.IssuerId,
                PermissionCompositions.ForRole(role), new Ed25519Verifier(), DateTimeOffset.UnixEpoch, Guid.NewGuid());
        return new NodeTeamRoster(roster);
    }

    private sealed class FixedActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }
}
