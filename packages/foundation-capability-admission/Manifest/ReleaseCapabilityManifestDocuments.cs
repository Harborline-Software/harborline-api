using System.Buffers;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.CapabilityAdmission;

/// <summary>Stable release-document verification failures, distinct from runtime capability refusals.</summary>
public static class ReleaseManifestVerificationCodes
{
    /// <summary>The document is not strict, decodable JSON in the admitted envelope shape.</summary>
    public const string Malformed = "release.manifest.malformed";
    /// <summary>The signed schema marker is unsupported.</summary>
    public const string UnsupportedSchema = "release.manifest.unsupported_schema";
    /// <summary>The Ed25519 signature is invalid.</summary>
    public const string SignatureInvalid = "release.manifest.signature_invalid";
    /// <summary>The issuer differs from the independently pinned release root.</summary>
    public const string RootMismatch = "release.manifest.root_mismatch";
    /// <summary>The signed release-engineering scope is not allowed.</summary>
    public const string ScopeMismatch = "release.manifest.scope_mismatch";
    /// <summary>The platform/build profile differs from the required profile.</summary>
    public const string ProfileMismatch = "release.manifest.profile_mismatch";
    /// <summary>The product, channel, or immutable release identity differs from policy.</summary>
    public const string ReleaseMismatch = "release.manifest.release_mismatch";
    /// <summary>The observed artifact set or bytes differ from the signed identities.</summary>
    public const string ArtifactMismatch = "release.manifest.artifact_mismatch";
    /// <summary>The signed payload violates the admitted semantic contract.</summary>
    public const string SemanticInvalid = "release.manifest.semantic_invalid";
    /// <summary>The authenticated manifest differs from independently composed build evidence.</summary>
    public const string ExpectedMismatch = "release.manifest.expected_mismatch";
    /// <summary>The release or release root is independently revoked.</summary>
    public const string Revoked = "release.manifest.revoked";
    /// <summary>The signed release sequence is below the independently persisted high-water mark.</summary>
    public const string Rollback = "release.manifest.rollback";
}

/// <summary>The independently pinned release-engineering expectations for one release document.</summary>
public sealed record ReleaseManifestTrustPolicy
{
    /// <summary>Creates immutable, independently supplied release verification expectations.</summary>
    public ReleaseManifestTrustPolicy(
        PrincipalId pinnedReleaseRoot,
        IEnumerable<string> allowedSignerScopes,
        string requiredProduct,
        string requiredChannel,
        string requiredReleaseIdentity,
        string requiredPlatformProfile,
        long minimumReleaseSequence = 0,
        IEnumerable<string>? revokedReleaseIdentities = null,
        IEnumerable<PrincipalId>? revokedReleaseRoots = null)
        : this(
            pinnedReleaseRoot,
            allowedSignerScopes,
            requiredProduct,
            requiredChannel,
            requiredReleaseIdentity,
            requiredPlatformProfile,
            minimumReleaseSequence,
            revokedReleaseIdentities,
            revokedReleaseRoots,
            allowDiscovery: false)
    {
    }

