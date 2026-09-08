using System;
using System.Collections.Generic;
using System.Linq;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// Reciprocal-rank fusion (ADR 0135 KG-search F3-lift amendment, Slice 1b) — merges the dense (<c>vec0</c> KNN)
/// and lexical (FTS5) result lists into one ranking by RANK, not raw score. RRF is score-scale-agnostic (it uses
/// only each list's position), which is why it fuses a Hamming distance and a bm25 score without normalization.
/// </summary>
/// <remarks>
/// Each candidate's fused score is <c>Σ_lists 1 / (k + rank)</c> over the lists it appears in (rank is 0-based;
/// the canonical RRF constant <c>k = 60</c> damps the contribution of low-ranked items). A record near the top
/// of EITHER list scores well; a record near the top of BOTH scores best. The fused list is the input to the
/// reranker rescore (deferred to Slice 1d — the eval/wire slice).
/// </remarks>
public static class Rrf
{
    /// <summary>The canonical RRF damping constant.</summary>
    public const int DefaultK = 60;

    /// <summary>
    /// Fuses ranked lists of record ids (each already in best-first order) into one RRF-ordered list. Ties in
    /// the fused score break by record id (ordinal) for deterministic ordering.
    /// </summary>
    /// <param name="rankedLists">The per-source ranked id lists (e.g. [vec0-knn-ids, fts5-ids]).</param>
    /// <param name="k">The RRF damping constant (defaults to <see cref="DefaultK"/>).</param>
    /// <returns>The fused record ids, best-first.</returns>
    public static IReadOnlyList<string> Fuse(
        IEnumerable<IReadOnlyList<string>> rankedLists, int k = DefaultK)
    {
        ArgumentNullException.ThrowIfNull(rankedLists);

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var list in rankedLists)
        {
            if (list is null)
            {
                continue;
            }
            for (var rank = 0; rank < list.Count; rank++)
            {
                var id = list[rank];
                var contribution = 1.0 / (k + rank);
                scores[id] = scores.TryGetValue(id, out var prior) ? prior + contribution : contribution;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key)
            .ToArray();
    }
}
