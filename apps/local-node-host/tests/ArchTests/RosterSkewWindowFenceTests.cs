using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Harborline.Api.Kernel.Sync.Handshake;
using Harborline.Api.LocalNodeHost.Data.Roster;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.ArchTests;

/// <summary>
/// T-1056/roster-issuedat-skew, R-0149 decision 1 (ruled A, 2026-11-07): a device-signed roster or rehost IssuedAt may
/// be at most ±30 s off its reference, everywhere, using the ONE constant the sync handshake already uses. This fence
/// fails if the roster window drifts from <see cref="HandshakeProtocol.HelloTimestampSkewSeconds"/>, or if a second
/// time literal appears where signed roster time is compared (Data/Roster, Data/Identity and BackupRestore).
/// </summary>
public sealed class RosterSkewWindowFenceTests
{
    /// <summary>Every time literal allowed where signed roster time is compared, by file, with the reviewed reason.</summary>
    private static readonly (string Path, string Expression, int Count, string Reason)[] Allowed =
    [
        ("apps/local-node-host/BackupRestore/SignedRosterRehostGrantProvider.cs", "now.AddMinutes(5)", 1,
            "now.AddMinutes(5) is the issued grant's lifetime (ExpiresAt), not a skew allowance."),
        ("apps/local-node-host/Data/Identity/AccountSetupInvitationIssuer.cs", "TimeSpan.FromHours(24)", 1, "Account setup invitation lifetime."),
        ("apps/local-node-host/Data/Identity/BootstrapClaimRedemption.cs", "TimeSpan.FromMinutes(15)", 1, "Bootstrap claim lifetime ceiling."),
        ("apps/local-node-host/Data/Identity/BootstrapClaimRedemption.cs", "TimeSpan.FromMilliseconds(Math.Min(5 * (attempt + 1), 100))", 1, "Contended claim write retry delay."),
        ("apps/local-node-host/Data/Identity/InstallationFounderBindingService.cs", "TimeSpan.FromMilliseconds(5 * (attempt + 1))", 1, "Contended founder binding write retry delay."),
        ("apps/local-node-host/Data/Identity/InstallationFounderBootstrapCeremony.cs", "TimeSpan.FromMinutes(10)", 1, "Bootstrap claim lifetime."),
        ("apps/local-node-host/Data/Identity/InstallationFounderBootstrapService.cs", "TimeSpan.FromMilliseconds(5 * (attempt + 1))", 1, "Contended founder bootstrap write retry delay."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityCoordinatorRecoveryDaemon.cs", "TimeSpan.FromSeconds(IdentityCoordinatorOptions.DefaultRecoverySweepIntervalSeconds)", 1, "Recovery sweep interval, not roster skew."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityCoordinatorService.cs", "TimeSpan.FromSeconds(30)", 1, "Identity coordinator lease duration."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityCoordinatorService.cs", "TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, Math.Clamp(attempts - 1, 0, 6))))", 1, "Recovery backoff interval."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityCutoverOrchestrator.cs", "TimeSpan.FromSeconds(1)", 1, "Cutover lease duration floor."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityCutoverOrchestrator.cs", "TimeSpan.FromHours(24)", 1, "Cutover lease duration ceiling."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityCutoverOrchestrator.cs", "TimeSpan.FromMilliseconds(5 * (attempt + 1))", 4, "Contended cutover write retry delays."),
        ("apps/local-node-host/Data/Identity/NodeAdministratorAuthority.cs", "TimeSpan.FromMilliseconds(5 * (attempt + 1))", 1, "Contended administrator write retry delay."),
        ("apps/local-node-host/Data/Identity/NodeRunLock.cs", "ShareRetryDelayMilliseconds = 50", 1, "Run-lock sharing-violation retry delay."),
        ("apps/local-node-host/Data/Identity/RecoveryInvitationIssuer.cs", "TimeSpan.FromHours(1)", 1, "Recovery invitation lifetime."),
        ("apps/local-node-host/Data/Identity/WebAccountAccessChallengeIssuer.cs", "TimeSpan.FromMinutes(5)", 1, "Account access challenge lifetime."),
        ("apps/local-node-host/Data/Identity/WebInstallationSessionStore.cs", "TimeSpan.FromMinutes(15)", 1, "Installation session lifetime."),
        ("apps/local-node-host/Data/Identity/WebJoinerAccountMinter.cs", "TimeSpan.FromMilliseconds(5 * (attempt + 1))", 1, "Contended joiner account write retry delay."),
        ("apps/local-node-host/Data/Identity/WebSelectedSessionLogoutAuthority.cs", "TimeSpan.FromSeconds(30)", 1, "Logout lease duration."),
        ("apps/local-node-host/Data/Identity/WebTenantSelectionAuthority.cs", "TimeSpan.FromSeconds(30)", 1, "Tenant selection lease duration."),
        ("apps/local-node-host/Data/Identity/WebTenantSwitchAuthority.cs", "TimeSpan.FromSeconds(30)", 1, "Tenant switch lease duration."),
    ];

    [Fact(DisplayName = "T-1056: the roster skew window is the sync handshake's one constant, 30 s")]
    public void TheRosterWindowIsTheHandshakeConstant()
    {
        Assert.Equal(30, HandshakeProtocol.HelloTimestampSkewSeconds);
        Assert.Equal(TimeSpan.FromSeconds(30), HandshakeProtocol.HelloTimestampSkew);
        Assert.Equal(TimeSpan.FromSeconds(30), NodeRosterRecord.ReceiveTimeWindow);
    }

    [Fact(DisplayName = "T-1056: no second time literal where signed roster time is compared")]
    public void NoSecondSkewLiteral()
    {
        var root = RepositoryRoot();
        var actual = Scan(root);
        var expected = Allowed.Select(a => $"{a.Path}\t{a.Expression}\t{a.Count}").Order(StringComparer.Ordinal).ToArray();
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

    [Fact]
    public void AuthorityCapReadsTheHandshakeWindow()
    {
        var source = StripComments(File.ReadAllText(Path.Combine(RepositoryRoot(),
            "apps", "local-node-host", "Data", "Identity", "EffectiveMemberPermissions.cs")));
        Assert.Matches(@"AdmittedInstant\.ForRosterAuthority\(at,\s*admittedAt,\s*HandshakeProtocol\.HelloTimestampSkew\)", source);
    }

    [Theory]
    [InlineData("Data/Identity", "private const int RosterSkewSeconds = 31;")]
    [InlineData("Data/Identity", "private const int Tolerance = 31;")]
    [InlineData("Data/Identity", "private const int Tolerance = (int)31;")]
    [InlineData("Data/Identity", "private static readonly TimeSpan Grace = new(0, 0, 31);")]
    [InlineData("Data/Identity", "private static readonly int Drift = 31;")]
    [InlineData("Data/Identity", "private int Grace { get; } = 31;")]
    [InlineData("Data/Identity", "private int Grace => 31;")]
    [InlineData("Data/Identity", "private bool Check(double tolerance = 31) => false;")]
    [InlineData("Data/Identity", "private bool Check(System.TimeSpan delta) => delta.TotalSeconds > 31;")]
    [InlineData("Data/Identity", "private bool Check(double a, double b) => Math.Abs(a - b) <= 31;")]
    [InlineData("Data/Identity", "private object Duration() => HandshakeProtocol.HelloTimestampSkew * 2;")]
    [InlineData("Data/Identity", "private bool Check(System.TimeSpan delta) => delta.TotalSeconds is > 31;")]
    [InlineData("Data/Identity", "private bool Check(System.TimeSpan delta) => delta is { TotalSeconds: > 31 };")]
    [InlineData("Data/Identity", "private object Duration() => TS.FromSeconds(31);")]
    [InlineData("Data/Identity", "private object Duration() => FromSeconds(31);")]
    [InlineData("Data/Identity", "private static readonly TimeSpan RosterSkew = TimeSpan.FromSeconds(31);")]
    [InlineData("Data/Identity", "private object Duration() => global::System.TimeSpan.FromSeconds(31);")]
    [InlineData("BackupRestore", "private object Duration() => new System.TimeSpan(0, 0, 31);")]
    [InlineData("Data/Roster", "private const int RosterSkewSeconds = 31;")]
    [InlineData("BackupRestore/Nested", "private const int RosterSkewSeconds = 31;")]
    public void APlantedSecondSkewIsDiscovered(string directory, string declaration)
    {
        var root = Path.Combine(Path.GetTempPath(), $"T-1056-skew-{Guid.NewGuid():N}");
        foreach (var scope in new[] { "Data/Roster", "Data/Identity", "BackupRestore", directory })
            Directory.CreateDirectory(Path.Combine(root, "apps", "local-node-host", scope));
        var folder = Path.Combine(root, "apps", "local-node-host", directory);
        try
        {
            File.WriteAllText(Path.Combine(folder, "Planted.cs"), $"using TS = System.TimeSpan; class Planted {{ {declaration} }}");
            Assert.NotEmpty(Scan(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Data/Roster")]
    [InlineData("Data/Identity")]
    [InlineData("BackupRestore")]
    public void AMissingScopeDirectoryFailsClosed(string missing)
    {
        var root = Path.Combine(Path.GetTempPath(), $"T-1056-skew-{Guid.NewGuid():N}");
        foreach (var directory in new[] { "Data/Roster", "Data/Identity", "BackupRestore" })
            Directory.CreateDirectory(Path.Combine(root, "apps", "local-node-host", directory));
        try
        {
            Directory.Delete(Path.Combine(root, "apps", "local-node-host", missing));
            Assert.Throws<DirectoryNotFoundException>(() => Scan(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string[] Scan(string root)
    {
        var files = new[] { "Data/Roster", "Data/Identity", "BackupRestore" }
            .Select(directory => Path.Combine(root, "apps", "local-node-host", directory))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories));
        return files
            .SelectMany(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot().DescendantNodes()
                .Where(IsTimeLiteral)
                .Select(node => $"{Path.GetRelativePath(root, file).Replace('\\', '/')}\t{node.NormalizeWhitespace().ToFullString()}"))
            .GroupBy(site => site, StringComparer.Ordinal)
            .Select(group => $"{group.Key}\t{group.Count()}")
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsTimeLiteral(SyntaxNode node) => node switch
    {
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } =>
            member.Name.Identifier.ValueText.StartsWith("From", StringComparison.Ordinal) && IsTimeSpan(member.Expression.ToString(), node)
            || member.Name.Identifier.ValueText is "AddSeconds" or "AddMinutes" or "AddHours" or "AddMilliseconds" or "AddTicks",
        InvocationExpressionSyntax { Expression: IdentifierNameSyntax name } =>
            name.Identifier.ValueText is "FromDays" or "FromHours" or "FromMinutes" or "FromSeconds" or "FromMilliseconds" or "FromMicroseconds" or "FromTicks",
        ObjectCreationExpressionSyntax creation => IsTimeSpan(creation.Type.ToString(), node),
        ImplicitObjectCreationExpressionSyntax creation => creation.Ancestors().Select(ancestor => ancestor switch
        {
            VariableDeclarationSyntax variable => variable.Type,
            PropertyDeclarationSyntax property => property.Type,
            MethodDeclarationSyntax method => method.ReturnType,
            _ => null,
        }).FirstOrDefault(type => type is not null) is { } type && IsTimeSpan(type.ToString(), node),
        VariableDeclaratorSyntax { Initializer: { } initializer } variable =>
            IsTimeName(variable.Identifier.ValueText)
                && initializer.Value.DescendantTokens().Any(token => token.IsKind(SyntaxKind.NumericLiteralToken)),
        PropertyDeclarationSyntax property =>
            IsTimeName(property.Identifier.ValueText)
            && (property.Initializer is { } initializer && IsNumber(initializer.Value)
                || property.ExpressionBody is { } body && IsNumber(body.Expression)),
        ParameterSyntax { Default: { } initializer } parameter =>
            IsTimeName(parameter.Identifier.ValueText) && IsNumber(initializer.Value),
        BinaryExpressionSyntax binary =>
            (binary.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(member =>
                member.Name.Identifier.ValueText == "HelloTimestampSkew")
            || binary.Kind() is SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression
                or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
                or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
            && (binary.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(member =>
                IsTimeUnit(member.Name.Identifier.ValueText))
                || binary.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(invocation =>
                    invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Abs" }
                    && invocation.ArgumentList.DescendantNodes().OfType<BinaryExpressionSyntax>()
                        .Any(argument => argument.IsKind(SyntaxKind.SubtractExpression)))))
            && binary.DescendantTokens().Any(token => token.IsKind(SyntaxKind.NumericLiteralToken)),
        IsPatternExpressionSyntax pattern =>
            (pattern.Expression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
                .Any(member => IsTimeUnit(member.Name.Identifier.ValueText))
            || pattern.Pattern.DescendantNodes().OfType<SubpatternSyntax>()
                .Any(property => property.NameColon is { } name && IsTimeUnit(name.Name.Identifier.ValueText)))
            && pattern.Pattern.DescendantTokens().Any(token => token.IsKind(SyntaxKind.NumericLiteralToken)),
        _ => false,
    };

    private static bool IsTimeName(string name) =>
        Regex.IsMatch(name, "Skew|Leeway|Window|Seconds|Tolerance|Drift|Grace", RegexOptions.IgnoreCase);

    private static bool IsNumber(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.NumericLiteralExpression),
        PrefixUnaryExpressionSyntax unary => IsNumber(unary.Operand),
        ParenthesizedExpressionSyntax parenthesized => IsNumber(parenthesized.Expression),
        CastExpressionSyntax cast => IsNumber(cast.Expression),
        BinaryExpressionSyntax binary => IsNumber(binary.Left) && IsNumber(binary.Right),
        _ => false,
    };

    private static bool IsTimeUnit(string name) => name is "TotalDays" or "TotalHours" or "TotalMinutes"
        or "TotalSeconds" or "TotalMilliseconds" or "TotalMicroseconds" or "Ticks";

    private static bool IsTimeSpan(string name, SyntaxNode context) =>
        name is "TimeSpan" or "System.TimeSpan" or "global::System.TimeSpan"
        || context.SyntaxTree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>().Any(directive =>
            directive.Alias?.Name.Identifier.ValueText == name
            && directive.Name?.ToString() is "System.TimeSpan" or "global::System.TimeSpan");

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
