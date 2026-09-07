using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Docs;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local document routes onto the shared Kestrel
/// listener (ADR 0127 — the documents node-flip / T4).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="DocumentRoutes.Map"/> (the single source of truth shared with the route
/// tests so the wire contract has no test/prod drift). Reads + the upload pipeline +
/// the cross-cluster link service are resolved from the composition root.
/// </para>
/// <para>
/// The services are passed to <see cref="DocumentRoutes.Map"/> as a service tuple.
/// </para>
/// <para>
/// Mapping order is explicit in <see cref="LocalNodeEndpointMapping"/>.
/// </para>
/// </remarks>
public sealed class HostedDocumentApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IServiceProvider _services;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly ILogger<HostedDocumentApiEndpoint> _logger;

    /// <summary>Constructs the hosted document API endpoint.</summary>
    public HostedDocumentApiEndpoint(
        SharedHostedWebApp sharedApp,
        IServiceProvider services,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedDocumentApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _services = services;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var docs = (
            _services.GetRequiredService<Harborline.Api.Blocks.Docs.Services.IAttachmentRepository>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Docs.Services.IAttachmentService>(),
            _services.GetRequiredService<Harborline.Api.Blocks.Docs.Services.IDocumentRefService>());
        _sharedApp.MapApiRoutes(app => DocumentRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            docs,
            _activeTeam));

        _logger.LogInformation(
            "Node-local documents API registered over the recoverable local-node store " +
            "(GET/POST {RouteBase}, GET {RouteBase}/{{id}}, GET {RouteBase}/{{id}}/content, " +
            "POST {RouteBase}/{{id}}/attach) — bytes inline in SQLCipher, 25 MB inline ceiling enforced.",
            DocumentRoutes.RouteBase,
            DocumentRoutes.RouteBase,
            DocumentRoutes.RouteBase,
            DocumentRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
