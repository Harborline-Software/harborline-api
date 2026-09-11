using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;

namespace Harborline.Api.Foundation.Packs.Verify;

/// <summary>
/// The typed verdict of verifying a pack file (design §2.4 / S-7 / S-11). Fail-closed: only
/// <see cref="Verified"/> permits a downstream reader; every other verdict withholds the content.
/// </summary>
public enum PackVerdict
{
    /// <summary>Fully verified: valid signature, content-addresses matched, signer trusted, epoch
    /// current. The only verdict that exposes the manifest + content to a reader.</summary>
    Verified = 0,

    /// <summary>The file is a well-formed pack but carries NO signature envelope. v1 never trusts an
    /// unsigned pack, but this is a distinct, honest verdict from a tamper.</summary>
    NotSigned = 1,

    /// <summary>The signature is cryptographically valid and the signer is RECOGNIZED, but under a
    /// sealed/retired (or otherwise not-current) epoch — epoch-unverifiable, NEVER a false
    /// <see cref="VerificationFailed"/> (ADR 0126 D4 / S-11).</summary>
    EpochUnverifiable = 2,

    /// <summary>Verification failed: the bytes are undecodable, the signature does not verify, an
    /// inner content-address does not match (item substitution), or the signer is untrusted. A
    /// fail-closed refuse.</summary>
    VerificationFailed = 3,
}

/// <summary>Stable, locale-independent detail codes carried alongside a verdict.</summary>
public static class PackVerificationCodes
{
    /// <summary>The bytes did not decode to a pack file.</summary>
    public const string Malformed = "pack.verify.malformed";
    /// <summary>The file carries no signature envelope.</summary>
    public const string NotSigned = "pack.verify.not_signed";
    /// <summary>The Ed25519 signature over the canonical subject did not verify (tamper).</summary>
    public const string SignatureInvalid = "pack.verify.signature_invalid";
    /// <summary>A manifest content ref has no matching carried payload.</summary>
    public const string ContentMissing = "pack.verify.content.missing";
    /// <summary>A carried payload is not referenced by the manifest (orphan / smuggled).</summary>
    public const string ContentOrphan = "pack.verify.content.orphan";
    /// <summary>A carried payload's bytes are not valid base64.</summary>
    public const string ContentPayloadMalformed = "pack.verify.content.payload_malformed";
    /// <summary>A carried payload's recomputed address does not match the signed manifest (the merkle
    /// catch — item substitution).</summary>
    public const string ContentAddressMismatch = "pack.verify.content.address_mismatch";
    /// <summary>The signed manifest declares a DCP but the file carries no DCP payload (missing leaf).</summary>
    public const string DcpMissing = "pack.verify.dcp.missing";
    /// <summary>The file carries a DCP payload the signed manifest does not reference (orphan / smuggled).</summary>
    public const string DcpOrphan = "pack.verify.dcp.orphan";
    /// <summary>The DCP payload's recomputed address does not match the signed manifest (the DCP merkle
    /// catch — a swapped Domain Compliance Profile; ADR 0145 D3.4).</summary>
    public const string DcpAddressMismatch = "pack.verify.dcp.address_mismatch";
    /// <summary>An <c>AssetTypeDefinition</c> binds a property form whose content key is NOT a
    /// <c>FormDefinition</c> leaf of the SAME pack (ticket 357). A pack-local content key cannot name another
    /// pack's leaf, so such a binding would dangle on the target node — refused by name, never dropped.</summary>
    public const string FormBindingNotInPack = "pack.verify.content.form_binding_not_in_pack";
    /// <summary>An <c>AssetTypeDefinition</c> carries <c>propertyFormBinding</c> under a declared content
    /// version older than the shape version that introduced the field (ticket 357) — the leaf claims a shape
    /// it does not have, so it is refused rather than read.</summary>
    public const string FormBindingSchemaUnsupported = "pack.verify.content.form_binding_schema_unsupported";
    /// <summary>An inspection-form map names a key that is not a <c>FormDefinition</c> leaf of this pack.</summary>
    public const string InspectionFormBindingNotInPack = "pack.verify.content.inspection_form_binding_not_in_pack";
    /// <summary>An inspection-form map appears under a content version older than its declared shape.</summary>
    public const string InspectionFormBindingSchemaUnsupported = "pack.verify.content.inspection_form_binding_schema_unsupported";
    /// <summary>The signer key is not recognized by any trust root (fail-closed refuse, S-1).</summary>
    public const string SignerUntrusted = "pack.verify.signer_untrusted";
    /// <summary>The signer is recognized but the epoch is sealed/retired (S-11).</summary>
    public const string EpochRetired = "pack.verify.epoch_unverifiable";
    /// <summary>Fully verified.</summary>
    public const string Verified = "pack.verify.verified";
}

/// <summary>
/// The result of a pack verification. The <see cref="Manifest"/> and <see cref="Contents"/> are
/// exposed ONLY when <see cref="Verdict"/> is <see cref="PackVerdict.Verified"/> — nothing reads the
/// pack's content before it is fully verified (S-7 verify-before-effect).
/// </summary>
/// <param name="Verdict">The typed verdict.</param>
/// <param name="Details">Stable detail codes (one of <see cref="PackVerificationCodes"/>).</param>
/// <param name="SignerKeyId">The signer's key-id, when the file had an envelope.</param>
/// <param name="Epoch">The claimed epoch, when the file had an envelope.</param>
/// <param name="VouchingScope">The scope that vouched (on Verified / EpochUnverifiable).</param>
/// <param name="Manifest">The verified manifest — ONLY on <see cref="PackVerdict.Verified"/>.</param>
/// <param name="Contents">The verified content items — ONLY on <see cref="PackVerdict.Verified"/>.</param>
public sealed record PackVerificationResult(
    PackVerdict Verdict,
    IReadOnlyList<string> Details,
    PrincipalId? SignerKeyId,
    long? Epoch,
    TrustScope? VouchingScope,
    PackManifest? Manifest,
    IReadOnlyList<PackContentItem>? Contents)
{
    internal static PackVerificationResult Fail(string code, PrincipalId? keyId = null, long? epoch = null)
        => new(PackVerdict.VerificationFailed, new[] { code }, keyId, epoch, null, null, null);

    internal static PackVerificationResult Fail(IReadOnlyList<string> codes, PrincipalId? keyId, long? epoch)
        => new(PackVerdict.VerificationFailed, codes, keyId, epoch, null, null, null);

    internal static PackVerificationResult Unsigned()
        => new(PackVerdict.NotSigned, new[] { PackVerificationCodes.NotSigned }, null, null, null, null, null);

    internal static PackVerificationResult EpochUnverifiable(PrincipalId keyId, long epoch, TrustScope? scope)
        => new(PackVerdict.EpochUnverifiable, new[] { PackVerificationCodes.EpochRetired }, keyId, epoch, scope, null, null);

    internal static PackVerificationResult Ok(
        PrincipalId keyId, long epoch, TrustScope scope, PackManifest manifest, IReadOnlyList<PackContentItem> contents)
        => new(PackVerdict.Verified, new[] { PackVerificationCodes.Verified }, keyId, epoch, scope, manifest, contents);
}
