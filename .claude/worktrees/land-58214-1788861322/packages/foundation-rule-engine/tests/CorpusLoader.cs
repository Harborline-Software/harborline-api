using System.Globalization;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.RuleEngine.Compilation;
using Harborline.Api.Foundation.RuleEngine.Conformance;
using Harborline.Api.Foundation.RuleEngine.Graph;
using Harborline.Api.Foundation.RuleEngine.Skins;

namespace Harborline.Api.Foundation.RuleEngine.Tests;

/// <summary>A fixed clock for deterministic <c>date.*</c> evaluation in the corpus.</summary>
internal sealed class FixedClock : TimeProvider
{
    private readonly DateTimeOffset _now;
    public FixedClock(DateTimeOffset now) => _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>
/// Loads + runs the shared conformance corpus (SPINE-1 §6.1). A faithful port of
/// harborline-platform's <c>hlp.foundation.rule-runtime.tests/CorpusLoader.cs</c>; see
/// <see cref="CorpusMirrorTests"/> for the ownership decision and the list of adaptations.
/// </summary>
internal static class CorpusLoader
{
    private static string CorpusDir => Path.Combine(AppContext.BaseDirectory, "corpus");

    public static IEnumerable<(string File, JsonObject Case)> AllCases()
    {
        foreach (var path in Directory.EnumerateFiles(CorpusDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var root = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
            string file = Path.GetFileName(path);
            foreach (var caseNode in (JsonArray)root["cases"]!)
            {
                yield return (file, (JsonObject)caseNode!);
            }
        }
    }

    /// <summary>Ticket 162: the stable code a publish-time-refusal case expects compile to raise.</summary>
    public static string CompileRefusalCode(JsonObject caseObj)
    {
        var rules = ((JsonArray)caseObj["definitionRules"]!).Select(n => ParseRule((JsonObject)n!)).ToList();
        try
        {
            RuleCompiler.Compile(rules, ParseLimits(caseObj));
        }
        catch (RuleCompilationException ex)
        {
            return ex.Code;
        }
        throw new InvalidOperationException($"case '{caseObj["name"]}' expected a compile refusal and none was raised");
    }

    /// <summary>Ticket 162: evaluate one rule through the guard tier over the instance as a flat context bag.</summary>
    public static string GuardValueOutcome(JsonObject caseObj)
    {
        string ruleId = caseObj["guardValue"]!.GetValue<string>();
        var rule = ((JsonArray)caseObj["definitionRules"]!)
            .Select(n => ParseRule((JsonObject)n!))
            .Single(r => r.Id == ruleId);
        var clock = new FixedClock(DateTimeOffset.Parse(
            caseObj["clock"]?.GetValue<string>() ?? "2026-06-30T00:00:00Z",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
        var bag = ((JsonObject)caseObj["instance"]!).ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone());
        var value = new GuardEvaluator(ParseLimits(caseObj), clock).EvaluateValue(rule, bag);
        return CanonicalJson.SerializeComputedValue(value);
    }

    /// <summary>
    /// The rules the case DECLARES — the exact list handed to the compiler. Ticket 193 review: the
    /// runner needs this separately from <see cref="RunCase"/> so it can assert the compiled graph
    /// introduced no rule the case never declared.
    /// </summary>
    public static List<RuleDefinition> DeclaredRules(JsonObject caseObj)
        => caseObj.TryGetPropertyValue("skin", out var skinNode) && skinNode is JsonObject skinObj
            ? new List<RuleDefinition> { CompileSkin(skinObj) }
            : ((JsonArray)caseObj["definitionRules"]!).Select(n => ParseRule((JsonObject)n!)).ToList();

    public static (CompiledGraph Compiled, RuleEvaluationResult Result) RunCase(JsonObject caseObj)
    {
        // ADR 0146 D2 — a case may carry a `skin` (decision-table / formula) authoring representation
        // instead of raw `definitionRules`. The skin compiles to the RuleDefinition(s) the normal flow
        // then compiles + evaluates — so a skin case exercises the identical downstream path (byte-identical
        // AST + outcomes prove both tiers' skin compilers agree).
        var rules = DeclaredRules(caseObj);
        var limits = ParseLimits(caseObj);
        var compiled = RuleCompiler.Compile(rules, limits);

        var clock = new FixedClock(DateTimeOffset.Parse(
            caseObj["clock"]?.GetValue<string>() ?? "2026-06-30T00:00:00Z",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));

        var graph = new FormRuleGraph(compiled, limits, clock);
        var instance = RuleInstance.FromJson((JsonObject)caseObj["instance"]!);
        var result = graph.EvaluateInstance(instance);
        return (compiled, result);
    }

    // Adaptation: the platform routes construction through RuleDefinitionFactory because its
    // RuleDefinition is a generated contract type whose optional members are Optional<T>. This tier's
    // RuleDefinition (Harborline.Api.Foundation.Forms.Models) takes plain nullables, so the factory
    // indirection has nothing to do and the record is constructed directly.
    private static RuleDefinition ParseRule(JsonObject r)
    {
        var presentation = r.TryGetPropertyValue("presentation", out var p) && p is JsonObject po
            ? ParseHint(po)
            : null;
        return new RuleDefinition(
            Id: r["id"]!.GetValue<string>(),
            Tier: Enum.Parse<RuleTier>(r["tier"]!.GetValue<string>()),
            Scope: Enum.Parse<RuleScope>(r["scope"]!.GetValue<string>()),
            ScopeTarget: r["scopeTarget"]?.GetValue<string>() ?? "",
            Expression: r["expression"]!.ToJsonString(),
            Action: Enum.Parse<RuleActionKind>(r["action"]!.GetValue<string>()),
            ErrorMessage: null,
            Presentation: presentation);
    }

    // ── ADR 0146 D2 skin parsing (corpus authoring JSON → the typed skin model → RuleDefinition) ──

    private static RuleDefinition CompileSkin(JsonObject skin)
    {
        string kind = skin["kind"]!.GetValue<string>();
        return kind switch
        {
            "decision-table" => DecisionTableCompiler.Compile(ParseDecisionTableSkin(skin)),
            "formula" => FormulaCompiler.Compile(ParseFormulaSkin(skin)),
            _ => throw new ArgumentException($"unknown skin kind '{kind}'"),
        };
    }

    private static DecisionTableSkin ParseDecisionTableSkin(JsonObject s) => new(
        RuleId: s["ruleId"]!.GetValue<string>(),
        Scope: Enum.Parse<RuleScope>(s["scope"]!.GetValue<string>()),
        ScopeTarget: s["scopeTarget"]?.GetValue<string>() ?? "",
        Action: Enum.Parse<RuleActionKind>(s["action"]!.GetValue<string>()),
        HitPolicy: s["hitPolicy"]!.GetValue<string>() switch
        {
            "priority" => HitPolicy.Priority,
            "first-match" => HitPolicy.FirstMatch,
            var p => throw new ArgumentException($"unknown hit policy '{p}'"),
        },
        Inputs: ((JsonArray)s["inputs"]!).Select(n => n!.GetValue<string>()).ToList(),
        Rows: ((JsonArray)s["rows"]!).Select(n => ParseRow((JsonObject)n!)).ToList(),
        NoMatch: ParseNoMatch((JsonObject)s["noMatch"]!));

    private static DecisionRow ParseRow(JsonObject r) => new(
        When: ((JsonArray)r["when"]!).Select(n => ParseCell((JsonObject)n!)).ToList(),
        Output: r["output"]?.DeepClone(),
        Priority: r["priority"]?.GetValue<int>() ?? 0);

    private static DecisionCell ParseCell(JsonObject c)
    {
        if (c.ContainsKey("any")) return DecisionCell.Wildcard;
        if (c.ContainsKey("op")) return DecisionCell.Compare(c["op"]!.GetValue<string>(), c["value"]?.DeepClone());
        if (c.TryGetPropertyValue("range", out var range) && range is JsonObject ro)
            return DecisionCell.Range(ro["lo"]?.DeepClone(), ro["hi"]?.DeepClone());
        throw new ArgumentException("a decision-table cell must be one of { any | op/value | range }");
    }

    private static NoMatch ParseNoMatch(JsonObject nm)
    {
        if (nm.ContainsKey("catchAll")) return NoMatch.CatchAll;
        if (nm.ContainsKey("default")) return NoMatch.WithDefault(nm["default"]?.DeepClone());
        throw new ArgumentException("noMatch must declare a `default` value or `catchAll: true` (board F1 — no silent null)");
    }

    private static FormulaSkin ParseFormulaSkin(JsonObject s) => new(
        RuleId: s["ruleId"]!.GetValue<string>(),
        Scope: Enum.Parse<RuleScope>(s["scope"]!.GetValue<string>()),
        ScopeTarget: s["scopeTarget"]?.GetValue<string>() ?? "",
        Action: Enum.Parse<RuleActionKind>(s["action"]!.GetValue<string>()),
        Inputs: ((JsonArray)s["inputs"]!).Select(n =>
        {
            var io = (JsonObject)n!;
            return new FormulaInput(io["ref"]!.GetValue<string>(), io["type"]?.GetValue<string>() ?? "any");
        }).ToList(),
        Expression: s["expression"]?.DeepClone());

    // Adaptation: same as ParseRule — this tier's PresentationHint / InternationalizedText are plain
    // records with a nullable string Severity, so the platform's RuleDefinitionFactory.Hint/Text
    // (which map to Optional<T> and a PresentationHintSeverityValue enum) collapse to constructors.
    private static PresentationHint ParseHint(JsonObject po)
    {
        InternationalizedText? badge = null;
        if (po.TryGetPropertyValue("badge", out var b) && b is JsonObject bo)
        {
            var values = new Dictionary<string, string>();
            foreach (var (k, v) in (JsonObject)bo["values"]!) values[k] = v!.GetValue<string>();
            badge = new InternationalizedText(bo["defaultLocale"]!.GetValue<string>(), values);
        }
        return new PresentationHint(
            po["severity"]?.GetValue<string>(),
            badge,
            po["styleToken"]?.GetValue<string>());
    }

    private static RuleEngineLimits ParseLimits(JsonObject caseObj)
    {
        if (!caseObj.TryGetPropertyValue("limits", out var l) || l is not JsonObject lo) return RuleEngineLimits.Default;
        var d = RuleEngineLimits.Default;
        return d with
        {
            MaxGraphNodes = lo["maxGraphNodes"]?.GetValue<int>() ?? d.MaxGraphNodes,
            MaxDependencyDepth = lo["maxDependencyDepth"]?.GetValue<int>() ?? d.MaxDependencyDepth,
            MaxReferencesPerRule = lo["maxReferencesPerRule"]?.GetValue<int>() ?? d.MaxReferencesPerRule,
            MaxAstNodes = lo["maxAstNodes"]?.GetValue<int>() ?? d.MaxAstNodes,
            StepBudget = lo["stepBudget"]?.GetValue<int>() ?? d.StepBudget,
        };
    }

    public static void WriteArtifact(string name, JsonNode content)
    {
        try
        {
            var dir = Path.Combine(RepoRoot(), "artifacts", "rule-runtime");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name), content.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // Artifact emission is best-effort (cross-tier diff convenience); never fail the suite on IO.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "repository.yaml"))) dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
