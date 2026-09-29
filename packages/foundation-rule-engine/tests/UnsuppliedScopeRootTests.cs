using System.Text.Json.Nodes;

namespace Harborline.Api.Foundation.RuleEngine.Tests;

/// <summary>
/// ck-7 S3: harborline-platform #200's <c>unsupplied-scope-root.json</c> conformance cases, verbatim, run
/// through the corpus runner. Held here rather than in the mirrored <c>Conformance/corpus/</c> because #200
/// had not merged when this copy took the fix; move them into the mirror on the next manifest bump.
/// </summary>
public sealed class UnsuppliedScopeRootTests
{
    private const string Corpus = """
{
  "_comment": "ck-7 S3 (rules-ck-28, forms-ck-13): Forms and Documents declare caller, clock and record_type, but no R1 evaluator supplies them. A bare caller.id used to lower to field.caller.id, so a rule meant to read the authenticated principal read a client-supplied \"caller.id\" candidate property and the submission committed. Both tiers now refuse such a reference at compile (fail closed), in a var path and in a missing key; a record field of that name stays readable as field.<name>.",
  "cases": [
    {
      "name": "scope-root/caller-path-refuses-at-compile",
      "definitionRules": [
        { "id": "v.caller", "tier": "JsonLogic", "scope": "Schema", "scopeTarget": "", "action": "Validate",
          "expression": { "==": [ { "var": "caller.id" }, "attacker" ] } }
      ],
      "instance": { "caller.id": "attacker" },
      "expectedCompileError": { "code": "rule.compile.bad_grammar" }
    },
    {
      "name": "scope-root/bare-caller-refuses-at-compile",
      "definitionRules": [
        { "id": "v.caller", "tier": "JsonLogic", "scope": "Schema", "scopeTarget": "", "action": "Validate",
          "expression": { "==": [ { "var": "caller" }, "attacker" ] } }
      ],
      "instance": { "caller": "attacker" },
      "expectedCompileError": { "code": "rule.compile.bad_grammar" }
    },
    {
      "name": "scope-root/clock-with-default-refuses-at-compile",
      "definitionRules": [
        { "id": "c.clock", "tier": "JsonLogic", "scope": "Field", "scopeTarget": "at", "action": "Compute",
          "expression": { "var": [ "clock.now", "1970-01-01" ] } }
      ],
      "instance": {},
      "expectedCompileError": { "code": "rule.compile.bad_grammar" }
    },
    {
      "name": "scope-root/record-type-missing-key-refuses-at-compile",
      "definitionRules": [
        { "id": "v.type", "tier": "JsonLogic", "scope": "Schema", "scopeTarget": "", "action": "Validate",
          "expression": { "missing": [ "record_type.id" ] } }
      ],
      "instance": {},
      "expectedCompileError": { "code": "rule.compile.bad_grammar" }
    },
    {
      "name": "scope-root/explicit-field-of-the-same-name-still-reads",
      "definitionRules": [
        { "id": "c.echo", "tier": "JsonLogic", "scope": "Field", "scopeTarget": "echo", "action": "Compute",
          "expression": { "var": "field.caller" } }
      ],
      "instance": { "caller": "front-desk" },
      "expectedAst": { "c.echo": { "var": "field.caller" } },
      "expectedOutcomes": {
        "c.echo": { "ruleId": "c.echo", "target": "field:echo", "outputType": "Value", "value": { "state": "Resolved", "value": "front-desk" } }
      }
    }
  ]
}
""";

    public static IEnumerable<object[]> Cases()
        => ((JsonArray)JsonNode.Parse(Corpus)!["cases"]!)
            .Select(c => new object[] { c!["name"]!.GetValue<string>(), (JsonObject)c.DeepClone() });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Platform_200_case_matches(string name, JsonObject caseObj)
        => new ConformanceTests().Corpus_case_matches_byte_identical("unsupplied-scope-root.json", name, caseObj);
}
