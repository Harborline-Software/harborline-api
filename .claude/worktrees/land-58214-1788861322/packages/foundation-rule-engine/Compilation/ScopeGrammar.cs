using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.RuleEngine.Model;
using Harborline.Api.Foundation.Forms.Models;

namespace Harborline.Api.Foundation.RuleEngine.Compilation;

/// <summary>
/// The neutral compiler's grammar-lowering half (SPINE-1 design §1.2). Lowers the
/// structured scope-grammar (<c>self</c> / <c>field.x</c> / <c>row.y</c> /
/// <c>table.agg(col)</c> / <c>parent.f</c> / <c>section.id.f</c>) carried inside a
/// rule's opaque JsonLogic expression into a canonical normalized AST (only
/// <c>field.</c> / <c>row.</c> vars + the <c>agg</c> operator). Performed ONCE so
/// both tiers consume the identical normalized AST — the corpus pins
/// <c>expectedAst</c> so a lowering divergence is caught, not just an eval divergence.
/// </summary>
internal sealed record LowerContext(RuleScope Scope, string ScopeTarget, string? SectionId);

internal static class ScopeGrammar
{
    public static OutputType OutputTypeFor(RuleActionKind action, string ruleId) => action switch
    {
        RuleActionKind.Compute => OutputType.Value,
        RuleActionKind.Validate => OutputType.Validity,
        RuleActionKind.Presentation => OutputType.Presentation,
        RuleActionKind.Options => OutputType.Options,
        RuleActionKind.Visibility or RuleActionKind.Required or RuleActionKind.ReadOnly => OutputType.Visibility,
        // L1145 (ADR 0038 as corrected): an unknown / out-of-enum action kind — a future or
        // foreign RESTRICTING kind arriving via pack install or sync — must refuse compilation,
        // never silently lower to a show/hide permit.
        _ => throw new RuleCompilationException(
            RuleEngineCodes.CompileUnknownAction,
            $"rule '{ruleId}': unknown action kind '{(int)action}' — refused (an unrecognized kind may restrict).",
            ruleId),
    };

