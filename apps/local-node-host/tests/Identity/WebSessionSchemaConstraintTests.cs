using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

/// <summary>
/// Boundary proofs for the web-session expiry / idle / owner-version CHECK constraints.
/// </summary>
/// <remarks>
/// <para>
/// The constraints are declared in <see cref="NodeLocalWebSessionDbContext"/> and emitted by the
/// WebSession migrations, but nothing exercised them: the schema could have shipped with any of the
/// comparison operators inverted or relaxed and every existing test would still have passed. These
/// tests are the pincer — each constraint is proved from BOTH sides against a real on-disk migrated
/// database and a frozen clock, so relaxing <c>&gt;</c> to <c>&gt;=</c> (or dropping a constraint
/// outright) fails a test rather than passing silently.
/// </para>
/// <para>
/// Deliberately out of scope, and still unexercised after this card:
/// <c>ck_web_installation_session_handle_digest</c> and
/// <c>ck_web_installation_session_authority_versions</c>. They are a separate bounded concern.
/// </para>
/// </remarks>
public sealed class WebSessionSchemaConstraintTests
{
    /// <summary>Frozen clock — the same instant the migration round-trip test issues against.</summary>
    private static readonly DateTimeOffset IssuedAt =
        DateTimeOffset.FromUnixTimeMilliseconds(1_752_640_000_000);

    /// <summary>The installation-session absolute TTL ceiling the schema enforces, in milliseconds.</summary>
    private const long InstallationTtlCeilingMilliseconds = 900_000;

