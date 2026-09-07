using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Persistence;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Capabilities;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Audit;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Card #3192 — the acting member must reach the audit seam THROUGH THE REAL SERVING PIPELINE.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> Every other MTW-2 2612-C test either injects a stub
/// <c>INodeCallerAttributionSource</c> (<c>NodeAttributionEnvelopeTests</c>) or hand-sets
/// <c>HttpContextAccessor.HttpContext</c> (<c>Mtw2TwoUserAcceptanceE2E</c>) — so the whole suite passed
/// while production request attribution did not propagate: the accessor was registered only on the OUTER generic host
/// (<c>Program.cs</c> → <c>NodeAuditComposition</c>), never on the inner <see cref="SharedHostedWebApp"/>
/// that actually serves requests, so <c>HttpContextAccessor.HttpContext</c> was null on every production
/// request and every audit row stamped <c>mode: operator-fallback</c>.
/// </para>
/// <para>
/// <b>What is REAL here (and why that is the point).</b> The container topology is production's exactly:
/// the audit slice is composed by the production <see cref="NodeAuditComposition.AddNodeAuditWrites"/> on
/// an OUTER container that has no HTTP pipeline, while requests are served by a real
/// <see cref="SharedHostedWebApp"/> built from its own inner <c>WebApplication</c> container. The request
/// is a real HTTP request over Kestrel, admitted by the real listener caller-auth middleware, whose
/// Accept-2 branch binds a real <see cref="SelectedSessionRequestPrincipal"/>. There is NO stub
/// attribution source and NO hand-set <c>HttpContext</c> anywhere in this file: whatever carries the
/// acting member from the middleware to <see cref="NodeAuditWriteEnlister"/> is production code, and if
/// it is inert the member assertion below fails.
/// </para>
/// <para>
/// <b>What is substituted, and why it is not the seam under test.</b> Only
/// <see cref="IWebSelectedSessionPrincipalAuthority"/> — the handle→principal materializer UPSTREAM of the
/// attribution propagation path. Its real implementation (fence, revalidation, grant pins) is covered by
/// <see cref="SelectedSessionRequestPrincipalTests"/> and <c>Mtw2TwoUserAcceptanceE2E</c>; re-driving it
/// here would add a second identity substrate without touching the attribution propagation path this card is about.
/// </para>
/// <para>
/// <b>Attribution wording (board finding F9).</b> These asserts are ATTESTATION INTEGRITY +
/// TAMPER-EVIDENCE only — never impersonation-resistance. The node key attests; a node can assert any
/// member. Per-member non-repudiation is the deferred passkey/roster-key future.
/// </para>
/// </remarks>
[Trait("PlanCard", "MTW-2-3192")]
public sealed class NodeServingPipelineAttributionTests
{
    // This test exercises request attribution, not ticket-066 audience admission. Reuse an exact
    // Phase A legacy-baseline pair so the independent attribution seam retains its old eligibility.
    private const string ProbePath = "/api/local-node/journal-entries";
    private const string CallerToken = "serving-pipeline-attribution-caller-token";
    private const string MemberHandle = "handle-member-with-at-least-256-bits-of-test-entropy-00000000";
    private const string MemberParty = "party-serving-pipeline-member";

    // A FROZEN base clock; the probe advances it one second per write so two audit rows never share an
    // OccurredAt (the hash-chain tie-break would otherwise depend on the random AuditId).
    private static readonly DateTimeOffset FrozenNow = new(2026, 7, 27, 10, 34, 0, TimeSpan.Zero);

    private static readonly string TenantIdValue = Guid.NewGuid().ToString("D");

