using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// The RUNTIME ADMISSION ROUTE proof (gap #3) over the REAL <see cref="SharedHostedWebApp"/> listener: the
/// generate-invite → redeem → signed-admission-lands-and-publishes flow, the caller-auth gating (a tokenless
/// caller → 401), no-escalation/single-use/TTL-expiry rejection, and the transport-key wiring on admit. Mirrors
/// <see cref="SharedHostedWebAppCallerAuthTests"/> (real in-process Kestrel + real <see cref="HttpClient"/>) but
/// maps the REAL <see cref="AdmissionRoutes"/> over a REAL <see cref="RosterCrdtProjection"/> so the published
/// admission is observable on the synced doctype.
/// </summary>
public sealed class AdmissionRouteTests : IAsyncLifetime
{
    private const string Token = "test-admission-session-token-1234-5678";
    private static readonly Guid Team = Guid.Parse("7e57aaaa-0000-0000-0000-00000000000a");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<string> _dirs = new();
    private readonly List<IAsyncDisposable> _async = new();
    private readonly List<ServiceProvider> _providers = new();
    private readonly List<SharedHostedWebApp> _apps = new();
    private readonly List<HttpClient> _clients = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        foreach (var a in _apps) { await a.StopAsync(CancellationToken.None); await a.DisposeAsync(); }
        foreach (var d in _async) await d.DisposeAsync();
        foreach (var p in _providers) await p.DisposeAsync();
        foreach (var dir in _dirs)
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    // The redeem route resolves the SoD-audit tenant via NodeTenant.Resolve(activeTeam) (#1295 F1), so the
    // harness provides a REAL active team (the production path always has one — routes are mapped after the
    // team-bootstrap hosted service seeds it).
    private sealed class FakeActiveTeamAccessor : IActiveTeamAccessor
    {
        public FakeActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed record Member(string PartyId, KeyPair Key, IOperationSigner Signer)
    {
        public static Member New(string partyId)
        {
            var kp = KeyPair.Generate();
            return new Member(partyId, kp, new Ed25519Signer(kp));
        }
    }

    private sealed class Harness
    {
        public required HttpClient Client { get; init; }
        public required NodeTeamRoster Roster { get; init; }
        public required RosterCrdtProjection Projection { get; init; }
        public required AdmissionCoordinator Coordinator { get; init; }
        public required Member Founder { get; init; }
        // The LIVE SoD audit trail the redeem route records into (#1295 F1) + the tenant it scopes to.
        public required IAuditEventReader AuditReader { get; init; }
        public required TenantId Tenant { get; init; }
        // #1296 F2 — the node's TEAM-SCOPED transport identity (HKDF(node-root, teamId)) the active team's child
        // container holds, i.e. EXACTLY what GET /identity must return and what this node presents in the sync
        // HELLO. Null when the harness was started WITHOUT a team-scoped identity (the pre-bootstrap 503 case).
        public NodeIdentity? TeamTransportIdentity { get; init; }
    }

    /// <summary>
    /// Build + start a real <see cref="SharedHostedWebApp"/> with <paramref name="sessionToken"/> mapping the REAL
    /// <see cref="AdmissionRoutes"/>: a genesis-founder roster, a real SQLite-backed projection, the coordinator,
    /// and the founder as the local admitter. <paramref name="clock"/> drives the coordinator's TTL clock.
    /// </summary>
    // #1296 F2 — derive a REAL team-scoped transport identity the SAME way the per-team registrar + the wire HELLO
    // do (HKDF(node-root, teamId) via TeamScopedNodeIdentity.Derive), so the key GET /identity returns under test is
    // the genuine handshake key, not a stand-in. The active team's child container registers THIS provider — the
    // only place a team-scoped INodeIdentityProvider lives in production (DefaultTeamServiceRegistrar).
    private static readonly Harborline.Api.Kernel.Security.Crypto.Ed25519Signer TransportSigner = new();
    private static readonly ITeamSubkeyDerivation SubkeyDerivation = new TeamSubkeyDerivation(TransportSigner);
    private static readonly IXWingSubkeyDerivation XWingSubkeyDerivation =
        new HkdfXWingSubkeyDerivation(new Harborline.Api.Kernel.Security.Crypto.XWingKem());

    private static NodeIdentity DeriveTeamScopedIdentity(Guid teamId)
    {
        // A distinct 32-byte root seed → a distinct node ROOT identity → a distinct team-scoped transport subkey,
        // exactly as a real install's Program.cs derives its rootIdentity.
        var rootSeed = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(rootSeed);
        var (rootPub, rootPriv) = TransportSigner.GenerateFromSeed(rootSeed);
        var nodeId = Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant();
        var root = new NodeIdentity(nodeId, rootPub, rootPriv);
        return TeamScopedNodeIdentity.Derive(root, teamId.ToString("D"), SubkeyDerivation);
    }

