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
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// C1 — CONVERSATION SCOPE: the comms doctype now carries a conversation dimension. These tests prove the C1
/// success criteria over the real in-process Kestrel listener + the real conversation registry:
/// <list type="bullet">
///   <item>the TEAM conversation (<c>"team"</c>) round-trips identically over BOTH the bare
///     <c>/api/local-node/comms</c> route (back-compat) AND the conversation-addressed
///     <c>/api/local-node/comms/team</c> route;</item>
///   <item>a NON-TEAM conversation id round-trips (append + read) and is SCOPED — its messages do NOT bleed
///     into the team channel and vice-versa;</item>
///   <item>each conversation is its OWN CRDT document on the delta router (one document per conversation);</item>
///   <item>the migration is additive — an existing pre-C1 row (no conversation_id) back-fills to <c>"team"</c>
///     and reads back on the team channel.</item>
/// </list>
/// Mirrors <see cref="CommsRouteTests"/> but exercises the registry-backed conversation addressing.
/// </summary>
public sealed class CommsConversationScopeTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _dir = null!;
    private CommsConversationRegistry _registry = null!;
    private NodePrincipalSigner _signer = null!;
    private IDbContextFactory<NodeLocalCommsDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _dir = Path.Combine(Path.GetTempPath(), "harborline-comms-convscope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var connectionString = $"Data Source={Path.Combine(_dir, "comms.db")};Pooling=False";

        builder.Services.AddDbContextFactory<NodeLocalCommsDbContext>(opt => opt.UseSqlite(connectionString));
        builder.Services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        builder.Services.AddHarborlineDeltaRouter();

        _app = builder.Build();

        _factory = _app.Services.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        // EnsureCreated builds the CURRENT model (incl. the conversation_id column) — the conversation-scoped
        // read path depends on it.
        await using (var ctx = await _factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        _registry = new CommsConversationRegistry(
            _app.Services.GetRequiredService<ICrdtEngine>(), _factory, new Ed25519Verifier(),
            _app.Services.GetRequiredService<IDeltaRouter>(),
            _app.Services.GetRequiredService<ILoggerFactory>());

        var seed = new byte[32];
        for (var i = 0; i < seed.Length; i++) seed[i] = (byte)(i + 1);
        _signer = new NodePrincipalSigner(seed);

        // Map the SHIPPING registry-backed routes (bare + conversation-addressed).
        CommsRoutes.Map(_app, _registry, _signer.Signer, NodeTestActiveTeam.Accessor,
            new NodeCallerSessionToken(null), "alice", TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _signer?.Dispose();
        await _registry.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    private const string BareRoute = "/api/local-node/comms";
    private const string TeamRoute = "/api/local-node/comms/team";

    private static string[] Bodies(JsonElement doc) =>
        doc.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("body").GetString()!).ToArray();

    // ── (a) the team conversation round-trips identically via BOTH the bare + conversation-addressed route ───

    [Fact(DisplayName = "C1: the team conversation round-trips identically via the bare route AND /comms/team")]
    public async Task TeamConversation_RoundTrips_Via_BareAnd_ConversationRoute()
    {
        // Append via the BARE route (back-compat — defaults to "team").
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync(BareRoute, new { body = "bare-1" })).StatusCode);
        // Append via the CONVERSATION-ADDRESSED team route.
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync(TeamRoute, new { body = "team-2" })).StatusCode);

        // Both routes read the SAME team log (the bare route == /comms/team).
        var viaBare = await _client.GetFromJsonAsync<JsonElement>(BareRoute);
        var viaTeam = await _client.GetFromJsonAsync<JsonElement>(TeamRoute);
        Assert.Equal(new[] { "bare-1", "team-2" }, Bodies(viaBare));
        Assert.Equal(new[] { "bare-1", "team-2" }, Bodies(viaTeam));

        // The wire carries the conversation id, stamped "team" on both.
        foreach (var m in viaBare.GetProperty("messages").EnumerateArray())
            Assert.Equal("team", m.GetProperty("conversationId").GetString());
    }

    // ── (b) a non-team conversation round-trips AND is isolated from the team channel ────────────────────────

    [Fact(DisplayName = "C1: a non-team conversation round-trips (append + read scoped) and is ISOLATED from the team channel")]
    public async Task NonTeamConversation_RoundTrips_AndIsIsolated()
    {
        const string convId = "dm:roundtrip-example";
        var convRoute = $"/api/local-node/comms/{convId}";

        // Append to the team channel + the non-team conversation.
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync(TeamRoute, new { body = "team only" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync(convRoute, new { body = "conv only" })).StatusCode);

        // The non-team conversation reads back ONLY its own message (scoped append + read).
        var conv = await _client.GetFromJsonAsync<JsonElement>(convRoute);
        Assert.Equal(new[] { "conv only" }, Bodies(conv));
        Assert.Equal(convId, conv.GetProperty("messages")[0].GetProperty("conversationId").GetString());

        // The team channel reads back ONLY its own message — no bleed from the non-team conversation.
        var team = await _client.GetFromJsonAsync<JsonElement>(TeamRoute);
        Assert.Equal(new[] { "team only" }, Bodies(team));

        // The registry created a distinct projection per conversation (one document per conversation).
        Assert.Contains("team", _registry.ActiveConversationIds);
        Assert.Contains(convId, _registry.ActiveConversationIds);
        // Distinct CRDT documents → distinct router streams.
        var router = _app.Services.GetRequiredService<IDeltaRouter>();
        Assert.Contains("team", router.RegisteredDocumentIds);
        Assert.Contains(convId, router.RegisteredDocumentIds);
    }

    // ── (b') the durable rows are conversation-scoped in the ONE shared table ────────────────────────────────

    [Fact(DisplayName = "C1: messages from two conversations share the ONE messages table, scoped by conversation_id")]
    public async Task TwoConversations_ShareTable_ScopedByColumn()
    {
        await _client.PostAsJsonAsync(TeamRoute, new { body = "t" });
        await _client.PostAsJsonAsync("/api/local-node/comms/dm:scoped", new { body = "d" });

        await using var ctx = await _factory.CreateDbContextAsync();
        var rows = await ctx.Set<NodeMessage>().AsNoTracking().OrderBy(m => m.Body).ToListAsync();
        Assert.Equal(2, rows.Count); // one table, two rows
        Assert.Equal("d", rows[0].Body);
        Assert.Equal("dm:scoped", rows[0].ConversationId);
        Assert.Equal("t", rows[1].Body);
        Assert.Equal("team", rows[1].ConversationId);
    }

    // ── (c) the migration is additive — a pre-C1 row (no conversation_id) back-fills to "team" ──────────────

    [Fact(DisplayName = "C1: an existing pre-C1 row (inserted with the column default) back-fills to \"team\" and reads on the team channel")]
    public async Task PreC1Row_BackFills_To_Team_And_ReadsOnTeamChannel()
    {
        // Simulate a pre-C1 durable row: insert via raw SQL WITHOUT the conversation_id column, so SQLite
        // applies the column DEFAULT ('team') the additive migration set — exactly the back-fill an upgraded
        // node sees for its existing team log.
        await using (var ctx = await _factory.CreateDbContextAsync())
        {
            await ctx.Database.ExecuteSqlRawAsync(
                "INSERT INTO messages (id, tenant_id, author_party_id, author_issuer_id, body, nonce, signature, authored_at) " +
                "VALUES ('preC1-id', {0}, 'alice', 'issuer', 'pre-C1 team message', 'nonce', 'sig', {1})",
                NodeTestActiveTeam.TestTeamId.Value.ToString(),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            // The row took the column default.
            var backfilled = await ctx.Set<NodeMessage>().AsNoTracking().SingleAsync(m => m.Id == "preC1-id");
            Assert.Equal("team", backfilled.ConversationId);
        }

        // And it reads back on the TEAM channel (the bare route) — the pre-C1 team log is preserved.
        var team = await _client.GetFromJsonAsync<JsonElement>(BareRoute);
        Assert.Contains("pre-C1 team message", Bodies(team));
    }

    // ── unknown conversation behaviour: the registry creates lazily, so any id is a valid (empty) thread ─────

    [Fact(DisplayName = "C1: GET on a never-used conversation returns an empty log (the registry creates it lazily)")]
    public async Task UnusedConversation_Returns_EmptyLog()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>("/api/local-node/comms/dm:never-used");
        Assert.Empty(Bodies(doc));
    }
}
