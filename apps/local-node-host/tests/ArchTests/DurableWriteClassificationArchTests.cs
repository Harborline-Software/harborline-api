using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// DES-0029 kernel-core-ck-6: every production durable write site in the host is classified against the
/// transaction-boundary obligation (record and audit atomic, rollback, replay, batch refusal). A new
/// <c>SaveChanges</c>/<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>/<c>ExecuteSql</c>/<c>ExecuteNonQuery</c> site,
/// or a changed count in a classified file, fails until its row in
/// <c>durable-write-classification.tsv</c> is written or updated.
/// </summary>
public sealed class DurableWriteClassificationArchTests
{
    private static readonly Regex WriteCall = new(
        @"\b(?:SaveChanges|ExecuteUpdate|ExecuteDelete|ExecuteSqlRaw|ExecuteSqlInterpolated|ExecuteSql|ExecuteNonQuery)(?:Async)?\s*[(<]",
        RegexOptions.Compiled);

    [Fact]
    public void EveryProductionDurableWriteSiteIsClassifiedWithItsExactCount()
    {
        // The class vocabulary, not an allow-list: every row must use one of these.
        string[] classes = ["atomic-audit", "record-is-audit", "outbox-audit", "post-commit-audit", "unaudited", "n/a"];
        var root = RepositoryRoot();
        var rows = ReadClassification(root);
        Assert.All(rows, row =>
        {
            Assert.True(classes.Contains(row.Value.Class), $"{row.Key}: unknown class '{row.Value.Class}'");
            Assert.False(string.IsNullOrWhiteSpace(row.Value.Evidence), $"{row.Key}: evidence is required");
        });

        var expected = rows.Select(row => $"{row.Key}\t{row.Value.Count}").Order(StringComparer.Ordinal).ToArray();
        var actual = Scan(root).Select(item => $"{item.Path}\t{item.Count}").ToArray();
        Assert.True(
            expected.SequenceEqual(actual, StringComparer.Ordinal),
            "Classify every durable write site in durable-write-classification.tsv. Missing or stale rows:" +
            Environment.NewLine + string.Join(Environment.NewLine, actual.Except(expected).Select(row => "+ " + row)
                .Concat(expected.Except(actual).Select(row => "- " + row))));
    }

    [Fact]
    public void ScanReportsAPlantedUnclassifiedWrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "ck6-write-scan-" + Guid.NewGuid().ToString("N"));
        var planted = Path.Combine(root, "apps", "local-node-host", "Data", "Planted");
        Directory.CreateDirectory(planted);
        try
        {
            File.WriteAllText(Path.Combine(planted, "Offender.cs"),
                "sealed class Offender { async Task Go(DbContext c) { await c.SaveChangesAsync(); " +
                "await c.Set<Row>().ExecuteDeleteAsync(); /* c.SaveChanges(); */ } }");

            Assert.Equal([("apps/local-node-host/Data/Planted/Offender.cs", 2)], Scan(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, (int Count, string Class, string Evidence)> ReadClassification(string root)
    {
        var path = Path.Combine(root, "apps", "local-node-host", "tests", "ArchTests", "durable-write-classification.tsv");
        var result = new Dictionary<string, (int, string, string)>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var columns = line.Split('\t', 4);
            Assert.Equal(4, columns.Length);
            Assert.True(int.TryParse(columns[1], out var count) && count > 0, $"Bad count: {line}");
            Assert.True(result.TryAdd(columns[0], (count, columns[2], columns[3])), $"Duplicate row: {columns[0]}");
        }
        return result;
    }

    private static (string Path, int Count)[] Scan(string root)
    {
        var host = Path.Combine(root, "apps", "local-node-host");
        if (!Directory.Exists(host)) return [];
        return Directory.EnumerateFiles(host, "*.cs", SearchOption.AllDirectories)
            .Select(file => (File: file, Relative: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Where(item => !item.Relative.Split('/').Any(segment =>
                segment is "tests" or "bin" or "obj" or ".git" or ".claude" or "Migrations"))
            .Where(item => !item.Relative.EndsWith(".Designer.cs", StringComparison.Ordinal)
                && !item.Relative.EndsWith(".g.cs", StringComparison.Ordinal))
            .Select(item => (item.Relative, Count: WriteCall.Matches(StripComments(File.ReadAllText(item.File))).Count))
            .Where(item => item.Count > 0)
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .ToArray();
    }

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
