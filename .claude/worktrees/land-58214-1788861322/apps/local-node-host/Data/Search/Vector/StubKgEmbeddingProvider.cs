using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The deterministic, self-identifying stub embedding provider (ADR 0135 KG-search F3-lift amendment, Slice 1b).
/// Produces a hash-seeded L2-normalized unit vector — the SAME envelope shape as a real BGE-M3 embedding — but
/// tags it with the <b>self-identifying sentinel</b> <see cref="KgModelFloorGate.StubModelSentinel"/>
/// (<c>stub-bge-m3</c>), NEVER a registered floor id.
/// </summary>
/// <remarks>
/// <para>
/// <b>The M1 no-fake-as-real property lives HERE, at the source.</b> The capability Slice-1a stub stamps
/// <c>model: 'bge-m3'</c> on its deterministic vectors (indistinguishable from real) — this .NET stub does NOT
/// repeat that mistake: it self-identifies, so the indexer's M1 gate rejects it from a production index. A test
/// indexer must EXPLICITLY opt into stub-indexing (<c>allowStubModel: true</c>) to admit it — there is no
/// silent path by which a fake pins as real (bug-1312 family).
/// </para>
/// <para>
/// The vector is byte-identical to the capability <c>stubVector</c> algorithm (FNV-1a seed → LCG PRNG → L2-normalize),
/// so a stub-indexed row and a capability-stub query vector agree — useful for deterministic RRF / ordering tests.
/// </para>
/// </remarks>
public sealed class StubKgEmbeddingProvider : IKgEmbeddingProvider
{
    /// <inheritdoc />
    public string Model => KgModelFloorGate.StubModelSentinel;

    /// <inheritdoc />
    public int Dimension { get; }

    /// <summary>Construct a stub provider emitting <paramref name="dimension"/>-wide vectors (defaults to BGE-M3's 1024).</summary>
    public StubKgEmbeddingProvider(int dimension = 1024)
    {
        if (dimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dimension), "Embedding dimension must be positive.");
        }
        Dimension = dimension;
    }

    /// <inheritdoc />
    public Task<KgEmbeddingArtifact> EmbedAsync(
        string recordId,
        string tenantId,
        string? subjectId,
        string text,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(text);

        var vector = StubVector(text, Dimension);
        return Task.FromResult(new KgEmbeddingArtifact(
            RecordId: recordId,
            TenantId: tenantId,
            SubjectId: subjectId,
            Vector: vector,
            Dimension: Dimension,
            // SELF-IDENTIFY — never a registered floor id. The M1 gate rejects this from a production index.
            Model: KgModelFloorGate.StubModelSentinel,
            ModelVersion: "stub"));
    }

    /// <summary>
    /// A deterministic L2-normalized unit vector derived from <paramref name="text"/> (FNV-1a seed → LCG PRNG →
    /// normalize). NOT a real embedding — byte-identical to the capability <c>stubVector</c> so the two stubs agree.
    /// </summary>
    public static IReadOnlyList<float> StubVector(string text, int dimension)
    {
        ArgumentNullException.ThrowIfNull(text);

        // FNV-1a-ish seed over the UTF-16 code units (matches the capability TS stub's charCodeAt loop).
        uint seed = 2166136261u;
        foreach (var ch in text)
        {
            seed ^= ch;
            seed = unchecked(seed * 16777619u);
        }

        var raw = new double[dimension];
        for (var i = 0; i < dimension; i++)
        {
            seed = unchecked(seed * 1103515245u + 12345u);
            raw[i] = ((double)seed / uint.MaxValue) * 2.0 - 1.0;
        }

        var sumSq = 0.0;
        for (var i = 0; i < dimension; i++)
        {
            sumSq += raw[i] * raw[i];
        }

        var norm = Math.Sqrt(sumSq);
        var inv = norm > 0 ? 1.0 / norm : 0.0;

        var vector = new float[dimension];
        for (var i = 0; i < dimension; i++)
        {
            vector[i] = (float)(raw[i] * inv);
        }
        return vector;
    }
}
