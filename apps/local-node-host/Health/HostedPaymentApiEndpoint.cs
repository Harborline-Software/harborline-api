using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data;

using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local payments <b>READ</b> routes on the shared
/// Kestrel listener (PM-doctype offline rebind for payments; Admiral ruling 2026-06-14).
/// </summary>
/// <remarks>
/// <para>
/// A thin <see cref="IHostedService"/> wrapper: the route mapping itself lives in
/// <see cref="PaymentRoutes.Map"/> (the single source of truth shared with the route
/// tests so the wire contract has no test/prod drift). The routes read the
/// kernel-ledger <c>payments</c> table via <see cref="LocalNodeDbContext"/> (the
/// shared-module financial context) on the SQLCipher-encrypted store (SC-1; no
/// plaintext path) — they NEVER write (the posting path owns mutations).
/// </para>
/// <para>
/// The DbContext factory is injected from the OUTER host container and passed to
/// <see cref="PaymentRoutes.Map"/> as a closed-over dependency. Resolving via
/// <c>[FromServices]</c> inside the route handler would fail because the routes are
/// mapped onto <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose
/// service provider does NOT have the outer-container factory registered (bug-2849).
/// </para>
/// <para>
/// Registration order: added to the composition root BEFORE
/// <see cref="SharedHostedWebApp"/> so its <c>StartAsync</c> maps paths while the
/// shared app is still pre-<c>StartAsync</c>.
/// </para>
/// </remarks>
public sealed class HostedPaymentApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly IDbContextFactory<LocalNodeDbContext> _factory;
    private readonly ILogger<HostedPaymentApiEndpoint> _logger;

    /// <summary>Constructs the hosted payments read API endpoint.</summary>
    public HostedPaymentApiEndpoint(
        SharedHostedWebApp sharedApp,
        IDbContextFactory<LocalNodeDbContext> factory,
        IActiveTeamAccessor activeTeam,
        ILogger<HostedPaymentApiEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(sharedApp);
        ArgumentNullException.ThrowIfNull(activeTeam);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);

        _sharedApp = sharedApp;
        _activeTeam = activeTeam;
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app => PaymentRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _factory,
            _activeTeam));

        _logger.LogInformation(
            "Node-local payments READ API registered over the kernel-ledger " +
            "(GET {RouteBase} + GET {RouteBase}/{{id}}).",
            PaymentRoutes.RouteBase,
            PaymentRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