    [Fact(DisplayName = "Serving pipeline: a real selected-session request through SharedHostedWebApp stamps the acting MEMBER on the audit row (mode: member-session) — not the operator fallback")]
    public async Task SelectedSessionRequest_StampsActingMember_ThroughRealServingPipeline()
    {
        await using var fixture = await Fixture.CreateAsync();

        using var response = await fixture.PostAsMemberAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await fixture.SingleAuditRowAsync();

        // THE assertion this card exists for. On unfixed main request attribution does not propagate; the enlister sees a
        // null attribution, and Actor collapses to the operator constant.
        Assert.Equal(MemberParty, row.Actor);
        Assert.NotEqual(ActiveTeamAuthorizationContext.LocalUserId, row.Actor);

        var attribution = ParseAttribution(row.Payload);
        Assert.Equal(NodeAuditWriteEnlister.CarriedDecisionAttributionSchema,
            attribution.GetProperty("schema").GetString());
        Assert.Equal(MemberParty, attribution.GetProperty("member_party_id").GetString());
        Assert.Equal(fixture.IssuerId.ToBase64Url(),
            attribution.GetProperty("attesting_public_key").GetString());
        var authority = ParseAuthority(row.Payload);
        Assert.Equal(MemberParty, authority.GetProperty("principal").GetString());
        Assert.Equal(LedgerTenant.Value, authority.GetProperty("tenant").GetString());

        // Attestation integrity is unaffected by the attribution propagation fix: the row still verifies under the node key.
        var op = NodeAuditSignaturePayload.TryReconstruct(row, fixture.IssuerId, row.Signature!);
        Assert.NotNull(op);
        Assert.True(fixture.Verifier.Verify(op!), "the member-attributed row must still verify under the node key");
    }

    [Fact(DisplayName = "Serving pipeline: an UNBOUND (bootstrap-token) request still degrades to the ruled operator fallback — the fix must not attribute someone else")]
    public async Task BootstrapTokenRequest_StillDegradesToOperatorFallback()
    {
        await using var fixture = await Fixture.CreateAsync();

        using var response = await fixture.PostAsBootstrapOperatorAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var row = await fixture.SingleAuditRowAsync();

        Assert.Equal(ActiveTeamAuthorizationContext.LocalUserId, row.Actor);
        var attribution = ParseAttribution(row.Payload);
        Assert.Equal(NodeAuditWriteEnlister.CarriedDecisionAttributionSchema,
            attribution.GetProperty("schema").GetString());
        Assert.Equal(ActiveTeamAuthorizationContext.LocalUserId,
            attribution.GetProperty("member_party_id").GetString());
        Assert.Equal(ActiveTeamAuthorizationContext.LocalUserId,
            ParseAuthority(row.Payload).GetProperty("principal").GetString());

        var op = NodeAuditSignaturePayload.TryReconstruct(row, fixture.IssuerId, row.Signature!);
        Assert.True(fixture.Verifier.Verify(op!), "the operator-fallback attestation must verify too");
    }

    [Fact(DisplayName = "Serving pipeline: the acting member does NOT leak past the request — a later unbound write on the same host stamps the operator fallback")]
    public async Task ActingMember_DoesNotLeakToTheNextUnboundWrite()
    {
        await using var fixture = await Fixture.CreateAsync();

        using var member = await fixture.PostAsMemberAsync();
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
        using var operatorWrite = await fixture.PostAsBootstrapOperatorAsync();
        Assert.Equal(HttpStatusCode.OK, operatorWrite.StatusCode);

        var rows = await fixture.AuditRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(MemberParty, rows[0].Actor);
        Assert.Equal(ActiveTeamAuthorizationContext.LocalUserId, rows[1].Actor);

        // The hash chain links across both rows, and BOTH classify Verified through the production
        // reader. The operator-fallback row is byte-shaped exactly like every row written before this
        // fix (the fix changes stamped VALUES, never the envelope schema or the signing input), so this
        // is also the compatibility assertion: pre-fix history still verifies alongside post-fix rows.
        Assert.Null(rows[0].PrevHash);
        Assert.Equal(rows[0].Hash, rows[1].PrevHash);

        var views = (await fixture.AuditViewsAsync()).ToList();
        Assert.Equal(2, views.Count);
        Assert.All(views, v => Assert.Equal(NodeAuditSignatureClassifier.Verified, v.SignatureState));
    }

    private static JsonElement ParseAttribution(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.GetProperty("attribution").Clone();
    }

