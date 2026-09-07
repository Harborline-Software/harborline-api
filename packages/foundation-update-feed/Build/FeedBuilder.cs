using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.UpdateFeed.Contract;
using Harborline.Api.Foundation.UpdateFeed.Serialization;

namespace Harborline.Api.Foundation.UpdateFeed.Build;

/// <summary>
/// Produces a signed feed tree (design note §2) from already-exported artifacts + a channel-root
/// signer. Reuses the existing signing envelope wholesale (<c>SignedOperation</c> + Ed25519 +
/// <c>CanonicalJson</c> — S-14) and content-addresses every referenced document with the platform
/// <c>Cid</c> primitive; it invents no crypto and no second canonicalizer.
/// </summary>
/// <remarks>
/// <para><b>F3 discipline is baked in (§7.1):</b> every CID a document references
/// (<c>revocationsCid</c>, <c>feedPolicyCid</c>, <c>indexCid</c>, <c>manifestCid</c>,
/// <c>artifactCid</c>) is computed from the referenced bytes BEFORE the referencing document is signed,
/// and <see cref="FeedTree.Files"/> is ordered <c>revocations.json</c> → <c>feed-policy.json</c> → pack
/// files → <c>channel.json</c>. The channel index that vouches for the policy and revocation list is the
/// LAST thing written, so a partial publish can never serve a fresh index pointing at missing/old policy
/// or revocation bytes.</para>
/// <para><b>Two signature layers (§2.3):</b> the publisher already signed each artifact's manifest
/// (carried in <see cref="FeedPackVersionInput.ManifestEnvelope"/>); this builder adds the channel-root
/// signature over the index documents. It does NOT re-sign manifests — it references them by CID.</para>
/// </remarks>
public sealed class FeedBuilder
{
    private readonly FeedFileCodec _codec;
    private readonly FeedPublishComplianceGate _complianceGate;

