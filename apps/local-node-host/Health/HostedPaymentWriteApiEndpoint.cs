using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Financial;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local payment-WRITE routes (invoice / bill <c>/payments</c>
/// sub-resources) on the shared Kestrel listener (ADR 0122 §D4 T2 — node payment-WRITE residency).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="PaymentWriteRoutes.Map"/> (the single source of truth shared with the route tests so the
/// wire contract has no test/prod drift). The routes record + apply payments to the recoverable
/// <c>local-node.db</c> and read the node AR/AP repositories to resolve the target's chart + party.
/// </para>
/// <para>
/// The write services are resolved from the composition root and passed to
/// <see cref="PaymentWriteRoutes.Map"/> as a service tuple.
/// </para>
/// <para>
/// Mapping order is explicit in <see cref="LocalNodeEndpointMapping"/>.
/// </para>
/// </remarks>
public sealed class HostedPaymentWriteApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IServiceProvider _services;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly NodeEfInvoiceRepository _invoices;
    private readonly NodeEfBillRepository _bills;
    private readonly ILogger<HostedPaymentWriteApiEndpoint> _logger;

    /// <summary>Constructs the hosted payment-write API endpoint.</summary>
    public HostedPaymentWriteApiEndpoint(
        SharedHostedWebApp sharedApp,
        IServiceProvider services,
        NodeEfInvoiceRepository invoices,
        NodeEfBillRepository bills,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedPaymentWriteApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(invoices);
        ArgumentNullException.ThrowIfNull(bills);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _services = services;
        _invoices = invoices;
        _bills = bills;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var payments = (
            _services.GetRequiredService<NodeEfPaymentRepository>(),
            _services.GetRequiredService<NodeEfPaymentApplicationRepository>(),
            _services.GetRequiredService<Harborline.Api.Blocks.FinancialPayments.Services.IPaymentApplicationService>());
        _sharedApp.MapApiRoutes(app => PaymentWriteRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            payments,
            _invoices,
            _bills,
            _activeTeam,
            _services.GetRequiredService<TimeProvider>()));

        _logger.LogInformation(
            "Node-local payment-WRITE API registered (POST/GET {InvoiceBase}/{{id}}/payments + " +
            "{BillBase}/{{id}}/payments).",
            PaymentWriteRoutes.InvoiceRouteBase,
            PaymentWriteRoutes.BillRouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
