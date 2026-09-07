using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Harborline.Api.Protocol;

namespace Harborline.Api.Protocol.Tests;

/// <summary>Loads the protocol fixture manifest that every projection lane is measured against.</summary>
internal static class ProtocolFixtures
{
    private static readonly string ProtocolRoot = Path.Combine(AppContext.BaseDirectory, "protocol");
    private static readonly string FixtureRoot = Path.Combine(ProtocolRoot, "fixtures");

    internal sealed record Case(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("outcome")] string Outcome,
        [property: JsonPropertyName("constraint")] string? Constraint = null,
        [property: JsonPropertyName("path")] string? Path = null);

    internal sealed record FixtureManifest(
        [property: JsonPropertyName("fixtures")] IReadOnlyList<Case> Fixtures,
        [property: JsonPropertyName("negativeFixtures")] IReadOnlyList<Case> NegativeFixtures);

    internal static FixtureManifest Manifest { get; } =
        JsonSerializer.Deserialize<FixtureManifest>(
            File.ReadAllText(System.IO.Path.Combine(FixtureRoot, "manifest.json")))
        ?? throw new InvalidOperationException("the protocol fixture manifest did not parse");

    internal static JsonNode ReadFixture(string file) =>
        JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(FixtureRoot, file)))
        ?? throw new InvalidOperationException($"{file}: fixture is not a JSON document");

    /// <summary>
    /// The codegen manifest: authority for the protocol identity, the host command surface, and
    /// the application routes that every projection mirrors as constants.
    /// </summary>
    internal static JsonNode ProtocolManifest { get; } =
        JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(ProtocolRoot, "manifest.json")))
        ?? throw new InvalidOperationException("the protocol manifest did not parse");

    internal static IEnumerable<JsonNode> Operations(string port) =>
        ProtocolManifest["ports"]!.AsArray()
            .Where(node => (string?)node!["name"] == port)
            .SelectMany(node => node!["operations"]!.AsArray())
            .Select(node => node!);

    /// <summary>Every public string constant declared on a generated static class.</summary>
    internal static IReadOnlyCollection<string> ConstantsOf(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is {IsLiteral: true, FieldType: {} fieldType} && fieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();

    /// <summary>
    /// The manifest names models in the language-neutral schema vocabulary; every lane resolves
    /// that name into its own type system. A miss here means the schema declares a model this
    /// projection never generated.
    /// </summary>
    internal static Type ResolveModel(string model) =>
        typeof(HarborlineProtocol).Assembly.GetType($"Harborline.Api.Protocol.{model}")
        ?? throw new InvalidOperationException($"the C# projection has no type for protocol model {model}");
}
