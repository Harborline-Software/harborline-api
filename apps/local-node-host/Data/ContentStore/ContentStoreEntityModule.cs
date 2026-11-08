using Harborline.Api.Foundation.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>The columns a staged item and a committed item share. Times are UTC ticks, so a cutoff compares in SQL on either provider.</summary>
public abstract class ContentItemColumns
{
    /// <summary>The opaque internal item key, which names the item's file.</summary>
    public Guid ItemKey { get; init; }
    /// <summary>The owning tenant.</summary>
    public required string TenantId { get; init; }
    /// <summary>The tenant-keyed content id.</summary>
    public required string ContentId { get; init; }
    /// <summary>The plaintext length.</summary>
    public long Length { get; init; }
    /// <summary>The detected media type.</summary>
    public required string MediaType { get; init; }
    /// <summary>When the upload was staged, in UTC ticks.</summary>
    public long CreatedUtcTicks { get; init; }
    /// <summary>The wrapped data key, kept here and never in the file (ADR 0114 decision 1).</summary>
    public required byte[] WrappedDataKey { get; init; }
    /// <summary>The nonce the data key was wrapped with.</summary>
    public required byte[] WrapNonce { get; init; }
    /// <summary>The wrap's authentication tag.</summary>
    public required byte[] WrapTag { get; init; }
    /// <summary>The random prefix of the segment nonces.</summary>
    public required byte[] NoncePrefix { get; init; }
    /// <summary>The plaintext segment size.</summary>
    public int SegmentSize { get; init; }
    /// <summary>The lifecycle state.</summary>
    public int State { get; init; }
}

/// <summary>A committed item. Its tenant and content id are unique, which decides a duplicate (ADR 0113 decision 2).</summary>
public sealed class ContentItemRow : ContentItemColumns;

/// <summary>A staged item, written only after its bytes are durable.</summary>
public sealed class ContentStagedRow : ContentItemColumns;

/// <summary>One occurrence of an owner on a committed item.</summary>
public sealed class ContentOwnerRow
{
    /// <summary>Keeps owners in the order they were added.</summary>
    public long Id { get; init; }
    /// <summary>The owned item.</summary>
    public Guid ItemKey { get; init; }
    /// <summary>The owning record.</summary>
    public required string Record { get; init; }
    /// <summary>The owning element.</summary>
    public required string Element { get; init; }
}

/// <summary>One quota count. The tenant's own count is the row with no record.</summary>
public sealed class ContentQuotaRow
{
    /// <summary>The counted tenant.</summary>
    public required string TenantId { get; init; }
    /// <summary>True for the tenant's own count.</summary>
    public bool TenantGrain { get; init; }
    /// <summary>The counted record, empty at tenant grain.</summary>
    public required string Record { get; init; }
    /// <summary>The logical bytes counted.</summary>
    public long Bytes { get; init; }
}

/// <summary>An append-only reclamation entry (ck-18).</summary>
public sealed class ContentReclamationRow
{
    /// <summary>Keeps entries in the order they were appended.</summary>
    public long Id { get; init; }
    /// <summary>The reclaimed content id.</summary>
    public required string ContentId { get; init; }
    /// <summary>The tenant it belonged to.</summary>
    public required string TenantId { get; init; }
    /// <summary>The reclamation's cause.</summary>
    public required string Cause { get; init; }
    /// <summary>The run that reclaimed it.</summary>
    public required string Run { get; init; }
}

/// <summary>An item's integrity episode (Q34 to Q36).</summary>
public sealed class ContentIntegrityRow
{
    /// <summary>The item's tenant.</summary>
    public required string TenantId { get; init; }
    /// <summary>The item.</summary>
    public Guid ItemKey { get; init; }
    /// <summary>The first failure, in UTC ticks.</summary>
    public long FirstSeenUtcTicks { get; init; }
    /// <summary>The latest failure, in UTC ticks.</summary>
    public long LastSeenUtcTicks { get; init; }
    /// <summary>How many failures the episode has counted.</summary>
    public long Count { get; init; }
    /// <summary>The segment that failed.</summary>
    public int SegmentIndex { get; init; }
    /// <summary>Whether the episode is still open.</summary>
    public bool Active { get; init; }
    /// <summary>The first failure's trace.</summary>
    public required string TraceId { get; init; }
    /// <summary>The first failure's operation.</summary>
    public required string OperationId { get; init; }
    /// <summary>The first failure's reader.</summary>
    public required string PrincipalId { get; init; }
    /// <summary>The owning record the failure was read through.</summary>
    public required string OwnerRecord { get; init; }
    /// <summary>The owning element the failure was read through.</summary>
    public required string OwnerElement { get; init; }
}

/// <summary>An operation identity a store write used, so a repeated key is refused.</summary>
public sealed class ContentOperationRow
{
    /// <summary>The caller's idempotency key.</summary>
    public required string IdempotencyKey { get; init; }
    /// <summary>The caller's command id.</summary>
    public required string CommandId { get; init; }
    /// <summary>The digest of the command's content.</summary>
    public required string Fingerprint { get; init; }
}

/// <summary>The content store's bookkeeping in the node's one storage model (ADR 0113 decision 1). It holds no ciphertext.</summary>
public sealed class ContentStoreEntityModule : IHarborlineEntityModule
{
    /// <inheritdoc />
    public string ModuleKey => "harborline.local-node.content-store";

    /// <inheritdoc />
    public void Configure(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<ContentItemRow>(entity =>
        {
            entity.ToTable("content_store_items");
            entity.HasKey(row => row.ItemKey);
            entity.HasIndex(row => new { row.TenantId, row.ContentId }).IsUnique();
        });
        modelBuilder.Entity<ContentStagedRow>(entity =>
        {
            entity.ToTable("content_store_staged");
            entity.HasKey(row => row.ItemKey);
            entity.HasIndex(row => row.CreatedUtcTicks);
        });
        modelBuilder.Entity<ContentOwnerRow>(entity =>
        {
            entity.ToTable("content_store_owners");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => row.ItemKey);
        });
        modelBuilder.Entity<ContentQuotaRow>(entity =>
        {
            entity.ToTable("content_store_quota");
            entity.HasKey(row => new { row.TenantId, row.TenantGrain, row.Record });
        });
        modelBuilder.Entity<ContentReclamationRow>(entity =>
        {
            entity.ToTable("content_store_reclamations");
            entity.HasKey(row => row.Id);
        });
        modelBuilder.Entity<ContentIntegrityRow>(entity =>
        {
            entity.ToTable("content_store_integrity");
            entity.HasKey(row => new { row.TenantId, row.ItemKey });
        });
        modelBuilder.Entity<ContentOperationRow>(entity =>
        {
            entity.ToTable("content_store_operations");
            entity.HasKey(row => row.IdempotencyKey);
        });
    }
}
