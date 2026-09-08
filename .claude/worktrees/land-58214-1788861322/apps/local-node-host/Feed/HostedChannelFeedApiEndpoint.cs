using System.Collections.Generic;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Feed;

/// <summary>
/// Hosted service that maps the update-feed U2 CHANNEL routes (<see cref="ChannelFeedRoutes.Map"/>) onto the
/// shared Kestrel listener and builds the PREVIEW trust surface: every channel's binary-pinned root is added
/// as a <see cref="TrustScope.HarborlineChannel"/> CURRENT root so a staged, feed-verified artifact previews
/// clean through the EXISTING install engine (in the dogfood first-party model the publisher IS the channel
/// root — the same key U1 signed the pack + feed with; §7.4). The feed already verified the artifact against
/// the pinned root during the check; handing the SAME bytes to <c>installer.Preview</c> re-runs the identical
/// verify-before-effect gate (S-7) — no new install path.
/// </summary>
public sealed class HostedChannelFeedApiEndpoint : IHostedService
{
    private readonly SharedHostedWebApp _sharedApp;
    private readonly IChannelFeedClient _client;
    private readonly IChannelRegistry _registry;
    private readonly IPackInstaller _installer;
    private readonly IPackInstallStore _store;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly AuthorizationGate _gate;
    private readonly TimeProvider _time;
    private readonly ILogger<HostedChannelFeedApiEndpoint> _logger;

    /// <summary>Constructs the hosted channel-feed endpoint.</summary>
    public HostedChannelFeedApiEndpoint(
        SharedHostedWebApp sharedApp,
        IChannelFeedClient client,
        IChannelRegistry registry,
        IPackInstaller installer,
        IPackInstallStore store,
        IActiveTeamAccessor activeTeam,
        AuthorizationGate gate,
        TimeProvider timeProvider,
        ILogger<HostedChannelFeedApiEndpoint> logger)
    {
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The preview trust store: every LOCALLY-PINNED channel root becomes a Harborline-channel scoped root
        // so a feed-verified artifact for that channel previews clean. A channel with no pin (F6 proposed
        // channel) contributes no root — its content would refuse at preview, exactly as it refuses at check.
        var roots = new List<PackTrustRoot>();
        foreach (var channel in _registry.ListChannels())
        {
            if (_registry.GetPin(channel.ChannelId) is { } pin)
            {
                roots.Add(new PackTrustRoot(
                    TrustScope.HarborlineChannel, pin.Root.KeyId, pin.Root.PublisherEpoch, TrustRootStatus.Current));
            }
        }
        var previewTrustStore = new InMemoryPackTrustStore(roots);

        // The install-time revocation list is the node's own (offline-tolerant Empty until provisioned); the
        // feed's revocations.json was already checked by the feed verifier during the check.
        IPackRevocationList previewRevocation = PackRevocationList.Empty;

        _sharedApp.MapApiRoutes(app => ChannelFeedRoutes.Map(
            app.MapSelectedSessionProductGroup(),
            _client, _registry, _installer, _store, previewTrustStore, previewRevocation,
            _activeTeam, _gate, _time, _logger));

        _logger.LogInformation(
            "Update-feed U2 channel API registered (POST {Check}, GET {List}); {Roots} pinned channel root(s).",
            ChannelFeedRoutes.CheckRoute, ChannelFeedRoutes.ListChannelsRoute, roots.Count);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
