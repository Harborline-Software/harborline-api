using System.Buffers.Binary;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Harborline.Api.LocalNodeHost.Data.ContentStore;
using Harborline.Api.LocalNodeHost.Tests.ContentStore;
using Harborline.Foundation.Assets.Common;
using Microsoft.EntityFrameworkCore;

namespace Harborline.Foundation.ContentStore.Tests;

// The producer's attack hooks reach into its in-memory backing, so here each does the same damage to the node's rows and files.
internal static class ProducerTestHooks
{
    private const int MagicBytes = 8;
    private const int FrameHeaderBytes = 8;

    public sealed record DurableKeyMaterial(Func<byte[]> ReadWrappedKey, Func<ICollection> ReadSegments)
    {
        // Read after an erase: a row or a file that is gone reads as empty, and one that survived fails the test's assertion.
        public byte[] WrappedDataKey => ReadWrappedKey();
        public ICollection Segments => ReadSegments();
    }

    public static (byte[] ContentIdKey, byte[] DataKeyWrapKey) DeriveSubkeys(byte[] key, TenantId tenant) =>
        ((byte[], byte[]))typeof(InMemoryContentStore).GetMethod("DeriveSubkeys", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [key, tenant])!;

    private static T Field<T>(InMemoryContentStore store, string name) =>
        (T)typeof(InMemoryContentStore).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;

    private static IContentBookkeeping Bookkeeping(InMemoryContentStore store) => Field<IContentBookkeeping>(store, "_bookkeeping");

    private static string PathOf(InMemoryContentStore store, Guid itemKey)
    {
        var name = itemKey.ToString("N", CultureInfo.InvariantCulture);
        return Path.Combine(Field<NodeContentByteStore>(store, "_bytes").DirectoryPath, name[..2], name);
    }

    public static async ValueTask<ContentStoreItem> StorageItemAsync(this InMemoryContentStore store, TenantId tenant, ContentId id) =>
        await Bookkeeping(store).FindItemAsync(new(tenant, id)) ?? throw new KeyNotFoundException();