    /// <summary>Constructs a builder over the feed document codec.</summary>
    public FeedBuilder(FeedFileCodec codec)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        var counselRegister = DcpCounselRegister.FromEmbeddedResource();
        _complianceGate = new FeedPublishComplianceGate(
            _codec,
            new PackFileCodec(),
            new DcpValidator(counselRegister),
            counselRegister);
    }

    /// <summary>Constructs a builder with an explicit DCP validator and counsel register. This seam is
    /// used by self-governed feeds and tests; it never changes trust roots or signing behavior.</summary>
    public FeedBuilder(
        FeedFileCodec codec,
        PackFileCodec packCodec,
        IDcpValidator dcpValidator,
        IDcpCounselRegister counselRegister)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _complianceGate = new FeedPublishComplianceGate(_codec, packCodec, dcpValidator, counselRegister);
    }

    /// <summary>
    /// Builds + signs the full feed tree for <paramref name="request"/>, signing every index document
    /// with <paramref name="channelRootSigner"/> (the channel root — its
    /// <see cref="IOperationSigner.IssuerId"/> is the pinned root a consumer verifies against).
    /// </summary>
    /// <exception cref="ArgumentException">A pack has no versions, or its declared <c>Latest</c> is not
    /// a live (non-yanked) version it carries, or <c>validUntil</c> is not after
    /// <c>generatedAt</c>.</exception>
    public async ValueTask<FeedTree> BuildAsync(
        FeedBuildRequest request,
        IOperationSigner channelRootSigner,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(channelRootSigner);

        var generatedAt = FeedInstants.Normalize(request.GeneratedAt);
        var validUntil = FeedInstants.Normalize(request.ValidUntil);
        if (validUntil <= generatedAt)
            throw new ArgumentException("validUntil must be after generatedAt.", nameof(request));

        var channelRootKeyId = FeedKeyId.Format(channelRootSigner.IssuerId);
        _complianceGate.ValidatePolicy(request.Policy);

        // Ordered file lists kept separate so we can assemble the F3 publish order at the end.
        var packFiles = new List<FeedFile>();
        var channelPackRefs = new List<ChannelPackRef>();

        foreach (var pack in request.Packs)
        {
            if (pack.Versions.Count == 0)
                throw new ArgumentException($"Pack '{pack.PackKey}' has no versions.", nameof(request));

            var versionEntries = new List<PerPackVersionEntry>(pack.Versions.Count);
            var latestIsLive = false;

            foreach (var version in pack.Versions)
            {
                // ADR 0153 D2/D3: before the channel root vouches for this pack, verify that its
                // publisher-signed DCP class is allowed and that v1's publisher is the channel root.
                _complianceGate.ValidatePack(request.Policy, pack.PackKey, version, channelRootSigner.IssuerId);

                // Artifact: content-address the exact PackFile bytes; the path embeds the CID.
                var artifactCid = Cid.FromBytes(version.ArtifactBytes.Span);
                var artifactPath = FeedPaths.ArtifactPath(pack.PackKey, version.Version, artifactCid);
                var artifactUrl = FeedPaths.PerPackArtifactUrl(version.Version, artifactCid);

                // Manifest: write the publisher envelope out, content-address the exact bytes.
                var manifestBytes = _codec.Encode(version.ManifestEnvelope);
                var manifestCid = Cid.FromBytes(manifestBytes);
                var manifestPath = FeedPaths.ManifestPath(pack.PackKey, version.Version);
                var manifestUrl = FeedPaths.PerPackManifestUrl(version.Version);

                packFiles.Add(new FeedFile(artifactPath, version.ArtifactBytes));
                packFiles.Add(new FeedFile(manifestPath, manifestBytes));

                versionEntries.Add(new PerPackVersionEntry(
                    Version: version.Version,
                    ManifestUrl: manifestUrl,
                    ManifestCid: manifestCid,
                    ArtifactUrl: artifactUrl,
                    ArtifactCid: artifactCid,
                    PublishedAt: FeedInstants.Normalize(version.PublishedAt),
                    MinNodeEpoch: version.MinNodeEpoch,
                    Supersedes: version.Supersedes,
                    YankedAt: version.YankedAt is { } y ? FeedInstants.Normalize(y) : null,
                    // RESERVED — v1 is always null (all-or-nothing per channel, §2.4 / UF-6).
                    RolloutBucket: null));

                if (string.Equals(version.Version, pack.Latest, StringComparison.Ordinal)
                    && version.YankedAt is null)
                {
                    latestIsLive = true;
                }
            }

            if (!latestIsLive)
            {
                throw new ArgumentException(
                    $"Pack '{pack.PackKey}' declares latest '{pack.Latest}', which is not a live " +
                    "(present, non-yanked) version it carries.",
                    nameof(request));
            }

            var perPackIndex = new PerPackIndex(
                FeedFormat: FeedFormats.V1,
                PackKey: pack.PackKey,
                ChannelRootKeyId: channelRootKeyId,
                Versions: versionEntries);

            var signedIndex = await channelRootSigner
                .SignAsync(perPackIndex, generatedAt, Guid.NewGuid(), ct)
                .ConfigureAwait(false);
            var indexBytes = _codec.Encode(signedIndex);
            var indexCid = Cid.FromBytes(indexBytes);

            packFiles.Add(new FeedFile(FeedPaths.PackIndex(pack.PackKey), indexBytes));
            channelPackRefs.Add(new ChannelPackRef(
                PackKey: pack.PackKey,
                Latest: pack.Latest,
                IndexUrl: FeedPaths.ChannelIndexUrl(pack.PackKey),
                IndexCid: indexCid));
        }

        // Revocation list — reuse the EXISTING signed revocation subject (S-11); the feed only hosts it.
        var revocationManifest = new PackRevocationManifest(
            Revoked: request.Revoked,
            IssuedAtEpochMs: FeedInstants.Normalize(request.RevocationIssuedAt).ToUnixTimeMilliseconds());
        var signedRevocations = await channelRootSigner
            .SignAsync(revocationManifest, generatedAt, Guid.NewGuid(), ct)
            .ConfigureAwait(false);
        var revocationBytes = _codec.Encode(signedRevocations);
        var revocationsCid = Cid.FromBytes(revocationBytes);

        // Feed policy — channel-root-signed, then CID-coupled into channel.json. It is emitted before
        // channel.json so a fresh index can never point at an absent/old policy (ADR 0153 D3).
        var signedPolicy = await channelRootSigner
            .SignAsync(request.Policy, generatedAt, Guid.NewGuid(), ct)
            .ConfigureAwait(false);
        var policyBytes = _codec.Encode(signedPolicy);
        var policyCid = Cid.FromBytes(policyBytes);

        // Channel index — references every CID computed above (F3: computed BEFORE this is signed).
        var channelIndex = new ChannelIndex(
            FeedFormat: FeedFormats.V1,
            Channel: request.Channel,
            ChannelRootKeyId: channelRootKeyId,
            GeneratedAt: generatedAt,
            ValidUntil: validUntil,
            Sequence: request.Sequence,
            TtlSeconds: request.TtlSeconds,
            // RESERVED (F4) — always null in feedFormat 1; the rotation-attestation seam is locked, the
            // mechanism deferred.
            RootAttestationUrl: null,
            Packs: channelPackRefs,
            RevocationsUrl: FeedPaths.RevocationsJson,
            RevocationsCid: revocationsCid,
            RevocationSequence: request.RevocationSequence,
            FeedPolicyUrl: FeedPaths.FeedPolicyJson,
            FeedPolicyCid: policyCid);

        var signedChannel = await channelRootSigner
            .SignAsync(channelIndex, generatedAt, Guid.NewGuid(), ct)
            .ConfigureAwait(false);
        var channelBytes = _codec.Encode(signedChannel);

        // F3 publish order: coupled roots FIRST, then all pack files, then channel.json LAST.
        var ordered = new List<FeedFile>(packFiles.Count + 3)
        {
            new(FeedPaths.RevocationsJson, revocationBytes),
            new(FeedPaths.FeedPolicyJson, policyBytes),
        };
        ordered.AddRange(packFiles);
        ordered.Add(new FeedFile(FeedPaths.ChannelJson, channelBytes));

        return new FeedTree(ordered, channelRootSigner.IssuerId, request.Channel);
    }
}

/// <summary>Normalizes signing instants to UTC, truncated to whole seconds — so a value round-trips
/// byte-aligned through JSON transport (the same precision discipline as the <c>PackExporter</c>
/// epoch-ms truncation), removing any sub-second/offset ambiguity from the freshness comparisons.</summary>
internal static class FeedInstants
{
    public static DateTimeOffset Normalize(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero);
    }
}
