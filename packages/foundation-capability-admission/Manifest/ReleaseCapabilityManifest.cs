namespace Harborline.Api.Foundation.CapabilityAdmission;

/// <summary>An executable artifact whose exact published bytes are bound by the release signature.</summary>
public sealed record ReleaseArtifactIdentity
{
    private ReleaseArtifactIdentity(
        string name,
        string platform,
        string url,
        string sha256,
        long sizeBytes)
    {
        Name = name;
        Platform = platform;
        Url = url;
        Sha256 = sha256;
        SizeBytes = sizeBytes;
    }

    /// <summary>The stable artifact name, such as <c>carrier.bundle</c> or <c>local-node</c>.</summary>
    public string Name { get; }
    /// <summary>The build platform or runtime identifier for the artifact.</summary>
    public string Platform { get; }
    /// <summary>The release-channel location of the exact hashed bytes.</summary>
    public string Url { get; }
    /// <summary>The lowercase SHA-256 digest of the final artifact bytes.</summary>
    public string Sha256 { get; }
    /// <summary>The exact artifact size in bytes.</summary>
    public long SizeBytes { get; }

    /// <summary>Creates a validated artifact identity.</summary>
    public static ReleaseArtifactIdentity Create(
        string name,
        string platform,
        string url,
        string sha256,
        long sizeBytes)
    {
        RequireToken(name, nameof(name));
        RequireToken(platform, nameof(platform));
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (!Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out _))
        {
            throw new ArgumentException("Artifact URL must be a valid relative or absolute URI.", nameof(url));
        }
        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        }
        if (sha256.Length != 64 || sha256.Any(c => !char.IsAsciiHexDigit(c) || char.IsAsciiLetterUpper(c)))
        {
            throw new ArgumentException("Artifact SHA-256 must be 64 lowercase hexadecimal characters.", nameof(sha256));
        }

        return new ReleaseArtifactIdentity(name, platform, url, sha256, sizeBytes);
    }

    internal static ReleaseArtifactIdentity Create(string name, string sha256, long sizeBytes) =>
        Create(name, "test", $"artifacts/{name}", sha256, sizeBytes);

    private static void RequireToken(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Artifact identity tokens must be unpadded and whitespace-free.", parameterName);
        }
    }
}

/// <summary>One artifact-owned executable projection for a versioned capability.</summary>
public sealed record ArtifactExecutableInventory
{
    private ArtifactExecutableInventory(string artifactName, ExecutableInventory executables)
    {
        ArtifactName = artifactName;
        Executables = executables;
    }

    /// <summary>The exact top-level release artifact that owns these registrations.</summary>
    public string ArtifactName { get; }
    /// <summary>The executable registrations compiled into that artifact.</summary>
    public ExecutableInventory Executables { get; }

    /// <summary>Creates an artifact-scoped executable projection.</summary>
    public static ArtifactExecutableInventory Create(string artifactName, ExecutableInventory executables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);
        if (!string.Equals(artifactName, artifactName.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Artifact name must not be padded.", nameof(artifactName));
        }

        return new ArtifactExecutableInventory(
            artifactName,
            executables ?? throw new ArgumentNullException(nameof(executables)));
    }
}

/// <summary>Compatibility/recovery declaration for one durable fact family owned by a capability.</summary>
public sealed record DurableFactCompatibility
{
    private DurableFactCompatibility(
        string factType,
        string artifactName,
        string currentSchemaVersion,
        IReadOnlyList<string> readableSchemaVersions,
        IReadOnlyList<string> recoveryHandlerAliases)
    {
        FactType = factType;
        ArtifactName = artifactName;
        CurrentSchemaVersion = currentSchemaVersion;
        ReadableSchemaVersions = readableSchemaVersions;
        RecoveryHandlerAliases = recoveryHandlerAliases;
    }

    /// <summary>The stable durable-fact family.</summary>
    public string FactType { get; }
    /// <summary>The artifact that owns recovery authority for this fact family.</summary>
    public string ArtifactName { get; }
    /// <summary>The schema version written by this release.</summary>
    public string CurrentSchemaVersion { get; }
    /// <summary>Every historical schema version this release can read and recover.</summary>
    public IReadOnlyList<string> ReadableSchemaVersions { get; }
    /// <summary>Manifest-bound bundled handlers that preserve recovery for existing facts.</summary>
    public IReadOnlyList<string> RecoveryHandlerAliases { get; }