    private static JsonElement ParseAuthority(string payloadJson)
    {
        using var doc = JsonDocument.Parse(payloadJson);
        return doc.RootElement.GetProperty("authority").Clone();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _dir;
        private readonly ServiceProvider _outerProvider;
        private readonly SharedHostedWebApp _app;
        private readonly NodePrincipalSigner _signer;
        private readonly IDbContextFactory<LocalNodeDbContext> _factory;

        private Fixture(
            string dir,
            ServiceProvider outerProvider,
            SharedHostedWebApp app,
            HttpClient client,
            NodePrincipalSigner signer,
            IDbContextFactory<LocalNodeDbContext> factory)
        {
            _dir = dir;
            _outerProvider = outerProvider;
            _app = app;
            Client = client;
            _signer = signer;
            _factory = factory;
        }

        internal HttpClient Client { get; }

        internal IOperationVerifier Verifier { get; } = new Ed25519Verifier();

        internal PrincipalId IssuerId => _signer.Signer.IssuerId;

        internal static async Task<Fixture> CreateAsync()
        {
            var dir = Path.Combine(
                Path.GetTempPath(), "serving-pipeline-attribution-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var connectionString =
                $"Data Source={Path.Combine(dir, "serving-pipeline-attribution.db")};Pooling=False";

            var signer = new NodePrincipalSigner(FixedSeed());
            var clock = new AdvancingClock(FrozenNow);

            // ── The OUTER container: EXACTLY the production audit composition, on a container with no
            //    HTTP pipeline — the same geometry Program.cs builds via Host.CreateApplicationBuilder.
            var outer = new ServiceCollection();
            outer.AddLogging();
            outer.AddSingleton<IHarborlineEntityModule, FinancialLedgerEntityModule>();
            outer.AddSingleton<IHarborlineEntityModule, Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule>();
            outer.AddSingleton<IHarborlineEntityModule, AuditEventEntityModule>();
            outer.AddDbContextFactory<LocalNodeDbContext>(opt => opt.UseSqlite(connectionString));
            outer.AddFrozenKernelClock(clock);
            outer.AddSingleton(signer);
            outer.AddNodeAuditWrites();

            // Listener prerequisites the shared app resolves from the outer container.
            outer.AddSingleton<IActiveTeamAccessor>(new NoTeamAccessor());
            outer.AddSingleton(new NodeCallerSessionToken(CallerToken));
            outer.AddSingleton<IWebSelectedSessionPrincipalAuthority>(new FixedSelectedSessionAuthority());

            var outerProvider = outer.BuildServiceProvider();

            var factory = outerProvider.GetRequiredService<IDbContextFactory<LocalNodeDbContext>>();
            await using (var ctx = await factory.CreateDbContextAsync())
            {
                await ctx.Database.EnsureCreatedAsync();
            }

            // The production journal store wired with the production enlister resolved from the OUTER
            // container — no stub attribution source anywhere.
            var journalStore = new NodeEfJournalStore(
                factory,
                NodeJournalWriteAdapters.Create(
                    audit: (Harborline.Api.Foundation.Coordination.IWriteEnlistment)
                        outerProvider.GetRequiredService<INodeAuditWriteEnlister>()));

            // ── The INNER serving app: the real production listener.
            var app = new SharedHostedWebApp(
                outerProvider,
                Options.Create(new LocalNodeOptions { HealthPort = 0 }),
                new LocalNodeExecutableEndpointRegistry(),
                outerProvider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
                outerProvider.GetRequiredService<TimeProvider>());

            var writes = 0;
            app.MapApiRoutes(routes =>
                routes.MapDeviceReachableProductDataGroup().MapPost(ProbePath, async (HttpContext http) =>
                {
                    // Distinct monotonic OccurredAt per write ⇒ a deterministic hash chain.
                    clock.Advance(TimeSpan.FromSeconds(1));
                    var id = "JE-3192-" + Interlocked.Increment(ref writes).ToString("D3");
                    var authority = new AuthorizationWriteContext(
                        new ActorId(NodeCallerParty.Resolve(http).Value),
                        LedgerTenant,
                        clock.GetUtcNow());
                    await journalStore.SaveAtomicForTestAsync(
                        LedgerTenant,
                        BalancedPosted(id, authority.At),
                        authority);
                    return Results.Ok();
                }));

            await app.StartAsync(CancellationToken.None);
            var client = new HttpClient { BaseAddress = new Uri(app.SelectedUrl!) };
            return new Fixture(dir, outerProvider, app, client, signer, factory);
        }

        /// <summary>A real request admitted by the listener's Accept-2 selected-session branch.</summary>
        internal async Task<HttpResponseMessage> PostAsMemberAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ProbePath);
            request.Headers.Add("Cookie", $"{WebSessionCookieNames.Selected}={MemberHandle}");
            return await Client.SendAsync(request);
        }

