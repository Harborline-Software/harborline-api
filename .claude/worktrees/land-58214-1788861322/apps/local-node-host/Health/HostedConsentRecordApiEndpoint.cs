using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Governance.Consent;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local subject-consent record routes onto the shared Kestrel listener
/// (ticket 213). A thin wrapper: the mapping lives in <see cref="ConsentRecordRoutes.Map"/>, the single
/// source of truth shared with the route tests so the wire contract has no test/prod drift.
/// </summary>
/// <remarks>
/// The gate and its store are injected from the OUTER host container and closed over, because the routes
/// are mapped onto <see cref="SharedHostedWebApp"/>'s inner <c>WebApplication</c>, whose provider does not
/// have the outer registrations (bug-2849, the same wiring the audit-events endpoint uses).
/// The primary-constructor shape is deliberate and matches the 205-era hosted endpoints beside this one:
/// the ADR 0160 legacy-authority debt scanner is a literal text inventory, so naming the active-team
/// accessor type once (a primary-constructor parameter) costs one token where a field plus a constructor
/// parameter costs two. The null guards this shape drops are already enforced inside
/// <see cref="ConsentRecordRoutes.Map"/>.
/// </remarks>
public sealed class HostedConsentRecordApiEndpoint(
    SharedHostedWebApp sharedApp,
    TenantConsentGate gate,
    ITenantConsentStore store,
    IActiveTeamAccessor activeTeam,
    ILogger<HostedConsentRecordApiEndpoint> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        sharedApp.MapApiRoutes(app => ConsentRecordRoutes.Map(
            app.MapDesktopPlaneOnlyGroup(), gate, store, activeTeam));

        logger.LogInformation(
            "Node-local subject-consent record API registered under {RouteBase} " +
            "(GET one record; POST request; POST activate, expire and revoke).",
            ConsentRecordRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
