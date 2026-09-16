using System.Net;

using Harborline.Api.LocalNodeHost;
using Harborline.Api.LocalNodeHost.Data.Roster;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace Harborline.Api.LocalNodeHost.Tests.Storage;

[Collection("Harborline process environment")]
public sealed class ComposedHostPostgresBootTests
{
    private const string RootSeedHex =
        "3463463463463463463463463463463463463463463463463463463463463463";

    [Fact]
    public async Task Default_boot_keeps_the_sqlcipher_store()
    {
        var dataDirectory = CreateDataDirectory();
        var environment = CaptureStorageEnvironment();

        try
        {
            SetStorageEnvironment(provider: null, connectionString: null);
            var baseAddress = await LocalNodeHostRuntime.StartAsync(
                "t456-composed-host-sqlite", dataDirectory, CancellationToken.None);
            using var client = new HttpClient { BaseAddress = baseAddress };
            using var health = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        finally
        {
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            RestoreStorageEnvironment(environment);
            DeleteDataDirectory(dataDirectory);
        }
    }

    [SkippableFact]
    public async Task Postgres_boot_creates_schema_persists_genesis_and_restarts()
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable("HARBORLINE_TEST_POSTGRES");
        Skip.If(string.IsNullOrWhiteSpace(configuredConnectionString),
            "HARBORLINE_TEST_POSTGRES is required for the real PostgreSQL composed-host test.");

        var dataDirectory = CreateDataDirectory();
        var environment = CaptureStorageEnvironment();
        var databaseName = "hl_" + Guid.NewGuid().ToString("N");
        var adminConnectionString = CreateDatabase(configuredConnectionString!, databaseName);
        var postgresConnectionString = new NpgsqlConnectionStringBuilder(configuredConnectionString!)
        {
            Database = databaseName,
        }.ConnectionString;

        try
        {
            SetStorageEnvironment("Postgres", postgresConnectionString);
            await AssertHealthyAsync("t456-composed-host-postgres", dataDirectory);

            await using (var connection = new NpgsqlConnection(postgresConnectionString))
            {
                await connection.OpenAsync();
                await using var schemaExists = new NpgsqlCommand(
                    "SELECT EXISTS (SELECT 1 FROM information_schema.tables " +
                    "WHERE table_schema = 'public' AND table_name = 'roster_records')", connection);
                Assert.True((bool)(await schemaExists.ExecuteScalarAsync())!);
            }

            var services = Assert.IsAssignableFrom<IServiceProvider>(LocalNodeHostRuntime.CurrentServices);
            var rosterFactory = services.GetRequiredService<IDbContextFactory<NodeLocalRosterDbContext>>();
            await using (var roster = await rosterFactory.CreateDbContextAsync())
            {
                Assert.True(await roster.RosterRecords.AnyAsync());
            }

            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            await AssertHealthyAsync("t456-composed-host-postgres-restart", dataDirectory);
        }
        finally
        {
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            RestoreStorageEnvironment(environment);
            DeleteDataDirectory(dataDirectory);
            await DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task Postgres_plaintext_connection_is_refused_at_startup()
    {
        var dataDirectory = CreateDataDirectory();
        var environment = CaptureStorageEnvironment();
        var output = new StringWriter();
        var previousOutput = Console.Out;
        Console.SetOut(output);

        try
        {
            SetStorageEnvironment(
                "Postgres",
                "Host=localhost;Port=5432;Username=harborline;Password=harborline;Database=harborline;SSL Mode=Disable");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                LocalNodeHostRuntime.StartAsync("t456-composed-host-plaintext", dataDirectory, CancellationToken.None));

            Assert.StartsWith("local-node.storage.postgres_tls_required:", exception.Message, StringComparison.Ordinal);
            Assert.Contains(
                "[local-node-host] storage: Postgres refused: TLS is required (SSL Mode=Require or stronger)",
                output.ToString());
        }
        finally
        {
            await LocalNodeHostRuntime.StopAsync(CancellationToken.None);
            Console.SetOut(previousOutput);
            RestoreStorageEnvironment(environment);
            DeleteDataDirectory(dataDirectory);
        }
    }

    private static async Task AssertHealthyAsync(string sessionToken, string dataDirectory)
    {
        var baseAddress = await LocalNodeHostRuntime.StartAsync(sessionToken, dataDirectory, CancellationToken.None);
        using var client = new HttpClient { BaseAddress = baseAddress };
        using var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    private static string CreateDatabase(string connectionString, string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "harborline" };
        using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
        command.ExecuteNonQuery();
        return builder.ConnectionString;
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static (string? RootSeed, string? Provider, string? ConnectionString) CaptureStorageEnvironment() =>
        (
            Environment.GetEnvironmentVariable("LocalNode__RootSeedHex"),
            Environment.GetEnvironmentVariable("LocalNode__Storage__Provider"),
            Environment.GetEnvironmentVariable("LocalNode__Storage__ConnectionString"));

    private static void SetStorageEnvironment(string? provider, string? connectionString)
    {
        Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", RootSeedHex);
        Environment.SetEnvironmentVariable("LocalNode__Storage__Provider", provider);
        Environment.SetEnvironmentVariable("LocalNode__Storage__ConnectionString", connectionString);
    }

    private static void RestoreStorageEnvironment((string? RootSeed, string? Provider, string? ConnectionString) environment)
    {
        Environment.SetEnvironmentVariable("LocalNode__RootSeedHex", environment.RootSeed);
        Environment.SetEnvironmentVariable("LocalNode__Storage__Provider", environment.Provider);
        Environment.SetEnvironmentVariable("LocalNode__Storage__ConnectionString", environment.ConnectionString);
    }

    private static string CreateDataDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"t456-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDataDirectory(string directory)
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
