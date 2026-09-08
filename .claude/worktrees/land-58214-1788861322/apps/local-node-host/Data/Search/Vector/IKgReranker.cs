using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The cross-encoder rerank seam (ADR 0135 KG-search F3-lift amendment, Slice 1d) — rescores a candidate set
/// of <c>(recordId, documentText)</c> pairs against the query by relevance, returning record ids best-first.
/// Slice 1b fuses the dense + lexical legs by RRF; this reranker is the final-stage rescore over the
/// RRF-fused top-k (the dominant query-time CPU cost — invoked only on an explicit AI-ask, never the
/// keystroke path; the spike measured ~650 ms top-20 on the Intel floor).
/// </summary>
/// <remarks>
/// <para>
/// <b>The rerank runs AFTER the clip.</b> The candidates handed to <see cref="RerankAsync"/> are already the
/// authorized, RRF-fused set — the reranker only re-orders them, it never widens the result. So the G-1 clip
/// remains the sole record-narrowing boundary; the reranker is a pure relevance re-sort over an
/// already-authorized set (it cannot leak a forbidden record because it is never handed one).
/// </para>
/// <para>
/// <b>Two implementations.</b> The real one (<see cref="Cli.KgCliReranker"/>) drives the capability
/// <c>rerank</c> runtime (bge-reranker-v2-m3 on the G-4 sandboxed CPU floor) over the <c>kg-embed</c> CLI;
/// the no-op (<see cref="NoOpKgReranker"/>) preserves the RRF order, the fail-safe default for a host with no
/// rerank provider (rerank is a quality enhancement, not a correctness gate — a missing reranker degrades to
/// the RRF order, never to a wrong/leaky result).
/// </para>
/// </remarks>
public interface IKgReranker
{
    /// <summary>
    /// Rescore <paramref name="candidates"/> (already clipped + RRF-fused) against <paramref name="queryText"/>
    /// and return their record ids best-first, truncated to <paramref name="topK"/>. The returned set is a
    /// SUBSET/re-ordering of the input ids — never an addition.
    /// </summary>
    Task<IReadOnlyList<string>> RerankAsync(
        string queryText,
        IReadOnlyList<RerankCandidate> candidates,
        int topK,
        CancellationToken ct = default);
}

/// <summary>One rerank candidate — an authorized record id + its document text to score against the query.</summary>
/// <param name="RecordId">The authorized record id (from the clipped, RRF-fused set).</param>
/// <param name="DocumentText">The record's text the cross-encoder scores against the query.</param>
public readonly record struct RerankCandidate(string RecordId, string DocumentText);

/// <summary>
/// The fail-safe no-op reranker — preserves the input (RRF) order, truncated to topK. The default for a host
/// with no rerank provider: rerank is a quality enhancement, so its absence degrades to the RRF ranking, never
/// to a wrong result.
/// </summary>
public sealed class NoOpKgReranker : IKgReranker
{
    /// <inheritdoc />
    public Task<IReadOnlyList<string>> RerankAsync(
        string queryText,
        IReadOnlyList<RerankCandidate> candidates,
        int topK,
        CancellationToken ct = default)
    {
        var n = System.Math.Min(System.Math.Max(0, topK), candidates.Count);
        var ids = new List<string>(n);
        for (var i = 0; i < n; i++)
        {
            ids.Add(candidates[i].RecordId);
        }
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }
}
