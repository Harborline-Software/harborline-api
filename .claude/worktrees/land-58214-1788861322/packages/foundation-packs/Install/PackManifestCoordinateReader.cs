using System.Text.Json;

namespace Harborline.Api.Foundation.Packs.Install;

/// <summary>
/// Reads only the unverified manifest coordinates needed to address the authorization gate. The
/// scan is bounded (64 KiB prefix, depth 16) and never validates or deserializes content; it walks
/// the manifest object to its end so a duplicate key/version is refused. Duplicate enclosing
/// objects are not detected here: <see cref="Serialization.PackFileCodec"/> refuses any duplicate
/// property, so a pack whose first manifest differs from its last can never verify, and the gate
/// target for it is at worst a claim for a pack that does not exist.
/// </summary>
internal static class PackManifestCoordinateReader
{
    internal const int MaximumPrefixBytes = 64 * 1024;

    internal static (string PackKey, string Version)? TryRead(ReadOnlySpan<byte> packBytes)
    {
        var prefixLength = Math.Min(packBytes.Length, MaximumPrefixBytes);
        var reader = new Utf8JsonReader(
            packBytes[..prefixLength],
            isFinalBlock: prefixLength == packBytes.Length,
            new JsonReaderState(new JsonReaderOptions { MaxDepth = 16 }));

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject
                || !TryEnterObject(ref reader, "envelope", rejectContents: true)
                || !TryEnterObject(ref reader, "payload", rejectContents: false)
                || !TryEnterObject(ref reader, "manifest", rejectContents: false))
            {
                return null;
            }

            string? packKey = null;
            string? version = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return null;
                }

                var propertyName = reader.GetString();
                if (!reader.Read())
                {
                    return null;
                }

                // Manifest content entries are skipped token-by-token (never validated or
                // deserialized) so the scan reaches the end of the manifest object and any later
                // duplicate coordinate is seen. The 64 KiB prefix and MaxDepth bound that skip.
                if (PropertyEquals(propertyName, "key"))
                {
                    if (reader.TokenType != JsonTokenType.String || packKey is not null)
                    {
                        return null;
                    }
                    packKey = reader.GetString();
                }
                else if (PropertyEquals(propertyName, "version"))
                {
                    if (reader.TokenType != JsonTokenType.String || version is not null)
                    {
                        return null;
                    }
                    version = reader.GetString();
                }
                else if (!reader.TrySkip())
                {
                    return null;
                }
            }

            // The whole manifest object is scanned before answering, so a duplicate key/version
            // later in the object (last-wins under the shipping decoder) is seen and refused above
            // rather than letting the first pair address the gate for a different pack.
            if (reader.TokenType == JsonTokenType.EndObject && packKey is not null && version is not null)
            {
                return (packKey, version);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static bool TryEnterObject(
        ref Utf8JsonReader reader,
        string expectedProperty,
        bool rejectContents)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return false;
            }

            var propertyName = reader.GetString();
            if (rejectContents && PropertyEquals(propertyName, "contents"))
            {
                return false;
            }
            if (!reader.Read())
            {
                return false;
            }
            if (PropertyEquals(propertyName, expectedProperty))
            {
                return reader.TokenType == JsonTokenType.StartObject;
            }
            if (!reader.TrySkip())
            {
                return false;
            }
        }

        return false;
    }

    private static bool PropertyEquals(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}
