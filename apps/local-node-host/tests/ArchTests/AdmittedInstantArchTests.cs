using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Harborline.Api.Foundation.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-1015 (DES-0029 ck-5 and ck-9, board finding F2): no public path builds a gate request from a raw instant.
/// The request and the write context take only an <see cref="AdmittedInstant"/>; that type is minted from the
/// kernel clock, and every production mint from a raw <see cref="DateTimeOffset"/> (a stored act's recorded
/// instant, or a named owed exemption) is pinned here, so a new one fails until it is reviewed.
/// </summary>
public sealed class AdmittedInstantArchTests
{
    /// <summary>Every production raw-to-admitted mint, by file and kind, with the reviewed reason.</summary>
    private static readonly (string Path, string Kind, int Count, string Reason)[] ReviewedMints =
    [
        ("apps/local-node-host/Data/Configuration/VerificationCandidateWorld.cs", "Exempt:ConfigurationRehearsal", 1,
            "Owed (T-1015 slice 3): the verification candidate world seeds and installs at the fixture's rehearsal instant."),
        ("apps/local-node-host/Data/Configuration/VerificationRunner.cs", "Exempt:ConfigurationRehearsal", 1,
            "Owed (T-1015 slice 3): verification decides the rehearsed create at the fixture's rehearsal instant."),
        ("apps/local-node-host/Data/Identity/EffectiveMemberPermissions.cs", "Exempt:SignedRosterIssuedAt", 1,
            "Owed (T-1015 slice 3): roster sync reads install-root authority at the device-signed roster IssuedAt (ADR-0053)."),
        ("apps/local-node-host/Layout/LayoutDenialOutbox.cs", "FromRecordedAct", 1,
            "The appender replays a stored denial at the OccurredAt the server clock wrote into its outbox row."),
        ("packages/foundation-packs/Install/PackProjectionAuthority.cs", "FromRecordedAct", 1,
            "Boot replay of a stored pack projection admission carries the activation instant recorded at install."),
    ];

    private static readonly Regex Mint = new(
        @"\bAdmittedInstant\s*\.\s*(?<kind>FromRecordedAct|Exempt)\b(?:\s*\([^;]*?\bAdmittedInstantExemption\s*\.\s*(?<exemption>\w+))?",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex StaticImport = new(
        @"\busing\s+static\s+[\w.]*\bAdmittedInstant\s*;", RegexOptions.Compiled);

    [Fact]
    public void AdmittedInstant_IsMintedOnlyByItsThreeNamedFactories()
    {
        var type = typeof(AdmittedInstant);
        Assert.DoesNotContain(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            ctor => !ctor.IsPrivate);
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name is "op_Implicit" or "op_Explicit");
        Assert.DoesNotContain(type.GetProperties(), property => property.SetMethod is { IsPrivate: false });

        var factories = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.ReturnType == type && !method.IsPrivate)
            .Select(Signature)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
        [
            "Exempt(DateTimeOffset,AdmittedInstantExemption)",
            "FromRecordedAct(DateTimeOffset)",
            "Read(TimeProvider)",
        ], factories);
    }

    [Theory]
    [InlineData(typeof(AuthorizationGateRequest))]
    [InlineData(typeof(AuthorizationWriteContext))]
    public void GateRequestAndWriteContext_AcceptNoRawDecisionInstant(Type type)
    {
        var rawConstructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(ctor => !ctor.IsPrivate && ctor.GetParameters().Any(parameter => IsRawInstant(parameter.ParameterType)))
            .Select(ctor => ctor.ToString())
            .ToArray();
        var rawSetters = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => IsRawInstant(property.PropertyType) && property.SetMethod is { IsPrivate: false })
            .Select(property => property.Name)
            .ToArray();
        var rawFields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(field => IsRawInstant(field.FieldType) && !field.IsPrivate)
            .Select(field => field.Name)
            .ToArray();

        Assert.Empty(rawConstructors);
        Assert.Empty(rawSetters);
        Assert.Empty(rawFields);
        // The decision instant itself is the admitted type, and it can be set only to another admitted instant.
        Assert.Equal(typeof(AdmittedInstant), type.GetProperty("Instant")!.PropertyType);
    }

    [Fact]
    public void ProductionRawInstantMintsMatchTheReviewedListExactly()
    {
        var expected = ReviewedMints
            .Select(row => $"{row.Path}\t{row.Kind}\t{row.Count}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.All(ReviewedMints, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason)));

        var actual = Scan(RepositoryRoot());
        Assert.True(expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"Expected:{Environment.NewLine}{string.Join(Environment.NewLine, expected)}" +
            $"{Environment.NewLine}Actual:{Environment.NewLine}{string.Join(Environment.NewLine, actual)}");
    }

    [Fact]
    public void MintScanReportsPlantedRawPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "t1015-mint-" + Guid.NewGuid().ToString("N"));
        var planted = Path.Combine(root, "packages", "planted");
        Directory.CreateDirectory(planted);
        try
        {
            File.WriteAllText(Path.Combine(planted, "Offender.cs"),
                "using static Harborline.Api.Foundation.Authorization.AdmittedInstant;\n" +
                "class C { object A(System.DateTimeOffset at) => AdmittedInstant.FromRecordedAct(at);\n" +
                "object B(System.DateTimeOffset at) => AdmittedInstant.Exempt(at,\n AdmittedInstantExemption.SignedRosterIssuedAt);\n" +
                "System.Func<System.DateTimeOffset, object> D() => AdmittedInstant.FromRecordedAct; }");

            Assert.Equal(
            [
                "packages/planted/Offender.cs\tExempt:SignedRosterIssuedAt\t1",
                "packages/planted/Offender.cs\tFromRecordedAct\t2",
                "packages/planted/Offender.cs\tusing static\t1",
            ], Scan(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string[] Scan(string root) =>
        ProductionFiles(root)
            .SelectMany(item =>
            {
                var source = StripComments(File.ReadAllText(item.File));
                return Mint.Matches(source)
                    .Select(match => match.Groups["kind"].Value == "Exempt"
                        ? "Exempt:" + (match.Groups["exemption"].Success ? match.Groups["exemption"].Value : "?")
                        : match.Groups["kind"].Value)
                    .Concat(StaticImport.Matches(source).Select(_ => "using static"))
                    .Select(kind => (item.Relative, Kind: kind));
            })
            .GroupBy(row => row)
            .Select(group => $"{group.Key.Relative}\t{group.Key.Kind}\t{group.Count()}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static bool IsRawInstant(Type type) =>
        type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?) || type == typeof(DateTime) || type == typeof(DateTime?);

    private static string Signature(MethodInfo method) =>
        $"{method.Name}({string.Join(",", method.GetParameters().Select(parameter => parameter.ParameterType.Name))})";

    private static IEnumerable<(string File, string Relative)> ProductionFiles(string root) =>
        new[] { "packages", "apps" }
            .Select(name => Path.Combine(root, name))
            .Where(Directory.Exists)
            .SelectMany(scanRoot => Directory.EnumerateFiles(scanRoot, "*.cs", SearchOption.AllDirectories))
            .Select(file => (File: file, Relative: Path.GetRelativePath(root, file).Replace('\\', '/')))
            .Where(item => !item.Relative.Split('/').Any(segment =>
                segment is "tests" or "bin" or "obj" or ".git" or ".claude"));

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
