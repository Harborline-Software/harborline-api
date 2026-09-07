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
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Crdt;
using Harborline.Api.Kernel.Crdt.Backends;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Sync.Application;
using Harborline.Api.Kernel.Sync.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data.Comms;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// GAP #2 — the comms author party id is the ACTIVE ENROLLED MEMBER (the trust-roster party bound to the
/// signing principal key), NOT the install-constant <c>"local"</c> that every node would otherwise stamp.
/// These tests prove the SHIPPING wiring (the 6-arg <see cref="CommsRoutes.Map"/> overload + the
/// <see cref="HostedCommsApiEndpoint"/> consistency assertion):
/// <list type="bullet">
///   <item>a message's <c>authorPartyId</c> is the active enrolled member's party id (NOT "local");</item>
///   <item>two distinct-root nodes stamp DISTINCT party ids (the two-user-test residual fixed);</item>
///   <item>the author-party-id↔signing-key consistency holds — the production forge-proof gate passes the
///     operator's OWN messages (and rejects a mismatch, not regressing #1277-B1);</item>
///   <item>the genesis (single-user) node stamps its own per-node-distinct party id + its messages verify;</item>
///   <item>the host consistency assertion FAILS-CLOSED when the author party is not the roster party bound to
///     the signing key (a composition error must not ship messages the node's own gate would drop).</item>
/// </list>
/// </summary>
public sealed class CommsAuthorPartyIdTests : IAsyncLifetime
{
    private static readonly IOperationVerifier Verifier = new Ed25519Verifier();
    private static readonly System.Guid TeamId = System.Guid.Parse("7e57dddd-0000-0000-0000-0000000000a2");

