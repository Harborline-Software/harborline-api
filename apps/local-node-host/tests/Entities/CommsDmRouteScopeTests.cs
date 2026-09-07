using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using System.IO;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// C4 — the DEV/TEST-GATED DM resolve-and-open route (<see cref="CommsRoutes.DmResolveRoute"/>), now over a
/// registry wired with a DM key provider so bodies are SEALED on append + UNSEALED for the participant reader.
/// Proves over the real in-process Kestrel listener + the conversation registry:
/// <list type="bullet">
///   <item>with the gate ENABLED, <c>POST/GET /api/local-node/comms/dm/{otherPartyId}</c> derives the
///     deterministic <c>dm:</c> id server-side (the no-coordination property) and round-trips the DM thread,
///     SCOPED — it does NOT bleed into the team channel;</item>
///   <item>a DM message lands on the EXACT <c>dm:</c> id the independent derivation computes (A==B);</item>
///   <item>two different DM partners are ISOLATED threads (A↔B never sees A↔C);</item>
///   <item>a self-DM is rejected;</item>
///   <item>with the gate DISABLED (the SHIPPED posture), the DM route is ABSENT (404) — NO user DM surface
///     exists pre-C4.</item>
/// </list>
/// </summary>
public sealed class CommsDmRouteScopeTests
{
    private const string ActiveMember = "alice";
    private const string Bare = "/api/local-node/comms";
    private const string TeamRoute = "/api/local-node/comms/team";

