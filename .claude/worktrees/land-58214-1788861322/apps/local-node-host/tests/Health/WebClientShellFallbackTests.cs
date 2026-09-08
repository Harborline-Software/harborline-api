using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Entities;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// A hard load of a CLIENT-SIDE route must reach the SPA shell, and the fallback must never answer for
/// the server's own paths (card earlier repository ticket #3489).
/// </summary>
/// <remarks>
/// <para>
/// <c>UseDefaultFiles</c> resolves only the bare origin, so before the fallback a PATH-form client route
/// such as <c>/team</c> 404'd. The Harborline App uses <c>createHashRouter</c>, so its own URLs carry the route in
/// the fragment and a reload sends <c>GET /</c> — those always worked. The path form is reached by a
/// hand-typed or externally-authored link, and by cmd- or middle-clicking the app's own breadcrumbs, which
/// are real anchors whose left-click handler an open-in-new-tab bypasses. Latent until the front door
/// landed (#3329).
/// </para>
/// <para>
/// The fallback sits BEFORE the caller-auth gate, alongside the static middleware, so its path predicate
/// is the only thing between the API surface and an HTML body. A fallback that caught everything would
/// turn an unauthenticated API call into a 200 with HTML — worse than the 404 it replaced. Each test
/// below pins one guard, and each asserts on the BODY as well as the status: a 404 whose body is the
/// shell would satisfy a status-only assertion while being exactly the bug.
/// </para>
/// <para>
/// Caller-auth is ENFORCED here and a real API route is mapped. Without both, "an unknown /api path gets a
/// real status" only proves that routing 404s — not that the gate still answers, which is the claim.
/// </para>
/// </remarks>
public sealed class WebClientShellFallbackTests : IAsyncLifetime
{
    private const string ShellMarker = "<!-- harborline-web-client-shell -->";
    private const string CallerToken = "shell-fallback-caller-token-0001";
    private const string MappedApiRoute = "/api/local-node/shell-fallback-probe";

