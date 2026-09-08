using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;
using Xunit.Abstractions;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// ADR 0134 P1b-2 / P2 — route-level tests for the canonical-node-key principal
/// SIGNING route (<see cref="CurrentPrincipalSignatureRoutes"/>). Resolves the
/// P2 "node-key reachability" flag: the canonical .NET node key now HAS a reachable,
/// tested signing surface.
/// </summary>
/// <remarks>
/// <para>
/// Hosts the SAME route handler <see cref="CurrentPrincipalSignatureRoutes.Map"/>
/// registers, on a real in-process Kestrel listener bound to an ephemeral loopback
/// port, with a FIXED-SEED <see cref="NodePrincipalSigner"/> so the issuerId /
/// node-public-key are deterministic, and drives it with a real
/// <see cref="HttpClient"/>. Proves end-to-end: the bounded host-resolved principal,
/// the camelCase signed-principal envelope shape, the node-public-key trust anchor,
/// the canonical-form signature self-verifies, and — critically — that the route is
/// NOT a signing oracle (no request body / query selects WHAT is signed).
/// </para>
/// <para>
/// The cross-LANGUAGE proof (this .NET envelope → the TS <c>verifySignedPrincipal</c>
/// path) is pinned in <c>apps/capability-host/src/membrane/node-route-principal.interop.test.ts</c>
/// from the deterministic vector this suite's <see cref="EmitDeterministicVectorForTsPin"/>
/// fact prints.
/// </para>
/// </remarks>
public sealed class CurrentPrincipalSignatureRouteTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _baseUrl = null!;
    private NodePrincipalSigner _nodeSigner = null!;

    // A fixed 32-byte seed (all 0x07) → a deterministic node identity for assertions.
    private static readonly byte[] FixedSeed = Enumerable.Repeat((byte)0x07, 32).ToArray();

    public CurrentPrincipalSignatureRouteTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _nodeSigner = new NodePrincipalSigner(FixedSeed);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        _app = builder.Build();

        // Map the SAME production route the hosted endpoint maps — one source of truth.
        CurrentPrincipalSignatureRoutes.Map(_app, _nodeSigner.Signer, _nodeSigner.NodePublicKey, TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _baseUrl = addresses!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _nodeSigner?.Dispose();
    }

    private const string Route = "/api/local-node/current-principal-signature";

    [Fact(DisplayName = "Route: GET returns the signed-principal envelope (camelCase wire shape)")]
    public async Task Get_ReturnsSignedPrincipalEnvelope()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);

        // The camelCase envelope matching TS SignedPrincipalEnvelope + nodePublicKey.
        Assert.True(doc.TryGetProperty("principal", out var principal));
        Assert.True(doc.TryGetProperty("issuerId", out var issuerId));
        Assert.True(doc.TryGetProperty("issuedAt", out var issuedAt));
        Assert.True(doc.TryGetProperty("nonce", out var nonce));
        Assert.True(doc.TryGetProperty("signature", out var signature));
        Assert.True(doc.TryGetProperty("nodePublicKey", out var nodePublicKey));

        // issuedAt is a BARE integer epoch-ms (not an ISO string) — the canonical form.
        Assert.Equal(JsonValueKind.Number, issuedAt.ValueKind);
        Assert.True(issuedAt.GetInt64() > 0);

        // issuerId == nodePublicKey == the fixed-seed node identity (deterministic).
        Assert.Equal(_nodeSigner.NodePublicKey, issuerId.GetString());
        Assert.Equal(_nodeSigner.NodePublicKey, nodePublicKey.GetString());

        // nonce is the lowercase-hyphenated UUID text form.
        Assert.True(Guid.TryParseExact(nonce.GetString(), "D", out _));

        // signature is base64url of the raw 64 bytes.
        var sigBytes = Base64UrlDecode(signature.GetString()!);
        Assert.Equal(64, sigBytes.Length);
    }

    [Fact(DisplayName = "Route: the signed principal is the HOST-RESOLVED OS user (os:<user>, local-os-user)")]
    public async Task Get_SignsHostResolvedPrincipal()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var principal = doc.GetProperty("principal");

        // Host-resolved — matches ResolveCurrentPrincipal() (Environment.UserName).
        var expected = CurrentPrincipalSignatureRoutes.ResolveCurrentPrincipal();
        Assert.Equal(expected.Id, principal.GetProperty("id").GetString());
        Assert.StartsWith("os:", principal.GetProperty("id").GetString());
        Assert.Equal("local-os-user", principal.GetProperty("kind").GetString());
        // Non-anonymous — the gate never signs an anonymous principal.
        Assert.True(principal.GetProperty("id").GetString()!.Length > "os:".Length);
    }

    [Fact(DisplayName = "274: a DECOMPOSED OS user name is minted at the derivation, so both consumers wrap it without throwing")]
    public async Task DecomposedOsUserName_IsMinted_AndReachesBothConsumers()
    {
        // macOS stores account names decomposed: "jose" + U+0301 rather than precomposed "josé".
        const string Decomposed = "josé";
        const string Composed = "josé";
        Assert.NotEqual(Composed, Decomposed);            // different strings...
        Assert.Equal(Composed, Decomposed.Normalize());   // ...one account name.

        var decomposedPrincipal = CurrentPrincipalSignatureRoutes.ResolveCurrentPrincipal(Decomposed);
        var composedPrincipal = CurrentPrincipalSignatureRoutes.ResolveCurrentPrincipal(Composed);

        // The EXACT expression KgSearchRoutes.cs:111 and KgCalendarDevIndexer.cs:240 evaluate. Before the
        // mint at the derivation site this threw ArgumentException out of the GET handler -> 500.
        var decomposedActor = new ActorId(decomposedPrincipal.Id);
        var composedActor = new ActorId(composedPrincipal.Id);
        Assert.Equal(composedActor, decomposedActor);
        Assert.Equal("os:" + Composed, decomposedActor.Value);

        // And a padded name still trims (the pre-274 behaviour the mint must preserve).
        Assert.Equal(composedActor, new ActorId(CurrentPrincipalSignatureRoutes.ResolveCurrentPrincipal("  " + Decomposed + " ").Id));

        // Route level: the signed principal id is itself a wrappable ActorId, on this host.
        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);
        var signedId = doc.GetProperty("principal").GetProperty("id").GetString()!;
        Assert.True(ActorId.IsCanonical(signedId), $"The route signed a non-canonical principal id '{signedId}'.");
        _ = new ActorId(signedId);
    }

    [Fact(DisplayName = "Route: BOUNDED — a request body cannot select an arbitrary principal (no signing oracle)")]
    public async Task Post_WithArbitraryPrincipal_DoesNotSignIt()
    {
        // Attempt to coax the route into signing an attacker-chosen principal by POSTing
        // a body. The route is GET-only with NO body binding — a POST is 404/405, and a
        // GET ignores any query. Either way the attacker's principal is NEVER signed.
        var attacker = new { id = "os:root", displayName = "ATTACKER", kind = "spoofed" };

        var post = await _client.PostAsJsonAsync(Route, attacker);
        Assert.True(
            post.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"Expected 404/405 for a POST body principal, got {(int)post.StatusCode}.");

        // And a GET with an attacker query string still signs ONLY the host principal.
        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Route}?id=os:root&kind=spoofed");
        var signedId = doc.GetProperty("principal").GetProperty("id").GetString();
        var hostId = CurrentPrincipalSignatureRoutes.ResolveCurrentPrincipal().Id;
        Assert.Equal(hostId, signedId);
        Assert.NotEqual("os:root", signedId);
    }

    [Fact(DisplayName = "Route: the signature verifies over the canonical form against the node public key")]
    public async Task Get_SignatureVerifies_OverCanonicalForm()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>(Route);

        var principal = new CurrentPrincipalSignatureRoutes.SignedPrincipalPayload(
            Id: doc.GetProperty("principal").GetProperty("id").GetString()!,
            DisplayName: doc.GetProperty("principal").GetProperty("displayName").GetString(),
            Kind: doc.GetProperty("principal").GetProperty("kind").GetString());
        var issuerId = PrincipalId.FromBase64Url(doc.GetProperty("issuerId").GetString()!);
        var issuedAtMs = doc.GetProperty("issuedAt").GetInt64();
        var nonce = Guid.ParseExact(doc.GetProperty("nonce").GetString()!, "D");
        var signature = Signature.FromBase64Url(doc.GetProperty("signature").GetString()!);

        // Rebuild the canonical signable bytes the route signed and verify the signature
        // resolves against the node public key (issuerId). Tamper → fails.
        var signable = CanonicalJson.SerializeSignable(
            principal,
            issuerId,
            DateTimeOffset.FromUnixTimeMilliseconds(issuedAtMs),
            nonce);

        Assert.True(
            KeyPair.VerifyRaw(issuerId.AsSpan(), signable, signature.AsSpan()),
            "The route signature must verify over the canonical signable bytes.");

        // Tampering the principal id breaks verification.
        var tampered = CanonicalJson.SerializeSignable(
            principal with { Id = "os:someone-else" }, issuerId,
            DateTimeOffset.FromUnixTimeMilliseconds(issuedAtMs), nonce);
        Assert.False(KeyPair.VerifyRaw(issuerId.AsSpan(), tampered, signature.AsSpan()));
    }

    [Fact(DisplayName = "Route: distinct calls use distinct nonces (replay defence input)")]
    public async Task Get_DistinctCalls_DistinctNonces()
    {
        var a = await _client.GetFromJsonAsync<JsonElement>(Route);
        var b = await _client.GetFromJsonAsync<JsonElement>(Route);
        Assert.NotEqual(
            a.GetProperty("nonce").GetString(),
            b.GetProperty("nonce").GetString());
    }

    /// <summary>
    /// Emits a DETERMINISTIC signed-principal envelope (fixed seed + fixed principal +
    /// fixed issuedAt + fixed nonce) as JSON to test output. The TS interop test pins
    /// this exact envelope and runs it through the REAL <c>verifySignedPrincipal</c>,
    /// proving the canonical-node-key → TS-verify path end-to-end. Re-run this fact and
    /// re-pin if the canonical form ever changes (it should not — #1254 froze it).
    /// </summary>
    [Fact(DisplayName = "Vector: emit a deterministic .NET-signed envelope for the TS interop pin")]
    public async Task EmitDeterministicVectorForTsPin()
    {
        using var signer = new NodePrincipalSigner(FixedSeed);

        // A FIXED principal (not Environment.UserName) so the vector is host-independent.
        var principal = new CurrentPrincipalSignatureRoutes.SignedPrincipalPayload(
            Id: "os:alice",
            DisplayName: "Alice Example",
            Kind: "local-os-user");
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(1781827200000);
        var nonce = Guid.ParseExact("11111111-2222-3333-4444-555555555555", "D");

        var op = await signer.Signer.SignAsync(principal, issuedAt, nonce);

        var envelope = new
        {
            principal = new
            {
                id = principal.Id,
                displayName = principal.DisplayName,
                kind = principal.Kind,
            },
            issuerId = op.IssuerId.ToBase64Url(),
            issuedAt = op.IssuedAt.ToUnixTimeMilliseconds(),
            nonce = op.Nonce.ToString("D"),
            signature = op.Signature.ToBase64Url(),
            nodePublicKey = signer.NodePublicKey,
        };

        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
        _output.WriteLine("=== DETERMINISTIC .NET SIGNED-PRINCIPAL ENVELOPE (pin into TS interop test) ===");
        _output.WriteLine(json);

        // Robust capture: also write to a deterministic temp path so the vector can be
        // read back cleanly regardless of console-logger CR mangling (used only to author
        // the TS pin; harmless in CI).
        var capturePath = Environment.GetEnvironmentVariable("CPSR_VECTOR_CAPTURE_PATH");
        if (!string.IsNullOrWhiteSpace(capturePath))
        {
            File.WriteAllText(capturePath, json, Encoding.UTF8);
        }

        // Sanity: it self-verifies over the canonical form here too.
        var signable = CanonicalJson.SerializeSignable(principal, op.IssuerId, issuedAt, nonce);
        Assert.True(KeyPair.VerifyRaw(op.IssuerId.AsSpan(), signable, op.Signature.AsSpan()));

        // The payload canonicalizes with camelCase keys (id/displayName/kind), byte-identical
        // to the TS serializeSignablePrincipal payload — the load-bearing cross-language
        // contract (a PascalCase Id would diverge + break the TS verify). Assert it here so a
        // regression in the payload's [JsonPropertyName] pins is caught WITHOUT re-running TS.
        var canon = Encoding.UTF8.GetString(signable);
        Assert.Contains("\"payload\":{\"displayName\":\"Alice Example\",\"id\":\"os:alice\",\"kind\":\"local-os-user\"}", canon);

        // Assert the deterministic node public key (so a regression in FromSeed is caught).
        Assert.Equal(signer.NodePublicKey, op.IssuerId.ToBase64Url());
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
