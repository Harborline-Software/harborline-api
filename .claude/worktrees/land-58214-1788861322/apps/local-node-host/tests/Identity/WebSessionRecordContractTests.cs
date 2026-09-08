using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class WebSessionRecordContractTests
{
    private static readonly Type[] AudienceRecordTypes =
    [
        typeof(WebAccountAccessChallengeRecord),
        typeof(WebUserSessionRecord),
        typeof(WebInstallationSessionRecord),
    ];

    [Fact]
    [Trait("PlanCard", "SES-01")]
    public void Cookie_Audiences_Are_Structurally_Distinct_Records()
    {
        Assert.Equal(3, AudienceRecordTypes.Distinct().Count());
        Assert.All(AudienceRecordTypes, type => Assert.True(type.IsClass && type.IsSealed));
        Assert.Equal(
            ["AccountChallenge", "SelectedSession", "InstallationSession"],
            Enum.GetNames<WebCookieAudience>());
    }

    [Fact]
    [Trait("PlanCard", "3359")]
    public void Null_Grant_Pins_Name_The_Record_Property_In_The_Refusal()
    {
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);

        var exception = Assert.Throws<ArgumentNullException>(() => new WebUserSessionRecord(
            SessionCorrelationId: "session-1",
            AccountId: "account-1",
            AccountSecurityVersion: 1,
            TenantId: "tenant-1",
            MembershipId: "membership-1",
            MembershipOwnerVersion: 1,
            TenantPrincipalId: "principal-1",
            CanonicalPartyReference: "party-1",
            PinnedGrantOwnerVersions: null!,
            AuthorizationEpoch: 1,
            HandleDigest: "handle-digest",
            AntiforgeryStateId: "antiforgery-1",
            CoordinationCorrelationId: "coordination-1",
            IssuedAtUtc: issuedAt,
            IdleExpiresAtUtc: issuedAt.AddMinutes(1),
            AbsoluteExpiresAtUtc: issuedAt.AddMinutes(2),
            OwnerVersion: 1));

        Assert.Equal(nameof(WebUserSessionRecord.PinnedGrantOwnerVersions), exception.ParamName);
    }

    [Fact]
    [Trait("PlanCard", "SES-01")]
    public void Challenge_Contains_Only_Preselection_Coordinates()
    {
        AssertProperties<WebAccountAccessChallengeRecord>(
            "ChallengeId",
            "AccountId",
            "AccountSecurityVersion",
            "HandleDigest",
            "CoordinationCorrelationId",
            "IssuedAtUtc",
            "AbsoluteExpiresAtUtc",
            "ConsumedAtUtc",
            "RevokedAtUtc",
            "OwnerVersion");

        Assert.DoesNotContain(
            typeof(WebAccountAccessChallengeRecord).GetProperties(),
            property => IsTenantAuthorityFact(property.Name));
    }

    [Fact]
    [Trait("PlanCard", "SES-01")]
    public void Selected_Session_Contains_The_Complete_R3C_Tuple()
    {
        AssertProperties<WebUserSessionRecord>(
            "SessionCorrelationId",
            "AccountId",
            "AccountSecurityVersion",
            "TenantId",
            "MembershipId",
            "MembershipOwnerVersion",
            "TenantPrincipalId",
            "CanonicalPartyReference",
            "PinnedGrantOwnerVersions",
            "AuthorizationEpoch",
            "HandleDigest",
            "AntiforgeryStateId",
            "CoordinationCorrelationId",
            "IssuedAtUtc",
            "IdleExpiresAtUtc",
            "AbsoluteExpiresAtUtc",
            "OwnerVersion");

        Assert.Equal(
            typeof(IReadOnlyList<PinnedGrantOwnerVersion>),
            typeof(WebUserSessionRecord).GetProperty("PinnedGrantOwnerVersions")!.PropertyType);
    }

    [Fact]
    [Trait("PlanCard", "SES-01")]
    public void Installation_Session_Contains_Only_Installation_Authority_Coordinates()
    {
        AssertProperties<WebInstallationSessionRecord>(
            "SessionCorrelationId",
            "AccountId",
            "AccountSecurityVersion",
            "InstallationGrantId",
            "InstallationGrantOwnerVersion",
            "AuthorizationEpoch",
            "HandleDigest",
            "AntiforgeryStateId",
            "CoordinationCorrelationId",
            "IssuedAtUtc",
            "AbsoluteExpiresAtUtc",
            "OwnerVersion");

        Assert.DoesNotContain(
            typeof(WebInstallationSessionRecord).GetProperties(),
            property => IsTenantAuthorityFact(property.Name));
    }

    [Fact]
    [Trait("PlanCard", "SES-01")]
    public void Supporting_Records_Carry_Digest_Ttl_And_Coordination_Evidence()
    {
        AssertProperties<PinnedGrantOwnerVersion>("GrantId", "OwnerVersion");
        AssertProperties<WebSessionRevocationRecord>(
            "RevocationId",
            "Audience",
            "AccountId",
            "SubjectCorrelationId",
            "SupersededByCorrelationId",
            "ReasonCode",
            "CoordinationCorrelationId",
            "RevokedAtUtc",
            "OwnerVersion");
        AssertProperties<WebAntiforgeryStateRecord>(
            "AntiforgeryStateId",
            "Audience",
            "AccountId",
            "SubjectCorrelationId",
            "TokenDigest",
            "CoordinationCorrelationId",
            "IssuedAtUtc",
            "AbsoluteExpiresAtUtc",
            "ConsumedAtUtc",
            "RevokedAtUtc",
            "OwnerVersion");
        Assert.Equal(
            ["Preparing", "Committing", "Finalizing", "Completed"],
            Enum.GetNames<WebSessionCoordinationState>());
    }

    [Fact]
    [Trait("PlanCard", "SES-01")]
    public void Contract_Source_Contains_No_Copied_Authority_Or_Raw_Bearer_Facts()
    {
        var source = File.ReadAllText(Path.Combine(FindHostSourceRoot(), "Data", "Identity",
            "WebSessionRecordContracts.cs"));
        var forbidden = new[]
        {
            "RoleId",
            "RoleName",
            "Permission",
            "PartyName",
            "PartyEmail",
            "PartyDisplayName",
            "GrantSnapshot",
            "ClaimsJson",
            "RawHandle",
            "CookieValue",
            "TokenValue",
        };

        Assert.All(forbidden, token => Assert.DoesNotContain(token, source, StringComparison.Ordinal));
        Assert.Contains("HandleDigest", source, StringComparison.Ordinal);
        Assert.Contains("TokenDigest", source, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("PlanCard", "SES-01")]
    public void Contracts_Are_Mapped_Only_By_The_Dedicated_Web_Session_Context()
    {
        using var context = CreateContext();

        var recordTypes = AudienceRecordTypes
            .Append(typeof(WebSessionRevocationRecord))
            .Append(typeof(WebAntiforgeryStateRecord))
            .ToArray();
        Assert.All(recordTypes, type => Assert.NotNull(context.Model.FindEntityType(type)));
        Assert.Equal(5, context.Model.GetEntityTypes().Count());
        Assert.Null(context.Model.FindEntityType(typeof(PinnedGrantOwnerVersion)));
        Assert.Equal(
            [
                "web_account_access_challenges",
                "web_user_sessions",
                "web_installation_sessions",
                "web_session_revocations",
                "web_antiforgery_states",
            ],
            recordTypes.Select(type => context.Model.FindEntityType(type)!.GetTableName()));
        Assert.All(recordTypes, type => Assert.True(
            context.Model.FindEntityType(type)!
                .FindProperty("OwnerVersion")!
                .IsConcurrencyToken));
        Assert.Equal(
            typeof(string),
            context.Model.FindEntityType(typeof(WebUserSessionRecord))!
                .FindProperty(nameof(WebUserSessionRecord.PinnedGrantOwnerVersions))!
                .GetTypeMapping().Converter!.ProviderClrType);
        Assert.False(context.Database.HasPendingModelChanges());

        var declaration = Path.GetFullPath(Path.Combine(
            FindHostSourceRoot(), "Data", "Identity", "WebSessionRecordContracts.cs"));
        var names = recordTypes.Select(type => type.Name).ToArray();
        var consumers = ProductionSourceFiles()
            .Where(file => !string.Equals(Path.GetFullPath(file), declaration, StringComparison.Ordinal))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}WebSessionMigrations{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(file => names.Any(name => File.ReadAllText(file).Contains(name, StringComparison.Ordinal)))
            .Select(file => Path.GetRelativePath(FindHostSourceRoot(), file))
            .ToArray();

        Assert.Equal(
            [
                Path.Combine("Data", "Identity", "AccountSetupInvitationIssuer.cs"),
                Path.Combine("Data", "Identity", "AdminTeamAccessAuthority.cs"),
                Path.Combine("Data", "Identity", "NodeLocalWebSessionDbContext.cs"),
                Path.Combine("Data", "Identity", "RecoveryInvitationIssuer.cs"),
                Path.Combine("Data", "Identity", "RecoverySessionRevoker.cs"),
                Path.Combine("Data", "Identity", "WebAccountAccessChallengeIssuer.cs"),
                Path.Combine("Data", "Identity", "WebAntiforgeryStateStore.cs"),
                Path.Combine("Data", "Identity", "WebInstallationSessionStore.cs"),
                Path.Combine("Data", "Identity", "WebSelectedSessionLogoutAuthority.cs"),
                Path.Combine("Data", "Identity", "WebSelectedSessionPrincipalAuthority.cs"),
                Path.Combine("Data", "Identity", "WebSelectedSessionStore.cs"),
                Path.Combine("Data", "Identity", "WebTenantSelectionAuthority.cs"),
                Path.Combine("Data", "Identity", "WebTenantSwitchAuthority.cs"),
            ],
            consumers.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Migration_RoundTrips_All_Five_Digest_Only_Record_Types()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"web-session-records-{Guid.NewGuid():N}.db");
        var issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);
        var installationHandleDigest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes("installation-handle")));

        try
        {
            await using (var context = CreateContext(databasePath))
            {
                await context.Database.MigrateAsync();

                context.AddRange(
                    new WebAccountAccessChallengeRecord(
                        "challenge-1", "account-1", 4, "challenge-handle-digest", "coordination-1",
                        issuedAt, issuedAt.AddMinutes(5), null, null, 1),
                    new WebUserSessionRecord(
                        "user-session-1", "account-1", 4, "tenant-1", "membership-1", 9,
                        "principal-1", "party-reference-1",
                        [new PinnedGrantOwnerVersion("grant-1", 12)],
                        3, "user-handle-digest", "antiforgery-1", "coordination-2",
                        issuedAt, issuedAt.AddMinutes(15), issuedAt.AddHours(8), 1),
                    new WebInstallationSessionRecord(
                        "installation-session-1", "account-1", 4, "installation-grant-1", 7,
                        2, installationHandleDigest, "antiforgery-2", "coordination-3",
                        issuedAt, issuedAt.AddMinutes(15), 1),
                    new WebSessionRevocationRecord(
                        "revocation-1", WebCookieAudience.SelectedSession, "account-1", "user-session-0",
                        "user-session-1", "superseded", "coordination-4", issuedAt, 1),
                    new WebAntiforgeryStateRecord(
                        "antiforgery-1", WebCookieAudience.SelectedSession, "account-1", "user-session-1",
                        "antiforgery-token-digest", "coordination-5", issuedAt, issuedAt.AddHours(8),
                        null, null, 1));
                await context.SaveChangesAsync();
                context.ChangeTracker.Clear();

                Assert.Equal(
                    "challenge-handle-digest",
                    (await context.AccountAccessChallenges.SingleAsync()).HandleDigest);
                var userSession = await context.UserSessions.SingleAsync();
                Assert.Equal("user-handle-digest", userSession.HandleDigest);
                Assert.Equal(
                    [new PinnedGrantOwnerVersion("grant-1", 12)],
                    userSession.PinnedGrantOwnerVersions);
                Assert.Equal(
                    installationHandleDigest,
                    (await context.InstallationSessions.SingleAsync()).HandleDigest);
                Assert.Equal(
                    WebCookieAudience.SelectedSession,
                    (await context.Revocations.SingleAsync()).Audience);
                Assert.Equal(
                    "antiforgery-token-digest",
                    (await context.AntiforgeryStates.SingleAsync()).TokenDigest);
                Assert.Contains(
                    "20260716214207_WebSessionRecords",
                    await context.Database.GetAppliedMigrationsAsync());
            }
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static bool IsTenantAuthorityFact(string name) =>
        name.Contains("Tenant", StringComparison.Ordinal) ||
        name.Contains("Membership", StringComparison.Ordinal) ||
        name.Contains("Principal", StringComparison.Ordinal) ||
        name.Contains("Party", StringComparison.Ordinal) ||
        name.Contains("PinnedGrant", StringComparison.Ordinal);

    private static void AssertProperties<T>(params string[] expected)
    {
        var actual = typeof(T).GetProperties().Select(property => property.Name).ToArray();
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    private static IEnumerable<string> ProductionSourceFiles()
    {
        var root = FindHostSourceRoot();
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static NodeLocalWebSessionDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<NodeLocalWebSessionDbContext>()
            .UseSqlite("Data Source=:memory:", sqlite =>
                sqlite.MigrationsHistoryTable(
                    NodeLocalWebSessionDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalWebSessionDbContext(options);
    }

    private static NodeLocalWebSessionDbContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<NodeLocalWebSessionDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False", sqlite =>
                sqlite.MigrationsHistoryTable(NodeLocalWebSessionDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalWebSessionDbContext(options);
    }

    private static string FindHostSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "local-node-host", "Program.cs");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)
                    ?? throw new InvalidOperationException("Program.cs has no parent directory.");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the local-node-host source root from " + AppContext.BaseDirectory);
    }
}
