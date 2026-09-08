using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class WebSelectedSessionStoreTests
{
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 7, 18, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "3359")]
    public async Task Two_Grant_Pins_Are_Treated_As_Missing_By_Selected_Session_Store()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"selected-session-malformed-{Guid.NewGuid():N}.db");
        const string selectedHandle = "selected-malformed-handle-with-fixture-entropy";
        try
        {
            var factory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(databasePath);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
                context.UserSessions.Add(SelectedSession(Digest(selectedHandle)));
                await context.SaveChangesAsync();
                await context.Database.ExecuteSqlAsync($$"""
                    UPDATE web_user_sessions
                    SET pinned_grant_owner_versions_json =
                        '[{"GrantId":"grant-1","OwnerVersion":4},
                          {"GrantId":"grant-2","OwnerVersion":5}]'
                    WHERE handle_digest = {{Digest(selectedHandle)}}
                    """);
            }

            var logger = new RecordingLogger();
            var selectedStore = new WebSelectedSessionStore(factory, logger);
            WebUserSessionRecord? resolved = null;
            var exception = await Record.ExceptionAsync(async () =>
                resolved = await selectedStore.FindActiveAsync(
                    Digest(selectedHandle),
                    currentAccountSecurityVersion: 3,
                    IssuedAt.AddMinutes(1)));

            Assert.Null(exception);
            Assert.Null(resolved);
            Assert.Contains(logger.Entries, entry =>
                entry.Level == LogLevel.Error &&
                entry.Message.Contains(
                    "identity.selected_session_invalid",
                    StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    [Trait("PlanCard", "3359")]
    public async Task Invalid_Grant_Pin_Json_Is_Treated_As_Missing_By_Selected_Session_Store()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"selected-session-invalid-json-{Guid.NewGuid():N}.db");
        const string selectedHandle = "selected-invalid-json-handle-with-fixture-entropy";
        try
        {
            var factory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(databasePath);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
                context.UserSessions.Add(SelectedSession(Digest(selectedHandle)));
                await context.SaveChangesAsync();
                await context.Database.ExecuteSqlAsync($$"""
                    UPDATE web_user_sessions
                    SET pinned_grant_owner_versions_json = 'not-json'
                    WHERE handle_digest = {{Digest(selectedHandle)}}
                    """);
            }

            var logger = new RecordingLogger();
            var selectedStore = new WebSelectedSessionStore(factory, logger);

            var resolved = await selectedStore.FindStoredAsync(Digest(selectedHandle));

            Assert.Null(resolved);
            Assert.Contains(logger.Entries, entry =>
                entry.Level == LogLevel.Error &&
                entry.Message.Contains(
                    "identity.selected_session_invalid",
                    StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    [Trait("PlanCard", "SES-05C")]
    public async Task Selected_And_Installation_Consumers_Reject_Their_Exact_Revocations()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"selected-session-store-{Guid.NewGuid():N}.db");
        const string selectedHandle = "selected-handle-with-fixture-entropy";
        const string installationHandle = "installation-handle-with-fixture-entropy";
        try
        {
            var factory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(databasePath);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
                context.UserSessions.Add(SelectedSession(Digest(selectedHandle)));
                context.InstallationSessions.Add(InstallationSession(Digest(installationHandle)));
                await context.SaveChangesAsync();
            }

            var selectedStore = new WebSelectedSessionStore(factory);
            var installationStore = new WebInstallationSessionStore(factory);
            Assert.NotNull(await selectedStore.FindActiveAsync(
                Digest(selectedHandle),
                currentAccountSecurityVersion: 3,
                IssuedAt.AddMinutes(1)));
            Assert.NotNull(await installationStore.FindActiveAsync(
                Digest(installationHandle),
                currentAccountSecurityVersion: 3,
                IssuedAt.AddMinutes(1)));

            Assert.True(await selectedStore.RevokeAsync(
                SelectedSession(Digest(selectedHandle)),
                "selected-logout-correlation",
                "user-logout",
                IssuedAt.AddMinutes(2)));
            Assert.True(await installationStore.RevokeAsync(
                Digest(installationHandle),
                "installation-logout-correlation",
                "user-logout",
                IssuedAt.AddMinutes(2)));

            // Reusing either raw handle must now fail at the shared gate ordering:
            // record valid -> NOT revoked -> future antiforgery -> request.
            Assert.Null(await selectedStore.FindActiveAsync(
                Digest(selectedHandle),
                currentAccountSecurityVersion: 3,
                IssuedAt.AddMinutes(3)));
            Assert.Null(await installationStore.FindActiveAsync(
                Digest(installationHandle),
                currentAccountSecurityVersion: 3,
                IssuedAt.AddMinutes(3)));

            await using var verification = factory.CreateDbContext();
            var revocations = await verification.Revocations.AsNoTracking()
                .OrderBy(row => row.Audience)
                .ToArrayAsync();
            Assert.Equal(2, revocations.Length);
            Assert.Contains(revocations, row =>
                row.Audience == WebCookieAudience.SelectedSession &&
                row.SubjectCorrelationId == "selected-session" &&
                row.ReasonCode == "user-logout");
            Assert.Contains(revocations, row =>
                row.Audience == WebCookieAudience.InstallationSession &&
                row.SubjectCorrelationId == "installation-session" &&
                row.ReasonCode == "user-logout");
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    [Trait("PlanCard", "MTW-01C")]
    public async Task Selected_Consumer_Rejects_Challenge_Audience_Handle_Across_Reopen()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"selected-session-audience-{Guid.NewGuid():N}.db");
        const string challengeHandle = "challenge-handle-with-at-least-256-bits-of-fixture-entropy";
        try
        {
            var factory = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(databasePath);
            await using (var context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync();
                context.AccountAccessChallenges.Add(new WebAccountAccessChallengeRecord(
                    ChallengeId: "challenge-correlation",
                    AccountId: "account-1",
                    AccountSecurityVersion: 3,
                    HandleDigest: Digest(challengeHandle),
                    CoordinationCorrelationId: "challenge-coordination",
                    IssuedAtUtc: IssuedAt,
                    AbsoluteExpiresAtUtc: IssuedAt.AddMinutes(5),
                    ConsumedAtUtc: null,
                    RevokedAtUtc: null,
                    OwnerVersion: 1));
                await context.SaveChangesAsync();
            }

            // The selected extractor reopens the same durable store but queries only the
            // selected-session audience. A digest collision across purpose tables cannot change
            // the audience of the challenge record.
            var reopened = new WebAccountAccessChallengeIssuerTests.SessionContextFactory(databasePath);
            var selectedStore = new WebSelectedSessionStore(reopened);

            Assert.Null(await selectedStore.FindActiveAsync(
                Digest(challengeHandle),
                currentAccountSecurityVersion: 3,
                IssuedAt.AddMinutes(1)));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static WebUserSessionRecord SelectedSession(string handleDigest) =>
        new(
            SessionCorrelationId: "selected-session",
            AccountId: "account-1",
            AccountSecurityVersion: 3,
            TenantId: "tenant-1",
            MembershipId: "membership-1",
            MembershipOwnerVersion: 2,
            TenantPrincipalId: "principal-1",
            CanonicalPartyReference: "party-1",
            PinnedGrantOwnerVersions: [new PinnedGrantOwnerVersion("grant-1", 4)],
            AuthorizationEpoch: 5,
            HandleDigest: handleDigest,
            AntiforgeryStateId: "selected-antiforgery",
            CoordinationCorrelationId: "selection-correlation",
            IssuedAtUtc: IssuedAt,
            IdleExpiresAtUtc: IssuedAt.AddMinutes(10),
            AbsoluteExpiresAtUtc: IssuedAt.AddHours(1),
            OwnerVersion: 1);

    private static WebInstallationSessionRecord InstallationSession(string handleDigest) =>
        new(
            SessionCorrelationId: "installation-session",
            AccountId: "account-1",
            AccountSecurityVersion: 3,
            InstallationGrantId: "installation-grant-1",
            InstallationGrantOwnerVersion: 4,
            AuthorizationEpoch: 5,
            HandleDigest: handleDigest,
            AntiforgeryStateId: "installation-antiforgery",
            CoordinationCorrelationId: "installation-correlation",
            IssuedAtUtc: IssuedAt,
            AbsoluteExpiresAtUtc: IssuedAt.AddMinutes(15),
            OwnerVersion: 1);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class RecordingLogger : ILogger<WebSelectedSessionStore>
    {
        internal List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
