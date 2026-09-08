using System.Security.Cryptography;
using System.Text;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Documents.Merge;
using Harborline.Api.Foundation.Documents.Model;
using Harborline.Api.Foundation.Documents.Rendering;
using Harborline.Api.Foundation.Recovery.Crypto;
using Harborline.Api.Foundation.Recovery.Erasure;
using Harborline.Api.Foundation.Recovery.LegalHold;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Audit;

namespace Harborline.Api.Foundation.Documents.Issuance;

/// <summary>
/// Mints an <b>immutable issued-document record</b> from a template + a record (#111 §3.6, the D2 keystone).
/// The one authoritative, record-minting render path (§3.2): walk → render to PDF bytes → store the bytes →
/// content-hash the semantic render → subject-key-encrypt the PII snapshot (never raw plaintext, §5.5) →
/// register a legal hold (ADR 0142 / council F2 — so a retention obligation blocks a later crypto-shred) →
/// persist the frozen record. The customer-facing number is <b>merged from the record</b> (the collision-safe
/// INV-, §4.1), never re-minted here.
/// </summary>
public sealed class DocumentIssuanceService
{
    private readonly DocumentRenderWalker _walker;
    private readonly IPdfExportWriter _writer;
    private readonly IBlobStore _blobs;
    private readonly ISubjectFieldEncryptor _subjectEncryptor;
    private readonly IIssuedDocumentStore _store;
    private readonly ILegalHoldService _legalHold;
    private readonly TimeProvider _clock;

    /// <summary>Constructs the issuance service over the render pipeline + provenance stores.</summary>
    public DocumentIssuanceService(
        DocumentRenderWalker walker,
        IPdfExportWriter writer,
        IBlobStore blobs,
        ISubjectFieldEncryptor subjectEncryptor,
        IIssuedDocumentStore store,
        ILegalHoldService legalHold,
        TimeProvider? clock = null)
    {
        _walker = walker ?? throw new ArgumentNullException(nameof(walker));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
        _subjectEncryptor = subjectEncryptor ?? throw new ArgumentNullException(nameof(subjectEncryptor));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _legalHold = legalHold ?? throw new ArgumentNullException(nameof(legalHold));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Walks a request's template + record into the semantic document — the shared render (no mint).</summary>
    public RenderedDocument Render(DocumentIssuanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _walker.Render(request.Template, request.Model, request.Format, request.NamedRuleResolver);
    }

    /// <summary>
    /// Renders + mints the immutable issued document. The stored bytes are the authoritative artifact; a
    /// later <see cref="Render"/> of the same pinned template + data is content-equivalent (council F3) —
    /// verifiable via <see cref="IssuedDocumentRecord.ContentHash"/>.
    /// </summary>
    public async Task<IssuedDocumentRecord> IssueAsync(DocumentIssuanceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. Render through the ONE authoritative pipeline → PDF bytes.
        var rendered = Render(request);
        var bytes = await _writer.WriteAsync(rendered, ct).ConfigureAwait(false);

        // 2. Store the bytes (the node wires an EnvelopeBlobStore → tenant-encrypted at rest).
        var cid = await _blobs.PutAsync(bytes, ct).ConfigureAwait(false);

        // 3. Content-hash the SEMANTIC render (deterministic where PDF bytes are not) — the F3 anchor.
        var contentHash = DocumentContentHash.Of(rendered);

        // 4. Subject-key-encrypt the PII snapshot — NEVER raw plaintext (§5.5).
        var snapshot = new List<EncryptedSnapshotField>(request.SubjectSnapshot.Count);
        foreach (var (key, plaintext) in request.SubjectSnapshot)
        {
            var enc = await _subjectEncryptor
                .EncryptForSubjectAsync(Encoding.UTF8.GetBytes(plaintext), request.Tenant, request.Subject, ct)
                .ConfigureAwait(false);
            snapshot.Add(new EncryptedSnapshotField(key, enc));
        }

        var documentId = string.IsNullOrWhiteSpace(request.DocumentId)
            ? "doc-" + Guid.NewGuid().ToString("N")
            : request.DocumentId!;
        var now = _clock.GetUtcNow();

        // 5. Register a legal hold (ADR 0142 / council F2). A record-scoped hold marks the issued money
        //    document retained; a subject-scoped hold is what makes SubjectErasureService's existing
        //    IsSubjectHeldAsync gate BLOCK the crypto-shred of the retention-required subject. WHICH scope /
        //    which fields / which jurisdictions is the counsel-adjacent policy F2 routes to ONR — the node
        //    supplies the policy; this service wires the mechanism.
        string? legalHoldId = null;
        if (request.LegalHoldScope != DocumentLegalHoldScope.None)
        {
            var heldRef = request.LegalHoldScope == DocumentLegalHoldScope.Subject
                ? HeldRef.ForSubject(request.Subject)
                : HeldRef.ForRecord("issued-document", documentId);
            var entry = await _legalHold
                .PlaceAsync(new LegalHoldPlaceRequest(request.Tenant, heldRef, request.LegalHoldMatter, request.PlacedBy), ct)
                .ConfigureAwait(false);
            legalHoldId = entry.HoldId.Value;
        }

        // 6. Mint the frozen record + persist (the store rejects a duplicate id — immutability).
        var record = new IssuedDocumentRecord(
            DocumentId: documentId,
            DocumentType: request.Template.DocumentType,
            RecordType: request.RecordType,
            RecordId: request.RecordId,
            RecordNumber: request.RecordNumber,
            TemplateKey: request.Template.Key,
            TemplateVersion: request.Template.Version,
            LocaleTag: rendered.LocaleTag,
            BlobCid: cid,
            ContentHash: contentHash,
            Snapshot: snapshot,
            IssuedAtUtc: now,
            LegalHoldId: legalHoldId);

        await _store.AddAsync(record, ct).ConfigureAwait(false);
        return record;
    }
}

/// <summary>The scope of the legal hold placed on issue (council F2 mechanism; the policy is ONR/counsel's).</summary>
public enum DocumentLegalHoldScope
{
    /// <summary>No hold (the document is freely erasable — a non-retention document class).</summary>
    None = 0,

