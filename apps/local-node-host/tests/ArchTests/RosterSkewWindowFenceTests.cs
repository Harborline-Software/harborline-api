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
        ("apps/local-node-host/Data/Identity/AccountSetupAcceptanceService.cs", "MaximumUsernameConflictDisclosures = 3", 1, "Username conflict disclosure count."),
        ("apps/local-node-host/Data/Identity/AdministratorAuthorityRecord.cs", "i = 0", 1, "Canonical payload iteration index."),
        ("apps/local-node-host/Data/Identity/AdministratorRecoveryCommand.cs", "i = 0", 1, "Recovery argument iteration index."),
        ("apps/local-node-host/Data/Identity/BootstrapClaimRedemption.cs", "BusyRetryCount = 100", 1, "Contended claim retry count."),
        ("apps/local-node-host/Data/Identity/BootstrapClaimRedemption.cs", "attempt = 0", 1, "Contended claim retry index."),
        ("apps/local-node-host/Data/Identity/GrantProjectionResolver.cs", "firstIndex = 0", 1, "Grant ledger scan index."),
        ("apps/local-node-host/Data/Identity/InstallationAuditIntegrity.cs", "index = 0", 1, "Audit entry iteration index."),
        ("apps/local-node-host/Data/Identity/InstallationFounderBindingService.cs", "BusyRetryCount = 8", 1, "Contended founder binding retry count."),
        ("apps/local-node-host/Data/Identity/InstallationFounderBindingService.cs", "MaximumIdempotencyKeyLength = 512", 1, "Idempotency key length limit."),
        ("apps/local-node-host/Data/Identity/InstallationFounderBindingService.cs", "attempt = 0", 1, "Contended founder binding retry index."),
        ("apps/local-node-host/Data/Identity/InstallationFounderBootstrapService.cs", "BusyRetryCount = 8", 1, "Contended founder bootstrap retry count."),
        ("apps/local-node-host/Data/Identity/InstallationFounderBootstrapService.cs", "attempt = 0", 1, "Contended founder bootstrap retry index."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityCoordinatorService.cs", "CoordinatorPayloadSchemaVersion = 2", 1, "Coordinator payload schema version."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityCoordinatorService.cs", "attempted = 0", 1, "Recovery attempts accumulator."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityCoordinatorService.cs", "int limit = 100", 1, "Recovery batch item limit."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityCutoverOrchestrator.cs", "BusyRetryCount = 8", 1, "Contended cutover retry count."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityCutoverOrchestrator.cs", "attempt = 0", 4, "Contended cutover retry indices."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityRecords.cs", "LegacyV1AuthorityVersion = 1", 1, "Legacy authority format version."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityRecords.cs", "Revision3AuthorityVersion = 2", 1, "Authority format version."),
        ("apps/local-node-host/Data/Identity/InstallationIdentityRecords.cs", "Revision3V2AuthorityVersion = 2", 1, "Authority format version alias."),
        ("apps/local-node-host/Data/Identity/LegacyIdentityMigrationService.cs", "ImmutableOptionsOwnerVersion = 0", 1, "Immutable options ownership version."),
        ("apps/local-node-host/Data/Identity/LegacyIdentityMigrationService.cs", "created = 0", 1, "Migration creation accumulator."),
        ("apps/local-node-host/Data/Identity/LegacyIdentityMigrationService.cs", "int classifiedCandidateCount = 0", 1, "Migration candidate count default."),
        ("apps/local-node-host/Data/Identity/LegacyIdentityMigrationService.cs", "replayed = 0", 1, "Migration replay accumulator."),
        ("apps/local-node-host/Data/Identity/NodeAdministratorAuthority.cs", "BusyRetryCount = 8", 1, "Contended administrator retry count."),
        ("apps/local-node-host/Data/Identity/NodeAdministratorAuthority.cs", "attempt = 0", 1, "Contended administrator retry index."),
        ("apps/local-node-host/Data/Identity/NodeRunLock.cs", "ShareRetryCount = 4", 1, "Run-lock sharing retry count."),
        ("apps/local-node-host/Data/Identity/NodeRunLock.cs", "attempt = 0", 1, "Run-lock sharing retry index."),
        ("apps/local-node-host/Data/Identity/RecoverySessionRevoker.cs", "staged = 0", 1, "Revoked session count."),
        ("apps/local-node-host/Data/Identity/TenantMembershipAuthorityStore.cs", "CasRetryLimit = 16", 1, "Membership compare-and-swap retry count."),
        ("apps/local-node-host/Data/Identity/TenantMembershipAuthorityStore.cs", "SchemaVersion = 3", 1, "Membership document schema version."),
        ("apps/local-node-host/Data/Identity/TenantMembershipAuthorityStore.cs", "SerializedDocumentByteCeiling = 4 * 1024 * 1024", 1, "Membership document size ceiling."),
        ("apps/local-node-host/Data/Identity/TenantMembershipAuthorityStore.cs", "attempt = 0", 1, "Membership compare-and-swap retry index."),
        ("apps/local-node-host/Data/Identity/TenantMembershipAuthorityStore.cs", "sequence = 0", 1, "Membership append sequence start."),
        ("apps/local-node-host/Data/Identity/WebAccountAccessChallengeIssuer.cs", "HandleByteLength = 32", 1, "Challenge handle entropy length."),
        ("apps/local-node-host/Data/Identity/WebAccountAccessChallengeIssuer.cs", "MaxPasswordLength = 4096", 1, "Password length limit."),
        ("apps/local-node-host/Data/Identity/WebAntiforgeryStateStore.cs", "MaximumCoordinateLength = 128", 1, "Antiforgery coordinate length limit."),
        ("apps/local-node-host/Data/Identity/WebAntiforgeryStateStore.cs", "MaximumTokenLength = 256", 1, "Antiforgery token length limit."),
        ("apps/local-node-host/Data/Identity/WebAntiforgeryStateStore.cs", "TokenByteLength = 32", 1, "Antiforgery token entropy length."),
        ("apps/local-node-host/Data/Identity/WebChosenCredentialFactory.cs", "MaxPasswordLength = 4096", 1, "Password length limit."),
        ("apps/local-node-host/Data/Identity/WebInstallationSessionStore.cs", "MaximumCoordinateLength = 128", 1, "Installation session coordinate length limit."),
        ("apps/local-node-host/Data/Identity/WebInstallationSessionStore.cs", "Sha256HexLength = 64", 1, "Session digest encoding length."),
        ("apps/local-node-host/Data/Identity/WebJoinerAccountMinter.cs", "BusyRetryCount = 8", 1, "Contended joiner account retry count."),
        ("apps/local-node-host/Data/Identity/WebJoinerAccountMinter.cs", "attempt = 0", 1, "Contended joiner account retry index."),
        ("apps/local-node-host/Data/Identity/WebSelectedSessionLogoutAuthority.cs", "PayloadSchemaVersion = 1", 1, "Logout payload schema version."),
        ("apps/local-node-host/Data/Identity/WebSelectedSessionStore.cs", "MaximumCoordinateLength = 128", 1, "Selected session coordinate length limit."),
        ("apps/local-node-host/Data/Identity/WebSelectedSessionStore.cs", "Sha256HexLength = 64", 1, "Selected session digest encoding length."),
        ("apps/local-node-host/Data/Identity/WebSelectedSessionStore.cs", "attempt = 0", 1, "Selected session compare-and-swap retry index."),
        ("apps/local-node-host/Data/Identity/WebTenantSelectionAuthority.cs", "PayloadSchemaVersion = 1", 1, "Tenant selection payload schema version."),
        ("apps/local-node-host/Data/Identity/WebTenantSwitchAuthority.cs", "PayloadSchemaVersion = 1", 1, "Tenant switch payload schema version."),
        ("apps/local-node-host/Data/Identity/WebUsernameNormalizer.cs", "MaxUsernameLength = 320", 1, "Username length limit."),
        ("apps/local-node-host/Data/Roster/HydrationRosterVerifier.cs", "Capacity = 4096", 1, "Hydration verifier cache capacity."),
        ("apps/local-node-host/Data/Roster/RosterCrdtProjection.cs", "IdenticalRecord = -2", 1, "Duplicate record comparison sentinel."),
        ("apps/local-node-host/Data/Roster/RosterCrdtProjection.cs", "PendingAdministratorRemovalAttemptLimit = 4", 1, "Pending removal retry count."),
        ("apps/local-node-host/Data/Roster/RosterCrdtProjection.cs", "index = 0", 2, "Roster iteration indices."),
        ("apps/local-node-host/Data/Roster/RosterCrdtProjection.cs", "removed = 0", 3, "Roster removal accumulators."),
        ("apps/local-node-host/Data/Roster/RosterRecordCrdtState.cs", "XWingPublicKeyLength = 1216", 1, "Hybrid public key encoding length."),
        ("apps/local-node-host/Data/Roster/RosterRecordCrdtState.cs", "int WireFormatVersion = 0", 1, "Legacy wire format default."),
    ];

    [Fact(DisplayName = "T-1056: the roster skew window is the sync handshake's one constant, 30 s")]
    public void TheRosterWindowIsTheHandshakeConstant()
    {
        Assert.Equal(30, HandshakeProtocol.HelloTimestampSkewSeconds);
        Assert.Equal(TimeSpan.FromSeconds(HandshakeProtocol.HelloTimestampSkewSeconds), HandshakeProtocol.HelloTimestampSkew);
        Assert.Equal(HandshakeProtocol.HelloTimestampSkew, NodeRosterRecord.ReceiveTimeWindow);
    }

    [Fact(DisplayName = "T-1056: no second time literal where signed roster time is compared")]
    [Trait("HandMutant", "T-1056-second-identity-skew")]
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
            IsNumber(initializer.Value)
            || Regex.IsMatch(variable.Identifier.ValueText, "Skew|Leeway|Window|Seconds", RegexOptions.IgnoreCase)
                && initializer.Value.DescendantTokens().Any(token => token.IsKind(SyntaxKind.NumericLiteralToken)),
        PropertyDeclarationSyntax property =>
            property.Initializer is { } initializer && IsNumber(initializer.Value)
            || property.ExpressionBody is { } body && IsNumber(body.Expression),
        ParameterSyntax { Default: { } initializer } => IsNumber(initializer.Value),
        BinaryExpressionSyntax binary =>
            binary.Kind() is SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression
                or SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression
                or SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression
            && binary.DescendantNodes().OfType<MemberAccessExpressionSyntax>().Any(member =>
                IsTimeUnit(member.Name.Identifier.ValueText))
            && binary.DescendantTokens().Any(token => token.IsKind(SyntaxKind.NumericLiteralToken)),
        IsPatternExpressionSyntax pattern =>
            (pattern.Expression.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
                .Any(member => IsTimeUnit(member.Name.Identifier.ValueText))
            || pattern.Pattern.DescendantNodes().OfType<SubpatternSyntax>()
                .Any(property => property.NameColon is { } name && IsTimeUnit(name.Name.Identifier.ValueText)))
            && pattern.Pattern.DescendantTokens().Any(token => token.IsKind(SyntaxKind.NumericLiteralToken)),
        _ => false,
    };

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
