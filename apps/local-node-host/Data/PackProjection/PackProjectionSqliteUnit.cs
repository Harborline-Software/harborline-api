using System.Data;
using Harborline.Api.Foundation.Definitions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.PackProjection;

/// <summary>The one SQLite transaction shared by pack lifecycle and authorization projection writes.</summary>
internal sealed class PackProjectionSqliteUnit : IPackProjectionDurableUnit
{
    private readonly DbContext owner;
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly string connectionString;

    internal PackProjectionSqliteUnit(DbContext owner)
    {
        this.owner = owner;
        connection = (SqliteConnection)owner.Database.GetDbConnection();
        connectionString = connection.ConnectionString;
        owner.Database.OpenConnection();
        transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
        owner.Database.UseTransaction(transaction);
    }

    internal T Join<T>(T context) where T : DbContext
    {
        // A second database cannot participate in this activation's atomic commit.
        if (!string.Equals(context.Database.GetConnectionString(), connectionString, StringComparison.Ordinal))
        {
            context.Dispose();
            throw new InvalidOperationException("Pack projection stores must share the same SQLite database.");
        }
        var original = context.Database.GetDbConnection();
        if (!ReferenceEquals(original, connection) && original.State != ConnectionState.Closed) original.Close();
        context.Database.SetDbConnection(connection, contextOwnsConnection: false);
        context.Database.UseTransaction(transaction);
        return context;
    }

    public void Commit() => transaction.Commit();

    public void Dispose()
    {
        // Closing an uncommitted transaction rolls it back. No domain callbacks run here.
        try { transaction.Dispose(); }
        finally { owner.Dispose(); }
    }
}