    private ReleaseManifestTrustPolicy(
        PrincipalId pinnedReleaseRoot,
        IEnumerable<string> allowedSignerScopes,
        string requiredProduct,
        string requiredChannel,
        string? requiredReleaseIdentity,
        string requiredPlatformProfile,
        long minimumReleaseSequence,
        IEnumerable<string>? revokedReleaseIdentities,
        IEnumerable<PrincipalId>? revokedReleaseRoots,
        bool allowDiscovery)
    {
        ArgumentNullException.ThrowIfNull(allowedSignerScopes);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredProduct);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredChannel);
        if (!allowDiscovery)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requiredReleaseIdentity);
        }
        if (minimumReleaseSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumReleaseSequence));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredPlatformProfile);
        var scopes = allowedSignerScopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (scopes.Length == 0 || scopes.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty signer scope is required.", nameof(allowedSignerScopes));
        }

        PinnedReleaseRoot = pinnedReleaseRoot;
        AllowedSignerScopes = Array.AsReadOnly(scopes);
        RequiredProduct = requiredProduct;
        RequiredChannel = requiredChannel;
        RequiredReleaseIdentity = requiredReleaseIdentity;
        RequiredPlatformProfile = requiredPlatformProfile;
        MinimumReleaseSequence = minimumReleaseSequence;
        RevokedReleaseIdentities = (revokedReleaseIdentities ?? Array.Empty<string>())
            .ToFrozenSet(StringComparer.Ordinal);
        RevokedReleaseRoots = (revokedReleaseRoots ?? Array.Empty<PrincipalId>()).ToFrozenSet();
    }

    /// <summary>
    /// Creates a latest-release discovery policy. The caller persists the returned sequence as its
    /// next minimum high-water mark before accepting a later pointer.
    /// </summary>
    public static ReleaseManifestTrustPolicy ForDiscovery(
        PrincipalId pinnedReleaseRoot,
        IEnumerable<string> allowedSignerScopes,
        string requiredProduct,
        string requiredChannel,
        string requiredPlatformProfile,
        long minimumReleaseSequence = 0,
        IEnumerable<string>? revokedReleaseIdentities = null,
        IEnumerable<PrincipalId>? revokedReleaseRoots = null) =>
        new(
            pinnedReleaseRoot,
            allowedSignerScopes,
            requiredProduct,
            requiredChannel,
            requiredReleaseIdentity: null,
            requiredPlatformProfile,
            minimumReleaseSequence,
            revokedReleaseIdentities,
            revokedReleaseRoots,
            allowDiscovery: true);

    /// <summary>The release-engineering public root pinned outside the document.</summary>
    public PrincipalId PinnedReleaseRoot { get; }
    /// <summary>The signer scopes admitted for this consumer and channel.</summary>
    public IReadOnlyList<string> AllowedSignerScopes { get; }
    /// <summary>The product this consumer expects.</summary>
    public string RequiredProduct { get; }
    /// <summary>The release channel this consumer expects.</summary>
    public string RequiredChannel { get; }
    /// <summary>The immutable release identity this consumer expects.</summary>
    public string? RequiredReleaseIdentity { get; }
    /// <summary>The exact platform/build profile this consumer expects.</summary>
    public string RequiredPlatformProfile { get; }
    /// <summary>The independently persisted per-channel anti-rollback floor.</summary>
    public long MinimumReleaseSequence { get; }
    /// <summary>Independently revoked immutable release identities.</summary>
    public IReadOnlySet<string> RevokedReleaseIdentities { get; }
    /// <summary>Independently revoked release roots.</summary>
    public IReadOnlySet<PrincipalId> RevokedReleaseRoots { get; }
}

/// <summary>A digest measured from actual staged or installed artifact bytes.</summary>
public sealed record ObservedReleaseArtifact
{
    private ObservedReleaseArtifact(string name, string sha256, long sizeBytes)
    {
        Name = name;
        Sha256 = sha256;
        SizeBytes = sizeBytes;
    }

    /// <summary>The stable signed artifact name.</summary>
    public string Name { get; }
    /// <summary>The SHA-256 digest measured from the observed bytes.</summary>
    public string Sha256 { get; }
    /// <summary>The byte count measured from the observed stream.</summary>
    public long SizeBytes { get; }

