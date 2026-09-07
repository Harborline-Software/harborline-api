using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Model;

namespace Harborline.Api.Foundation.RuleEngine.Conformance;

/// <summary>
/// Canonical (deterministic) JSON serialization of a <see cref="RuleOutcome"/> — the
/// cross-tier comparison surface (SPINE-1 design §6.1). Object keys are sorted;
/// integral numbers serialize without a decimal point; the byte form is identical
/// to the TS tier's <c>canonicalOutcome</c> so the conformance corpus can assert
/// <b>byte-identical</b> outcomes across both engines.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Serializes a <see cref="RuleOutcome"/> to its canonical JSON string.</summary>
    public static string SerializeOutcome(RuleOutcome outcome)
    {
        var obj = new JsonObject
        {
            ["ruleId"] = outcome.RuleId,
            ["target"] = outcome.Target.Key,
            ["outputType"] = outcome.OutputType.ToString(),
        };
        switch (outcome.OutputType)
        {
            case OutputType.Value:
                obj["value"] = SerializeComputed(outcome.Value!);
                break;
            case OutputType.Validity:
                obj["validity"] = SerializeValidity(outcome.Validity!);
                break;
            case OutputType.Visibility:
                var vis = outcome.Visibility!;
                obj["visibility"] = new JsonObject
                {
                    ["visible"] = vis.Visible,
                    ["required"] = vis.Required,
                    ["readOnly"] = vis.ReadOnly,
                };
                break;
            case OutputType.Presentation:
                obj["presentation"] = SerializePresentation(outcome.Presentation!);
                break;
            case OutputType.Options:
                obj["options"] = SerializeOptions(outcome.Options!);
                break;
        }
        var sb = new StringBuilder();
        Write(obj, sb);
        return sb.ToString();
    }

    /// <summary>Serializes a bare <see cref="ComputedValue"/> canonically (the guard-value corpus lane; ticket 162).</summary>
    public static string SerializeComputedValue(ComputedValue cv)
    {
        var sb = new StringBuilder();
        Write(SerializeComputed(cv), sb);
        return sb.ToString();
    }

    private static JsonObject SerializeComputed(ComputedValue cv)
    {
        var o = new JsonObject { ["state"] = cv.State.ToString() };
        if (cv.State == ValueState.Resolved) o["value"] = cv.Value?.DeepClone();
        if (cv.State == ValueState.Error && cv.Error is not null) o["error"] = SerializeError(cv.Error);
        return o;
    }

    private static JsonObject SerializeValidity(Validity v)
    {
        var o = new JsonObject { ["ok"] = v.Ok };
        if (v.Error is not null) o["error"] = SerializeError(v.Error);
        return o;
    }

    private static JsonObject SerializeOptions(OptionsOutcome oo)
    {
        var o = new JsonObject { ["state"] = oo.State.ToString() };
        if (oo.State == ValueState.Resolved)
        {
            var arr = new JsonArray();
            foreach (var opt in oo.Options ?? Array.Empty<JsonNode?>()) arr.Add(opt?.DeepClone());
            o["options"] = arr;
        }
        if (oo.State == ValueState.Error && oo.Error is not null) o["error"] = SerializeError(oo.Error);
        return o;
    }

    private static JsonObject SerializeError(RuleError e)
    {
        var p = new JsonObject();
        foreach (var (k, val) in e.Params.OrderBy(kv => kv.Key, StringComparer.Ordinal)) p[k] = val;
        return new JsonObject { ["code"] = e.Code, ["params"] = p };
    }

    private static JsonObject SerializePresentation(PresentationOutcome p)
    {
        var o = new JsonObject
        {
            ["severity"] = p.Severity?.ToString().ToLowerInvariant(),
        };
        if (p.StyleToken is not null) o["styleToken"] = p.StyleToken;
        if (p.Badge is not null)
        {
            var badge = new JsonObject { ["defaultLocale"] = p.Badge.DefaultLocale };
            var values = new JsonObject();
            foreach (var (k, val) in p.Badge.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal)) values[k] = val;
            badge["values"] = values;
            o["badge"] = badge;
        }
        return o;
    }

    /// <summary>Writes any <see cref="JsonNode"/> canonically (sorted keys, integral numbers without a point).</summary>
    public static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                bool first = true;
                foreach (var (k, v) in obj.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(k, sb);
                    sb.Append(':');
                    Write(v, sb);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (int i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(arr[i], sb);
                }
                sb.Append(']');
                break;
            case JsonValue val:
                WriteValue(val, sb);
                break;
        }
    }

    private static void WriteValue(JsonValue val, StringBuilder sb)
    {
        if (val.TryGetValue<bool>(out var b)) { sb.Append(b ? "true" : "false"); return; }
        if (val.TryGetValue<string>(out var s)) { WriteString(s, sb); return; }
        if (val.TryGetValue<long>(out var l)) { sb.Append(CanonicalNumber.ToJsonString(l)); return; }
        if (val.TryGetValue<int>(out var iv)) { sb.Append(CanonicalNumber.ToJsonString((long)iv)); return; }
        if (val.TryGetValue<double>(out var d)) { sb.Append(CanonicalNumber.ToJsonString(d)); return; }
        // Fallback: round-trip raw JSON text.
        sb.Append(val.ToJsonString());
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
