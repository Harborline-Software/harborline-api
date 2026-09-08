using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The .NET-side embedding-provider seam (ADR 0135 KG-search F3-lift amendment, Slice 1b) — produces the dense
/// vector + its model provenance for a piece of record text. This is the boundary the indexer pulls vectors
/// through; it deliberately abstracts over WHERE the embedding comes from.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two implementations, one M1-fenced contract.</b>
/// <list type="bullet">
///   <item>The REAL provider (Slice 1d — the eval/wire slice) drives the capability <c>embeddings</c> runtime (BGE-M3
///     on the G-4 sandboxed CPU floor) over the capability-core CLI/SDK and returns artifacts tagged
///     <c>model = "bge-m3"</c>. That wire is NOT built here — Slice 1b is the secure index + clip + crypto layer.</item>
///   <item>The deterministic <see cref="StubKgEmbeddingProvider"/> returns a hash-seeded unit vector tagged with
///     the SELF-IDENTIFYING sentinel <see cref="KgModelFloorGate.StubModelSentinel"/> (<c>stub-bge-m3</c>), so a
///     stub vector can NEVER pin as genuine <c>bge-m3</c> in the durable index (M1). It exercises the full
///     index / clip / crypto / RRF path on CI without the heavy model, exactly mirroring the capability stub's role.</item>
/// </list>
/// </para>
/// <para>
/// <b>Fail-closed when no provider.</b> A production host with no real provider registered does NOT silently
/// fall back to the stub; the composition leaves the indexer with no provider and the index ingest path surfaces
/// <see cref="KgFloorUnavailableException"/> (<c>provider.kg_floor_unavailable</c>) rather than admitting an
/// unverifiable artifact.
/// </para>
/// </remarks>
public interface IKgEmbeddingProvider
{
    /// <summary>The model id this provider tags its artifacts with — a registered floor id (real) or the stub sentinel.</summary>
    string Model { get; }

    /// <summary>The declared embedding dimension this provider emits (1024 for BGE-M3).</summary>
    int Dimension { get; }

    /// <summary>
    /// Embed <paramref name="text"/> into a dense vector + its model provenance. The returned
    /// <see cref="KgEmbeddingArtifact.Model"/> is what the indexer's M1 gate checks: only a registered floor id
    /// is admitted to the durable index.
    /// </summary>
    /// <param name="recordId">The source record (the clip's narrowing key — carried onto the artifact).</param>
    /// <param name="tenantId">The tenant the record belongs to.</param>
    /// <param name="subjectId">The GDPR subject the embedding is keyed to (G-6), or null for tenant-wide.</param>
    /// <param name="text">The record text to embed.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<KgEmbeddingArtifact> EmbedAsync(
        string recordId,
        string tenantId,
        string? subjectId,
        string text,
        CancellationToken ct = default);
}
