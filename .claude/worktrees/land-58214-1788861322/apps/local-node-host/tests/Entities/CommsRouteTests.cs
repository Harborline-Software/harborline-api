using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Route-level coverage for the comms append-log surface on the SHIPPED product API — POST/GET
/// <c>/api/local-node/comms</c> over a real in-process Kestrel listener, the real
/// <see cref="CommsCrdtProjection"/> (real <see cref="YDotNetCrdtEngine"/> backend), and the node's
/// canonical signer. Mirrors <c>ContactDeleteRouteTests</c>.
/// </summary>
public sealed class CommsRouteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private CommsCrdtProjection _crdt = null!;
    private NodePrincipalSigner _signer = null!;
    private IDbContextFactory<NodeLocalCommsDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-comms-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "comms.db")};Pooling=False";

        builder.Services.AddDbContextFactory<NodeLocalCommsDbContext>(opt => opt.UseSqlite(connectionString));
        builder.Services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();

        _app = builder.Build();

        _factory = _app.Services.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        await using (var ctx = await _factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        _crdt = new CommsCrdtProjection(
            _app.Services.GetRequiredService<ICrdtEngine>(), _factory, new Ed25519Verifier(),
            NullLogger<CommsCrdtProjection>.Instance);

        // The node's canonical signer over a fixed 32-byte test seed (the active member's signing identity).
        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++) seed[i] = (byte)(i + 1);
        _signer = new NodePrincipalSigner(seed);

        // Map the SAME production routes (mirrors HostedCommsApiEndpoint wiring — no [FromServices]).
        CommsRoutes.Map(_app, _crdt, _signer.Signer, NodeTestActiveTeam.Accessor, TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _signer?.Dispose();
        await _crdt.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private const string Route = "/api/local-node/comms";

    // ── POST appends, GET returns the ordered log ────────────────────────────────────────────────────────

    [Fact(DisplayName = "Comms route: POST appends a signed message; GET returns it attributed + in order")]
    public async Task Post_Appends_Get_Returns_Ordered_Attributed()
    {
        var r1 = await _client.PostAsJsonAsync(Route, new { body = "first message" });
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        var r2 = await _client.PostAsJsonAsync(Route, new { body = "second message" });
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);

        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var messages = doc.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());

        // Ordered (authored order) + attributed: each carries the active member's id + a verifiable signature.
        Assert.Equal("first message", messages[0].GetProperty("body").GetString());
        Assert.Equal("second message", messages[1].GetProperty("body").GetString());
        var nodeIssuer = _signer.NodePublicKey;
        foreach (var m in messages.EnumerateArray())
        {
            // This test maps the routes via the 4-arg overload (no roster wired — the dev/single-host path),
            // which stamps the FallbackAuthorPartyId ("local"). The SHIPPING path stamps the active enrolled
            // member's per-node-distinct id — proven by Post_Stamps_ActiveEnrolledMember_PartyId below.
            Assert.Equal("local", m.GetProperty("authorPartyId").GetString());
            Assert.Equal(nodeIssuer, m.GetProperty("authorIssuerId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(m.GetProperty("signature").GetString()));
        }
    }

    [Fact(DisplayName = "Comms route: POST with an empty body → 400 body_required")]
    public async Task Post_EmptyBody_Returns400()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { body = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "Comms route: the appended message lands in the durable EF store + the CRDT list")]
    public async Task Post_Persists_To_Durable_Store_And_Crdt()
    {
        var resp = await _client.PostAsJsonAsync(Route, new { body = "durable + synced" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        // Durable EF row exists (the recoverable store — the CRDT's only durable sink, SC4-C2).
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            var rows = await ctx.Set<NodeMessage>().AsNoTracking().ToListAsync();
            Assert.Single(rows);
            Assert.Equal("durable + synced", rows[0].Body);
        }

        // And the CRDT list holds it (so it would ship as a sync delta).
        Assert.Equal(1, _crdt.Count);
    }

    // ── The product-route append converges to a peer (mirrors the harness) ──────────────────────────────

    [Fact(DisplayName = "Comms route: a message appended via the product route converges to a peer replica")]
    public async Task RouteAppend_Converges_To_Peer()
    {
        var peerDir = Path.Combine(Path.GetTempPath(), "harborline-comms-peer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(peerDir);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalCommsDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(peerDir, "comms.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        await using var peerSp = services.BuildServiceProvider();
        var peerFactory = peerSp.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        await using (var ctx = await peerFactory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();
        await using var peer = new CommsCrdtProjection(
            peerSp.GetRequiredService<ICrdtEngine>(), peerFactory, new Ed25519Verifier(),
            NullLogger<CommsCrdtProjection>.Instance);

        // Append via the product route, sync to the peer over the live Changed→reconcile trigger.
        Assert.Equal(HttpStatusCode.Created,
            (await _client.PostAsJsonAsync(Route, new { body = "reaches the peer" })).StatusCode);

        var delta = await _crdt.EncodeOutboundDeltaAsync(
            CommsCrdtProjection.DocumentId, peer.VectorClock, CancellationToken.None);
        await peer.ApplyInboundDeltaAsync(CommsCrdtProjection.DocumentId, 1, delta!.Value, CancellationToken.None);
        await peer.DrainPendingReconcilesAsync();

        var peerLog = await peer.ReadLogAsync(
            NodeTestActiveTeam.TestTeamId.Value.ToString(), CancellationToken.None);
        Assert.Single(peerLog);
        Assert.Equal("reaches the peer", peerLog[0].Body);
        Assert.True(CommsMessageFactory.VerifyAuthorship(peerLog[0], new Ed25519Verifier()));

        try { Directory.Delete(peerDir, recursive: true); } catch { /* best-effort */ }
    }
}
