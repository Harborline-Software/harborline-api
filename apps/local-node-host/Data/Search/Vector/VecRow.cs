namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// One row of the durable KG vector index (ADR 0135 KG-search F3-lift amendment, Slice 1b) — the
/// <b>per-subject-encrypted</b> embedding for a single domain record, plus the metadata the exact clip (G-1),
/// the model-pin (G-5), and the GDPR crypto-shred (G-6) key on.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the authoritative, durable embedding store — and the G-6 crypto-shred boundary.</b> The embedding
/// is NOT stored in cleartext: <see cref="EncryptedEmbedding"/> is the packed binary code (the spike's default
/// <c>bit</c> quantization, 24× smaller) sealed under the record's <b>per-subject sub-key</b>
/// (<c>ISubjectFieldEncryptor</c>, ADR 0135 GDPR direction). Crypto-shredding a subject destroys their
/// per-subject key, so every one of their <see cref="EncryptedEmbedding"/> blobs becomes permanently
/// undecryptable — the index is <b>not</b> a shred bypass (embeddings are partially invertible, so this matters).
/// </para>
/// <para>
/// <b>The <c>vec0</c> acceleration table is DERIVED + rebuildable + purged-on-shred.</b> The real
/// <c>sqlite-vec</c> path needs a cleartext bit-vector column to run a native KNN; that <c>vec0</c> table is
/// populated by DECRYPTING each subject's embedding at index-build time and is itself only file-encrypted
/// (SQLCipher), never per-subject. So on crypto-shred the subject's <c>vec0</c> rows are DELETED alongside the
/// durable row (a rebuildable index, clean to drop) — the durable per-subject-encrypted blob is the boundary,
/// the <c>vec0</c> table is a cache of it. The brute-force engine decrypts per-subject on the fly and holds no
/// cleartext at rest, so it has no analogous purge obligation.
/// </para>
/// <para>
/// <b>Encrypted at rest twice over (SC-1 + G-6).</b> The whole row lives in the SQLCipher-keyed
/// <c>local-node.db</c> file (SC-1, file-level), and <see cref="EncryptedEmbedding"/> is additionally sealed
/// under the per-subject sub-key (G-6, subject-level). The <see cref="TenantId"/> column + the clip's
/// <c>record_id IN (…)</c> are the within-file record-narrowing boundary.
/// </para>
/// </remarks>
public sealed class VecRow
{
    /// <summary>The source domain record's identity — the metadata-column clip key the vec0 <c>WHERE IN</c> narrows on. PK.</summary>
    public required string RecordId { get; set; }

    /// <summary>The tenant the record belongs to (per-tenant isolation; the in-file defence-in-depth predicate).</summary>
    public required string TenantId { get; set; }

    /// <summary>
    /// The GDPR data subject this embedding is keyed to (G-6) — the per-subject sub-key label. The reserved
    /// sentinel <see cref="VecIndexConstants.TenantWideSubject"/> is used for records with no specific subject
    /// (still per-tenant-DEK-derived; still shreddable as a unit, just not natural-person-scoped).
    /// </summary>
    public required string SubjectId { get; set; }

    /// <summary>The model id that produced the embedding (G-5; M1 — must be a registered floor id to be indexed).</summary>
    public required string Model { get; set; }

    /// <summary>The model revision (G-5) — a change forces a re-embed; a mismatch on read is a hard fault.</summary>
    public required string ModelVersion { get; set; }

    /// <summary>The embedding dimension (G-5) — a width mismatch vs the model floor's declared dimension is a hard fault.</summary>
    public required int Dimension { get; set; }

    /// <summary>
    /// The packed binary code (<c>bit</c> quantization) sealed under the record's per-subject sub-key (G-6).
    /// Stored as the <c>EncryptedField</c> envelope's packed bytes (ciphertext || tag).
    /// </summary>
    public required byte[] EncryptedEmbedding { get; set; }

    /// <summary>The AES-GCM nonce for <see cref="EncryptedEmbedding"/> (part of the <c>EncryptedField</c> envelope).</summary>
    public required byte[] EmbeddingNonce { get; set; }

    /// <summary>The <c>EncryptedField</c> key version (G-6 envelope).</summary>
    public required int KeyVersion { get; set; }

    /// <summary>
    /// Residency mirror (G-3) — an <c>OnlineOnly</c> record is NEVER written here (insert-refusal), so this is
    /// always <see cref="SearchResidency.Cache"/> for a persisted row. The column exists so the never-index
    /// invariant is queryable + arch-testable, exactly like <c>search_nodes.residency</c>.
    /// </summary>
    public SearchResidency Residency { get; set; } = SearchResidency.Cache;
}

/// <summary>Shared constants for the vector index.</summary>
public static class VecIndexConstants
{
    /// <summary>
    /// The reserved subject label for records with no natural-person subject (G-6). Encrypted under a
    /// tenant-DEK-derived sub-key for this sentinel; shreddable as a unit but not a person's erasure target.
    /// </summary>
    public const string TenantWideSubject = "__tenant-wide__";

    /// <summary>The vec0 virtual-table name (the derived cleartext bit-vector acceleration index).</summary>
    public const string Vec0TableName = "search_vec0";

    /// <summary>The durable per-subject-encrypted content table name.</summary>
    public const string VecRowsTableName = "search_vec_rows";
}
