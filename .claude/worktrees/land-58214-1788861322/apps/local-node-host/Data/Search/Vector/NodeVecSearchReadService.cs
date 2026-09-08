using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.LocalNodeHost.Data.HomeEpoch;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The clipped HYBRID read surface over the local-first KG index (ADR 0135 KG-search F3-lift amendment, Slice
/// 1b) — embeds the query, runs the BGE-M3 dense (<c>vec0</c> KNN) leg AND the FTS5 lexical leg, fuses them by
/// RRF, and returns the clipped, fused ranking. EVERY read it runs is clipped through the fail-closed G-2
/// same-transaction closure projection (G-1 on the vec path).
/// </summary>
/// <remarks>
/// <para>
/// <b>G-1 — the clip is the sole producer of the vec query's record narrowing.</b> The scope is resolved FIRST,
/// then the vec0 KNN and the FTS5 MATCH build their <c>WHERE</c> EXCLUSIVELY from it (via
/// <see cref="VecRecordClip"/> on the vec path). An empty scope short-circuits to an empty result WITHOUT
/// touching the index. The vec KNN engine pre-filters <c>record_id</c> before distance, so a forbidden record's
/// vector never participates and the returned k is from the allowed set even when the global-nearest are all
/// forbidden (the no-neighbour-leak property).
/// </para>
/// <para>
/// <b>G-2 — one read transaction; single-snapshot TOCTOU closure.</b> The grant read (the durable EF grant
/// rows) AND the vec0 / FTS5 scans run on ONE <see cref="SqliteConnection"/> inside ONE
/// <c>BEGIN IMMEDIATE…COMMIT</c> transaction via <see cref="HomeEpochFenceTransaction"/> — so a revoke
/// landing mid-query cannot widen the already-materialized allow-set. This is the closure Slice 0 deferred.
/// </para>
/// </remarks>
public sealed class NodeVecSearchReadService
{
    /// <summary>Default fused-result limit.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Hard upper bound on the result limit.</summary>
    public const int MaxLimit = 200;

    private readonly IDbContextFactory<NodeLocalSearchDbContext> _contextFactory;
    private readonly IAuthorizedRecordSetProjection _clip;
    private readonly IKgEmbeddingProvider _queryEmbedder;
    private readonly IVecKnnEngine _knnEngine;
    private readonly IKgReranker _reranker;

    /// <summary>
    /// Construct bound to the search file, the G-2 resolver, the query embedder, the KNN engine, and the
    /// cross-encoder reranker. <paramref name="reranker"/> may be null ⇒ the fail-safe
    /// <see cref="NoOpKgReranker"/> (the RRF order is returned unchanged; a missing reranker degrades quality,
    /// never correctness).
    /// </summary>
    public NodeVecSearchReadService(
        IDbContextFactory<NodeLocalSearchDbContext> contextFactory,
        IAuthorizedRecordSetProjection clip,
        IKgEmbeddingProvider queryEmbedder,
        IVecKnnEngine knnEngine,
        IKgReranker? reranker = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _clip = clip ?? throw new ArgumentNullException(nameof(clip));
        _queryEmbedder = queryEmbedder ?? throw new ArgumentNullException(nameof(queryEmbedder));
        _knnEngine = knnEngine ?? throw new ArgumentNullException(nameof(knnEngine));
        _reranker = reranker ?? new NoOpKgReranker();
    }

    /// <summary>
    /// Hybrid clipped search: dense (vec0 KNN) + lexical (FTS5) fused by RRF, returning ONLY records the
    /// principal is authorized to see (G-1), with the grant read inside the scan transaction (G-2).
    /// </summary>
    public async Task<IReadOnlyList<HybridHit>> HybridSearchAsync(
        TenantId tenantId,
        ActorId principalId,
        string queryText,
        DateTimeOffset at,
        int limit = DefaultLimit,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryText);
        var effectiveLimit = Math.Clamp(limit, 1, MaxLimit);

        // The query embedding (dense leg). The provider is M1-fenced; for the read path the model id is not
        // re-checked (the INDEX is the integrity boundary — only real-floor vectors were ever stored), so a
        // stub query embedder is fine for deterministic tests as long as the index holds the matching vectors.
        var queryArtifact = await _queryEmbedder
            .EmbedAsync("__query__", tenantId.Value, subjectId: null, queryText, ct)
            .ConfigureAwait(false);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await HomeEpochFenceTransaction.RunAsync(ctx, async () =>
        {
            var connection = (SqliteConnection)ctx.Database.GetDbConnection();
            var tx = (SqliteTransaction)ctx.Database.CurrentTransaction!.GetDbTransaction();
            var scope = await _clip.ResolveAsync(connection, tx, tenantId, principalId, at, ct)
                .ConfigureAwait(false);
            if (scope.IsEmpty)
            {
                // Fail-closed short-circuit — nothing authorized ⇒ no query runs, empty result.
                return Array.Empty<HybridHit>();
            }

            // Over-fetch each leg so RRF has depth, then truncate the fused list to the limit.
            var legK = Math.Min(MaxLimit, effectiveLimit * 4);

            var denseHits = await _knnEngine
                .KnnAsync(connection, tx, tenantId.Value, scope, queryArtifact.Vector, legK, ct)
                .ConfigureAwait(false);
            var denseIds = denseHits.Select(h => h.RecordId).ToArray();

            var lexicalIds = await RunClippedFtsIdsAsync(
                connection, tx, tenantId.Value, scope, queryText, legK, ct).ConfigureAwait(false);

            // RRF fusion (rank-based; fuses Hamming distance + clip-local term frequency with no normalization). Fuse a DEEPER pool than
            // the final limit so the cross-encoder reranker has a richer candidate set to rescore (the spike's
            // responsive-with-rerank stage); the reranker truncates to the limit.
            var fusionDepth = Math.Min(MaxLimit, Math.Max(effectiveLimit, effectiveLimit * 4));
            var fusedIds = Rrf.Fuse(new[] { denseIds, lexicalIds }).Take(fusionDepth).ToArray();

            var denseRank = denseIds
                .Select((id, i) => (id, i))
                .ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
            var lexicalRank = lexicalIds
                .Select((id, i) => (id, i))
                .ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);

            // Build the rerank candidate pool from the fused ids — defence-in-depth: re-apply the clip so only
            // authorized ids reach the reranker (the reranker re-orders an already-authorized set; it never widens
            // it). Fetch each candidate's document text from search_nodes WITHIN THE SAME read transaction (G-2),
            // so the rerank sees the same single snapshot the clip + scans did.
            var authorizedFused = new List<string>(fusedIds.Length);
            foreach (var id in fusedIds)
            {
                if (scope.Authorizes(id))
                {
                    authorizedFused.Add(id);
                }
            }

            var documentTexts = await FetchDocumentTextsAsync(
                connection, tx, tenantId.Value, scope, authorizedFused, ct).ConfigureAwait(false);

            var candidates = new List<RerankCandidate>(authorizedFused.Count);
            foreach (var id in authorizedFused)
            {
                candidates.Add(new RerankCandidate(
                    id, documentTexts.TryGetValue(id, out var text) ? text : string.Empty));
            }

            // CROSS-ENCODER RERANK — the final relevance re-sort over the clipped, RRF-fused candidates. The
            // reranker returns a SUBSET/re-ordering (never an addition) truncated to the limit; the NoOp reranker
            // preserves the RRF order. A real reranker (bge-reranker-v2-m3) rescores; fail-safe to RRF on a fault.
            var rerankedIds = await _reranker
                .RerankAsync(queryText, candidates, effectiveLimit, ct)
                .ConfigureAwait(false);

            // Project the reranked ids back to hits, recording each id's rank in the dense + lexical legs.
            var result = new List<HybridHit>(rerankedIds.Count);
            foreach (var id in rerankedIds)
            {
                if (!scope.Authorizes(id))
                {
                    continue; // belt-and-braces — the reranker re-orders an authorized set, but never surface an
                              // unauthorized id even if a future reranker were misimplemented.
                }
                result.Add(new HybridHit(
                    RecordId: id,
                    DenseRank: denseRank.TryGetValue(id, out var dr) ? dr : null,
                    LexicalRank: lexicalRank.TryGetValue(id, out var lr) ? lr : null));
            }
            return (IReadOnlyList<HybridHit>)result;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Retrieve the permission-clipped GROUNDING for a generative ask (ADR 0135 KG-search Slice 2-foundation,
    /// G-G2) — runs the SAME clipped hybrid search as <see cref="HybridSearchAsync"/>, then returns each top hit's
    /// authorized document text, all inside ONE read transaction (G-2). The returned grounding is assembled ONLY
    /// from authorized content: the resolved <see cref="AuthorizedRecordScope"/> is the sole record narrowing, and
    /// each grounding row's text is fetched through the clip (defence in depth). A forbidden record NEVER reaches
    /// the grounding — the model only ever sees authorized text.
    /// </summary>
    /// <remarks>
    /// This is the ONE clipped content path the <c>GroundingAssembler</c> consumes; the assembler does NOT issue a
    /// raw <c>search_nodes</c> read (that would be a clip-omitting side door the <c>SearchClipArchFence</c> forbids
    /// — only this read service may read the content table). The returned <see cref="ClippedGrounding.Scope"/> is
    /// what the assembler asserts it was handed (G-G2 — it accepts an <see cref="AuthorizedRecordScope"/>, never a
    /// bare id-list).
    /// </remarks>
    public async Task<ClippedGrounding> RetrieveClippedGroundingAsync(
        TenantId tenantId,
        ActorId principalId,
        string queryText,
        DateTimeOffset at,
        int limit = 8,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(queryText);
        var effectiveLimit = Math.Clamp(limit, 1, MaxLimit);

        var queryArtifact = await _queryEmbedder
            .EmbedAsync("__query__", tenantId.Value, subjectId: null, queryText, ct)
            .ConfigureAwait(false);

        await using var ctx = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await HomeEpochFenceTransaction.RunAsync(ctx, async () =>
        {
            var connection = (SqliteConnection)ctx.Database.GetDbConnection();
            var tx = (SqliteTransaction)ctx.Database.CurrentTransaction!.GetDbTransaction();
            var scope = await _clip.ResolveAsync(connection, tx, tenantId, principalId, at, ct)
                .ConfigureAwait(false);
            if (scope.IsEmpty)
            {
                // Fail-closed: nothing authorized ⇒ empty grounding (the scope is still returned so the assembler can
                // assert it was handed a real, fail-closed scope, not a bare id-list).
                return new ClippedGrounding(scope, Array.Empty<GroundingRow>());
            }

            var legK = Math.Min(MaxLimit, effectiveLimit * 4);

            var denseHits = await _knnEngine
                .KnnAsync(connection, tx, tenantId.Value, scope, queryArtifact.Vector, legK, ct)
                .ConfigureAwait(false);
            var denseIds = denseHits.Select(h => h.RecordId).ToArray();

            var lexicalIds = await RunClippedFtsIdsAsync(
                connection, tx, tenantId.Value, scope, queryText, legK, ct).ConfigureAwait(false);

            var fusedIds = Rrf.Fuse(new[] { denseIds, lexicalIds }).Take(effectiveLimit).ToArray();

            // Defence-in-depth: re-apply the clip so only authorized ids reach the grounding fetch.
            var authorizedFused = new List<string>(fusedIds.Length);
            foreach (var id in fusedIds)
            {
                if (scope.Authorizes(id))
                {
                    authorizedFused.Add(id);
                }
            }

            var documentTexts = await FetchDocumentTextsAsync(
                connection, tx, tenantId.Value, scope, authorizedFused, ct).ConfigureAwait(false);

            var rows = new List<GroundingRow>(authorizedFused.Count);
            foreach (var id in authorizedFused)
            {
                // Belt-and-braces: never surface text for an id the clip does not authorize.
                if (scope.Authorizes(id) && documentTexts.TryGetValue(id, out var text))
                {
                    rows.Add(new GroundingRow(id, text));
                }
            }

            return new ClippedGrounding(scope, rows);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetch the searchable document text (title + body) for the candidate record ids from <c>search_nodes</c>,
    /// inside the caller's read transaction (G-2) and re-narrowed by the clip (defence-in-depth — the text is
    /// only read for authorized ids). Returns a map of record id → its concatenated text for the reranker.
    /// </summary>
    private static async Task<Dictionary<string, string>> FetchDocumentTextsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tenantId,
        AuthorizedRecordScope scope,
        IReadOnlyList<string> recordIds,
        CancellationToken ct)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (recordIds.Count == 0)
        {
            return texts;
        }

        var (clipSql, clipBinder) = VecRecordClip.Build(scope, column: "record_id");
        var (candidateSql, candidateBinder) = VecRecordClip.BuildCandidateIntersection(recordIds);

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT record_id, title, body
            FROM search_nodes
            WHERE tenant_id = $tenant
              AND {clipSql}
              AND {candidateSql};
            """;
        cmd.Parameters.AddWithValue("$tenant", tenantId);
        clipBinder(cmd);
        candidateBinder(cmd);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            var title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            var body = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            texts[id] = string.IsNullOrEmpty(body) ? title : $"{title}\n{body}";
        }
        return texts;
    }

    /// <summary>
    /// Runs the clipped FTS5 MATCH, ranks all authorized matches locally, and returns the top record ids.
    /// Mirrors the Slice-0 read service's clipped FTS path; the clip's WHERE is the sole record narrowing.
    /// </summary>
    private static async Task<IReadOnlyList<string>> RunClippedFtsIdsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string tenantId,
        AuthorizedRecordScope scope,
        string queryText,
        int limit,
        CancellationToken ct)
    {
        var (clipSql, clipBinder) = VecRecordClip.Build(scope, column: "n.record_id");

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
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

        var clippedMatches = new List<SearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            clippedMatches.Add(new SearchHit(
                RecordId: reader.GetString(0),
                TenantId: reader.GetString(1),
                NodeType: reader.GetString(2),
                Title: reader.GetString(3),
                Body: reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
        }
        return ClipLocalSearchRanker.Rank(clippedMatches, queryText, limit)
            .Select(hit => hit.RecordId)
            .ToArray();
    }

    /// <summary>Builds a trigram-friendly FTS5 MATCH expression (same as the Slice-0 read service).</summary>
    private static string BuildMatchExpression(string queryText)
    {
        var tokens = queryText.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return "\"" + queryText.Replace("\"", "\"\"") + "\"*";
        }
        for (var i = 0; i < tokens.Length; i++)
        {
            tokens[i] = "\"" + tokens[i].Replace("\"", "\"\"") + "\"*";
        }
        return string.Join(" ", tokens);
    }
}

/// <summary>One hybrid (RRF-fused) result — a record + its rank in each leg it appeared in (null = absent from that leg).</summary>
/// <param name="RecordId">The authorized record id.</param>
/// <param name="DenseRank">0-based rank in the dense (vec0) leg, or null if it did not appear there.</param>
/// <param name="LexicalRank">0-based rank in the lexical (FTS5) leg, or null if it did not appear there.</param>
public readonly record struct HybridHit(string RecordId, int? DenseRank, int? LexicalRank);

/// <summary>
/// The permission-clipped grounding retrieved for a generative ask (ADR 0135 KG-search Slice 2-foundation, G-G2)
/// — the resolved fail-closed <see cref="AuthorizedRecordScope"/> plus the authorized grounding rows. The
/// <c>GroundingAssembler</c> accepts THIS (which carries the scope), never a bare id-list — so every grounding
/// row is provably from the authorized set.
/// </summary>
/// <param name="Scope">The fail-closed scope the grounding was retrieved under (the structural G-G2 authorization scope).</param>
/// <param name="Rows">The authorized grounding rows (record id + clipped document text), top-ranked first.</param>
public sealed record ClippedGrounding(
    AuthorizedRecordScope Scope,
    IReadOnlyList<GroundingRow> Rows);

/// <summary>One authorized grounding row — an authorized record id + its clipped document text (UNTRUSTED).</summary>
/// <param name="RecordId">The authorized record id (always in the clip's scope).</param>
/// <param name="Text">The record's document text — UNTRUSTED (may carry a stored injection); taint-labeled downstream.</param>
public readonly record struct GroundingRow(string RecordId, string Text);
