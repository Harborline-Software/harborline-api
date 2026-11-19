using System.Text;

using Harborline.Api.Tooling.PackEnumCodegen;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackEnumCodegenTests
{
    private const string KindPath = "packages/foundation-packs/Model/PackContentKind.g.cs";
    private const string PillarPath = "packages/foundation-packs/Graph/PackPillar.g.cs";

    // Synthetic registry and documentation, independent of the pinned registry and generated files.
    private const string Export = """
        {"schemaVersion":1,"pillars":[{"name":"Other","value":99},{"name":"Forms","value":1}],
         "kinds":[{"name":"Held","value":20,"pillar":"Other","status":"reserved"},
                  {"name":"Fixture","value":7,"pillar":"Forms","status":"shipped"}]}
        """;

    private static Dictionary<string, string> Documentation() => new()
    {
        ["PackContentKind"] = "/// <summary>Fixture kinds.</summary>",
        ["PackContentKind.Fixture"] = "/// <summary>A fixture.</summary>",
        ["PackContentKind.Held"] = "/// <summary>Documentation cannot turn a reserved row into a member.</summary>",
        ["PackContentKind.$footer"] = "// No grants.",
        ["PackPillar"] = "/// <summary>Fixture pillars.</summary>",
        ["PackPillar.Forms"] = "/// <summary>Forms.</summary>",
        ["PackPillar.Other"] = "/// <summary>Other.</summary>",
    };

    [Fact]
    public void Fixture_rows_generate_literal_values_documentation_and_reserved_list()
    {
        var output = PackEnumGenerator.Generate(Encoding.UTF8.GetBytes(Export), Documentation());

        var kind = Encoding.UTF8.GetString(output[KindPath]);
        Assert.Contains("/// <summary>A fixture.</summary>\n    Fixture = 7,", kind);
        Assert.Contains("// Reserved content kinds (never enum members): Held = 20", kind);
        Assert.DoesNotContain("\n    Held =", kind);
        Assert.Contains("// No grants.", kind);
        var pillar = Encoding.UTF8.GetString(output[PillarPath]);
        Assert.Contains("Forms = 1,", pillar);
        Assert.Contains("Other = 99,", pillar);
        Assert.True(pillar.IndexOf("Forms =", StringComparison.Ordinal)
            < pillar.IndexOf("Other =", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("PackContentKind.Fixture")]
    [InlineData("PackPillar.Forms")]
    public void Undocumented_member_refuses_generation(string key)
    {
        var documentation = Documentation();
        documentation.Remove(key);

        var error = Assert.Throws<InvalidDataException>(() =>
            PackEnumGenerator.Generate(Encoding.UTF8.GetBytes(Export), documentation));

        Assert.Equal($"Missing documentation: {key}", error.Message);
    }

    [Theory]
    [InlineData(7, 8, KindPath)]
    [InlineData(99, 100, PillarPath)]
    public void Changed_fixture_value_fails_check_names_the_file_and_writes_nothing(
        int oldValue, int newValue, string stalePath)
    {
        var root = Path.Combine(Path.GetTempPath(), "pack-enum-codegen-tests-" + Guid.NewGuid().ToString("N"));
        var original = PackEnumGenerator.Generate(Encoding.UTF8.GetBytes(Export), Documentation());
        Assert.Equal(0, PackEnumGenerator.Run(original, root, check: false, TextWriter.Null));
        var before = original.Keys.ToDictionary(path => path, path => File.ReadAllBytes(Path.Combine(root, path)));
        var changed = PackEnumGenerator.Generate(Encoding.UTF8.GetBytes(Export.Replace($"\"value\":{oldValue}", $"\"value\":{newValue}",
            StringComparison.Ordinal)), Documentation());
        using var diagnostics = new StringWriter();

        Assert.Equal(1, PackEnumGenerator.Run(changed, root, check: true, diagnostics));

        Assert.Equal($"Generated pack enum is stale: {stalePath}\n", diagnostics.ToString().ReplaceLineEndings("\n"));
        Assert.All(before, row => Assert.Equal(row.Value, File.ReadAllBytes(Path.Combine(root, row.Key))));
        Assert.Equal(0, PackEnumGenerator.Run(original, root, check: true, TextWriter.Null));
        Assert.Equal(0, PackEnumGenerator.Run(original, root, check: false, TextWriter.Null));
        Assert.All(before, row => Assert.Equal(row.Value, File.ReadAllBytes(Path.Combine(root, row.Key))));
    }

    [Fact]
    public void Missing_outputs_fail_check_naming_both_files_without_creating_a_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "pack-enum-codegen-tests-" + Guid.NewGuid().ToString("N"));
        var output = PackEnumGenerator.Generate(Encoding.UTF8.GetBytes(Export), Documentation());
        using var diagnostics = new StringWriter();

        Assert.Equal(1, PackEnumGenerator.Run(output, root, check: true, diagnostics));

        Assert.Equal($"Generated pack enum is stale: {KindPath}\nGenerated pack enum is stale: {PillarPath}\n",
            diagnostics.ToString().ReplaceLineEndings("\n"));
        Assert.False(Directory.Exists(root));
    }
}
