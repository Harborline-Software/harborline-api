using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// Ticket 274 fence: a shell-derived account name must be minted into an <c>ActorId</c> at the
/// derivation, never interpolated into a string that a later <c>new ActorId(...)</c> refuses.
/// The allow-list names every production file that touches a shell account API; each must also
/// call <c>ActorId.Mint</c>, so a new un-minting derivation cannot land silently.
/// </summary>
public sealed class ShellIdentityMintArchTests
{
    private static readonly Regex ShellAccountApi = new(
        @"\b(?:Environment\s*\.\s*User(?:Name|DomainName)|WindowsIdentity\s*\.\s*GetCurrent|UserPrincipalName|processUser)\b",
        RegexOptions.Compiled);

    [Fact]
    public void ProductionShellAccountDerivationsMatchTheCommentedAllowListExactly()
    {
        var root = RepositoryRoot();
        var allowed = ReadAllowList(root);
        Assert.All(allowed, item => Assert.False(string.IsNullOrWhiteSpace(item.Value.Reason)));

        var expectedRows = allowed
            .Select(item => $"{item.Key}\t{item.Value.Count}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualRows = Scan(root)
            .Select(item => $"{item.Path}\t{item.Count}")
            .ToArray();
        Assert.True(
            expectedRows.SequenceEqual(actualRows, StringComparer.Ordinal),
            $"Expected:{Environment.NewLine}{string.Join(Environment.NewLine, expectedRows)}" +
            $"{Environment.NewLine}Actual:{Environment.NewLine}{string.Join(Environment.NewLine, actualRows)}");
    }

    [Fact]
    public void EveryAllowedShellDerivationFileMints()
    {
        var root = RepositoryRoot();
        var missing = ReadAllowList(root).Keys
            .Where(relative => !File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)))
                .Contains("ActorId.Mint", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([], missing);
    }

    [Fact]
    public void FenceReportsAPlantedUnmintedDerivation()
    {
        var root = Path.Combine(Path.GetTempPath(), "ticket-274-shell-" + Guid.NewGuid().ToString("N"));
        var planted = Path.Combine(root, "packages", "planted");
        Directory.CreateDirectory(planted);
        try
        {
            File.WriteAllText(Path.Combine(planted, "Offender.cs"),
                "sealed class Bad { string Id() => $\"os:{Environment.UserName}\"; " +
                "string Dom() => Environment.UserDomainName; }");

            Assert.Equal([("packages/planted/Offender.cs", 2)], Scan(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Dictionary<string, (int Count, string Reason)> ReadAllowList(string root)
    {
        var path = Path.Combine(root, "apps", "local-node-host", "tests", "ArchTests",
            "shell-identity-mint-allowlist.tsv");
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
            .Select(item => (item.Relative,
                Count: ShellAccountApi.Matches(StripComments(File.ReadAllText(item.File))).Count))
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
