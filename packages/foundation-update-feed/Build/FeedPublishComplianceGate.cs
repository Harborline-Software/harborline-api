using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.UpdateFeed.Contract;
using Harborline.Api.Foundation.UpdateFeed.Serialization;

namespace Harborline.Api.Foundation.UpdateFeed.Build;

/// <summary>Stable refusal codes for the ADR 0153 publish-ceremony gate.</summary>
public static class FeedPublishCodes
{
    public const string PolicyInvalid = "feed.publish.policy.invalid";
    public const string ManifestInvalid = "feed.publish.manifest.invalid";
    public const string PublisherRootMismatch = "feed.publish.publisher.root-mismatch";
    public const string DcpInvalid = "feed.publish.dcp.invalid";
    public const string RegulatoryClassNotAllowed = "feed.publish.regulatory-class.not-allowed";
}

/// <summary>A typed, fail-closed refusal raised before a channel index can be signed.</summary>
public sealed class FeedPublishRefusedException : InvalidOperationException
{
    public string Code { get; }

    public FeedPublishRefusedException(string code, string message) : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// Validates the per-feed policy and each already-signed pack before the channel root vouches for it.
/// The classification source is the publisher-signed manifest DCP ref, cross-checked against the
/// merkle-bound DCP payload. No feed-delivered key is added to a trust store.
/// </summary>
internal sealed class FeedPublishComplianceGate
{
    private readonly FeedFileCodec _feedCodec;
    private readonly PackFileCodec _packCodec;
    private readonly IDcpValidator _governedDcpValidator;
    private readonly IDcpCounselRegister _governedCounselRegister;
    private readonly FeedCounselRegisterResolver _counselRegisterResolver;
    private readonly Ed25519Verifier _verifier = new();

    public FeedPublishComplianceGate(
        FeedFileCodec feedCodec,
        PackFileCodec packCodec,
        IDcpValidator dcpValidator,
        IDcpCounselRegister counselRegister)
    {
        _feedCodec = feedCodec ?? throw new ArgumentNullException(nameof(feedCodec));
        _packCodec = packCodec ?? throw new ArgumentNullException(nameof(packCodec));
        _governedDcpValidator = dcpValidator ?? throw new ArgumentNullException(nameof(dcpValidator));
        _governedCounselRegister = counselRegister ?? throw new ArgumentNullException(nameof(counselRegister));
        _counselRegisterResolver = new FeedCounselRegisterResolver(_governedCounselRegister);
    }

    public void ValidatePolicy(FeedPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (string.IsNullOrWhiteSpace(policy.CounselRegisterRef)
            || policy.AllowedRegulatoryClasses is not { Count: > 0 }
            || policy.AllowedRegulatoryClasses.Any(value => !Enum.IsDefined(value))
            || policy.AllowedRegulatoryClasses.Distinct().Count() != policy.AllowedRegulatoryClasses.Count)
        {
            Refuse(FeedPublishCodes.PolicyInvalid,
                "feed policy requires a counsel-register reference and a non-empty, defined, unique class allowlist");
        }

        if (!policy.SelfGoverned)
        {
            var uncleared = policy.AllowedRegulatoryClasses
                .Where(value => !_governedCounselRegister.IsCleared(value))
                .Cast<RegulatoryClass?>()
                .FirstOrDefault();
            if (uncleared is { } value)
            {
                Refuse(FeedPublishCodes.PolicyInvalid,
                    $"feed policy allows '{value}', which has no cleared counsel row");
            }
        }
    }

    public void ValidatePack(
        FeedPolicy policy,
        string packKey,
        FeedPackVersionInput version,
        PrincipalId channelRoot)
    {
        var envelope = version.ManifestEnvelope;
        if (!envelope.IssuerId.Equals(channelRoot))
        {
            Refuse(FeedPublishCodes.PublisherRootMismatch,
                $"pack '{packKey}' v{version.Version} is signed by an issuer other than the channel root");
        }
        if (!_verifier.Verify(envelope))
        {
            Refuse(FeedPublishCodes.ManifestInvalid,
                $"pack '{packKey}' v{version.Version} manifest signature is invalid");
        }

        var file = _packCodec.TryDecode(version.ArtifactBytes.Span);
        if (file?.Envelope is null
            || !_feedCodec.Encode(file.Envelope).AsSpan().SequenceEqual(_feedCodec.Encode(envelope)))
        {
            Refuse(FeedPublishCodes.ManifestInvalid,
                $"pack '{packKey}' v{version.Version} artifact does not carry the supplied signed manifest");
        }

        var manifestDcp = envelope.Payload.Manifest.Dcp;
        if (manifestDcp is null || file.Dcp is null)
        {
            Refuse(FeedPublishCodes.DcpInvalid,
                $"pack '{packKey}' v{version.Version} has no signed DCP leaf");
        }

        byte[] dcpBytes;
        DomainComplianceProfile? dcp;
        try
        {
            dcpBytes = Convert.FromBase64String(file.Dcp.ContentBase64);
            dcp = JsonSerializer.Deserialize<DomainComplianceProfile>(dcpBytes);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NotSupportedException)
        {
            Refuse(FeedPublishCodes.DcpInvalid,
                $"pack '{packKey}' v{version.Version} DCP payload is malformed");
            return;
        }

        if (dcp is null
            || !Cid.FromBytes(dcpBytes).Equals(manifestDcp.ContentAddress)
            || dcp.RegulatoryClass != manifestDcp.RegulatoryClass
            || !string.Equals(dcp.DcpVersion, manifestDcp.DcpVersion, StringComparison.Ordinal))
        {
            Refuse(FeedPublishCodes.DcpInvalid,
                $"pack '{packKey}' v{version.Version} DCP payload does not match its signed manifest ref");
        }

        // Test policy membership before resolving the self-governed register. A class outside the signed
        // allowlist is a policy refusal, never misreported as a DCP/counsel failure.
        if (!policy.AllowedRegulatoryClasses.Contains(manifestDcp.RegulatoryClass))
        {
            Refuse(FeedPublishCodes.RegulatoryClassNotAllowed,
                $"pack '{packKey}' v{version.Version} class '{manifestDcp.RegulatoryClass}' is not allowed by the feed policy");
        }

        var counselRegister = _counselRegisterResolver.Resolve(policy);
        var dcpFindings = _governedDcpValidator.Validate(dcp);
        if (policy.SelfGoverned)
        {
            // Keep every schema/governance finding from the injected validator. Only the ordinary
            // register's class-clearance finding is replaced by the signed feed-local register.
            dcpFindings = dcpFindings
                .Where(finding => finding.Code != DcpValidationCodes.RegulatoryClassNotCleared)
                .ToList();
        }
        if (dcpFindings.Count > 0 || !counselRegister.IsCleared(dcp.RegulatoryClass))
        {
            Refuse(FeedPublishCodes.DcpInvalid,
                $"pack '{packKey}' v{version.Version} DCP failed validation: " +
                string.Join(",", dcpFindings.Select(finding => finding.Code)));
        }
    }

    [DoesNotReturn]
    private static void Refuse(string code, string message) => throw new FeedPublishRefusedException(code, message);
}
