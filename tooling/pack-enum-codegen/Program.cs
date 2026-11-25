using System.Text.Json;

using Harborline.Api.Tooling.PackEnumCodegen;
using Harborline.Foundation.Definitions;

if (args.Length > 1 || args.Length == 1 && args[0] != "--check")
{
    Console.Error.WriteLine("Usage: dotnet run --project tooling/pack-enum-codegen [-- --check]");
    return 2;
}

try
{
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "Harborline.Api.slnx")))
        root = root.Parent;
    if (root is null) throw new InvalidDataException("Cannot locate Harborline.Api.slnx.");
    if (!PackContentKindRegistry.VerifyCheckedInExport())
        throw new InvalidDataException("The pinned content-kinds export differs from its registry.");

    var documentation = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(
        Path.Combine(root.FullName, "tooling/pack-enum-codegen/documentation.json")))
        ?? throw new InvalidDataException("Missing enum documentation.");
    var outputs = PackEnumGenerator.Generate(PackContentKindRegistry.LoadCheckedInExport(), documentation);
    return PackEnumGenerator.Run(outputs, root.FullName, args.Contains("--check"), Console.Error);
}
catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
