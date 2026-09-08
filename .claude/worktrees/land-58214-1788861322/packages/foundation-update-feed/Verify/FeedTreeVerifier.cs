using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Foundation.UpdateFeed.Contract;
using Harborline.Api.Foundation.UpdateFeed.Serialization;

namespace Harborline.Api.Foundation.UpdateFeed.Verify;

/// <summary>
/// Verifies a whole feed tree end-to-end from a PINNED root (design note §2.3 / U1 offline verify
/// script) — the "mirror is a dumb copy" proof. It reads bytes from any <see cref="IFeedSource"/> (a
/// directory, an in-memory tree, a CDN client, a USB stick) and trusts NONE of them: every document is
/// verified by its channel-root signature, every reference by its content-address, and every artifact by
/// the existing <c>PackVerifier</c> (publisher signature + merkle binding + trust store + revocation).
/// Nothing here depends on the URL that served a byte — only on the signatures over the bytes.
/// </summary>
/// <remarks>
/// This is the SINGLE verify implementation shared by the offline verify script (U1) and — by design —
/// the node feed client (U2) and marketplace Browse (#136). It reuses the platform verify primitives
/// wholesale (<c>Ed25519Verifier</c>, <c>Cid.FromBytes</c>, <c>PackVerifier</c>,
/// <c>PackRevocationListVerifier</c>, <c>PackVersion</c>); it invents no verification of its own beyond
/// the feed-envelope fences (feedFormat refusal, monotonic sequence, signed validUntil, revocationsCid
/// coupling).
/// </remarks>
public sealed class FeedTreeVerifier
{
    private readonly FeedFileCodec _feedCodec;
    private readonly PackFileCodec _packCodec;
    private readonly Ed25519Verifier _ed25519 = new();

    /// <summary>Constructs a verifier over the feed + pack codecs.</summary>
    public FeedTreeVerifier(FeedFileCodec feedCodec, PackFileCodec packCodec)
    {
        _feedCodec = feedCodec ?? throw new ArgumentNullException(nameof(feedCodec));
        _packCodec = packCodec ?? throw new ArgumentNullException(nameof(packCodec));
    }

