using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the D2 node-local submission-DRAFT routes (save / resume / abandon /
/// list) onto the shared Kestrel listener (ADR 0135 amendment 2026-07-01). The save-and-resume
/// companion to <see cref="HostedFormsApiEndpoint"/>.
/// </summary>
/// <remarks>
/// The routes are SCOPED-service-backed (fail-closed party resolution reads the scoped principal),
/// so — unlike <see cref="HostedFormsApiEndpoint"/> which closes over singleton deps — this endpoint
/// hands <see cref="FormDraftRoutes.Map"/> the OUTER root <see cref="IServiceProvider"/> and the
/// route handlers create a per-request scope (the bug-2849-safe pattern for a scoped dependency).
/// Registered before <c>SharedHostedWebApp</c> so its paths are mapped before Kestrel starts.
/// </remarks>
public sealed class HostedFormDraftsApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IServiceProvider _services;
    private readonly ILogger<HostedFormDraftsApiEndpoint> _logger;

    /// <summary>Constructs the hosted draft-API endpoint.</summary>
    public HostedFormDraftsApiEndpoint(
        SharedHostedWebApp sharedApp,
        IServiceProvider services,
        ILogger<HostedFormDraftsApiEndpoint> logger)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
        {
            // MTW-2 decision (ADR 0160 R3-I items 4 and 6): the web plane does not get drafts.
            // Resolving the acting member per request is the other answer, but that is MTW-3 work and
            // needs a permission model that does not exist. This refusal is a RESTRICTION, not a repair:
            // drafts remain desktop-only until that lands.
            //
            // Drafts are consequential even though they do not sign roster material: the handlers resolve
            // party state from the OUTER container, so an unattributed request would read and write the
            // operator's drafts. Require the listener's positive desktop assertion. Accept-3 still works
            // because its single configured founder credential publishes that assertion after authentication;
            // selected-session, device, un-enforced and attribution-omission paths do not.
            var desktopPlaneOnly = app.MapDesktopPlaneOnlyGroup();
            FormDraftRoutes.Map(desktopPlaneOnly, _services);
        });

        _logger.LogInformation(
            "D2 desktop-plane-only node-local submission-draft API registered " +
            "(PUT/GET/DELETE {RouteBase}/{{formId}}/drafts/{{caseId}}; " +
            "GET {RouteBase}/drafts). Drafts key by (tenant, case, party) via the fail-closed IPartyContext seam.",
            FormDraftRoutes.RouteBase, FormDraftRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
