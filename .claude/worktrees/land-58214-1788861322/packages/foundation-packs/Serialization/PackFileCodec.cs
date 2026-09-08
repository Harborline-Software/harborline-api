using System.Text.Json;
using System.Text.Json.Serialization;

using Harborline.Api.Foundation.Packs.Model;

namespace Harborline.Api.Foundation.Packs.Serialization;

/// <summary>
/// Encodes/decodes a <see cref="PackFile"/> to/from the single-file byte stream. This is TRANSPORT
/// framing only — it is deliberately NOT the canonical signing form. The signature is computed over
/// <c>CanonicalJson</c> of the signed subject (S-14), and verification RE-canonicalizes the decoded
/// object, so the on-disk framing here can be ordinary (human-diffable) JSON without weakening the
/// signature: any tamper is caught by re-canonicalization or by re-hashing the content payloads, not
/// by trusting these bytes.
/// </summary>
public sealed class PackFileCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // Pinned byte fixtures and content-addressed artifacts must not vary by host OS.
        NewLine = "\n",
        // Enums as readable strings on disk. Note: this is IRRELEVANT to the signature — the signed
        // form is CanonicalJson of the reconstructed OBJECT (enums as numbers there), reproduced
        // identically at verify. On-disk representation choices never affect verification.
        Converters = { new JsonStringEnumConverter() },
        // Ticket 199 slice 4: a pack with ANY duplicate property is not decodable. Last-wins
        // decoding would let a transport encoding carry two manifests (or two keys) where the
        // pre-gate coordinate reader and the verifier could disagree about which pack this is;
        // refusing duplicates makes "the claimed coordinates" a single well-defined thing.
        AllowDuplicateProperties = false,
    };

    /// <summary>Serializes a pack file to its canonical-for-transport UTF-8 byte form.</summary>
    public byte[] Encode(PackFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return JsonSerializer.SerializeToUtf8Bytes(file, Options);
    }

    /// <summary>
    /// Attempts to decode a pack file from bytes. Returns <c>null</c> on ANY structural failure
    /// (not JSON, wrong shape, a malformed key/signature/CID) so the caller can map "not a decodable
    /// pack" to a fail-closed verdict rather than leaking a parser exception across the boundary.
    /// </summary>
    public PackFile? TryDecode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            // Ticket 150 (L1145): every content item must DECLARE a kind this node knows. An absent
            // "kind" would otherwise deserialize to PackContentKind.FormDefinition (0), and an
            // undefined NUMBER survives JsonStringEnumConverter untouched — both install the item
            // typed as something it is not. platform-package-v1.md §4 refuses an unknown restricting
            // kind outright, because "a weaker floor arrived at silently is worse than a refused
            // install". An unknown kind STRING already threw its way to null; these two did not.
            if (!ContentKindsAreDeclaredAndKnown(bytes))
            {
                return null;
            }

            return JsonSerializer.Deserialize<PackFile>(bytes, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            // A malformed base64url PrincipalId/Signature or a bad CID string surfaces here via the
            // value-type converters — treat as an undecodable (hence untrusted) pack.
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when every <c>contents[]</c> item declares a <c>kind</c> that maps to a DEFINED
    /// <see cref="PackContentKind"/>. Shape problems other than the kind itself return true and are
    /// left to the deserializer, which already fails closed on them — this checks one thing.
    /// </summary>
    private static bool ContentKindsAreDeclaredAndKnown(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        using var document = JsonDocument.ParseValue(ref reader);

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(document.RootElement, "contents", out var contents)
            || contents.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        foreach (var item in contents.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            if (!TryGetPropertyIgnoreCase(item, "kind", out var kind))
            {
                return false;
            }

            var known = kind.ValueKind switch
            {
                JsonValueKind.String => Enum.TryParse<PackContentKind>(kind.GetString(), ignoreCase: true, out var named)
                    && Enum.IsDefined(named),
                JsonValueKind.Number => kind.TryGetInt32(out var numeric)
                    && Enum.IsDefined((PackContentKind)numeric),
                _ => false,
            };

            if (!known)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Property lookup matching the case-insensitivity the deserializer itself uses, so this
    /// gate never refuses a pack the deserializer would have accepted.</summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
