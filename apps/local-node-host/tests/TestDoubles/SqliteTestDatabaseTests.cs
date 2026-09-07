using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Tests.TestDoubles;

public sealed class SqliteTestDatabaseTests
{
    /// <summary>
    /// Windows enforces mandatory file locking, so deleting a file that an open handle still holds
    /// throws. POSIX permits unlinking an open file, so the same call simply succeeds. The invariant
    /// under test - a pooled DbContext keeps its connection open past dispose - is not different on
    /// Linux, it is merely unobservable through File.Delete there. Skipped rather than weakened, so
    /// the gap shows up in the run summary instead of passing vacuously.
    /// </summary>
    [SkippableFact]
    public async Task Pooled_context_holds_the_file_after_the_context_is_disposed()
    {
        Skip.IfNot(
            OperatingSystem.IsWindows(),
            "Mandatory file locking is a Windows behaviour; POSIX allows unlinking an open file, so a held handle cannot be observed through File.Delete.");

        var pooledPath = Path.Combine(Path.GetTempPath(), $"sqlite-pooled-control-{Guid.NewGuid():N}.db");

        try
        {
            var pooledOptions = new DbContextOptionsBuilder()
                .UseSqlite($"Data Source={pooledPath}")
                .Options;

            await using (var context = new DbContext(pooledOptions))
            {
                await context.Database.OpenConnectionAsync();
            }

            Assert.Throws<IOException>(() => File.Delete(pooledPath));
        }
        finally
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={pooledPath}");
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            if (File.Exists(pooledPath))
            {
                File.Delete(pooledPath);
            }
        }
    }

    /// <summary>
    /// The other half of the original single test. This half asserts the fixture helper's own
    /// contract and depends on no platform-specific locking behaviour, so it runs everywhere -
    /// which is why it is a separate test rather than sharing the Windows skip above.
    /// </summary>
    [Fact]
    public async Task Fixture_connection_releases_the_file_so_it_can_be_deleted()
    {
        var fixturePath = Path.Combine(Path.GetTempPath(), $"sqlite-fixture-control-{Guid.NewGuid():N}.db");

        try
        {
            var fixtureOptions = new DbContextOptionsBuilder()
                .UseSqlite(SqliteTestDatabase.ConnectionString(fixturePath))
                .Options;

            await using (var context = new DbContext(fixtureOptions))
            {
                await context.Database.OpenConnectionAsync();
            }

            SqliteTestDatabase.Delete(fixturePath);

            Assert.False(File.Exists(fixturePath));
        }
        finally
        {
            if (File.Exists(fixturePath))
            {
                File.Delete(fixturePath);
            }
        }
    }
}
