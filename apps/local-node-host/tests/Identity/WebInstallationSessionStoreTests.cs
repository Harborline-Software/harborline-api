using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Harborline.Api.LocalNodeHost.Data.Identity;

namespace Harborline.Api.LocalNodeHost.Tests.Identity;

public sealed class WebInstallationSessionStoreTests
{
    private const string PreviousMigration = "20260716214207_WebSessionRecords";
    private static readonly DateTimeOffset IssuedAt =
        new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("PlanCard", "IAS-01A")]
    public async Task Migration_Fresh_Upgrade_And_Restart_Have_No_Pending_Model()
    {
        var databasePath = DatabasePath();
        try
        {
            var factory = new SessionContextFactory(databasePath);
            await using (var upgrading = factory.CreateDbContext())
            {
                await upgrading.GetService<IMigrator>().MigrateAsync(PreviousMigration);
                upgrading.InstallationSessions.Add(Session("upgrade", Digest("upgrade"), 7));
                await upgrading.SaveChangesAsync();

                await upgrading.Database.MigrateAsync();
                Assert.False(upgrading.Database.HasPendingModelChanges());
                Assert.Equal(3, (await upgrading.Database.GetAppliedMigrationsAsync()).Count());
                Assert.Equal(1, await upgrading.InstallationSessions.CountAsync());
            }

            await using (var restarted = factory.CreateDbContext())
            {
                await restarted.Database.MigrateAsync();
                Assert.False(restarted.Database.HasPendingModelChanges());
                Assert.Equal(1, await restarted.InstallationSessions.CountAsync());
            }

            var freshPath = DatabasePath();
            try
            {
                await using var fresh = new SessionContextFactory(freshPath).CreateDbContext();
                await fresh.Database.MigrateAsync();
                Assert.False(fresh.Database.HasPendingModelChanges());
                Assert.Equal(3, (await fresh.Database.GetAppliedMigrationsAsync()).Count());
            }
            finally
            {
                DeleteDatabase(freshPath);
            }
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    [Trait("PlanCard", "IAS-01A")]
    public async Task Fifteen_Minute_Maximum_Is_Enforced_By_Store_And_Migrated_Database()
    {
        var databasePath = DatabasePath();
        try
        {
            var factory = new SessionContextFactory(databasePath);
            await MigrateAsync(factory);
            var store = new WebInstallationSessionStore(factory);
            var tooLong = Session("store-too-long", Digest("store-too-long"), 1) with
            {
                AbsoluteExpiresAtUtc = IssuedAt + WebInstallationSessionStore.MaximumLifetime +
                    TimeSpan.FromMilliseconds(1),
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(tooLong));

            await using var context = factory.CreateDbContext();
            context.InstallationSessions.Add(tooLong);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    [Trait("PlanCard", "IAS-01B")]
    public async Task Digest_Only_Session_Survives_Restart_And_Fails_Closed_On_Expiry_Or_Version_Drift()
    {
        var databasePath = DatabasePath();
        const string rawHandle = "raw-installation-session-handle-never-persisted";
        var handleDigest = Digest(rawHandle);
        try
        {
            var factory = new SessionContextFactory(databasePath);
            await MigrateAsync(factory);
            await new WebInstallationSessionStore(factory)
                .CreateAsync(Session("restart", handleDigest, 9));

            var restarted = new WebInstallationSessionStore(new SessionContextFactory(databasePath));
            Assert.NotNull(await restarted.FindActiveAsync(
                handleDigest,
                9,
                IssuedAt.AddMinutes(14).AddSeconds(59)));
            Assert.Null(await restarted.FindActiveAsync(handleDigest, 10, IssuedAt.AddMinutes(1)));
            Assert.Null(await restarted.FindActiveAsync(handleDigest, 9, IssuedAt.AddMinutes(15)));

            Assert.DoesNotContain(rawHandle, Encoding.UTF8.GetString(File.ReadAllBytes(databasePath)),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    [Trait("PlanCard", "IAS-01B")]
    public async Task Exact_Revocation_Is_Idempotent_And_Survives_Restart()
    {
        var databasePath = DatabasePath();
        var handleDigest = Digest("revoke");
        try
        {
            var factory = new SessionContextFactory(databasePath);
            await MigrateAsync(factory);
            var store = new WebInstallationSessionStore(factory);
            await store.CreateAsync(Session("revoke", handleDigest, 3));

            Assert.True(await store.RevokeAsync(
                handleDigest,
                "revoke-correlation",
                "explicit-revoke",
                IssuedAt.AddMinutes(1)));
            Assert.False(await store.RevokeAsync(
                handleDigest,
                "revoke-replay",
                "replay",
                IssuedAt.AddMinutes(2)));
            Assert.Null(await new WebInstallationSessionStore(new SessionContextFactory(databasePath))
                .FindActiveAsync(handleDigest, 3, IssuedAt.AddMinutes(3)));

            await using var context = factory.CreateDbContext();
            var revocation = await context.Revocations.AsNoTracking().SingleAsync();
            Assert.Equal(WebCookieAudience.InstallationSession, revocation.Audience);
            Assert.Equal("revoke", revocation.SubjectCorrelationId);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    [Trait("PlanCard", "IAS-01B")]
    public async Task Security_Version_Primitive_Revokes_Only_Stale_Installation_Sessions()
    {
        var databasePath = DatabasePath();
        try
        {
            var factory = new SessionContextFactory(databasePath);
            await MigrateAsync(factory);
            var store = new WebInstallationSessionStore(factory);
            await store.CreateAsync(Session("stale", Digest("stale"), 4));
            await store.CreateAsync(Session("current", Digest("current"), 5));
            await store.CreateAsync(Session("other-account", Digest("other"), 4) with
            {
                AccountId = "account-2",
            });

            Assert.Equal(1, await store.RevokeStaleAccountSecurityVersionsAsync(
                "account-1",
                5,
                "security-version-correlation",
                "account-security-version-advanced",
                IssuedAt.AddMinutes(1)));
            Assert.Null(await store.FindActiveAsync(Digest("stale"), 5, IssuedAt.AddMinutes(2)));
            Assert.NotNull(await store.FindActiveAsync(Digest("current"), 5, IssuedAt.AddMinutes(2)));
            Assert.NotNull(await store.FindActiveAsync(Digest("other"), 4, IssuedAt.AddMinutes(2)));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    [Trait("PlanCard", "IAS-01B")]
    public async Task Tampered_Durable_Authority_Fails_Closed()
    {
        var databasePath = DatabasePath();
        var handleDigest = Digest("tamper");
        try
        {
            var factory = new SessionContextFactory(databasePath);
            await MigrateAsync(factory);
            var store = new WebInstallationSessionStore(factory);
            await store.CreateAsync(Session("tamper", handleDigest, 2));

            await using (var context = factory.CreateDbContext())
            {
                await context.Database.OpenConnectionAsync();
                try
                {
                    await context.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
                    await context.Database.ExecuteSqlRawAsync(
                        "UPDATE web_installation_sessions SET installation_grant_owner_version = 0;");
                }
                finally
                {
                    await context.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = OFF;");
                    await context.Database.CloseConnectionAsync();
                }
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.FindActiveAsync(handleDigest, 2, IssuedAt.AddMinutes(1)));
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    [Trait("PlanCard", "IAS-01B")]
    public void Store_Is_Dormant_And_Its_Api_Accepts_Only_Digests()
    {
        var type = typeof(WebInstallationSessionStore);
        Assert.False(type.IsPublic);
        Assert.DoesNotContain(
            type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters()),
            parameter => parameter.ParameterType.Name.Contains("Logger", StringComparison.Ordinal));
        Assert.All(
            type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetParameters())
                .Where(parameter => parameter.Name!.Contains("handle", StringComparison.OrdinalIgnoreCase)),
            parameter => Assert.EndsWith("Digest", parameter.Name!, StringComparison.OrdinalIgnoreCase));

        var root = FindHostSourceRoot();
        var consumers = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(
                $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains(nameof(WebInstallationSessionStore),
                StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();
        Assert.Equal(
            [Path.Combine("Data", "Identity", "WebInstallationSessionStore.cs")],
            consumers);
    }

    private static WebInstallationSessionRecord Session(
        string correlationId,
        string handleDigest,
        long accountSecurityVersion) =>
        new(
            SessionCorrelationId: correlationId,
            AccountId: "account-1",
            AccountSecurityVersion: accountSecurityVersion,
            InstallationGrantId: "installation-grant-1",
            InstallationGrantOwnerVersion: 7,
            AuthorizationEpoch: 3,
            HandleDigest: handleDigest,
            AntiforgeryStateId: $"antiforgery-{correlationId}",
            CoordinationCorrelationId: $"coordination-{correlationId}",
            IssuedAtUtc: IssuedAt,
            AbsoluteExpiresAtUtc: IssuedAt.AddMinutes(15),
            OwnerVersion: 1);

    private static string Digest(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task MigrateAsync(SessionContextFactory factory)
    {
        await using var context = factory.CreateDbContext();
        await context.Database.MigrateAsync();
    }

    private static string DatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"installation-session-{Guid.NewGuid():N}.db");

    private static void DeleteDatabase(string path)
    {
        File.Delete(path);
        File.Delete($"{path}-shm");
        File.Delete($"{path}-wal");
    }

    private static string FindHostSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "apps", "local-node-host", "Program.cs");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate local-node-host source root.");
    }

    private sealed class SessionContextFactory(string databasePath)
        : IDbContextFactory<NodeLocalWebSessionDbContext>
    {
        public NodeLocalWebSessionDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<NodeLocalWebSessionDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False", sqlite =>
                    sqlite.MigrationsHistoryTable(
                        NodeLocalWebSessionDbContext.MigrationsHistoryTableName))
                .Options;
            return new NodeLocalWebSessionDbContext(options);
        }
    }
}
