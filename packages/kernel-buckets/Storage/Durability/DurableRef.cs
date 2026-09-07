using Harborline.Api.Foundation.Blobs;

namespace Harborline.Api.Kernel.Buckets.Storage.Durability;

/// <summary>
/// PERS-2 (ADR 0137 D5b durability guard) — an opaque, stable identity for a durably-held unit of data that the
/// durability guard reasons about. It unifies the two "shed a copy" seams the guard protects:
/// <list type="bullet">
///   <item>the record-body eviction seam (<see cref="IStorageBudgetManager.EvictLruAsync"/>), keyed by
///   <c>(bucket, recordId)</c>; and</item>
///   <item>the blob-retention seam (<see cref="Harborline.Api.Foundation.Blobs.IBlobStore.UnpinAsync"/>), keyed by
///   <see cref="Cid"/>.</item>
/// </list>
/// The guard, the possession verifier, the eligibility predicate, and the never-evict registry all key on this
/// type, so a single durability decision covers both seams identically.
/// </summary>
/// <remarks>
/// The <see cref="Value"/> is namespaced by construction (<c>record:</c> / <c>blob:</c>) so a bucket record and a
/// blob CID can never collide in the possession ledger or the pin registry.
/// </remarks>
public readonly record struct DurableRef
{
    private DurableRef(string value) => Value = value;

    /// <summary>The stable, namespaced string key. Never null or empty once constructed via a factory.</summary>
    public string Value { get; }

    /// <summary>A reference to a bucket record body (the <see cref="IStorageBudgetManager"/> eviction unit).</summary>
    public static DurableRef ForRecord(string bucketName, string recordId)
    {
        ArgumentException.ThrowIfNullOrEmpty(bucketName);
        ArgumentException.ThrowIfNullOrEmpty(recordId);
        return new DurableRef($"record:{bucketName}/{recordId}");
    }

    /// <summary>A reference to a content-addressed blob (the <see cref="IBlobStore.UnpinAsync"/> retention unit).</summary>
    public static DurableRef ForBlob(Cid cid)
    {
        if (string.IsNullOrEmpty(cid.Value))
        {
            throw new ArgumentException("Cid.Value must be non-empty.", nameof(cid));
        }
        return new DurableRef($"blob:{cid.Value}");
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