    private static long FrameBytes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return FrameHeaderBytes + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(MagicBytes)) + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(MagicBytes + 4));
    }

    public static async ValueTask CorruptCiphertextAsync(this InMemoryContentStore store, TenantId tenant, ContentId id, int segment)
    {
        var path = PathOf(store, (await store.StorageItemAsync(tenant, id)).StageId);
        var offset = MagicBytes + segment * FrameBytes(path) + FrameHeaderBytes;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        file.Position = offset;
        var value = file.ReadByte();
        file.Position = offset;
        file.WriteByte((byte)(value ^ 1));
    }

    public static async ValueTask CorruptWrapTagAsync(this InMemoryContentStore store, TenantId tenant, ContentId id)
    {
        var item = await store.StorageItemAsync(tenant, id);
        var tag = item.WrapTag.ToArray();
        tag[0] ^= 1;
        await SetKeyAsync(store, tenant, id, item.WrappedDataKey, tag);
    }

    public static async ValueTask RewrapWithContentIdSubkeyAsync(this InMemoryContentStore store, TenantId tenant, ContentId id)
    {
        var item = await store.StorageItemAsync(tenant, id);
        var (contentIdKey, wrapKey) = DeriveSubkeys(Field<ITenantContentKeyProvider>(store, "_keys").GetKey(tenant), tenant);
        var associated = Encoding.ASCII.GetBytes(item.ContentId.Value);
        // ADR 0116 decision 1: the data-key-wrap subkey opens only the item's epoch key, and the epoch key opens the data key.
        // The AAD is DES-0054 eng-10's, as platform ContentStoreEnvelopeTests writes it independently of the store.
        var epoch = (await Field<IContentEpochKeyStore>(store, "_epochs").GetAsync(tenant, item.EpochId))!;
        var epochKey = new byte[32];
        using (var subkey = new AesGcm(wrapKey, 16))
            subkey.Decrypt(epoch.WrapNonce, epoch.WrappedKey, epoch.WrapTag, epochKey,
                Encoding.UTF8.GetBytes("harborline-content-store:epoch-key:v1|" + epoch.EpochId.ToString("N") + "|" + tenant.Value));
        var dataKey = new byte[32];
        using (var wrapper = new AesGcm(epochKey, 16)) wrapper.Decrypt(item.WrapNonce, item.WrappedDataKey, item.WrapTag, dataKey, associated);
        var wrapped = new byte[32];
        var tag = new byte[16];
        using (var wrong = new AesGcm(contentIdKey, 16)) wrong.Encrypt(item.WrapNonce, dataKey, wrapped, tag, associated);
        await SetKeyAsync(store, tenant, id, wrapped, tag);
    }

    public static async ValueTask DropLastSegmentKeepingLengthAsync(this InMemoryContentStore store, TenantId tenant, ContentId id) =>
        DropLastFrame(PathOf(store, (await store.StorageItemAsync(tenant, id)).StageId));

    public static async ValueTask TruncateLastSegmentAsync(this InMemoryContentStore store, TenantId tenant, ContentId id)
    {
        var item = await store.StorageItemAsync(tenant, id);
        var segments = DropLastFrame(PathOf(store, item.StageId));
        await using var context = await NodeContentTestStore.ContextsOf(Bookkeeping(store)).CreateDbContextAsync();
        await context.Set<ContentItemRow>().Where(row => row.ItemKey == item.StageId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Length, segments * item.SegmentSize));
    }

    public static async ValueTask<DurableKeyMaterial> KeyMaterialAsync(this InMemoryContentStore store, TenantId tenant, ContentId id) =>
        Material(store, (await store.StorageItemAsync(tenant, id)).StageId);

    public static DurableKeyMaterial KeyMaterial(this InMemoryContentStore store, Guid stage) => Material(store, stage);

    private static DurableKeyMaterial Material(InMemoryContentStore store, Guid itemKey) => new(
        () =>
        {
            using var context = NodeContentTestStore.ContextsOf(Bookkeeping(store)).CreateDbContext();
            return context.Set<ContentItemRow>().Where(row => row.ItemKey == itemKey).Select(row => row.WrappedDataKey).FirstOrDefault()
                ?? context.Set<ContentStagedRow>().Where(row => row.ItemKey == itemKey).Select(row => row.WrappedDataKey).FirstOrDefault()
                ?? [];
        },
        () => File.Exists(PathOf(store, itemKey)) ? new[] { PathOf(store, itemKey) } : Array.Empty<string>());

    // Everything the node retains for items: each row's values and each file's bytes.
    public static async ValueTask<List<object>> RetainedAsync(this InMemoryContentStore store)
    {
        await using var context = await NodeContentTestStore.ContextsOf(Bookkeeping(store)).CreateDbContextAsync();
        var rows = new List<ContentItemColumns>();
        rows.AddRange(await context.Set<ContentItemRow>().AsNoTracking().ToListAsync());
        rows.AddRange(await context.Set<ContentStagedRow>().AsNoTracking().ToListAsync());
        var retained = rows.SelectMany(row => new object[] { row.TenantId, row.ContentId, row.MediaType, row.WrappedDataKey, row.WrapNonce, row.WrapTag, row.NoncePrefix }).ToList();
        var directory = Field<NodeContentByteStore>(store, "_bytes").DirectoryPath;
        retained.AddRange(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(File.ReadAllBytes));
        return retained;
    }

    private static long DropLastFrame(string path)
    {
        var frame = FrameBytes(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
        var complete = (file.Length - MagicBytes - 1) / frame;
        file.SetLength(MagicBytes + complete * frame);
        return complete;
    }

    private static async ValueTask SetKeyAsync(InMemoryContentStore store, TenantId tenant, ContentId id, byte[] wrapped, byte[] tag)
    {
        await using var context = await NodeContentTestStore.ContextsOf(Bookkeeping(store)).CreateDbContextAsync();
        await context.Set<ContentItemRow>().Where(row => row.TenantId == tenant.Value && row.ContentId == id.Value)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.WrappedDataKey, wrapped).SetProperty(row => row.WrapTag, tag));
    }
}
