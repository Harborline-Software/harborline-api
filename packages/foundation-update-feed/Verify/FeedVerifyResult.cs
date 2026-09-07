using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.Foundation.UpdateFeed.Verify;

/// <summary>
/// Stable, localizable finding codes for feed verification. A consumer localizes off the code, never
/// the English detail (the fleet localizable-code discipline). Grouped by the stage that raises them.
/// </summary>
public static class FeedVerifyCodes
{
    // ── channel index ───────────────────────────────────────────────────────────────────────────
    public const string ChannelMissing = "feed.channel.missing";
    public const string ChannelMalformed = "feed.channel.malformed";
    public const string UnsupportedFeedFormat = "feed.format.unsupported";
    public const string ChannelSignatureInvalid = "feed.channel.signature-invalid";
    public const string ChannelRootMismatch = "feed.channel.root-mismatch";
    public const string ChannelRootIdInconsistent = "feed.channel.root-id-inconsistent";
    public const string ReservedFieldPopulated = "feed.reserved-field-populated";
    public const string SequenceRollback = "feed.channel.sequence-rollback";
    public const string ChannelExpired = "feed.channel.expired";

    // ── index-coupled feed policy ───────────────────────────────────────────────────────────────
    public const string FeedPolicyMissing = "feed.policy.missing";
    public const string FeedPolicyCidMismatch = "feed.policy.cid-mismatch";
    public const string FeedPolicyMalformed = "feed.policy.malformed";
    public const string FeedPolicySignatureInvalid = "feed.policy.signature-invalid";
    public const string FeedPolicyRootMismatch = "feed.policy.root-mismatch";
    public const string FeedPolicyInvalid = "feed.policy.invalid";

    // ── revocation list ─────────────────────────────────────────────────────────────────────────
    public const string RevocationsMissing = "feed.revocations.missing";
    public const string RevocationsCidMismatch = "feed.revocations.cid-mismatch";
    public const string RevocationMalformed = "feed.revocations.malformed";
    public const string RevocationSignatureInvalid = "feed.revocations.signature-invalid";
    public const string RevocationRootMismatch = "feed.revocations.root-mismatch";

    // ── per-pack index ──────────────────────────────────────────────────────────────────────────
    public const string IndexMissing = "feed.index.missing";
    public const string IndexCidMismatch = "feed.index.cid-mismatch";
    public const string IndexMalformed = "feed.index.malformed";
    public const string IndexFeedFormatUnsupported = "feed.index.format-unsupported";
    public const string IndexSignatureInvalid = "feed.index.signature-invalid";
    public const string IndexRootMismatch = "feed.index.root-mismatch";
    public const string IndexRootIdInconsistent = "feed.index.root-id-inconsistent";
    public const string IndexPackKeyMismatch = "feed.index.pack-key-mismatch";
    public const string LatestMissing = "feed.index.latest-missing";

    // ── manifest + artifact (content-addressed, publisher-signed) ─────────────────────────────────
    public const string ManifestMissing = "feed.manifest.missing";
    public const string ManifestCidMismatch = "feed.manifest.cid-mismatch";
    public const string ManifestMalformed = "feed.manifest.malformed";
    public const string ManifestSignatureInvalid = "feed.manifest.signature-invalid";
    public const string ManifestPublisherRootMismatch = "feed.manifest.publisher-root-mismatch";
    public const string ManifestDcpMissing = "feed.manifest.dcp-missing";
    public const string RegulatoryClassNotAllowed = "feed.manifest.regulatory-class-not-allowed";
    public const string ManifestArtifactMismatch = "feed.manifest.artifact-mismatch";
    public const string ArtifactMissing = "feed.artifact.missing";
    public const string ArtifactCidMismatch = "feed.artifact.cid-mismatch";
    public const string ArtifactFileNameMismatch = "feed.artifact.filename-mismatch";
    public const string PackVerificationFailed = "feed.artifact.pack-verification-failed";
    public const string PackRevoked = "feed.artifact.pack-revoked";

