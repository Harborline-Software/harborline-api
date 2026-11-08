// Shared producer conformance suite from platform 22491171, ContentStoreTests.cs.
// Only the concrete in-memory runner is omitted. Internal hooks are supplied by ProducerTestHooks, and the object-graph walk
// over the in-memory backing is replaced by ProducerTestHooks.RetainedAsync, which reads what the node keeps: its rows and files.
#pragma warning disable CA2022 // These reads test what one read returns, so a short read is the point.
using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Harborline.Foundation.Assets.Common;
using Harborline.Foundation.ExecutionRuntime;
using Xunit;

namespace Harborline.Foundation.ContentStore.Tests;

// Tests named Clause_<ids>_... are the DES-0054 §9 verification clauses, one test per clause; the Trait carries the
// clause's first id so the design record can point its probe at test:<name>.
public abstract class ContentStoreStorageContract
{
    protected abstract IContentStoreStorage CreateStorage();
    protected virtual IContentStoreStorage RestartStorage(IContentStoreStorage storage) => storage;

    [Fact, Trait("DES-0054", "content-store-ck-17")]
    public async Task Clause_q31_storage_port_preserves_committed_staged_and_reclamation_state_when_a_store_is_rebuilt()
    {
        var backing = CreateStorage();
        var clock = new ContractClock();
        var first = new InMemoryContentStore(new(300_000, 600_000, 600_000, TimeSpan.FromMinutes(1)), new ContractKeys(), clock, backing);
        var committedStage = await first.PutAsync(new TenantId("contract"), new MemoryStream("committed"u8.ToArray()), "text/plain", null);
        var committed = await first.CommitReferenceAsync(new TenantId("contract"), committedStage, new ContentOwner("record", "one"), "one.txt");
        var staged = await first.PutAsync(new TenantId("contract"), new MemoryStream("staged"u8.ToArray()), "text/plain", null);
        var reclaimedStage = await first.PutAsync(new TenantId("contract"), new MemoryStream("reclaimed"u8.ToArray()), "text/plain", null);
        var reclaimed = await first.CommitReferenceAsync(new TenantId("contract"), reclaimedStage, new ContentOwner("record", "gone"), "gone.txt");
        await first.RemoveReferenceAsync(new TenantId("contract"), reclaimed, HoldStatus.None, "dispose");

        var rebuilt = new InMemoryContentStore(new(300_000, 600_000, 600_000, TimeSpan.FromMinutes(1)), new ContractKeys(), clock, RestartStorage(backing));
        Assert.Equal(new ReclamationEntry(reclaimed.ContentId, new TenantId("contract"), "last-reference-removed", "dispose"), Assert.Single(rebuilt.Reclamations));
        using var body = (await rebuilt.ReadAsync(new TenantId("contract"), committed, true)).Body;
        using var plaintext = new MemoryStream();
        await body.CopyToAsync(plaintext);
        Assert.Equal("committed"u8.ToArray(), plaintext.ToArray());
        Assert.Equal(1, rebuilt.StagedCount);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await rebuilt.SweepExpiredAsync("sweep"));
        Assert.Equal(ContentRefusals.StagingExpired, (await Assert.ThrowsAsync<ContentRefusedException>(async () => await rebuilt.CommitReferenceAsync(new TenantId("contract"), staged, new ContentOwner("record", "two"), "two.txt"))).Code);
        Assert.Collection(rebuilt.Reclamations,
            entry => Assert.Equal(new ReclamationEntry(reclaimed.ContentId, new TenantId("contract"), "last-reference-removed", "dispose"), entry),
            entry =>
            {
                Assert.Equal(new TenantId("contract"), entry.TenantId);
                Assert.Equal("staging-expired", entry.Cause);
                Assert.Equal("sweep", entry.Run);
            });
    }

    private sealed class ContractKeys : ITenantContentKeyProvider
    {
        public byte[] GetKey(TenantId tenantId) => SHA256.HashData(Encoding.UTF8.GetBytes("contract:" + tenantId.Value));
    }

    private sealed class ContractClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}

