using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Harborline.Api.Blocks.Docs.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Persistence;

namespace Harborline.Api.Blocks.Docs.Data;

/// <summary>
/// <see cref="IHarborlineEntityModule"/> for the Documents (blocks-docs) cluster
/// (ADR 0015 module-entity registration; ADR 0113 D5 fenced-set persistence PR 5).
///
/// <para>
/// Covers entity configurations for:
/// <list type="bullet">
///   <item><see cref="Attachment"/> — file-catalog metadata row. Immutable
///         post-upload: new version = fresh row with
///         <see cref="Attachment.ReplacesAttachmentId"/>. Tombstone-not-delete.
///         <see cref="StorageRef"/> stored as JSONB (discriminated union; same
///         JSONB-for-complex-value-object rationale as prior PRs).</item>
///   <item><see cref="DocumentRef"/> — cross-cluster join table linking an
///         <see cref="Attachment"/> to a parent entity in a consumer cluster.
///         Tombstone-not-delete; soft-delete sets <c>DeletedAtUtc</c> but
///         preserves the row for audit history.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>JSONB for StorageRef.</b> <see cref="StorageRef"/> is a discriminated-union
/// record over three storage tiers (Inline / FoundationBlob / ExternalUrl). A
/// JSONB column is cleaner than three nullable columns + a Kind discriminator,
/// and avoids fighting EF's owned-entity mechanics for a struct-shaped value
/// object on .NET 11 preview. This matches the established JSONB pattern from
/// <see cref="Financial.FinancialLedgerEntityModule"/>.
/// </para>
///
/// <para>
/// <b>Tenant isolation.</b> Both <see cref="Attachment"/> and
/// <see cref="DocumentRef"/> carry a <c>TenantId</c> column. Neither implements
/// <see cref="Harborline.Api.Foundation.MultiTenancy.IMustHaveTenant"/> because their
/// multi-tenancy is expressed via explicit TenantId predicates in the EF repos
/// (defence-in-depth per ADR 0092) — the global automatic filter in
/// <see cref="SignalBridgeDbContext.ApplyTenantQueryFilters"/> only runs on
/// entities that implement the marker interface.
/// </para>
/// </summary>
public sealed class DocsEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.blocks.docs";

    // ── Shared JSON options ─────────────────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Common converters ───────────────────────────────────────────────────

    private static readonly ValueConverter<TenantId, string> TenantIdConverter =
        new(v => v.Value, v => new TenantId(v));

    private static readonly ValueConverter<Instant, DateTimeOffset> InstantConverter =
        new(v => v.Value, v => new Instant(v));

    private static readonly ValueConverter<Instant?, DateTimeOffset?> NullableInstantConverter =
        new(v => v == null ? (DateTimeOffset?)null : v.Value.Value,
            v => v == null ? (Instant?)null : new Instant(v.Value));

    // ── Docs typed-id converters ────────────────────────────────────────────

    private static readonly ValueConverter<AttachmentId, string> AttachmentIdConverter =
        new(v => v.Value, v => new AttachmentId(v));

    private static readonly ValueConverter<AttachmentId?, string?> NullableAttachmentIdConverter =
        new(v => v == null ? null : v.Value.Value,
            v => v == null ? (AttachmentId?)null : new AttachmentId(v));

    private static readonly ValueConverter<DocumentRefId, string> DocumentRefIdConverter =
        new(v => v.Value, v => new DocumentRefId(v));

    private static readonly ValueConverter<AttachmentStatus, string> AttachmentStatusConverter =
        new(v => v.ToString(), v => Enum.Parse<AttachmentStatus>(v));

    private static readonly ValueConverter<Sensitivity, string> SensitivityConverter =
        new(v => v.ToString(), v => Enum.Parse<Sensitivity>(v));

    // ── StorageRef JSONB converter ──────────────────────────────────────────
    // StorageRef is a discriminated-union record. JSONB round-trips cleanly via
    // System.Text.Json using its [JsonConverter] on StorageRefKind. InlineBytes
    // (ReadOnlyMemory<byte>?) is serialized as a base64 string by the default
    // JsonSerializer; acceptable for the small inline tier (≤ 8 KB per doc).

    private static readonly ValueConverter<StorageRef, string> StorageRefConverter =
        new(v => JsonSerializer.Serialize(v, JsonOptions),
            v => DeserializeStorageRef(v));

    // Helper to avoid `throw` inside expression trees (CS8188).
    private static StorageRef DeserializeStorageRef(string v) =>
        JsonSerializer.Deserialize<StorageRef>(v, JsonOptions)
        ?? throw new InvalidOperationException("StorageRef column deserialized to null.");

    private static readonly ValueComparer<StorageRef> StorageRefComparer =
        new((a, b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b),
            v => v == null ? 0 : HashCode.Combine(v.Kind.ToString(), v.FoundationCid, v.ExternalUrl),
            // StorageRef is an immutable sealed record — returning the same reference is a valid snapshot.
            v => v);

    // ── RevisionVector ──────────────────────────────────────────────────────

    private static readonly ValueConverter<IReadOnlyDictionary<string, long>, string> RevisionVectorConverter =
        new(v => JsonSerializer.Serialize(v, JsonOptions),
            v => (IReadOnlyDictionary<string, long>?)
                     JsonSerializer.Deserialize<Dictionary<string, long>>(v, JsonOptions)
                 ?? new Dictionary<string, long>());

    private static readonly ValueComparer<IReadOnlyDictionary<string, long>> RevisionVectorComparer =
        new((a, b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b),
            v => v == null ? 0 : v.GetHashCode(),
            v => v);

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ConfigureAttachments(modelBuilder);
        ConfigureDocumentRefs(modelBuilder);
    }

    private void ConfigureAttachments(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Attachment>(e =>
        {
            e.ToTable("attachments");
            e.HasKey(a => a.Id);

            e.Property(a => a.Id)
                .HasConversion(AttachmentIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(a => a.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            // StorageRef — discriminated union stored as JSONB
            e.Property(a => a.StorageRef)
                .HasColumnName("storage_ref_json")
                .HasColumnType("jsonb")
                .HasConversion(StorageRefConverter, StorageRefComparer)
                .IsRequired();

            // Content-hash for dedup (AttachmentService.FindByContentHashAsync)
            e.Property(a => a.ContentHash).HasMaxLength(128).IsRequired();
            e.Property(a => a.MimeType).HasMaxLength(256).IsRequired();
            e.Property(a => a.SizeBytes).IsRequired();
            e.Property(a => a.OriginalFilename).HasMaxLength(512).IsRequired();

            // Optional thumbnail — also stored as JSONB when present
            e.Property(a => a.ThumbnailRef)
                .HasColumnName("thumbnail_ref_json")
                .HasColumnType("jsonb")
                .HasConversion(
                    new ValueConverter<StorageRef?, string?>(
                        v => v == null ? null : JsonSerializer.Serialize(v, JsonOptions),
                        v => v == null ? null : JsonSerializer.Deserialize<StorageRef>(v, JsonOptions)),
                    new ValueComparer<StorageRef?>(
                        (a, b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b),
                        v => v == null ? 0 : v.GetHashCode(),
                        // StorageRef is an immutable sealed record — returning same reference is a valid snapshot.
                        v => v));

            e.Property(a => a.Sensitivity)
                .HasConversion(SensitivityConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(a => a.Status)
                .HasConversion(AttachmentStatusConverter)
                .HasMaxLength(32)
                .IsRequired();

            e.Property(a => a.ReplacesAttachmentId)
                .HasConversion(NullableAttachmentIdConverter)
                .HasMaxLength(128);

            e.Property(a => a.ReplacedByAttachmentId)
                .HasConversion(NullableAttachmentIdConverter)
                .HasMaxLength(128);

            // CRDT envelope
            e.Property(a => a.CreatedAtUtc).HasConversion(InstantConverter).IsRequired();
            e.Property(a => a.CreatedBy).HasMaxLength(256);
            e.Property(a => a.UpdatedAtUtc).HasConversion(InstantConverter).IsRequired();
            e.Property(a => a.UpdatedBy).HasMaxLength(256);
            e.Property(a => a.DeletedAtUtc).HasConversion(NullableInstantConverter);
            e.Property(a => a.DeletedBy).HasMaxLength(256);
            e.Property(a => a.DeletedReason).HasMaxLength(1024);
            e.Property(a => a.Version).IsRequired();

            e.Property(a => a.RevisionVector)
                .HasColumnName("revision_vector_json")
                .HasColumnType("jsonb")
                .HasConversion(RevisionVectorConverter, RevisionVectorComparer)
                .IsRequired();

            // Tenant-scoped list (ListByTenantAsync)
            e.HasIndex(a => a.TenantId).HasDatabaseName("ix_attachments_tenant_id");
            // Content-hash dedup (FindByContentHashAsync) — active rows
            e.HasIndex(a => new { a.TenantId, a.ContentHash })
                .HasFilter("\"DeletedAtUtc\" IS NULL")
                .HasDatabaseName("ix_attachments_tenant_content_hash");
            // Status filter for quota calc (GetTenantTotalSizeBytesAsync)
            e.HasIndex(a => new { a.TenantId, a.Status })
                .HasDatabaseName("ix_attachments_tenant_status");
        });
    }

    private void ConfigureDocumentRefs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentRef>(e =>
        {
            e.ToTable("document_refs");
            e.HasKey(dr => dr.Id);

            e.Property(dr => dr.Id)
                .HasConversion(DocumentRefIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(dr => dr.TenantId)
                .HasConversion(TenantIdConverter)
                .HasMaxLength(256)
                .IsRequired();

            e.Property(dr => dr.AttachmentId)
                .HasConversion(AttachmentIdConverter)
                .HasMaxLength(128)
                .IsRequired();

            e.Property(dr => dr.ClusterCode).HasMaxLength(128).IsRequired();
            e.Property(dr => dr.ParentEntityType).HasMaxLength(128).IsRequired();
            e.Property(dr => dr.ParentEntityId).HasMaxLength(256).IsRequired();
            e.Property(dr => dr.AttachmentRole).HasMaxLength(128);

            // CRDT envelope
            e.Property(dr => dr.CreatedAtUtc).HasConversion(InstantConverter).IsRequired();
            e.Property(dr => dr.CreatedBy).HasMaxLength(256);
            e.Property(dr => dr.UpdatedAtUtc).HasConversion(InstantConverter).IsRequired();
            e.Property(dr => dr.UpdatedBy).HasMaxLength(256);
            e.Property(dr => dr.DeletedAtUtc).HasConversion(NullableInstantConverter);
            e.Property(dr => dr.DeletedBy).HasMaxLength(256);
            e.Property(dr => dr.DeletedReason).HasMaxLength(1024);
            e.Property(dr => dr.Version).IsRequired();

            e.Property(dr => dr.RevisionVector)
                .HasColumnName("revision_vector_json")
                .HasColumnType("jsonb")
                .HasConversion(RevisionVectorConverter, RevisionVectorComparer)
                .IsRequired();

            // Reverse lookup: which entities point at this attachment? (FindByAttachmentAsync)
            e.HasIndex(dr => new { dr.TenantId, dr.AttachmentId })
                .HasFilter("\"DeletedAtUtc\" IS NULL")
                .HasDatabaseName("ix_document_refs_tenant_attachment_active");

            // Forward lookup: which attachments does this entity own? (FindByParentAsync)
            e.HasIndex(dr => new { dr.TenantId, dr.ClusterCode, dr.ParentEntityType, dr.ParentEntityId })
                .HasFilter("\"DeletedAtUtc\" IS NULL")
                .HasDatabaseName("ix_document_refs_tenant_parent_active");
        });
    }
}
