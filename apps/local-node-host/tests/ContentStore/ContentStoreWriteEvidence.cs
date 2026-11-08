// From platform 22491171, unchanged.
using Harborline.Foundation.Assets.Common;
using Harborline.Kernel.Core;

namespace Harborline.Foundation.ContentStore.Tests;

// Fresh kernel evidence for tests about something else, so the section 9 tests keep their own shape. Boundary behaviour is
// tested with explicit evidence in ContentStoreBoundaryTests.
internal static class ContentStoreWriteEvidence
{
    public const string Actor = "principal.test-writer";

    public static ContentStoreWrite New(string actor = Actor, IContentBookkeepingSession? transaction = null)
    {
        var id = Guid.NewGuid().ToString("N");
        return new(new("command-" + id, "key-" + id, "fingerprint-" + id), new("audit-" + id, actor, DateTimeOffset.UnixEpoch, ReadOnlyMemory<byte>.Empty), transaction);
    }

    public static ValueTask<Guid> PutAsync(this InMemoryContentStore store, TenantId tenant, Stream stream, string? mediaType, string? sha256, FieldNarrowing? field = null) =>
        store.PutAsync(tenant, stream, mediaType, sha256, New(), field);

    public static ValueTask<ContentReference> CommitReferenceAsync(this InMemoryContentStore store, TenantId tenant, Guid stage, ContentOwner owner, string name) =>
        store.CommitReferenceAsync(tenant, stage, owner, name, New());

    public static ValueTask<bool> RemoveReferenceAsync(this InMemoryContentStore store, TenantId tenant, ContentReference reference, HoldStatus hold, string run) =>
        store.RemoveReferenceAsync(tenant, reference, hold, run, New());

    // A reader for tests about something else; the reader's own rules are tested in ContentStoreBoundaryTests.
    public static readonly ContentReadContext Reader = new("trace-test", "operation-test", "tenant-opaque-test", "principal.test-reader");

    public static ValueTask<ContentDelivery> ReadAsync(this InMemoryContentStore store, TenantId tenant, ContentReference reference, bool authorized,
        ContentByteRange? range = null, string? ifRange = null) =>
        store.ReadAsync(tenant, reference, authorized, Reader, range, ifRange);

    public static ValueTask<ContentExportResult> ExportAsync(this InMemoryContentStore store, TenantId tenant, IEnumerable<ContentReference> references, bool authorized) =>
        store.ExportAsync(tenant, references, authorized, Reader);

    public static ValueTask<int> SweepExpiredAsync(this InMemoryContentStore store, string run) =>
        store.SweepExpiredAsync(run, New(ContentStoreRunRegistration.StagingSweepActor));
}