public abstract class ContentStoreTests : ContentStoreStorageContract
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly byte[] Hello = "hello"u8.ToArray();

    [Fact, Trait("DES-0054", "content-store-eng-1")]
    public async Task Clause_ck7_eng1_one_byte_over_a_segment_boundary_retains_no_partial_item()
    {
        var backing = CreateStorage();
        var store = new InMemoryContentStore(new(131_072, 300_000, 300_000, TimeSpan.FromMinutes(1)),
            new TestKeys(), new FakeTimeProvider(), backing);
        var bytes = LargeBytes(2);

        var refusal = await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream(bytes), "text/plain", null));

        Assert.Equal("content.too-large", refusal.Code);
        Assert.Equal(131_072, refusal.Detail["limit"]);
        Assert.Equal(131_073, refusal.Detail["observed"]);
        Assert.Equal(0, backing.Bookkeeping.StagedCount);
        Assert.Equal(0, backing.Bookkeeping.CommittedCount);
        Assert.Empty(backing.Bookkeeping.Reclamations);
        Assert.Empty(await backing.Bytes.WrittenAtOrBeforeAsync(DateTimeOffset.MaxValue));
        var stage = await store.PutAsync(TenantA, new MemoryStream(bytes[..131_072]), "text/plain", null);
        var reference = await store.CommitReferenceAsync(TenantA, stage, new("record", "file"), "exact.txt");
        Assert.Equal(bytes[..131_072], await ReadBytes(await store.ReadAsync(TenantA, reference, true)));
    }

    [Fact, Trait("DES-0054", "content-store-eng-10")]
    public async Task Clause_q30_a_failed_body_never_skips_a_corrupt_middle_segment_or_completes_normally()
    {
        var log = new CapturingLogger();
        var store = LargeStore(new FakeTimeProvider(), log);
        var reference = await Commit(store, TenantA, "record", LargeBytes(2));
        var unaffected = await Commit(store, TenantA, "other", "safe"u8.ToArray());
        await store.CorruptCiphertextAsync(TenantA, reference.ContentId, 1);
        using var body = (await store.ReadAsync(TenantA, reference, true)).Body;
        var buffer = new byte[65_536];
        var clear = Assert.Single(body.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.GetValue(body)).OfType<byte[]>(), bytes => bytes.Length == 65_536);

        Assert.Equal(65_536, await body.ReadAsync(buffer.AsMemory()));
        Assert.Equal(Enumerable.Repeat((byte)'a', 65_536).ToArray(), buffer);
        var failure = await Assert.ThrowsAsync<ContentIntegrityException>(async () => await body.ReadAsync(new byte[1].AsMemory()));
        Assert.Equal(1, failure.SegmentIndex);
        Assert.All(clear, value => Assert.Equal(0, value));
        Assert.Same(failure, await Assert.ThrowsAsync<ContentIntegrityException>(async () => await body.ReadAsync(new byte[1].AsMemory())));
        Assert.Same(failure, await Assert.ThrowsAsync<ContentIntegrityException>(async () => await body.ReadAsync(Memory<byte>.Empty)));
        Assert.Equal(1, Assert.Single(store.IntegrityEntries).Count);
        Assert.Single(log.Events);
        Assert.Equal("Committed", (await store.StorageItemAsync(TenantA, reference.ContentId)).State.ToString());
        Assert.Equal("safe"u8.ToArray(), await ReadBytes(await store.ReadAsync(TenantA, unaffected, true)));
    }

    [Fact, Trait("DES-0054", "content-store-eng-10")]
    public async Task Clause_eng10_stream_cancellation_and_disposal_are_not_integrity_failures()
    {
        var store = Store();
        var reference = await Commit(store, TenantA, "record");
        var body = (await store.ReadAsync(TenantA, reference, true)).Body;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var buffer = new byte[2];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            var read = await body.ReadAsync(buffer.AsMemory(), cancellation.Token);
            Assert.InRange(read, 0, 2);
        });
        Assert.Equal(5, body.Length);
        Assert.Equal(2, await body.ReadAsync(buffer.AsMemory()));
        Assert.Equal("he"u8.ToArray(), buffer);
        Assert.Equal(5, body.Length);
        var privateBuffers = body.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(field => field.GetValue(body)).OfType<byte[]>().ToArray();
        Assert.Contains(privateBuffers, bytes => bytes.AsSpan().SequenceEqual("hello"u8));
        body.Dispose();
        Assert.All(privateBuffers, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        Assert.False(body.CanRead);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await body.ReadAsync(buffer.AsMemory()));
        Assert.Empty(store.IntegrityEntries);
        Assert.True(store.IsIntegrityHealthy);
    }

    [Fact, Trait("DES-0054", "content-store-ck-18")]
    public async Task Clause_q34_storage_restart_preserves_integrity_until_full_verification_or_erasure()
    {
        var backing = CreateStorage();
        var clock = new FakeTimeProvider();
        var log = new CapturingLogger();
        var options = new ContentStoreOptions(300_000, 600_000, 600_000, TimeSpan.FromMinutes(1));
        var first = new InMemoryContentStore(options, new TestKeys(), clock, backing, log);
        var reference = await Commit(first, TenantA, "record", LargeBytes(2));
        await first.CorruptCiphertextAsync(TenantA, reference.ContentId, 1);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await first.ReadAsync(TenantA, reference, true, new ContentByteRange(65_536, 65_538)));
        var episode = Assert.Single(first.IntegrityEntries);

        backing = RestartStorage(backing);
        var rebuilt = new InMemoryContentStore(options, new TestKeys(), clock, backing, log);
        Assert.Equal(episode, Assert.Single(rebuilt.IntegrityEntries));
        Assert.False(rebuilt.IsIntegrityHealthy);
        Assert.Equal("aaa"u8.ToArray(), await ReadBytes(await rebuilt.ReadAsync(TenantA, reference, true, new ContentByteRange(0, 2))));
        Assert.False(rebuilt.IsIntegrityHealthy);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await rebuilt.ReadAsync(TenantA, reference, true, new ContentByteRange(65_536, 65_538)));
        Assert.Equal(2, Assert.Single(rebuilt.IntegrityEntries).Count);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), Assert.Single(rebuilt.IntegrityEntries).LastSeenUtc);
        Assert.Single(log.Events);

        await rebuilt.CorruptCiphertextAsync(TenantA, reference.ContentId, 1);
        Assert.Equal(LargeBytes(2), await ReadBytes(await rebuilt.ReadAsync(TenantA, reference, true)));
        Assert.True(rebuilt.IsIntegrityHealthy);
        backing = RestartStorage(backing);
        var recovered = new InMemoryContentStore(options, new TestKeys(), clock, backing, log);
        Assert.False(Assert.Single(recovered.IntegrityEntries).Active);
        await recovered.CorruptCiphertextAsync(TenantA, reference.ContentId, 1);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await recovered.ReadAsync(TenantA, reference, true, new ContentByteRange(65_536, 65_538)));
        Assert.Equal(2, log.Events.Count);
        Assert.True(await recovered.RemoveReferenceAsync(TenantA, reference, HoldStatus.None, "erase"));
        Assert.True(recovered.IsIntegrityHealthy);
    }

    [Fact, Trait("DES-0054", "content-store-ck-7")]
    public async Task Clause_ck7_eng1_put_one_byte_over_the_effective_limit_refuses_too_large_with_detail_and_stages_nothing()
    {
        var store = Store(itemLimit: 100);
        var field = new FieldNarrowing("field:inspection.photos", 4, null);
        var refusal = await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null, field));
        Assert.Equal(ContentRefusals.TooLarge, refusal.Code);
        Assert.Equal("field:inspection.photos", refusal.Definition);
        Assert.Equal(4, refusal.Detail["limit"]);
        Assert.Equal(5, refusal.Detail["observed"]);
        Assert.Equal(0, store.StagedCount);

        // A field limit above the store's never widens it: detail.limit is the store's (ck-7, board P4).
        var lower = Store(itemLimit: 4);
        var storeRefusal = await Assert.ThrowsAsync<ContentRefusedException>(async () => await lower.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null, new FieldNarrowing("field:wide", 1000, null)));
        Assert.Equal(4, storeRefusal.Detail["limit"]);
        Assert.Equal(InMemoryContentStore.StoreDeclaration, storeRefusal.Definition);

        // Exactly at the limit is admitted.
        await lower.PutAsync(TenantA, new MemoryStream("hell"u8.ToArray()), "text/plain", null);
    }

    [Fact, Trait("DES-0054", "content-store-ck-8")]
    public async Task Clause_ck8_eng5_two_concurrent_commits_that_fit_alone_but_not_together_exactly_one_commits()
    {
        foreach (var (options, expected) in new[]
        {
            (new ContentStoreOptions(100, 5, 1000, TimeSpan.FromMinutes(1)), ContentRefusals.RecordQuota),
            (new ContentStoreOptions(100, 1000, 5, TimeSpan.FromMinutes(1)), ContentRefusals.TenantQuota),
        })
        {
            var store = new InMemoryContentStore(options, new TestKeys(), new FakeTimeProvider(), CreateStorage());
            var first = await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null);
            var second = await store.PutAsync(TenantA, new MemoryStream("world"u8.ToArray()), "text/plain", null);
            using var barrier = new Barrier(2);
            var outcomes = await Task.WhenAll(new[] { first, second }.Select(stage => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    await store.CommitReferenceAsync(TenantA, stage, new ContentOwner("record-1", stage.ToString()), "x.txt");
                    return (ContentRefusedException?)null;
                }
                catch (ContentRefusedException refusal)
                {
                    return refusal;
                }
            })));

            var refusal = Assert.Single(outcomes, outcome => outcome is not null)!;
            Assert.Equal(expected, refusal.Code);
            Assert.Equal(5, refusal.Detail["limit"]);
            Assert.Equal(10, refusal.Detail["observed"]);
            Assert.Equal(1, store.CommittedCount);
        }
    }

    [Fact, Trait("DES-0054", "content-store-ck-5")]
    public async Task Clause_ck5_ck6_eng3_html_renamed_pdf_refuses_mismatch_and_an_allowed_type_outside_the_field_list_refuses_not_allowed()
    {
        var store = Store();
        var html = "<!DOCTYPE html><html><script>alert(1)</script></html>"u8.ToArray();
        Assert.Equal(ContentRefusals.MediaTypeMismatch,
            (await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream(html), "application/pdf", null))).Code);

        var narrow = new FieldNarrowing("field:pdf-only", null, new HashSet<string> { "application/pdf" });
        var refusal = await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null, narrow));
        Assert.Equal(ContentRefusals.MediaTypeNotAllowed, refusal.Code);
        Assert.Equal("field:pdf-only", refusal.Definition);

        // Bytes with no admitted signature and no text shape are outside the store allowlist.
        var zip = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x01 };
        Assert.Equal(ContentRefusals.MediaTypeNotAllowed,
            (await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream(zip), null, null))).Code);
        Assert.Equal(0, store.StagedCount);
    }

    [Fact, Trait("DES-0054", "content-store-eng-2")]
    public async Task Clause_eng2_a_declared_digest_that_differs_from_the_bytes_refuses_digest_mismatch()
    {
        var store = Store();
        var wrong = Convert.ToHexStringLower(SHA256.HashData("different"u8));
        Assert.Equal(ContentRefusals.DigestMismatch,
            (await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", wrong))).Code);
        Assert.Equal(ContentRefusals.DigestMismatch,
            (await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", "not-hex"))).Code);
        Assert.Equal(0, store.StagedCount);

        await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", Convert.ToHexStringLower(SHA256.HashData(Hello)));
        Assert.Equal(1, store.StagedCount);
    }

    [Fact, Trait("DES-0054", "content-store-ck-4")]
    public async Task Clause_ck4_eng4_same_bytes_twice_in_one_tenant_store_one_item_and_in_two_tenants_two_items()
    {
        var store = Store();
        var first = await Commit(store, TenantA, "record-1");
        var againStage = await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null);
        Assert.Equal(1, store.StagedCount); // the put stages its own copy, exactly as a put of new bytes (board P2)
        var again = await store.CommitReferenceAsync(TenantA, againStage, new ContentOwner("record-2", "photos/0"), "y.txt");
        Assert.Equal(first.ContentId, again.ContentId);
        Assert.Equal(1, store.CommittedCount);
        Assert.Equal(0, store.StagedCount);

        var fresh = await Commit(store, TenantA, "record-3", "other"u8.ToArray());
        Assert.Equal(typeof(ContentReference), fresh.GetType()); // same answer shape as the collapsed commit
        var other = await Commit(store, TenantB, "record-1");
        Assert.NotEqual(first.ContentId, other.ContentId);
        Assert.Equal(3, store.CommittedCount);
    }

    [Fact, Trait("DES-0054", "content-store-eng-6")]
    public async Task Clause_eng6_eng11_an_unauthorized_read_and_another_tenants_read_refuse_exactly_as_a_nonexistent_id()
    {
        var store = Store();
        var reference = await Commit(store, TenantA, "record-1");
        var missing = await Assert.ThrowsAsync<ContentNotFoundException>(async () => await store.ReadAsync(TenantA, reference with { ContentId = new("hmac-sha256:" + new string('a', 64)) }, true));
        var denied = await Assert.ThrowsAsync<ContentNotFoundException>(async () => await store.ReadAsync(TenantA, reference, false));
        var cross = await Assert.ThrowsAsync<ContentNotFoundException>(async () => await store.ReadAsync(TenantB, reference, true));
        var wrongOwner = await Assert.ThrowsAsync<ContentNotFoundException>(async () => await store.ReadAsync(TenantA, reference with { Owner = new("record-9", "x") }, true));
        Assert.All(new[] { denied, cross, wrongOwner }, refusal => Assert.Equal(missing.Message, refusal.Message));
    }

    [Fact, Trait("DES-0054", "content-store-eng-7")]
    public async Task Clause_eng7_a_served_item_carries_nosniff_attachment_a_strong_etag_and_only_the_detected_type()
    {
        var store = Store();
        var stage = await store.PutAsync(TenantA, new MemoryStream("a,b\r\n1,2\r\n"u8.ToArray()), "text/csv; charset=utf-8", null);
        var reference = await store.CommitReferenceAsync(TenantA, stage, new ContentOwner("record-1", "files/0"), "evil\"\r\nSet-Cookie: x.csv");
        var delivery = await store.ReadAsync(TenantA, reference, true);
        Assert.Equal("nosniff", ContentDelivery.XContentTypeOptions);
        Assert.Equal("text/csv", delivery.ContentType);
        Assert.Equal("attachment; filename=\"evil___Set-Cookie: x.csv\"; filename*=UTF-8''evil%22%0D%0ASet-Cookie%3A%20x.csv", delivery.ContentDisposition);
        Assert.Equal("\"" + reference.ContentId.Value + "\"", delivery.ETag);
        Assert.DoesNotContain("W/", delivery.ETag, StringComparison.Ordinal);
        Assert.Equal(200, delivery.StatusCode);

        var unicode = await store.CommitReferenceAsync(TenantA, await store.PutAsync(TenantA, new MemoryStream("still text"u8.ToArray()), "text/plain", null),
            new ContentOwner("record-1", "files/1"), "résumé.pdf");
        var unicodeDelivery = await store.ReadAsync(TenantA, unicode, true);
        Assert.Equal("attachment; filename=\"r_sum_.pdf\"; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf", unicodeDelivery.ContentDisposition);
    }

    [Fact, Trait("DES-0054", "content-store-ck-10")]
    public async Task Clause_ck10_eng9_erasing_one_of_two_references_keeps_the_bytes_and_erasing_the_last_reclaims_them()
    {
        var store = Store();
        var first = await Commit(store, TenantA, "record-1");
        var second = await Commit(store, TenantA, "record-2");
        Assert.False(await store.RemoveReferenceAsync(TenantA, first, HoldStatus.None, "disposal-1"));
        Assert.Equal(Hello, await ReadBytes(await store.ReadAsync(TenantA, second, true)));

        Assert.True(await store.RemoveReferenceAsync(TenantA, second, HoldStatus.None, "disposal-2"));
        await Assert.ThrowsAsync<ContentNotFoundException>(async () => await store.ReadAsync(TenantA, second, true));
        Assert.Equal(0, store.CommittedCount);
        var entry = Assert.Single(store.Reclamations);
        Assert.Equal(new ReclamationEntry(second.ContentId, TenantA, "last-reference-removed", "disposal-2"), entry);
    }

    [Fact, Trait("DES-0054", "content-store-cc-4")]
    public async Task Clause_ck10_cc4_a_hold_keeps_the_bytes_until_released_and_an_unreadable_hold_fails_closed()
    {
        var store = Store();
        var held = await Commit(store, TenantA, "record-held");
        var other = await Commit(store, TenantA, "record-other");
        Assert.False(await store.RemoveReferenceAsync(TenantA, other, HoldStatus.None, "disposal-1"));
        Assert.False(await store.RemoveReferenceAsync(TenantA, held, HoldStatus.Held, "disposal-2"));
        Assert.False(await store.RemoveReferenceAsync(TenantA, held, HoldStatus.Unreadable, "disposal-3"));
        Assert.Equal(Hello, await ReadBytes(await store.ReadAsync(TenantA, held, true)));
        Assert.Empty(store.Reclamations);

        Assert.True(await store.RemoveReferenceAsync(TenantA, held, HoldStatus.None, "disposal-4"));
        Assert.Single(store.Reclamations);
    }

    [Fact, Trait("DES-0054", "content-store-ck-13")]
    public async Task Clause_ck13_a_staged_item_nobody_commits_expires_under_the_registered_sweep_with_no_committed_orphan()
    {
        var clock = new FakeTimeProvider();
        var store = Store(clock: clock);
        var registry = new RunKindRegistry();
        ContentStoreRunRegistration.Register(registry);
        Assert.Equal("content-store", registry.Require(ContentStoreRunRegistration.StagingSweepKind).OwningEngine);
        Assert.Equal("schedule", ContentStoreRunRegistration.StagingSweepTriggerKind);
        Assert.Equal("sys.sched.content-staging", ContentStoreRunRegistration.StagingSweepSchedule);

        var abandoned = await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null);
        var late = await store.PutAsync(TenantA, new MemoryStream("late"u8.ToArray()), "text/plain", null);
        clock.Advance(TimeSpan.FromMinutes(1));

        // Commit after expiry refuses even before any sweep has run.
        Assert.Equal(ContentRefusals.StagingExpired,
            (await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.CommitReferenceAsync(TenantA, late, new ContentOwner("record-1", "0"), "x.txt"))).Code);

        // No request is in flight: the put returned long ago; the sweep alone reclaims.
        Assert.Equal(1, await store.SweepExpiredAsync("sweep-1"));
        Assert.Equal(0, store.StagedCount);
        Assert.Equal(0, store.CommittedCount);
        Assert.All(store.Reclamations, entry => Assert.Equal("staging-expired", entry.Cause));
        Assert.Contains(store.Reclamations, entry => entry.Run == "sweep-1");
        Assert.Equal(ContentRefusals.StagingExpired,
            (await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.CommitReferenceAsync(TenantA, abandoned, new ContentOwner("record-1", "0"), "x.txt"))).Code);
    }

    [Fact, Trait("DES-0054", "content-store-ck-12")]
    public async Task Clause_ck12_cc5_two_references_to_one_item_are_each_governed_by_their_own_marking()
    {
        var store = Store();
        var open = await Commit(store, TenantA, "record-open");
        var restricted = await Commit(store, TenantA, "record-restricted");
        Assert.Equal(open.ContentId, restricted.ContentId);

        // The gate evaluates each reference's field marking upstream; the store keeps no marking of its own.
        Assert.Equal(Hello, await ReadBytes(await store.ReadAsync(TenantA, open, authorized: true)));
        await Assert.ThrowsAsync<ContentNotFoundException>(async () => await store.ReadAsync(TenantA, restricted, authorized: false));
    }

    [Fact, Trait("DES-0054", "content-store-cc-7")]
    public async Task Clause_cc7_export_fixity_verifies_every_keyed_digest_a_tampered_byte_refuses_and_no_plain_sha256_is_carried()
    {
        var store = Store();
        var references = new[] { await Commit(store, TenantA, "record-1"), await Commit(store, TenantA, "record-2", "second"u8.ToArray()) };
        var export = await store.ExportAsync(TenantA, references, authorized: true);
        InMemoryContentStore.VerifyExport(export.Package, export.FixityKey);

        foreach (var item in export.Package.Items)
        {
            Assert.NotEqual(SHA256.HashData(item.Bytes), item.Digest);
            Assert.DoesNotContain(Convert.ToHexStringLower(SHA256.HashData(item.Bytes)), System.Text.Json.JsonSerializer.Serialize(export.Package), StringComparison.Ordinal);
        }

        Assert.NotEqual(export.FixityKey, (await store.ExportAsync(TenantA, references, authorized: true)).FixityKey); // fresh per export
        export.Package.Items[1].Bytes[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => InMemoryContentStore.VerifyExport(export.Package, export.FixityKey));
        await Assert.ThrowsAsync<ContentNotFoundException>(async () => await store.ExportAsync(TenantA, references, authorized: false));
    }

    [Fact]
    public async Task Identical_bytes_in_two_tenants_get_different_keyed_ids_and_no_plain_digest_is_persisted()
    {
        var store = Store();
        var plain = SHA256.HashData(Hello);
        var stage = await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", Convert.ToHexStringLower(plain));
        var a = await store.CommitReferenceAsync(TenantA, stage, new ContentOwner("record-1", "0"), "x.txt");
        var b = await Commit(store, TenantB, "record-1");
        Assert.NotEqual(a.ContentId, b.ContentId);
        var subkeys = ProducerTestHooks.DeriveSubkeys(new TestKeys().GetKey(TenantA), TenantA);
        Assert.Equal("hmac-sha256:" + Convert.ToHexStringLower(HMACSHA256.HashData(subkeys.ContentIdKey, Hello)), a.ContentId.Value);

        var retained = await store.RetainedAsync();
        Assert.DoesNotContain(retained.OfType<byte[]>(), bytes => bytes.AsSpan().IndexOf(plain) >= 0);
        Assert.DoesNotContain(retained.OfType<string>(), text => text.Contains(Convert.ToHexStringLower(plain), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_tampered_or_truncated_ciphertext_refuses_on_read()
    {
        var store = new InMemoryContentStore(new(200_000, 600_000, 600_000, TimeSpan.FromMinutes(1)), new TestKeys(), new FakeTimeProvider(), CreateStorage());
        var tampered = await Commit(store, TenantA, "record-1", LargeBytes(2));
        await store.CorruptCiphertextAsync(TenantA, tampered.ContentId, 1);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await ReadBytes(await store.ReadAsync(TenantA, tampered, true)));

        var truncated = await Commit(store, TenantA, "record-2", LargeBytes(2));
        await store.TruncateLastSegmentAsync(TenantA, truncated.ContentId);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await ReadBytes(await store.ReadAsync(TenantA, truncated, true)));
    }

    [Fact]
    public async Task A_single_range_answers_206_across_segments_416_when_unsatisfiable_and_the_whole_item_on_a_stale_if_range()
    {
        var store = Store();
        var reference = await Commit(store, TenantA, "record-1", "0123456789"u8.ToArray());
        var etag = "\"" + reference.ContentId.Value + "\"";

        var partial = await store.ReadAsync(TenantA, reference, true, new ContentByteRange(2, 7));
        Assert.Equal(206, partial.StatusCode);
        Assert.Equal("234567"u8.ToArray(), await ReadBytes(partial));
        Assert.Equal("bytes 2-7/10", partial.ContentRange);

        var tail = await store.ReadAsync(TenantA, reference, true, new ContentByteRange(8, 500), etag);
        Assert.Equal("89"u8.ToArray(), await ReadBytes(tail));
        Assert.Equal("bytes 8-9/10", tail.ContentRange);

        var unsatisfiable = await store.ReadAsync(TenantA, reference, true, new ContentByteRange(10, 12));
        Assert.Equal(416, unsatisfiable.StatusCode);
        Assert.Equal("bytes */10", unsatisfiable.ContentRange);

        var stale = await store.ReadAsync(TenantA, reference, true, new ContentByteRange(2, 7), "\"hmac-sha256:" + new string('0', 64) + "\"");
        Assert.Equal(200, stale.StatusCode);
        Assert.Equal("0123456789"u8.ToArray(), await ReadBytes(stale));
    }

    [Fact]
    public async Task The_text_check_is_exactly_whatwg_7_1_over_the_1445_byte_resource_header()
    {
        var store = Store(itemLimit: 4000);
        await store.PutAsync(TenantA, new MemoryStream("esc\u001B[0m"u8.ToArray()), "text/plain", null); // 0x1B is not a binary data byte
        await store.PutAsync(TenantA, new MemoryStream([0xFF, 0xFE, 0x68, 0x00]), "text/plain", null); // UTF-16LE with a BOM
        Assert.Equal(ContentRefusals.MediaTypeMismatch,
            (await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream([0x68, 0x00, 0x69, 0x00]), "text/plain", null))).Code); // UTF-16 without a BOM

        var late = Enumerable.Repeat((byte)'a', 1445).Concat(new byte[] { 0x00 }).ToArray();
        await store.PutAsync(TenantA, new MemoryStream(late), "text/plain", null); // known limit: binary after the header (ck-6)
        var early = Enumerable.Repeat((byte)'a', 1444).Concat(new byte[] { 0x00 }).ToArray();
        await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream(early), "text/plain", null));
    }

    [Fact]
    public async Task A_commit_naming_another_tenants_stage_refuses_and_leaves_that_stage_intact()
    {
        var store = Store();
        var stage = await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null);
        Assert.Equal(ContentRefusals.StagingExpired,
            (await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.CommitReferenceAsync(TenantB, stage, new ContentOwner("record-1", "0"), "x.txt"))).Code);
        Assert.Equal(1, store.StagedCount);
        await store.CommitReferenceAsync(TenantA, stage, new ContentOwner("record-1", "0"), "x.txt");
    }

    [Fact]
    public void The_store_refuses_to_start_without_every_unstated_measurement()
    {
        Assert.Throws<ArgumentException>(() => new InMemoryContentStore(new(0, 1, 1, TimeSpan.FromMinutes(1)), new TestKeys(), new FakeTimeProvider()));
        Assert.Throws<ArgumentException>(() => new InMemoryContentStore(new(1, 0, 1, TimeSpan.FromMinutes(1)), new TestKeys(), new FakeTimeProvider()));
        Assert.Throws<ArgumentException>(() => new InMemoryContentStore(new(1, 1, 0, TimeSpan.FromMinutes(1)), new TestKeys(), new FakeTimeProvider()));
        Assert.Throws<ArgumentException>(() => new InMemoryContentStore(new(1, 1, 1, TimeSpan.Zero), new TestKeys(), new FakeTimeProvider()));
        Assert.Equal(4, Enum.GetValues<ContentLifecycleState>().Length); // quarantined reserved, closed at four (§6)
    }

    [Fact]
    public async Task Erasure_collapse_and_the_sweep_destroy_the_data_key_and_the_ciphertext()
    {
        var clock = new FakeTimeProvider();
        var store = Store(clock: clock);
        var reference = await Commit(store, TenantA, "record-1");
        var committed = await store.KeyMaterialAsync(TenantA, reference.ContentId);
        Assert.False(await store.RemoveReferenceAsync(TenantA, reference with { Owner = new("record-9", "x") }, HoldStatus.None, "disposal-0"));
        Assert.True(await store.RemoveReferenceAsync(TenantA, reference, HoldStatus.None, "disposal-1"));
        Assert.All(committed.WrappedDataKey, value => Assert.Equal(0, value));
        Assert.Empty(committed.Segments);

        var kept = await Commit(store, TenantA, "record-2");
        var duplicate = await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null);
        var collapsed = store.KeyMaterial(duplicate);
        await store.CommitReferenceAsync(TenantA, duplicate, new ContentOwner("record-3", "0"), "x.txt");
        Assert.All(collapsed.WrappedDataKey, value => Assert.Equal(0, value));
        Assert.Empty(collapsed.Segments);
        Assert.Equal(Hello, await ReadBytes(await store.ReadAsync(TenantA, kept, true)));

        var abandoned = await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null);
        var swept = store.KeyMaterial(abandoned);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.SweepExpiredAsync("sweep-1");
        Assert.All(swept.WrappedDataKey, value => Assert.Equal(0, value));
        Assert.Empty(swept.Segments);
    }

    [Fact]
    public async Task Every_byte_below_0x20_is_classified_exactly_as_whatwg_7_1_binary_data_bytes()
    {
        var binary = new HashSet<int>(Enumerable.Range(0x00, 9).Concat([0x0B]).Concat(Enumerable.Range(0x0E, 13)).Concat(Enumerable.Range(0x1C, 4)));
        var store = Store();
        for (var value = 0; value <= 0x20; value++)
        {
            var bytes = new byte[] { (byte)'a', (byte)value, (byte)'b' };
            if (binary.Contains(value))
            {
                Assert.Equal(ContentRefusals.MediaTypeMismatch, (await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream(bytes), "text/plain", null))).Code);
            }
            else
            {
                await store.PutAsync(TenantA, new MemoryStream(bytes), "text/plain", null);
            }
        }

        await store.PutAsync(TenantA, new MemoryStream("RIFF\0\0\0\0WEBPVP8 "u8.ToArray()), "image/webp", null);
        await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream("RIFF\0\0\0\0WEBP"u8.ToArray()), "image/webp", null));
    }

    [Fact]
    public async Task A_short_tenant_key_a_malformed_content_id_and_a_missing_segment_are_refused()
    {
        var store = new InMemoryContentStore(new(100, 100, 100, TimeSpan.FromMinutes(1)), new ShortKeys(), new FakeTimeProvider(), CreateStorage());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null));

        foreach (var malformed in new[] { "sha256:" + new string('a', 64), "hmac-sha256:" + new string('a', 63), "hmac-sha256:" + new string('A', 64), "hmac-sha256:" + new string('g', 64), "hmac-sha256:" + new string('/', 64) })
        {
            Assert.Throws<ArgumentException>(() => new ContentId(malformed));
        }

        var valid = Store();
        var reference = await Commit(valid, TenantA, "record-1", "0123456789"u8.ToArray());
        Assert.Equal("5"u8.ToArray(), await ReadBytes(await valid.ReadAsync(TenantA, reference, true, new ContentByteRange(5, 5))));
        await valid.DropLastSegmentKeepingLengthAsync(TenantA, reference.ContentId);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await ReadBytes(await valid.ReadAsync(TenantA, reference, true)));
    }

    [Fact, Trait("DES-0054", "content-store-eng-10")]
    public async Task Clause_q27_q28_fixed_segments_and_purpose_bound_hkdf_subkeys_keep_content_ids_and_wraps_separate()
    {
        var keys = new TestKeys();
        var root = keys.GetKey(TenantA);
        var contentIdKey = IndependentlyDeriveHkdfSha256(root, TenantA, "harborline-content-store:content-id:v1");
        var dataKeyWrapKey = IndependentlyDeriveHkdfSha256(root, TenantA, "harborline-content-store:data-key-wrap:v1");
        Assert.NotEqual(contentIdKey, dataKeyWrapKey);

        var store = new InMemoryContentStore(new(300_000, 600_000, 600_000, TimeSpan.FromMinutes(1)), keys, new FakeTimeProvider(), CreateStorage());
        var bytes = LargeBytes(2);
        var reference = await Commit(store, TenantA, "record", bytes);
        var expectedContentId = new ContentId(ContentId.Algorithm + Convert.ToHexStringLower(HMACSHA256.HashData(contentIdKey, bytes)));
        Assert.Equal(expectedContentId, reference.ContentId);
        Assert.Equal(65_536, (await store.StorageItemAsync(TenantA, reference.ContentId)).SegmentSize);
        await store.RewrapWithContentIdSubkeyAsync(TenantA, reference.ContentId);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await store.ReadAsync(TenantA, reference, true));
    }

    [Fact, Trait("DES-0054", "content-store-eng-10")]
    public async Task Clause_q30_q34_q36_middle_segment_range_failure_is_typed_logged_once_counted_and_only_a_full_verified_read_ends_the_episode()
    {
        var clock = new FakeTimeProvider();
        var log = new CapturingLogger();
        var store = new InMemoryContentStore(new(300_000, 600_000, 600_000, TimeSpan.FromMinutes(1)), new TestKeys(), clock,
            CreateStorage(), log);
        var reference = await Commit(store, TenantA, "record", LargeBytes(2));
        await store.CorruptCiphertextAsync(TenantA, reference.ContentId, 1);
        var context = new ContentReadContext("trace-1", "operation-1", "tenant-opaque", "principal-1");

        var first = await Assert.ThrowsAsync<ContentIntegrityException>(async () => await store.ReadAsync(TenantA, reference, true, context, new ContentByteRange(65_536, 65_538)));
        Assert.Equal(1, first.SegmentIndex);
        Assert.Single(log.Events);
        var entry = Assert.Single(store.IntegrityEntries);
        Assert.True(entry.Active);
        Assert.Equal(1, entry.Count);
        Assert.False(store.IsIntegrityHealthy);

        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await store.ReadAsync(TenantA, reference, true, context, new ContentByteRange(65_536, 65_538)));
        entry = Assert.Single(store.IntegrityEntries);
        Assert.Equal(2, entry.Count);
        Assert.Single(log.Events);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), entry.LastSeenUtc);

        Assert.Equal(LargeBytes(2).AsSpan(0, 3).ToArray(), await ReadBytes(await store.ReadAsync(TenantA, reference, true, context, new ContentByteRange(0, 2))));
        Assert.True(Assert.Single(store.IntegrityEntries).Active);

        await store.CorruptCiphertextAsync(TenantA, reference.ContentId, 1); // restored backing bytes
        Assert.Equal(LargeBytes(2), await ReadBytes(await store.ReadAsync(TenantA, reference, true, context: context)));
        Assert.True(store.IsIntegrityHealthy);
        Assert.False(Assert.Single(store.IntegrityEntries).Active);

        await store.CorruptCiphertextAsync(TenantA, reference.ContentId, 1);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await store.ReadAsync(TenantA, reference, true, context, new ContentByteRange(65_536, 65_538)));
        Assert.Equal(2, log.Events.Count);
        Assert.Equal(1, Assert.Single(store.IntegrityEntries).Count);
    }

    [Fact, Trait("DES-0054", "content-store-eng-10")]
    public async Task Clause_q30_q36_every_failed_read_marks_an_error_span_with_error_type_and_increments_the_zero_alarm_counter()
    {
        var spans = new List<Activity>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Harborline.Foundation.ContentStore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(activities);
        long failures = 0;
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Harborline.Foundation.ContentStore") listener.EnableMeasurementEvents(instrument);
        };
        meter.SetMeasurementEventCallback<long>((_, value, _, _) => failures += value);
        meter.Start();

        var store = new InMemoryContentStore(new(300_000, 600_000, 600_000, TimeSpan.FromMinutes(1)), new TestKeys(), new FakeTimeProvider(), CreateStorage());
        var reference = await Commit(store, TenantA, "record", LargeBytes(2));
        await store.CorruptCiphertextAsync(TenantA, reference.ContentId, 1);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await store.ReadAsync(TenantA, reference, true, new ContentByteRange(65_536, 65_538)));
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await store.ReadAsync(TenantA, reference, true, new ContentByteRange(65_536, 65_538)));

        Assert.Equal(2, failures);
        Assert.Equal(2, spans.Count);
        Assert.All(spans, span =>
        {
            Assert.Equal(ActivityStatusCode.Error, span.Status);
            Assert.Equal("content.integrity", span.GetTagItem("error.type"));
        });
    }

    [Fact]
    public async Task Filename_star_keeps_every_rfc_8187_attr_char_boundary_and_a_trailing_percent_is_not_a_hex_pair()
    {
        var store = Store(itemLimit: 100);
        var reference = await store.CommitReferenceAsync(TenantA, await store.PutAsync(TenantA, new MemoryStream("%PDF-1.4\n"u8.ToArray()), "application/pdf", null), new("record", "n"), "0azAZ9é~.pdf");
        Assert.Equal("attachment; filename=\"0azAZ9_~.pdf\"; filename*=UTF-8''0azAZ9%C3%A9~.pdf", (await store.ReadAsync(TenantA, reference, true)).ContentDisposition);
        var percent = await store.CommitReferenceAsync(TenantA, await store.PutAsync(TenantA, new MemoryStream("%PDF-1.4\n"u8.ToArray()), "application/pdf", null), new("record", "p"), "ab%4");
        Assert.Equal("attachment; filename=\"ab%4\"", (await store.ReadAsync(TenantA, percent, true)).ContentDisposition);
    }

    private InMemoryContentStore LargeStore(FakeTimeProvider clock, CapturingLogger log) =>
        new(new(300_000, 600_000, 600_000, TimeSpan.FromMinutes(1)), new TestKeys(), clock, CreateStorage(), log);

    [Fact, Trait("DES-0054", "content-store-ck-18")]
    public async Task Clause_q34_erasing_the_last_reference_ends_its_integrity_episode()
    {
        var clock = new FakeTimeProvider();
        var log = new CapturingLogger();
        var store = LargeStore(clock, log);
        var reference = await Commit(store, TenantA, "record", LargeBytes(2));
        await store.CorruptCiphertextAsync(TenantA, reference.ContentId, 1);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await store.ReadAsync(TenantA, reference, true, new ContentByteRange(65_536, 65_538)));
        Assert.False(store.IsIntegrityHealthy);
        Assert.True(await store.RemoveReferenceAsync(TenantA, reference, HoldStatus.None, "run"));
        Assert.True(store.IsIntegrityHealthy);
        Assert.False(Assert.Single(store.IntegrityEntries).Active);

        // A different item's later corruption opens a new episode and logs again.
        var other = await Commit(store, TenantA, "record-2", "two"u8.ToArray());
        await store.CorruptCiphertextAsync(TenantA, other.ContentId, 0);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await store.ReadAsync(TenantA, other, true));
        Assert.False(store.IsIntegrityHealthy);
        Assert.Equal(2, log.Events.Count);

        await store.RemoveReferenceAsync(TenantA, other, HoldStatus.None, "run-2");
        Assert.True(store.IsIntegrityHealthy);
    }

    [Fact, Trait("DES-0054", "content-store-ck-18")]
    public async Task Clause_q34_a_read_that_races_erasure_records_no_episode()
    {
        var store = LargeStore(new FakeTimeProvider(), new CapturingLogger());
        var reference = await Commit(store, TenantA, "record", LargeBytes(2));
        var delivery = await store.ReadAsync(TenantA, reference, true);
        Assert.True(await store.RemoveReferenceAsync(TenantA, reference, HoldStatus.None, "run"));
        await Assert.ThrowsAsync<ContentIntegrityException>(() => ReadBytes(delivery));
        Assert.Empty(store.IntegrityEntries);
        Assert.True(store.IsIntegrityHealthy);
    }

    [Fact, Trait("DES-0054", "content-store-cc-7")]
    public async Task Clause_cc7_swapping_two_exported_content_ids_fails_fixity()
    {
        var store = Store();
        var a = await Commit(store, TenantA, "record-1", "one"u8.ToArray());
        var b = await Commit(store, TenantA, "record-2", "two"u8.ToArray());
        var export = await store.ExportAsync(TenantA, [a, b], authorized: true);
        var swapped = new ContentExport([export.Package.Items[0] with { ContentId = export.Package.Items[1].ContentId },
            export.Package.Items[1] with { ContentId = export.Package.Items[0].ContentId }]);
        Assert.Throws<InvalidDataException>(() => InMemoryContentStore.VerifyExport(swapped, export.FixityKey));
        InMemoryContentStore.VerifyExport(export.Package, export.FixityKey);
    }

    [Fact, Trait("DES-0054", "content-store-eng-6")]
    public async Task Clause_q30_unauthorized_range_is_indistinguishable_from_not_found_and_never_reveals_a_416_or_content_range()
    {
        var store = Store();
        var reference = await Commit(store, TenantA, "record");
        var missing = await Assert.ThrowsAsync<ContentNotFoundException>(async () => await store.ReadAsync(TenantA, reference with { ContentId = new("hmac-sha256:" + new string('b', 64)) }, true, new ContentByteRange(999, 1000)));
        var denied = await Assert.ThrowsAsync<ContentNotFoundException>(async () => await store.ReadAsync(TenantA, reference, false, new ContentByteRange(999, 1000)));
        var cross = await Assert.ThrowsAsync<ContentNotFoundException>(async () => await store.ReadAsync(TenantB, reference, true, new ContentByteRange(999, 1000)));
        Assert.Equal(missing.Message, denied.Message);
        Assert.Equal(missing.Message, cross.Message);
    }

    [Fact]
    public async Task Contract_arguments_and_field_boundaries_are_rejected_at_the_public_boundary()
    {
        var options = new ContentStoreOptions(5, 10, 10, TimeSpan.FromMinutes(1));
        var keys = new TestKeys();
        var clock = new FakeTimeProvider();
        var backing = CreateStorage();
        Assert.Throws<ArgumentNullException>(() => new InMemoryContentStore(null!, keys, clock, backing));
        Assert.Throws<ArgumentNullException>(() => new InMemoryContentStore(options, null!, clock, backing));
        Assert.Throws<ArgumentNullException>(() => new InMemoryContentStore(options, keys, null!, backing));
        Assert.Throws<ArgumentNullException>(() => new InMemoryContentStore(options, keys, clock, null!));

        var store = new InMemoryContentStore(options, keys, clock, backing);
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.PutAsync(TenantA, null!, "text/plain", null));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null, field: new("zero", 0, null)));
        var sameLimit = await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream("123456"u8.ToArray()), "text/plain", null, field: new("field", 5, null)));
        Assert.Equal(InMemoryContentStore.StoreDeclaration, sameLimit.Definition);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.CommitReferenceAsync(TenantA, Guid.NewGuid(), null!, "x.txt"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.CommitReferenceAsync(TenantA, Guid.NewGuid(), new("record", "field"), " "));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.ReadAsync(TenantA, null!, true));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.RemoveReferenceAsync(TenantA, null!, HoldStatus.None, "run"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.RemoveReferenceAsync(TenantA, new(new("hmac-sha256:" + new string('a', 64)), 0, "text/plain", "x", new("r", "e")), HoldStatus.None, " "));
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.SweepExpiredAsync(" "));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.ExportAsync(TenantA, null!, true));
        Assert.Throws<ArgumentNullException>(() => InMemoryContentStore.VerifyExport(null!, new byte[32]));
        Assert.Throws<ArgumentNullException>(() => InMemoryContentStore.VerifyExport(new ContentExport([]), null!));
    }

    [Fact]
    public async Task Contract_range_math_wrap_corruption_and_multiple_integrity_episodes_remain_observable()
    {
        var store = new InMemoryContentStore(new(300_000, 600_000, 600_000, TimeSpan.FromMinutes(1)), new TestKeys(), new FakeTimeProvider(), CreateStorage());
        var bytes = LargeBytes(2);
        var first = await Commit(store, TenantA, "first", bytes);
        var second = await Commit(store, TenantA, "second", bytes.Select(value => (byte)(value ^ 1)).ToArray());
        Assert.Equal(bytes.AsSpan(65_535, 3).ToArray(), await ReadBytes(await store.ReadAsync(TenantA, first, true, new ContentByteRange(65_535, 65_537))));

        await store.CorruptWrapTagAsync(TenantA, first.ContentId);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await store.ReadAsync(TenantA, first, true));
        await store.CorruptCiphertextAsync(TenantA, second.ContentId, 1);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await ReadBytes(await store.ReadAsync(TenantA, second, true)));
        await store.CorruptCiphertextAsync(TenantA, second.ContentId, 1);
        Assert.Equal(bytes.Select(value => (byte)(value ^ 1)).ToArray(), await ReadBytes(await store.ReadAsync(TenantA, second, true)));
        Assert.False(store.IsIntegrityHealthy);
    }

    [Fact]
    public async Task Contract_sniffing_and_filename_encoding_preserve_boundary_and_injection_semantics()
    {
        var store = Store(itemLimit: 100);
        var exactWebp = "RIFF\0\0\0\0WEBPVP"u8.ToArray();
        var webp = await store.CommitReferenceAsync(TenantA, await store.PutAsync(TenantA, new MemoryStream(exactWebp), "image/webp", null), new("record", "webp"), "a%ab.txt");
        var delivery = await store.ReadAsync(TenantA, webp, true);
        Assert.Equal("image/webp", delivery.ContentType);
        Assert.Equal("attachment; filename=\"a_ab.txt\"; filename*=UTF-8''a%25ab.txt", delivery.ContentDisposition);

        var falseWebp = "RIFF\0\0\0\0NOTWEB"u8.ToArray();
        Assert.Equal(ContentRefusals.MediaTypeNotAllowed,
            (await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream(falseWebp), null, null))).Code);
        var narrowed = new FieldNarrowing("field:octets", null, new HashSet<string> { "application/octet-stream" });
        var refusal = await Assert.ThrowsAsync<ContentRefusedException>(async () => await store.PutAsync(TenantA, new MemoryStream([0, 1, 2]), null, null, narrowed));
        Assert.Equal(InMemoryContentStore.StoreDeclaration, refusal.Definition);

        var ascii = await store.CommitReferenceAsync(TenantA, await store.PutAsync(TenantA, new MemoryStream(Hello), "text/plain", null), new("record", "ascii"), "AZaz09!#$&+-.^_`|~ space");
        Assert.Equal("attachment; filename=\"AZaz09!#$&+-.^_`|~ space\"", (await store.ReadAsync(TenantA, ascii, true)).ContentDisposition);
    }

    [Fact]
    public async Task Contract_delivery_stream_supports_only_reading_and_resolves_an_episode_after_the_final_byte()
    {
        var store = Store();
        var reference = await Commit(store, TenantA, "record");
        await store.CorruptCiphertextAsync(TenantA, reference.ContentId, 0);
        await Assert.ThrowsAsync<ContentIntegrityException>(async () => await store.ReadAsync(TenantA, reference, true));
        await store.CorruptCiphertextAsync(TenantA, reference.ContentId, 0);

        using var body = (await store.ReadAsync(TenantA, reference, true)).Body;
        Assert.True(body.CanRead);
        Assert.False(body.CanSeek);
        Assert.False(body.CanWrite);
        Assert.Throws<NotSupportedException>(() => body.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => body.SetLength(0));
        Assert.Throws<NotSupportedException>(() => body.Write([], 0, 0));
        var buffer = new byte[Hello.Length];
        Assert.Equal(Hello.Length, await body.ReadAsync(buffer.AsMemory()));
        Assert.Equal(Hello, buffer);
        Assert.Equal(0, await body.ReadAsync(buffer.AsMemory()));
        Assert.True(store.IsIntegrityHealthy);
    }

    private static async Task<ContentReference> Commit(InMemoryContentStore store, TenantId tenant, string record, byte[]? bytes = null) =>
        await store.CommitReferenceAsync(tenant, await store.PutAsync(tenant, new MemoryStream(bytes ?? Hello), "text/plain", null), new ContentOwner(record, "files/0"), "x.txt");

    private InMemoryContentStore Store(long itemLimit = 100, FakeTimeProvider? clock = null) =>
        new(new(itemLimit, 1000, 1000, TimeSpan.FromMinutes(1)), new TestKeys(), clock ?? new FakeTimeProvider(), CreateStorage());

    private static async Task<byte[]> ReadBytes(ContentDelivery delivery)
    {
        using var body = delivery.Body;
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static byte[] LargeBytes(int segments) => Enumerable.Repeat((byte)'a', (65_536 * segments) + 1).ToArray();

    private static byte[] IndependentlyDeriveHkdfSha256(byte[] root, TenantId tenant, string info)
    {
        var output = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, root, output, Encoding.UTF8.GetBytes(tenant.Value), Encoding.ASCII.GetBytes(info));
        return output;
    }


    private sealed class TestKeys : ITenantContentKeyProvider
    {
        public byte[] GetKey(TenantId tenantId) => SHA256.HashData(Encoding.UTF8.GetBytes("test-key:" + tenantId.Value));
    }

    private sealed class ShortKeys : ITenantContentKeyProvider
    {
        public byte[] GetKey(TenantId tenantId) => new byte[16];
    }

    private sealed class CapturingLogger : IContentIntegrityLogger
    {
        public List<ContentIntegrityLogEvent> Events { get; } = [];
        public void LogIntegrityFailure(ContentIntegrityLogEvent integrityEvent) => Events.Add(integrityEvent);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}
