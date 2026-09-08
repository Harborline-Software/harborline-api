using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector.Cli;

/// <summary>
/// The REAL <see cref="IKgEmbeddingProvider"/> (ADR 0135 KG-search F3-lift amendment, Slice 1d) — drives the
/// capability <c>embeddings</c> runtime (BGE-M3 on the G-4 sandboxed CPU floor) through the <c>kg-embed</c> CLI
/// (the agent-client doctrine: CLI(<c>--json</c>) + SDK, NOT MCP). One text per call (the indexer embeds
/// per-record), so it sends a single-text <c>EmbeddingsCore</c> and maps the returned artifact onto a
/// <see cref="KgEmbeddingArtifact"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>M1 provenance is HONEST end to end.</b> The CLI tags a REAL worker vector <c>bge-m3</c> and a degraded
/// STUB vector <c>stub-bge-m3</c> (bug-1358 fixed at the TS source). This provider copies the CLI-reported
/// <c>model</c>/<c>modelVersion</c> straight onto the artifact — it NEVER overrides them. So if the host has
/// not armed the real worker, the artifact carries the stub sentinel and the indexer's M1 gate REFUSES it
/// (the host fails closed at ingest rather than indexing a fake as real). The provider does not pretend.
/// </para>
/// <para>
/// <b><see cref="Model"/> is advisory.</b> It reports <c>bge-m3</c> (the floor this provider TARGETS) for
/// health/registration; the AUTHORITATIVE provenance is the per-artifact <c>model</c> the CLI stamps, which
/// the indexer checks. A non-armed host's provider therefore advertises <c>bge-m3</c> but produces
/// <c>stub-bge-m3</c>-tagged artifacts — the M1 gate, not this property, is the boundary.
/// </para>
/// </remarks>
public sealed class KgCliEmbeddingProvider : IKgEmbeddingProvider
{
    private readonly CapabilityKgCliClient _cli;
    private readonly int _timeoutMs;

    /// <inheritdoc />
    public string Model => KgModelFloor.BgeM3.Id;

    /// <inheritdoc />
    public int Dimension => KgModelFloor.BgeM3.Dimension;

    /// <summary>Construct bound to the capability CLI client + the per-invoke timeout.</summary>
    public KgCliEmbeddingProvider(CapabilityKgCliClient cli, int timeoutMs)
    {
        _cli = cli ?? throw new ArgumentNullException(nameof(cli));
        _timeoutMs = timeoutMs > 0 ? timeoutMs : 120_000;
    }

    /// <inheritdoc />
    public async Task<KgEmbeddingArtifact> EmbedAsync(
        string recordId,
        string tenantId,
        string? subjectId,
        string text,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentNullException.ThrowIfNull(text);

        var job = JsonSerializer.Serialize(new
        {
            capabilityId = "embeddings",
            core = new
            {
                texts = new[] { text },
                dimension = Dimension,
                timeout = _timeoutMs,
            },
        });

        var result = await _cli.InvokeAsync(job, _timeoutMs, ct).ConfigureAwait(false);

        if (!string.Equals(result.Status, "succeeded", StringComparison.Ordinal))
        {
            // A failed envelope ⇒ no usable artifact. Throw the floor-unavailable signal so the indexer fails
            // closed (provider.kg_floor_unavailable) — it never indexes a missing/unverifiable embedding.
            throw new KgFloorUnavailableException(
                Model,
                $"capability embeddings invoke failed: {result.Error?.Code} {result.Error?.Message}");
        }

        var artifact = FirstEmbeddingArtifact(result);
        if (artifact?.Vectors is null || artifact.Vectors.Count == 0)
        {
            throw new KgFloorUnavailableException(Model, "capability embeddings result carried no vectors");
        }

        var vector = artifact.Vectors[0];
        var dimension = artifact.Dimension ?? vector.Count;

        // Copy the CLI-reported provenance VERBATIM — the indexer's M1 gate is the boundary, not this provider.
        // (Real path ⇒ "bge-m3"; degraded stub ⇒ "stub-bge-m3", which M1 refuses — fail-closed, never a fake.)
        return new KgEmbeddingArtifact(
            RecordId: recordId,
            TenantId: tenantId,
            SubjectId: subjectId,
            Vector: vector,
            Dimension: dimension,
            Model: artifact.Model ?? "(unreported)",
            ModelVersion: artifact.ModelVersion ?? "(unreported)");
    }

    private static CapabilityArtifact? FirstEmbeddingArtifact(CapabilityCapabilityResult result)
    {
        if (result.Artifacts is null)
        {
            return null;
        }
        foreach (var a in result.Artifacts)
        {
            if (string.Equals(a.Kind, "embeddings", StringComparison.Ordinal))
            {
                return a;
            }
        }
        return null;
    }
}
