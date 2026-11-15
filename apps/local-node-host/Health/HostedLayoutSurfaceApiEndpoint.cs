using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Layout;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Maps the Layout surface route (T-735) before the shared listener starts, over the registered
/// <see cref="LayoutSurfaceHost"/>. Root records are the registry's; related targets come from
/// <see cref="AbsentLayoutRelatedRecordSource"/> until the host serves a Records instance store (T-619), so every
/// related block resolves empty, identically for every caller.
/// </summary>
public sealed class HostedLayoutSurfaceApiEndpoint(
    SharedHostedWebApp sharedApp,
    LayoutSurfaceHost host,
    IPackInstallStore packs,
    IRegistryEntityRepository entities,
    PackBoundRegistryRecordWriter records,
    TimeProvider time,
    AuthorizedActAudit audit,
    ILogger<HostedLayoutSurfaceApiEndpoint> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        sharedApp.MapApiRoutes(app => LayoutSurfaceRoutes.Map(
            app.MapSelectedSessionProductGroup(), host, packs,
            new RegistryLayoutRootRecordSource(entities, records), AbsentLayoutRelatedRecordSource.Instance, time, audit));
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Layout surface API registered at GET {Route}.", LayoutSurfaceRoutes.RouteTemplate);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
