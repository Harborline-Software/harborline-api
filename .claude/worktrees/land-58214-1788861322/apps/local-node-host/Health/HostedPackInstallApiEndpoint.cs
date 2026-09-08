using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime.Teams;

namespace Harborline.Api.LocalNodeHost.Health;

/// <summary>
/// Hosted service that maps the Pack Composer B-1b INSTALL routes (<see cref="PackInstallRoutes.Map"/>)
/// onto the shared Kestrel listener and BUILDS the v1 trust surface: the node's own-roster CURRENT root
/// PLUS the Harborline-channel root SLOT (S-11/S-13), and the channel-distributed revocation list. Trust
/// stays own-roster + channel only — there is no third-party root and no "install anyway" path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Channel root + revocation (S-11).</b> The Harborline-channel signing key is provisioned out of band;
/// when its public key is configured (<c>HARBORLINE_PACK_CHANNEL_KEY</c>, base64url), it is added as a
/// <see cref="TrustScope.HarborlineChannel"/> CURRENT root so channel-shipped packs verify. The signed,
/// channel-distributed revocation list attaches at the same seam (<see cref="PackRevocationListVerifier"/>);
/// until one is provisioned the list is the offline-tolerant <see cref="PackRevocationList.Empty"/> — which
/// revokes nothing but reports itself STALE, so the install-preview surfaces the offline posture honestly
/// rather than silently claiming "nothing revoked".
/// </para>
/// <para>
/// The durable audit sink (<see cref="KernelAuditPackInstallAudit"/>) + the real ADR 0143 admission adapter
/// (<see cref="PackWorkflowAdmissionAdapter"/>) are bound in Program.cs BEFORE <c>AddPackComposerInstall</c>,
/// so the install engine resolves the durable + real adapters, not the foundation fail-closed defaults.
/// </para>
/// </remarks>
internal sealed class HostedPackInstallApiEndpoint : IHostedService
{
    /// <summary>The channel epoch for v1 (ADR 0126 D4) — a fixed baseline mirroring the own-roster epoch.</summary>
    public const long ChannelEpoch = 1;

    /// <summary>Environment variable carrying the Harborline-channel signing PUBLIC key (base64url).</summary>
    public const string ChannelKeyEnvVar = "HARBORLINE_PACK_CHANNEL_KEY";

    /// <summary>The pre-rename spelling of <see cref="ChannelKeyEnvVar"/>. Still honoured (new name wins) so an
    /// operator that provisioned the key under the old name keeps working for one release; delete after that.
    /// </summary>
    public const string LegacyChannelKeyEnvVar = "SHIPYARD_PACK_CHANNEL_KEY";

    private readonly SharedHostedWebApp _sharedApp;
    private readonly IPackInstaller _installer;
    private readonly IPackInstallStore _store;
    private readonly NodePrincipalSigner _signer;
    private readonly IPackTrustStore _trustStore;
    private readonly IPackRevocationList _revocation;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly AuthorizationGate _gate;
    private readonly Harborline.Api.LocalNodeHost.Data.PackProjection.IPackSeedProjector _projector;
    private readonly IPackFeatureGraphReadModel _graphReadModel;
    private readonly TimeProvider _time;
    private readonly ILogger<HostedPackInstallApiEndpoint> _logger;
    private readonly Harborline.Api.Foundation.Packs.Install.Compatibility.IPackPlatformCompatibility? _platform;

