using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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

    /// <summary>
    /// T-986: the signed payload envelope, as JSON, for an entry signed when it was staged (an enrollment
    /// compensating-control event). Null for an entry the drain signs on delivery.
    /// </summary>
    public string? SignedPayloadJson { get; set; }

    /// <summary>When the entry reached the trail, Unix-ms UTC; null while it is owed.</summary>
    public long? PublishedAtUnixMs { get; set; }

    /// <summary>Failed publication attempts.</summary>
    public int Attempts { get; set; }

    /// <summary>The last publication failure, for the operator.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// The one mapping of <c>search_audit_outbox</c>. The search context owns the table and its migrations;
    /// T-986 maps it into the roster context too (excluded from that context's migrations), so an enrollment
    /// change stages its audit on the context that commits the roster record.
    /// </summary>
    internal static void Map(EntityTypeBuilder<AuditOutboxRow> e)
    {
        e.ToTable("search_audit_outbox");
        e.HasKey(r => r.AuditId);
        e.Property(r => r.AuditId).HasColumnName("audit_id").HasMaxLength(64);
        e.Property(r => r.TenantId).HasColumnName("tenant_id").HasMaxLength(256);
        e.Property(r => r.EventType).HasColumnName("event_type").HasMaxLength(256);
        e.Property(r => r.OccurredAt).HasColumnName("occurred_at");
        e.Property(r => r.Nonce).HasColumnName("nonce").HasMaxLength(64);
        e.Property(r => r.BodyJson).HasColumnName("body_json");
        e.Property(r => r.Actor).HasColumnName("actor").HasMaxLength(512);
        e.Property(r => r.TargetKind).HasColumnName("target_kind").HasMaxLength(256);
        e.Property(r => r.TargetId).HasColumnName("target_id").HasMaxLength(512);
        e.Property(r => r.TargetScope).HasColumnName("target_scope").HasMaxLength(1024);
        e.Property(r => r.Act).HasColumnName("act").HasMaxLength(1024);
        e.Property(r => r.AuthoritySnapshotJson).HasColumnName("authority_snapshot_json");
        e.Property(r => r.SignedPayloadJson).HasColumnName("signed_payload_json");
        e.Property(r => r.PublishedAtUnixMs).HasColumnName("published_at_unix_ms");
        e.Property(r => r.Attempts).HasColumnName("attempts");
        e.Property(r => r.LastError).HasColumnName("last_error");
        // The drainer reads the owed rows only.
        e.HasIndex(r => r.PublishedAtUnixMs).HasDatabaseName("ix_search_audit_outbox_published");
    }
}
