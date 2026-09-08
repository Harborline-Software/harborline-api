using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.Kernel.Sync.Protocol;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// Route-level tests for the node-local sync-status surface
/// (<see cref="SyncStatusRoutes"/>) — the Phase-A four-state multi-device sync
/// display read-model. Hosts the SAME route handler
/// <see cref="HostedSyncStatusApiEndpoint"/> registers, on a real in-process
/// Kestrel listener, and drives it with a real <see cref="HttpClient"/> to pin
/// the shared wire contract FED's Harborline App UI consumes.
/// </summary>
/// <remarks>
/// The read-model is team-scoped, so the route resolves it per-request from a
/// <see cref="FakeActiveTeamAccessor"/> whose <see cref="TeamContext"/> exposes a
/// real <see cref="SyncStatusReadModel"/> over a controllable
/// <see cref="FakeGossipDaemon"/> — the same isolation the unit tests use,
/// exercised end-to-end over HTTP. <see cref="SyncStatusRoutes.Map"/> is the
/// production registration helper (single source of truth, no test/prod drift).
/// </remarks>
public sealed class SyncStatusRouteTests : IAsyncLifetime
{
    private static readonly TeamId TestTeamId = new(new Guid("22222222-2222-2222-2222-222222222222"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private FakeGossipDaemon _daemon = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        // A real read-model over a controllable daemon, hosted in a team context.
        _daemon = new FakeGossipDaemon();
        var readModel = new SyncStatusReadModel(
            _daemon, Options.Create(new GossipDaemonOptions { RoundIntervalSeconds = 30 }), timeProvider: TimeProvider.System);

        var teamServices = new ServiceCollection();
        teamServices.AddSingleton<ISyncStatusReadModel>(readModel);
        var team = new TeamContext(TestTeamId, "Test Team", teamServices.BuildServiceProvider(), TimeProvider.System);
        var accessor = new FakeActiveTeamAccessor(team);

        // Map the SAME production route the hosted endpoint registers.
        SyncStatusRoutes.Map(_app, accessor, TimeProvider.System);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task GET_sync_status_returns_the_contract_shape_for_a_mixed_fleet()
    {
        // One HAS, one SHOULD (in backoff), one security-COULDN'T.
        _daemon.SetPeer(new PeerInfo("good", Key(0x01), DateTimeOffset.UtcNow.AddSeconds(-3), 0));
        _daemon.SetPeer(new PeerInfo("waiting", Key(0x02), DateTimeOffset.UtcNow.AddMinutes(-5), 0,
            LastSeenNonce: 0, ConsecutiveFailures: 1, BackoffUntil: DateTimeOffset.UtcNow.AddSeconds(30)));
        _daemon.SetPeer(new PeerInfo("intruder", Key(0x03), DateTimeOffset.MinValue, 0));
        _daemon.RaiseFrame(new GossipFrameEventArgs(
            "good", "01010101", GossipFrameType.GossipPing, DateTimeOffset.UtcNow));
        _daemon.RaiseFrame(new GossipFrameEventArgs(
            "intruder", "03030303", GossipFrameType.HandshakeFailure, DateTimeOffset.UtcNow,
            ErrorCode: ErrorCode.PeerUntrusted));
        _daemon.RaiseRound(new GossipRoundCompletedEventArgs(3, 1, 1));

        var response = await _client.GetAsync(SyncStatusRoutes.RouteBase);
        Assert.True(response.IsSuccessStatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // Fleet aggregate — worst-state-wins → couldnt. Lowercase wire token.
        Assert.Equal("couldnt", root.GetProperty("aggregate").GetString());

        // Observer "as of" honesty stamp present + parseable (ISO-8601).
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("asOf").GetString(), out _));

        // WILL cadence shape: { nextRoundAt, roundIntervalSeconds }.
        var cadence = root.GetProperty("cadence");
        Assert.Equal(30, cadence.GetProperty("roundIntervalSeconds").GetInt32());
        Assert.True(DateTimeOffset.TryParse(cadence.GetProperty("nextRoundAt").GetString(), out _));

        // Per-peer rows — each carries the full contract shape.
        var peers = root.GetProperty("peers").EnumerateArray().ToList();
        Assert.Equal(3, peers.Count);
        foreach (var peer in peers)
        {
            Assert.True(peer.TryGetProperty("deviceId", out _));
            Assert.True(peer.TryGetProperty("label", out _));
            Assert.True(peer.TryGetProperty("state", out _));
            Assert.True(peer.TryGetProperty("lastReachedAt", out _));
            Assert.True(peer.TryGetProperty("offlineDurationMs", out _));
            Assert.True(peer.TryGetProperty("isSecurityEvent", out _));
            Assert.True(peer.TryGetProperty("errorCode", out _));
        }

        // The security lane reads DISTINCTLY: isSecurityEvent + PEER_UNTRUSTED.
        var security = Assert.Single(peers, p => p.GetProperty("isSecurityEvent").GetBoolean());
        Assert.Equal("couldnt", security.GetProperty("state").GetString());
        Assert.Equal("PEER_UNTRUSTED", security.GetProperty("errorCode").GetString());

        // A HAS peer carries a lastReachedAt + raw offlineDurationMs, no security flag.
        var has = Assert.Single(peers, p => p.GetProperty("state").GetString() == "has");
        Assert.False(has.GetProperty("isSecurityEvent").GetBoolean());
        Assert.False(has.GetProperty("lastReachedAt").ValueKind == JsonValueKind.Null);
        Assert.False(has.GetProperty("offlineDurationMs").ValueKind == JsonValueKind.Null);

        // A SHOULD peer (waiting) is present and is NOT a security event.
        Assert.Single(peers, p =>
            p.GetProperty("state").GetString() == "should"
            && !p.GetProperty("isSecurityEvent").GetBoolean());
    }

    [Fact]
    public async Task GET_sync_status_returns_a_calm_empty_snapshot_for_a_solo_node()
    {
        // No peers, no events → HAS aggregate, empty peers, still stamped.
        var response = await _client.GetAsync(SyncStatusRoutes.RouteBase);
        Assert.True(response.IsSuccessStatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal("has", root.GetProperty("aggregate").GetString());
        Assert.Empty(root.GetProperty("peers").EnumerateArray());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("asOf").GetString(), out _));
    }

