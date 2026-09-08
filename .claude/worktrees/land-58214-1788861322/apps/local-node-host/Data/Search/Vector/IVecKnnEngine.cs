using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The vector KNN seam (ADR 0135 KG-search F3-lift amendment, Slice 1b) — runs a clipped k-nearest-neighbour
/// scan over the indexed embeddings for ONE tenant, narrowed to an authorized record set. This is the vector
/// half of the G-1 exact clip: the engine is handed the <see cref="AuthorizedRecordScope"/> and MUST apply it
/// as a <b>pre-filter on <c>record_id</c> BEFORE distance</b> (never a post-filter) — so a forbidden record's
/// vector never participates in the ranking and the returned k is drawn from the allowed set even when the
/// global-nearest vectors are all forbidden (the no-neighbour-leak proof; spike R-1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two implementations, identical clip semantics.</b>
/// <list type="bullet">
///   <item><see cref="Sqlite.Vec0KnnEngine"/> — the real <c>sqlite-vec</c> (<c>vec0</c>) path: loads the
///     digest-pinned native onto the SQLCipher connection, runs a brute-force <c>MATCH</c> KNN with the clip
///     baked into <c>WHERE record_id IN (…)</c> (a metadata column, NOT a partition key — the spike-proven
///     ~1× storage design). Used when the native is present + opted-in.</item>
///   <item><see cref="BruteForce.BruteForceKnnEngine"/> — a deterministic pure-C# cosine/Hamming scan over the
///     SAME per-subject-decrypted vectors with the SAME <c>record_id</c> pre-filter. It computes REAL nearest
///     neighbours over real vectors, so the clip-exactness / G-6 / RRF security properties are genuinely tested
///     on a host without the vec0 native (this Intel CI host). It is NOT a stub that bypasses the clip — the
///     pre-filter is applied identically.</item>
/// </list>
/// Because both engines pre-filter <c>record_id</c> before distance, the clip-exactness property is faithful on
/// either; an arch-fence asserts the vec0 SQL path is clip-bound so neither can become a side door.
/// </para>
/// </remarks>
public interface IVecKnnEngine
{
    /// <summary>
    /// Runs a clipped KNN scan against the indexed embeddings on the OPEN, KEYED <paramref name="connection"/>
    /// inside the caller's read transaction (G-2 — the grant read + the scan share one transaction).
    /// </summary>
    /// <param name="connection">The open, SQLCipher-keyed node connection (vec0 already loaded if real-path).</param>
    /// <param name="transaction">The read transaction the scan runs inside (G-2 same-txn TOCTOU closure).</param>
    /// <param name="tenantId">The tenant to search within (the in-file isolation predicate).</param>
    /// <param name="scope">The fail-closed authorized-record scope — the SOLE producer of the record pre-filter (G-1).</param>
    /// <param name="queryVector">The query embedding to rank against (already produced by the embedding provider).</param>
    /// <param name="k">The number of nearest neighbours to return (drawn from the allowed set).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The top-k authorized record ids + their distance, nearest first.</returns>
    Task<IReadOnlyList<VecKnnHit>> KnnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        AuthorizedRecordScope scope,
        IReadOnlyList<float> queryVector,
        int k,
        CancellationToken ct);
}

/// <summary>One KNN result — an authorized record id and its distance to the query (smaller = nearer).</summary>
/// <param name="RecordId">The authorized record id.</param>
/// <param name="Distance">The distance to the query vector (cosine/Hamming per quant; smaller is nearer).</param>
public readonly record struct VecKnnHit(string RecordId, double Distance);
