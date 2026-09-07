using System;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector.Sqlite;

/// <summary>
/// Writes/deletes the derived cleartext bit-code rows in the <c>search_vec0</c> virtual table (ADR 0135
/// KG-search F3-lift amendment, Slice 1b). Used ONLY when the <c>vec0</c> native loaded; the indexer drives it
/// alongside the durable per-subject-encrypted row so the acceleration cache stays in lock-step and is
/// purged-on-shred (G-6 — no cleartext residue for an erased subject).
/// </summary>
/// <remarks>
/// The connection is opened + keyed via the search context's factory and the native is loaded per-connection by
/// the composition's connection bootstrap. This sink assumes the <c>search_vec0</c> table already exists (the
/// composition calls <see cref="Vec0KnnEngine.EnsureTableAsync"/> at bootstrap).
/// </remarks>
public sealed class Vec0AccelerationSink : IVecAccelerationSink
{
    private readonly IDbContextFactory<NodeLocalSearchDbContext> _contextFactory;

    /// <summary>Construct bound to the search context factory (the vec0-loaded SQLCipher connection source).</summary>
    public Vec0AccelerationSink(IDbContextFactory<NodeLocalSearchDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <inheritdoc />
    public async Task UpsertAsync(string recordId, string tenantId, byte[] packedCode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        ArgumentNullException.ThrowIfNull(packedCode);

        await using var dbCtx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var connection = (SqliteConnection)dbCtx.Database.GetDbConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var del = connection.CreateCommand();
        del.CommandText = $"DELETE FROM {VecIndexConstants.Vec0TableName} WHERE record_id = $r;";
        del.Parameters.AddWithValue("$r", recordId);
        await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await using var ins = connection.CreateCommand();
        ins.CommandText = UpsertCommandText;
        ins.Parameters.AddWithValue("$r", recordId);
        ins.Parameters.AddWithValue("$t", tenantId);
        ins.Parameters.AddWithValue("$e", packedCode);
        await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string recordId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordId);

        await using var dbCtx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var connection = (SqliteConnection)dbCtx.Database.GetDbConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var del = connection.CreateCommand();
        del.CommandText = $"DELETE FROM {VecIndexConstants.Vec0TableName} WHERE record_id = $r;";
        del.Parameters.AddWithValue("$r", recordId);
        await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The v0.1.9 bit-vector insert contract. <c>vec_bit</c> adds sqlite-vec's bit subtype to the packed BLOB;
    /// without it sqlite-vec treats the value as float32.
    /// </summary>
    internal static string UpsertCommandText =>
        $"INSERT INTO {VecIndexConstants.Vec0TableName}(record_id, tenant_id, embedding) " +
        "VALUES ($r, $t, vec_bit($e));";
}