    /// <summary>Creates a defensive compatibility declaration.</summary>
    public static DurableFactCompatibility Create(
        string factType,
        string artifactName,
        string currentSchemaVersion,
        IEnumerable<string> readableSchemaVersions,
        IEnumerable<string>? recoveryHandlerAliases = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factType);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentSchemaVersion);
        ArgumentNullException.ThrowIfNull(readableSchemaVersions);
        return new DurableFactCompatibility(
            factType,
            artifactName,
            currentSchemaVersion,
            Array.AsReadOnly(readableSchemaVersions.ToArray()),
            Array.AsReadOnly((recoveryHandlerAliases ?? Array.Empty<string>()).ToArray()));
    }

    internal static DurableFactCompatibility Create(
        string factType,
        string schemaVersion,
        IEnumerable<string>? recoveryHandlerAliases = null) =>
        Create(factType, "carrier.bundle", schemaVersion, [schemaVersion], recoveryHandlerAliases);
}

/// <summary>One capability entry inside a signed composite release manifest.</summary>
public sealed record ReleaseCapabilityEntry
{
    private ReleaseCapabilityEntry(
        CapabilityKey key,
        IReadOnlyList<ArtifactExecutableInventory> executableInventories,
        IReadOnlyList<string> recoveryActions,
        IReadOnlyList<DurableFactCompatibility> durableFacts)
    {
        Key = key;
        ExecutableInventories = executableInventories;
        RecoveryActions = recoveryActions;
        DurableFacts = durableFacts;
    }

    /// <summary>The exact capability id and contract version.</summary>
    public CapabilityKey Key { get; }
    /// <summary>Executable registrations partitioned by the artifact that can actually observe them.</summary>
    public IReadOnlyList<ArtifactExecutableInventory> ExecutableInventories { get; }
    /// <summary>Reviewed actions allowed during bounded disable/recovery transitions.</summary>
    public IReadOnlyList<string> RecoveryActions { get; }
    /// <summary>Durable facts whose compatibility/recovery handlers this release preserves.</summary>
    public IReadOnlyList<DurableFactCompatibility> DurableFacts { get; }

    /// <summary>Creates an entry with defensive collection snapshots.</summary>
    public static ReleaseCapabilityEntry Create(
        CapabilityKey key,
        IEnumerable<ArtifactExecutableInventory> executableInventories,
        IEnumerable<string>? recoveryActions = null,
        IEnumerable<DurableFactCompatibility>? durableFacts = null)
    {
        ArgumentNullException.ThrowIfNull(executableInventories);
        return new ReleaseCapabilityEntry(
            key,
            Array.AsReadOnly(executableInventories.ToArray()),
            Array.AsReadOnly((recoveryActions ?? Array.Empty<string>()).ToArray()),
            Array.AsReadOnly((durableFacts ?? Array.Empty<DurableFactCompatibility>()).ToArray()));
    }

    internal static ReleaseCapabilityEntry Create(
        CapabilityKey key,
        ExecutableInventory executables,
        IEnumerable<string>? recoveryActions = null,
        IEnumerable<DurableFactCompatibility>? durableFacts = null) =>
        Create(
            key,
            [ArtifactExecutableInventory.Create("carrier.bundle", executables)],
            recoveryActions,
            durableFacts);
}

/// <summary>
/// The single signed PLANE-3 release and capability truth. It carries no tenant state, permissions,
/// configuration, or executable code.
/// </summary>
public sealed record ReleaseCapabilityManifest
{
    /// <summary>The only manifest schema this package currently admits.</summary>
    public const string SupportedSchemaVersion = "shipyard.release-capabilities/v2";

    private ReleaseCapabilityManifest(
        string schemaVersion,
        string product,
        string channel,
        string releaseIdentity,
        DateTimeOffset releasedAt,
        string platformProfile,
        string signerScope,
        string notes,
        IReadOnlyList<ReleaseArtifactIdentity> artifacts,
        IReadOnlyList<ReleaseCapabilityEntry> capabilities,
        long releaseSequence)
    {
        SchemaVersion = schemaVersion;
        Product = product;
        Channel = channel;
        ReleaseIdentity = releaseIdentity;
        ReleasedAt = releasedAt;
        PlatformProfile = platformProfile;
        SignerScope = signerScope;
        Notes = notes;
        Artifacts = artifacts;
        Capabilities = capabilities;
        ReleaseSequence = releaseSequence;
    }

