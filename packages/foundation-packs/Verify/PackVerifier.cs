using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;

namespace Harborline.Api.Foundation.Packs.Verify;

/// <summary>
/// Default <see cref="IPackVerifier"/>. The verify-before-effect pipeline runs in a fixed order so a
/// tamper always beats an epoch nuance:
/// <list type="number">
///   <item>decode the file (undecodable ⇒ <c>VerificationFailed</c>; no envelope ⇒ <c>NotSigned</c>);</item>
///   <item>cryptographically verify the signature by RE-canonicalizing the subject — never trusting
///     the on-wire bytes (S-14);</item>
///   <item>re-hash every carried payload and match it against the SIGNED manifest's content-addresses
///     (the merkle catch — item substitution ⇒ <c>VerificationFailed</c>);</item>
///   <item>resolve the signer key + epoch against the trust store: untrusted ⇒ <c>VerificationFailed</c>;
///     recognized-but-retired-epoch ⇒ <c>EpochUnverifiable</c> (never a false fail, S-11);
///     current ⇒ <c>Verified</c>.</item>
/// </list>
/// Only <c>Verified</c> exposes the manifest + content to the caller (S-7).
/// </summary>
public sealed class PackVerifier : IPackVerifier
{
    private readonly IOperationVerifier _operationVerifier;
    private readonly PackFileCodec _codec;

