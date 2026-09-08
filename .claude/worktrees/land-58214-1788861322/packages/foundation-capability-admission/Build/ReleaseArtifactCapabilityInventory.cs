using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.CapabilityAdmission;

/// <summary>
/// One build-produced capability declaration owned by a single final release artifact.
/// </summary>
public sealed record ReleaseArtifactCapabilityDeclaration
{
    private ReleaseArtifactCapabilityDeclaration(
        CapabilityKey key,
        ExecutableInventory executables,
        IReadOnlyList<string> recoveryActions,
        IReadOnlyList<DurableFactCompatibility> durableFacts)
    {
        Key = key;
        Executables = executables;
        RecoveryActions = recoveryActions;
        DurableFacts = durableFacts;
    }

    /// <summary>The stable capability id and version.</summary>
    public CapabilityKey Key { get; }
    /// <summary>The registrations compiled into the owning artifact.</summary>
    public ExecutableInventory Executables { get; }
    /// <summary>Reviewed recovery actions implemented by the owning artifact.</summary>
    public IReadOnlyList<string> RecoveryActions { get; }
    /// <summary>Durable-fact compatibility implemented by the owning artifact.</summary>
    public IReadOnlyList<DurableFactCompatibility> DurableFacts { get; }

    /// <summary>Creates an immutable artifact-owned declaration.</summary>
    public static ReleaseArtifactCapabilityDeclaration Create(
        CapabilityKey key,
        ExecutableInventory executables,
        IEnumerable<string>? recoveryActions = null,
        IEnumerable<DurableFactCompatibility>? durableFacts = null) =>
        new(
            key,
            executables ?? throw new ArgumentNullException(nameof(executables)),
            Array.AsReadOnly((recoveryActions ?? Array.Empty<string>()).ToArray()),
            Array.AsReadOnly((durableFacts ?? Array.Empty<DurableFactCompatibility>()).ToArray()));
}

/// <summary>
/// Strict post-build receipt binding a machine-emitted executable inventory to exact final artifact bytes.
/// It is build evidence only; the signed composite release manifest remains runtime authority.
/// </summary>
public sealed record ReleaseArtifactCapabilityInventory
{
    /// <summary>The only admitted artifact-inventory schema.</summary>
    public const string SupportedSchemaVersion = "shipyard.release-artifact-inventory/v2";

    private ReleaseArtifactCapabilityInventory(
        string schemaVersion,
        string artifactName,
        string platformProfile,
        string artifactSha256,
        long artifactSizeBytes,
        IReadOnlyList<ReleaseArtifactCapabilityDeclaration> capabilities)
    {
        SchemaVersion = schemaVersion;
        ArtifactName = artifactName;
        PlatformProfile = platformProfile;
        ArtifactSha256 = artifactSha256;
        ArtifactSizeBytes = artifactSizeBytes;
        Capabilities = capabilities;
    }

    /// <summary>The closed document schema.</summary>
    public string SchemaVersion { get; }
    /// <summary>The exact top-level release artifact this receipt describes.</summary>
    public string ArtifactName { get; }
    /// <summary>The build profile whose conditional registrations were resolved.</summary>
    public string PlatformProfile { get; }
    /// <summary>The lowercase SHA-256 of the final artifact bytes.</summary>
    public string ArtifactSha256 { get; }
    /// <summary>The exact final artifact byte count.</summary>
    public long ArtifactSizeBytes { get; }
    /// <summary>The artifact-owned capability registrations.</summary>
    public IReadOnlyList<ReleaseArtifactCapabilityDeclaration> Capabilities { get; }

    /// <summary>Creates and validates an immutable post-build receipt.</summary>
    public static ReleaseArtifactCapabilityInventory Create(
        string schemaVersion,
        string artifactName,
        string platformProfile,
        string artifactSha256,
        long artifactSizeBytes,
        IEnumerable<ReleaseArtifactCapabilityDeclaration> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        var inventory = new ReleaseArtifactCapabilityInventory(
            schemaVersion,
            artifactName,
            platformProfile,
            artifactSha256,
            artifactSizeBytes,
            Array.AsReadOnly(capabilities.ToArray()));
        ReleaseArtifactCapabilityInventoryValidator.Validate(inventory);
        return inventory;
    }
}

/// <summary>Deterministic strict JSON codec for post-build artifact inventory receipts.</summary>
public sealed class ReleaseArtifactCapabilityInventoryDocuments
{
    private const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Serializes a validated receipt with stable ordering and canonical JSON.</summary>
    public byte[] Serialize(ReleaseArtifactCapabilityInventory inventory)
    {
        ReleaseArtifactCapabilityInventoryValidator.Validate(inventory);
        return JsonSerializer.SerializeToUtf8Bytes(ToWire(inventory), WireOptions);
    }

