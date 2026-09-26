using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-664: the field runtime chooses a value-domain field's editor (DES-0030 decision 3), so no host
/// code may map an authored <c>ControlHint</c> string to a control type again. Every production read
/// of the hint is listed here with the reason it is not a dispatch; a new read is red until someone
/// adds a row, and a row whose line is gone is red too, so the list cannot go stale.
/// </summary>
public sealed class ControlHintDispatchFenceTests
{
    /// <summary>(repo-relative path, trimmed code line, occurrences). Each is a pass-through or an admission check.</summary>
    private static readonly (string Path, string Line, int Count)[] AllowedReads =
    [
        // Wire overlay to stored model, and stored model back to the authoring wire.
        ("apps/local-node-host/Health/FormDefinitionRoutes.cs", "ControlHint: kv.Value.ControlHint,", 1),
        ("apps/local-node-host/Health/FormDefinitionRoutes.cs", "f.ControlHint,", 1),
        // Admission: a hint on a value-domain field is refused (T-724 ruling 37); only its presence is read.
        ("apps/local-node-host/Health/FormDefinitionRoutes.cs", "if (string.IsNullOrWhiteSpace(field.ControlHint)", 1),
        // Render wire: a value-domain field's hint is then replaced by the runtime's editor (FieldEditorChoice).
        ("apps/local-node-host/Health/FormsRoutes.cs", "f.ControlHint,", 1),
        ("packages/foundation-forms-engine/FormEngine.cs", "ControlHint: fieldOverlay?.ControlHint,", 2),
        // Render plan (T-752): a write, not a read; the runtime's editor overwrites a value-domain field's hint.
        ("apps/local-node-host/Health/RenderPlanCatalogue.cs", "presentation[\"controlHint\"] = domain.Editor.ToString();", 1),
    ];

    private static readonly Regex HintRead = new(
        @"\.ControlHint\b|\[\s*""controlHint""\s*\]|Property\(\s*""controlHint""",
        RegexOptions.Compiled);

    [Fact(DisplayName = "T-664: no host code dispatches on an authored ControlHint")]
    public void Every_production_read_of_the_control_hint_is_an_allowed_pass_through()
    {
        var root = RepositoryRoot();
        var reads = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(file => (File: file, Relative: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Where(file => !file.Relative.Split('/').Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .SelectMany(file => CodeOnly(File.ReadAllText(file.File))
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => HintRead.IsMatch(line))
                .Select(line => (Path: file.Relative, Line: line)))
            .GroupBy(read => read)
            .ToDictionary(group => group.Key, group => group.Count());

        var expected = AllowedReads.ToDictionary(row => (row.Path, row.Line), row => row.Count);
        var unexpected = reads.Where(read => !expected.TryGetValue(read.Key, out var count) || count != read.Value)
            .Select(read => $"{read.Key.Path}: `{read.Key.Line}` x{read.Value}");
        var stale = expected.Where(row => !reads.ContainsKey(row.Key))
            .Select(row => $"{row.Key.Path}: `{row.Key.Line}`");

        Assert.True(!unexpected.Any() && !stale.Any(),
            "ControlHint reads must be pass-throughs or the admission check. Unexpected: ["
            + string.Join(" | ", unexpected) + "]; stale allow rows: [" + string.Join(" | ", stale) + "]");
    }

    private static readonly string[] ExcludedSegments =
        ["tests", "obj", "bin", "node_modules", ".git", ".claude", ".codex", "artifacts", ".feed"];

    private static string CodeOnly(string source)
    {
        var withoutBlockComments = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(withoutBlockComments, @"//.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var hostRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;
        var repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(hostRoot)!)!;
        Assert.True(File.Exists(Path.Combine(repositoryRoot, "Harborline.Api.slnx")),
            $"Could not locate the repository root from '{thisFile}'.");
        return repositoryRoot;
    }
}
