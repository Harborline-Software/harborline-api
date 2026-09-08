using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>Ticket 216 fence: only the host composition root may introduce wall time.</summary>
public sealed class KernelClockArchTests
{
    private static readonly Regex ForbiddenWallTime = new(
        @"(?:\b(?:DateTime(?:Offset)?\s*\.\s*(?:Now|UtcNow)|TimeProvider\s*\.\s*System|Environment\s*\.\s*TickCount(?:64)?)|:\s*TimeProvider\b)",
        RegexOptions.Compiled);

    private static readonly Regex ClockRegistration = new(
        @"\b(?:(?:TryAdd|Add)Singleton\s*(?:<\s*TimeProvider\s*>)?\s*\(\s*TimeProvider\s*\.\s*System|TryAddSingleton\s*\(\s*rootTimeProvider)",
        RegexOptions.Compiled);

    [Fact]
    public void ProductionWallTimeReferencesMatchTheCommentedAllowListExactly()
    {
        var root = RepositoryRoot();
        var allowed = ReadAllowList(root);
        var actual = Scan(root).ToDictionary(item => item.Path, item => item.Count, StringComparer.Ordinal);

        Assert.All(allowed, item => Assert.False(string.IsNullOrWhiteSpace(item.Value.Reason)));
        var expectedRows = allowed
            .Select(item => $"{item.Key}\t{item.Value.Count}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualRows = actual
            .Select(item => $"{item.Key}\t{item.Value}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            expectedRows.SequenceEqual(actualRows, StringComparer.Ordinal),
            $"Expected:{Environment.NewLine}{string.Join(Environment.NewLine, expectedRows)}" +
            $"{Environment.NewLine}Actual:{Environment.NewLine}{string.Join(Environment.NewLine, actualRows)}");
    }

    [Fact]
    public void OnlyProgramRegistersTheProductionTimeProvider()
    {
        var registrations = ProductionFiles(RepositoryRoot())
            .Where(item => ClockRegistration.IsMatch(StripComments(File.ReadAllText(item.File))))
            .Select(item => item.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["apps/local-node-host/Program.cs"], registrations);
        var program = StripComments(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "apps", "local-node-host", "Program.cs")));
        Assert.Single(Regex.Matches(program, @"TimeProvider\s*\.\s*System").Cast<Match>());
    }

    [Fact]
    public void WallTimeFenceReportsAPlantedPackageClock()
    {
        var root = Path.Combine(Path.GetTempPath(), "ticket-216-clock-" + Guid.NewGuid().ToString("N"));
        var planted = Path.Combine(root, "packages", "planted");
        Directory.CreateDirectory(planted);
        try
        {
            File.WriteAllText(Path.Combine(planted, "Offender.cs"),
                "sealed class BadClock : TimeProvider { long N() => Environment.TickCount64; " +
                "DateTimeOffset U() => TimeProvider.System.GetUtcNow(); DateTime L() => DateTime.Now; }");

            Assert.Equal([("packages/planted/Offender.cs", 4)], Scan(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, (int Count, string Reason)> ReadAllowList(string root)
    {
        var path = Path.Combine(root, "apps", "local-node-host", "tests", "ArchTests",
            "kernel-clock-allowlist.tsv");
        var result = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var columns = line.Split('\t', 3);
            Assert.Equal(3, columns.Length);
            Assert.True(int.TryParse(columns[1], out var count) && count > 0);
            Assert.True(result.TryAdd(columns[0], (count, columns[2])), $"Duplicate allow-list path: {columns[0]}");
        }
        return result;
    }

    private static (string Path, int Count)[] Scan(string root) =>
        ProductionFiles(root)
            .Where(item => item.Relative != "apps/local-node-host/Program.cs")
            .Select(item => (item.Relative,
                Count: ForbiddenWallTime.Matches(StripComments(File.ReadAllText(item.File))).Count))
            .Where(item => item.Count > 0)
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .Select(item => (item.Relative, item.Count))
            .ToArray();

    private static IEnumerable<(string File, string Relative)> ProductionFiles(string root) =>
        new[] { "packages", "apps" }
            .Select(name => Path.Combine(root, name))
            .Where(Directory.Exists)
            .SelectMany(scanRoot => Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
            .Select(file => (File: file, Relative: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Where(item => !item.Relative.Split('/').Any(segment =>
                segment is "tests" or "bin" or "obj" or ".git" or ".claude"))
            .Where(item => !item.Relative.EndsWith(".Designer.cs", StringComparison.Ordinal)
                && !item.Relative.EndsWith(".g.cs", StringComparison.Ordinal)
                && !item.Relative.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase));

    private static string StripComments(string source) => Regex.Replace(
        source,
        @"//.*?$|/\*.*?\*/",
        string.Empty,
        RegexOptions.Multiline | RegexOptions.Singleline);

    private static string RepositoryRoot([CallerFilePath] string file = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(file)!);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "apps"))
                && Directory.Exists(Path.Combine(directory.FullName, "packages")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