    /// <summary>Constructs the verifier over the Ed25519 operation verifier + the file codec.</summary>
    public PackVerifier(IOperationVerifier operationVerifier, PackFileCodec codec)
    {
        _operationVerifier = operationVerifier ?? throw new ArgumentNullException(nameof(operationVerifier));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    /// <inheritdoc />
    public PackVerificationResult Verify(ReadOnlySpan<byte> packFileBytes, IPackTrustStore trustStore)
    {
        ArgumentNullException.ThrowIfNull(trustStore);

        // (1) Decode. Undecodable ⇒ fail-closed (not a pack we can vouch for).
        var file = _codec.TryDecode(packFileBytes);
        if (file is null)
        {
            return PackVerificationResult.Fail(PackVerificationCodes.Malformed);
        }

        // A well-formed pack with no signature envelope is NotSigned (distinct from a tamper).
        if (file.Envelope is not { } envelope)
        {
            return PackVerificationResult.Unsigned();
        }

        var keyId = envelope.IssuerId;
        var subject = envelope.Payload;
        var epoch = subject.Epoch;
        var manifest = subject.Manifest;

        // (2) Cryptographic verification. Ed25519Verifier RE-serializes the subject via CanonicalJson
        //     (S-14 — the signed form is recomputed from the deserialized object, never the on-wire
        //     bytes), so ANY mutation to a manifest field or the epoch fails here.
        if (!_operationVerifier.Verify(envelope))
        {
            return PackVerificationResult.Fail(PackVerificationCodes.SignatureInvalid, keyId, epoch);
        }

        // (3) Merkle content-address binding. Re-hash every carried payload and match it against the
        //     SIGNED manifest — a substituted inner item (valid outer signature) is caught here.
        var contentCheck = VerifyContentAddresses(manifest, file.Contents, keyId, epoch, out var verifiedItems);
        if (contentCheck is not null)
        {
            return contentCheck;
        }

        // (3b) DCP merkle-address binding (ADR 0145 D3.4). The DCP is a distinct signed leaf: re-hash the
        //      carried DCP payload and match it against the SIGNED manifest's DCP ref — a swapped DCP (valid
        //      outer signature) is caught here, exactly like a swapped content item. A legacy/pre-DCP pack
        //      (neither manifest ref nor payload) is not a tamper — presence-enforcement ("a pack MUST carry
        //      a valid DCP") is the install-side / ADR 0129-amendment concern (Phase-1 STUB), not this catch.
        var dcpCheck = VerifyDcpAddress(manifest, file.Dcp, keyId, epoch);
        if (dcpCheck is not null)
        {
            return dcpCheck;
        }

        // (3c) Intra-pack content-reference binding (ticket 357). An AssetTypeDefinition's
        //      propertyFormBinding is a content key into THIS pack's FormDefinition leaves — the one key
        //      space install publishes the form under. A binding naming a leaf the pack does not carry
        //      (a cross-pack binding, or a typo) would dangle after activation, so it is refused BY NAME
        //      here rather than silently dropped at projection; a binding under a content version older
        //      than the shape that introduced the field is likewise refused, never read.
        var bindingCheck = VerifyFormBindings(verifiedItems!, keyId, epoch);
        if (bindingCheck is not null)
        {
            return bindingCheck;
        }

        // (4) Epoch-aware trust resolution.
        var resolution = trustStore.Resolve(keyId, epoch);
        switch (resolution.Match)
        {
            case PackTrustMatch.None:
                // Signature is cryptographically valid, but no root recognizes the signer ⇒
                // fail-closed refuse (S-1). NOT an epoch nuance — the key is simply unknown.
                return PackVerificationResult.Fail(PackVerificationCodes.SignerUntrusted, keyId, epoch);

            case PackTrustMatch.RetiredOrUnverifiableEpoch:
                // Recognized signer, sealed/retired (or not-current) epoch ⇒ epoch-unverifiable,
                // NEVER a false VerificationFailed (ADR 0126 D4 / S-11).
                return PackVerificationResult.EpochUnverifiable(keyId, epoch, resolution.Scope);

            case PackTrustMatch.Current:
                return PackVerificationResult.Ok(keyId, epoch, resolution.Scope!.Value, manifest, verifiedItems!);

            default:
                // Defensive: an unmodelled match is a refuse, never a silent pass.
                return PackVerificationResult.Fail(PackVerificationCodes.SignerUntrusted, keyId, epoch);
        }
    }

    /// <summary>
    /// Verifies the merkle binding: every manifest content-ref has exactly one carried payload whose
    /// re-hashed canonical bytes reproduce the signed content-address, and no payload is orphaned.
    /// Returns a failing result on any mismatch, else <c>null</c> and the reconstructed verified items.
    /// </summary>
    private static PackVerificationResult? VerifyContentAddresses(
        PackManifest manifest,
        IReadOnlyList<PackContentPayload> payloads,
        PrincipalId keyId,
        long epoch,
        out IReadOnlyList<PackContentItem>? verifiedItems)
    {
        verifiedItems = null;
        var errors = new List<string>();

        // Index payloads by key; a duplicate key is itself a refusal (ambiguous binding).
        var payloadsByKey = new Dictionary<string, PackContentPayload>(StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            if (!payloadsByKey.TryAdd(payload.Key, payload))
            {
                errors.Add(PackVerificationCodes.ContentOrphan);
            }
        }

        var items = new List<PackContentItem>(manifest.Contents.Count);
        var boundKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reference in manifest.Contents)
        {
            if (!payloadsByKey.TryGetValue(reference.Key, out var payload))
            {
                errors.Add(PackVerificationCodes.ContentMissing);
                continue;
            }
            boundKeys.Add(reference.Key);

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload.ContentBase64);
            }
            catch (FormatException)
            {
                errors.Add(PackVerificationCodes.ContentPayloadMalformed);
                continue;
            }

            // Re-hash the payload bytes and compare to the SIGNED manifest address (the merkle catch).
            var recomputed = Cid.FromBytes(bytes);
            if (!recomputed.Equals(reference.ContentAddress))
            {
                errors.Add(PackVerificationCodes.ContentAddressMismatch);
                continue;
            }

