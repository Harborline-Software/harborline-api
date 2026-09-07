using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector.Cli;

/// <summary>
/// The REAL <see cref="IKgReranker"/> (ADR 0135 KG-search F3-lift amendment, Slice 1d) — drives the capability
/// <c>rerank</c> runtime (bge-reranker-v2-m3 cross-encoder on the G-4 sandboxed CPU floor) through the
/// <c>kg-embed</c> CLI (the agent-client doctrine: CLI(<c>--json</c>) + SDK, NOT MCP). Sends the
/// candidate document texts + the query, receives <c>{ index, score }</c> pairs sorted best-first, and maps
/// the returned indices back to the candidate record ids.
/// </summary>
/// <remarks>
/// <b>Fail-safe, not fail-closed.</b> Rerank is a relevance ENHANCEMENT over an already-authorized, RRF-fused
/// set — not a correctness gate. So if the CLI invoke fails, the reranker degrades to the input (RRF) order
/// rather than throwing: a missing rerank never produces a WRONG or LEAKY result (the clip already authorized
/// the set), only a less-optimally-ordered one. This is the opposite posture from the embedding provider
/// (which fails CLOSED, because a missing/unverifiable embedding must NOT be indexed as real).
/// </remarks>
public sealed class KgCliReranker : IKgReranker
{
    private readonly CapabilityKgCliClient _cli;
    private readonly int _timeoutMs;

    /// <summary>Construct bound to the capability CLI client + the per-invoke timeout.</summary>
    public KgCliReranker(CapabilityKgCliClient cli, int timeoutMs)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _timeoutMs = timeoutMs > 0 ? timeoutMs : 120_000;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> RerankAsync(
        string queryText,
        IReadOnlyList<RerankCandidate> candidates,
        int topK,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queryText);
        ArgumentNullException.ThrowIfNull(candidates);

        var effectiveTopK = Math.Min(Math.Max(0, topK), candidates.Count);
        if (effectiveTopK == 0 || candidates.Count == 0)
        {
            return Array.Empty<string>();
        }

        var documents = new string[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            documents[i] = candidates[i].DocumentText;
        }

        var job = JsonSerializer.Serialize(new
        {
            capabilityId = "rerank",
            core = new
            {
                query = queryText,
                documents,
                topK = effectiveTopK,
                timeout = _timeoutMs,
            },
        });

        CapabilityCapabilityResult result;
        try
        {
            result = await _cli.InvokeAsync(job, _timeoutMs, ct).ConfigureAwait(false);
        }
        catch (CapabilityKgCliException)
        {
            // FAIL-SAFE: degrade to the RRF order (the set is already authorized). Never throw on rerank.
            return DegradeToInputOrder(candidates, effectiveTopK);
        }

        if (!string.Equals(result.Status, "succeeded", StringComparison.Ordinal))
        {
            return DegradeToInputOrder(candidates, effectiveTopK);
        }

        var artifact = FirstRerankArtifact(result);
        if (artifact?.Scored is null || artifact.Scored.Count == 0)
        {
            return DegradeToInputOrder(candidates, effectiveTopK);
        }

        // The worker already sorted best-first + truncated to topK; map the indices back to record ids,
        // guarding against an out-of-range index (a malformed result degrades that entry, never throws).
        var ids = new List<string>(artifact.Scored.Count);
        foreach (var score in artifact.Scored)
        {
            if (score.Index >= 0 && score.Index < candidates.Count)
            {
                ids.Add(candidates[score.Index].RecordId);
            }
        }
        return ids.Count > 0 ? ids : DegradeToInputOrder(candidates, effectiveTopK);
    }

    private static IReadOnlyList<string> DegradeToInputOrder(
        IReadOnlyList<RerankCandidate> candidates, int topK)
    {
        var ids = new List<string>(topK);
        for (var i = 0; i < topK; i++)
        {
            ids.Add(candidates[i].RecordId);
        }
        return ids;
    }

    private static CapabilityArtifact? FirstRerankArtifact(CapabilityCapabilityResult result)
    {
        if (result.Artifacts is null)
        {
            return null;
        }
        foreach (var a in result.Artifacts)
        {
            if (string.Equals(a.Kind, "rerank", StringComparison.Ordinal))
            {
                return a;
            }
        }
        return null;
    }
}
