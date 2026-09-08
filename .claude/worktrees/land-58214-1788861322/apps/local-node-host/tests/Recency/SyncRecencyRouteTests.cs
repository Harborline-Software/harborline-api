using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.MissionSpace;
using Harborline.Api.Foundation.UI;
using Harborline.Api.Protocol;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Gossip;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.UICore.Wayfinder;
using Harborline.Api.UICore.Wayfinder.Widgets;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Recency;

public sealed class SyncRecencyRouteTests
{
    [Fact]
    public void Reference_app_sync_status_contract_carries_renderable_recency_evidence()
    {
        const string json = """
            {
              "aggregate": "has",
              "asOf": "2026-08-18T14:31:00.0000000+00:00",
              "peers": [],
              "cadence": { "nextRoundAt": null, "roundIntervalSeconds": 30 },
              "recency": {
                "basis": "peer-exchange",
                "currentness": "not-established",
                "lastExchange": {
                  "peerDeviceId": "53535353",
                  "peerLabel": "site-tablet",
                  "exchangedAt": "2026-08-18T14:30:00.0000000+00:00"
                }
              }
            }
            """;

        var status = JsonSerializer.Deserialize<HarborlineSyncStatus>(json);

        Assert.NotNull(status);
        Assert.Equal("peer-exchange", status.Recency.Basis);
        Assert.Equal("not-established", status.Recency.Currentness);
        Assert.Equal("site-tablet", status.Recency.LastExchange!.PeerLabel);
    }

    [Fact]
    public async Task Sync_state_widget_names_peer_exchange_and_disclaims_currentness()
    {
        var exchangedAt = new DateTimeOffset(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
        var envelope = new MissionEnvelope
        {
            Hardware = null!,
            User = null!,
            Regulatory = null!,
            Runtime = null!,
            FormFactor = null!,
            Edition = null!,
            Network = null!,
            TrustAnchor = null!,
            SyncState = new SyncStateSnapshot
            {
                State = SyncState.Healthy,
                LastSyncedAt = exchangedAt,
                ProbeStatus = ProbeStatus.Healthy,
            },
            VersionVector = null!,
            SnapshotAt = exchangedAt,
        };
        var context = new HelmRenderContext(
            envelope,
            new TenantId("recency-test"),
            new ActorId("recency-test"),
            ActiveTeamId: null,
            Now: exchangedAt);

        var view = await new SyncStateWidget().ComputeAsync(context);

        Assert.Equal("Last peer exchange 2026-08-18 14:30:00Z; currentness not established", view.SecondaryLabel);
        Assert.DoesNotContain("synced", view.SecondaryLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GET_sync_status_reports_the_latest_peer_exchange_without_claiming_currentness()
    {
        var exchangedAt = new DateTimeOffset(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);
        var daemon = new FakeGossipDaemon();
        daemon.SetPeer(new PeerInfo("site-tablet", Key(0x53), exchangedAt, 0));
        var readModel = new SyncStatusReadModel(
            daemon,
            Options.Create(new GossipDaemonOptions { RoundIntervalSeconds = 30 }), timeProvider: TimeProvider.System);
        var services = new ServiceCollection();
        services.AddSingleton<ISyncStatusReadModel>(readModel);
        var team = new TeamContext(
            new TeamId(new Guid("53535353-5353-5353-5353-535353535353")),
            "Recency Team",
            services.BuildServiceProvider(), TimeProvider.System);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        SyncStatusRoutes.Map(app, new FakeActiveTeamAccessor(team), TimeProvider.System);
        await app.StartAsync();

        try
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>();
            using var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };

            using var response = await client.GetAsync(SyncStatusRoutes.RouteBase);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            var recency = document.RootElement.GetProperty("recency");
            Assert.Equal("peer-exchange", recency.GetProperty("basis").GetString());
            Assert.Equal("not-established", recency.GetProperty("currentness").GetString());
            var lastExchange = recency.GetProperty("lastExchange");
            Assert.Equal("site-tablet", lastExchange.GetProperty("peerLabel").GetString());
            Assert.Equal("2026-08-18T14:30:00.0000000+00:00", lastExchange.GetProperty("exchangedAt").GetString());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static byte[] Key(byte seed)
    {
        var key = new byte[32];
        Array.Fill(key, seed);
        return key;
    }

    private sealed class FakeActiveTeamAccessor(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void KeepEvent() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }

    private sealed class FakeGossipDaemon : IGossipDaemon
    {
        private readonly Dictionary<string, PeerInfo> _peers = new(StringComparer.Ordinal);

        public IReadOnlyCollection<PeerInfo> KnownPeers => _peers.Values.ToList();
        public event EventHandler<GossipRoundCompletedEventArgs>? RoundCompleted;
        public event EventHandler<GossipFrameEventArgs>? FrameReceived;
        public bool IsRunning => false;
        public bool IsListening => false;

        public void SetPeer(PeerInfo peer) => _peers[peer.Endpoint] = peer;
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartListeningAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopListeningAsync(CancellationToken ct) => Task.CompletedTask;
        public Task TriggerPushAsync(OutboundSyncLane lane, CancellationToken ct) => Task.CompletedTask;
        public void AddPeer(string peerEndpoint, byte[] peerPublicKey) { }
        public void RemovePeer(string peerEndpoint) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private void KeepEvents()
        {
            RoundCompleted?.Invoke(this, new GossipRoundCompletedEventArgs(0, 0, 0));
            FrameReceived?.Invoke(this, new GossipFrameEventArgs(
                "unused", "unused", GossipFrameType.GossipPing, DateTimeOffset.MinValue));
        }
    }
}