            items.Add(new PackContentItem(reference.Key, reference.Kind, reference.Version, bytes, recomputed));
        }

        // Any carried payload the manifest does not reference is a smuggled extra ⇒ refuse.
        foreach (var payload in payloads)
        {
            if (!boundKeys.Contains(payload.Key))
            {
                errors.Add(PackVerificationCodes.ContentOrphan);
            }
        }

        if (errors.Count > 0)
        {
            // Distinct codes, order-stable, so the detail list is deterministic.
            var distinct = errors.Distinct().ToList();
            return PackVerificationResult.Fail(distinct, keyId, epoch);
        }

        verifiedItems = items;
        return null;
    }

    /// <summary>
    /// The content-shape version that introduced <c>propertyFormBinding</c> on an
    /// <c>AssetTypeDefinition</c> body — the host-side parser
    /// (<c>PackAssetTypeContent.FormBindingShapeVersion</c>) pins the same number; a leaf declaring less
    /// than this may not carry the field.
    /// </summary>
    private const string FormBindingShapeVersion = "1.1.0";

    /// <summary>
    /// Verifies that every <c>AssetTypeDefinition</c> property-form binding names a <c>FormDefinition</c>
    /// leaf of the SAME pack, under a declared content version that admits the field. Returns a failing
    /// result on the first offending leaf (codes are distinct + order-stable), else <c>null</c>.
    /// </summary>
    private static PackVerificationResult? VerifyFormBindings(
        IReadOnlyList<PackContentItem> items,
        PrincipalId keyId,
        long epoch)
    {
        var formKeys = new HashSet<string>(
            items.Where(i => i.Kind == PackContentKind.FormDefinition).Select(i => i.Key),
            StringComparer.Ordinal);
        var errors = new List<string>();

        foreach (var item in items.Where(i => i.Kind == PackContentKind.AssetTypeDefinition))
        {
            string? binding;
            try
            {
                binding = JsonNode.Parse(item.CanonicalBytes.Span) is JsonObject obj
                          && obj.TryGetPropertyValue("propertyFormBinding", out var node)
                          && node is JsonValue value
                          && value.TryGetValue<string>(out var key)
                    ? key.Trim()
                    : null;
            }
            catch (JsonException)
            {
                // An unparseable body carries no binding we can judge; the projector refuses it as
                // malformed. Verification is about the signed bytes, not the per-kind content contract.
                continue;
            }

            if (string.IsNullOrEmpty(binding))
            {
                continue;
            }

            if (Install.PackVersion.Compare(item.Version ?? string.Empty, FormBindingShapeVersion) < 0)
            {
                errors.Add(PackVerificationCodes.FormBindingSchemaUnsupported);
            }
            else if (!formKeys.Contains(binding))
            {
                errors.Add(PackVerificationCodes.FormBindingNotInPack);
            }
        }

        return errors.Count == 0
            ? null
            : PackVerificationResult.Fail(errors.Distinct().ToList(), keyId, epoch);
    }

    /// <summary>
    /// Verifies the DCP merkle binding (ADR 0145 D3.4): if the signed manifest declares a DCP leaf, the
    /// file must carry exactly one DCP payload whose re-hashed canonical bytes reproduce the signed
    /// content-address; a manifest DCP with no payload is <c>DcpMissing</c>, a payload with no manifest ref
    /// is <c>DcpOrphan</c>, an address mismatch is <c>DcpAddressMismatch</c>. Both absent = a legacy/pre-DCP
    /// pack (not a tamper). Returns a failing result on any mismatch, else <c>null</c>.
    /// </summary>
    private static PackVerificationResult? VerifyDcpAddress(
        PackManifest manifest,
        PackDcpPayload? dcpPayload,
        PrincipalId keyId,
        long epoch)
    {
        var declared = manifest.Dcp;

        if (declared is null)
        {
            // No manifest DCP ref. A carried DCP payload with no signed ref is a smuggled leaf ⇒ refuse.
            return dcpPayload is null
                ? null
                : PackVerificationResult.Fail(PackVerificationCodes.DcpOrphan, keyId, epoch);
        }

        // The manifest declares a DCP but the file carries no payload ⇒ the leaf is missing.
        if (dcpPayload is null)
        {
            return PackVerificationResult.Fail(PackVerificationCodes.DcpMissing, keyId, epoch);
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dcpPayload.ContentBase64);
        }
        catch (FormatException)
        {
            return PackVerificationResult.Fail(PackVerificationCodes.DcpAddressMismatch, keyId, epoch);
        }

        // Re-hash the DCP payload bytes and compare to the SIGNED manifest ref (the DCP merkle catch).
        var recomputed = Cid.FromBytes(bytes);
        return recomputed.Equals(declared.ContentAddress)
            ? null
            : PackVerificationResult.Fail(PackVerificationCodes.DcpAddressMismatch, keyId, epoch);
    }
}