    private static string[] Bodies(JsonElement doc) =>
        doc.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("body").GetString()!).ToArray();

    // ── A small self-hosted comms surface with a configurable DM dev gate. ──────────────────────────────────
    private sealed class Host : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required string Dir { get; init; }
        public required CommsConversationRegistry Registry { get; init; }
        public required NodePrincipalSigner Signer { get; init; }

        public static async Task<Host> StartAsync(bool dmGateEnabled)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();

            var dir = Path.Combine(Path.GetTempPath(), "harborline-comms-dmroute-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            builder.Services.AddDbContextFactory<NodeLocalCommsDbContext>(
                opt => opt.UseSqlite($"Data Source={Path.Combine(dir, "comms.db")};Pooling=False"));
            builder.Services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
            builder.Services.AddHarborlineDeltaRouter();

            var app = builder.Build();
            var factory = app.Services.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
            await using (var ctx = await factory.CreateDbContextAsync())
                await ctx.Database.EnsureCreatedAsync();

            var seed = new byte[32];
            for (var i = 0; i < seed.Length; i++) seed[i] = (byte)(i + 1);
            var signer = new NodePrincipalSigner(seed);

            // C4 — the registry is wired with a DM key provider so the dm/ route SEALS bodies on append + UNSEALS
            // them for the participant reader. The active member is "alice" (the route stamps it as author).
            var dmKeyProvider = new DerivedDmConversationKeyProvider(
                new SeedDerivedParticipantDmKeyResolver(seed, ActiveMember));
            var registry = new CommsConversationRegistry(
                app.Services.GetRequiredService<ICrdtEngine>(), factory, new Ed25519Verifier(),
                app.Services.GetRequiredService<IDeltaRouter>(),
                app.Services.GetRequiredService<ILoggerFactory>(),
                rosterBinding: null,
                dmKeyProvider: dmKeyProvider);

            CommsRoutes.Map(app, registry, signer.Signer, NodeTestActiveTeam.Accessor,
                new NodeCallerSessionToken(null), ActiveMember, new CommsDmFeatureFlag(enabled: dmGateEnabled),
                TimeProvider.System);

            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
            return new Host { App = app, Client = client, Dir = dir, Registry = registry, Signer = signer };
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Signer.Dispose();
            await Registry.DisposeAsync();
            await App.StopAsync();
            await App.DisposeAsync();
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    /// <summary>The team id the route resolves (so the test derives the SAME dm: id the route does).</summary>
    private static string TeamId => NodeTenant.Resolve(NodeTestActiveTeam.Accessor).Value;

    // ── (a) the DM resolve route round-trips, lands on the deterministic dm: id, scoped from the team. ──────

    [Fact(DisplayName = "C2 (gate ON): DM resolve route round-trips on the deterministic dm: id, SCOPED from the team channel")]
    public async Task Dm_Route_RoundTrips_On_Deterministic_Id_Scoped_From_Team()
    {
        await using var host = await Host.StartAsync(dmGateEnabled: true);

        // Append a DM to bob + a team message.
        Assert.Equal(HttpStatusCode.Created,
            (await host.Client.PostAsJsonAsync($"{Bare}/dm/bob", new { body = "hi bob (dm)" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await host.Client.PostAsJsonAsync(TeamRoute, new { body = "team msg" })).StatusCode);

        // The DM route reads back ONLY the DM message...
        var dm = await host.Client.GetFromJsonAsync<JsonElement>($"{Bare}/dm/bob");
        Assert.Equal(new[] { "hi bob (dm)" }, Bodies(dm));

        // ...and the message is stamped with the EXACT dm: id the independent derivation computes (A==B).
        var expectedDmId = DmConversationId.Derive(TeamId, ActiveMember, "bob");
        Assert.Equal(expectedDmId, dm.GetProperty("messages")[0].GetProperty("conversationId").GetString());

        // The team channel did NOT receive the DM (scoped — no bleed).
        var team = await host.Client.GetFromJsonAsync<JsonElement>(TeamRoute);
        Assert.Equal(new[] { "team msg" }, Bodies(team));

        // C4 — the DM thread IS reachable via the generic conversation-addressed route at its derived id, but that
        // generic route returns the body AT REST (sealed ciphertext) — only the dm/ resolve route unseals for the
        // participant. So the generic route surfaces CIPHERTEXT (the envelope), NOT the plaintext (proof the body
        // is sealed end-to-end, not just hidden behind the dm/ route).
        var viaConvId = await host.Client.GetFromJsonAsync<JsonElement>($"{Bare}/{expectedDmId}");
        var sealedBody = Bodies(viaConvId).Single();
        Assert.True(DmContentSeal.IsSealed(sealedBody));
        Assert.DoesNotContain("hi bob (dm)", sealedBody, StringComparison.Ordinal);
    }

    // ── (b) two distinct DM partners are isolated threads (A↔B never sees A↔C). ──────────────────────────────

    [Fact(DisplayName = "C2 (gate ON): two different DM partners are ISOLATED threads (alice↔bob never sees alice↔carol)")]
    public async Task Two_Dm_Partners_Are_Isolated()
    {
        await using var host = await Host.StartAsync(dmGateEnabled: true);

        await host.Client.PostAsJsonAsync($"{Bare}/dm/bob", new { body = "to bob" });
        await host.Client.PostAsJsonAsync($"{Bare}/dm/carol", new { body = "to carol" });

        var dmBob = await host.Client.GetFromJsonAsync<JsonElement>($"{Bare}/dm/bob");
        var dmCarol = await host.Client.GetFromJsonAsync<JsonElement>($"{Bare}/dm/carol");
        Assert.Equal(new[] { "to bob" }, Bodies(dmBob));
        Assert.Equal(new[] { "to carol" }, Bodies(dmCarol));

        // The two DM ids differ (distinct pairs → distinct dm: ids).
        Assert.NotEqual(
            DmConversationId.Derive(TeamId, ActiveMember, "bob"),
            DmConversationId.Derive(TeamId, ActiveMember, "carol"));
    }

    // ── (c) order-independence at the route: alice→bob and the bob→alice id are the SAME thread. ─────────────

    [Fact(DisplayName = "C2 (gate ON): the DM route's derived id equals the order-swapped derivation (no-coordination)")]
    public async Task Dm_Route_Id_Is_Order_Independent()
    {
        await using var host = await Host.StartAsync(dmGateEnabled: true);
        await host.Client.PostAsJsonAsync($"{Bare}/dm/bob", new { body = "x" });

        var dm = await host.Client.GetFromJsonAsync<JsonElement>($"{Bare}/dm/bob");
        var routeId = dm.GetProperty("messages")[0].GetProperty("conversationId").GetString();
        // What bob's node would derive for "my DM with alice" — the SAME id.
        Assert.Equal(DmConversationId.Derive(TeamId, "bob", ActiveMember), routeId);
    }

    // ── (d) self-DM rejected. ───────────────────────────────────────────────────────────────────────────────

    [Fact(DisplayName = "C2 (gate ON): a self-DM (DM with yourself) is rejected 400")]
    public async Task Self_Dm_Is_Rejected()
    {
        await using var host = await Host.StartAsync(dmGateEnabled: true);
        var resp = await host.Client.PostAsJsonAsync($"{Bare}/dm/{ActiveMember}", new { body = "to self" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ── (e) THE KILL-SWITCH: with the DM flag DISABLED the DM route is ABSENT (the incident kill-switch). ──
    // The DM surface SHIPS ON by default now (C4+C5 sealed the body); this proves the kill-switch still removes
    // ONLY the DM route — team messaging is unaffected — so an operator can disable DMs in an incident without a
    // redeploy and without touching the team channel or the crypto.

    [Fact(DisplayName = "Kill-switch (DM flag OFF): the DM resolve route is ABSENT (404); the team channel is unaffected")]
    public async Task Dm_Route_Is_Absent_When_Flag_Disabled()
    {
        await using var host = await Host.StartAsync(dmGateEnabled: false);

        // The DM resolve route is not mapped → 404 for both verbs.
        var post = await host.Client.PostAsJsonAsync($"{Bare}/dm/bob", new { body = "should not exist" });
        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        var get = await host.Client.GetAsync($"{Bare}/dm/bob");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        // The team channel STILL works with the DM flag off (the flag fences ONLY the DM surface).
        Assert.Equal(HttpStatusCode.Created,
            (await host.Client.PostAsJsonAsync(TeamRoute, new { body = "team still works" })).StatusCode);
        var team = await host.Client.GetFromJsonAsync<JsonElement>(TeamRoute);
        Assert.Equal(new[] { "team still works" }, Bodies(team));
    }
}
