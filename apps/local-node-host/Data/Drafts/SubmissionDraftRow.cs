using System;

namespace Harborline.Api.LocalNodeHost.Data.Drafts;

/// <summary>
/// The node-local persistence row for a D2 save-and-resume submission-draft (ADR 0135
/// amendment 2026-07-01). Flat projection of
/// <see cref="Harborline.Api.Foundation.Forms.Drafts.SubmissionDraft"/> mapped by
/// <see cref="NodeLocalDraftsDbContext"/> onto the <c>form_drafts</c> table.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composite key = the D2 tuple.</b> <c>(TenantId, CaseId, PartyId)</c> is the primary
/// key, so a draft is addressed by exactly the ratified keying tuple and no two tenants /
/// parties can ever collide on a shared case id.
/// </para>
/// <para>
/// <b>At-rest protection.</b> The <see cref="Body"/> is stored as UTF-8 JSON bytes inside
/// the node's SQLCipher whole-file-encrypted database (SC-1) — the same at-rest posture as
/// every other node-local table (payroll / comms / workflow rows are not double-encrypted;
/// the file cipher is the protection). Subject crypto-shred is realized by the store's
/// erasure-registry consult + hard-delete, not by per-field ciphertext (see
/// <see cref="NodeEfSubmissionDraftStore"/>).
/// </para>
/// </remarks>
public sealed class SubmissionDraftRow
{
    /// <summary>The tenant id (first key element).</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The client-mintable case/subject id (second key element).</summary>
    public string CaseId { get; set; } = string.Empty;

    /// <summary>The actor's per-org PartyId as a canonical "N" GUID string (third key element).</summary>
    public string PartyId { get; set; } = string.Empty;

    /// <summary>The form definition this draft is being filled against.</summary>
    public string FormId { get; set; } = string.Empty;

    /// <summary>Content-addressed CID of the JSON Schema the draft validates against.</summary>
    public string SchemaRef { get; set; } = string.Empty;

    /// <summary>The form definition id from the provenance header.</summary>
    public string DefinitionId { get; set; } = string.Empty;

    /// <summary>Canonical "{major}.{minor}.{patch}" version of the definition revision.</summary>
    public string DefinitionVersion { get; set; } = string.Empty;

    /// <summary>The rule/compute engine identifier in force.</summary>
    public string EngineVersion { get; set; } = string.Empty;

    /// <summary>The actor locale-preference chain, serialized as a JSON string array.</summary>
    public string LocaleChainJson { get; set; } = "[]";

    /// <summary>The serialized partial candidate values (UTF-8 JSON object bytes).</summary>
    public byte[] Body { get; set; } = Array.Empty<byte>();

    /// <summary>The optional data subject whose PII the draft holds (legal-hold / retention / erasure visibility).</summary>
    public string? SubjectId { get; set; }

    /// <summary>When the draft was first saved.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>When the draft was last saved.</summary>
    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <summary>The retention TTL after which a legal-hold-gated purge may remove it; null = no TTL.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; set; }
}
