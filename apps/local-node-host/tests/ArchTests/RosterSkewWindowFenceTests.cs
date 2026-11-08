using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.LocalNodeHost.Data.Roster;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-1056/roster-issuedat-skew, R-0149 decision 1 (ruled A, 2026-11-07): a device-signed roster or rehost IssuedAt may
/// be at most ±30 s off its reference, everywhere, using the ONE constant the sync handshake already uses. This fence
/// fails if the roster window drifts from <see cref="HandshakeProtocol.HelloTimestampSkewSeconds"/>, or if a second
/// time literal appears where signed roster time is compared (Data/Roster and the rehost grant provider).
/// </summary>
public sealed class RosterSkewWindowFenceTests
{
    private static readonly Regex TimeLiteral = new(
        @"\bTimeSpan\s*\.\s*From[A-Za-z]+\s*\(|\bnew\s+TimeSpan\s*\(|\.\s*Add(?:Seconds|Minutes|Hours|Milliseconds|Ticks)\s*\(",
        RegexOptions.Compiled);

    /// <summary>Every time literal allowed where signed roster time is compared, by file, with the reviewed reason.</summary>
    private static readonly (string Path, int Count, string Reason)[] Allowed =
    [
        ("apps/local-node-host/BackupRestore/SignedRosterRehostGrantProvider.cs", 1,
            "now.AddMinutes(5) is the issued grant's lifetime (ExpiresAt), not a skew allowance."),
    ];

    [Fact(DisplayName = "T-1056: the roster skew window is the sync handshake's one constant, 30 s")]
    public void TheRosterWindowIsTheHandshakeConstant()
    {
        Assert.Equal(30, HandshakeProtocol.HelloTimestampSkewSeconds);
        Assert.Equal(TimeSpan.FromSeconds(HandshakeProtocol.HelloTimestampSkewSeconds), HandshakeProtocol.HelloTimestampSkew);
        Assert.Equal(HandshakeProtocol.HelloTimestampSkew, NodeRosterRecord.ReceiveTimeWindow);
    }

    [Fact(DisplayName = "T-1056: no second time literal where signed roster time is compared")]
    public void NoSecondSkewLiteral()
    {
        var root = RepositoryRoot();
        var actual = Scan(root);
        var expected = Allowed.Select(a => $"{a.Path}\t{a.Count}").Order(StringComparer.Ordinal).ToArray();
        Assert.All(Allowed, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason)));
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"Expected:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}{Environment.NewLine}" +
            $"Actual:{Environment.NewLine}{string.Join(Environment.NewLine, actual)}{Environment.NewLine}" +
            "A skew bound on a signed roster time must read HandshakeProtocol.HelloTimestampSkew (NodeRosterRecord.ReceiveTimeWindow), never a new value (R-0149 decision 1).");
    }

    [Fact(DisplayName = "T-1056: the rehost not-yet-valid check reads the one window")]
    public void RehostReadsTheWindow()
    {
        var source = StripComments(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "apps", "local-node-host", "BackupRestore", "SignedRosterRehostGrantProvider.cs")));
        Assert.Matches(@"signed\.IssuedAt\s*>\s*now\s*\+\s*(?:[A-Za-z.]+\.)?HandshakeProtocol\.HelloTimestampSkew\)\s*reason\s*=\s*""rehost\.not_yet_valid""", source);
    }

    private static string[] Scan(string root)
    {
        var files = Directory.EnumerateFiles(Path.Combine(root, "apps", "local-node-host", "Data", "Roster"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Append(Path.Combine(root, "apps", "local-node-host", "BackupRestore", "SignedRosterRehostGrantProvider.cs"));
        return files
            .Select(f => (Path: Path.GetRelativePath(root, f).Replace('\\', '/'), Count: TimeLiteral.Matches(StripComments(File.ReadAllText(f))).Count))
            .Where(r => r.Count > 0)
            .Select(r => $"{r.Path}\t{r.Count}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string StripComments(string source)
        => Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"//[^\n]*", string.Empty);

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