    /// <summary>Verifies <paramref name="source"/> against <paramref name="pinnedRoot"/> under
    /// <paramref name="options"/>. Returns a typed result (never throws for a bad feed — a malformed /
    /// tampered / stale tree is reported as findings, fail-closed).</summary>
    public FeedVerifyResult Verify(IFeedSource source, FeedPinnedRoot pinnedRoot, FeedVerifyOptions options)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pinnedRoot);
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<FeedVerifyFinding>();
        var warnings = new List<FeedVerifyFinding>();

        // ── (1) channel.json ─────────────────────────────────────────────────────────────────────
        if (!source.TryRead(FeedPaths.ChannelJson, out var channelBytes))
        {
            failures.Add(new(FeedVerifyCodes.ChannelMissing, "channel.json not found", FeedPaths.ChannelJson));
            return new FeedVerifyResult(failures, warnings, summary: null);
        }

        var signedChannel = _feedCodec.TryDecode<ChannelIndex>(channelBytes.Span);
        if (signedChannel is null)
        {
            failures.Add(new(FeedVerifyCodes.ChannelMalformed, "channel.json did not decode", FeedPaths.ChannelJson));
            return new FeedVerifyResult(failures, warnings, summary: null);
        }
        var channel = signedChannel.Payload;

        // feedFormat refusal — fail-closed, honest, never a guess at a newer shape (§2.5).
        if (!FeedFormats.IsSupported(channel.FeedFormat))
        {
            failures.Add(new(FeedVerifyCodes.UnsupportedFeedFormat,
                $"channel feedFormat {channel.FeedFormat} is not supported (this build reads {FeedFormats.V1})",
                FeedPaths.ChannelJson));
            return new FeedVerifyResult(failures, warnings, summary: null);
        }

        // Reserved F4 slot MUST be null in feedFormat 1 (the seam is locked, the mechanism deferred).
        if (channel.RootAttestationUrl is not null)
        {
            failures.Add(new(FeedVerifyCodes.ReservedFieldPopulated,
                "rootAttestationUrl is reserved and must be null in feedFormat 1", FeedPaths.ChannelJson));
        }

        // Channel-root signature — the freshness + latest-pointer + revocation authority (§2.3 layer b).
        if (!_ed25519.Verify(signedChannel))
        {
            failures.Add(new(FeedVerifyCodes.ChannelSignatureInvalid,
                "channel.json channel-root signature did not verify", FeedPaths.ChannelJson));
        }
        if (!signedChannel.IssuerId.Equals(pinnedRoot.KeyId))
        {
            failures.Add(new(FeedVerifyCodes.ChannelRootMismatch,
                "channel.json is not signed by the pinned channel root", FeedPaths.ChannelJson));
        }
        if (!FeedKeyId.Matches(channel.ChannelRootKeyId, signedChannel.IssuerId))
        {
            failures.Add(new(FeedVerifyCodes.ChannelRootIdInconsistent,
                "channel.json channelRootKeyId does not match its signer", FeedPaths.ChannelJson));
        }

        // Anti-rollback: refuse a sequence below the consumer's floor / high-water (F2 / §7.2).
        if (options.MinSequence is { } floor && channel.Sequence < floor)
        {
            failures.Add(new(FeedVerifyCodes.SequenceRollback,
                $"channel sequence {channel.Sequence} is below the required floor {floor} (rollback)",
                FeedPaths.ChannelJson));
        }

        // Freshness (F1): channel-typed posture past the SIGNED validUntil.
        if (options.Now > channel.ValidUntil)
        {
            if (options.ChannelKind == ChannelKind.Online)
            {
                failures.Add(new(FeedVerifyCodes.ChannelExpired,
                    $"online channel is past validUntil {channel.ValidUntil:O} at {options.Now:O} " +
                    "(fail-closed for the install path)", FeedPaths.ChannelJson));
            }
            else
            {
                warnings.Add(new(FeedVerifyCodes.ChannelExpiredSideloadWarning,
                    $"sideload channel is past validUntil {channel.ValidUntil:O} (expected-stale; not locked out)",
                    FeedPaths.ChannelJson));
            }
        }

        // The pinned root, expressed as a scope-carrying trust root, for artifact + revocation resolution.
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(TrustScope.HarborlineChannel, pinnedRoot.KeyId, pinnedRoot.PublisherEpoch,
                TrustRootStatus.Current),
        });

        // ── (2) feed-policy.json — D3 coupling (fail-closed on cid mismatch) ─────────────────────
        var policy = VerifyFeedPolicy(source, channel, pinnedRoot, failures);

        // ── (3) revocations.json — F3 coupling (fail-closed on cid mismatch) ─────────────────────
        var revocationList = VerifyRevocations(
            source, channel, pinnedRoot, trustStore, options, failures, warnings, out var revokedCount);

        // ── (4) per-pack chains ───────────────────────────────────────────────────────────────────
        var verifiedPacks = new List<FeedVerifiedPack>();
        foreach (var packRef in channel.Packs)
        {
            VerifyPack(source, packRef, pinnedRoot, trustStore, revocationList, policy, failures, verifiedPacks);
        }

        var summary = new FeedVerifySummary(
            Channel: channel.Channel,
            Sequence: channel.Sequence,
            GeneratedAt: channel.GeneratedAt,
            ValidUntil: channel.ValidUntil,
            RevocationsCid: channel.RevocationsCid,
            FeedPolicyCid: channel.FeedPolicyCid,
            RevokedCount: revokedCount,
            Packs: verifiedPacks);

        return new FeedVerifyResult(failures, warnings, summary);
    }

    private FeedPolicy? VerifyFeedPolicy(
        IFeedSource source,
        ChannelIndex channel,
        FeedPinnedRoot pinnedRoot,
        List<FeedVerifyFinding> failures)
    {
        if (string.IsNullOrWhiteSpace(channel.FeedPolicyUrl)
            || string.IsNullOrWhiteSpace(channel.FeedPolicyCid.Value))
        {
            failures.Add(new(FeedVerifyCodes.FeedPolicyMissing,
                "channel index carries no index-coupled feed-policy reference", FeedPaths.ChannelJson));
            return null;
        }
        if (!TryResolve(FeedPaths.DirectoryOf(FeedPaths.ChannelJson), channel.FeedPolicyUrl,
                out var policyPath, failures, FeedVerifyCodes.FeedPolicyMissing))
        {
            return null;
        }
        if (!source.TryRead(policyPath, out var policyBytes))
        {
            failures.Add(new(FeedVerifyCodes.FeedPolicyMissing, "feed-policy.json not found", policyPath));
            return null;
        }
        if (!Cid.FromBytes(policyBytes.Span).Equals(channel.FeedPolicyCid))
        {
            failures.Add(new(FeedVerifyCodes.FeedPolicyCidMismatch,
                "feed-policy.json content-address does not match the signed channel index", policyPath));
            return null;
        }

        var signedPolicy = _feedCodec.TryDecode<FeedPolicy>(policyBytes.Span);
        if (signedPolicy is null)
        {
            failures.Add(new(FeedVerifyCodes.FeedPolicyMalformed, "feed-policy.json did not decode", policyPath));
            return null;
        }
        if (!_ed25519.Verify(signedPolicy))
        {
            failures.Add(new(FeedVerifyCodes.FeedPolicySignatureInvalid,
                "feed-policy.json channel-root signature did not verify", policyPath));
            return null;
        }
        if (!signedPolicy.IssuerId.Equals(pinnedRoot.KeyId))
        {
            failures.Add(new(FeedVerifyCodes.FeedPolicyRootMismatch,
                "feed-policy.json is not signed by the pinned channel root", policyPath));
            return null;
        }

        var policy = signedPolicy.Payload;
        if (string.IsNullOrWhiteSpace(policy.CounselRegisterRef)
            || policy.AllowedRegulatoryClasses is not { Count: > 0 }
            || policy.AllowedRegulatoryClasses.Any(value => !Enum.IsDefined(value))
            || policy.AllowedRegulatoryClasses.Distinct().Count() != policy.AllowedRegulatoryClasses.Count)
        {
            failures.Add(new(FeedVerifyCodes.FeedPolicyInvalid,
                "feed-policy.json carries an invalid class allowlist or counsel-register reference", policyPath));
            return null;
        }
        return policy;
    }

    private IPackRevocationList VerifyRevocations(
        IFeedSource source,
        ChannelIndex channel,
        FeedPinnedRoot pinnedRoot,
        IPackTrustStore trustStore,
        FeedVerifyOptions options,
        List<FeedVerifyFinding> failures,
        List<FeedVerifyFinding> warnings,
        out int revokedCount)
    {
        revokedCount = 0;
        if (!TryResolve(FeedPaths.DirectoryOf(FeedPaths.ChannelJson), channel.RevocationsUrl,
                out var revPath, failures, FeedVerifyCodes.RevocationsMissing))
        {
            return PackRevocationList.Empty;
        }

        if (!source.TryRead(revPath, out var revBytes))
        {
            failures.Add(new(FeedVerifyCodes.RevocationsMissing, "revocations.json not found", revPath));
            return PackRevocationList.Empty;
        }

        // F3 coupling: the index VOUCHES for the EXACT revocation list — a cid mismatch is fail-closed.
        if (!Cid.FromBytes(revBytes.Span).Equals(channel.RevocationsCid))
        {
            failures.Add(new(FeedVerifyCodes.RevocationsCidMismatch,
                "revocations.json content-address does not match the channel index's revocationsCid " +
                "(channel temporarily inconsistent / stale — refuse to advance)", revPath));
            return PackRevocationList.Empty;
        }

        var signedRev = _feedCodec.TryDecode<PackRevocationManifest>(revBytes.Span);
        if (signedRev is null)
        {
            failures.Add(new(FeedVerifyCodes.RevocationMalformed, "revocations.json did not decode", revPath));
            return PackRevocationList.Empty;
        }
        if (!_ed25519.Verify(signedRev))
        {
            failures.Add(new(FeedVerifyCodes.RevocationSignatureInvalid,
                "revocations.json channel-root signature did not verify", revPath));
            return PackRevocationList.Empty;
        }
        if (!signedRev.IssuerId.Equals(pinnedRoot.KeyId))
        {
            failures.Add(new(FeedVerifyCodes.RevocationRootMismatch,
                "revocations.json is not signed by the pinned channel root", revPath));
            return PackRevocationList.Empty;
        }

        // Materialize via the EXISTING S-11 verifier (reuse — the feed only hosts the list).
        var list = PackRevocationListVerifier.Verify(signedRev, trustStore, _ed25519);
        revokedCount = signedRev.Payload.Revoked.Count;

        if (options.RevocationMaxAge is { } maxAge && list.IsStale(options.Now, maxAge))
        {
            warnings.Add(new(FeedVerifyCodes.RevocationListStaleWarning,
                "revocation list is stale (offline-tolerant — surfaced, not blocking)", revPath));
        }

        return list;
    }

    private void VerifyPack(
        IFeedSource source,
        ChannelPackRef packRef,
        FeedPinnedRoot pinnedRoot,
        IPackTrustStore trustStore,
        IPackRevocationList revocationList,
        FeedPolicy? policy,
        List<FeedVerifyFinding> failures,
        List<FeedVerifiedPack> verifiedPacks)
    {
        // (3a) per-pack index — feed-base-relative, cid-coupled to the channel index.
        if (!TryResolve(FeedPaths.DirectoryOf(FeedPaths.ChannelJson), packRef.IndexUrl,
                out var indexPath, failures, FeedVerifyCodes.IndexMissing))
        {
            return;
        }
        if (!source.TryRead(indexPath, out var indexBytes))
        {
            failures.Add(new(FeedVerifyCodes.IndexMissing, $"index not found for pack '{packRef.PackKey}'", indexPath));
            return;
        }
        if (!Cid.FromBytes(indexBytes.Span).Equals(packRef.IndexCid))
        {
            failures.Add(new(FeedVerifyCodes.IndexCidMismatch,
                $"per-pack index content-address does not match the channel index's indexCid " +
                $"for pack '{packRef.PackKey}'", indexPath));
            return;
        }
        var signedIndex = _feedCodec.TryDecode<PerPackIndex>(indexBytes.Span);
        if (signedIndex is null)
        {
            failures.Add(new(FeedVerifyCodes.IndexMalformed, $"index did not decode for '{packRef.PackKey}'", indexPath));
            return;
        }
        var index = signedIndex.Payload;
        if (!FeedFormats.IsSupported(index.FeedFormat))
        {
            failures.Add(new(FeedVerifyCodes.IndexFeedFormatUnsupported,
                $"per-pack index feedFormat {index.FeedFormat} is not supported", indexPath));
            return;
        }
        if (!_ed25519.Verify(signedIndex))
        {
            failures.Add(new(FeedVerifyCodes.IndexSignatureInvalid,
                $"per-pack index signature did not verify for '{packRef.PackKey}'", indexPath));
            return;
        }
        if (!signedIndex.IssuerId.Equals(pinnedRoot.KeyId))
        {
            failures.Add(new(FeedVerifyCodes.IndexRootMismatch,
                $"per-pack index is not signed by the pinned channel root for '{packRef.PackKey}'", indexPath));
            return;
        }
        if (!FeedKeyId.Matches(index.ChannelRootKeyId, signedIndex.IssuerId))
        {
            failures.Add(new(FeedVerifyCodes.IndexRootIdInconsistent,
                $"per-pack index channelRootKeyId does not match its signer for '{packRef.PackKey}'", indexPath));
        }
        if (!string.Equals(index.PackKey, packRef.PackKey, StringComparison.Ordinal))
        {
            failures.Add(new(FeedVerifyCodes.IndexPackKeyMismatch,
                $"per-pack index packKey '{index.PackKey}' does not match channel ref '{packRef.PackKey}'",
                indexPath));
            return;
        }

        // (3b) resolve the channel's declared latest — must be present and not yanked.
        var entry = index.Versions.FirstOrDefault(
            v => string.Equals(v.Version, packRef.Latest, StringComparison.Ordinal) && v.YankedAt is null);
        if (entry is null)
        {
            failures.Add(new(FeedVerifyCodes.LatestMissing,
                $"channel declares latest '{packRef.Latest}' for '{packRef.PackKey}', which is absent or yanked",
                indexPath));
            return;
        }

        var indexDir = FeedPaths.DirectoryOf(indexPath);

        // (3c) manifest.json — content-addressed, publisher-signed.
        if (!TryResolve(indexDir, entry.ManifestUrl, out var manifestPath, failures, FeedVerifyCodes.ManifestMissing))
        {
            return;
        }
        if (!source.TryRead(manifestPath, out var manifestBytes))
        {
            failures.Add(new(FeedVerifyCodes.ManifestMissing, "manifest.json not found", manifestPath));
            return;
        }
        if (!Cid.FromBytes(manifestBytes.Span).Equals(entry.ManifestCid))
        {
            failures.Add(new(FeedVerifyCodes.ManifestCidMismatch,
                "manifest.json content-address does not match the index's manifestCid", manifestPath));
            return;
        }
        var signedManifest = _feedCodec.TryDecode<PackSignatureSubject>(manifestBytes.Span);
        if (signedManifest is null)
        {
            failures.Add(new(FeedVerifyCodes.ManifestMalformed, "manifest.json did not decode", manifestPath));
            return;
        }
        if (!_ed25519.Verify(signedManifest))
        {
            failures.Add(new(FeedVerifyCodes.ManifestSignatureInvalid,
                "manifest.json publisher signature did not verify", manifestPath));
            return;
        }
        if (!signedManifest.IssuerId.Equals(pinnedRoot.KeyId))
        {
            failures.Add(new(FeedVerifyCodes.ManifestPublisherRootMismatch,
                "manifest publisher is not the pinned channel root (ADR 0153 D2 v1)", manifestPath));
            return;
        }

        var manifestDcp = signedManifest.Payload.Manifest.Dcp;
        if (manifestDcp is null)
        {
            failures.Add(new(FeedVerifyCodes.ManifestDcpMissing,
                "publisher-signed manifest carries no DCP classification", manifestPath));
            return;
        }
        if (policy is not null && !policy.AllowedRegulatoryClasses.Contains(manifestDcp.RegulatoryClass))
        {
            failures.Add(new(FeedVerifyCodes.RegulatoryClassNotAllowed,
                $"publisher-signed regulatory class '{manifestDcp.RegulatoryClass}' is not allowed by the feed policy",
                manifestPath));
            return;
        }

        // (3d) artifact.<cid>.pack — filename embeds the cid; bytes re-hash to it.
        var expectedArtifactFile = FeedPaths.ArtifactFileName(entry.ArtifactCid);
        if (!entry.ArtifactUrl.Equals(expectedArtifactFile, StringComparison.Ordinal) &&
            !entry.ArtifactUrl.EndsWith("/" + expectedArtifactFile, StringComparison.Ordinal))
        {
            failures.Add(new(FeedVerifyCodes.ArtifactFileNameMismatch,
                $"artifactUrl '{entry.ArtifactUrl}' does not embed the declared artifactCid", indexPath));
        }
        if (!TryResolve(indexDir, entry.ArtifactUrl, out var artifactPath, failures, FeedVerifyCodes.ArtifactMissing))
        {
            return;
        }
        if (!source.TryRead(artifactPath, out var artifactBytes))
        {
            failures.Add(new(FeedVerifyCodes.ArtifactMissing, "artifact blob not found", artifactPath));
            return;
        }
        if (!Cid.FromBytes(artifactBytes.Span).Equals(entry.ArtifactCid))
        {
            failures.Add(new(FeedVerifyCodes.ArtifactCidMismatch,
                "artifact content-address does not match the index's artifactCid", artifactPath));
            return;
        }

        // (3e) the EXISTING pack verify — publisher signature + merkle binding + trust store (S-7/S-14).
        var packResult = new PackVerifier(_ed25519, _packCodec).Verify(artifactBytes.Span, trustStore);
        if (packResult.Verdict != PackVerdict.Verified)
        {
            failures.Add(new(FeedVerifyCodes.PackVerificationFailed,
                $"pack verify verdict {packResult.Verdict} ({string.Join(",", packResult.Details)})", artifactPath));
            return;
        }

        // (3f) consistency: the extracted manifest.json envelope must be the SAME publisher + version as
        //      the artifact's own envelope (the manifest.json is that envelope, extracted).
        if (packResult.SignerKeyId is not { } packSigner || !signedManifest.IssuerId.Equals(packSigner) ||
            !string.Equals(packResult.Manifest?.Version, entry.Version, StringComparison.Ordinal))
        {
            failures.Add(new(FeedVerifyCodes.ManifestArtifactMismatch,
                "manifest.json does not match the artifact's own signed envelope (signer/version)", manifestPath));
            return;
        }

        // (3g) revocation check (S-11): the pack's signer + epoch must not be on the revocation list.
        if (packResult.SignerKeyId is { } signer && packResult.Epoch is { } epoch &&
            revocationList.IsRevoked(signer, epoch))
        {
            failures.Add(new(FeedVerifyCodes.PackRevoked,
                $"pack signer/epoch is on the revocation list for '{packRef.PackKey}'", artifactPath));
            return;
        }

        verifiedPacks.Add(new FeedVerifiedPack(
            packRef.PackKey, packRef.Latest, entry.ArtifactCid, entry.MinNodeEpoch));
    }

    /// <summary>Resolves a document-relative URL, mapping a malformed/hostile path to a fail-closed
    /// finding instead of an exception.</summary>
    private static bool TryResolve(
        string baseDir, string relativeUrl, out string resolved,
        List<FeedVerifyFinding> failures, string missingCode)
    {
        try
        {
            resolved = FeedPaths.ResolveRelative(baseDir, relativeUrl);
            return true;
        }
        catch (FormatException ex)
        {
            resolved = string.Empty;
            failures.Add(new(missingCode, $"malformed feed URL '{relativeUrl}': {ex.Message}", null));
            return false;
        }
    }
}
