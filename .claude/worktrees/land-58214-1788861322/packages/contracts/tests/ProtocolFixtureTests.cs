using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harborline.Api.Protocol.Tests;

/// <summary>
/// Executes the language-neutral protocol fixture manifest against the generated C# projection.
/// The TypeScript lane (packages/contracts/src/__tests__/protocol-fixtures.test.ts) and the Rust
/// lane (packages/contracts/rust/src/lib.rs) already ran this manifest; this lane did not, which
/// left the projection that actually serves the API as the only one never measured against the
/// wire contract it implements.
/// </summary>
public sealed class ProtocolFixtureTests
{
    [Theory]
    [MemberData(nameof(AcceptedCases))]
    public void AcceptsFixtureAndRoundTripsItUnchanged(string model, string file)
    {
        var expected = ProtocolFixtures.ReadFixture(file);
        var type = ProtocolFixtures.ResolveModel(model);

        var parsed = JsonSerializer.Deserialize(expected.ToJsonString(), type);
        Assert.NotNull(parsed);

        var actual = JsonNode.Parse(JsonSerializer.Serialize(parsed, type));
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"{file}: round-trip diverged from the fixture.\nexpected: {expected.ToJsonString()}\nactual:   {actual?.ToJsonString()}");
    }

    [Theory]
    [MemberData(nameof(RejectedCases))]
    public void RejectsFixtureForItsDeclaredConstraint(string model, string file, string constraint, string path)
    {
        var candidate = ProtocolFixtures.ReadFixture(file);
        var type = ProtocolFixtures.ResolveModel(model);

        var error = Record.Exception(() => JsonSerializer.Deserialize(candidate.ToJsonString(), type));
        Assert.True(error is not null, $"{file}: the projection accepted a payload violating {constraint}");

        var diagnostic = $"{error!.GetType().Name}: {error.Message}";
        var comparison = PathComparisonFor(constraint);
        if (comparison is not null)
        {
            Assert.True(
                error.Message.Contains(path, comparison.Value),
                $"{file}: the diagnostic must name the offending property {path}, but was -- {diagnostic}");
        }

        Assert.True(
            DescribesConstraint(error, constraint),
            $"{file}: the diagnostic must describe the declared constraint {constraint}, but was -- {diagnostic}");
    }

    /// <summary>
    /// How this lane's diagnostic refers to the offending property, or null where it does not name
    /// one at all. The manifest declares the constraint and the wire path; it deliberately does not
    /// declare wording, because each projection words its own diagnostics.
    /// </summary>
    private static StringComparison? PathComparisonFor(string constraint) => constraint switch
    {
        // The generated validators quote the wire property name verbatim.
        "required" or "unknown-property" => StringComparison.Ordinal,
        // The payload carried a mis-cased name; the validator reports the canonical wire name.
        "exact-case" => StringComparison.OrdinalIgnoreCase,
        // Range violations surface from the record's property setter, so the diagnostic names the
        // PascalCase C# member rather than the camelCase wire property.
        "minimum" or "maximum" => StringComparison.OrdinalIgnoreCase,
        // KNOWN DIAGNOSTIC GAP, not a conformance failure: the generated enum converter throws
        // "invalid <EnumType>" and never names the property that carried the value. TypeScript
        // reports "<path>.kind: outside enum". The rejection is correct; only the message is
        // less specific, so this lane asserts the rejection and its reason but cannot assert
        // the path. Tightening it means changing the generator, which is a separate change.
        "enum" => null,
        _ => throw new InvalidOperationException($"unsupported declared constraint {constraint}"),
    };

    private static bool DescribesConstraint(Exception error, string constraint) => constraint switch
    {
        "required" => error.Message.Contains("missing required property", StringComparison.Ordinal),
        "enum" => error.Message.Contains("invalid ", StringComparison.Ordinal),
        "exact-case" => error.Message.Contains("must match exact wire casing", StringComparison.Ordinal),
        "unknown-property" => error.Message.Contains("unexpected property", StringComparison.Ordinal),
        "minimum" or "maximum" => error is ArgumentOutOfRangeException,
        _ => throw new InvalidOperationException($"unsupported declared constraint {constraint}"),
    };

    public static TheoryData<string, string> AcceptedCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var testCase in ProtocolFixtures.Manifest.Fixtures) data.Add(testCase.Model, testCase.File);
        return data;
    }

    public static TheoryData<string, string, string, string> RejectedCases()
    {
        var data = new TheoryData<string, string, string, string>();
        foreach (var testCase in ProtocolFixtures.Manifest.NegativeFixtures)
        {
            data.Add(
                testCase.Model,
                testCase.File,
                testCase.Constraint ?? throw new InvalidOperationException($"{testCase.File} declares no constraint"),
                testCase.Path ?? throw new InvalidOperationException($"{testCase.File} names no offending property"));
        }
        return data;
    }
}
