using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Documents.Issuance;
using Harborline.Api.Foundation.Documents.Rendering;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.People;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the Documents pillar D3 render/issue routes onto the shared Kestrel listener
/// (#111 §6 D3 — "the render/issue HTTP endpoint on the node is NOT built yet" per the D1/D2 handoff).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping lives in
/// <see cref="DocumentTemplateRoutes.Map"/> (the single source of truth). The document-issuance pipeline
/// (<see cref="DocumentIssuanceService"/> / <see cref="IDocumentTemplateRegistry"/> / <see cref="IPdfExportWriter"/>)
/// plus the invoice and party repositories are resolved from the composition root and passed as
/// closed-over dependencies.
/// </para>
/// <para>
/// Mapping order is explicit in <see cref="LocalNodeEndpointMapping"/>.
/// </para>
/// </remarks>
public sealed class HostedDocumentTemplateApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly DocumentIssuanceService _issuance;
    private readonly IDocumentTemplateRegistry _registry;
    private readonly IPdfExportWriter _writer;
    private readonly NodeEfInvoiceRepository _invoices;
    private readonly NodeEfPartyRepository _parties;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly TimeProvider _time;
    private readonly ILogger<HostedDocumentTemplateApiEndpoint> _logger;

    /// <summary>Constructs the hosted document-templates API endpoint.</summary>
    public HostedDocumentTemplateApiEndpoint(
        SharedHostedWebApp sharedApp,
        DocumentIssuanceService issuance,
        IDocumentTemplateRegistry registry,
        IPdfExportWriter writer,
        NodeEfInvoiceRepository invoices,
        NodeEfPartyRepository parties,
        IActiveTeamAccessor activeTeam,
        TimeProvider time,
        ILogger<HostedDocumentTemplateApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(issuance);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(parties);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _issuance = issuance;
        _registry = registry;
        _writer = writer;
        _invoices = invoices;
        _parties = parties;
        _activeTeam = activeTeam;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => DocumentTemplateRoutes.Map(
            app.MapSelectedSessionProductGroup(),
            _issuance,
            _registry,
            _writer,
            _invoices,
            _parties,
            _activeTeam,
            _time));

        _logger.LogInformation(
            "Documents pillar D3 render/issue API registered (POST {RouteBase}/render — non-minting preview; "
            + "POST {RouteBase}/issue — CP mint through DocumentIssuanceService).",
            DocumentTemplateRoutes.RouteBase,
            DocumentTemplateRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
