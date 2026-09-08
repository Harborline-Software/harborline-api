using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the node-local asset-registry routes onto the shared Kestrel listener
/// (ADR 0101 Rev 3.1 Wave 2b). Thin <see cref="IHostedService"/> wrapper around
/// <see cref="AssetRegistryRoutes.Map"/> (the single source of truth); the registry stores + active-team
/// accessor + clock are injected from the OUTER host container and closed over — never resolved via
/// <c>[FromServices]</c> inside the handlers (bug-2849).
/// </summary>
public sealed class HostedAssetRegistryApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IEntityTypeRegistry _types;
    private readonly IRegistryEntityRepository _entities;
    private readonly ITypedRelationshipStore _edges;
    private readonly IConditionAssessmentStore _conditions;
    private readonly IFormSubmissionRecordStore _submissions;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly TimeProvider _clock;
    private readonly ILogger<HostedAssetRegistryApiEndpoint> _logger;

    /// <summary>Constructs the hosted asset-registry API endpoint.</summary>
    public HostedAssetRegistryApiEndpoint(
        SharedHostedWebApp sharedApp,
        IEntityTypeRegistry types,
        IRegistryEntityRepository entities,
        ITypedRelationshipStore edges,
        IConditionAssessmentStore conditions,
        IFormSubmissionRecordStore submissions,
        IActiveTeamAccessor activeTeam,
        TimeProvider clock,
        ILogger<HostedAssetRegistryApiEndpoint> logger)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _entities = entities ?? throw new ArgumentNullException(nameof(entities));
        _edges = edges ?? throw new ArgumentNullException(nameof(edges));
        _conditions = conditions ?? throw new ArgumentNullException(nameof(conditions));
        _submissions = submissions ?? throw new ArgumentNullException(nameof(submissions));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sharedApp.MapApiRoutes(app =>
            AssetRegistryRoutes.Map(
                app.MapDeviceReachableProductDataGroup(),
                _types,
                _entities,
                _edges,
                _conditions,
                _submissions,
                _activeTeam,
                _clock));

        _logger.LogInformation(
            "Node-local asset-registry API registered (GET {Base}/types, GET/POST {Base}/entities, " +
            "GET {Base}/entities/{{id}}/tree, GET {Base}/entities/{{id}}/condition, " +
            "GET {Base}/entities/{{id}}/submissions, POST {Base}/edges).",
            AssetRegistryRoutes.RouteBase, AssetRegistryRoutes.RouteBase, AssetRegistryRoutes.RouteBase,
            AssetRegistryRoutes.RouteBase, AssetRegistryRoutes.RouteBase, AssetRegistryRoutes.RouteBase);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