    private async Task<Harness> StartAsync(
        string? sessionToken, TimeProvider? clock = null, bool withTeamScopedIdentity = true)
    {
        var testClock = clock ?? TimeProvider.System;
        var founder = Member.New("founder");
        var genesis = MemberRoster.Genesis(Team, founder.PartyId, founder.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var roster = new NodeTeamRoster(genesis);

        var dir = Path.Combine(Path.GetTempPath(), $"harborline-admission-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var connectionString = $"Data Source={Path.Combine(dir, "roster.db")};Pooling=False";

        var inner = new ServiceCollection();
        inner.AddLogging();
        inner.AddDbContextFactory<NodeLocalRosterDbContext>(opt => opt.UseSqlite(connectionString));
        inner.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        var sp = inner.BuildServiceProvider();
        _providers.Add(sp);
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        var projection = new RosterCrdtProjection(
            sp.GetRequiredService<ICrdtEngine>(), factory, Verifier,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RosterCrdtProjection>.Instance, roster);
        _async.Add(projection);

        var coordinator = new AdmissionCoordinator(Verifier, new InMemoryAdmissionTokenStore(), testClock);

        // The LIVE SoD audit control (#1295 F1) — the same KernelAuditEnrollmentCompensatingControlRecorder the host wires, over an in-memory
        // kernel audit trail, so the F1 PROOF can read what a REAL runtime admit recorded (NOT a direct sink call).
        var auditSigner = new Ed25519Signer(KeyPair.Generate());
        var auditTrail = new InMemoryAuditTrail();
        var auditReader = new InMemoryAuditEventReader(auditTrail, auditTrail, auditSigner);
        var sodAudit = new KernelAuditEnrollmentCompensatingControlRecorder(auditTrail, auditSigner, time: testClock);

        // A REAL active team so NodeTenant.Resolve(activeTeam) yields the SoD-audit tenant (the production path
        // always has a seeded active team before routes serve). #1296 F2: the active team's CHILD container is the
        // ONLY place a team-scoped INodeIdentityProvider lives (DefaultTeamServiceRegistrar), so the GET /identity
        // route resolves the transport key from HERE — register the real HKDF(node-root, teamId) identity so the
        // route returns the genuine handshake key. When withTeamScopedIdentity is false, we omit it to model the
        // pre-bootstrap window (the route then 503s rather than returning a wrong key).
        NodeIdentity? teamTransportIdentity = withTeamScopedIdentity ? DeriveTeamScopedIdentity(Team) : null;
        var teamCollection = new ServiceCollection();
        if (teamTransportIdentity is not null)
            teamCollection.AddSingleton<INodeIdentityProvider>(new InMemoryNodeIdentityProvider(teamTransportIdentity));
        var teamServices = teamCollection.BuildServiceProvider();
        _providers.Add(teamServices);
        var teamContext = new TeamContext(new TeamId(Team), "Test Team", teamServices, TimeProvider.System);
        var activeTeam = new FakeActiveTeamAccessor(teamContext);
        var tenant = ActiveTeamTenantContext.ProjectTenantId(teamContext.TeamId);

        // Real SharedHostedWebApp over its outer container (provides the IActiveTeamAccessor + caller-auth token).
        var outer = new ServiceCollection();
        if (clock is null)
            outer.AddTestKernelClock();
        else
            outer.AddFrozenKernelClock(clock);
        outer.AddLogging();
        outer.AddSingleton<IActiveTeamAccessor>(activeTeam);
        outer.AddSingleton(new NodeCallerSessionToken(sessionToken));
        var outerProvider = outer.BuildServiceProvider();
        _providers.Add(outerProvider);

        var options = Options.Create(new LocalNodeOptions { HealthPort = 0 });
        var logger = outerProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>();
        var app = new SharedHostedWebApp(
            outerProvider,
            options,
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            logger,
            outerProvider.GetRequiredService<TimeProvider>());
        _apps.Add(app);

        app.MapApiRoutes(a =>
            AdmissionRoutes.Map(
                a, coordinator, roster, founder.Signer, founder.PartyId, Verifier, projection,
                sodAudit, activeTeam));

        await app.StartAsync(CancellationToken.None);
        var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        _clients.Add(client);

        return new Harness
        {
            Client = client,
            Roster = roster,
            Projection = projection,
            Coordinator = coordinator,
            Founder = founder,
            AuditReader = auditReader,
            Tenant = tenant,
            TeamTransportIdentity = teamTransportIdentity,
        };
    }

    private static HttpRequestMessage Post(string path, object body, string? bearer = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    private static async Task<string> GenerateInviteAsync(HttpClient client, string bearer, double? ttlMinutes = null)
    {
        var resp = await client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/invites", new { ttlMinutes }, bearer));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("tokenId").GetString()!;
    }

    private static object RedeemBody(string tokenId, Member joiner, byte[]? transportKeyOverride = null)
    {
        // TWO-SIDED WIRE ENROLLMENT: the redeem body is now the SIGNED enrollment request (cerebrum [2026-06-21]).
        // The joiner signs the (token, party, transport-key, principal-key) binding with its principal signer;
        // the route verifies it (proof-of-possession) before admitting. Build it via the canonical protocol helper
        // so the signature is byte-faithful to what the route's verifier reconstructs.
        var transportKey = transportKeyOverride ?? FreshTransportKey();
        var request = Harborline.Api.Foundation.IdentityAtlas.Enrollment.WireEnrollment.BuildRequest(
            tokenId, joiner.PartyId, transportKey, joiner.Signer, DateTimeOffset.UtcNow, Guid.NewGuid());
        return new
        {
            tokenId = request.TokenId,
            joiningPartyId = request.JoiningPartyId,
            joiningPublicKey = request.JoiningPrincipalPublicKey,
            joiningTransportKey = request.JoiningTransportPublicKey,
            signature = request.Signature,
            issuedAtUnixMs = request.IssuedAt.ToUnixTimeMilliseconds(),
            nonce = request.Nonce.ToString(),
        };
    }

    // A joiner's team-scoped transport PUBLIC key is supplied by the joiner over the channel; the route only needs
    // a well-formed 32-byte key, so a fresh random 32-byte key models it for the route-level tests (the real
    // derivation is exercised in Gap3TransportKeyLinkageTests).
    private static byte[] FreshTransportKey()
    {
        var k = new byte[PrincipalId.LengthInBytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(k);
        return k;
    }

    // ── TEST: generate-invite → redeem → signed admission lands in the roster + publishes to the synced doctype. ──

    [Fact(DisplayName = "admission route: generate-invite → redeem → signed admission lands in the roster + syncs")]
    public async Task GenerateInvite_Redeem_Admits_And_Publishes()
    {
        var h = await StartAsync(Token);
        var bob = Member.New("bob");

        // The harness does not pre-seed the genesis onto the doctype, so the publish under test is the NEW
        // admission record the redeem produces — the doctype is empty until then.
        Assert.Equal(0, h.Projection.Count);

        var tokenId = await GenerateInviteAsync(h.Client, Token);
        var resp = await h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem", RedeemBody(tokenId, bob), Token));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        // TWO-SIDED WIRE ENROLLMENT: the redeem now returns the A→B bootstrap response (EnrollmentResponse) — the
        // team id + anchor + the synced roster (carrying bob's admission) + the per-member transport map. Assert
        // the response is the bootstrap payload and carries bob's admission.
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(h.Roster.Current.TeamId.ToString("D"), doc.RootElement.GetProperty("teamId").GetString());
        var admissions = doc.RootElement.GetProperty("admissions").EnumerateArray()
            .Select(a => a.GetProperty("partyId").GetString()).ToArray();
        Assert.Contains("bob", admissions);
        var transportParties = doc.RootElement.GetProperty("memberTransportKeys").EnumerateArray()
            .Select(k => k.GetProperty("partyId").GetString()).ToArray();
        Assert.Contains(h.Founder.PartyId, transportParties); // A's transport key (so B trusts A).
        Assert.Contains("bob", transportParties);             // B's transport key (echoed).

        // bob is now a signed member of the LIVE roster, validated to genesis.
        Assert.True(h.Roster.Current.Contains("bob"));
        Assert.Equal(bob.Key.PrincipalId, h.Roster.ForgeProofBinding("bob"));
        Assert.True(h.Roster.Current.ValidatesToGenesis(Verifier));

        // The admission was PUBLISHED to the synced doctype (so it converges to peers).
        Assert.Contains(h.Projection.Snapshot(),
            s => s.Kind == RosterRecordKind.Admission && s.PartyId == "bob");
    }

    // ── THE F1 PROOF (#1295): a RUNTIME admit THROUGH THE REAL ROUTE records a SoD audit event. ──
    // This is what the deep-review F1 finding demanded: NOT a direct KernelAuditEnrollmentCompensatingControlRecorder.RecordMemberAdmittedAsync
    // unit call (that only proves the unit), but the SEAM being INVOKED by the real admit op driven through the
    // production route + the real DI graph. If the wiring regressed (the route stopped calling the sink), this
    // fails — the control would be back to test-only.

    [Fact(DisplayName = "admission route: a runtime redeem RECORDS a SoD MemberAdmitted audit event (#1295 F1 — control is LIVE)")]
    public async Task Redeem_RecordsEnrollmentControlAuditEvent_AtRuntime()
    {
        var h = await StartAsync(Token);
        var bob = Member.New("bob");

        // The SoD audit trail is empty before any runtime op (the sink has not been invoked).
        Assert.Equal(0, await CountEnrollmentControlAsync(h, AuditEventType.MemberAdmitted));

        var tokenId = await GenerateInviteAsync(h.Client, Token);
        var resp = await h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem", RedeemBody(tokenId, bob), Token));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // THE PROOF: the real admit op landed a SoD MemberAdmitted record in the trail — the seam fired at
        // runtime, driven by the route, not a direct sink call.
        var page = await h.AuditReader.ListAsync(
            h.Tenant, new AuditEventReaderQuery(EventType: AuditEventType.MemberAdmitted));
        var record = Assert.Single(page.Records);

        // …and it carries the correct who-did-what-to-whom attribution from the real op.
        var body = record.Payload.Payload.Body;
        Assert.Equal(h.Founder.PartyId, body["admitter_party_id"]);
        Assert.Equal("bob", body["admitted_party_id"]);
        Assert.Equal("invite", body["admission_mode"]);
        Assert.Equal(bob.Key.PrincipalId.ToBase64Url(), body["admitted_public_key"]);
    }

    private static async Task<int> CountEnrollmentControlAsync(Harness h, AuditEventType type)
    {
        var page = await h.AuditReader.ListAsync(h.Tenant, new AuditEventReaderQuery(EventType: type));
        return page.Records.Count;
    }

    // ── TEST: caller-auth REQUIRED — a tokenless caller is 401 on both routes. ──

    [Fact(DisplayName = "admission route: a TOKENLESS caller is 401 on generate-invite AND redeem (caller-auth gated)")]
    public async Task Tokenless_Caller_Is_401()
    {
        var h = await StartAsync(Token);
        var bob = Member.New("bob");

        var invite = await h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/invites", new { }));
        Assert.Equal(HttpStatusCode.Unauthorized, invite.StatusCode);

        var redeem = await h.Client.SendAsync(
            Post($"{AdmissionRoutes.RouteBase}/redeem", RedeemBody("any-token", bob)));
        Assert.Equal(HttpStatusCode.Unauthorized, redeem.StatusCode);

        // A wrong token is also rejected fail-closed.
        var wrong = await h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/invites", new { }, "wrong-token"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    // ── TEST: invite is SINGLE-USE — a second redeem of the same token is rejected. ──

    [Fact(DisplayName = "admission route: an invite is SINGLE-USE — a replayed redeem is rejected")]
    public async Task Invite_Is_Single_Use()
    {
        var h = await StartAsync(Token);
        var bob = Member.New("bob");
        var carol = Member.New("carol");

        var tokenId = await GenerateInviteAsync(h.Client, Token);

        var first = await h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem", RedeemBody(tokenId, bob), Token));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // A second redeem of the SAME token (even for a different joiner) is rejected — single-use.
        var replay = await h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem", RedeemBody(tokenId, carol), Token));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.False(h.Roster.Current.Contains("carol"));
    }

    // ── TEST: a TTL-EXPIRED invite is rejected. ──

    [Fact(DisplayName = "admission route: a TTL-EXPIRED invite is rejected on redeem")]
    public async Task Expired_Invite_Is_Rejected()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var h = await StartAsync(Token, clock);
        var bob = Member.New("bob");

        var tokenId = await GenerateInviteAsync(h.Client, Token, ttlMinutes: 1);

        // Advance past the 1-minute TTL.
        clock.Advance(TimeSpan.FromMinutes(2));

        var resp = await h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem", RedeemBody(tokenId, bob), Token));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(h.Roster.Current.Contains("bob"));
    }

    // ── TEST: NO-ESCALATION is inherited — the admit confers at most the member composition (not owner). ──

    [Fact(DisplayName = "admission route: invite admit grants the minimal MEMBER composition (no-escalation inherited)")]
    public async Task Admit_Grants_Member_Composition_Not_Owner()
    {
        var h = await StartAsync(Token);
        var bob = Member.New("bob");

        var tokenId = await GenerateInviteAsync(h.Client, Token);
        var resp = await h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem", RedeemBody(tokenId, bob), Token));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // bob got the minimal member composition — NOT the founder's owner set. He cannot, e.g., admit others
        // beyond what a member holds (the AdmitOverInvite default + MemberRoster.Admit no-escalation). Concretely:
        // a member does NOT hold the root-grant (grant:permissions + org:transfer-ownership).
        var bobPerms = h.Roster.Current.PermissionsOf("bob");
        Assert.NotNull(bobPerms);
        var memberPerms = PermissionCompositions.Member;
        Assert.True(bobPerms!.IsSubsetOf(memberPerms));
        Assert.False(bobPerms.Contains(Permission.GrantPermissions));
        Assert.False(bobPerms.Contains(Permission.OrgTransferOwnership));
    }

    // ── TEST: a malformed transport key is rejected 400 (fail-closed). ──

    [Fact(DisplayName = "admission route: a malformed/short transport key is rejected 400 (fail-closed)")]
    public async Task Malformed_Transport_Key_Is_400()
    {
        var h = await StartAsync(Token);
        var tokenId = await GenerateInviteAsync(h.Client, Token);

        // A signed request whose transport key is MALFORMED — the route parses keys (catches malformed → 400)
        // before/independently of the signature verify. (The signature here is over the malformed value, which
        // never reaches verification because the key parse fails first.)
        var joiner = Member.New("bob");
        var signed = Harborline.Api.Foundation.IdentityAtlas.Enrollment.WireEnrollment.BuildRequest(
            tokenId, joiner.PartyId, FreshTransportKey(), joiner.Signer, DateTimeOffset.UtcNow, Guid.NewGuid());
        var body = new
        {
            tokenId,
            joiningPartyId = "bob",
            joiningPublicKey = joiner.Key.PrincipalId.ToBase64Url(),
            joiningTransportKey = "not-base64url!!", // malformed
            signature = signed.Signature,
            issuedAtUnixMs = signed.IssuedAt.ToUnixTimeMilliseconds(),
            nonce = signed.Nonce.ToString(),
        };
        var resp = await h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem", body, Token));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── #1296 F2 PROOF 1: GET /identity RESOLVES (no startup throw) + returns the TEAM-SCOPED transport key. ──
    // The deep-review F2 concern (1) was that INodeIdentityProvider was injected from the OUTER container, where it
    // is never registered → an unresolvable ctor → a startup throw. The fix resolves it from the ACTIVE TEAM's
    // child container at request time. This test starts the REAL listener with the REAL route (so a ctor/resolution
    // fault would surface) and asserts the GET succeeds and returns the team-scoped key, not the principal key.

    [Fact(DisplayName = "admission route: GET /identity resolves the TEAM-SCOPED transport key (no outer-container throw) — #1296 F2")]
    public async Task GetIdentity_Returns_TeamScoped_TransportKey()
    {
        var h = await StartAsync(Token);

        var identity = await GetIdentityAsync(h.Client, Token);

        // partyId + principalPublicKey come from the install-level roster + signer (unchanged).
        Assert.Equal(h.Roster.Current.GenesisPartyId, identity.partyId);
        Assert.Equal(h.Founder.Signer.IssuerId.ToBase64Url(), identity.principalPublicKey);

        // THE FIX: the transportPublicKey is the ACTIVE TEAM's team-scoped subkey — NOT the principal key.
        var returnedTransport = DecodeBase64Url(identity.transportPublicKey);
        Assert.Equal(h.TeamTransportIdentity!.PublicKey, returnedTransport);
        Assert.NotEqual(h.Founder.Signer.IssuerId.AsSpan().ToArray(), returnedTransport); // distinct from principal
    }

    // ── #1296 F2 PROOF 2 (THE BITE): the transport key GET /identity returns — the key a joiner presents at redeem
    //    and the joined team RECORDS — equals the team-scoped subkey AND passes the joined team's
    //    MemberSetTrustPolicy. This is the wire-trust-gate precursor to the gap-C/D two-node test: a redeemed
    //    member WOULD pass the HELLO trust check. F2 concern (2): returning the root/own-team key here would store a
    //    transport key that the handshake rejects → the nodes never trust each other for sync. ──

    [Fact(DisplayName = "admission route: the GET /identity transport key, recorded at redeem, PASSES the joined team's MemberSetTrustPolicy (wire-trust precursor) — #1296 F2")]
    public async Task RedeemedTransportKey_PassesJoinedTeam_TrustPolicy()
    {
        // The ADMITTER node (the team being joined). Its active team holds its OWN team-scoped subkey (the trust
        // floor); the joiner's key is what we admit + must trust.
        var admitter = await StartAsync(Token);

        // The JOINER node — a SEPARATE install with its OWN root → its OWN team-scoped subkey for the SAME team.
        // Its GET /identity is the production source of the joiner's transport key (the Harborline App never fakes it).
        var joinerNode = await StartAsync(Token);
        var joinerIdentity = await GetIdentityAsync(joinerNode.Client, Token);
        var joinerTransportKey = DecodeBase64Url(joinerIdentity.transportPublicKey);

        // Sanity: the joiner's GET /identity transport key IS its node's team-scoped subkey (HKDF(joiner-root,team)).
        Assert.Equal(joinerNode.TeamTransportIdentity!.PublicKey, joinerTransportKey);

        // Redeem an invite on the ADMITTER node, presenting the joiner's REAL transport key from its GET /identity
        // (exactly what redeemInvite() does: fetchNodeIdentity() → joiningTransportKey). The admitter records it via
        // AdmitPeer into its TrustedTransportKeys() — the set MemberSetTrustPolicy checks.
        var bob = Member.New("bob");
        var tokenId = await GenerateInviteAsync(admitter.Client, Token);
        // The SIGNED enrollment request: bob signs the binding over the JOINER's real team-scoped transport key
        // (two-sided wire enrollment — the admitter verifies bob's proof-of-possession before admitting).
        var signedReq = Harborline.Api.Foundation.IdentityAtlas.Enrollment.WireEnrollment.BuildRequest(
            tokenId, bob.PartyId, joinerTransportKey, bob.Signer, DateTimeOffset.UtcNow, Guid.NewGuid());
        var redeemBody = new
        {
            tokenId,
            joiningPartyId = bob.PartyId,
            joiningPublicKey = bob.Key.PrincipalId.ToBase64Url(),
            joiningTransportKey = joinerIdentity.transportPublicKey, // the JOINER's real team-scoped key
            signature = signedReq.Signature,
            issuedAtUnixMs = signedReq.IssuedAt.ToUnixTimeMilliseconds(),
            nonce = signedReq.Nonce.ToString(),
        };
        var resp = await admitter.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem", redeemBody, Token));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // The admitter's roster now records the joiner's transport key. Build the joined team's trust gate the SAME
        // way the per-team registrar does — {own-subkey floor} ∪ {admitted-member transport keys} — and assert the
        // joiner's HELLO (which presents the SAME team-scoped key its GET /identity returned) is TRUSTED.
        var policy = new MemberSetTrustPolicy(() =>
        {
            var keys = new List<byte[]> { admitter.TeamTransportIdentity!.PublicKey }; // own-subkey floor
            keys.AddRange(admitter.Roster.TrustedTransportKeys());                     // admitted peers
            return keys;
        });

        var joinerHello = new HelloMessage(
            NodeId: joinerNode.TeamTransportIdentity!.NodeIdBytes,
            SchemaVersion: "1",
            SupportedVersions: new[] { "1" },
            PublicKey: joinerTransportKey, // the EXACT key the joiner presents on the wire (= its GET /identity key)
            Timestamp: 0UL,
            Signature: Array.Empty<byte>());

        // THE PROOF: the redeemed member WOULD pass the joined team's wire trust gate — the recorded transport key
        // matches the handshake key (the gap-C/D two-node precursor). A regression that returned the root/own-team
        // key from GET /identity would record a NON-matching key and this would be False.
        Assert.True(policy.IsTrusted(joinerHello));

        // And a STRANGER (a different team-scoped key, never admitted) is still rejected — fail-closed.
        var strangerHello = joinerHello with { PublicKey = DeriveTeamScopedIdentity(Team).PublicKey };
        Assert.False(policy.IsTrusted(strangerHello));
    }

    // ── #1296 F2 PROOF 3: pre-bootstrap (no active team yet) FAILS CLOSED with 503, never a wrong/absent key. ──

    [Fact(DisplayName = "admission route: GET /identity 503s when no active team is materialized (fail-closed, no wrong key) — #1296 F2")]
    public async Task GetIdentity_NoActiveTeam_Is_503()
    {
        var h = await StartAsync(Token, withTeamScopedIdentity: false);

        var resp = await h.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, $"{AdmissionRoutes.RouteBase}/identity")
            { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", Token) } });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
    }

    private static async Task<NodeIdentityDto> GetIdentityAsync(HttpClient client, string bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"{AdmissionRoutes.RouteBase}/identity")
        { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", bearer) } };
        var resp = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        return new NodeIdentityDto(
            root.GetProperty("partyId").GetString()!,
            root.GetProperty("principalPublicKey").GetString()!,
            root.GetProperty("transportPublicKey").GetString()!);
    }

    private sealed record NodeIdentityDto(string partyId, string principalPublicKey, string transportPublicKey);

    // ── MINOR-1 (cerebrum [2026-06-21] verdict): the POST /admission/join HTTP ROUTE was untested. ────────────
    //
    // The B-side E2E drove NodeEnrollmentJoinService.JoinAsync DIRECTLY, never the HTTP route — so the route's
    // body-validation, the fail-closed-reject → 400 mapping, the InvalidOperationException → 409 mapping, AND that
    // the route is genuinely F1-caller-auth-gated (a tokenless caller → 401, like the other /admission/* routes)
    // were unproven. These tests drive the REAL route over the REAL SharedHostedWebApp listener → the REAL
    // NodeEnrollmentJoinService → the REAL NodeWireEnrollmentClient; only the wire transport (the bytes to A) is a
    // test double, exactly as the design injects it. Mirrors the AdmissionRouteTests harness shape above.

    /// <summary>A test <see cref="IEnrollmentTransport"/>: <c>null</c> response models A rejecting / an unreachable
    /// admitter (→ the client returns a fail-closed enroll outcome → the route 400s); throwing
    /// <see cref="InvalidOperationException"/> models the not-configured wiring fault (→ the route 409s).</summary>
    private sealed class StubEnrollmentTransport : IEnrollmentTransport
    {
        private readonly bool _throwNotConfigured;
        public EnrollmentRequest? LastRequest { get; private set; }
        public StubEnrollmentTransport(bool throwNotConfigured = false) => _throwNotConfigured = throwNotConfigured;
        public Task<EnrollmentResponse?> SendAsync(EnrollmentRequest request, CancellationToken ct)
        {
            LastRequest = request;
            if (_throwNotConfigured)
                throw new InvalidOperationException("admitter endpoint not configured");
            return Task.FromResult<EnrollmentResponse?>(null); // A rejected / unreachable → fail-closed enroll.
        }
    }

    /// <summary>Build + start a real listener that ALSO maps POST /admission/join over a real
    /// <see cref="NodeEnrollmentJoinService"/> wired to <paramref name="transport"/>. Returns the client + the
    /// public team anchor fields a join body needs.</summary>
    private async Task<(HttpClient Client, string TeamId, string GenesisPartyId, string GenesisPublicKey)>
        StartWithJoinAsync(string? sessionToken, IEnrollmentTransport transport)
    {
        var founder = Member.New("founder");
        var genesis = MemberRoster.Genesis(Team, founder.PartyId, founder.Signer, Verifier, DateTimeOffset.UtcNow, Guid.NewGuid());
        var roster = new NodeTeamRoster(genesis);

        var dir = Path.Combine(Path.GetTempPath(), $"harborline-join-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);

        var inner = new ServiceCollection();
        inner.AddLogging();
        inner.AddDbContextFactory<NodeLocalRosterDbContext>(opt => opt.UseSqlite($"Data Source={Path.Combine(dir, "roster.db")};Pooling=False"));
        inner.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        var sp = inner.BuildServiceProvider();
        _providers.Add(sp);
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync()) await ctx.Database.EnsureCreatedAsync();

        var projection = new RosterCrdtProjection(
            sp.GetRequiredService<ICrdtEngine>(), factory, Verifier,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RosterCrdtProjection>.Instance, roster);
        _async.Add(projection);

        var coordinator = new AdmissionCoordinator(Verifier, new InMemoryAdmissionTokenStore(), clock: TimeProvider.System);
        var sodAudit = new KernelAuditEnrollmentCompensatingControlRecorder(new InMemoryAuditTrail(), new Ed25519Signer(KeyPair.Generate()), time: TimeProvider.System);

        // A real active team (with a team-scoped identity) so the SoD-tenant + identity resolution behave; the join
        // route does not read the identity, but mapping AdmissionRoutes.Map requires a real active team accessor.
        var teamCollection = new ServiceCollection();
        teamCollection.AddSingleton<INodeIdentityProvider>(new InMemoryNodeIdentityProvider(DeriveTeamScopedIdentity(Team)));
        var teamServices = teamCollection.BuildServiceProvider();
        _providers.Add(teamServices);
        var teamContext = new TeamContext(new TeamId(Team), "Test Team", teamServices, TimeProvider.System);

        // A REAL team factory + active accessor so NodeEnrollmentJoinService can materialize + switch on a (here
        // unreached) success path; the join route tests exercise the REJECT (400) + NOT-CONFIGURED (409) + 401 +
        // 400-missing-field paths, none of which reach the switch — so a real-but-unexercised factory is right.
        var realFactory = new TeamContextFactory(TimeProvider.System);
        _async.Add(realFactory);
        var realAccessor = new ActiveTeamAccessor(realFactory);

        // The join service over a REAL client + the injected (stub) transport.
        var rootSeed = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(rootSeed);
        var (rootPub, rootPriv) = TransportSigner.GenerateFromSeed(rootSeed);
        var nodeId = Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant();
        var rootIdentity = new NodeIdentity(nodeId, rootPub, rootPriv);
        var principalSigner = new Ed25519Signer(KeyPair.Generate());
        var client = new NodeWireEnrollmentClient(
            rootIdentity, SubkeyDerivation, XWingSubkeyDerivation,
            principalSigner, "os:joiner#0001", roster, Verifier, clock: TimeProvider.System);
        var storeActivator = new NoopActivator();
        var joinService = new NodeEnrollmentJoinService(
            client, transport, realFactory, storeActivator, realAccessor,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NodeEnrollmentJoinService>.Instance);

        var outer = new ServiceCollection();
        outer.AddTestKernelClock();
        outer.AddLogging();
        outer.AddSingleton<IActiveTeamAccessor>(new FakeActiveTeamAccessor(teamContext));
        outer.AddSingleton(new NodeCallerSessionToken(sessionToken));
        var outerProvider = outer.BuildServiceProvider();
        _providers.Add(outerProvider);

        var options = Options.Create(new LocalNodeOptions { HealthPort = 0 });
        var logger = outerProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>();
        var app = new SharedHostedWebApp(
            outerProvider,
            options,
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            logger,
            outerProvider.GetRequiredService<TimeProvider>());
        _apps.Add(app);

        app.MapApiRoutes(a => AdmissionRoutes.Map(
            a, coordinator, roster, founder.Signer, founder.PartyId, Verifier, projection, sodAudit,
            outerProvider.GetRequiredService<IActiveTeamAccessor>(), joinService));

        await app.StartAsync(CancellationToken.None);
        var http = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
        _clients.Add(http);

        var anchor = TeamTrustAnchor.FromRoster(roster.Current);
        return (http, anchor.TeamId, anchor.GenesisPartyId, anchor.GenesisPublicKey);
    }

    private sealed class NoopActivator : ITeamStoreActivator
    {
        public ValueTask ActivateAsync(TeamId teamId, CancellationToken ct) => default;
    }

    [Fact(DisplayName = "join route: a TOKENLESS caller is 401 (the /join route is F1-caller-auth-gated like the other /admission/* routes) — MINOR-1")]
    public async Task Join_TokenlessCaller_Is_401()
    {
        var (client, teamId, gp, gk) = await StartWithJoinAsync(Token, new StubEnrollmentTransport());

        var resp = await client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/join",
            new { tokenId = "t", joiningPartyId = "web-party", teamId, genesisPartyId = gp, genesisPublicKey = gk })); // no bearer
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact(DisplayName = "join route: a missing-field body is 400 (fail-closed body validation) — MINOR-1")]
    public async Task Join_MissingField_Is_400()
    {
        var (client, _, gp, gk) = await StartWithJoinAsync(Token, new StubEnrollmentTransport());

        // Missing team_id.
        var resp = await client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/join",
            new { tokenId = "t", joiningPartyId = "web-party", genesisPartyId = gp, genesisPublicKey = gk }, Token));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "join route: a fail-closed REJECT (A unreachable/rejected) is an opaque 400 (node unchanged) — MINOR-1")]
    public async Task Join_RejectedEnroll_Is_Opaque_400()
    {
        // The stub transport returns null → the real client returns a fail-closed enroll outcome → the route 400s.
        var transport = new StubEnrollmentTransport();
        var (client, teamId, gp, gk) = await StartWithJoinAsync(Token, transport);

        var resp = await client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/join",
            new { tokenId = Guid.NewGuid().ToString("N"), joiningPartyId = "web-party", teamId, genesisPartyId = gp, genesisPublicKey = gk }, Token));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("web-party", transport.LastRequest!.JoiningPartyId);
        Assert.True(WireEnrollment.VerifyRequest(transport.LastRequest, Verifier));
        // Opaque reason — the body never discloses which of unreachable/rejected/expired.
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("error", out _));
    }

    [Fact(DisplayName = "join route: a wiring fault (transport not configured) is a coarse 409 join_not_configured — MINOR-1")]
    public async Task Join_NotConfigured_Is_409()
    {
        // The stub transport throws InvalidOperationException → the route maps it to a 409 (not a 500 leak).
        var (client, teamId, gp, gk) = await StartWithJoinAsync(Token, new StubEnrollmentTransport(throwNotConfigured: true));

        var resp = await client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/join",
            new { tokenId = "t", joiningPartyId = "web-party", teamId, genesisPartyId = gp, genesisPublicKey = gk }, Token));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("join_not_configured", doc.RootElement.GetProperty("error").GetString());
    }

    // base64url decode (the route emits base64url: '+'→'-', '/'→'_', '=' trimmed) — reuse PrincipalId's validating
    // decode (32-byte Ed25519), then surface the raw bytes for the byte-for-byte key comparison.
    private static byte[] DecodeBase64Url(string value) => PrincipalId.FromBase64Url(value).AsSpan().ToArray();

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