    /// <summary>Measures a readable stream from its current position. The caller retains stream ownership.</summary>
    public static async ValueTask<ObservedReleaseArtifact> MeasureAsync(
        string name,
        Stream artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!artifact.CanRead)
        {
            throw new ArgumentException("Artifact stream must be readable.", nameof(artifact));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long size = 0;
        try
        {
            while (true)
            {
                var read = await artifact.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                size = checked(size + read);
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new ObservedReleaseArtifact(name, Convert.ToHexStringLower(hash.GetHashAndReset()), size);
    }

    internal static ObservedReleaseArtifact FromDigest(string name, string sha256, long sizeBytes) =>
        new(name, sha256, sizeBytes);
}

/// <summary>Fail-closed verification result. The opaque manifest is exposed only on success.</summary>
public sealed record ReleaseCapabilityManifestVerificationResult
{
    private ReleaseCapabilityManifestVerificationResult(
        VerifiedReleaseCapabilityManifest? verifiedManifest,
        string? failureCode)
    {
        VerifiedManifest = verifiedManifest;
        FailureCode = failureCode;
    }

    /// <summary>Whether every framing, trust, semantic, metadata, and artifact check passed.</summary>
    public bool Succeeded => VerifiedManifest is not null;
    /// <summary>The opaque runtime input, present only after complete verification.</summary>
    public VerifiedReleaseCapabilityManifest? VerifiedManifest { get; }
    /// <summary>The stable refusal code, or null on success.</summary>
    public string? FailureCode { get; }

    internal static ReleaseCapabilityManifestVerificationResult Success(VerifiedReleaseCapabilityManifest manifest) =>
        new(manifest, null);

    internal static ReleaseCapabilityManifestVerificationResult Failure(string code) => new(null, code);
}

/// <summary>A signature-authenticated release pointer safe to use for artifact discovery.</summary>
public sealed class AuthenticatedReleaseCapabilityManifest
{
    internal AuthenticatedReleaseCapabilityManifest(ReleaseCapabilityManifest manifest) => Manifest = manifest;

    internal ReleaseCapabilityManifest Manifest { get; }

    /// <summary>The authenticated immutable release identity.</summary>
    public string ReleaseIdentity => Manifest.ReleaseIdentity;
    /// <summary>The authenticated monotonic sequence to persist as the next discovery floor.</summary>
    public long ReleaseSequence => Manifest.ReleaseSequence;
    /// <summary>The authenticated platform/build profile.</summary>
    public string PlatformProfile => Manifest.PlatformProfile;
    /// <summary>Authenticated artifact locations and digests, safe to use for download discovery.</summary>
    public IReadOnlyList<ReleaseArtifactIdentity> Artifacts => Manifest.Artifacts;
}

/// <summary>Fail-closed envelope authentication result; no artifact bytes are required at this stage.</summary>
public sealed record ReleaseCapabilityManifestAuthenticationResult
{
    private ReleaseCapabilityManifestAuthenticationResult(
        AuthenticatedReleaseCapabilityManifest? authenticatedManifest,
        string? failureCode)
    {
        AuthenticatedManifest = authenticatedManifest;
        FailureCode = failureCode;
    }

    /// <summary>Whether framing, signature, metadata, and semantic checks passed.</summary>
    public bool Succeeded => AuthenticatedManifest is not null;
    /// <summary>The authenticated download-discovery document, or null on failure.</summary>
    public AuthenticatedReleaseCapabilityManifest? AuthenticatedManifest { get; }
    /// <summary>The stable refusal code, or null on success.</summary>
    public string? FailureCode { get; }

    internal static ReleaseCapabilityManifestAuthenticationResult Success(
        AuthenticatedReleaseCapabilityManifest manifest) => new(manifest, null);

    internal static ReleaseCapabilityManifestAuthenticationResult Failure(string code) => new(null, code);
}

/// <summary>
/// Signs and verifies the one composite PLANE-3 <c>releases.json</c> document. This is the only code that
/// materializes the opaque runtime-admission wrapper.
/// </summary>
public sealed class ReleaseCapabilityManifestDocuments
{
    private const int MaximumDocumentBytes = 4 * 1024 * 1024;
    private static readonly byte[] NonceDomain = Encoding.UTF8.GetBytes("shipyard.release-capabilities.nonce.v1\0");
    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly IOperationVerifier _signatureVerifier;

    /// <summary>Creates the document service with the non-substitutable platform Ed25519 verifier.</summary>
    public ReleaseCapabilityManifestDocuments() : this(new Ed25519Verifier())
    {
    }

    internal ReleaseCapabilityManifestDocuments(IOperationVerifier signatureVerifier) =>
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));

    /// <summary>Applies the complete semantic manifest validator without minting any runtime proof.</summary>
    public void Validate(ReleaseCapabilityManifest manifest) =>
        ReleaseCapabilityManifestValidator.Validate(manifest);

