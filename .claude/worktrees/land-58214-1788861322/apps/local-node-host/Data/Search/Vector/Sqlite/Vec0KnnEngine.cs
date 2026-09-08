using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector.Sqlite;

/// <summary>
/// The real <c>sqlite-vec</c> (<c>vec0</c>) KNN engine (ADR 0135 KG-search F3-lift amendment, Slice 1b) — runs a
/// brute-force bit-vector KNN over the derived <c>search_vec0</c> virtual table, with the EXACT clip baked into
/// the query as a metadata-column <c>record_id IN (…)</c> pre-filter (NOT a partition key — the spike-proven
/// ~1× storage design). This is the production accelerator; it is used only when <see cref="Vec0Native"/> loaded
/// the native (opt-in + bundled). On a host without the native, the brute-force engine is used instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>G-1 — the clip is the SOLE producer of the vec0 query's record narrowing.</b> The KNN SQL is built HERE,
/// from the scope, via <see cref="VecRecordClip"/>: every <c>vec0</c> KNN this class issues carries the clip's
/// <c>record_id</c> constraint, and an arch-fence asserts no other production source issues a <c>vec0 MATCH</c>.
/// <c>sqlite-vec</c> applies the metadata <c>record_id</c> constraint as a TRUE pre-filter (spike R-1: returns
/// k from the allowed set even when the global-nearest are forbidden) — exact, no neighbour leak.
/// </para>
/// <para>
/// <b>The vec0 table holds cleartext bit codes (file-encrypted only).</b> It is the derived, rebuildable,
/// purged-on-shred acceleration cache of the durable per-subject-encrypted rows — so a crypto-shredded subject's
/// vec0 entry is deleted alongside its durable row (G-6). The query embedding is binary-quantized to the same
/// bit code before the KNN.
/// </para>
/// </remarks>
public sealed class Vec0KnnEngine : IVecKnnEngine
{
    private readonly int _dimension;

    /// <summary>Construct for the index's embedding dimension (the vec0 bit-vector width, e.g. 1024 for BGE-M3).</summary>
    public Vec0KnnEngine(int dimension)
    {
        if (dimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension), "Embedding dimension must be positive.");
        }
        _dimension = dimension;
    }

    /// <summary>
    /// Ensures the <c>search_vec0</c> virtual table exists on the (vec0-loaded) connection. Idempotent. The
    /// table holds a <c>bit[N]</c> vector column keyed by an explicit text <c>record_id</c> primary key (the clip
    /// key) — NOT a partition key (the spike's storage-fatal design). Called once at index bootstrap by the
    /// composition when the native loaded.
    /// </summary>
    public async Task EnsureTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildCreateTableSql();
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the v0.1.9 vec0 schema. Tenant is ordinary metadata (no <c>+</c>): auxiliary columns cannot be
    /// constrained in a KNN query, while metadata columns are applied as true prefilters.
    /// </summary>
    internal string BuildCreateTableSql() =>
        $"CREATE VIRTUAL TABLE IF NOT EXISTS {VecIndexConstants.Vec0TableName} USING vec0(" +
        $"record_id text PRIMARY KEY, tenant_id text, embedding bit[{_dimension}]);";

    /// <inheritdoc />
    public async Task<IReadOnlyList<VecKnnHit>> KnnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        AuthorizedRecordScope scope,
        IReadOnlyList<float> queryVector,
        int k,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(queryVector);
        if (scope.IsEmpty)
        {
            return Array.Empty<VecKnnHit>();
        }

        var queryCode = BinaryQuantization.Pack(queryVector);

        // G-1: the clip's record_id constraint is the SOLE record narrowing — built from the scope, here.
        var (clipSql, clipBinder) = VecRecordClip.Build(scope);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        // sqlite-vec KNN with record-primary-key and tenant-metadata prefilters ANDed in. The record_id
        // constraint is applied as a TRUE pre-filter by sqlite-vec (spike R-1).
        cmd.CommandText = BuildKnnSql(clipSql);
        cmd.Parameters.AddWithValue("$q", queryCode);
        cmd.Parameters.AddWithValue("$k", Math.Max(1, k));
        cmd.Parameters.AddWithValue("$tenant", tenantId);
        clipBinder(cmd);

        var hits = new List<VecKnnHit>(k);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            hits.Add(new VecKnnHit(reader.GetString(0), reader.GetDouble(1)));
        }
        return hits;
    }

    /// <summary>
    /// Builds a clipped v0.1.9 KNN query. <c>vec_bit</c> supplies sqlite-vec's bit-vector subtype; an untyped
    /// BLOB is interpreted as float32 and is rejected by a <c>bit[N]</c> column.
    /// </summary>
    internal static string BuildKnnSql(string clipSql) =>
        $"""
            SELECT record_id, distance
            FROM {VecIndexConstants.Vec0TableName}
            WHERE embedding MATCH vec_bit($q)
              AND k = $k
              AND tenant_id = $tenant
              AND {clipSql}
            ORDER BY distance;
            """;
}
