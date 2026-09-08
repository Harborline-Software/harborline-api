using Harborline.Api.Blocks.AccessGrant;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.IdentityAtlas.Enrollment;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Security.Keys;
using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.Kernel.Sync.Identity;
using Harborline.Api.LocalNodeHost.Data.Admission;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Data.Roster;
using Harborline.Api.LocalNodeHost.Data.Search;
using Harborline.Api.LocalNodeHost.Data.Search.Vector;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Search;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// MTW-2 #3167 — the web-admitted PAIRING enrollment over the REAL <see cref="AdmissionRoutes"/> listener. Proves:
/// <list type="bullet">
///   <item>THE E2E — a web-admitted member mints a device-pairing token (the real mint), and the device redeems it
///     end-to-end through the real <c>/admission/redeem</c> route → a signed atlas admission, no backdoor.</item>
///   <item>R2 mode-exclusivity — when web-plane admission is enabled for the tenant, a binding-ABSENT (plain) redeem
///     REFUSES opaque; the plain enrollment path is OFF (the "no backdoor" property).</item>
///   <item>the mode TOGGLE — with NO live web grant (web-plane disabled), the plain path admits (open posture).</item>
///   <item>F4-a — EVERY pairing / mode-exclusive refusal is byte-identical on the wire (HTTP status + body), so a
///     prober gets no content/status enumeration oracle.</item>
///   <item>R6/D — the durable token store's Redeem is atomic single-use under concurrency.</item>
/// </list>
/// </summary>
public sealed class PairingRedeemRouteTests : IAsyncLifetime
{
    private const string SessionToken = "test-pairing-session-token-abcd-1234";
    private static readonly Guid Team = Guid.Parse("7e57aaaa-0000-0000-0000-00000000000b");
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);

    private static readonly Harborline.Api.Kernel.Security.Crypto.Ed25519Signer TransportSigner = new();
    private static readonly ITeamSubkeyDerivation SubkeyDerivation = new TeamSubkeyDerivation(TransportSigner);

    private readonly List<string> _dirs = new();
    private readonly List<IAsyncDisposable> _async = new();
    private readonly List<ServiceProvider> _providers = new();
    private readonly List<SearchTestStore> _searchStores = new();
    private readonly List<SharedHostedWebApp> _apps = new();
    private readonly List<HttpClient> _clients = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        foreach (var a in _apps) { await a.StopAsync(CancellationToken.None); await a.DisposeAsync(); }
        foreach (var a in _async) await a.DisposeAsync();
        foreach (var s in _searchStores) await s.DisposeAsync();
        foreach (var p in _providers) await p.DisposeAsync();
        foreach (var d in _dirs)
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { /* best-effort */ }
    }

    // ── THE E2E: mint → redeem through the real route → admitted; the plain path is closed (no backdoor). ──

    [Fact(DisplayName = "distinct-seed web joiner: token Party and team anchor stay bound through commit")]
    public async Task DistinctSeedJoiner_PresentsWebBoundParty_ThroughRealJoinRoute()
    {
        var admitter = await ComposeAdmitterAsync(new FixedPartyReader("party-1"));
        var joinerProvider = await ComposeJoinerAsync(admitter.Dispatch);
        var joinerRoster = joinerProvider.GetRequiredService<NodeTeamRoster>();
        var joinerPartyId = joinerRoster.Current.GenesisPartyId;
        var join = joinerProvider.GetRequiredService<NodeEnrollmentJoinService>();
        var transport = Assert.IsType<PairingDispatchTransport>(
            joinerProvider.GetRequiredService<IEnrollmentTransport>());
        var token = admitter.Mint.MintForSession(
            SessionPrincipal(admitter.Tenant), admitter.Roster.Current, ttl: null).Token!;

        Assert.NotEqual(admitter.Roster.Current.GenesisPartyId, joinerPartyId);
        Assert.NotEqual("party-1", joinerPartyId);

        var forged = await join.JoinAsync(
            token.TokenId, "forged-party", token.Anchor, CancellationToken.None);
        Assert.False(forged.Succeeded);
        Assert.False(admitter.Roster.Current.Contains("forged-party"));

        var teamARoster = admitter.Roster.Current;
        var teamBRoster = MemberRoster.Genesis(
            Guid.Parse("7e57bbbb-0000-0000-0000-000000000099"),
            teamARoster.GenesisPartyId,
            admitter.FounderSigner,
            Verifier,
            Now,
            Guid.NewGuid());
        admitter.Roster.AdoptEnrollment(teamBRoster, new Dictionary<string, byte[]>());

        var wrongTeam = await join.JoinAsync(
            token.TokenId, "party-1", token.Anchor, CancellationToken.None);
        Assert.False(wrongTeam.Succeeded);
        Assert.Equal(teamBRoster.TeamId, admitter.Roster.Current.TeamId);
        Assert.False(admitter.Roster.Current.Contains("party-1"));

        // The anchor-scoped refusal is pre-decision: restoring team A lets the SAME live token admit.
        admitter.Roster.AdoptEnrollment(teamARoster, new Dictionary<string, byte[]>());

        var joined = await join.JoinAsync(
            token.TokenId, "party-1", token.Anchor, CancellationToken.None);
        Assert.True(joined.Succeeded, transport.LastOutcome?.InternalReason);
        Assert.True(admitter.Roster.Current.Contains("party-1"));
        Assert.True(joinerRoster.Current.Contains("party-1"));
        Assert.Contains(admitter.Roster.Current.EnumerateAdmissions(), row => row.PartyId == "party-1");
    }

    [Fact(DisplayName = "composed pairing: a team switch after anchor resolution is refused at atomic install")]
    public async Task TeamSwitch_AfterAnchorResolution_CannotOverwriteLiveRosterAtInstall()
    {
        var barrier = new GatedPartyReader("party-1");
        var admitter = await ComposeAdmitterAsync(barrier);
        var joinerProvider = await ComposeJoinerAsync(admitter.Dispatch);
        var joinerRoster = joinerProvider.GetRequiredService<NodeTeamRoster>();
        var joinerTeamBefore = joinerRoster.Current;
        var join = joinerProvider.GetRequiredService<NodeEnrollmentJoinService>();
        var token = admitter.Mint.MintForSession(
            SessionPrincipal(admitter.Tenant), admitter.Roster.Current, ttl: null).Token!;
        var publicationBefore = JsonSerializer.SerializeToUtf8Bytes(admitter.Projection.Snapshot());

        // The shipping admitter resolves token.Anchor before the bridge calls this gated party-reader seam.
        // Releasing it therefore drives the request from a resolved team-A decision into the atomic install.
        var pending = join.JoinAsync(
            token.TokenId, "party-1", token.Anchor, CancellationToken.None);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var teamBFounder = Member.New("team-b-founder");
        var teamBRoster = MemberRoster.Genesis(
            Guid.Parse("7e57bbbb-0000-0000-0000-0000000000aa"),
            teamBFounder.PartyId,
            teamBFounder.Signer,
            Verifier,
            Now,
            Guid.NewGuid());
        var teamBBytes = JsonSerializer.SerializeToUtf8Bytes(teamBRoster.EnumerateAdmissions());
        admitter.Roster.AdoptEnrollment(teamBRoster, new Dictionary<string, byte[]>());
        barrier.Release.TrySetResult(true);

        var outcome = await pending;

        Assert.False(outcome.Succeeded);
        Assert.Equal(teamBBytes, JsonSerializer.SerializeToUtf8Bytes(
            admitter.Roster.Current.EnumerateAdmissions()));
        Assert.False(admitter.Roster.Current.Contains("party-1"));
        Assert.Equal(publicationBefore, JsonSerializer.SerializeToUtf8Bytes(admitter.Projection.Snapshot()));
        Assert.Same(joinerTeamBefore, joinerRoster.Current);
    }

    [Fact(DisplayName = "pairing E2E: a web-admitted member mints a token, the device redeems it through the real route → admitted; plain path refused (no backdoor)")]
    public async Task WebAdmittedMember_Enrolls_A_Device_EndToEnd_NoBackdoor()
    {
        var h = await StartAsync(webPlaneEnabled: true);
        var device = Member.New("party-1"); // the enrolling device's atlas principal key; party = the web-plane party.

        // (1) The member (authenticated, session-derived) mints a device-pairing token — the real mint, bound pins.
        var mintOutcome = h.Mint.MintForSession(SessionPrincipal(h.Tenant), h.Roster.Current, ttl: null);
        Assert.True(mintOutcome.Minted);
        var tokenId = mintOutcome.Token!.TokenId;

        // (2) The device redeems it through the REAL /admission/redeem route (pairing path). X-Wing MANDATORY.
        var resp = await h.Client.SendAsync(
            Post($"{AdmissionRoutes.RouteBase}/redeem", PairingRedeemBody(tokenId, device, XWingKeyB64(3)), SessionToken));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var admissions = doc.RootElement.GetProperty("admissions").EnumerateArray()
            .Select(a => a.GetProperty("partyId").GetString()).ToArray();
        Assert.Contains("party-1", admissions);

        // The signed admission landed in the live roster + validates to genesis + carries the X-Wing key.
        Assert.True(h.Roster.Current.Contains("party-1"));
        Assert.True(h.Roster.Current.ValidatesToGenesis(Verifier));
        Assert.NotNull(h.Roster.Current.XWingPublicKeyOf("party-1"));

        // F3-b — the token is DURABLY redeemed (a replay of the SAME pairing token now refuses opaque).
        var replay = await h.Client.SendAsync(
            Post($"{AdmissionRoutes.RouteBase}/redeem", PairingRedeemBody(tokenId, device, XWingKeyB64(3)), SessionToken));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

        // (3) NO BACKDOOR — a PLAIN redeem (a fresh unbound invite, binding-absent) is REFUSED because web-plane
        //     admission is enabled for the tenant: the plain path is mode-exclusively OFF (R2).
        var plainDevice = Member.New("party-plain");
        var plainToken = h.Coordinator.CreateInvite(h.Anchor); // a real invite token — but no pairing binding.
        var plain = await h.Client.SendAsync(
            Post($"{AdmissionRoutes.RouteBase}/redeem", PlainRedeemBody(plainToken.TokenId, plainDevice), SessionToken));
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
        Assert.False(h.Roster.Current.Contains("party-plain"));
    }

    // ── R2 mode TOGGLE: with NO live web grant (web-plane disabled), the plain path admits (open posture). ──

    [Fact(DisplayName = "pairing: with web-plane admission DISABLED (no live grant), the plain enrollment path admits (mode toggle)")]
    public async Task WebPlane_Disabled_Plain_Path_Admits()
    {
        var h = await StartAsync(webPlaneEnabled: false);
        var bob = Member.New("bob");
        var token = h.Coordinator.CreateInvite(h.Anchor);
        var resp = await h.Client.SendAsync(
            Post($"{AdmissionRoutes.RouteBase}/redeem", PlainRedeemBody(token.TokenId, bob), SessionToken));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(h.Roster.Current.Contains("bob"));
    }

    [Fact(DisplayName = "pairing: a mode flip linearizes at the completed web-plane read (M4)")]
    public async Task Mode_Flip_Uses_Documented_Point_In_Time_Transition_Semantics()
    {
        var transition = new FirstReadDisabledThenEnabledState();
        var h = await StartAsync(webPlaneEnabled: false, webPlaneOverride: transition);

        // The fake commits the mode flip immediately before returning the first read's disabled snapshot. That
        // attempt has already linearized in plain mode and may finish; attempts whose read begins afterward refuse.
        var before = Member.New("transition-before");
        var beforeToken = h.Coordinator.CreateInvite(h.Anchor);
        var admitted = await h.Client.SendAsync(Post(
            $"{AdmissionRoutes.RouteBase}/redeem",
            PlainRedeemBody(beforeToken.TokenId, before),
            SessionToken));

        Assert.True(transition.Enabled);
        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.True(h.Roster.Current.Contains(before.PartyId));

        var after = Member.New("transition-after");
        var afterToken = h.Coordinator.CreateInvite(h.Anchor);
        var refused = await h.Client.SendAsync(Post(
            $"{AdmissionRoutes.RouteBase}/redeem",
            PlainRedeemBody(afterToken.TokenId, after),
            SessionToken));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False(h.Roster.Current.Contains(after.PartyId));
    }

    // ── R5(a): the limiter must be observed firing on the real 7473 admission handler. ──

    [Fact(DisplayName = "wire enrollment: second admission from a capped source is rejected before admission (R5-a)")]
    public async Task Wire_Enrollment_Rate_Limiter_Fires_On_The_Admission_Path()
    {
        var h = await StartAsync(
            webPlaneEnabled: false,
            rateLimiter: new PairingRedeemRateLimiter(perSourceMax: 1, perTenantMax: 10, clock: TimeProvider.System),
            startHttpListener: false);
        var first = Member.New("wire-first");
        var second = Member.New("wire-second");
        var firstToken = h.Coordinator.CreateInvite(h.Anchor);
        var secondToken = h.Coordinator.CreateInvite(h.Anchor);
        const string observedSource = "198.51.100.44";

        var admitted = await h.WireHandler.HandleAsync(
            EnrollmentWireCodec.EncodeRequest(PlainEnrollmentRequest(firstToken.TokenId, first)),
            observedSource,
            CancellationToken.None);
        var throttled = await h.WireHandler.HandleAsync(
            EnrollmentWireCodec.EncodeRequest(PlainEnrollmentRequest(secondToken.TokenId, second)),
            observedSource,
            CancellationToken.None);

        Assert.True(admitted.Accepted);
        Assert.False(throttled.Accepted);
        Assert.Equal("rate_limited", throttled.RejectReason);
        Assert.True(h.Roster.Current.Contains(first.PartyId));
        Assert.False(h.Roster.Current.Contains(second.PartyId));
    }

    [Fact(DisplayName = "pairing: loopback rate keys use the authenticated principal, not the shared loopback IP (L3)")]
    public void Loopback_Rate_Key_Is_Principal_Aware_And_Does_Not_Retain_The_Bearer()
    {
        var first = new DefaultHttpContext();
        first.Connection.RemoteIpAddress = IPAddress.Loopback;
        first.Request.Headers.Authorization = "Bearer caller-secret-a";

        var sameCaller = new DefaultHttpContext();
        sameCaller.Connection.RemoteIpAddress = IPAddress.Loopback;
        sameCaller.Request.Headers.Authorization = "Bearer caller-secret-a";

        var otherCaller = new DefaultHttpContext();
        otherCaller.Connection.RemoteIpAddress = IPAddress.Loopback;
        otherCaller.Request.Headers.Authorization = "Bearer caller-secret-b";

        var firstKey = AdmissionRoutes.PairingRateLimitSource(first);
        Assert.Equal(firstKey, AdmissionRoutes.PairingRateLimitSource(sameCaller));
        Assert.NotEqual(firstKey, AdmissionRoutes.PairingRateLimitSource(otherCaller));
        Assert.DoesNotContain("caller-secret", firstKey, StringComparison.Ordinal);

        var selected = new DefaultHttpContext();
        selected.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
        selected.Request.Headers.Authorization = "Bearer ignored-bootstrap";
        selected.Features.Set(SessionPrincipal(new TenantId("tenant-rate-key")));
        Assert.Equal(
            "principal:tenant-rate-key:principal-1",
            AdmissionRoutes.PairingRateLimitSource(selected));

        var remote = new DefaultHttpContext();
        remote.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.47");
        remote.Request.Headers.Authorization = "Bearer ignored-off-loopback";
        Assert.Equal("ip:198.51.100.47", AdmissionRoutes.PairingRateLimitSource(remote));
    }

    [Fact(DisplayName = "wire enrollment: live web-plane grant disables the unbound plain path (R2)")]
    public async Task Wire_Enrollment_Web_Plane_Predicate_Closes_The_Plain_Path()
    {
        var h = await StartAsync(webPlaneEnabled: true, startHttpListener: false);
        var joiner = Member.New("wire-plain-bypass");
        var token = h.Coordinator.CreateInvite(h.Anchor);

        var result = await h.WireHandler.HandleAsync(
            EnrollmentWireCodec.EncodeRequest(PlainEnrollmentRequest(token.TokenId, joiner)),
            "198.51.100.45",
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("enrollment_refused", result.RejectReason);
        Assert.False(h.Roster.Current.Contains(joiner.PartyId));
    }

    [Fact(DisplayName = "wire enrollment: orphan live grant fails closed and disables the unbound plain path")]
    public async Task Wire_Enrollment_Orphan_Live_Grant_Closes_The_Plain_Path()
    {
        var h = await StartAsync(
            webPlaneEnabled: true,
            startHttpListener: false,
            includeAuthorizationEpoch: false);
        var joiner = Member.New("wire-orphan-grant");
        var token = h.Coordinator.CreateInvite(h.Anchor);

        var result = await h.WireHandler.HandleAsync(
            EnrollmentWireCodec.EncodeRequest(PlainEnrollmentRequest(token.TokenId, joiner)),
            "198.51.100.46",
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("enrollment_refused", result.RejectReason);
        Assert.False(h.Roster.Current.Contains(joiner.PartyId));
    }

    // ── #258: the NETWORK handler's opaque refusal names its cause on the operator side (both branches). ──
    //
    // The loopback route already records the dispatch's InternalReason (AdmissionRoutes.TryPairingDispatchAsync →
    // RouteRejectMapping). The 7473 handler dropped it, so a cross-machine `enrollment_refused` named no cause on
    // any surface and the two branches that produce it could not be told apart. The WIRE stays opaque either way —
    // these assert the operator-side record only.

    [Fact(DisplayName = "wire enrollment: a web-plane refusal records plain_path_disabled on the operator side (#258)")]
    public async Task Wire_Enrollment_Refusal_Records_The_Plain_Path_Disabled_Reason()
    {
        var log = new CapturingLogger();
        var h = await StartAsync(
            webPlaneEnabled: true,
            startHttpListener: false,
            wireDiag: new Harborline.Api.LocalNodeHost.Diagnostics.CommsDiagnostics(log, enabled: true));
        var joiner = Member.New("wire-reason-plain-path");
        var token = h.Coordinator.CreateInvite(h.Anchor);

        var result = await h.WireHandler.HandleAsync(
            EnrollmentWireCodec.EncodeRequest(PlainEnrollmentRequest(token.TokenId, joiner)),
            "198.51.100.48",
            CancellationToken.None);

        // The wire is unchanged — still the single opaque refusal (no new leak).
        Assert.False(result.Accepted);
        Assert.Equal("enrollment_refused", result.RejectReason);

        var lines = string.Join("\n", log.Entries.Select(e => e.Message));
        Assert.Contains("plain_path_disabled", lines, StringComparison.Ordinal);
        Assert.Contains(joiner.PartyId, lines, StringComparison.Ordinal);
        Assert.DoesNotContain(token.TokenId, lines, StringComparison.Ordinal); // no token id on any surface
    }

    [Fact(DisplayName = "wire enrollment: a dispatch fault records pairing_dispatch_faulted:<type> on the operator side (#258)")]
    public async Task Wire_Enrollment_Refusal_Records_The_Dispatch_Fault_Reason()
    {
        var log = new CapturingLogger();
        var h = await StartAsync(
            webPlaneEnabled: true,
            routeBindingsOverride: new ThrowingBindingStore(),
            startHttpListener: false,
            wireDiag: new Harborline.Api.LocalNodeHost.Diagnostics.CommsDiagnostics(log, enabled: true));
        var joiner = Member.New("wire-reason-fault");
        var token = h.Coordinator.CreateInvite(h.Anchor);

        var result = await h.WireHandler.HandleAsync(
            EnrollmentWireCodec.EncodeRequest(PlainEnrollmentRequest(token.TokenId, joiner)),
            "198.51.100.49",
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("enrollment_refused", result.RejectReason);

        var lines = string.Join("\n", log.Entries.Select(e => e.Message));
        // The FAULT branch is distinguishable from the web-plane branch — that is the whole point.
        Assert.Contains("pairing_dispatch_faulted:InvalidOperationException", lines, StringComparison.Ordinal);
        Assert.DoesNotContain("plain_path_disabled", lines, StringComparison.Ordinal);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger : ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
    }

    // ── F4-a: every pairing / mode-exclusive refusal is byte-identical: status, headers, and body. ──

    [Fact(DisplayName = "pairing: all pairing/mode-exclusive refusals are byte-identical on the wire — no content/status oracle (F4-a)")]
    public async Task All_Pairing_Refusals_Are_Byte_Identical_On_The_Wire()
    {
        var h = await StartAsync(webPlaneEnabled: true);
        var device = Member.New("party-1");

        var refusals = new List<RefusalWireImage>();

        // (a) mode-exclusive refusal: a plain (binding-absent) redeem while web-plane is enabled.
        refusals.Add(await CollectAsync(h, PlainRedeemBody(h.Coordinator.CreateInvite(h.Anchor).TokenId, Member.New("p-a"))));

        // A bound pairing token for the following pairing-path refusals.
        var bound = h.Mint.MintForSession(SessionPrincipal(h.Tenant), h.Roster.Current, ttl: null).Token!;

        // (b) pairing PoP failure: X-Wing substituted after signing (signature no longer covers the transcript).
        refusals.Add(await CollectAsync(h, TamperedXWingBody(bound.TokenId, device)));

        // (c) pairing X-Wing ABSENT (mandatory) — a validly-signed request with no X-Wing key.
        var bound2 = h.Mint.MintForSession(SessionPrincipal(h.Tenant), h.Roster.Current, ttl: null).Token!;
        refusals.Add(await CollectAsync(h, PairingRedeemBody(bound2.TokenId, device, xwingB64: string.Empty)));

        // (d) pairing pin mismatch: the enrollment presents a DIFFERENT party than the token was bound to.
        var bound3 = h.Mint.MintForSession(SessionPrincipal(h.Tenant), h.Roster.Current, ttl: null).Token!;
        refusals.Add(await CollectAsync(h, PairingRedeemBody(bound3.TokenId, Member.New("party-other"), XWingKeyB64(4))));

        // Every refusal is byte-identical: same HTTP status, application-controlled headers, AND body bytes.
        var first = refusals[0];
        Assert.All(refusals, r => Assert.Equal(first.Status, r.Status));
        Assert.All(refusals, r => Assert.Equal(first.ContentType, r.ContentType));
        Assert.All(refusals, r => Assert.Equal(first.ContentLength, r.ContentLength));
        Assert.All(refusals, r => Assert.Equal(first.CacheControl, r.CacheControl));
        Assert.All(refusals, r => Assert.Equal(first.Body, r.Body));
        // Sanity: it IS the opaque refusal (not an accidental 200 / different shape).
        Assert.Equal(HttpStatusCode.BadRequest, first.Status);
        Assert.Equal("application/json; charset=utf-8", first.ContentType);
        Assert.Equal("no-store", first.CacheControl);
        Assert.Contains("enrollment_refused", first.Body);
    }

    [Fact(DisplayName = "pairing: missing_field and 429 remain the only sanctioned divergences from opaque refusal (L2)")]
    public async Task Validation_And_Throttle_Responses_Are_Explicit_Divergences_From_Opaque_Refusal()
    {
        var h = await StartAsync(
            webPlaneEnabled: true,
            rateLimiter: new PairingRedeemRateLimiter(perSourceMax: 1, perTenantMax: 10, clock: TimeProvider.System));

        var missing = await CollectAsync(h, new { joiningPartyId = "missing-token-and-keys" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Contains("missing_field", missing.Body, StringComparison.Ordinal);

        var opaque = await CollectAsync(
            h,
            PlainRedeemBody(h.Coordinator.CreateInvite(h.Anchor).TokenId, Member.New("opaque-first")));
        Assert.Equal(HttpStatusCode.BadRequest, opaque.Status);
        Assert.Contains("enrollment_refused", opaque.Body, StringComparison.Ordinal);

        var throttled = await CollectAsync(
            h,
            PlainRedeemBody(h.Coordinator.CreateInvite(h.Anchor).TokenId, Member.New("throttled-second")));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.Status);

        Assert.NotEqual(opaque.Body, missing.Body);
        Assert.NotEqual(opaque.Status, throttled.Status);
        Assert.DoesNotContain("enrollment_refused", throttled.Body, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "pairing: canonical opaque refusal fixes status, headers, and body together (F4-a)")]
    public async Task Canonical_Opaque_Refusal_Fixes_The_Complete_Wire_Image()
    {
        var images = new List<RefusalWireImage>();
        for (var i = 0; i < 4; i++)
        {
            var services = new ServiceCollection().AddOptions().AddLogging().BuildServiceProvider();
            _providers.Add(services);
            var context = new DefaultHttpContext { RequestServices = services };
            await using var body = new MemoryStream();
            context.Response.Body = body;

            await OpaqueEnrollmentRefusalResult.Instance.ExecuteAsync(context);
            body.Position = 0;
            using var reader = new StreamReader(body);
            images.Add(new RefusalWireImage(
                (HttpStatusCode)context.Response.StatusCode,
                context.Response.ContentType,
                context.Response.ContentLength,
                context.Response.Headers.CacheControl.ToString(),
                await reader.ReadToEndAsync()));
        }

        var expected = images[0];
        Assert.All(images, image => Assert.Equal(expected, image));
        Assert.Equal(HttpStatusCode.BadRequest, expected.Status);
        Assert.Equal("application/json; charset=utf-8", expected.ContentType);
        Assert.Equal("no-store", expected.CacheControl);
        Assert.Equal("{\"error\":\"enrollment_refused\"}", expected.Body);
    }

    // ── R6/D: the durable token store's Redeem is atomic single-use under concurrency. ──

    [Fact(DisplayName = "durable token store: N concurrent redeems of one token — exactly ONE succeeds (atomic CAS single-use, R6/D)")]
    public async Task Durable_Token_Store_Redeem_Is_Atomic_Single_Use_Under_Concurrency()
    {
        var admissionFactory = NewAdmissionFactory();
        await using (var ctx = await admissionFactory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();
        // Two independent stores model two host processes sharing the same database. A process-local lock cannot
        // make this test pass; the UPDATE ... WHERE redeemed = 0 row-count CAS must arbitrate the winner.
        var stores = new[]
        {
            new DurableAdmissionTokenStore(admissionFactory),
            new DurableAdmissionTokenStore(admissionFactory),
        };

        var anchor = new TeamTrustAnchor(Team.ToString("D"), "founder", new string('A', 43));
        var token = AdmissionToken.Mint(anchor, Now, TimeSpan.FromMinutes(30));
        stores[0].Issue(token);

        // Fire 32 concurrent redeems of the SAME token id. Exactly one must be Accepted; the rest AlreadyRedeemed.
        var tasks = Enumerable.Range(0, 32)
            .Select(i => Task.Run(() => stores[i % stores.Length].Redeem(token.TokenId, Now)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Accepted));
        Assert.Equal(31, results.Count(r => r.Outcome == RedeemOutcome.AlreadyRedeemed));
    }

    [Fact(DisplayName = "pairing route: two concurrent full redeems of one token admit exactly once (L1)")]
    public async Task Full_Route_Concurrent_Redeem_Admits_Exactly_Once()
    {
        var h = await StartAsync(webPlaneEnabled: true);
        var device = Member.New("party-1");
        var token = h.Mint.MintForSession(
            SessionPrincipal(h.Tenant), h.Roster.Current, ttl: null).Token!;
        var body = PairingRedeemBody(token.TokenId, device, XWingKeyB64(9));

        var responses = await Task.WhenAll(
            h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem", body, SessionToken)),
            h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem", body, SessionToken)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.BadRequest));
        Assert.True(h.Roster.Current.Contains(device.PartyId));
        Assert.Single(h.Roster.Current.Members, m => m.PartyId == device.PartyId);
    }

    // ── M3: a durable-store fault in the route's OWN pre-admitter calls is contained to the opaque 400. ──

    [Fact(DisplayName = "pairing: a binding-store fault in the route pre-dispatch is contained to an opaque 400 — never a distinguishable 500 (verdict M3)")]
    public async Task Route_Level_Binding_Store_Fault_Is_Contained_To_Opaque_400()
    {
        var device = Member.New("party-1");

        // A REFERENCE opaque refusal from a HEALTHY harness (mode-exclusive: a plain redeem while web-plane enabled).
        var healthy = await StartAsync(webPlaneEnabled: true);
        var reference = await healthy.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem",
            PlainRedeemBody(healthy.Coordinator.CreateInvite(healthy.Anchor).TokenId, Member.New("p-ref")), SessionToken));
        var refStatus = reference.StatusCode;
        var refBody = await reference.Content.ReadAsStringAsync();

        // A harness whose ROUTE binding-store Lookup THROWS (models a locked SQLite / partial-migration missing-table
        // fault — the SqliteException M3 flags that the store's own JsonException catch does NOT cover).
        var faulted = await StartAsync(webPlaneEnabled: true, routeBindingsOverride: new ThrowingBindingStore());
        var resp = await faulted.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem",
            PairingRedeemBody(Guid.NewGuid().ToString("N"), device, XWingKeyB64(3)), SessionToken));

        // NOT a 500 — the throw is contained; and byte-identical (status + body) to the healthy opaque refusal, so a
        // durable fault leaks no distinguishable oracle.
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(refStatus, resp.StatusCode);
        Assert.Equal(refBody, await resp.Content.ReadAsStringAsync());
        Assert.Contains("enrollment_refused", refBody);
    }

    private sealed class ThrowingBindingStore : IWebPairingInviteBindingStore
    {
        public void Bind(WebPairingInviteBinding binding) =>
            throw new InvalidOperationException("induced binding-store fault");
        public WebPairingInviteBinding? Lookup(string tokenId) =>
            throw new InvalidOperationException("induced binding-store fault");
    }

    // ── Harness ──────────────────────────────────────────────────────────────────────────────────────────

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
        public HttpClient Client =>
            _client ?? throw new InvalidOperationException("This harness was created without an HTTP listener.");
        internal HttpClient? _client;
        public required NodeTeamRoster Roster { get; init; }
        public required AdmissionCoordinator Coordinator { get; init; }
        public required WebAdmittedMemberPairingTokenMint Mint { get; init; }
        public required Member Founder { get; init; }
        public required TeamTrustAnchor Anchor { get; init; }
        public required TenantId Tenant { get; init; }
        public required NodeEnrollmentAdmitter WireHandler { get; init; }
    }

    private async Task<Harness> StartAsync(
        bool webPlaneEnabled,
        IWebPairingInviteBindingStore? routeBindingsOverride = null,
        PairingRedeemRateLimiter? rateLimiter = null,
        bool startHttpListener = true,
        bool includeAuthorizationEpoch = true,
        IWebPlaneAdmissionState? webPlaneOverride = null,
        Harborline.Api.LocalNodeHost.Diagnostics.CommsDiagnostics? wireDiag = null)
    {
        var founder = Member.New("founder");
        var genesis = MemberRoster.Genesis(Team, founder.PartyId, founder.Signer, Verifier, Now, Guid.NewGuid());
        var roster = new NodeTeamRoster(genesis);

        // Roster projection (CRDT + SQLite).
        var rosterFactory = NewRosterFactory(out var crdtSp);
        _providers.Add(crdtSp);
        await using (var ctx = await rosterFactory.CreateDbContextAsync()) await ctx.Database.EnsureCreatedAsync();
        var projection = new RosterCrdtProjection(TimeProvider.System,
            crdtSp.GetRequiredService<ICrdtEngine>(), rosterFactory, Verifier,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RosterCrdtProjection>.Instance, roster);
        _async.Add(projection);

        // Durable admission db (token store + pairing-binding store).
        var admissionFactory = NewAdmissionFactory();
        await using (var ctx = await admissionFactory.CreateDbContextAsync()) await ctx.Database.EnsureCreatedAsync();
        var tokenStore = new DurableAdmissionTokenStore(admissionFactory);
        var bindings = new DurableWebPairingInviteBindingStore(admissionFactory);
        var coordinator = new AdmissionCoordinator(Verifier, tokenStore, new FixedTimeProvider(Now));

        // Active team (team-scoped identity for the bootstrap response) + the projected tenant.
        var teamCollection = new ServiceCollection();
        teamCollection.AddSingleton<INodeIdentityProvider>(new InMemoryNodeIdentityProvider(DeriveTeamScopedIdentity(Team)));
        var teamServices = teamCollection.BuildServiceProvider();
        _providers.Add(teamServices);
        var teamContext = new TeamContext(new TeamId(Team), "Test Team", teamServices, TimeProvider.System);
        var activeTeam = new FakeActiveTeamAccessor(teamContext);
        var teamContexts = new FixedTeamContextFactory(teamContext);
        var tenant = ActiveTeamTenantContext.ProjectTenantId(teamContext.TeamId);

        // Grant store — ONE live grant iff web-plane is to be ENABLED (the R2 predicate + the bridge pin both read it).
        var search = await SearchTestStore.CreateAsync();
        _searchStores.Add(search);
        if (webPlaneEnabled)
        {
            await using var ctx = search.CreateContext();
            ctx.Grants.Add(new GrantRow
            {
                GrantId = "grant-1", TenantId = tenant.Value, SubjectId = "principal-1",
                RoleVocabulary = AccessGrantAuthorizationSeed.MemberRole.Vocabulary,
                RoleName = AccessGrantAuthorizationSeed.MemberRole.Name, ScopeValue = "/", Residency = 0,
                ValidityFromUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), GrantedBy = "issuer-1",
                GrantedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), GranterKind = (int)GranterKind.Person,
                Source = (int)GrantSourceKind.Manual, Approver = "issuer-1",
                LastReviewedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
                RevokedAtUnixMs = null, OwnerVersion = 4,
            });
            if (includeAuthorizationEpoch)
            {
                ctx.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
                {
                    TenantId = tenant.Value, PrincipalId = "principal-1", AuthorizationEpoch = 7,
                });
            }
            await ctx.SaveChangesAsync();
        }

        // The pairing bundle (bridge + gated admitter + mint + web-plane predicate + rate limiter).
        var bridge = new WebAdmittedMemberAtlasBridge(
            new FixedPartyReader("party-1"), search.Factory, coordinator, bindings, new FixedTimeProvider(Now),
            new Harborline.Api.LocalNodeHost.Tests.Identity.FixedAuthorizationClosure());
        var mint = new WebAdmittedMemberPairingTokenMint(coordinator, bindings);
        var gated = new PairingTokenGatedAdmitter(
            bridge, roster, founder.Signer, founder.PartyId, Verifier, projection,
            NullEnrollmentCompensatingControlRecorder.Instance, bindings, teamContexts, diag: null);
        var webPlane = webPlaneOverride
            ?? new LiveWebPlaneAdmissionState(search.Factory, new FixedTimeProvider(Now));
        // The route's binding lookup uses routeBindingsOverride when supplied (to induce a durable fault at the
        // route pre-dispatch layer for the M3 containment test); the bridge + mint keep the real durable store.
        var pairing = new PairingRedeemDispatch(
            gated, routeBindingsOverride ?? bindings, webPlane,
            rateLimiter ?? new PairingRedeemRateLimiter(clock: TimeProvider.System), activeTeam);
        var wireAdmitter = new WireEnrollmentAdmitter(
            coordinator, roster, founder.Signer, founder.PartyId, Verifier, projection,
            NullEnrollmentCompensatingControlRecorder.Instance, activeTeam);
        var wireHandler = new NodeEnrollmentAdmitter(wireAdmitter, pairing, logger: null, diag: wireDiag);

        HttpClient? client = null;
        if (startHttpListener)
        {
            // The real listener.
            var outer = new ServiceCollection();
            outer.AddTestKernelClock();
            outer.AddLogging();
            outer.AddSingleton<IActiveTeamAccessor>(activeTeam);
            outer.AddSingleton(new NodeCallerSessionToken(SessionToken));
            var outerProvider = outer.BuildServiceProvider();
            _providers.Add(outerProvider);

            var options = Options.Create(new LocalNodeOptions { HealthPort = 0 });
            var logger = outerProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>();
            var app = new SharedHostedWebApp(
                outerProvider, options,
                new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(), logger,
                outerProvider.GetRequiredService<TimeProvider>());
            _apps.Add(app);
            app.MapApiRoutes(a => AdmissionRoutes.Map(
                a, coordinator, roster, founder.Signer, founder.PartyId, Verifier, projection,
                NullEnrollmentCompensatingControlRecorder.Instance, pairing));
            await app.StartAsync(CancellationToken.None);
            client = new HttpClient
            {
                BaseAddress = new Uri(app.SelectedUrl!),
                Timeout = TimeSpan.FromSeconds(10),
            };
            _clients.Add(client);
        }

        return new Harness
        {
            _client = client, Roster = roster, Coordinator = coordinator, Mint = mint,
            Founder = founder,
            Anchor = TeamTrustAnchor.FromRoster(genesis), Tenant = tenant, WireHandler = wireHandler,
        };
    }

    private sealed class PairingDispatchTransport(IEnrollmentRedeemDispatch dispatch) : IEnrollmentTransport
    {
        public PairingDispatchOutcome? LastOutcome { get; private set; }

        public async Task<EnrollmentResponse?> SendAsync(EnrollmentRequest request, CancellationToken ct)
        {
            var outcome = LastOutcome = await dispatch.DispatchAsync(request, "distinct-seed-joiner", ct);
            return outcome.Kind == PairingDispatchKind.Accepted ? outcome.Response : null;
        }
    }

    private sealed record ComposedAdmitter(
        NodeTeamRoster Roster,
        WebAdmittedMemberPairingTokenMint Mint,
        PairingRedeemDispatch Dispatch,
        RosterCrdtProjection Projection,
        IOperationSigner FounderSigner,
        TenantId Tenant);

    private async Task<ComposedAdmitter> ComposeAdmitterAsync(ICanonicalPrincipalPartyReader partyReader)
    {
        var provider = await ComposeNodeAsync(
            "admitter",
            '3',
            services =>
            {
                services.RemoveAll<ICanonicalPrincipalPartyReader>();
                services.AddSingleton(partyReader);
                services.RemoveAll<Harborline.Api.Blocks.AccessGrant.IAuthorizationClosureReader>();
                services.AddSingleton<Harborline.Api.Blocks.AccessGrant.IAuthorizationClosureReader>(
                    new Harborline.Api.LocalNodeHost.Tests.Identity.FixedAuthorizationClosure());
            });

        var admissionFactory = provider.GetRequiredService<IDbContextFactory<NodeLocalAdmissionDbContext>>();
        await using (var context = await admissionFactory.CreateDbContextAsync())
            await context.Database.MigrateAsync();
        var rosterFactory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var context = await rosterFactory.CreateDbContextAsync())
            await context.Database.MigrateAsync();
        var searchFactory = provider.GetRequiredService<IDbContextFactory<NodeLocalSearchDbContext>>();
        await using (var context = await searchFactory.CreateDbContextAsync())
            await context.Database.MigrateAsync();

        var roster = provider.GetRequiredService<NodeTeamRoster>();
        await provider.GetRequiredService<ITeamContextFactory>().GetOrCreateAsync(
            new TeamId(roster.Current.TeamId), "Composed Admitter", CancellationToken.None);
        await provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<global::Harborline.Api.LocalNodeHost.RosterSyncBootstrapHostedService>()
            .Single()
            .StartAsync(CancellationToken.None);
        var tenant = ActiveTeamTenantContext.ProjectTenantId(new TeamId(roster.Current.TeamId));
        await using (var context = await searchFactory.CreateDbContextAsync())
        {
            context.Grants.Add(new GrantRow
            {
                GrantId = "grant-1", TenantId = tenant.Value, SubjectId = "principal-1",
                RoleVocabulary = AccessGrantAuthorizationSeed.MemberRole.Vocabulary,
                RoleName = AccessGrantAuthorizationSeed.MemberRole.Name, ScopeValue = "/", Residency = 0,
                ValidityFromUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), GrantedBy = "issuer-1",
                GrantedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(), GranterKind = (int)GranterKind.Person,
                Source = (int)GrantSourceKind.Manual, Approver = "issuer-1",
                LastReviewedAtUnixMs = Now.AddMinutes(-1).ToUnixTimeMilliseconds(),
                RevokedAtUnixMs = null, OwnerVersion = 4,
            });
            context.GrantAuthorizationEpochs.Add(new GrantAuthorizationEpochRow
            {
                TenantId = tenant.Value, PrincipalId = "principal-1", AuthorizationEpoch = 7,
            });
            await context.SaveChangesAsync();
        }

        return new ComposedAdmitter(
            roster,
            provider.GetRequiredService<WebAdmittedMemberPairingTokenMint>(),
            provider.GetRequiredService<PairingRedeemDispatch>(),
            provider.GetRequiredService<RosterCrdtProjection>(),
            provider.GetRequiredService<Harborline.Api.LocalNodeHost.Health.NodePrincipalSigner>().Signer,
            tenant);
    }

    private async Task<IServiceProvider> ComposeJoinerAsync(IEnrollmentRedeemDispatch dispatch)
    {
        var provider = await ComposeNodeAsync(
            "joiner",
            '4',
            services =>
            {
                services.RemoveAll<IEnrollmentTransport>();
                services.AddSingleton<IEnrollmentTransport>(new PairingDispatchTransport(dispatch));
            });
        var rosterFactory = provider.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
        await using (var context = await rosterFactory.CreateDbContextAsync())
            await context.Database.MigrateAsync();
        var roster = provider.GetRequiredService<NodeTeamRoster>();
        await provider.GetRequiredService<ITeamContextFactory>().GetOrCreateAsync(
            new TeamId(roster.Current.TeamId), "Composed Joiner", CancellationToken.None);
        await provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<global::Harborline.Api.LocalNodeHost.RosterSyncBootstrapHostedService>()
            .Single()
            .StartAsync(CancellationToken.None);
        return provider;
    }

    private async Task<IServiceProvider> ComposeNodeAsync(
        string role,
        char rootSeedDigit,
        Action<IServiceCollection>? finalServiceRegistration)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"s239-composed-{role}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        _dirs.Add(dataDirectory);
        IServiceProvider? provider = null;

        await Assert.ThrowsAsync<CompositionProbeCompleteException>(() =>
            global::LocalNodeHostComposition.RunAsync(
                [
                    "--LocalNode:RootSeedHex=" + new string(rootSeedDigit, 64),
                    "--LocalNode:Diagnostics:CommsDiagnosticLogging=false",
                    "--Logging:EventLog:LogLevel:Default=None",
                ],
                sessionTokenOverride: SessionToken,
                dataDirectory: dataDirectory,
                kernelClock: new FixedTimeProvider(Now),
                finalServiceRegistration: finalServiceRegistration,
                installFootprintRootOverride: dataDirectory,
                finalServiceProviderProbe: (services, factory) =>
                {
                    provider = factory.CreateServiceProvider(factory.CreateBuilder(services));
                    throw new CompositionProbeCompleteException();
                }));

        Assert.NotNull(provider);
        _async.Add(Assert.IsAssignableFrom<IAsyncDisposable>(provider));
        return provider;
    }

    private sealed class CompositionProbeCompleteException : Exception;

    private static SelectedSessionRequestPrincipal SessionPrincipal(TenantId tenant) => new(
        accountId: "account-1",
        tenantId: tenant,
        principalUserId: new PrincipalUserId("principal-1"),
        canonicalParty: new CanonicalPartyReference("party-1"),
        membershipId: "membership-1",
        membershipOwnerVersion: 3,
        pinnedGrantOwnerVersions: new[] { new PinnedGrantOwnerVersion("grant-1", 4) },
        authorizationEpoch: 7,
        sessionCorrelationId: "session-corr-1",
        coordinationCorrelationId: "coordination-1");

    private sealed record RefusalWireImage(
        HttpStatusCode Status,
        string? ContentType,
        long? ContentLength,
        string? CacheControl,
        string Body);

    private static async Task<RefusalWireImage> CollectAsync(Harness h, object body)
    {
        var resp = await h.Client.SendAsync(Post($"{AdmissionRoutes.RouteBase}/redeem", body, SessionToken));
        return new RefusalWireImage(
            resp.StatusCode,
            resp.Content.Headers.ContentType?.ToString(),
            resp.Content.Headers.ContentLength,
            resp.Headers.CacheControl?.ToString(),
            await resp.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Post(string path, object body, string bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    private static object PairingRedeemBody(string tokenId, Member joiner, string xwingB64)
    {
        var request = WireEnrollment.BuildRequest(
            tokenId, joiner.PartyId, FreshKey(), joiner.Signer, Now, Guid.NewGuid(),
            joiningDmPublicKey: FreshKey(), joiningXWingPublicKey: xwingB64);
        return ToBody(request);
    }

    // A pairing body whose X-Wing key is SWAPPED after signing (PoP failure — the signature no longer covers it).
    private static object TamperedXWingBody(string tokenId, Member joiner)
    {
        var request = WireEnrollment.BuildRequest(
            tokenId, joiner.PartyId, FreshKey(), joiner.Signer, Now, Guid.NewGuid(),
            joiningDmPublicKey: FreshKey(), joiningXWingPublicKey: XWingKeyB64(3));
        return ToBody(request with { JoiningXWingPublicKey = XWingKeyB64(88) });
    }

    private static object PlainRedeemBody(string tokenId, Member joiner)
    {
        return ToBody(PlainEnrollmentRequest(tokenId, joiner));
    }

    private static EnrollmentRequest PlainEnrollmentRequest(string tokenId, Member joiner) =>
        WireEnrollment.BuildRequest(
            tokenId, joiner.PartyId, FreshKey(), joiner.Signer, Now, Guid.NewGuid());

    private static object ToBody(EnrollmentRequest request) => new
    {
        tokenId = request.TokenId,
        joiningPartyId = request.JoiningPartyId,
        joiningPublicKey = request.JoiningPrincipalPublicKey,
        joiningTransportKey = request.JoiningTransportPublicKey,
        signature = request.Signature,
        issuedAtUnixMs = request.IssuedAt.ToUnixTimeMilliseconds(),
        nonce = request.Nonce.ToString(),
        joiningDmKey = request.JoiningDmPublicKey,
        joiningXWingKey = request.JoiningXWingPublicKey,
    };

    private static byte[] FreshKey()
    {
        var k = new byte[Harborline.Api.Foundation.Crypto.PrincipalId.LengthInBytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(k);
        return k;
    }

    private static string XWingKeyB64(byte seed)
    {
        var bytes = new byte[1216];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static NodeIdentity DeriveTeamScopedIdentity(Guid teamId)
    {
        var rootSeed = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(rootSeed);
        var (rootPub, rootPriv) = TransportSigner.GenerateFromSeed(rootSeed);
        var nodeId = Convert.ToHexString(rootPub.AsSpan(0, 16)).ToLowerInvariant();
        var root = new NodeIdentity(nodeId, rootPub, rootPriv);
        return TeamScopedNodeIdentity.Derive(root, teamId.ToString("D"), SubkeyDerivation);
    }

    private IDbContextFactory<NodeLocalAdmissionDbContext> NewAdmissionFactory()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-pairing-route-adm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var services = new ServiceCollection();
        services.AddDbContextFactory<NodeLocalAdmissionDbContext>(
            opt => opt.UseSqlite($"Data Source={Path.Combine(dir, "admission.db")};Pooling=False"));
        var sp = services.BuildServiceProvider();
        _providers.Add(sp);
        return sp.GetRequiredService<IDbContextFactory<NodeLocalAdmissionDbContext>>();
    }

    private IDbContextFactory<NodeLocalRosterDbContext> NewRosterFactory(out ServiceProvider sp)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"harborline-pairing-route-roster-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalRosterDbContext>(
            opt => opt.UseSqlite($"Data Source={Path.Combine(dir, "roster.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
    }

    private sealed class FixedPartyReader(string partyId) : ICanonicalPrincipalPartyReader
    {
        public ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<CanonicalPartyBinding?>(
                new CanonicalPartyBinding(tenant, user, new CanonicalPartyReference(partyId)));
    }

    private sealed class GatedPartyReader(string partyId) : ICanonicalPrincipalPartyReader
    {
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<CanonicalPartyBinding?> ResolveAsync(
            TenantId tenant, PrincipalUserId user, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new CanonicalPartyBinding(tenant, user, new CanonicalPartyReference(partyId));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FirstReadDisabledThenEnabledState : IWebPlaneAdmissionState
    {
        private int _reads;
        internal bool Enabled { get; private set; }

        public Task<bool> IsEnabledAsync(TenantId tenant, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                Enabled = true;
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        }
    }

    private sealed class FakeActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }

    private sealed class FixedTeamContextFactory(TeamContext context) : ITeamContextFactory
    {
        public IReadOnlyCollection<TeamContext> Active { get; } = [context];
        public Task<TeamContext> GetOrCreateAsync(TeamId teamId, string displayName, CancellationToken ct) =>
            Task.FromResult(context);
        public Task RemoveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
    }
}
