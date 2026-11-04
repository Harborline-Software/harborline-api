using System.Collections;
using System.Reflection;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ContentStore;

namespace Harborline.Foundation.ContentStore.Tests;

// Bridge the producer's existing friend-assembly-only attack hooks without changing the shared probes.
internal static class ProducerTestHooks
{
    public static (byte[] ContentIdKey, byte[] DataKeyWrapKey) DeriveSubkeys(byte[] key, TenantId tenant) =>
        ((byte[], byte[]))typeof(InMemoryContentStore).GetMethod("DeriveSubkeys", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [key, tenant])!;
    private static object? Invoke(InMemoryContentStore store, string name, params object[] args) =>
        typeof(InMemoryContentStore).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, args.Select(a => a.GetType()).ToArray())!.Invoke(store, args);
    private static IContentStoreStorage Storage(InMemoryContentStore store) =>
        (IContentStoreStorage)typeof(InMemoryContentStore).GetField("_storage", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
    private static void Tamper(InMemoryContentStore store, string name, TenantId tenant, ContentId id, params object[] tail)
    {
        var backing = Storage(store);
        lock (backing.SyncRoot)
        {
            Invoke(store, name, [tenant, id, .. tail]);
            // Simulate changed bytes on disk too, rather than relying on shutdown to persist them.
            backing.Save(new(ContentStoreOperation.Commit, [new(tenant, id)], [], [], []));
        }
    }
    public static void CorruptCiphertext(this InMemoryContentStore store, TenantId tenant, ContentId id, int segment) => Tamper(store, nameof(CorruptCiphertext), tenant, id, segment);
    public static void CorruptWrapTag(this InMemoryContentStore store, TenantId tenant, ContentId id) => Tamper(store, nameof(CorruptWrapTag), tenant, id);
    public static void RewrapWithContentIdSubkey(this InMemoryContentStore store, TenantId tenant, ContentId id) => Tamper(store, nameof(RewrapWithContentIdSubkey), tenant, id);
    public static void DropLastSegmentKeepingLength(this InMemoryContentStore store, TenantId tenant, ContentId id) => Tamper(store, nameof(DropLastSegmentKeepingLength), tenant, id);
    public static void TruncateLastSegment(this InMemoryContentStore store, TenantId tenant, ContentId id) => Tamper(store, nameof(TruncateLastSegment), tenant, id);
    public static ContentStoreItem StorageItem(this InMemoryContentStore store, TenantId tenant, ContentId id) => (ContentStoreItem)Invoke(store, nameof(StorageItem), tenant, id)!;
    public static (byte[] WrappedDataKey, ICollection Segments) KeyMaterial(this InMemoryContentStore store, TenantId tenant, ContentId id) => ((byte[], ICollection))Invoke(store, nameof(KeyMaterial), tenant, id)!;
    public static (byte[] WrappedDataKey, ICollection Segments) KeyMaterial(this InMemoryContentStore store, Guid stage) => ((byte[], ICollection))Invoke(store, nameof(KeyMaterial), stage)!;
}
