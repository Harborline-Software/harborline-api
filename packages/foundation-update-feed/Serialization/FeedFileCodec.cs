using System.Text.Json;
using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Crypto;

namespace Harborline.Api.Foundation.UpdateFeed.Serialization;

/// <summary>
/// Encodes/decodes the feed's signed documents (<c>channel.json</c>, per-pack <c>index.json</c>,
/// <c>revocations.json</c>, and the extracted <c>manifest.json</c> envelope) to/from their on-disk
/// bytes. Like <c>PackFileCodec</c>, this is TRANSPORT framing only — deliberately NOT the canonical
/// signing form.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the on-disk framing can be ordinary human-diffable JSON without weakening the signature:</b>
/// a <c>SignedOperation&lt;T&gt;</c> signature is computed over <c>CanonicalJson.SerializeSignable</c> of
/// the <c>{issuedAt, issuerId, nonce, payload}</c> envelope (S-14), and <c>Ed25519Verifier</c>
/// RE-canonicalizes the DESERIALIZED object at verify — never trusting these bytes. So key order,
/// indentation, camelCase, and enum-as-string here are all irrelevant to verification: any tamper is
/// caught by re-canonicalization, or (for the artifact/manifest/index/revocation coupling) by re-hashing
/// the bytes against a signed CID. This is the same discipline as <c>PackFileCodec</c>.
/// </para>
/// <para>
/// The codec is fail-closed on decode: ANY structural failure (not JSON, wrong shape, a malformed
/// key/signature/CID) returns <c>null</c> so a caller maps "not a decodable feed doc" to a refuse
/// verdict rather than leaking a parser exception across the boundary.
/// </para>
/// </remarks>
public sealed class FeedFileCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Pinned byte fixtures and content-addressed feed files must not vary by host OS.
        NewLine = "\n",
        // Enums as readable strings on disk — IRRELEVANT to the signature (the signed form is
        // CanonicalJson of the reconstructed object, reproduced identically at verify).
        Converters = { new JsonStringEnumConverter() },
        // Emit nulls for the RESERVED slots (rootAttestationUrl, rolloutBucket, supersedes, yankedAt)
        // so the on-disk shape shows the reserved fields explicitly (design-note faithful).
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serializes a signed feed document to its canonical-for-transport UTF-8 bytes.</summary>
    public byte[] Encode<T>(SignedOperation<T> document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.SerializeToUtf8Bytes(document, Options);
    }

    /// <summary>
    /// Attempts to decode a signed feed document from bytes. Returns <c>null</c> on ANY structural
    /// failure (fail-closed) so the caller maps "undecodable" to a refuse verdict.
    /// </summary>
    public SignedOperation<T>? TryDecode<T>(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<SignedOperation<T>>(bytes, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            // A malformed base64url PrincipalId/Signature or a bad CID string surfaces here via the
            // value-type converters — treat as an undecodable (hence untrusted) document.
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