    /// <summary>Constructs the hosted install endpoint. <paramref name="platform"/> is the running
    /// build's compatibility facts — optional for back-compat embedders; when present the installed-pack
    /// list marks Active-but-platform-refused packs (ticket 160 visibility).</summary>
    public HostedPackInstallApiEndpoint(
        SharedHostedWebApp sharedApp,
        IPackInstaller installer,
        IPackInstallStore store,
        NodePrincipalSigner signer,
        IPackTrustStore trustStore,
        IPackRevocationList revocation,
        IActiveTeamAccessor activeTeam,
        AuthorizationGate gate,
        Harborline.Api.LocalNodeHost.Data.PackProjection.IPackSeedProjector projector,
        IPackFeatureGraphReadModel graphReadModel,
        TimeProvider timeProvider,
        ILogger<HostedPackInstallApiEndpoint> logger,
        Harborline.Api.Foundation.Packs.Install.Compatibility.IPackPlatformCompatibility? platform = null)
    {
        _platform = platform;
        _sharedApp = sharedApp ?? throw new ArgumentNullException(nameof(sharedApp));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
        _revocation = revocation ?? throw new ArgumentNullException(nameof(revocation));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _projector = projector ?? throw new ArgumentNullException(nameof(projector));
        _graphReadModel = graphReadModel ?? throw new ArgumentNullException(nameof(graphReadModel));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Ticket 208 fix 1: the trust surface is BUILT ONCE at composition (BuildTrustStore below) and
        // resolved from the container here, so every install path — this endpoint's routes and the Access
        // administration preload — verifies against the SAME roots and the same revocation list. A path
        // that builds its own trust root cannot refuse a revoked or untrusted signature.
        // F3: the break-glass authorizing principal is server-owned (the node's own key id), never a
        // client-asserted query value. A future broker-PEP replaces this with a per-user principal.
        var authorizingPrincipal = _signer.Signer.IssuerId.ToBase64Url();

        _sharedApp.MapApiRoutes(app => PackInstallRoutes.Map(
            app, _installer, _store, _trustStore, _revocation, _activeTeam, _gate, _time, _logger,
            authorizingPrincipal, _projector, _platform));

        // App-layer feature graph (G1) — the read-only GET /packs/graph, same tenant-scoped posture as the
        // sibling install routes. A projection over install state (no store, no mutation); the Apps surface
        // (G3) + install-diff preview (G4) render slices of what it returns.
        _sharedApp.MapApiRoutes(app => PackGraphRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _graphReadModel,
            _activeTeam));

        // T3-3 pack-driven Harborline App navigation — a read-only composition over the SAME durable Active seed
        // layers. It is intentionally mapped beside the graph rather than added to PackSeedProjector: nav
        // has no mutable runtime store, and deriving on read prevents a second source of truth.
        _sharedApp.MapApiRoutes(app => PackNavigationRoutes.Map(
            app.MapDeviceReachableProductDataGroup(),
            _store,
            _activeTeam,
            _logger));

        _logger.LogInformation(
            "Pack Composer B-1b install API registered (POST {Install}, POST {Preview}, POST {Activate}, "
            + "POST {Deactivate}, GET {List}, GET {Graph}, GET {Navigation}).",
            PackInstallRoutes.InstallRoute, PackInstallRoutes.PreviewRoute,
            PackInstallRoutes.ActivateRoute, PackInstallRoutes.DeactivateRoute,
            PackInstallRoutes.ListInstalledRoute, PackGraphRoutes.GraphRoute,
            PackNavigationRoutes.NavigationRoute);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the node's v1 pack trust surface: the own-roster CURRENT root, plus the Harborline-channel
    /// root when its public key is provisioned (<see cref="ChannelKeyEnvVar"/>). Called once by the
    /// composition root, which registers the result as the singleton <see cref="IPackTrustStore"/>.
    /// </summary>
    public static IPackTrustStore BuildTrustStore(NodePrincipalSigner signer, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(logger);

        var roots = new List<PackTrustRoot>
        {
            // (a) The node's OWN roster key — self-authored packs moving between the org's instances.
            new(TrustScope.OwnRoster, signer.Signer.IssuerId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        };

        // (b) The Harborline-channel root SLOT — added when the channel public key is provisioned (S-11).
        var channelKeyB64 = Environment.GetEnvironmentVariable(ChannelKeyEnvVar)
            ?? Environment.GetEnvironmentVariable(LegacyChannelKeyEnvVar);
        if (!string.IsNullOrWhiteSpace(channelKeyB64))
        {
            try
            {
                var channelKey = PrincipalId.FromBase64Url(channelKeyB64.Trim());
                roots.Add(new PackTrustRoot(TrustScope.HarborlineChannel, channelKey, ChannelEpoch, TrustRootStatus.Current));
                logger.LogInformation("Pack Composer: Harborline-channel trust root configured.");
            }
            catch (FormatException)
            {
                logger.LogWarning(
                    "Pack Composer: {EnvVar} is set but is not a valid base64url key — channel root NOT added.",
                    ChannelKeyEnvVar);
            }
        }

        return new InMemoryPackTrustStore(roots);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
