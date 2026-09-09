using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.LocalNodeHost.Enrollment;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// inc-4 — the LOAD-BEARING caller-auth security assertions for
/// <see cref="NodeCallerSessionToken"/>. A loopback bind authenticates the host, not the
/// calling process; this proves the per-boot session-token guard turns away a local
/// stranger <b>fail-closed (401)</b> while letting the token-bearing Harborline App through.
/// </summary>
/// <remarks>
/// <para>
/// Mounts the REAL <see cref="CurrentPrincipalSignatureRoutes.Map"/> route (the canonical
/// node-key SIGNING surface — the highest-value attestation route) on a real in-process
/// Kestrel listener, with the SAME caller-auth overload the hosted endpoint uses, and drives
/// it with an <see cref="HttpClient"/>. The unit-level <see cref="NodeCallerSessionToken"/>
/// facts pin the constant-time compare + the un-enforced dev fallback directly.
/// </para>
/// <para>
/// This is the security gate the inc-4 deep-review hangs on: "does a missing/wrong token get
/// rejected fail-closed?" — proven end-to-end over HTTP, not just asserted in prose.
/// </para>
/// </remarks>
public sealed class NodeCallerSessionTokenTests : IAsyncLifetime
{
    private const string Token = "carrier-per-boot-session-token-0123456789abcdef";
    private const string SignatureRoute = "/api/local-node/current-principal-signature";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _baseUrl = null!;
    private NodePrincipalSigner _nodeSigner = null!;
    private NodeTeamRoster _roster = null!;

    // Fixed 32-byte seed → deterministic node identity (mirrors the sibling signing-route suite).
    private static readonly byte[] FixedSeed = Enumerable.Repeat((byte)0x07, 32).ToArray();

    public async Task InitializeAsync()
    {
        _nodeSigner = new NodePrincipalSigner(FixedSeed);
        _roster = new NodeTeamRoster(MemberRoster.Genesis(
            Guid.Parse("29400000-0000-4000-8000-000000000041"),
            "canonical-token-test-principal-294",
            _nodeSigner.Signer,
            new Ed25519Verifier(),
            DateTimeOffset.UnixEpoch,
            Guid.Parse("29400000-0000-4000-8000-000000000042")));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        // The SAME production route the hosted endpoint maps — but WITH the caller-auth
        // guard ENFORCED (a configured token). One source of truth for the wire contract.
        var callerAuth = new NodeCallerSessionToken(Token);
        CurrentPrincipalSignatureRoutes.Map(
            _app,
            _nodeSigner.Signer,
            _nodeSigner.NodePublicKey,
            callerAuth,
            () => HostedCurrentPrincipalSignatureApiEndpoint.ResolveCurrentPrincipal(_roster, _nodeSigner),
            TimeProvider.System);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
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

    // ── The load-bearing fail-closed assertions ────────────────────────────────────

    [Fact(DisplayName = "Caller-auth: a VALID bearer token is ALLOWED (200) and the route signs")]
    public async Task ValidBearer_Allowed()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, SignatureRoute);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(doc.TryGetProperty("signature", out _), "An authenticated call still returns the signed envelope.");
    }

    [Fact(DisplayName = "Caller-auth: a MISSING Authorization header is REJECTED fail-closed (401) — the stranger case")]
    public async Task MissingHeader_Rejected_FailClosed()
    {
        // A local stranger process that just hits the loopback port with NO token.
        using var res = await _client.GetAsync(SignatureRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        // And the node did NOT sign anything for the stranger — the 401 body is the auth error.
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("caller_unauthenticated", doc.GetProperty("error").GetString());
        Assert.False(doc.TryGetProperty("signature", out _), "A rejected call must NOT return a signature.");
    }

    [Fact(DisplayName = "Caller-auth: a WRONG bearer token is REJECTED fail-closed (401)")]
    public async Task WrongBearer_Rejected_FailClosed()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, SignatureRoute);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token + "-tampered");

        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact(DisplayName = "Caller-auth: a non-Bearer scheme (Basic) is REJECTED fail-closed (401)")]
    public async Task WrongScheme_Rejected_FailClosed()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, SignatureRoute);
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Token);

        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact(DisplayName = "Caller-auth: an EMPTY bearer value is REJECTED fail-closed (401)")]
    public async Task EmptyBearer_Rejected_FailClosed()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, SignatureRoute);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer ");

        using var res = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── Unit-level guard contract (constant-time compare + dev fallback) ────────────

    [Fact(DisplayName = "Guard unit: enforced token matches the exact secret, rejects everything else")]
    public void Guard_Enforced_MatchesExactSecretOnly()
    {
        var guard = new NodeCallerSessionToken(Token);

        Assert.True(guard.IsEnforced);
        Assert.True(guard.Matches(Token));
        Assert.False(guard.Matches(Token + "x"));      // longer
        Assert.False(guard.Matches(Token[..^1]));      // shorter (prefix)
        Assert.False(guard.Matches("totally-different"));
        Assert.False(guard.Matches(null));
        Assert.False(guard.Matches(""));
    }

    [Fact(DisplayName = "Guard unit: NO token configured ⇒ un-enforced (dev/single-host-trusted) — permits, never matches")]
    public void Guard_NoToken_UnEnforced_Permits()
    {
        var devGuard = new NodeCallerSessionToken(null);
        Assert.False(devGuard.IsEnforced);
        // Un-enforced mode permits the call regardless of header (dev/Bridge-tenant fallback)...
        var blankGuard = new NodeCallerSessionToken("   ");
        Assert.False(blankGuard.IsEnforced);
        // ...but Matches() still returns false (there is no secret to match), so a future
        // caller can't be tricked into believing an un-enforced guard "authenticated" anything.
        Assert.False(devGuard.Matches("anything"));
        Assert.False(devGuard.Matches(null));
    }
}