    /// <summary>64 uppercase hex characters, so the digest-shape constraint is satisfied.</summary>
    private static readonly string InstallationHandleDigest = new('A', 64);

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Challenge_Absolute_Expiry_At_Or_Before_Issue_Is_Rejected()
    {
        await WithMigratedDatabaseAsync(async context =>
        {
            await AssertRejectedAsync(
                context,
                Challenge(absoluteExpiresAt: IssuedAt),
                "ck_web_account_challenge_expiry");
            await AssertRejectedAsync(
                context,
                Challenge(absoluteExpiresAt: IssuedAt.AddMilliseconds(-1)),
                "ck_web_account_challenge_expiry");

            Assert.Empty(await context.AccountAccessChallenges.ToListAsync());
        });
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task User_Session_Absolute_Expiry_At_Or_Before_Issue_Is_Rejected()
    {
        await WithMigratedDatabaseAsync(async context =>
        {
            // Idle is pinned to the issue instant + 1ms. BOTH constraints are violated here: with
            // absolute <= issued, `idle > issued AND idle <= absolute` is unsatisfiable. Which name
            // SQLite reports depends on CREATE TABLE declaration order, and EF emits them
            // alphabetically, so `..._absolute_expiry` is declared first and wins. The constraint-NAME
            // assertion below is what pins the absolute fence -- not the fixture shape.
            await AssertRejectedAsync(
                context,
                UserSession(
                    idleExpiresAt: IssuedAt.AddMilliseconds(1),
                    absoluteExpiresAt: IssuedAt),
                "ck_web_user_session_absolute_expiry");
            await AssertRejectedAsync(
                context,
                UserSession(
                    idleExpiresAt: IssuedAt.AddMilliseconds(1),
                    absoluteExpiresAt: IssuedAt.AddMilliseconds(-1)),
                "ck_web_user_session_absolute_expiry");

            Assert.Empty(await context.UserSessions.ToListAsync());
        });
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task User_Session_Idle_Expiry_Exceeding_Absolute_Is_Rejected()
    {
        await WithMigratedDatabaseAsync(async context =>
        {
            var absoluteExpiresAt = IssuedAt.AddHours(8);

            await AssertRejectedAsync(
                context,
                UserSession(
                    idleExpiresAt: absoluteExpiresAt.AddMilliseconds(1),
                    absoluteExpiresAt: absoluteExpiresAt),
                "ck_web_user_session_idle_expiry");

            Assert.Empty(await context.UserSessions.ToListAsync());
        });
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task User_Session_Idle_Expiry_At_Or_Before_Issue_Is_Rejected()
    {
        await WithMigratedDatabaseAsync(async context =>
        {
            await AssertRejectedAsync(
                context,
                UserSession(idleExpiresAt: IssuedAt, absoluteExpiresAt: IssuedAt.AddHours(8)),
                "ck_web_user_session_idle_expiry");
            await AssertRejectedAsync(
                context,
                UserSession(
                    idleExpiresAt: IssuedAt.AddMilliseconds(-1),
                    absoluteExpiresAt: IssuedAt.AddHours(8)),
                "ck_web_user_session_idle_expiry");

            Assert.Empty(await context.UserSessions.ToListAsync());
        });
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Installation_Session_Absolute_Expiry_Outside_The_Ttl_Ceiling_Is_Rejected()
    {
        await WithMigratedDatabaseAsync(async context =>
        {
            await AssertRejectedAsync(
                context,
                InstallationSession(absoluteExpiresAt: IssuedAt),
                "ck_web_installation_session_expiry");
            await AssertRejectedAsync(
                context,
                InstallationSession(
                    absoluteExpiresAt: IssuedAt.AddMilliseconds(
                        InstallationTtlCeilingMilliseconds + 1)),
                "ck_web_installation_session_expiry");

            Assert.Empty(await context.InstallationSessions.ToListAsync());
        });
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Antiforgery_State_Absolute_Expiry_At_Or_Before_Issue_Is_Rejected()
    {
        await WithMigratedDatabaseAsync(async context =>
        {
            await AssertRejectedAsync(
                context,
                AntiforgeryState(absoluteExpiresAt: IssuedAt),
                "ck_web_antiforgery_state_expiry");
            await AssertRejectedAsync(
                context,
                AntiforgeryState(absoluteExpiresAt: IssuedAt.AddMilliseconds(-1)),
                "ck_web_antiforgery_state_expiry");

            Assert.Empty(await context.AntiforgeryStates.ToListAsync());
        });
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Owner_Version_Zero_Is_Rejected_By_Every_Audience_And_Supporting_Table()
    {
        await WithMigratedDatabaseAsync(async context =>
        {
            await AssertRejectedAsync(
                context,
                Challenge(ownerVersion: 0),
                "ck_web_account_challenge_owner_version");
            await AssertRejectedAsync(
                context,
                UserSession(ownerVersion: 0),
                "ck_web_user_session_owner_version");
            await AssertRejectedAsync(
                context,
                InstallationSession(ownerVersion: 0),
                "ck_web_installation_session_owner_version");
            await AssertRejectedAsync(
                context,
                Revocation(ownerVersion: 0),
                "ck_web_session_revocation_owner_version");
            await AssertRejectedAsync(
                context,
                AntiforgeryState(ownerVersion: 0),
                "ck_web_antiforgery_state_owner_version");

            Assert.Empty(await context.AccountAccessChallenges.ToListAsync());
            Assert.Empty(await context.UserSessions.ToListAsync());
            Assert.Empty(await context.InstallationSessions.ToListAsync());
            Assert.Empty(await context.Revocations.ToListAsync());
            Assert.Empty(await context.AntiforgeryStates.ToListAsync());
        });
    }

    [Fact]
    [Trait("PlanCard", "MTW-2")]
    public async Task Values_One_Unit_Inside_Every_Boundary_Are_Accepted()
    {
        // The accepting half of the pincer: if any constraint were tightened by one unit (> becoming
        // >=, <= becoming <, owner_version > 0 becoming > 1) these rows would stop being insertable.
        await WithMigratedDatabaseAsync(async context =>
        {
            context.AddRange(
                Challenge(absoluteExpiresAt: IssuedAt.AddMilliseconds(1), ownerVersion: 1),
                UserSession(
                    idleExpiresAt: IssuedAt.AddMilliseconds(1),
                    absoluteExpiresAt: IssuedAt.AddMilliseconds(1),
                    ownerVersion: 1),
                InstallationSession(
                    absoluteExpiresAt: IssuedAt.AddMilliseconds(InstallationTtlCeilingMilliseconds),
                    ownerVersion: 1),
                Revocation(ownerVersion: 1),
                AntiforgeryState(absoluteExpiresAt: IssuedAt.AddMilliseconds(1), ownerVersion: 1));

            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var userSession = await context.UserSessions.SingleAsync();
            Assert.Equal(userSession.AbsoluteExpiresAtUtc, userSession.IdleExpiresAtUtc);
            Assert.Equal(
                InstallationTtlCeilingMilliseconds,
                (long)((await context.InstallationSessions.SingleAsync()).AbsoluteExpiresAtUtc - IssuedAt)
                    .TotalMilliseconds);
            Assert.Single(await context.AccountAccessChallenges.ToListAsync());
            Assert.Single(await context.Revocations.ToListAsync());
            Assert.Single(await context.AntiforgeryStates.ToListAsync());
        });
    }

    private static WebAccountAccessChallengeRecord Challenge(
        DateTimeOffset? absoluteExpiresAt = null,
        long ownerVersion = 1) =>
        new(
            "challenge-1",
            "account-1",
            4,
            "challenge-handle-digest",
            "coordination-1",
            IssuedAt,
            absoluteExpiresAt ?? IssuedAt.AddMinutes(5),
            null,
            null,
            ownerVersion);

    private static WebUserSessionRecord UserSession(
        DateTimeOffset? idleExpiresAt = null,
        DateTimeOffset? absoluteExpiresAt = null,
        long ownerVersion = 1) =>
        new(
            "user-session-1",
            "account-1",
            4,
            "tenant-1",
            "membership-1",
            9,
            "principal-1",
            "party-reference-1",
            [new PinnedGrantOwnerVersion("grant-1", 12)],
            3,
            "user-handle-digest",
            "antiforgery-1",
            "coordination-2",
            IssuedAt,
            idleExpiresAt ?? IssuedAt.AddMinutes(15),
            absoluteExpiresAt ?? IssuedAt.AddHours(8),
            ownerVersion);

    private static WebInstallationSessionRecord InstallationSession(
        DateTimeOffset? absoluteExpiresAt = null,
        long ownerVersion = 1) =>
        new(
            "installation-session-1",
            "account-1",
            4,
            "installation-grant-1",
            7,
            2,
            InstallationHandleDigest,
            "antiforgery-2",
            "coordination-3",
            IssuedAt,
            // 10 minutes, strictly inside the 15-minute TTL ceiling: a default sitting ON the
            // boundary makes an unrelated TTL change fail the owner-version tests by name.
            absoluteExpiresAt ?? IssuedAt.AddMinutes(10),
            ownerVersion);

    private static WebSessionRevocationRecord Revocation(long ownerVersion = 1) =>
        new(
            "revocation-1",
            WebCookieAudience.SelectedSession,
            "account-1",
            "user-session-0",
            "user-session-1",
            "superseded",
            "coordination-4",
            IssuedAt,
            ownerVersion);

    private static WebAntiforgeryStateRecord AntiforgeryState(
        DateTimeOffset? absoluteExpiresAt = null,
        long ownerVersion = 1) =>
        new(
            "antiforgery-1",
            WebCookieAudience.SelectedSession,
            "account-1",
            "user-session-1",
            "antiforgery-token-digest",
            "coordination-5",
            IssuedAt,
            absoluteExpiresAt ?? IssuedAt.AddHours(8),
            null,
            null,
            ownerVersion);

    /// <summary>
    /// Add <paramref name="record"/>, prove the named CHECK constraint rejected the write, and leave
    /// the change tracker clean so the caller can attempt the next boundary against the same store.
    /// </summary>
    private static async Task AssertRejectedAsync(
        NodeLocalWebSessionDbContext context,
        object record,
        string constraintName)
    {
        context.Add(record);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(
            async () => await context.SaveChangesAsync());

        Assert.Contains(
            constraintName,
            exception.InnerException?.Message ?? string.Empty,
            StringComparison.Ordinal);

        context.Entry(record).State = EntityState.Detached;
        context.ChangeTracker.Clear();
    }

    private static async Task WithMigratedDatabaseAsync(
        Func<NodeLocalWebSessionDbContext, Task> body)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"web-session-constraints-{Guid.NewGuid():N}.db");

        try
        {
            await using var context = CreateContext(databasePath);
            await context.Database.MigrateAsync();
            await body(context);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static NodeLocalWebSessionDbContext CreateContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<NodeLocalWebSessionDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False", sqlite =>
                sqlite.MigrationsHistoryTable(NodeLocalWebSessionDbContext.MigrationsHistoryTableName))
            .Options;

        return new NodeLocalWebSessionDbContext(options);
    }
}