    /// <summary>
    /// Compares every normalized signed field with a caller-built expected manifest. This prevents a
    /// valid signature over different capability metadata from satisfying release verification.
    /// </summary>
    public bool MatchesExpected(
        ReleaseCapabilityManifest expected,
        AuthenticatedReleaseCapabilityManifest authenticated)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(authenticated);
        ReleaseCapabilityManifestValidator.Validate(expected);
        return CanonicalJson.Serialize(ToWire(expected)).AsSpan().SequenceEqual(
            CanonicalJson.Serialize(ToWire(authenticated.Manifest)));
    }

    /// <summary>Normalizes, deterministically issues, signs, and transport-encodes a release manifest.</summary>
    public async ValueTask<byte[]> SignAsync(
        ReleaseCapabilityManifest manifest,
        IOperationSigner releaseSigner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(releaseSigner);
        ReleaseCapabilityManifestValidator.Validate(manifest);
        var wire = ToWire(manifest);
        var issuedAt = manifest.ReleasedAt.ToUniversalTime();
        var nonce = DeterministicNonce(wire, releaseSigner.IssuerId);
        var signed = await releaseSigner.SignAsync(wire, issuedAt, nonce, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToUtf8Bytes(signed, WireOptions);
    }

    /// <summary>
    /// Authenticates the signed pointer before download. The returned immutable artifact URLs and digests
    /// may be used for discovery, but cannot enter runtime admission until <see cref="VerifyArtifacts"/>.
    /// </summary>
    public ReleaseCapabilityManifestAuthenticationResult Authenticate(
        ReadOnlySpan<byte> document,
        ReleaseManifestTrustPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (document.Length == 0 || document.Length > MaximumDocumentBytes || HasDuplicateJsonProperties(document))
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.Malformed);
        }

        SignedOperation<ReleaseCapabilityManifestWire>? signed;
        try
        {
            signed = JsonSerializer.Deserialize<SignedOperation<ReleaseCapabilityManifestWire>>(document, WireOptions);
        }
        catch (JsonException)
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.Malformed);
        }
        catch (FormatException)
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.Malformed);
        }
        catch (NotSupportedException)
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.Malformed);
        }

        if (signed is null)
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.Malformed);
        }
        if (!signed.IssuerId.Equals(policy.PinnedReleaseRoot))
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.RootMismatch);
        }
        if (policy.RevokedReleaseRoots.Contains(signed.IssuerId))
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.Revoked);
        }

        try
        {
            if (!_signatureVerifier.Verify(signed))
            {
                return ReleaseCapabilityManifestAuthenticationResult.Failure(
                    ReleaseManifestVerificationCodes.SignatureInvalid);
            }
        }
        catch
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.SignatureInvalid);
        }
        if (signed.IssuedAt.ToUniversalTime() != signed.Payload.ReleasedAt.ToUniversalTime() ||
            signed.Nonce != DeterministicNonce(signed.Payload, signed.IssuerId))
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(
                ReleaseManifestVerificationCodes.SemanticInvalid);
        }

        ReleaseCapabilityManifest manifest;
        try
        {
            manifest = FromWire(signed.Payload);
        }
        catch
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.SemanticInvalid);
        }

        if (!string.Equals(manifest.SchemaVersion, ReleaseCapabilityManifest.SupportedSchemaVersion, StringComparison.Ordinal))
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.UnsupportedSchema);
        }
        if (!policy.AllowedSignerScopes.Contains(manifest.SignerScope, StringComparer.Ordinal))
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.ScopeMismatch);
        }
        if (!string.Equals(manifest.PlatformProfile, policy.RequiredPlatformProfile, StringComparison.Ordinal))
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.ProfileMismatch);
        }
        if (policy.RevokedReleaseIdentities.Contains(manifest.ReleaseIdentity))
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.Revoked);
        }
        if (manifest.ReleaseSequence < policy.MinimumReleaseSequence)
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.Rollback);
        }
        if (!string.Equals(manifest.Product, policy.RequiredProduct, StringComparison.Ordinal) ||
            !string.Equals(manifest.Channel, policy.RequiredChannel, StringComparison.Ordinal) ||
            (policy.RequiredReleaseIdentity is { } requiredIdentity &&
             !string.Equals(manifest.ReleaseIdentity, requiredIdentity, StringComparison.Ordinal)))
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.ReleaseMismatch);
        }

        try
        {
            ReleaseCapabilityManifestValidator.Validate(manifest);
            return ReleaseCapabilityManifestAuthenticationResult.Success(
                new AuthenticatedReleaseCapabilityManifest(manifest));
        }
        catch
        {
            return ReleaseCapabilityManifestAuthenticationResult.Failure(ReleaseManifestVerificationCodes.SemanticInvalid);
        }
    }

    /// <summary>Compares downloaded/installed bytes and mints the opaque runtime input only on an exact match.</summary>
    public ReleaseCapabilityManifestVerificationResult VerifyArtifacts(
        AuthenticatedReleaseCapabilityManifest authenticatedManifest,
        IReadOnlyCollection<ObservedReleaseArtifact> observedArtifacts)
    {
        ArgumentNullException.ThrowIfNull(authenticatedManifest);
        ArgumentNullException.ThrowIfNull(observedArtifacts);
        if (!ArtifactsMatch(authenticatedManifest.Manifest.Artifacts, observedArtifacts))
        {
            return ReleaseCapabilityManifestVerificationResult.Failure(ReleaseManifestVerificationCodes.ArtifactMismatch);
        }

        return ReleaseCapabilityManifestVerificationResult.Success(
            new VerifiedReleaseCapabilityManifest(
                authenticatedManifest.Manifest,
                authenticatedManifest.Manifest.PlatformProfile,
                authenticatedManifest.Manifest.Artifacts.Select(x => x.Name)));
    }

    /// <summary>
    /// Verifies one installed runtime artifact without requiring unrelated platform installers. The
    /// resulting proof can admit only a catalog for this exact artifact.
    /// </summary>
    public ReleaseCapabilityManifestVerificationResult VerifyRuntimeArtifact(
        AuthenticatedReleaseCapabilityManifest authenticatedManifest,
        string runtimeArtifactName,
        IReadOnlyCollection<ObservedReleaseArtifact> observedArtifacts)
    {
        ArgumentNullException.ThrowIfNull(authenticatedManifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeArtifactName);
        ArgumentNullException.ThrowIfNull(observedArtifacts);
        var expected = authenticatedManifest.Manifest.Artifacts
            .Where(x => string.Equals(x.Name, runtimeArtifactName, StringComparison.Ordinal))
            .ToArray();
        if (expected.Length != 1 || !ArtifactsMatch(expected, observedArtifacts))
        {
            return ReleaseCapabilityManifestVerificationResult.Failure(
                ReleaseManifestVerificationCodes.ArtifactMismatch);
        }

        return ReleaseCapabilityManifestVerificationResult.Success(
            new VerifiedReleaseCapabilityManifest(
                authenticatedManifest.Manifest,
                authenticatedManifest.Manifest.PlatformProfile,
                [runtimeArtifactName]));
    }

    /// <summary>Convenience composition for callers that already possess the exact artifact bytes.</summary>
    public ReleaseCapabilityManifestVerificationResult Verify(
        ReadOnlySpan<byte> document,
        ReleaseManifestTrustPolicy policy,
        IReadOnlyCollection<ObservedReleaseArtifact> observedArtifacts)
    {
        var authentication = Authenticate(document, policy);
        return authentication.AuthenticatedManifest is { } authenticated
            ? VerifyArtifacts(authenticated, observedArtifacts)
            : ReleaseCapabilityManifestVerificationResult.Failure(
                authentication.FailureCode ?? ReleaseManifestVerificationCodes.SemanticInvalid);
    }

    private static bool ArtifactsMatch(
        IReadOnlyList<ReleaseArtifactIdentity> expected,
        IReadOnlyCollection<ObservedReleaseArtifact> observed)
    {
        var byName = new Dictionary<string, ObservedReleaseArtifact>(StringComparer.Ordinal);
        foreach (var artifact in observed)
        {
            if (artifact is null || !byName.TryAdd(artifact.Name, artifact))
            {
                return false;
            }
        }
        if (byName.Count != expected.Count)
        {
            return false;
        }
        foreach (var artifact in expected)
        {
            if (!byName.TryGetValue(artifact.Name, out var actual) || actual.SizeBytes != artifact.SizeBytes)
            {
                return false;
            }
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(actual.Sha256),
                        Convert.FromHexString(artifact.Sha256)))
                {
                    return false;
                }
            }
            catch (FormatException)
            {
                return false;
            }
        }
        return true;
    }

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

    private static Guid DeterministicNonce(ReleaseCapabilityManifestWire wire, PrincipalId issuer)
    {
        var payload = CanonicalJson.Serialize(wire);
        var bytes = new byte[NonceDomain.Length + PrincipalId.LengthInBytes + payload.Length];
        NonceDomain.CopyTo(bytes, 0);
        issuer.AsSpan().CopyTo(bytes.AsSpan(NonceDomain.Length));
        payload.CopyTo(bytes.AsSpan(NonceDomain.Length + PrincipalId.LengthInBytes));
        var digest = SHA256.HashData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return new Guid(digest.AsSpan(0, 16), bigEndian: true);
    }

    private static ReleaseCapabilityManifestWire ToWire(ReleaseCapabilityManifest manifest) => new(
        manifest.SchemaVersion,
        manifest.Product,
        manifest.Channel,
        manifest.ReleaseIdentity,
        manifest.ReleaseSequence,
        manifest.ReleasedAt.ToUniversalTime(),
        manifest.PlatformProfile,
        manifest.SignerScope,
        manifest.Notes,
        manifest.Artifacts.OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new ReleaseArtifactWire(x.Name, x.Platform, x.Url, x.Sha256, x.SizeBytes)).ToArray(),
        manifest.Capabilities.OrderBy(x => x.Key)
            .Select(x => new ReleaseCapabilityEntryWire(
                x.Key.Id.Value,
                x.Key.Version.Value,
                x.ExecutableInventories.OrderBy(y => y.ArtifactName, StringComparer.Ordinal)
                    .Select(y => new ArtifactExecutableInventoryWire(
                        y.ArtifactName,
                        ToWire(y.Executables))).ToArray(),
                x.RecoveryActions.Order(StringComparer.Ordinal).ToArray(),
                x.DurableFacts.OrderBy(y => y.FactType, StringComparer.Ordinal)
                    .Select(y => new DurableFactCompatibilityWire(
                        y.FactType,
                        y.ArtifactName,
                        y.CurrentSchemaVersion,
                        y.ReadableSchemaVersions.Order(StringComparer.Ordinal).ToArray(),
                        y.RecoveryHandlerAliases.Order(StringComparer.Ordinal).ToArray())).ToArray()))
            .ToArray());

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

    private static ReleaseCapabilityManifest FromWire(ReleaseCapabilityManifestWire wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        return ReleaseCapabilityManifest.Create(
            wire.SchemaVersion,
            wire.Product,
            wire.Channel,
            wire.ReleaseIdentity,
            wire.ReleasedAt,
            wire.PlatformProfile,
            wire.SignerScope,
            wire.Notes,
            Require(wire.Artifacts).Select(x => ReleaseArtifactIdentity.Create(
                x.Name, x.Platform, x.Url, x.Sha256, x.SizeBytes)),
            Require(wire.Capabilities).Select(x => ReleaseCapabilityEntry.Create(
                new CapabilityKey(CapabilityId.Of(x.CapabilityId), CapabilityVersion.Of(x.CapabilityVersion)),
                Require(x.ExecutableInventories).Select(y => ArtifactExecutableInventory.Create(
                    y.ArtifactName,
                    FromWire(y.Executables))),
                Require(x.RecoveryActions),
                Require(x.DurableFacts).Select(y => DurableFactCompatibility.Create(
                    y.FactType,
                    y.ArtifactName,
                    y.CurrentSchemaVersion,
                    Require(y.ReadableSchemaVersions),
                    Require(y.RecoveryHandlerAliases))))),
            wire.ReleaseSequence);
    }

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
        values ?? throw new JsonException("A required manifest collection is null.");
}

