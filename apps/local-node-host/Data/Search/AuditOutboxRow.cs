namespace Harborline.Api.LocalNodeHost.Data.Search;

/// <summary>
/// DES-0029 kernel-core-ck-6: one audit entry owed to the kernel audit trail, staged in the SAME transaction as the
/// authorization or erasure write it records, so the write and its audit commit together or not at all.
/// <see cref="Audit.NodeAuditOutbox"/> drains it to the trail; a published row keeps its publication instant.
/// </summary>
public sealed class AuditOutboxRow
{
    /// <summary>The audit entry id, fixed when the row is staged (the trail's idempotency key).</summary>
    public required string AuditId { get; set; }

    /// <summary>The tenant the audited act belongs to.</summary>
    public required string TenantId { get; set; }

    /// <summary>The kernel audit event type.</summary>
    public required string EventType { get; set; }

    /// <summary>The act's instant, which is also the signed payload's issue instant.</summary>
    public required DateTimeOffset OccurredAt { get; set; }

    /// <summary>The payload signature nonce.</summary>
    public required string Nonce { get; set; }

    /// <summary>The payload body, a JSON object of string values.</summary>
    public required string BodyJson { get; set; }

    /// <summary>The acting principal, when the act has one.</summary>
    public string? Actor { get; set; }

    /// <summary>The decided target kind, for an authorized entry.</summary>
    public string? TargetKind { get; set; }

    /// <summary>The decided target id, for an authorized entry.</summary>
    public string? TargetId { get; set; }

    /// <summary>The decided target scope, for an authorized entry.</summary>
    public string? TargetScope { get; set; }

    /// <summary>The decided act (<c>operation@scope</c>), for an authorized entry.</summary>
    public string? Act { get; set; }

    /// <summary>The authority snapshot captured from the live decision, as JSON; null for a system entry.</summary>
    public string? AuthoritySnapshotJson { get; set; }

    /// <summary>When the entry reached the trail, Unix-ms UTC; null while it is owed.</summary>
    public long? PublishedAtUnixMs { get; set; }

    /// <summary>Failed publication attempts.</summary>
    public int Attempts { get; set; }

    /// <summary>The last publication failure, for the operator.</summary>
    public string? LastError { get; set; }
}