    /// <summary>The manifest schema marker.</summary>
    public string SchemaVersion { get; }
    /// <summary>The distributable product identifier.</summary>
    public string Product { get; }
    /// <summary>The release channel.</summary>
    public string Channel { get; }
    /// <summary>The immutable release identity bound by the signature envelope.</summary>
    public string ReleaseIdentity { get; }
    /// <summary>The release issuance instant, normalized by the generator.</summary>
    public DateTimeOffset ReleasedAt { get; }
    /// <summary>The release platform/build profile.</summary>
    public string PlatformProfile { get; }
    /// <summary>The release-engineering signer scope the envelope must attest.</summary>
    public string SignerScope { get; }
    /// <summary>Human-facing release notes; never interpreted as executable configuration.</summary>
    public string Notes { get; }
    /// <summary>Final executable artifacts and digests bound by the release signature.</summary>
    public IReadOnlyList<ReleaseArtifactIdentity> Artifacts { get; }
    /// <summary>The complete declared capability set for this release profile.</summary>
    public IReadOnlyList<ReleaseCapabilityEntry> Capabilities { get; }
    /// <summary>Monotonic channel sequence used with independently persisted anti-rollback policy.</summary>
    public long ReleaseSequence { get; }

    /// <summary>Creates a manifest with defensive artifact and capability snapshots.</summary>
    public static ReleaseCapabilityManifest Create(
        string schemaVersion,
        string product,
        string channel,
        string releaseIdentity,
        DateTimeOffset releasedAt,
        string platformProfile,
        string signerScope,
        string notes,
        IEnumerable<ReleaseArtifactIdentity> artifacts,
        IEnumerable<ReleaseCapabilityEntry> capabilities,
        long releaseSequence = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(product);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(signerScope);
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (releaseSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(releaseSequence));
        }
        return new ReleaseCapabilityManifest(
            schemaVersion,
            product,
            channel,
            releaseIdentity,
            releasedAt,
            platformProfile,
            signerScope,
            notes,
            Array.AsReadOnly(artifacts.ToArray()),
            Array.AsReadOnly(capabilities.ToArray()),
            releaseSequence);
    }

    internal static ReleaseCapabilityManifest Create(
        string schemaVersion,
        string releaseIdentity,
        string platformProfile,
        string signerScope,
        IEnumerable<ReleaseArtifactIdentity> artifacts,
        IEnumerable<ReleaseCapabilityEntry> capabilities) =>
        Create(
            schemaVersion,
            "test-product",
            "test-channel",
            releaseIdentity,
            DateTimeOffset.UnixEpoch,
            platformProfile,
            signerScope,
            string.Empty,
            artifacts,
            capabilities);
}

/// <summary>
/// Opaque proof that release signature, signer scope, profile, artifact digests, and manifest shape were
/// verified before runtime admission. Only this assembly's verifier can construct it.
/// </summary>
public sealed class VerifiedReleaseCapabilityManifest
{
    internal VerifiedReleaseCapabilityManifest(ReleaseCapabilityManifest manifest, string expectedPlatformProfile)
        : this(manifest, expectedPlatformProfile, manifest.Artifacts.Select(x => x.Name))
    {
    }

    internal VerifiedReleaseCapabilityManifest(
        ReleaseCapabilityManifest manifest,
        string expectedPlatformProfile,
        IEnumerable<string> verifiedArtifactNames)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPlatformProfile);
        ArgumentNullException.ThrowIfNull(verifiedArtifactNames);
        if (!string.Equals(
                manifest.SchemaVersion,
                ReleaseCapabilityManifest.SupportedSchemaVersion,
                StringComparison.Ordinal) ||
            !string.Equals(manifest.PlatformProfile, expectedPlatformProfile, StringComparison.Ordinal))
        {
            throw new CapabilityCatalogAdmissionException(
                CapabilityRefusalCodes.ManifestMismatch,
                "The verified manifest schema or platform profile is not admitted by this runtime.");
        }

        ReleaseCapabilityManifestValidator.Validate(manifest);
        var names = verifiedArtifactNames.ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0 || names.Any(x => !manifest.Artifacts.Any(
                artifact => string.Equals(artifact.Name, x, StringComparison.Ordinal))))
        {
            throw new CapabilityCatalogAdmissionException(
                CapabilityRefusalCodes.ManifestMismatch,
                "Verified artifact evidence is empty or absent from the signed manifest.");
        }
        Manifest = manifest;
        VerifiedArtifactNames = names;
    }

    internal ReleaseCapabilityManifest Manifest { get; }
    internal IReadOnlySet<string> VerifiedArtifactNames { get; }
}