    /// <summary>Strictly parses and semantically validates one receipt.</summary>
    public ReleaseArtifactCapabilityInventory Parse(ReadOnlySpan<byte> document)
    {
        if (document.Length == 0 || document.Length > MaximumDocumentBytes || HasDuplicateJsonProperties(document))
        {
            throw new JsonException(
                "Artifact inventory is empty, oversized, malformed, or contains duplicate properties.");
        }

        ArtifactCapabilityInventoryWire wire;
        try
        {
            wire = JsonSerializer.Deserialize<ArtifactCapabilityInventoryWire>(document, WireOptions)
                ?? throw new JsonException("Artifact inventory did not decode.");
        }
        catch (NotSupportedException ex)
        {
            throw new JsonException("Artifact inventory uses an unsupported JSON shape.", ex);
        }

        return FromWire(wire);
    }

    private static ArtifactCapabilityInventoryWire ToWire(ReleaseArtifactCapabilityInventory inventory) => new(
        inventory.SchemaVersion,
        inventory.ArtifactName,
        inventory.PlatformProfile,
        inventory.ArtifactSha256,
        inventory.ArtifactSizeBytes,
        inventory.Capabilities.OrderBy(x => x.Key).Select(x => new ArtifactCapabilityDeclarationWire(
            x.Key.Id.Value,
            x.Key.Version.Value,
            ToWire(x.Executables),
            x.RecoveryActions.Order(StringComparer.Ordinal).ToArray(),
            x.DurableFacts.OrderBy(y => y.FactType, StringComparer.Ordinal).Select(y =>
                new ArtifactDurableFactWire(
                    y.FactType,
                    y.ArtifactName,
                    y.CurrentSchemaVersion,
                    y.ReadableSchemaVersions.Order(StringComparer.Ordinal).ToArray(),
                    y.RecoveryHandlerAliases.Order(StringComparer.Ordinal).ToArray())).ToArray())).ToArray());

    private static ExecutableInventoryWire ToWire(ExecutableInventory inventory) => new(
        inventory.Routes.Order(StringComparer.Ordinal).ToArray(),
        inventory.Commands.Order(StringComparer.Ordinal).ToArray(),
        inventory.Handlers.Order(StringComparer.Ordinal).ToArray(),
        inventory.Controls.Order(StringComparer.Ordinal).ToArray(),
        inventory.Migrations.Order(StringComparer.Ordinal).ToArray(),
        inventory.BundledAliases.Order(StringComparer.Ordinal).ToArray(),
        inventory.StateStores.Order(StringComparer.Ordinal).ToArray(),
        inventory.BackgroundWorkers.Order(StringComparer.Ordinal).ToArray(),
        inventory.DataFamilies.Order(StringComparer.Ordinal).ToArray(),
        inventory.Caches.Order(StringComparer.Ordinal).ToArray(),
        inventory.SearchIndexes.Order(StringComparer.Ordinal).ToArray(),
        inventory.FileStores.Order(StringComparer.Ordinal).ToArray(),
        inventory.ReportExports.Order(StringComparer.Ordinal).ToArray(),
        inventory.AuditStreams.Order(StringComparer.Ordinal).ToArray(),
        inventory.Jobs.Order(StringComparer.Ordinal).ToArray(),
        inventory.HostPermissions.Order(StringComparer.Ordinal).ToArray());

    private static ReleaseArtifactCapabilityInventory FromWire(ArtifactCapabilityInventoryWire wire) =>
        ReleaseArtifactCapabilityInventory.Create(
            wire.SchemaVersion,
            wire.ArtifactName,
            wire.PlatformProfile,
            wire.ArtifactSha256,
            wire.ArtifactSizeBytes,
            Require(wire.Capabilities).Select(x => ReleaseArtifactCapabilityDeclaration.Create(
                new CapabilityKey(CapabilityId.Of(x.CapabilityId), CapabilityVersion.Of(x.CapabilityVersion)),
                FromWire(x.Executables),
                Require(x.RecoveryActions),
                Require(x.DurableFacts).Select(y => DurableFactCompatibility.Create(
                    y.FactType,
                    y.ArtifactName,
                    y.CurrentSchemaVersion,
                    Require(y.ReadableSchemaVersions),
                    Require(y.RecoveryHandlerAliases))))));

    private static ExecutableInventory FromWire(ExecutableInventoryWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return new ExecutableInventory(
            Require(wire.Routes),
            Require(wire.Commands),
            Require(wire.Handlers),
            Require(wire.Controls),
            Require(wire.Migrations),
            Require(wire.BundledAliases),
            Require(wire.StateStores),
            Require(wire.BackgroundWorkers),
            Require(wire.DataFamilies),
            Require(wire.Caches),
            Require(wire.SearchIndexes),
            Require(wire.FileStores),
            Require(wire.ReportExports),
            Require(wire.AuditStreams),
            Require(wire.Jobs),
            Require(wire.HostPermissions));
    }

    private static IReadOnlyList<T> Require<T>(IReadOnlyList<T>? values) =>
        values ?? throw new JsonException("A required artifact inventory collection is null.");

