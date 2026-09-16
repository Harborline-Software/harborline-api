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
    private static readonly HashSet<string> InternationalizedTextProperties = new(StringComparer.Ordinal)
    {
        "title", "description", "label", "helpText", "confirmationMessage", "presentationBadge", "text",
    };
    public const string PackKey = PackSealedSystemTypeAdmission.PlatformPackKey;
    public const string PackVersion = "1.4.0";
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
        var activated = await installer.ActivateAsync(context, PackKey, PackVersion, cancellationToken).ConfigureAwait(false);
        if (!activated.Activated)
            logger.LogError("Platform pack activation was refused: {Reason}.", activated.Error);
        else if (activated.Detail is not null)
            logger.LogWarning("Platform pack activation committed with post-commit diagnostics: {Detail}", activated.Detail);
    }

    // CA1869: one cached instance, as CompromisedDeviceResponseService and the audit reader already do.
    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web);

    internal static PackExportRequest ReadExportRequest(string authoringPrincipal)
    {
        using var stream = typeof(PlatformPackPreloadHostedService).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing platform export document '{ResourceName}'.");
        var document = JsonSerializer.Deserialize<ExportPackRequestDto>(stream, ExportJsonOptions)
            ?? throw new InvalidOperationException("The platform export document is not a JSON object.");
        return new PackExportRequest(Key: document.Key, Version: document.Version, Name: document.Name ?? document.Key,
            Description: document.Description ?? string.Empty, ScopeTier: Enum.Parse<PackScopeTier>(document.ScopeTier, true),
            Contents: (document.Contents ?? []).Select(ToContentSource).ToArray(),
            Dependencies: (document.Dependencies ?? []).Select(dependency => new PackDependencyRef(
                dependency.Key, dependency.Version, dependency.DeclaredDependencyKeys ?? [])).ToArray(),
            CapabilityRequirements: document.CapabilityRequirements ?? [], Epoch: PackComposerRoutes.OwnRosterEpoch,
            Dcp: DomainComplianceProfile.General(authoringPrincipal));
    }

    private static PackContentSource ToContentSource(ExportContentDto item)
    {
        var kind = Enum.Parse<PackContentKind>(item.Kind, true);
        var content = JsonNode.Parse(item.Content!.Value.GetRawText())!;
        if (kind == PackContentKind.FormDefinition) ValidateReleasedFormText(content);
        return new PackContentSource(item.Key, kind, item.Version, content);
    }

    /// <summary>
    /// The sealed platform seed is authored against the public form wire contract. Refuse legacy or
    /// incomplete localized text at resource load rather than signing a pack that admission would
    /// silently lower to empty copy. This check is intentionally bounded to the released platform
    /// resource; it does not reinterpret already-admitted immutable pack versions.
    /// </summary>
    internal static void ValidateReleasedFormText(JsonNode content)
    {
        if (!TryValidateInternationalizedText(content, string.Empty, out var error))
            throw new InvalidDataException(error);
    }

    private static bool TryValidateInternationalizedText(JsonNode node, string path, out string error)
    {
        error = string.Empty;
        if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
                if (array[index] is { } child
                    && !TryValidateInternationalizedText(child, $"{path}[{index}]", out error)) return false;
            return true;
        }
        if (node is not JsonObject obj) return true;

        foreach (var property in obj)
        {
            if (property.Value is null) continue;
            var childPath = string.IsNullOrEmpty(path) ? property.Key : $"{path}.{property.Key}";
            var textCandidate = property.Value is not JsonObject candidate
                || candidate.ContainsKey("defaultLocale") || candidate.ContainsKey("values")
                || candidate.ContainsKey("kind") || candidate.ContainsKey("value");
            if (InternationalizedTextProperties.Contains(property.Key) && textCandidate)
            {
                if (property.Value is not JsonObject text
                    || text.Count != 2
                    || !text.TryGetPropertyValue("defaultLocale", out var localeNode)
                    || localeNode is not JsonValue localeValue
                    || !localeValue.TryGetValue<string>(out var locale)
                    || string.IsNullOrWhiteSpace(locale)
                    || !text.TryGetPropertyValue("values", out var valuesNode)
                    || valuesNode is not JsonObject values
                    || values.Count == 0
                    || !values.TryGetPropertyValue(locale, out var defaultValueNode)
                    || defaultValueNode is not JsonValue defaultValue
                    || !defaultValue.TryGetValue<string>(out var localized)
                    || string.IsNullOrWhiteSpace(localized)
                    || values.Any(entry => string.IsNullOrWhiteSpace(entry.Key)
                        || entry.Value is not JsonValue value
                        || !value.TryGetValue<string>(out var translated)
                        || string.IsNullOrWhiteSpace(translated)))
                {
                    error = $"{childPath} must be an InternationalizedTextDto with a populated default locale";
                    return false;
                }
                continue;
            }
            if (!TryValidateInternationalizedText(property.Value, childPath, out error)) return false;
        }
        return true;
    }
}
