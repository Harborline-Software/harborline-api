using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 151 slice 2 — the traceability fence over the record-write-path spec ids (RW-1..RW-9).
/// <para>
/// Every id must be claimed twice: once by a PRODUCTION enforcement site (a <c>// holds RW-n</c> comment
/// on the code that enforces it) and once by a TEST (a <c>[Trait("Holds", "RW-n")]</c> or the id in the
/// test's DisplayName). The fence fails naming the id that lost one of the two, so a spec row nobody
/// tagged — or an enforcement site deleted in a later slice — is a red test, not a quiet gap in a table.
/// </para>
/// </summary>
public sealed class AcceptanceTraceabilityArchTests
{
    /// <summary>The record-write-path spec's requirement ids (control repo specs/record-write-path/spec.md).</summary>
    internal static readonly string[] AcceptanceIds =
        [.. Enumerable.Range(1, 9).Select(n => $"RW-{n}")];

    [Fact(DisplayName = "151 s2: every RW id is claimed by a production site and by a test")]
    public void EveryAcceptanceId_HasAProductionSiteAndATest()
    {
        var root = RepositoryRoot();
        var production = Read(EnumerateSource(Path.Combine(root, "apps")).Concat(
            EnumerateSource(Path.Combine(root, "packages"))));
        var tests = Read(EnumerateSource(Path.Combine(root, "apps", "local-node-host", "tests"), tests: true));

        var missing = AcceptanceIds
            .Select(id => (
                id,
                sites: production.Count(entry => Holds(entry.Text, id)),
                rows: tests.Count(entry => Claims(entry.Text, id))))
            .Where(row => row.sites == 0 || row.rows == 0)
            .Select(row => $"{row.id}: {row.sites} production site(s), {row.rows} test(s)")
            .ToArray();

        Assert.True(missing.Length == 0,
            "Every record-write-path id needs a '// holds RW-n' comment on the code that enforces it and a "
            + "test that declares it ([Trait(\"Holds\", \"RW-n\")] or the id in its DisplayName):"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>A production enforcement site: <c>// holds RW-n</c> (RW-Hn hazard notes do not count).</summary>
    private static bool Holds(string source, string id) =>
        Regex.IsMatch(source, $@"//[^\r\n]*\bholds\b[^\r\n]*\b{Regex.Escape(id)}\b", RegexOptions.CultureInvariant);

    private static bool Claims(string source, string id) =>
        Regex.IsMatch(source, $@"Trait\(""Holds"",\s*""{Regex.Escape(id)}""\)", RegexOptions.CultureInvariant)
        || Regex.IsMatch(source, $@"DisplayName = ""[^""\r\n]*\b{Regex.Escape(id)}\b", RegexOptions.CultureInvariant);

    private static (string Path, string Text)[] Read(IEnumerable<string> files) =>
        [.. files.Select(file => (file, File.ReadAllText(file)))];

    private static IEnumerable<string> EnumerateSource(string root, bool tests = false) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)
                    && (tests || !file.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
                        StringComparison.Ordinal)))
            : [];

    private static string RepositoryRoot([CallerFilePath] string thisFile = "")
    {
        var hostRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;
        var repositoryRoot = Path.GetDirectoryName(Path.GetDirectoryName(hostRoot)!)!;
        Assert.True(
            Directory.Exists(Path.Combine(repositoryRoot, "apps"))
                && Directory.Exists(Path.Combine(repositoryRoot, "packages")),
            $"Could not locate the repository root from '{thisFile}'.");
        return repositoryRoot;
    }
}
