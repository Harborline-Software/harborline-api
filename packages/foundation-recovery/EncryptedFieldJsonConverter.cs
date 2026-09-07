using System;
using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harborline.Api.Foundation.Recovery;

/// <summary>
/// JSON converter for <see cref="EncryptedField"/>. Serializes as
/// <c>{ "ct": "&lt;base64url&gt;", "nonce": "&lt;base64url&gt;", "kv": &lt;int&gt;, "suite": &lt;int&gt; }</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <c>suite</c> tag is OPTIONAL on read and is the back-compat seam (ADR 0004 §1).</b>
/// Every <see cref="EncryptedField"/> persisted before the suite dimension existed has NO
/// <c>suite</c> property. When the tag is absent the field is read as
/// <see cref="CryptoSuites.LegacyDefault"/> (suite #1, AES-256-GCM+HKDF) — the algorithm those
/// blobs were actually sealed with — so 100% of legacy ciphertext stays decryptable. Required
/// fields remain <c>ct</c> / <c>nonce</c> / <c>kv</c> only; adding a required <c>suite</c> would
/// break that invariant.
/// </para>
/// <para>
/// A present <c>suite</c> integer is parsed faithfully into <see cref="CryptoSuite"/> even when
/// it names a suite this build does not recognize (a forward version). Just like an out-of-range
/// <c>kv</c>, that is NOT rejected here — the wire layer is a pure (de)serializer. The decryptor
/// is the crypto-policy boundary that fails closed on an unregistered suite
/// (see <see cref="CryptoSuites.IsRegistered"/>).
/// </para>
/// </remarks>
internal sealed class EncryptedFieldJsonConverter : JsonConverter<EncryptedField>
{
    private const string CiphertextProperty = "ct";
    private const string NonceProperty = "nonce";
    private const string KeyVersionProperty = "kv";
    private const string SuiteProperty = "suite";

    public override EncryptedField Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected start of EncryptedField object.");
        }

        ReadOnlyMemory<byte>? ciphertext = null;
        ReadOnlyMemory<byte>? nonce = null;
        int? keyVersion = null;
        // Absent on the wire (every legacy blob) ⇒ the implicit legacy default. This is the
        // hard back-compat invariant: a missing suite tag means suite #1, never a parse error.
        CryptoSuite suite = CryptoSuites.LegacyDefault;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (ciphertext is null || nonce is null || keyVersion is null)
                {
                    throw new JsonException("EncryptedField requires 'ct', 'nonce', and 'kv'.");
                }
                return new EncryptedField(ciphertext.Value, nonce.Value, keyVersion.Value, suite);
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("Expected EncryptedField property name.");
            }

            var name = reader.GetString();
            reader.Read();

            switch (name)
            {
                case CiphertextProperty:
                    ciphertext = DecodeBase64Url(reader.GetString());
                    break;
                case NonceProperty:
                    nonce = DecodeBase64Url(reader.GetString());
                    break;
                case KeyVersionProperty:
                    keyVersion = reader.GetInt32();
                    break;
                case SuiteProperty:
                    suite = (CryptoSuite)reader.GetInt32();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("Unexpected end of JSON within EncryptedField.");
    }

    public override void Write(Utf8JsonWriter writer, EncryptedField value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString(CiphertextProperty, EncodeBase64Url(value.Ciphertext.Span));
        writer.WriteString(NonceProperty, EncodeBase64Url(value.Nonce.Span));
        writer.WriteNumber(KeyVersionProperty, value.KeyVersion);
        // Always emit the suite tag on new writes — the value becomes self-describing.
        // Old readers (pre-this-change) ignore the unknown property via their default arm.
        writer.WriteNumber(SuiteProperty, (int)value.Suite);
        writer.WriteEndObject();
    }

    private static string EncodeBase64Url(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }
        var standard = Convert.ToBase64String(bytes);
        return standard.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static ReadOnlyMemory<byte> DecodeBase64Url(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return ReadOnlyMemory<byte>.Empty;
        }
        var standard = input.Replace('-', '+').Replace('_', '/');
        var padding = standard.Length % 4;
        if (padding == 2)
        {
            standard += "==";
        }
        else if (padding == 3)
        {
            standard += "=";
        }
        else if (padding == 1)
        {
            throw new JsonException("Invalid base64url: malformed length.");
        }
        try
        {
            return Convert.FromBase64String(standard);
        }
        catch (FormatException ex)
        {
            throw new JsonException("Invalid base64url in EncryptedField.", ex);
        }
    }
}
