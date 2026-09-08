using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.FinancialAr.Services;

using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Workflow;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local AR invoice routes onto the shared Kestrel listener
/// (Cohort D Step 2b — the AR node-flip; ADR 0113 ABSOLUTE local-first).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="InvoiceRoutes.Map"/> (the single source of truth shared with the route tests so the
/// wire contract has no test/prod drift). Reads + Draft create come from the
/// <see cref="NodeEfInvoiceRepository"/> (with the canonical number minted at create time via
/// the <see cref="IInvoiceNumberingService"/>); the issue / void / write-off writes go
/// through the <see cref="IInvoicePostingService"/> — whose <c>IssueAsync</c> posts the
/// balanced JE via the Step-2a node posting service over the recoverable <see cref="NodeEfJournalStore"/>.
/// </para>
/// <para>
/// The accessors are injected from the OUTER host container and passed to
/// <see cref="InvoiceRoutes.Map"/> as closed-over dependencies. Resolving via <c>[FromServices]</c>
/// inside the route handlers would fail because the routes are mapped onto
/// <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose service provider does NOT
/// have the outer-container registrations (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE <see cref="SharedHostedWebApp"/> so its
/// <c>StartAsync</c> maps paths while the shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedInvoiceApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly NodeEfInvoiceRepository _invoices;
    private readonly IInvoiceNumberingService _numbering;
    private readonly IInvoicePostingService _posting;
    private readonly NodeInvoiceApprovalCutover _approvalCutover;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HostedInvoiceApiEndpoint> _logger;

    /// <summary>Constructs the hosted invoice API endpoint.</summary>
    /// <remarks>
    /// The <paramref name="approvalCutover"/> wires the ADR 0135 cutover: an over-threshold issue routes
    /// through the approval engine (park, no inline post) instead of posting the JE directly. It is a
    /// required dependency in the production host (the engine is always composed here); the
    /// <see cref="InvoiceRoutes.Map"/> overload still accepts a null cutover for the legacy route tests that
    /// don't compose the engine.
    /// </remarks>
    public HostedInvoiceApiEndpoint(
        SharedHostedWebApp sharedApp,
        NodeEfInvoiceRepository invoices,
        IInvoiceNumberingService numbering,
        IInvoicePostingService posting,
        NodeInvoiceApprovalCutover approvalCutover,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        ILogger<HostedInvoiceApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(numbering);
        ArgumentNullException.ThrowIfNull(posting);
        ArgumentNullException.ThrowIfNull(approvalCutover);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _invoices = invoices;
        _numbering = numbering;
        _posting = posting;
        _approvalCutover = approvalCutover;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => InvoiceRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _invoices,
            _numbering,
            _posting,
            _activeTeam,
            _approvalCutover,
            _timeProvider));

        _logger.LogInformation(
            "Node-local AR invoices API registered over the recoverable local-node store " +
            "(GET/POST {RouteBase}, GET/DELETE {RouteBase}/{{id}}, POST {RouteBase}/{{id}}/[issue|void|write-off]).",
            InvoiceRoutes.RouteBase,
            InvoiceRoutes.RouteBase,
            InvoiceRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
