using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// The clipped read surface over the local-first knowledge-graph "360-view" (ADR 0135 KG-search F3-lift
/// amendment, Slice 0). It is the <b>ONLY</b> way to read the FTS5 / graph index, and EVERY read it runs is
/// clipped through the fail-closed <see cref="IAuthorizedRecordSetProjection"/> (G-1). There is no public
/// method, and no read path — neither the FTS5 <c>MATCH</c> nor a raw read of the underlying
/// <c>search_nodes</c> content table — that returns rows without the clip's <c>WHERE</c>. Both paths are
/// fenced by <c>SearchClipArchFence</c>, which is what makes an un-clipped query structurally impossible
/// (the FTS index AND the content table it mirrors are both covered).
/// </summary>
/// <remarks>
/// <para>
/// <b>G-1 — the clip is the sole producer of the record-narrowing <c>WHERE</c>.</b> Both
/// <see cref="SearchAsync"/> (FTS5) and <see cref="ExpandAsync"/> (graph) resolve an
/// <see cref="AuthorizedRecordScope"/> from the projection FIRST, then build the query's <c>WHERE</c>
/// EXCLUSIVELY from that scope. The raw FTS5 <c>MATCH</c> and the recursive-CTE graph walk are
/// <c>private</c> and always take the scope; nothing outside this class can issue a <c>MATCH</c> against
/// the index. The graph walk applies the clip at EVERY hop (the recursive member is clipped, not only the
/// output projection), so a traversal can never pass through an unauthorized node. An empty scope
/// short-circuits to an empty result WITHOUT touching the index (defence in depth).
/// </para>
/// <para>
/// <b>G-2 — one fenced snapshot.</b> Closure freshness reconciliation, closure resolution, and the FTS5 or
/// graph scan run on one <see cref="SqliteConnection"/> inside one <c>BEGIN IMMEDIATE</c> transaction. A
/// grant, binding, or definition mutation is therefore wholly before or after the protected read.
/// </para>
/// <para>
/// <b>Tenant scope + isolation.</b> Every query carries an explicit <c>tenant_id = $tenant</c> predicate
/// on top of the record clip. The per-tenant encrypted FILE is the outer isolation boundary; the
/// <c>tenant_id</c> column is the in-file defence-in-depth check (ADR 0092 — no ambient query filter).
/// </para>
/// </remarks>
public sealed class NodeSearchReadService
{
    /// <summary>Default maximum number of hits returned by a search.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Hard upper bound on the result limit.</summary>
    public const int MaxLimit = 200;

    private readonly IDbContextFactory<NodeLocalSearchDbContext> _contextFactory;
    private readonly IAuthorizedRecordSetProjection _clip;

    /// <summary>Construct bound to the search context factory and the fail-closed clip seam.</summary>
    /// <param name="contextFactory">The node-local search EF context factory (the SQLCipher file).</param>
    /// <param name="clip">The fail-closed authorized-record-set projection — the SOLE producer of the WHERE (G-1).</param>
    public NodeSearchReadService(
        IDbContextFactory<NodeLocalSearchDbContext> contextFactory,
        IAuthorizedRecordSetProjection clip)
    {
        _contextFactory = contextFactory ?? throw new System.ArgumentNullException(nameof(contextFactory));
        _clip = clip ?? throw new System.ArgumentNullException(nameof(clip));
    }

