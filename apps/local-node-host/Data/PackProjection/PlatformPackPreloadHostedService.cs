using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.AccessGrant;
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

/// <summary>Installs the released platform catalogue seed through the ordinary pack path before Access.</summary>
internal sealed class PlatformPackPreloadHostedService : IHostedService
{
    public const string PackKey = PackSealedSystemTypeAdmission.PlatformPackKey;
    public const string PackVersion = "1.0.0";
    private const string ResourceName = "Harborline.Api.LocalNodeHost.Packs.platform-pack.export.json";

    private readonly IPackExporter exporter;
    private readonly NodePrincipalSigner signer;
    private readonly IPackInstaller installer;
    private readonly IPackInstallStore store;
    private readonly IPackTrustStore trustStore;
    private readonly IPackRevocationList revocation;
    private readonly IActiveTeamAccessor activeTeam;
    private readonly TimeProvider time;
    private readonly ILogger<PlatformPackPreloadHostedService> logger;

    public PlatformPackPreloadHostedService(IPackExporter exporter, NodePrincipalSigner signer,
        IPackInstaller installer, IPackInstallStore store, IPackTrustStore trustStore,
        IPackRevocationList revocation, IActiveTeamAccessor activeTeam, TimeProvider timeProvider,
        ILogger<PlatformPackPreloadHostedService> logger)
    {
        this.exporter = exporter;
        this.signer = signer;
        this.installer = installer;
        this.store = store;
        this.trustStore = trustStore;
        this.revocation = revocation;
        this.activeTeam = activeTeam;
        time = timeProvider;
        this.logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await PreloadAsync(NodeTenant.Resolve(activeTeam), cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "PlatformPackPreloadHostedService failed to preload {PackKey}.", PackKey);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task PreloadAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        var active = store.GetActive(tenant, PackKey);
        if (active is not null && active.Version == PackVersion) return;

        var context = new PackInstallContext(tenant, trustStore, revocation, time.GetUtcNow(),
            PackInstallRoutes.RevocationMaxAge, Principal: AccessGrantAuthorizationSeed.NodeOperatorPrincipal);
        if (store.GetVersion(tenant, PackKey, PackVersion) is null)
        {
            var exported = await exporter.ExportAsync(ReadExportRequest(signer.Signer.IssuerId.ToBase64Url()),
                signer.Signer, cancellationToken).ConfigureAwait(false);
            if (!exported.Succeeded || exported.FileBytes is null)
            {
                logger.LogError("Platform pack export was refused: {Codes}.", string.Join(",", exported.Validation.Errors.Select(error => error.Code)));
                return;
            }
            var installed = installer.Install(exported.FileBytes, context);
            if (!installed.Installed)
            {
                logger.LogError("Platform pack install was refused: {Codes}.", string.Join(",", installed.RefusalCodes));
                return;
            }
        }
        var activated = installer.Activate(context, PackKey, PackVersion);
        if (!activated.Activated)
            logger.LogError("Platform pack activation was refused: {Reason}.", activated.Error);
    }

    private static PackExportRequest ReadExportRequest(string authoringPrincipal)
    {
        using var stream = typeof(PlatformPackPreloadHostedService).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing platform export document '{ResourceName}'.");
        var document = JsonSerializer.Deserialize<ExportPackRequestDto>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The platform export document is not a JSON object.");
        return new PackExportRequest(Key: document.Key, Version: document.Version, Name: document.Name ?? document.Key,
            Description: document.Description ?? string.Empty, ScopeTier: Enum.Parse<PackScopeTier>(document.ScopeTier, true),
            Contents: (document.Contents ?? []).Select(item => new PackContentSource(item.Key,
                Enum.Parse<PackContentKind>(item.Kind, true), item.Version,
                JsonNode.Parse(item.Content!.Value.GetRawText())!)).ToArray(), Dependencies: [],
            CapabilityRequirements: document.CapabilityRequirements ?? [], Epoch: PackComposerRoutes.OwnRosterEpoch,
            Dcp: DomainComplianceProfile.General(authoringPrincipal));
    }
}
