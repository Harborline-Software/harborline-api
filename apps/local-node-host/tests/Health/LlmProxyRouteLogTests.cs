using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Diagnostics;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

/// <summary>
/// CodeQL #27: when the LLM upstream is unreachable, the proxy logs the caller's decoded <c>{**rest}</c> path
/// bounded and escaped, and never the assembled target with its query string.
/// </summary>
public sealed class LlmProxyRouteLogTests : IAsyncLifetime
{
    private const string Upstream = "http://127.0.0.1:9";

    private readonly LogCapture _logs = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        LlmProxyRoutes.Map(
            _app,
            new HttpClient(new UnreachableUpstream()),
            Upstream,
            _logs.CreateLogger("LlmProxyRoutes"));
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // The probe is U+2028, a line separator LogText escapes, rather than CR/LF: the route builds the upstream
    // Uri from the decoded path outside its try, so a CR/LF there would test System.Uri, not the log line.
    [Fact]
    public async Task Unreachable_upstream_logs_a_line_separator_path_escaped_and_drops_the_query_string()
    {
        var response = await _client.GetAsync(
            $"{LlmProxyRoutes.RouteBase}/v1/models{Uri.EscapeDataString("\u2028forged")}?api_key=query-secret");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var line = Assert.Single(_logs.Records);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal(Upstream, line.Value("Upstream"));
        Assert.Equal("v1/models\\u2028forged", line.Value("Path"));
        Assert.DoesNotContain("\u2028", line.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("query-secret", _logs.AllText(), StringComparison.Ordinal);
    }

    /// <summary>An upstream that is never reachable, so every proxied call takes the logged 502 path.</summary>
    private sealed class UnreachableUpstream : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("upstream unreachable (test double)");
    }
}
