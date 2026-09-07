using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Recovery;

namespace Harborline.Api.Foundation.Documents.Issuance;

/// <summary>
/// The <b>document is a RECORD</b> (#111 design §0/§3.6, ADR 0146 D9 provenance): an <b>immutable</b>
/// artifact frozen at issue-time — the rendered bytes (by <see cref="BlobCid"/>), the pinned template
/// version, and a data snapshot that is <b>never raw PII plaintext</b> (subject-key-encrypted fields, §5.5).
/// Reproducible forever; a template edit NEVER alters an already-issued document. The number is <b>merged
/// from the record</b>, not minted here (§4.1 — the invoice record owns the collision-safe INV- number).
/// </summary>
/// <remarks>
/// Immutability is structural: this is an init-only record, and <see cref="IIssuedDocumentStore"/> rejects a
/// second write for the same <see cref="DocumentId"/> — an issued money document is never overwritten
/// (masters archive, ledger reverses; a correction is a NEW document — §4.2). A retention obligation on an
/// issued money document is enforced by a legal hold (<see cref="LegalHoldId"/>, ADR 0142 / council F2),
/// which blocks a GDPR crypto-shred of the retention-required subject fields.
/// </remarks>
/// <param name="DocumentId">Opaque, unique issued-document id (the legal-hold + store key).</param>
/// <param name="DocumentType">The document type issued (e.g. <c>invoice</c>).</param>
/// <param name="RecordType">The source record type (e.g. <c>invoice</c>).</param>
/// <param name="RecordId">The source record id.</param>
/// <param name="RecordNumber">The customer-facing number merged from the record (the collision-safe INV-, §4.1) — reused, never re-minted.</param>
/// <param name="TemplateKey">The template key that rendered this document.</param>
/// <param name="TemplateVersion">The pinned template version (S-8 watermark) — the reproducibility binding.</param>
/// <param name="LocaleTag">The document locale it was rendered in (§1.5).</param>
/// <param name="BlobCid">The rendered PDF bytes in the (envelope-encrypted-at-rest) blob store — the authoritative artifact.</param>
/// <param name="ContentHash">A deterministic hash of the rendered semantic content (§3.6) — the content-equivalence anchor (F3).</param>
/// <param name="Snapshot">The subject-key-encrypted data snapshot — never raw PII (§5.5). A subject crypto-shred renders these unreadable.</param>
/// <param name="IssuedAtUtc">The issue instant.</param>
/// <param name="LegalHoldId">The legal hold placed on issue (ADR 0142 / F2), or null when no hold was requested.</param>
public sealed record IssuedDocumentRecord(
    string DocumentId,
    string DocumentType,
    string RecordType,
    string RecordId,
    string RecordNumber,
    string TemplateKey,
    string TemplateVersion,
    string LocaleTag,
    Cid BlobCid,
    string ContentHash,
    IReadOnlyList<EncryptedSnapshotField> Snapshot,
    DateTimeOffset IssuedAtUtc,
    string? LegalHoldId);

/// <summary>
/// One field of the issued document's data snapshot, <b>subject-key-encrypted</b> (§3.6/§5.5). The value is
/// an <see cref="EncryptedField"/> sealed under the data subject's per-subject key — never plaintext PII —
/// so a GDPR crypto-shred of that subject destroys the key and renders the frozen snapshot unreadable,
/// without breaking the audit hash-chain (the record still exists; its subject content is cryptographically
/// gone). This is the fact-snapshot-PII lesson applied to the document record.
/// </summary>
/// <param name="Key">The snapshot field name (e.g. <c>customer.name</c>).</param>
/// <param name="Value">The subject-key-encrypted value envelope.</param>
public sealed record EncryptedSnapshotField(string Key, EncryptedField Value);
