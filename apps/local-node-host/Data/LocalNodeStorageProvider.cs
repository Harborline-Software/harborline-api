using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

using Npgsql;

namespace Harborline.Api.LocalNodeHost.Data;

/// <summary>T-456's single production seam for local-node relational-provider selection.</summary>
internal static class LocalNodeStorageProvider
{
    private const string NpgsqlProviderName = "Npgsql.EntityFrameworkCore.PostgreSQL";

    internal static void ValidateAndAnnounce(StorageOptions storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        if (storage.Provider == StorageProvider.Sqlite)
        {
            Console.WriteLine("[local-node-host] storage: SQLite (SQLCipher)");
            return;
        }

        if (storage.Provider != StorageProvider.Postgres)
            throw new InvalidOperationException($"local-node.storage.provider_unknown: '{storage.Provider}'.");

        try
        {
            var connection = new NpgsqlConnectionStringBuilder(storage.ConnectionString);
            if (connection.SslMode is not (SslMode.Require or SslMode.VerifyCA or SslMode.VerifyFull))
            {
                Console.WriteLine(
                    "[local-node-host] storage: Postgres refused: TLS is required (SSL Mode=Require or stronger)");
                throw new InvalidOperationException("local-node.storage.postgres_tls_required: TLS is required.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Console.WriteLine(
                "[local-node-host] storage: Postgres refused: TLS is required (SSL Mode=Require or stronger)");
            throw new InvalidOperationException(
                "local-node.storage.postgres_tls_required: PostgreSQL requires a valid TLS connection string.",
                exception);
        }

        Console.WriteLine("[local-node-host] storage: Postgres (TLS required)");
    }

    internal static void Configure(
        DbContextOptionsBuilder options,
        StorageOptions storage,
        string sqliteConnectionString,
        SqlCipherConnectionInterceptor? sqlCipherInterceptor,
        string migrationsHistoryTable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(storage);

        switch (storage.Provider)
        {
            case StorageProvider.Sqlite:
                ArgumentNullException.ThrowIfNull(sqlCipherInterceptor);
                options.UseSqlite(sqliteConnectionString, sqlite =>
                    sqlite.MigrationsHistoryTable(migrationsHistoryTable));
                options.AddInterceptors(sqlCipherInterceptor);
                break;

            case StorageProvider.Postgres:
                if (string.IsNullOrWhiteSpace(storage.ConnectionString))
                    throw new InvalidOperationException(
                        "local-node.storage.postgres_connection_string_required: LocalNode:Storage:ConnectionString is required.");
                options.UseNpgsql(storage.ConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(migrationsHistoryTable));
                break;

            default:
                throw new InvalidOperationException($"local-node.storage.provider_unknown: '{storage.Provider}'.");
        }
    }

    /// <summary>
    /// A check-constraint body proving <paramref name="column"/> holds 64 upper-case hex digits, in the
    /// dialect of the provider the context runs on: SQLite has GLOB and no regular expressions;
    /// Postgres has the reverse.
    /// </summary>
    internal static string HexDigest64Check(DatabaseFacade database, string column) =>
        IsPostgres(database)
            ? $"length({column}) = 64 AND {column} ~ '^[0-9A-F]*$'"
            : $"length({column}) = 64 AND {column} NOT GLOB '*[^0-9A-F]*'";

    internal static bool IsPostgres(DatabaseFacade database) =>
        string.Equals(database.ProviderName, NpgsqlProviderName, StringComparison.Ordinal);

    internal static async Task MigrateOrEnsureCreatedAsync(
        DatabaseFacade database,
        CancellationToken cancellationToken)
    {
        if (!IsPostgres(database))
        {
            await database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // ponytail: Postgres schema comes from the model, not migrations; the first model change after R1 needs Postgres migrations.
        // The node owns several independent DbContext models in one database, and EnsureCreated stops
        // as soon as it finds any table, so each context creates its own tables when they are absent.
        var creator = database.GetService<IRelationalDatabaseCreator>();
        if (!await creator.ExistsAsync(cancellationToken).ConfigureAwait(false))
            await creator.CreateAsync(cancellationToken).ConfigureAwait(false);
        if (!await ModelTablesExistAsync(database, cancellationToken).ConfigureAwait(false))
            await creator.CreateTablesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> ModelTablesExistAsync(
        DatabaseFacade database,
        CancellationToken cancellationToken)
    {
        var tableNames = database.GetService<ICurrentDbContext>().Context.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .Where(tableName => !string.IsNullOrWhiteSpace(tableName))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (tableNames.Length == 0)
            return true;

        var quotedTableNames = string.Join(", ", tableNames.Select(tableName =>
            "'" + tableName!.Replace("'", "''", StringComparison.Ordinal) + "'"));
        await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = database.GetDbConnection().CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM information_schema.tables " +
                "WHERE table_schema = current_schema() AND table_name IN (" + quotedTableNames + ")";
            var existing = Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            return existing == tableNames.Length;
        }
        finally
        {
            await database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