internal sealed record ReleaseCapabilityManifestWire(
    string SchemaVersion,
    string Product,
    string Channel,
    string ReleaseIdentity,
    long ReleaseSequence,
    DateTimeOffset ReleasedAt,
    string PlatformProfile,
    string SignerScope,
    string Notes,
    IReadOnlyList<ReleaseArtifactWire> Artifacts,
    IReadOnlyList<ReleaseCapabilityEntryWire> Capabilities);

internal sealed record ReleaseArtifactWire(
    string Name,
    string Platform,
    string Url,
    string Sha256,
    long SizeBytes);

internal sealed record ReleaseCapabilityEntryWire(
    string CapabilityId,
    string CapabilityVersion,
    IReadOnlyList<ArtifactExecutableInventoryWire> ExecutableInventories,
    IReadOnlyList<string> RecoveryActions,
    IReadOnlyList<DurableFactCompatibilityWire> DurableFacts);

internal sealed record ArtifactExecutableInventoryWire(
    string ArtifactName,
    ExecutableInventoryWire Executables);

internal sealed record ExecutableInventoryWire(
    IReadOnlyList<string> Routes,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> Handlers,
    IReadOnlyList<string> Controls,
    IReadOnlyList<string> Migrations,
    IReadOnlyList<string> BundledAliases,
    IReadOnlyList<string> StateStores,
    IReadOnlyList<string> BackgroundWorkers,
    IReadOnlyList<string> DataFamilies,
    IReadOnlyList<string> Caches,
    IReadOnlyList<string> SearchIndexes,
    IReadOnlyList<string> FileStores,
    IReadOnlyList<string> ReportExports,
    IReadOnlyList<string> AuditStreams,
    IReadOnlyList<string> Jobs,
    IReadOnlyList<string> HostPermissions);

internal sealed record DurableFactCompatibilityWire(
    string FactType,
    string ArtifactName,
    string CurrentSchemaVersion,
    IReadOnlyList<string> ReadableSchemaVersions,
    IReadOnlyList<string> RecoveryHandlerAliases);