    /// <summary>
    /// Full-text search (FTS5, trigram tokenizer) over the indexed node text for
    /// <paramref name="principalId"/> within <paramref name="tenantId"/>, returning ONLY records the
    /// principal is authorized to see (G-1). Multilingual: the trigram tokenizer gives non-zero CJK recall
    /// (spike R-3).
    /// </summary>
    /// <param name="tenantId">The tenant to search within.</param>
    /// <param name="principalId">The acting principal whose authorization clips the results.</param>
    /// <param name="queryText">The user's search text.</param>
    /// <param name="at">The instant the clip evaluates grant active/expiry at (G-2 same snapshot).</param>
    /// <param name="limit">Max hits (clamped to <see cref="MaxLimit"/>).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        TenantId tenantId,
        ActorId principalId,
        string queryText,
        System.DateTimeOffset at,
        int limit = DefaultLimit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryText);
        var effectiveLimit = System.Math.Clamp(limit, 1, MaxLimit);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        IReadOnlyList<SearchHit> result = System.Array.Empty<SearchHit>();
        await HomeEpochFenceTransaction.RunAsync(ctx, async () =>
        {
            var connection = (SqliteConnection)ctx.Database.GetDbConnection();
            var tx = (SqliteTransaction)ctx.Database.CurrentTransaction!.GetDbTransaction();
            var scope = await _clip.ResolveAsync(
                connection, tx, tenantId, principalId, at, ct).ConfigureAwait(false);
            if (!scope.IsEmpty)
            {
                result = await RunClippedFtsAsync(
                    connection, tx, tenantId.ToString(), scope, queryText, effectiveLimit, ct)
                    .ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Expand a 1–2 hop neighbourhood around a seed record for <paramref name="principalId"/> within
    /// <paramref name="tenantId"/>, returning ONLY authorized nodes (G-1). The recursive-CTE walk crosses
    /// an edge only to an authorized target — an edge to an unauthorized record is never traversed, so the
    /// graph walk cannot widen the result past the clip.
    /// </summary>
    /// <param name="tenantId">The tenant the seed + neighbourhood belong to.</param>
    /// <param name="principalId">The acting principal whose authorization clips the neighbourhood.</param>
    /// <param name="seedRecordId">The record to expand around.</param>
    /// <param name="at">The instant the clip evaluates grant active/expiry at (G-2 same snapshot).</param>
    /// <param name="maxHops">1 or 2 hops (clamped to [1, 2]).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<SearchHit>> ExpandAsync(
        TenantId tenantId,
        ActorId principalId,
        string seedRecordId,
        System.DateTimeOffset at,
        int maxHops = 1,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(seedRecordId);
        var hops = System.Math.Clamp(maxHops, 1, 2);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        IReadOnlyList<SearchHit> result = System.Array.Empty<SearchHit>();
        await HomeEpochFenceTransaction.RunAsync(ctx, async () =>
        {
            var connection = (SqliteConnection)ctx.Database.GetDbConnection();
            var tx = (SqliteTransaction)ctx.Database.CurrentTransaction!.GetDbTransaction();
            var scope = await _clip.ResolveAsync(
                connection, tx, tenantId, principalId, at, ct).ConfigureAwait(false);
            if (!scope.IsEmpty && scope.Authorizes(seedRecordId))
            {
                result = await RunClippedExpandAsync(
                    connection, tx, tenantId.ToString(), scope, seedRecordId, hops, ct)
                    .ConfigureAwait(false);
            }
        }, ct).ConfigureAwait(false);
        return result;
    }

    // ── PRIVATE: the only place a raw FTS5 MATCH is issued — always built from the scope's WHERE (G-1) ──

    private static async Task<IReadOnlyList<SearchHit>> RunClippedFtsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tenantId,
        AuthorizedRecordScope scope,
        string queryText,
        int limit,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;

        // The clip's WHERE is the SOLE record-narrowing predicate. tenant_id is the in-file isolation check.
        var (clipSql, clipBinder) = BuildRecordClip(scope, alias: "n");

        // Query search_fts un-aliased and JOIN the node table for the projected columns + the clip.
        cmd.CommandText = $"""
            SELECT n.record_id, n.tenant_id, n.node_type, n.title, n.body
            FROM search_fts
            JOIN search_nodes n ON n.tenant_id = search_fts.tenant_id
                               AND n.record_id = search_fts.record_id
            WHERE search_fts MATCH $query
              AND n.tenant_id = $tenant
              AND {clipSql};
            """;
        cmd.Parameters.AddWithValue("$query", BuildMatchExpression(queryText));
        cmd.Parameters.AddWithValue("$tenant", tenantId);
        clipBinder(cmd);

        var clippedMatches = await ReadHitsAsync(cmd, ct).ConfigureAwait(false);

        // ponytail: this materializes every clipped match before paging. Replace it with a bounded,
        // clip-local ranking/index strategy when authorized candidate-set size reaches that ceiling.
        return ClipLocalSearchRanker.Rank(clippedMatches, queryText, limit);
    }

    private static async Task<IReadOnlyList<SearchHit>> RunClippedExpandAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tenantId,
        AuthorizedRecordScope scope,
        string seedRecordId,
        int hops,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;

        // Recursive CTE walking the undirected neighbourhood, clipped at EVERY HOP — NOT only at the output
        // projection (the F1 fix; security-engineering verdict earlier repository ticket #1370). A candidate frontier record is
        // admitted to the walk ONLY if the clip authorizes it: the clip predicate is applied INSIDE the
        // recursive member, against the candidate's joined node row (alias `nf`). The seed is already verified
        // authorized by the caller, so the anchor needs no re-check. Because no UNAUTHORIZED node ever enters
        // the frontier, the walk cannot pass THROUGH a forbidden intermediary to reach an authorized distal
        // node — closing the graph-STRUCTURE inference channel (output-only clipping blocked forbidden rows
        // but still surfaced an authorized node reachable solely via a hidden one). depth < $hops bounds it
        // to 1–2 hops. SELECT DISTINCT de-duplicates a record reached by multiple paths/depths (the F2 fix —
        // the CTE UNIONs on (record_id, depth), so a node reached at several depths produced duplicate rows).
        var (clipFrontierSql, clipFrontierBinder) = BuildRecordClip(scope, alias: "nf");
        var (clipOutSql, _) = BuildRecordClip(scope, alias: "n"); // same predicate, output alias (defence in depth)

        cmd.CommandText = $"""
            WITH RECURSIVE reachable(record_id, depth) AS (
                SELECT $seed, 0
                UNION
                SELECT nf.record_id, r.depth + 1
                FROM reachable r
                JOIN search_edges e
                  ON e.tenant_id = $tenant
                 AND (e.source_record_id = r.record_id OR e.target_record_id = r.record_id)
                JOIN search_nodes nf
                  ON nf.record_id = CASE WHEN e.source_record_id = r.record_id
                                         THEN e.target_record_id ELSE e.source_record_id END
                 AND nf.tenant_id = $tenant
                WHERE r.depth < $hops
                  AND {clipFrontierSql}
            )
            SELECT DISTINCT n.record_id, n.tenant_id, n.node_type, n.title, n.body
            FROM reachable rc
            JOIN search_nodes n ON n.record_id = rc.record_id
            WHERE n.tenant_id = $tenant
              AND {clipOutSql}
            ORDER BY n.node_type, n.record_id;
            """;
        cmd.Parameters.AddWithValue("$seed", seedRecordId);
        cmd.Parameters.AddWithValue("$tenant", tenantId);
        cmd.Parameters.AddWithValue("$hops", hops);
        clipFrontierBinder(cmd);

        return await ReadHitsAsync(cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the record-narrowing <c>WHERE</c> fragment + its parameter binder EXCLUSIVELY from the scope.
    /// This is the ONLY producer of the clip predicate (G-1). Whole-tenant ⇒ a tautology that still leaves
    /// the explicit <c>tenant_id = $tenant</c> predicate as the sole narrowing (never cross-tenant). An
    /// explicit id set ⇒ <c>record_id IN ($r0, $r1, …)</c> with parameterized ids. (An empty scope never
    /// reaches here — the callers short-circuit it.)
    /// </summary>
    private static (string sql, System.Action<SqliteCommand> binder) BuildRecordClip(
        AuthorizedRecordScope scope, string alias)
    {
        if (scope.WholeTenant)
        {
            // Whole-tenant: no per-record narrowing beyond the tenant predicate the caller already applied.
            return ("1 = 1", static _ => { });
        }

        var ids = new List<string>(scope.RecordIds);
        // Parameterize each id (never interpolate — injection-safe + plan-stable).
        var names = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            names[i] = "$r" + i.ToString(CultureInfo.InvariantCulture);
        }

        var sql = $"{alias}.record_id IN ({string.Join(", ", names)})";
        void Binder(SqliteCommand cmd)
        {
            for (var i = 0; i < ids.Count; i++)
            {
                cmd.Parameters.AddWithValue(names[i], ids[i]);
            }
        }

        return (sql, Binder);
    }

    private static async Task<IReadOnlyList<SearchHit>> ReadHitsAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            hits.Add(new SearchHit(
                RecordId: reader.GetString(0),
                TenantId: reader.GetString(1),
                NodeType: reader.GetString(2),
                Title: reader.GetString(3),
                Body: reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
        }

        return hits;
    }

    /// <summary>
    /// Builds an FTS5 MATCH expression for the user's query text. Each whitespace-delimited token is wrapped
    /// as a quoted prefix term (<c>"token"*</c>) so the trigram tokenizer matches substrings (incl. CJK,
    /// where there is no whitespace), and double-quotes inside a token are escaped so the MATCH grammar is
    /// not breakable by user input.
    /// </summary>
    private static string BuildMatchExpression(string queryText)
    {
        var tokens = queryText.Split(
            (char[]?)null, System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            // The whole string had no whitespace-delimited tokens (e.g. a single CJK run) — quote it whole.
            return "\"" + queryText.Replace("\"", "\"\"") + "\"*";
        }

        for (var i = 0; i < tokens.Length; i++)
        {
            tokens[i] = "\"" + tokens[i].Replace("\"", "\"\"") + "\"*";
        }

        return string.Join(" ", tokens);
    }
}

/// <summary>One result row of a clipped KG / FTS5 search — the search-facing projection of a node.</summary>
/// <param name="RecordId">The source record id.</param>
/// <param name="TenantId">The tenant the record belongs to.</param>
/// <param name="NodeType">The kind of domain entity (party / lease / invoice / …).</param>
/// <param name="Title">The record's display title.</param>
/// <param name="Body">The record's body text.</param>
public readonly record struct SearchHit(
    string RecordId,
    string TenantId,
    string NodeType,
    string Title,
    string Body);