    private readonly List<IAsyncDisposable> _asyncDisposables = new();
    private readonly List<WebApplication> _apps = new();
    private readonly List<HttpClient> _clients = new();
    private readonly List<NodePrincipalSigner> _signers = new();
    private readonly List<string> _dirs = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _clients) c.Dispose();
        foreach (var s in _signers) s.Dispose();
        foreach (var d in _asyncDisposables) await d.DisposeAsync();
        foreach (var a in _apps) { await a.StopAsync(); await a.DisposeAsync(); }
        foreach (var dir in _dirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── A node whose comms routes are mapped via the SHIPPING (6-arg) overload, the author resolved from a ──
    //    seeded genesis roster exactly as HostedCommsApiEndpoint.ResolveAndAssertActiveMember does. ─────────
    private sealed record Node(HttpClient Client, string AuthorPartyId, NodePrincipalSigner Signer);

    private async Task<Node> NewShippingNodeAsync(byte[] rootSeed)
    {
        var signer = new NodePrincipalSigner(rootSeed);
        _signers.Add(signer);

        // Seed the genesis (self) roster with a PER-NODE-DISTINCT party id derived from the node principal key
        // — the same shape Program.cs seeds (os:<user>#<key8>). Distinct roots ⇒ distinct key8 ⇒ distinct ids.
        var key8 = System.Convert.ToHexString(signer.Signer.IssuerId.AsSpan()[..4]).ToLowerInvariant();
        var partyId = $"os:tester#{key8}";
        var roster = new NodeTeamRoster(MemberRoster.Genesis(
            TeamId, partyId, signer.Signer, Verifier, System.DateTimeOffset.UtcNow, System.Guid.NewGuid()));

        // The consistency invariant HostedCommsApiEndpoint asserts before mapping: the author party is bound,
        // in this roster, to the signing key. Mirror it here so this helper builds the SHIPPING author source.
        var bound = roster.Current.PublicKeyOf(partyId);
        Assert.NotNull(bound);
        Assert.True(bound!.Value.Equals(signer.Signer.IssuerId));

        var dir = Path.Combine(Path.GetTempPath(), "harborline-comms-gap2-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddDbContextFactory<NodeLocalCommsDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(dir, "comms.db")};Pooling=False"));
        builder.Services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        var app = builder.Build();
        _apps.Add(app);

        var factory = app.Services.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        // The production forge-proof projection — rosterBinding wired from THIS seeded roster (as AddNodeComms).
        var crdt = new CommsCrdtProjection(
            app.Services.GetRequiredService<ICrdtEngine>(), factory, new Ed25519Verifier(),
            NullLogger<CommsCrdtProjection>.Instance, rosterBinding: roster.ForgeProofBinding);
        _asyncDisposables.Add(crdt);

        // SHIPPING (6-arg) overload: author = the resolved active enrolled member's party id.
        CommsRoutes.Map(app, crdt, signer.Signer, NodeTestActiveTeam.Accessor,
            new NodeCallerSessionToken(null), partyId, TimeProvider.System);

        await app.StartAsync();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var client = new HttpClient { BaseAddress = new System.Uri(addresses!.Addresses.First()) };
        _clients.Add(client);
        return new Node(client, partyId, signer);
    }

    private static byte[] Seed(byte fill)
    {
        var s = new byte[32];
        for (var i = 0; i < s.Length; i++) s[i] = (byte)(fill + i);
        return s;
    }

    private const string Route = "/api/local-node/comms";

    private static async Task<string> PostAndReadAuthorAsync(HttpClient client, string body)
    {
        var resp = await client.PostAsJsonAsync(Route, new { body });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("authorPartyId").GetString()!;
    }

    // ── TEST 1: the SHIPPING path stamps the active enrolled member's id, NOT the constant "local". ─────────

    [Fact(DisplayName = "gap #2: a message's authorPartyId is the active enrolled member's id (NOT \"local\")")]
    public async Task Post_Stamps_ActiveEnrolledMember_PartyId_NotLocal()
    {
        var node = await NewShippingNodeAsync(Seed(0x10));

        var author = await PostAndReadAuthorAsync(node.Client, "hello");
        Assert.Equal(node.AuthorPartyId, author);
        Assert.NotEqual("local", author);
        Assert.StartsWith("os:tester#", author); // the per-node-distinct os:<user>#<key8> shape

        // GET returns the same attribution.
        var doc = await node.Client.GetFromJsonAsync<JsonElement>(Route);
        Assert.Equal(node.AuthorPartyId, doc.GetProperty("messages")[0].GetProperty("authorPartyId").GetString());
    }

    // ── TEST 2: two distinct-root nodes stamp DISTINCT party ids (the two-user-test residual fixed). ────────

    [Fact(DisplayName = "gap #2: two distinct-root nodes stamp DISTINCT author party ids")]
    public async Task TwoDistinctRootNodes_StampDistinctPartyIds()
    {
        var nodeA = await NewShippingNodeAsync(Seed(0x20));
        var nodeB = await NewShippingNodeAsync(Seed(0x40)); // a DIFFERENT root → a different principal key

        var authorA = await PostAndReadAuthorAsync(nodeA.Client, "from A");
        var authorB = await PostAndReadAuthorAsync(nodeB.Client, "from B");

        // The whole point of gap #2: not both "local" — they are DISTINCT.
        Assert.NotEqual(authorA, authorB);
        Assert.NotEqual("local", authorA);
        Assert.NotEqual("local", authorB);
        // And distinct because the signing keys are distinct (the key8 suffix differs).
        Assert.NotEqual(nodeA.Signer.NodePublicKey, nodeB.Signer.NodePublicKey);
    }

    // ── TEST 3: author↔signing-key consistency — the operator's OWN messages pass the forge-proof gate. ─────

    [Fact(DisplayName = "gap #2: the stamped author is roster-bound to the signing key → forge-proof gate passes own messages")]
    public async Task StampedAuthor_IsBoundToSigningKey_ForgeProofGatePassesOwnMessages()
    {
        var node = await NewShippingNodeAsync(Seed(0x30));

        // The route stamped (authorPartyId, authorIssuerId). The consistency the forge-proof gate requires is:
        // authorPartyId is the roster party BOUND to authorIssuerId — so the operator's own message is not dropped.
        var resp = await node.Client.PostAsJsonAsync(Route, new { body = "my own message" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var stampedAuthor = created.GetProperty("authorPartyId").GetString()!;
        var stampedIssuer = created.GetProperty("authorIssuerId").GetString()!;

        // The route signed with the node's principal key.
        Assert.Equal(node.Signer.NodePublicKey, stampedIssuer);

        // The roster the production gate consumes binds the stamped author to the node's signing key.
        var roster = new NodeTeamRoster(MemberRoster.Genesis(
            TeamId, node.AuthorPartyId, node.Signer.Signer, Verifier, System.DateTimeOffset.UtcNow, System.Guid.NewGuid()));

        // FORGE-PROOF (passes own messages): the stamped author IS the roster party bound to the stamped key.
        var boundKey = roster.ForgeProofBinding(stampedAuthor);
        Assert.NotNull(boundKey);
        Assert.Equal(stampedIssuer, boundKey!.Value.ToBase64Url());

        // And the inverse holds (don't regress #1277-B1): a claim for a party NOT in the roster has NO binding
        // → fail-closed (dropped). A forger who signs with their own key but stamps another party fails too.
        Assert.Null(roster.ForgeProofBinding("os:someone-else#deadbeef"));
    }

    // ── TEST 4: the host consistency assertion FAILS-CLOSED on an author/key mismatch (no shipping a self-drop). ──

    [Fact(DisplayName = "gap #2: HostedCommsApiEndpoint fails-closed when the author is not roster-bound to the signing key")]
    public async Task HostedEndpoint_FailsClosed_OnAuthorKeyMismatch()
    {
        // The seeded roster binds a DIFFERENT key than the comms signer — the exact composition error the
        // assertion guards (the operator's own messages would otherwise be dropped by the production merge gate).
        var commsSigner = new NodePrincipalSigner(Seed(0x50));
        _signers.Add(commsSigner);
        var otherSigner = new NodePrincipalSigner(Seed(0x60)); // binds a DIFFERENT key into the roster
        _signers.Add(otherSigner);

        var dir = Path.Combine(Path.GetTempPath(), "harborline-comms-gap2-mismatch-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);

        // A minimal CommsConversationRegistry over its own SQLite store (the assertion throws BEFORE any
        // conversation projection is touched, but the endpoint ctor requires a non-null registry).
        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddLogging();
        services.AddDbContextFactory<NodeLocalCommsDbContext>(opt =>
            opt.UseSqlite($"Data Source={Path.Combine(dir, "comms.db")};Pooling=False"));
        services.AddSingleton<ICrdtEngine, YDotNetCrdtEngine>();
        services.AddSingleton<IActiveTeamAccessor>(NodeTestActiveTeam.Accessor);
        services.AddSingleton(new NodeCallerSessionToken(null));
        services.AddHarborlineDeltaRouter();
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<NodeLocalCommsDbContext>>();
        await using (var ctx = await factory.CreateDbContextAsync())
            await ctx.Database.EnsureCreatedAsync();

        // Roster binds "os:mismatch" to OTHER's key; the endpoint signs with commsSigner — they differ.
        var roster = new NodeTeamRoster(MemberRoster.Genesis(
            TeamId, "os:mismatch", otherSigner.Signer, Verifier, System.DateTimeOffset.UtcNow, System.Guid.NewGuid()));
        var registry = new CommsConversationRegistry(
            sp.GetRequiredService<ICrdtEngine>(), factory, new Ed25519Verifier(),
            sp.GetRequiredService<IDeltaRouter>(), sp.GetRequiredService<ILoggerFactory>(),
            rosterBinding: roster.ForgeProofBinding);
        _asyncDisposables.Add(registry);

        // SharedHostedWebApp constructed directly (mirrors SharedHostedWebAppCallerAuthTests). It is never
        // started — the consistency assertion throws before StartAsync touches it.
        var sharedApp = new SharedHostedWebApp(
            sp,
            Microsoft.Extensions.Options.Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SharedHostedWebApp>>(),
            sp.GetRequiredService<TimeProvider>());

        var endpoint = new HostedCommsApiEndpoint(
            sharedApp, registry, commsSigner, NodeTestActiveTeam.Accessor, new NodeCallerSessionToken(null), roster,
            new CommsDmFeatureFlag(enabled: false), sp.GetRequiredService<TimeProvider>(),
            NullLogger<HostedCommsApiEndpoint>.Instance);

        // FAIL-CLOSED: the roster binds the author to OTHER's key, not the comms signer's → StartAsync throws.
        var ex = await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => endpoint.StartAsync(CancellationToken.None));
        Assert.Contains("author binding is inconsistent", ex.Message);

        await sharedApp.DisposeAsync();
    }
}
