using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.Crypto;

/// <summary>
/// Produces byte-stable canonical JSON for signing. Object keys are sorted by UTF-16 code unit
/// (ordinal — equivalent to RFC 8785 / JCS for ASCII keys), array order is preserved, no
/// whitespace, UTF-8, and strings are escaped with the MINIMAL rule defined by RFC 8785 §3.2.2.2
/// (only <c>"</c>, <c>\</c>, and U+0000–U+001F are escaped; everything else — including
/// <c>&amp; &lt; &gt; +</c>, accented and astral-plane characters, U+2028/U+2029, and the BOM — is
/// emitted as literal UTF-8). This makes the output BYTE-IDENTICAL to JavaScript
/// <c>JSON.stringify</c> for the same logical value, which is the load-bearing cross-language
/// compatibility contract (see <c>apps/capability-host/src/membrane/signed-principal.ts</c> and its interop
/// test).
/// </summary>
/// <remarks>
/// <para>This is a pragmatic canonicalizer sufficient for <see cref="SignedOperation{T}"/>.
/// Logically-equal objects with different key orderings produce identical byte sequences.</para>
/// <para><b>Why a hand-written escaper instead of <c>Utf8JsonWriter</c>:</b> neither
/// <c>JavaScriptEncoder.UnsafeRelaxedJsonEscaping</c> nor <c>JavaScriptEncoder.Create(UnicodeRanges.All)</c>
/// produces JCS-minimal output — <c>Utf8JsonWriter</c> always uppercases the <c>\uXXXX</c> hex and
/// always escapes the astral plane (surrogate pairs), U+2028/U+2029, the BOM, and the C1 controls,
/// whereas JS <c>JSON.stringify</c> (the JCS reference here) keeps all of those literal and uses
/// lowercase hex. The only way to reach byte-identity with JS for the FULL input domain — including
/// an adversarial <c>displayName</c> such as <c>&amp; &lt; &gt; " é 😀</c> plus RTL text — is to
/// emit the JCS escaping explicitly. (Verified empirically against both runtimes.)</para>
/// <para>Related but distinct: <see cref="Harborline.Api.Foundation.Assets.Common.JsonCanonicalizer"/>
/// operates on <c>JsonDocument</c> for the Assets hash chain; this class operates on arbitrary
/// CLR objects via <see cref="JsonSerializer"/> + <see cref="JsonNode"/> and adds the
/// <see cref="SerializeSignable"/> envelope helper.</para>
/// </remarks>
public static class CanonicalJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>Compact options for the rare fallback scalar serialization (no whitespace).</summary>
    private static readonly JsonSerializerOptions CompactSerializerOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>Serializes an arbitrary CLR value to canonical-JSON UTF-8 bytes.</summary>
    /// <remarks>Uses the runtime type of <paramref name="value"/> when non-null so abstract or
    /// polymorphic payloads serialize their concrete properties, not just the base surface.</remarks>
    public static byte[] Serialize<T>(T value)
    {
        // Fail-closed BEFORE JsonSerializer.SerializeToNode silently substitutes any ill-formed
        // UTF-16 to U+FFFD (see EnsureWellFormedUtf16). A signing canonical form must never sign
        // ambiguous/malformed bytes.
        EnsureWellFormedUtf16(value);
        var node = SerializeToNodeByRuntimeType(value);
        var sorted = SortKeys(node);
        return NodeToBytes(sorted);
    }

    /// <summary>
    /// Builds the signable envelope <c>{issuedAt, issuerId, nonce, payload}</c>, sorts all keys,
    /// and returns the canonical UTF-8 bytes that an Ed25519 signature covers.
    /// </summary>
    /// <remarks>
    /// <para><c>issuedAt</c> is emitted as an INTEGER number of Unix epoch-milliseconds
    /// (<see cref="DateTimeOffset.ToUnixTimeMilliseconds"/>) — NOT an ISO-8601 string. This is the
    /// single canonical timestamp form shared with the TypeScript signer
    /// (<c>Date.prototype.getTime()</c>), eliminating every precision/offset/escaping delta a string
    /// timestamp would introduce. The ±replay window is seconds-scale, so millisecond resolution is
    /// ample.</para>
    /// <para><c>nonce</c> is the canonical lowercase-hyphenated GUID TEXT form
    /// (<see cref="Guid"/> <c>"D"</c>), NOT base64 of <c>Guid.ToByteArray()</c> — the latter
    /// has a mixed-endian layout that would not match a TypeScript UUID string. <c>issuerId</c> is
    /// base64url of the raw 32 public-key bytes (a raw byte string — no endian concern).</para>
    /// </remarks>
    public static byte[] SerializeSignable<T>(
        T payload,
        PrincipalId issuerId,
        DateTimeOffset issuedAt,
        Guid nonce)
    {
        // Fail-closed on ill-formed UTF-16 in the payload BEFORE serialization substitutes it to
        // U+FFFD (the envelope's own issuerId/nonce fields are machine-minted and always
        // well-formed; the payload is the caller-supplied surface). Never sign malformed bytes.
        EnsureWellFormedUtf16(payload);
        var payloadNode = SerializeToNodeByRuntimeType(payload);

        var envelope = new JsonObject
        {
            // Integer epoch-ms — byte-identical to the TS signer's `Date.getTime()`.
            ["issuedAt"] = JsonValue.Create(issuedAt.ToUnixTimeMilliseconds()),
            ["issuerId"] = JsonValue.Create(issuerId.ToBase64Url()),
            ["nonce"] = JsonValue.Create(nonce.ToString("D")),
            ["payload"] = payloadNode,
        };

        var sorted = SortKeys(envelope);
        return NodeToBytes(sorted);
    }

    /// <summary>
    /// Serializes using the runtime type of <paramref name="value"/> so abstract/polymorphic
    /// payloads include their concrete members. Falls back to the compile-time type T only
    /// when <paramref name="value"/> is <c>null</c>.
    /// </summary>
    private static JsonNode? SerializeToNodeByRuntimeType<T>(T value)
    {
        if (value is null)
            return JsonSerializer.SerializeToNode<T>(value, SerializerOptions);
        return JsonSerializer.SerializeToNode(value, value.GetType(), SerializerOptions);
    }

    /// <summary>
    /// Validates that every <see cref="string"/> reachable from <paramref name="value"/> is
    /// WELL-FORMED UTF-16 (no unpaired surrogate), throwing <see cref="ArgumentException"/> on the
    /// first violation. This is a DEFENSIVE, fail-closed guard run at the canonical-form INPUT.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this runs at the input — not in the escaper:</b>
    /// <see cref="JsonSerializer.SerializeToNode{T}(T, JsonSerializerOptions?)"/> SILENTLY substitutes
    /// any ill-formed UTF-16 (a lone/unpaired surrogate) to the replacement character U+FFFD before a
    /// <see cref="JsonNode"/> is ever produced — so by the time <c>WriteJsonString</c> runs the lone
    /// surrogate is already gone. A <em>signing</em> canonical form must be deterministic AND
    /// unambiguous: ill-formed UTF-16 has no well-defined UTF-8 encoding, so we REJECT it (fail-closed)
    /// rather than sign a mangled-to-U+FFFD or escape-everywhere variant. Both this .NET side and the
    /// TypeScript signer (<c>apps/capability-host/src/membrane/signed-principal.ts</c>) reject identically, so the
    /// two languages stay in parity (both reject — neither silently diverges). A correctly-paired
    /// surrogate (an astral character such as <c>😀</c>) is well-formed and passes unchanged.</para>
    /// <para>Production callers sign machine-minted, well-formed data, so this never fires for them —
    /// it is a defensive boundary that prevents an adversarial/malformed input from entering a signed
    /// envelope.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">A reachable string contains an unpaired surrogate.</exception>
    public static void EnsureWellFormedUtf16<T>(T value)
    {
        if (value is null)
            return;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        ScanForIllFormedUtf16(value, visited, depth: 0);
    }

    /// <summary>The maximum object-graph depth the input guard will traverse (defense-in-depth against
    /// an unexpectedly deep or self-referential graph; signing payloads are shallow DTOs).</summary>
    private const int MaxScanDepth = 64;

    /// <summary>
    /// Recursively walks an object graph and validates every reachable <see cref="string"/>
    /// (including dictionary keys, collection elements, and public property/field values, plus
    /// strings carried inside a <see cref="JsonNode"/>/<see cref="JsonElement"/>) for well-formed
    /// UTF-16. Reference cycles are broken by <paramref name="visited"/>; depth is bounded.
    /// </summary>
    private static void ScanForIllFormedUtf16(object? obj, HashSet<object> visited, int depth)
    {
        if (obj is null || depth > MaxScanDepth)
            return;

        switch (obj)
        {
            case string s:
                ValidateStringWellFormed(s);
                return;

            // Terminal value-ish types that cannot carry a string member we'd sign. Enumerated
            // explicitly so the reflective fall-through never recurses into framework internals.
            case bool or char or sbyte or byte or short or ushort or int or uint
                or long or ulong or float or double or decimal
                or DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly
                or Guid or Uri or Enum:
                return;

            // A JsonElement (backing JsonValue / SerializeToNode payloads) — but note: by the time a
            // JsonElement exists, STJ has ALREADY substituted any lone surrogate to U+FFFD, so this
            // path can only confirm well-formed strings. The guard's teeth are on the CLR-string and
            // JsonValue<string> paths reached BEFORE serialization. Kept for completeness.
            case JsonElement element:
                ScanJsonElement(element, visited, depth);
                return;
        }

        // Break cycles for reference types (value types can't form a cycle by reference).
        if (!obj.GetType().IsValueType && !visited.Add(obj))
            return;

        switch (obj)
        {
            case JsonValue jsonValue:
                // A CLR-backed string JsonValue (JsonValue.Create("…")) still carries the raw string.
                if (jsonValue.TryGetValue<string>(out var nodeStr))
                    ValidateStringWellFormed(nodeStr);
                else if (jsonValue.TryGetValue<JsonElement>(out var nodeElement))
                    ScanJsonElement(nodeElement, visited, depth);
                return;

            case JsonObject jsonObject:
                foreach (var kvp in jsonObject)
                {
                    ValidateStringWellFormed(kvp.Key);
                    ScanForIllFormedUtf16(kvp.Value, visited, depth + 1);
                }
                return;

            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                    ScanForIllFormedUtf16(item, visited, depth + 1);
                return;

            case IDictionary dictionary:
                foreach (DictionaryEntry entry in dictionary)
                {
                    ScanForIllFormedUtf16(entry.Key, visited, depth + 1);
                    ScanForIllFormedUtf16(entry.Value, visited, depth + 1);
                }
                return;

            case IEnumerable enumerable:
                foreach (var item in enumerable)
                    ScanForIllFormedUtf16(item, visited, depth + 1);
                return;
        }

        // Plain CLR object — walk public readable instance properties (no index params) and fields.
        var type = obj.GetType();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;
            object? propertyValue;
            try
            {
                propertyValue = property.GetValue(obj);
            }
            catch
            {
                // A throwing getter cannot contribute a signable string we can read — skip it.
                continue;
            }
            ScanForIllFormedUtf16(propertyValue, visited, depth + 1);
        }
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            ScanForIllFormedUtf16(field.GetValue(obj), visited, depth + 1);
    }

    /// <summary>Walks a <see cref="JsonElement"/> for string leaves (object property names + values,
    /// array items). Strings reaching here are post-STJ-substitution, hence already well-formed.</summary>
    private static void ScanJsonElement(JsonElement element, HashSet<object> visited, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                ValidateStringWellFormed(element.GetString()!);
                return;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    ValidateStringWellFormed(property.Name);
                    ScanJsonElement(property.Value, visited, depth + 1);
                }
                return;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    ScanJsonElement(item, visited, depth + 1);
                return;
        }
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> if <paramref name="s"/> contains an unpaired UTF-16
    /// surrogate: a high surrogate (U+D800–U+DBFF) NOT immediately followed by a low surrogate
    /// (U+DC00–U+DFFF), or a low surrogate not preceded by a high surrogate. A correctly-paired
    /// surrogate (an astral character) is well-formed and passes.
    /// </summary>
    private static void ValidateStringWellFormed(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                    throw IllFormedUtf16(i);
                i++; // skip the valid low surrogate of the pair
            }
            else if (char.IsLowSurrogate(ch))
            {
                // A low surrogate here is unpaired (a paired one is consumed by the branch above).
                throw IllFormedUtf16(i);
            }
        }
    }

    private static ArgumentException IllFormedUtf16(int index) => new(
        $"CanonicalJson cannot sign ill-formed UTF-16 (unpaired surrogate at index {index}) — " +
        "reject, do not sign.");

    /// <summary>
    /// Recursively produces a new node tree with object keys sorted by UTF-16 code unit (ordinal).
    /// Array order is preserved. Scalars are returned as deep clones.
    /// </summary>
    internal static JsonNode? SortKeys(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;

            case JsonObject obj:
                {
                    var sorted = new JsonObject();
                    foreach (var kvp in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                    {
                        // DeepClone detaches the child from its current parent so we can re-parent it.
                        sorted[kvp.Key] = SortKeys(kvp.Value?.DeepClone());
                    }
                    return sorted;
                }

            case JsonArray arr:
                {
                    var sortedArr = new JsonArray();
                    foreach (var item in arr)
                        sortedArr.Add(SortKeys(item?.DeepClone()));
                    return sortedArr;
                }

            default:
                // Value node — clone to detach from any parent.
                return node.DeepClone();
        }
    }

    /// <summary>
    /// Serializes a (key-sorted) node tree to UTF-8 with the JCS-minimal escaping described on the
    /// class. Produces byte-identical output to JS <c>JSON.stringify</c> for the same logical value.
    /// </summary>
    private static byte[] NodeToBytes(JsonNode? node)
    {
        var sb = new StringBuilder();
        WriteNode(node, sb);
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void WriteNode(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                return;

            case JsonObject obj:
                {
                    sb.Append('{');
                    var first = true;
                    foreach (var kvp in obj)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteJsonString(kvp.Key, sb);
                        sb.Append(':');
                        WriteNode(kvp.Value, sb);
                    }
                    sb.Append('}');
                    return;
                }

            case JsonArray arr:
                {
                    sb.Append('[');
                    var first = true;
                    foreach (var item in arr)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteNode(item, sb);
                    }
                    sb.Append(']');
                    return;
                }

            case JsonValue value:
                WriteValue(value, sb);
                return;

            default:
                throw new InvalidOperationException($"Unexpected JsonNode kind: {node.GetType()}");
        }
    }

    /// <summary>
    /// Writes a scalar <see cref="JsonValue"/>. A value may be backed EITHER by a
    /// <see cref="JsonElement"/> (when it came from <c>JsonSerializer.SerializeToNode</c>)
    /// OR by a boxed CLR primitive (when built via <c>JsonValue.Create(...)</c>, as the envelope's
    /// <c>issuedAt</c>/<c>issuerId</c>/<c>nonce</c> are). We branch on the backing kind so both are
    /// handled — strings get JCS escaping; numbers/bool/null are written by their canonical literal.
    /// </summary>
    private static void WriteValue(JsonValue value, StringBuilder sb)
    {
        // CLR-backed string (JsonValue.Create("...")) — common for the envelope fields.
        if (value.TryGetValue<string>(out var str))
        {
            WriteJsonString(str, sb);
            return;
        }

        // JsonElement-backed (serialized payloads) — inspect the element's kind.
        if (value.TryGetValue<JsonElement>(out var element))
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    WriteJsonString(element.GetString()!, sb);
                    return;
                case JsonValueKind.Number:
                    // GetRawText preserves the source numeric token exactly (integers stay
                    // integers; no scientific-notation reformatting).
                    sb.Append(element.GetRawText());
                    return;
                case JsonValueKind.True:
                    sb.Append("true");
                    return;
                case JsonValueKind.False:
                    sb.Append("false");
                    return;
                case JsonValueKind.Null:
                    sb.Append("null");
                    return;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected scalar JSON value kind: {element.ValueKind}");
            }
        }

        // CLR-backed bool.
        if (value.TryGetValue<bool>(out var b))
        {
            sb.Append(b ? "true" : "false");
            return;
        }

        // CLR-backed integer (the epoch-ms timestamp lands here via JsonValue.Create(long)).
        if (value.TryGetValue<long>(out var l))
        {
            sb.Append(l.ToString(CultureInfo.InvariantCulture));
            return;
        }

        // Fallback: any other CLR-backed numeric (int/double/decimal/...). ToJsonString emits the
        // value with no whitespace; for the integer/string/bool domain this canonicalizer signs,
        // the branches above already cover the cross-language-pinned cases.
        sb.Append(value.ToJsonString(CompactSerializerOptions));
    }

    /// <summary>
    /// Writes a JSON string literal with RFC 8785 / JCS §3.2.2.2 minimal escaping — byte-for-byte
    /// what JavaScript <c>JSON.stringify</c> emits for the same string:
    /// <list type="bullet">
    ///   <item><c>"</c> → <c>\"</c>, <c>\</c> → <c>\\</c>;</item>
    ///   <item>U+0008 <c>\b</c>, U+0009 <c>\t</c>, U+000A <c>\n</c>, U+000C <c>\f</c>, U+000D <c>\r</c>;</item>
    ///   <item>every other U+0000–U+001F → <c>\u00xx</c> (LOWERCASE hex);</item>
    ///   <item>everything else (including <c>&amp; &lt; &gt; +</c>, the astral plane, U+2028/U+2029,
    ///   the BOM, and all other non-ASCII) → literal.</item>
    /// </list>
    /// The string is iterated by <see cref="char"/> (UTF-16 code unit); surrogate pairs pass through
    /// unescaped and are encoded to literal 4-byte UTF-8 by <see cref="Encoding.UTF8"/>, exactly as JS does.
    /// </summary>
    private static void WriteJsonString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                default:
                    if (ch < 0x20)
                    {
                        sb.Append("\\u");
                        sb.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
