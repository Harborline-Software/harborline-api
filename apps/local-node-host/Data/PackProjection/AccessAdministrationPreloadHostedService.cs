using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>
/// Preloads the Access administration package (ticket 208, L673/L685): on first run it exports, installs
/// and activates <c>harborline.access-administration</c> through the ORDINARY
/// <see cref="IPackExporter"/> → <see cref="IPackInstaller"/> path, so the Access surfaces are
/// catalogue definitions with pack provenance rather than compiled screens, and a later package may
/// replace them whole.
/// </summary>
/// <remarks>
/// <para>
/// <b>Definitions only, and only what this host can admit.</b> The package carries the grant form and
/// the privileged-review workflow, and the holders navigation declaration. This service writes no
/// <c>AccessGrant</c>. The holdings view definition and access-by-person report remain deferred
/// alongside their entity type and report cartridge registrations.
/// </para>
/// <para>
/// <b>No bypass.</b> Install and activation run the same verify → admission → atomic commit → project
/// sequence any third-party package runs, under the node operator's ordinary <c>packages:operate</c>
/// holding, against the host's COMPOSED trust store and revocation list. There is no "our own package"
/// shortcut and no self-issued trust root: a revoked or untrusted signature is refused here too.
/// </para>
/// <para>
/// <b>All or nothing.</b> If projection refuses any shipped definition, the package is NOT left active
/// with a partial projection: activation is reversed (which retracts whatever did project), the refusal
/// is logged, and the version stays installed-but-not-active — a durable pending state visible in the
/// ordinary installed-pack listing, which the NEXT boot retries.
/// </para>
/// <para>
/// <b>Idempotent and defensive.</b> A second boot sees the installed version and returns without
/// re-exporting; every failure is logged loudly and never faults node startup (same posture as
/// <see cref="PackSeedProjectionHostedService"/>).
/// </para>
/// </remarks>
internal sealed class AccessAdministrationPreloadHostedService : IHostedService
{
    /// <summary>The preloaded package's key.</summary>
    public const string PackKey = "harborline.access-administration";

    /// <summary>The preloaded package's pinned version.</summary>
    public const string PackVersion = "1.1.1";

    /// <summary>
    /// The install provenance this preload records: shipped with every installation, and replaceable by
    /// an ordinary package upgrade rather than pinned by compiled code.
    /// </summary>
    public const string Provenance = "preloaded-replaceable";

    private const string ResourceName =
        "Harborline.Api.LocalNodeHost.Packs.access-administration-pack.export.json";

    private readonly IPackExporter _exporter;
    private readonly NodePrincipalSigner _signer;
    private readonly IPackInstaller _installer;
    private readonly IPackInstallStore _store;
    private readonly IPackTrustStore _trustStore;
    private readonly IPackRevocationList _revocation;
    private readonly IActiveTeamAccessor _activeTeam;
    private readonly TimeProvider _time;
    private readonly ILogger<AccessAdministrationPreloadHostedService> _logger;