    // ── warnings (non-fatal) ──────────────────────────────────────────────────────────────────────
    public const string ChannelExpiredSideloadWarning = "feed.channel.expired-sideload";
    public const string RevocationListStaleWarning = "feed.revocations.stale";
}

/// <summary>One verification finding: a stable <see cref="Code"/>, a human <see cref="Detail"/> (for
/// logs / the CLI, never localized off), and the feed path it concerns (when applicable).</summary>
/// <param name="Code">A stable <see cref="FeedVerifyCodes"/> value.</param>
/// <param name="Detail">Human-readable diagnostic detail.</param>
/// <param name="Path">The feed-base-relative path this finding concerns, or <c>null</c>.</param>
public sealed record FeedVerifyFinding(string Code, string Detail, string? Path = null);

/// <summary>What the verifier resolved for one pack on success.</summary>
/// <param name="PackKey">The pack key.</param>
/// <param name="Latest">The channel's declared-current version.</param>
/// <param name="ArtifactCid">The verified artifact's content-address.</param>
/// <param name="MinNodeEpoch">The schema-epoch floor the latest version requires (§6.4).</param>
public sealed record FeedVerifiedPack(string PackKey, string Latest, Cid ArtifactCid, long MinNodeEpoch);

/// <summary>The channel-level facts the verifier confirmed on success (for the CLI / a node's UX).</summary>
/// <param name="Channel">The channel identifier.</param>
/// <param name="Sequence">The channel index's monotonic sequence.</param>
/// <param name="GeneratedAt">Issuance time.</param>
/// <param name="ValidUntil">The signed freshness deadline (F1).</param>
/// <param name="RevocationsCid">The coupled revocation list's content-address (F3).</param>
/// <param name="FeedPolicyCid">The coupled feed policy's content-address (ADR 0153 D3).</param>
/// <param name="RevokedCount">How many <c>{key, epoch}</c> pairs the revocation list carries.</param>
/// <param name="Packs">The packs verified.</param>
public sealed record FeedVerifySummary(
    string Channel,
    long Sequence,
    DateTimeOffset GeneratedAt,
    DateTimeOffset ValidUntil,
    Cid RevocationsCid,
    Cid FeedPolicyCid,
    int RevokedCount,
    IReadOnlyList<FeedVerifiedPack> Packs);

/// <summary>
/// The outcome of an end-to-end feed verification from a pinned root. <see cref="Ok"/> is true only when
/// <see cref="Failures"/> is empty; <see cref="Warnings"/> may be non-empty on success (e.g. a
/// sideload channel past its deadline, which is expected-stale and does not lock out — F1).
/// </summary>
public sealed class FeedVerifyResult
{
    /// <summary>True iff nothing failed (warnings are allowed).</summary>
    public bool Ok => Failures.Count == 0;

    /// <summary>The fatal findings (empty ⇒ verified).</summary>
    public IReadOnlyList<FeedVerifyFinding> Failures { get; }

    /// <summary>Non-fatal findings surfaced honestly (staleness on a sideload / offline channel).</summary>
    public IReadOnlyList<FeedVerifyFinding> Warnings { get; }

    /// <summary>The confirmed channel-level facts, or <c>null</c> if verification could not get far
    /// enough to summarize (e.g. the channel index was missing/malformed).</summary>
    public FeedVerifySummary? Summary { get; }

    /// <summary>Constructs a result.</summary>
    public FeedVerifyResult(
        IReadOnlyList<FeedVerifyFinding> failures,
        IReadOnlyList<FeedVerifyFinding> warnings,
        FeedVerifySummary? summary)
    {
        Failures = failures ?? throw new ArgumentNullException(nameof(failures));
        Warnings = warnings ?? throw new ArgumentNullException(nameof(warnings));
        Summary = summary;
    }
}