        /// <summary>A real request admitted by the listener's Accept-1 bootstrap-token branch (no member).</summary>
        internal async Task<HttpResponseMessage> PostAsBootstrapOperatorAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ProbePath);
            request.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);
            return await Client.SendAsync(request);
        }

        internal async Task<NodeAuditEventRow> SingleAuditRowAsync() =>
            Assert.Single(await AuditRowsAsync());

        /// <summary>The production read model's classification of every row (signature + chain state).</summary>
        internal async Task<IReadOnlyList<NodeAuditEventView>> AuditViewsAsync()
        {
            var reader = new NodeAuditEventReader(
                _factory, new NodeAuditSignatureVerificationContext(IssuerId, Verifier));
            return (await reader.ListAsync(LedgerTenant.Value, new NodeAuditEventReaderQuery())).Events;
        }

        internal async Task<IReadOnlyList<NodeAuditEventRow>> AuditRowsAsync()
        {
            await using var ctx = await _factory.CreateDbContextAsync();
            return await ctx.Set<NodeAuditEventRow>()
                .AsNoTracking()
                .Where(r => r.TenantId == LedgerTenant.Value)
                .OrderBy(r => r.OccurredAt).ThenBy(r => r.AuditId)
                .ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
            await _outerProvider.DisposeAsync();
            _signer.Dispose();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    private static readonly TenantId LedgerTenant = new(TenantIdValue);

    private static byte[] FixedSeed()
    {
        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++)
        {
            seed[i] = (byte)(0x31 + i);
        }
        return seed;
    }

    private static JournalEntry BalancedPosted(string id, DateTimeOffset at) =>
        new JournalEntry(
            id: new JournalEntryId(id),
            tenantId: LedgerTenant,
            entryDate: new DateOnly(2026, 7, 27),
            memo: "serving-pipeline attribution probe",
            lines: new List<JournalEntryLine>
            {
                new(new GLAccountId("1000"), 100m, 0m),
                new(new GLAccountId("4000"), 0m, 100m),
            },
            createdAtUtc: new Instant(at))
        {
            Status = JournalEntryStatus.Posted,
            PostedAtUtc = new Instant(at),
        };

    /// <summary>
    /// Materializes ONE real <see cref="SelectedSessionRequestPrincipal"/> for the known handle. This is
    /// the only substituted seam — it stands UPSTREAM of the attribution propagation path under test (see the class remarks).
    /// </summary>
    private sealed class FixedSelectedSessionAuthority : IWebSelectedSessionPrincipalAuthority
    {
        public Task<SelectedSessionRequestPrincipal?> AuthenticateAsync(
            string? selectedHandle,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(selectedHandle, MemberHandle, StringComparison.Ordinal)
                ? new SelectedSessionRequestPrincipal(
                    accountId: "account-serving-pipeline",
                    tenantId: new TenantId(TenantIdValue),
                    principalUserId: new PrincipalUserId("principal-serving-pipeline"),
                    canonicalParty: new CanonicalPartyReference(MemberParty),
                    membershipId: "membership-serving-pipeline",
                    membershipOwnerVersion: 3,
                    pinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-serving-pipeline", 4)],
                    authorizationEpoch: 7,
                    sessionCorrelationId: "session-serving-pipeline",
                    coordinationCorrelationId: "coordination-serving-pipeline")
                : null);
    }

    private sealed class NoTeamAccessor : IActiveTeamAccessor
    {
        public TeamContext? Active => null;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class AdvancingClock(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        internal void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }
}