    private static bool HasDuplicateJsonProperties(ReadOnlySpan<byte> document)
    {
        try
        {
            using var parsed = JsonDocument.Parse(document.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
            return HasDuplicates(parsed.RootElement);
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static bool HasDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicates(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicates(item))
                {
                    return true;
                }
            }
        }
        return false;
    }
}

internal static class ReleaseArtifactCapabilityInventoryValidator
{
    public static void Validate(ReleaseArtifactCapabilityInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        RequireToken(inventory.SchemaVersion, "schema version");
        RequireToken(inventory.ArtifactName, "artifact name");
        RequireToken(inventory.PlatformProfile, "platform profile");
        if (!string.Equals(
                inventory.SchemaVersion,
                ReleaseArtifactCapabilityInventory.SupportedSchemaVersion,
                StringComparison.Ordinal))
        {
            Refuse($"Unsupported artifact inventory schema '{inventory.SchemaVersion}'.");
        }
        if (inventory.ArtifactSizeBytes < 0)
        {
            Refuse("Artifact inventory size cannot be negative.");
        }
        if (string.IsNullOrEmpty(inventory.ArtifactSha256) ||
            inventory.ArtifactSha256.Length != 64 || inventory.ArtifactSha256.Any(c =>
                !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c)))
        {
            Refuse("Artifact inventory SHA-256 must be 64 lowercase hexadecimal characters.");
        }

        var keys = new HashSet<CapabilityKey>();
        foreach (var capability in inventory.Capabilities)
        {
            ArgumentNullException.ThrowIfNull(capability);
            ReleaseCapabilityManifestValidator.ValidateKey(capability.Key);
            if (!keys.Add(capability.Key))
            {
                Refuse($"Duplicate artifact capability '{capability.Key}' is not allowed.");
            }
            ReleaseCapabilityManifestValidator.ValidateInventory(capability.Executables);
            if (!capability.Executables.Flatten().Any())
            {
                Refuse($"Artifact capability '{capability.Key}' has no executable evidence.");
            }
            ReleaseCapabilityManifestValidator.ValidateTokens(capability.RecoveryActions, "recovery action");

            var facts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fact in capability.DurableFacts)
            {
                ArgumentNullException.ThrowIfNull(fact);
                RequireToken(fact.FactType, "durable fact type");
                RequireToken(fact.ArtifactName, "durable fact artifact name");
                RequireToken(fact.CurrentSchemaVersion, "durable fact current schema version");
                if (!facts.Add(fact.FactType))
                {
                    Refuse($"Duplicate durable fact '{fact.FactType}' is not allowed for '{capability.Key}'.");
                }
                if (!string.Equals(fact.ArtifactName, inventory.ArtifactName, StringComparison.Ordinal))
                {
                    Refuse(
                        $"Durable fact '{fact.FactType}' names '{fact.ArtifactName}' but the owning " +
                        $"artifact receipt is '{inventory.ArtifactName}'.");
                }
                ReleaseCapabilityManifestValidator.ValidateTokens(
                    fact.ReadableSchemaVersions,
                    "readable durable fact schema version");
                ReleaseCapabilityManifestValidator.ValidateTokens(
                    fact.RecoveryHandlerAliases,
                    "recovery handler alias");
                if (!fact.ReadableSchemaVersions.Contains(fact.CurrentSchemaVersion, StringComparer.Ordinal))
                {
                    Refuse(
                        $"Durable fact '{fact.FactType}' does not include current schema " +
                        $"'{fact.CurrentSchemaVersion}' in readable versions.");
                }
                foreach (var alias in fact.RecoveryHandlerAliases)
                {
                    if (!capability.Executables.BundledAliases.Contains(alias, StringComparer.Ordinal))
                    {
                        Refuse(
                            $"Durable fact '{fact.FactType}' recovery alias '{alias}' is not in the " +
                            $"owning artifact inventory.");
                    }
                }
            }
        }
    }

    private static void RequireToken(string value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsWhiteSpace))
        {
            Refuse($"Artifact inventory {kind} is empty, padded, or contains whitespace.");
        }
    }

    private static void Refuse(string message) =>
        throw new CapabilityCatalogAdmissionException(CapabilityRefusalCodes.ManifestMismatch, message);
}

internal sealed record ArtifactCapabilityInventoryWire(
    string SchemaVersion,
    string ArtifactName,
    string PlatformProfile,
    string ArtifactSha256,
    long ArtifactSizeBytes,
    IReadOnlyList<ArtifactCapabilityDeclarationWire> Capabilities);

internal sealed record ArtifactCapabilityDeclarationWire(
    string CapabilityId,
    string CapabilityVersion,
    ExecutableInventoryWire Executables,
    IReadOnlyList<string> RecoveryActions,
    IReadOnlyList<ArtifactDurableFactWire> DurableFacts);

internal sealed record ArtifactDurableFactWire(
    string FactType,
    string ArtifactName,
    string CurrentSchemaVersion,
    IReadOnlyList<string> ReadableSchemaVersions,
    IReadOnlyList<string> RecoveryHandlerAliases);