    private string _bundleRoot = string.Empty;
    private SharedHostedWebApp? _app;
    private ServiceProvider? _provider;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        _bundleRoot = Path.Combine(Path.GetTempPath(), $"harborline-shell-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_bundleRoot);
        Directory.CreateDirectory(Path.Combine(_bundleRoot, "assets"));
        await File.WriteAllTextAsync(
            Path.Combine(_bundleRoot, "index.html"),
            $"<!doctype html><html><body>{ShellMarker}</body></html>");
        await File.WriteAllTextAsync(
            Path.Combine(_bundleRoot, "assets", "main-a1b2c3.js"), "export const real = true;");

        var services = new ServiceCollection();
        services.AddTestKernelClock();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton<IActiveTeamAccessor>(NodeTestActiveTeam.Accessor);
        // ENFORCED, and with a real API route mapped below. With a null token the gate waves everyone
        // through and no route exists, so "an unknown /api path gets a real status" would only be proving
        // that routing 404s — not that the caller-auth gate still answers, which is the actual claim.
        services.AddSingleton(new NodeCallerSessionToken(CallerToken));
        services.Configure<NodeWebClientOptions>(o =>
        {
            o.Enabled = true;
            o.BundleRoot = _bundleRoot;
        });
        _provider = services.BuildServiceProvider();

        _app = new SharedHostedWebApp(
            _provider,
            Options.Create(new LocalNodeOptions { HealthPort = 0 }),
            new Harborline.Api.LocalNodeHost.Capabilities.LocalNodeExecutableEndpointRegistry(),
            _provider.GetRequiredService<ILogger<SharedHostedWebApp>>(),
            _provider.GetRequiredService<TimeProvider>());

        _app.MapApiRoutes(a => a.MapGet(MappedApiRoute, () => Results.Ok(new { ok = true })));

        await _app.StartAsync(CancellationToken.None);
        _client = new HttpClient { BaseAddress = new Uri(_app.SelectedUrl!) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null) { await _app.StopAsync(CancellationToken.None); await _app.DisposeAsync(); }
        if (_provider is not null) await _provider.DisposeAsync();
        try { if (Directory.Exists(_bundleRoot)) Directory.Delete(_bundleRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    [Theory]
    [InlineData("/team")]           // the path form of a real client route
    [InlineData("/admin/access")]   // nested — the fallback is not depth-limited
    [InlineData("/apish")]          // a client route whose name merely STARTS with a reserved prefix
    public async Task A_hard_load_of_a_client_route_serves_the_shell(string path)
    {
        var response = await _client!.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ShellMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_bare_origin_still_serves_the_shell()
    {
        // UseDefaultFiles already handled this one; pinned so the fallback cannot regress it.
        var response = await _client!.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(ShellMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/api/local-node/does-not-exist")]
    [InlineData("/api/session/does-not-exist")]
    [InlineData("/health/does-not-exist")]
    [InlineData("/ws/does-not-exist")]
    public async Task An_unknown_server_path_gets_a_real_status_and_never_the_shell(string path)
    {
        // THE guard the card names. A fallback that answered here would turn an unauthenticated API call
        // into a 200 with an HTML body — worse than the 404 it replaced.
        var response = await _client!.GetAsync(path);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            ShellMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mapped_api_route_still_answers_401_without_a_token()
    {
        // THE property the fallback must not break, and the one the first revision of this suite could not
        // see: caller-auth is ENFORCED here and this route really exists, so a 401 is the gate answering —
        // not routing failing to match. A fallback that swallowed it would return 200 with HTML.
        var response = await _client!.GetAsync(MappedApiRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(
            ShellMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mapped_api_route_still_serves_a_caller_holding_the_token()
    {
        // The control on the control: if the gate rejected everything, the test above would pass for the
        // wrong reason.
        var request = new HttpRequestMessage(HttpMethod.Get, MappedApiRoute);
        request.Headers.Add(NodeCallerSessionToken.HeaderName, "Bearer " + CallerToken);

        var response = await _client!.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    // Kestrel does not decode %2F, does not collapse //, and passes ; and \\ through, so a prefix-only
    // test let each of these reach the shell — turning a logged 401 into a silent 200. None ever selected
    // an endpoint, so nothing was reachable that should not have been; the STATUS is what regressed.
    //
    // Sent over a raw socket rather than HttpClient: HttpClient rewrites some of these before they leave
    // (it reads a leading // as protocol-relative and tries to connect to a host called "api"), so a
    // client-level test would be asserting on a request the server never sees.
    [InlineData("/api%2flocal-node%2fshell-fallback-probe")]
    [InlineData("//api/local-node/shell-fallback-probe")]
    [InlineData("/api;/local-node/shell-fallback-probe")]
    [InlineData("/api\\local-node\\shell-fallback-probe")]
    [InlineData("/%2e%2e/api/local-node/shell-fallback-probe")]
    public async Task An_encoded_path_never_gets_the_shell(string target)
    {
        var raw = await SendRawAsync(target);

        Assert.DoesNotContain(ShellMarker, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(" 200 ", raw[..Math.Min(raw.Length, 16)], StringComparison.Ordinal);
    }

    /// <summary>Writes a request line verbatim, so the server sees exactly the target under test.</summary>
    private async Task<string> SendRawAsync(string target)
    {
        var uri = new Uri(_app!.SelectedUrl!);
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port);
        await using var stream = tcp.GetStream();

        var request = $"GET {target} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\nConnection: close\r\n\r\n";
        var bytes = System.Text.Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task A_missing_asset_declines_and_never_answers_html()
    {
        // The property is that the fallback DECLINES, never that a particular status comes back: with
        // caller-auth enforced the gate answers 401 before routing reaches its 404. Either is correct.
        // Answering HTML would produce a MIME-type failure in the browser instead of a missing-asset
        // error, hiding the real fault behind a confusing one.
        var response = await _client!.GetAsync("/assets/main-does-not-exist.js");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            ShellMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_asset_is_still_served_as_itself()
    {
        // The control: if the fallback ran ahead of the static handler, every asset would come back as
        // the shell and the app would never boot.
        var response = await _client!.GetAsync("/assets/main-a1b2c3.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("export const real", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_post_to_an_unknown_path_never_gets_the_shell()
    {
        // Same harm as swallowing /api, reached by a different door: a write that silently succeeds with
        // a 200 and an HTML body.
        var response = await _client!.PostAsync("/team", new StringContent(string.Empty));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            ShellMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