    /// <summary>Parses + lowers the opaque expression text into a canonical AST.</summary>
    public static JsonNode? Lower(string expression, LowerContext ctx, string ruleId)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(expression);
        }
        catch (JsonException ex)
        {
            throw new RuleCompilationException(
                RuleEngineCodes.CompileInvalidExpression,
                $"rule '{ruleId}': expression is not valid JSON: {ex.Message}",
                ruleId);
        }

        return Rewrite(parsed, ctx, ruleId);
    }

    private static JsonNode? Rewrite(JsonNode? node, LowerContext ctx, string ruleId)
    {
        switch (node)
        {
            case JsonObject obj when obj.Count == 1 && obj.ContainsKey("var"):
            {
                var pathNode = obj["var"];
                // {"var":[path, default]} or {"var":"path"}
                string path;
                JsonNode? def = null;
                if (pathNode is JsonArray arr)
                {
                    path = arr.Count > 0 ? (arr[0]?.GetValue<string>() ?? "") : "";
                    def = arr.Count > 1 ? arr[1]?.DeepClone() : null;
                }
                else
                {
                    path = pathNode?.GetValue<string>() ?? "";
                }

                // A table.fn(col) var lowers to the agg operator.
                if (path.StartsWith("table.", StringComparison.Ordinal) && path.Contains('('))
                {
                    return LowerAgg(path, ctx, ruleId);
                }

                string canonical = LowerVarPath(path, ctx, ruleId);
                if (def is not null)
                {
                    return new JsonObject { ["var"] = new JsonArray(JsonValue.Create(canonical), def) };
                }
                return new JsonObject { ["var"] = JsonValue.Create(canonical) };
            }
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (k, v) in obj)
                {
                    result[k] = Rewrite(v, ctx, ruleId);
                }
                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                {
                    result.Add(Rewrite(item, ctx, ruleId));
                }
                return result;
            }
            default:
                return node?.DeepClone();
        }
    }

    private static JsonNode LowerAgg(string path, LowerContext ctx, string ruleId)
    {
        // table.<fn>(<col>)  or  table.<fn>(<section>.<col>)
        int open = path.IndexOf('(');
        int close = path.IndexOf(')');
        if (close <= open)
        {
            throw Bad(ruleId, $"malformed table aggregate '{path}'");
        }
        string fn = path[6..open];
        string arg = path[(open + 1)..close];
        string section, col;
        int dot = arg.IndexOf('.');
        if (dot >= 0)
        {
            section = arg[..dot];
            col = arg[(dot + 1)..];
        }
        else
        {
            section = ctx.SectionId
                ?? throw Bad(ruleId, $"table aggregate '{path}' needs an explicit section (table.{fn}(section.{arg})) outside a row/table-scoped rule");
            col = arg;
        }
        if (fn.Length == 0 || section.Length == 0 || col.Length == 0)
        {
            throw Bad(ruleId, $"malformed table aggregate '{path}'");
        }
        return new JsonObject
        {
            ["agg"] = new JsonArray(JsonValue.Create(fn), JsonValue.Create(section), JsonValue.Create(col)),
        };
    }

    private static string LowerVarPath(string path, LowerContext ctx, string ruleId)
    {
        if (path == "self")
        {
            return ctx.Scope switch
            {
                RuleScope.Row => "row." + RowFieldOf(ctx, ruleId),
                RuleScope.Field => "field." + ctx.ScopeTarget,
                _ => throw Bad(ruleId, "'self' is only valid in a Field- or Row-scoped rule"),
            };
        }
        if (path.StartsWith("row.", StringComparison.Ordinal))
        {
            if (ctx.Scope != RuleScope.Row)
            {
                throw Bad(ruleId, $"'row.' reference '{path}' is only valid in a Row-scoped rule");
            }
            return path;
        }
        if (path.StartsWith("parent.", StringComparison.Ordinal))
        {
            // parent.<f> reaches the top-level instance — addresses the same cell as field.<f>.
            return "field." + path["parent.".Length..];
        }
        if (path.StartsWith("field.", StringComparison.Ordinal))
        {
            return path;
        }
        if (path.StartsWith("section.", StringComparison.Ordinal))
        {
            // section.<id>.<field> disambiguates authoring; resolves to the top-level field.
            var rest = path["section.".Length..];
            int lastDot = rest.IndexOf('.');
            if (lastDot < 0) throw Bad(ruleId, $"malformed section reference '{path}' (expected section.<id>.<field>)");
            return "field." + rest[(lastDot + 1)..];
        }
        // WF-KEY (ADR 0140) process-context prefixes: wf.state / wf.actor / wf.iteration /
        // timer.<id>. A workflow guard reads the PROCESS context bag, not a record cell, so
        // these pass through CANONICALLY (no "field." rewrite) and are NOT extracted as field
        // deps (see ExtractRefs below). Additive: no form rule addresses a wf./timer. cell, so
        // existing lowering is byte-identical. Mirrors @harborline-software/rule-engine grammar.ts.
        if (path.StartsWith("wf.", StringComparison.Ordinal) || path.StartsWith("timer.", StringComparison.Ordinal))
        {
            return path;
        }
        // bare name => top-level field (flat-form back-compat)
        return "field." + path;
    }

    private static string RowFieldOf(LowerContext ctx, string ruleId)
    {
        int slash = ctx.ScopeTarget.IndexOf('/');
        if (slash < 0) throw Bad(ruleId, $"Row rule ScopeTarget '{ctx.ScopeTarget}' must be 'section/field'");
        return ctx.ScopeTarget[(slash + 1)..];
    }

    private static RuleCompilationException Bad(string ruleId, string message)
        => new(RuleEngineCodes.CompileBadGrammar, $"rule '{ruleId}': {message}", ruleId);

    // ── reference extraction ──────────────────────────────────────────────────

    public static IReadOnlyList<RuleRef> ExtractRefs(JsonNode? ast, string ruleId = "")
    {
        var refs = new List<RuleRef>();
        Walk(ast, refs, ruleId);
        return refs;
    }

    private static void Walk(JsonNode? node, List<RuleRef> refs, string ruleId)
    {
        switch (node)
        {
            case JsonObject obj when obj.Count == 1 && obj.ContainsKey("var"):
            {
                string path = VarPathOf(obj["var"]);
                if (path.StartsWith("field.", StringComparison.Ordinal))
                    refs.Add(new FieldRef(path["field.".Length..]));
                else if (path.StartsWith("row.", StringComparison.Ordinal))
                    refs.Add(new RowFieldRef(path["row.".Length..]));
                // var with a default array: also walk the default for nested vars
                if (obj["var"] is JsonArray a && a.Count > 1) Walk(a[1], refs, ruleId);
                break;
            }
            case JsonObject obj when obj.Count == 1 && obj.ContainsKey("agg"):
            {
                // Ticket 162: an agg node the graph cannot statically register - wrong arity, or
                // expression-valued / non-string args - used to be SKIPPED here, leaving no fold
                // cell and a per-keystroke runtime refusal with no authoring-time signal. The
                // compiler refuses it at publish instead, identically to the TS tier.
                if (obj["agg"] is not JsonArray a || a.Count != 3
                    || a.Any(part => part is not JsonValue v || !v.TryGetValue<string>(out _)))
                {
                    throw Bad(ruleId, "a table aggregate must be a static [fn, section, col] string triple");
                }
                refs.Add(new AggRef(
                    a[1]!.GetValue<string>(),
                    a[0]!.GetValue<string>(),
                    a[2]!.GetValue<string>()));
                break;
            }
            case JsonObject obj:
                foreach (var (_, v) in obj) Walk(v, refs, ruleId);
                break;
            case JsonArray arr:
                foreach (var item in arr) Walk(item, refs, ruleId);
                break;
        }
    }

    private static string VarPathOf(JsonNode? varNode)
        => varNode is JsonArray a
            ? (a.Count > 0 ? a[0]?.GetValue<string>() ?? "" : "")
            : varNode?.GetValue<string>() ?? "";

    // ── AST metrics (static bounds) ────────────────────────────────────────────

    /// <summary>Counts AST nodes + enforces the per-rule literal-length bound; throws on violation.</summary>
    public static int Measure(JsonNode? node, RuleEngineLimits limits, string ruleId)
    {
        int count = 1;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, v) in obj) count += Measure(v, limits, ruleId);
                break;
            case JsonArray arr:
                foreach (var item in arr) count += Measure(item, limits, ruleId);
                break;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.Length > limits.MaxLiteralLength:
                throw new RuleCompilationException(
                    RuleEngineCodes.CompileLiteralTooLong,
                    $"rule '{ruleId}': a string literal of length {s.Length} exceeds the bound {limits.MaxLiteralLength}",
                    ruleId);
        }
        return count;
    }
}