    /// <summary>Constructs the preload over the ordinary export + install seams.</summary>
    public AccessAdministrationPreloadHostedService(
        IPackExporter exporter,
        NodePrincipalSigner signer,
        IPackInstaller installer,
        IPackInstallStore store,
        IPackTrustStore trustStore,
        IPackRevocationList revocation,
        IActiveTeamAccessor activeTeam,
        TimeProvider timeProvider,
        ILogger<AccessAdministrationPreloadHostedService> logger)
    {
        _exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
        _revocation = revocation ?? throw new ArgumentNullException(nameof(revocation));
        _activeTeam = activeTeam ?? throw new ArgumentNullException(nameof(activeTeam));
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await PreloadAsync(NodeTenant.Resolve(_activeTeam), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A preload may never brick node boot, but a swallowed failure must be LOUD (ticket 160).
            _logger.LogError(
                ex, "AccessAdministrationPreloadHostedService: preloading {PackKey} v{Version} FAILED — "
                + "continuing node boot; the Access surfaces are absent until the next attempt succeeds.",
                PackKey, PackVersion);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Exports, installs and activates the package for <paramref name="tenant"/> unless this version is
    /// already ACTIVE (a version installed but inactive is retried from activation). Internal so the acceptance tests drive the SAME path node boot drives.
    /// </summary>
    internal async Task PreloadAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        // ACTIVE at this version is the only done state. A version that is installed but NOT active is a
        // prior boot's refused projection (below): the retry re-activates it rather than re-installing it.
        var active = _store.GetActive(tenant, PackKey);
        if (active is not null && string.Equals(active.Version, PackVersion, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "AccessAdministrationPreloadHostedService: {PackKey} v{Version} is already active for "
                + "tenant {Tenant} — nothing to preload.", PackKey, PackVersion, tenant);
            return;
        }

        // Ticket 176: this is the author-controlled bootstrap path. Do not even export or install
        // Access until the platform catalogue it declares as a dependency is active. The installer
        // repeats the same named refusal for all other callers of an artifact that declares it.
        if (_store.GetActive(tenant, PlatformPackPreloadHostedService.PackKey) is null)
        {
            _logger.LogError(
                "AccessAdministrationPreloadHostedService: preloading {PackKey} was REFUSED [{Code}] "
                + "because {PlatformPackKey} is not active.",
                PackKey,
                PackInstallCodes.ActivatePlatformPackRequired,
                PlatformPackPreloadHostedService.PackKey);
            return;
        }

        var pending = _store.GetVersion(tenant, PackKey, PackVersion) is not null;

        var request = ReadExportRequest(_signer.Signer.IssuerId.ToBase64Url());

        var context = new PackInstallContext(
            tenant,
            // The host's COMPOSED trust surface, not a root of this service's own making: an untrusted or
            // revoked signature is refused on this path exactly as it is on POST /packs/install.
            _trustStore,
            _revocation,
            _time.GetUtcNow(),
            PackInstallRoutes.RevocationMaxAge,
            // The preload acts as the node operator, whose ordinary `packages:operate` holding the
            // authorization seed already grants — not as a privileged installer of "our own" package.
            Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);

        if (!pending)
        {
            var exported = await _exporter.ExportAsync(request, _signer.Signer, cancellationToken)
                .ConfigureAwait(false);
            if (!exported.Succeeded || exported.FileBytes is null)
            {
                _logger.LogError(
                    "AccessAdministrationPreloadHostedService: the {PackKey} export document failed pack "
                    + "validation [{Codes}] — the package is NOT preloaded.",
                    PackKey, string.Join(",", exported.Validation.Errors.Select(e => e.Code)));
                return;
            }

            var installed = _installer.Install(exported.FileBytes, context);
            if (!installed.Installed)
            {
                _logger.LogError(
                    "AccessAdministrationPreloadHostedService: installing {PackKey} v{Version} was REFUSED "
                    + "[{Codes}] — the package is NOT preloaded.",
                    PackKey, PackVersion, string.Join(",", installed.RefusalCodes));
                return;
            }
        }

        var activated = _installer.Activate(context, PackKey, PackVersion);
        if (!activated.Activated)
        {
            _logger.LogError(
                "AccessAdministrationPreloadHostedService: activating {PackKey} v{Version} was REFUSED "
                + "[{Error}] — the package is installed but its definitions are not live.",
                PackKey, PackVersion, activated.Error);
            return;
        }

        var refusals = activated.ProjectionResult is PackSeedProjectionSummary projection
            ? projection.Refusals
            : Array.Empty<PackSeedProjectionRefusal>();
        if (refusals.Count > 0)
        {
            // All or nothing: a package shipping a definition this host will not admit must not be left
            // ACTIVE over a partial projection. Reversing activation retracts whatever did project and
            // leaves the version installed-and-INACTIVE — the pending state the installed-pack listing
            // shows an operator, and the state the next boot retries from.
            var reason = string.Join(",", refusals.Select(r => $"{r.ContentKey}={r.Code}@{r.Pointer}"));
            var reversed = _installer.Deactivate(context, PackKey, PackVersion);
            _logger.LogError(
                "AccessAdministrationPreloadHostedService: {PackKey} v{Version} was NOT preloaded — {Count} "
                + "definition(s) were refused by projection [{Refusals}]; activation reversed (deactivated "
                + "{Reversed}). The version stays installed and INACTIVE; the next boot retries it.",
                PackKey, PackVersion, refusals.Count, reason, reversed.Deactivated);
            return;
        }

        _logger.LogInformation(
            "AccessAdministrationPreloadHostedService: preloaded {PackKey} v{Version} for tenant {Tenant} "
            + "with {Provenance} provenance — {Count} definition(s) projected, no grant row written.",
            PackKey, PackVersion, tenant, Provenance, request.Contents.Count);
    }

    /// <summary>
    /// Reads the committed export document (<c>_shared/packs/access-administration/</c>, embedded at build)
    /// and maps it onto the same <see cref="PackExportRequest"/> the export route builds.
    /// </summary>
    internal static PackExportRequest ReadExportRequest(string authoringPrincipal)
    {
        using var stream = typeof(AccessAdministrationPreloadHostedService).Assembly
                               .GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException(
                               $"The embedded Access administration export document '{ResourceName}' is missing.");
        var document = JsonSerializer.Deserialize<ExportPackRequestDto>(
                           stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                       ?? throw new InvalidOperationException(
                           "The Access administration export document is not a JSON object.");

        var contents = (document.Contents ?? Array.Empty<ExportContentDto>())
            .Select(item => new PackContentSource(
                item.Key,
                Enum.Parse<PackContentKind>(item.Kind, ignoreCase: true),
                item.Version,
                JsonNode.Parse(item.Content!.Value.GetRawText())!))
            .ToList();

        return new PackExportRequest(
            Key: document.Key,
            Version: document.Version,
            Name: document.Name ?? document.Key,
            Description: document.Description ?? string.Empty,
            ScopeTier: Enum.Parse<PackScopeTier>(document.ScopeTier, ignoreCase: true),
            Contents: contents,
            Dependencies: (document.Dependencies ?? Array.Empty<ExportDependencyDto>())
                .Select(dependency => new PackDependencyRef(
                    dependency.Key,
                    dependency.Version,
                    dependency.DeclaredDependencyKeys ?? Array.Empty<string>()))
                .ToArray(),
            CapabilityRequirements: document.CapabilityRequirements ?? Array.Empty<string>(),
            Epoch: PackComposerRoutes.OwnRosterEpoch,
            Dcp: DomainComplianceProfile.General(authoringPrincipal));
    }
}
