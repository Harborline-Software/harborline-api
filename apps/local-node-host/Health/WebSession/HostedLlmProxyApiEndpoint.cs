using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harborline.Api.LocalNodeHost.Health.WebSession;

/// <summary>
/// Hosted service that maps the session-gated LLM proxy (<c>/api/llm/v1/*</c>) onto the shared
/// listener when an upstream is configured (<c>LocalNode:WebClient:LlmUpstreamBase</c>). Owns a
/// single long-lived <see cref="HttpClient"/> to the fixed loopback upstream (a generous timeout for
/// streaming generations). Registered only when the web-client profile is enabled AND an upstream is set.
/// </summary>
public sealed class HostedLlmProxyApiEndpoint : IHostedService, IDisposable
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly NodeWebClientOptions _options;
    private readonly ILogger<HostedLlmProxyApiEndpoint> _logger;
    private readonly HttpClient _client;

    /// <summary>Constructs the hosted LLM proxy endpoint.</summary>
    public HostedLlmProxyApiEndpoint(
        SharedHostedWebApp sharedApp,
        IOptions<NodeWebClientOptions> options,
        ILogger<HostedLlmProxyApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _options = options.Value;
        _logger = logger;
        // A streaming generation can run for minutes; per-request cancellation still rides RequestAborted.
        _client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var upstream = _options.LlmUpstreamBase;
        if (string.IsNullOrWhiteSpace(upstream))
        {
            _logger.LogInformation(
                "LLM proxy: no upstream configured (LocalNode:WebClient:LlmUpstreamBase) — not mapped; Pilot falls back to echo.");
            return Task.CompletedTask;
        }

        _sharedApp.MapApiRoutes(app => LlmProxyRoutes.Map(
            app.MapSelectedSessionProductGroup(),
            _client,
            upstream,
            _logger));
        _logger.LogInformation(
            "LLM proxy registered: {RouteBase}/v1/* -> {Upstream} (session-gated, same-origin).",
            LlmProxyRoutes.RouteBase, upstream);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