    /// <summary>A hold on the issued document record (marks THIS money document retained).</summary>
    Record = 1,

    /// <summary>A hold on the data subject — makes the crypto-shred gate refuse to erase the subject (blocks retention-required-field shred).</summary>
    Subject = 2,
}

/// <summary>The inputs to mint an issued document (#111 §3.6).</summary>
public sealed record DocumentIssuanceRequest
{
    /// <summary>The template to render.</summary>
    public required TemplateDefinition Template { get; init; }

    /// <summary>The record data mapped into the merge model.</summary>
    public required DocumentMergeModel Model { get; init; }

    /// <summary>The document-locale format context.</summary>
    public required DocumentFormatContext Format { get; init; }

    /// <summary>The tenant scope.</summary>
    public required TenantId Tenant { get; init; }

    /// <summary>The data subject whose per-subject key seals the snapshot (the customer/contact).</summary>
    public required SubjectId Subject { get; init; }

    /// <summary>The source record type (e.g. <c>invoice</c>).</summary>
    public required string RecordType { get; init; }

    /// <summary>The source record id.</summary>
    public required string RecordId { get; init; }

    /// <summary>The customer-facing number merged from the record (the collision-safe INV-, §4.1).</summary>
    public required string RecordNumber { get; init; }

    /// <summary>The actor placing the legal hold (single-actor placement per ADR 0142).</summary>
    public required ActorId PlacedBy { get; init; }

    /// <summary>An explicit issued-document id, or null to generate one.</summary>
    public string? DocumentId { get; init; }

    /// <summary>The PII fields to subject-key-encrypt into the snapshot (never raw plaintext, §5.5).</summary>
    public IReadOnlyDictionary<string, string> SubjectSnapshot { get; init; } = new Dictionary<string, string>();

    /// <summary>The legal-hold scope placed on issue (F2 mechanism). Default: a record-scoped retention hold.</summary>
    public DocumentLegalHoldScope LegalHoldScope { get; init; } = DocumentLegalHoldScope.Record;

    /// <summary>The legal-hold matter string.</summary>
    public string LegalHoldMatter { get; init; } = "money-document-retention";

    /// <summary>Optional resolver for a conditional block's named-Rule reference (§1.3).</summary>
    public Func<DocumentBlockGuard, RuleDefinition?>? NamedRuleResolver { get; init; }
}

/// <summary>
/// The deterministic content hash of a rendered document (#111 §3.6) — SHA-256 over the semantic text
/// projection, the content-equivalence anchor (council F3). Independent of PDF byte-level non-determinism.
/// </summary>
public static class DocumentContentHash
{
    /// <summary>Computes the content hash of a rendered document.</summary>
    public static string Of(RenderedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var canonical = document.DocumentType + "" + document.LocaleTag + ""
            + (document.CurrencyCode ?? string.Empty) + "" + document.ExtractText();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "sha256:" + Convert.ToHexStringLower(bytes);
    }
}