    /// <summary>
    /// POST-JOIN-TRIGGER SURVIVAL (the Harborline App's "node GET /api/local-node/sync-status failed: 500"). After a
    /// wire-enrollment JOIN switches the active team OFF the join's call stack, the Harborline App polls this route
    /// immediately. Resolving the team-scoped <see cref="ISyncStatusReadModel"/> from the adopted team's child
    /// container — or projecting its <c>Snapshot()</c> — can THROW in that transient window (a still-constructing
    /// adopted-team daemon, or a racing rebind disposing a child provider ⇒ <see cref="ObjectDisposedException"/>;
    /// a faulting ctor PROPAGATES — <c>GetService</c> returns null ONLY for an UNREGISTERED service). The route MUST
    /// degrade to a calm 200 snapshot, NEVER a 500 that masks whether the join converged.
    /// </summary>
    [Fact]
    public async Task GET_sync_status_survives_a_read_model_resolution_that_throws_post_join()
    {
        // A team whose ISyncStatusReadModel resolution THROWS — exactly the post-join-trigger / mid-rebind window.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        var teamServices = new ServiceCollection();
        // Mirror the production registration: a factory whose construction faults (a still-building adopted-team
        // daemon / a disposed child provider). GetService PROPAGATES this — it is NOT swallowed to null.
        teamServices.AddSingleton<ISyncStatusReadModel>(_ =>
            throw new InvalidOperationException("adopted-team daemon still constructing (post-join transient)"));
        var team = new TeamContext(
            new TeamId(new Guid("33333333-3333-3333-3333-333333333333")),
            "Adopting Team",
            teamServices.BuildServiceProvider(), TimeProvider.System);
        var accessor = new FakeActiveTeamAccessor(team);

        SyncStatusRoutes.Map(app, accessor, TimeProvider.System);
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

            var response = await client.GetAsync(SyncStatusRoutes.RouteBase);

            // The READOUT failure must NOT 500 — it degrades to a calm 200 snapshot.
            Assert.True(response.IsSuccessStatusCode, $"expected 2xx, got {(int)response.StatusCode}");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            // Calm empty snapshot, still stamped.
            Assert.Equal("has", root.GetProperty("aggregate").GetString());
            Assert.Empty(root.GetProperty("peers").EnumerateArray());
            Assert.True(DateTimeOffset.TryParse(root.GetProperty("asOf").GetString(), out _));

            // The enrollment signal goes degraded ("not yet readable") so the Harborline App keeps polling rather than
            // treating the readout failure as the join failing.
            var enrollment = root.GetProperty("enrollment");
            Assert.False(enrollment.GetProperty("complete").GetBoolean());
            Assert.StartsWith("sync_status_unreadable:", enrollment.GetProperty("reason").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    /// <summary>
    /// A FAULTING enrollment-rebind status seam (the live worker) must not 500 the readout either — reading
    /// <see cref="IEnrollmentRebindStatus.LastRebind"/> is a status concern that degrades to the healthy default,
    /// never an opaque 500. The sync snapshot itself still projects normally.
    /// </summary>
    [Fact]
    public async Task GET_sync_status_survives_a_rebind_status_that_throws()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var accessor = NewAccessorOverFakeDaemon(out _);
        SyncStatusRoutes.Map(
            app, accessor, new NodeCallerSessionToken(null), TimeProvider.System,
            new ThrowingRebindStatus());
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

            var response = await client.GetAsync(SyncStatusRoutes.RouteBase);

            Assert.True(response.IsSuccessStatusCode, $"expected 2xx, got {(int)response.StatusCode}");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            // Snapshot projects normally; the faulting rebind status degraded to healthy (did not 500).
            Assert.Equal("has", root.GetProperty("aggregate").GetString());
            Assert.True(root.GetProperty("enrollment").GetProperty("complete").GetBoolean());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static FakeActiveTeamAccessor NewAccessorOverFakeDaemon(out FakeGossipDaemon daemon)
    {
        daemon = new FakeGossipDaemon();
        var readModel = new SyncStatusReadModel(
            daemon, Options.Create(new GossipDaemonOptions { RoundIntervalSeconds = 30 }), timeProvider: TimeProvider.System);
        var teamServices = new ServiceCollection();
        teamServices.AddSingleton<ISyncStatusReadModel>(readModel);
        var team = new TeamContext(
            new TeamId(new Guid("44444444-4444-4444-4444-444444444444")),
            "Rebind Team",
            teamServices.BuildServiceProvider(), TimeProvider.System);
        return new FakeActiveTeamAccessor(team);
    }

    private sealed class ThrowingRebindStatus : IEnrollmentRebindStatus
    {
        public RebindOutcome LastRebind =>
            throw new InvalidOperationException("rebind status seam faulted (must not 500 the readout)");
    }

    private static byte[] Key(byte seed)
    {
        var k = new byte[32];
        Array.Fill(k, seed);
        return k;
    }

    private sealed class FakeActiveTeamAccessor : IActiveTeamAccessor
    {
        public FakeActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }

    private sealed class FakeGossipDaemon : IGossipDaemon
    {
        private readonly Dictionary<string, PeerInfo> _peers = new(StringComparer.Ordinal);

        public void SetPeer(PeerInfo info) => _peers[info.Endpoint] = info;
        public void RaiseFrame(GossipFrameEventArgs e) => FrameReceived?.Invoke(this, e);
        public void RaiseRound(GossipRoundCompletedEventArgs e) => RoundCompleted?.Invoke(this, e);

        public IReadOnlyCollection<PeerInfo> KnownPeers => _peers.Values.ToList();
        public event EventHandler<GossipRoundCompletedEventArgs>? RoundCompleted;
        public event EventHandler<GossipFrameEventArgs>? FrameReceived;

        public bool IsRunning => false;
        public bool IsListening => false;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartListeningAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopListeningAsync(CancellationToken ct) => Task.CompletedTask;
        public Task TriggerPushAsync(OutboundSyncLane lane, CancellationToken ct) => Task.CompletedTask;
        public void AddPeer(string peerEndpoint, byte[] peerPublicKey) { }
        public void RemovePeer(string peerEndpoint) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
