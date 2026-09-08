namespace Harborline.Api.Foundation.CapabilityAdmission;

internal static class ReleaseCapabilityManifestValidator
{
    public static void Validate(ReleaseCapabilityManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Require(manifest.SchemaVersion, "schema version");
        Require(manifest.Product, "product");
        Require(manifest.Channel, "channel");
        Require(manifest.ReleaseIdentity, "release identity");
        Require(manifest.PlatformProfile, "platform profile");
        Require(manifest.SignerScope, "signer scope");
        if (manifest.ReleaseSequence <= 0)
        {
            Refuse("The release sequence must be positive.");
        }
        ArgumentNullException.ThrowIfNull(manifest.Notes);
        ArgumentNullException.ThrowIfNull(manifest.Artifacts);
        ArgumentNullException.ThrowIfNull(manifest.Capabilities);
        if (!string.Equals(
                manifest.SchemaVersion,
                ReleaseCapabilityManifest.SupportedSchemaVersion,
                StringComparison.Ordinal))
        {
            Refuse($"Unsupported release capability schema '{manifest.SchemaVersion}'.");
        }
        if (manifest.Artifacts.Count == 0)
        {
            Refuse("A release capability manifest must bind at least one executable artifact.");
        }

        var artifactNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in manifest.Artifacts)
        {
            ArgumentNullException.ThrowIfNull(artifact);
            Require(artifact.Name, "artifact name");
            Require(artifact.Platform, "artifact platform");
            Require(artifact.Url, "artifact URL");
            if (!artifactNames.Add(artifact.Name))
            {
                Refuse($"Duplicate release artifact '{artifact.Name}' is not allowed.");
            }
        }

        var capabilityKeys = new HashSet<CapabilityKey>();
        foreach (var entry in manifest.Capabilities)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ValidateKey(entry.Key);
            if (!capabilityKeys.Add(entry.Key))
            {
                Refuse($"Duplicate manifest capability '{entry.Key}' is not allowed.");
            }

            ArgumentNullException.ThrowIfNull(entry.ExecutableInventories);
            var inventoryArtifacts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var artifactInventory in entry.ExecutableInventories)
            {
                ArgumentNullException.ThrowIfNull(artifactInventory);
                Require(artifactInventory.ArtifactName, "inventory artifact name");
                if (!artifactNames.Contains(artifactInventory.ArtifactName))
                {
                    Refuse(
                        $"Capability '{entry.Key}' references unknown artifact " +
                        $"'{artifactInventory.ArtifactName}'.");
                }
                if (!inventoryArtifacts.Add(artifactInventory.ArtifactName))
                {
                    Refuse(
                        $"Capability '{entry.Key}' has duplicate inventory for artifact " +
                        $"'{artifactInventory.ArtifactName}'.");
                }

                ArgumentNullException.ThrowIfNull(artifactInventory.Executables);
                ValidateInventory(artifactInventory.Executables);
                if (!artifactInventory.Executables.Flatten().Any())
                {
                    Refuse(
                        $"Capability '{entry.Key}' has no executable evidence in artifact " +
                        $"'{artifactInventory.ArtifactName}'.");
                }
            }

            if (entry.ExecutableInventories.Count == 0)
            {
                Refuse($"Capability '{entry.Key}' must belong to at least one executable artifact.");
            }
            CapabilityInventoryProfiles.Validate(entry);

            ValidateTokens(entry.RecoveryActions, "recovery action");
            var factTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var durableFact in entry.DurableFacts)
            {
                ArgumentNullException.ThrowIfNull(durableFact);
                if (!factTypes.Add(durableFact.FactType))
                {
                    Refuse($"Duplicate durable fact '{durableFact.FactType}' is not allowed for '{entry.Key}'.");
                }
                Require(durableFact.ArtifactName, "durable fact authority artifact");
                Require(durableFact.CurrentSchemaVersion, "durable fact current schema version");
                ValidateTokens(durableFact.ReadableSchemaVersions, "readable durable fact schema version");
                if (!durableFact.ReadableSchemaVersions.Contains(
                        durableFact.CurrentSchemaVersion,
                        StringComparer.Ordinal))
                {
                    Refuse(
                        $"Durable fact '{durableFact.FactType}' does not include its current schema " +
                        $"'{durableFact.CurrentSchemaVersion}' in readable versions.");
                }
                var authorityInventory = entry.ExecutableInventories.SingleOrDefault(x =>
                    string.Equals(x.ArtifactName, durableFact.ArtifactName, StringComparison.Ordinal));
                if (authorityInventory is null)
                {
                    Refuse(
                        $"Durable fact '{durableFact.FactType}' references unknown authority artifact " +
                        $"'{durableFact.ArtifactName}'.");
                    continue;
                }
                ValidateTokens(durableFact.RecoveryHandlerAliases, "recovery handler alias");
                foreach (var alias in durableFact.RecoveryHandlerAliases)
                {
                    if (!authorityInventory.Executables.BundledAliases.Contains(alias, StringComparer.Ordinal))
                    {
                        Refuse(
                            $"Recovery handler alias '{alias}' is not in '{entry.Key}' authority artifact " +
                            $"'{durableFact.ArtifactName}'.");
                    }
                }
            }
        }
    }

    internal static void ValidateInventory(ExecutableInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ValidateTokens(inventory.Routes, "route");
        ValidateTokens(inventory.Commands, "command");
        ValidateTokens(inventory.Handlers, "handler");
        ValidateTokens(inventory.Controls, "control");
        ValidateTokens(inventory.Migrations, "migration");
        ValidateTokens(inventory.BundledAliases, "bundled alias");
        ValidateTokens(inventory.StateStores, "state store");
        ValidateTokens(inventory.BackgroundWorkers, "background worker");
        ValidateTokens(inventory.DataFamilies, "data family");
        ValidateTokens(inventory.Caches, "cache");
        ValidateTokens(inventory.SearchIndexes, "search index");
        ValidateTokens(inventory.FileStores, "file store");
        ValidateTokens(inventory.ReportExports, "report export");
        ValidateTokens(inventory.AuditStreams, "audit stream");
        ValidateTokens(inventory.Jobs, "job");
        ValidateTokens(inventory.HostPermissions, "host permission");
        foreach (var token in inventory.HostPermissions)
        {
            if (!HostPermissionToken.IsWellFormed(token))
            {
                Refuse(
                    $"Host permission token '{token}' does not match the pinned " +
                    "'<identifier>' or '<identifier>@<16-lowercase-hex scope digest>' grammar.");
            }
        }
    }

    internal static void ValidateKey(CapabilityKey key)
    {
        if (string.IsNullOrWhiteSpace(key.Id.Value) || string.IsNullOrWhiteSpace(key.Version.Value))
        {
            Refuse("Capability keys must contain a validated id and version.");
        }
    }

    internal static void ValidateTokens(IEnumerable<string> values, string kind)
    {
        ArgumentNullException.ThrowIfNull(values);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                Refuse($"A {kind} token is empty or padded.");
            }
            if (!seen.Add(value))
            {
                Refuse($"Duplicate {kind} token '{value}' is not allowed.");
            }
        }
    }

    private static void Require(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            Refuse($"The release {kind} is empty or padded.");
        }
    }

    private static void Refuse(string message) =>
        throw new CapabilityCatalogAdmissionException(CapabilityRefusalCodes.ManifestMismatch, message);
}
