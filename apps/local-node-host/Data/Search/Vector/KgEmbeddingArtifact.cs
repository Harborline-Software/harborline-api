using System.Collections.Generic;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// The embedding artifact handed to the vector indexer (ADR 0135 KG-search F3-lift amendment, Slice 1b) — a
/// dense vector for one record's text PLUS the <b>provenance</b> the indexer's <b>M1 no-fake-as-real gate</b>
/// keys on: which <see cref="Model"/> + <see cref="ModelVersion"/> produced it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mirrors the capability <c>EmbeddingsArtifact</c> (apps/capability-host) deliberately.</b> When the .NET node is wired to
/// the capability embeddings runtime over the capability-core CLI/SDK (Slice 1d — the eval/wire slice), the
/// runtime's <c>{ vectors, dimension, model, modelVersion }</c> envelope maps one-to-one onto this type. Until
/// that wire lands, the artifact is produced by an <see cref="IKgEmbeddingProvider"/> on the .NET side — the
/// real provider (digest-pinned floor) or the deterministic test provider that SELF-IDENTIFIES (see below).
/// </para>
/// <para>
/// <b>The M1 security property — the <see cref="Model"/> id is the no-fake-as-real boundary.</b> The indexer
/// REJECTS any artifact whose <see cref="Model"/> is not a registered REAL floor id (<see cref="KgModelFloor"/>).
/// A deterministic stub MUST self-identify with a non-floor sentinel (<c>stub-bge-m3</c>) so it can never pin
/// as genuine <c>bge-m3</c> in the durable, partially-invertible index. This is the same family as the
/// no-mock-crypto fail-closed lesson (bug-1312): a fake artifact that wears a real label is a silent leak of
/// trust, so the label itself is fenced — the only path that yields a <c>bge-m3</c>-tagged row destined for
/// the index is the real provider.
/// </para>
/// </remarks>
/// <param name="RecordId">The source domain record this vector was embedded from (the clip's narrowing key).</param>
/// <param name="TenantId">The tenant the record belongs to (the per-tenant isolation boundary).</param>
/// <param name="SubjectId">
/// The GDPR data subject this record's embedding is keyed to (G-6). The vector is encrypted under this
/// subject's per-subject sub-key, so crypto-shredding the subject makes the embedding undecryptable. Null
/// when the record is not subject-scoped (encrypted under the tenant-wide subject sentinel).
/// </param>
/// <param name="Vector">The dense embedding (BGE-M3 ⇒ 1024 dims). Each row's width is asserted against the model's dimension (G-5).</param>
/// <param name="Dimension">
/// The declared embedding dimension (mirrors the capability artifact's <c>dimension</c>). A mismatch with the model
/// floor's declared dimension OR with the actual <see cref="Vector"/> width is a hard fault (G-5).
/// </param>
/// <param name="Model">
/// The model id that produced the vector — the M1 gate boundary. A REAL floor id (<c>bge-m3</c>) for a
/// genuine embedding; a self-identifying sentinel (<c>stub-bge-m3</c>) for the deterministic stub.
/// </param>
/// <param name="ModelVersion">The model revision (G-5) — a change forces a full re-embed, never a silently-mixed index.</param>
public sealed record KgEmbeddingArtifact(
    string RecordId,
    string TenantId,
    string? SubjectId,
    IReadOnlyList<float> Vector,
    int Dimension,
    string Model,
    string ModelVersion);
