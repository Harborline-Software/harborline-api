using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.UpdateFeed;
using Harborline.Api.Foundation.UpdateFeed.Verify;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Feed;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Feed;

/// <summary>
/// Update-feed U2 — the node CHANNEL CLIENT adversarial matrix, from the CONSUMING side (mirrors U1's
/// verifier cases end-to-end over the real HTTP fetcher + the durable sequence store): a clean online feed
/// verifies + stages + advances the F2 high-water; tamper / rollback-below-floor / wrong-root / freeze on an
/// online channel all fail-closed with NOTHING staged and the high-water UNMOVED; a sideload past validUntil
/// is expected-stale (warn, not lock-out); an un-pinned channel needs the trust ceremony (F6); and the
/// high-water is per-(tenant, channel) — channel + tenant isolated — and SURVIVES a restart.
/// </summary>
public sealed class ChannelFeedClientTests
{
    private static readonly TenantId TenantA = TenantId.FromString("tenant-A");
    private static readonly TenantId TenantB = TenantId.FromString("tenant-B");
    private const string ChannelId = "test-online";
    private static readonly DateTimeOffset Fresh = FeedTestBuilder.Base.AddHours(1);

    private static IHttpClientFactory NewFactory()
        => new ServiceCollection().AddHttpClient().BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

    private static BinaryPinnedChannelRegistry OnlineRegistry(
        string feedBase, FeedPinnedRoot root, long floor = 1, string channelId = ChannelId)
        => new(
            new[] { new NodeChannel(channelId, "Test online", feedBase, ChannelKind.Online, FeedKeyId.Format(root.KeyId), true) },
            new[] { new ChannelRootPin(channelId, root, floor, "dogfood-dev") });

    private static ChannelFeedClient Client(
        IChannelRegistry registry, IChannelSequenceStore store, IHttpClientFactory factory, DateTimeOffset now)
        => new(registry, new HttpFeedFetcher(factory), store, new FixedTimeProvider(now));

    // ── happy path (online): verify → stage → advance high-water ───────────────────────────────────
    [Fact(DisplayName = "U2: a clean online feed verifies, stages the pack, and advances the durable F2 high-water")]
    public async Task Online_verified_stages_and_advances_highwater()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 5);
        await using var server = await FeedTestServer.StartAsync(feed.Files);
        await using var db = await PacksTestStore.CreateAsync();
        var store = new DurableChannelSequenceStore(db.Factory);
        var client = Client(OnlineRegistry(server.FeedBaseUrl, feed.Root), store, server.HttpClientFactory, Fresh);

        var result = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);

        Assert.Equal(ChannelCheckStatus.Verified, result.Status);
        Assert.True(result.Ok);
        var pack = Assert.Single(result.StagedPacks);
        Assert.Equal(FeedTestBuilder.PackKey, pack.PackKey);
        Assert.Equal("0.1.0", pack.Latest);
        Assert.False(pack.ArtifactBytes.IsEmpty);
        Assert.Equal(5, store.GetHighWater(TenantA, ChannelId)); // advanced only on success
    }

    // ── ROLLBACK: a sequence below the persisted high-water is refused, nothing staged, fence UNMOVED ──
    [Fact(DisplayName = "U2: an online feed whose sequence is below the persisted high-water is refused (rollback) — nothing staged, high-water unmoved")]
    public async Task Rollback_below_persisted_highwater_is_refused()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 3);
        await using var server = await FeedTestServer.StartAsync(feed.Files);
        await using var db = await PacksTestStore.CreateAsync();
        var store = new DurableChannelSequenceStore(db.Factory);
        store.AdvanceHighWater(TenantA, ChannelId, 5); // node has already seen a newer index

        var client = Client(OnlineRegistry(server.FeedBaseUrl, feed.Root), store, server.HttpClientFactory, Fresh);
        var result = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);

        Assert.Equal(ChannelCheckStatus.VerificationFailed, result.Status);
        Assert.Contains(result.Failures, f => f.Code == FeedVerifyCodes.SequenceRollback);
        Assert.Empty(result.StagedPacks);
        Assert.Equal(5, store.GetHighWater(TenantA, ChannelId)); // fence NOT lowered by the rollback attempt
    }

    // ── F2 build-time first-contact floor: a fresh node refuses a below-baseline sequence ──────────────
    [Fact(DisplayName = "U2: a fresh node (no persisted high-water) refuses a sequence below the build-time floor pinned beside the root (F2 first-contact)")]
    public async Task Buildtime_floor_refuses_below_baseline_on_fresh_node()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 3);
        await using var server = await FeedTestServer.StartAsync(feed.Files);
        await using var db = await PacksTestStore.CreateAsync();
        var store = new DurableChannelSequenceStore(db.Factory);

        // Pin a build-time floor of 10; the fresh node has high-water 0 but must still refuse seq 3.
        var client = Client(OnlineRegistry(server.FeedBaseUrl, feed.Root, floor: 10), store, server.HttpClientFactory, Fresh);
        var result = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);

        Assert.Equal(ChannelCheckStatus.VerificationFailed, result.Status);
        Assert.Contains(result.Failures, f => f.Code == FeedVerifyCodes.SequenceRollback);
        Assert.Empty(result.StagedPacks);
    }

    // ── TAMPER: a flipped artifact byte fails CID/pack verify — nothing staged, fence UNMOVED ──────────
    [Fact(DisplayName = "U2: a tampered artifact byte fails verification — nothing staged, high-water not advanced")]
    public async Task Tamper_artifact_byte_is_refused()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 5);
        var artifactPath = FeedTestBuilder.ArtifactPath(feed.Files);
        feed.Files[artifactPath][^1] ^= 0xFF; // flip one byte of the content-addressed blob

        await using var server = await FeedTestServer.StartAsync(feed.Files);
        await using var db = await PacksTestStore.CreateAsync();
        var store = new DurableChannelSequenceStore(db.Factory);
        var client = Client(OnlineRegistry(server.FeedBaseUrl, feed.Root), store, server.HttpClientFactory, Fresh);

        var result = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);

        Assert.Equal(ChannelCheckStatus.VerificationFailed, result.Status);
        Assert.Contains(result.Failures, f => f.Code == FeedVerifyCodes.ArtifactCidMismatch);
        Assert.Empty(result.StagedPacks);
        Assert.Equal(0, store.GetHighWater(TenantA, ChannelId)); // a tampered feed never advances the fence
    }

    // ── WRONG ROOT: a feed not signed by the pinned root is refused ───────────────────────────────────
    [Fact(DisplayName = "U2: a feed signed by a different key than the pinned root is refused (root mismatch)")]
    public async Task Wrong_pinned_root_is_refused()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 5);
        await using var server = await FeedTestServer.StartAsync(feed.Files);
        await using var db = await PacksTestStore.CreateAsync();
        var store = new DurableChannelSequenceStore(db.Factory);

        // Pin the WRONG root — the feed's channel-root signature will not verify against it.
        var client = Client(OnlineRegistry(server.FeedBaseUrl, FeedTestBuilder.WrongRoot()), store, server.HttpClientFactory, Fresh);
        var result = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);

        Assert.Equal(ChannelCheckStatus.VerificationFailed, result.Status);
        Assert.Contains(result.Failures, f => f.Code == FeedVerifyCodes.ChannelRootMismatch);
        Assert.Empty(result.StagedPacks);
    }

    // ── FREEZE / EXPIRY (online): past the signed validUntil an ONLINE channel is fail-closed ─────────
    [Fact(DisplayName = "U2: an online channel past its signed validUntil is fail-closed (freeze) — nothing staged")]
    public async Task Freeze_expired_validUntil_online_is_failclosed()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 5, validUntil: FeedTestBuilder.Base.AddDays(1));
        await using var server = await FeedTestServer.StartAsync(feed.Files);
        await using var db = await PacksTestStore.CreateAsync();
        var store = new DurableChannelSequenceStore(db.Factory);

        var past = FeedTestBuilder.Base.AddDays(2); // now is past validUntil
        var client = Client(OnlineRegistry(server.FeedBaseUrl, feed.Root), store, server.HttpClientFactory, past);
        var result = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);

        Assert.Equal(ChannelCheckStatus.VerificationFailed, result.Status);
        Assert.Contains(result.Failures, f => f.Code == FeedVerifyCodes.ChannelExpired);
        Assert.Empty(result.StagedPacks);
    }

    // ── SIDELOAD past validUntil: expected-stale — WARNS but still verifies (no air-gapped lockout) ───
    [Fact(DisplayName = "U2: a sideload channel past its validUntil is expected-stale — it warns but still verifies + stages (no air-gapped lockout)")]
    public async Task Sideload_expired_validUntil_warns_but_verifies()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 5, validUntil: FeedTestBuilder.Base.AddDays(1));
        var dir = WriteToTempDir(feed.Files);
        try
        {
            await using var db = await PacksTestStore.CreateAsync();
            var store = new DurableChannelSequenceStore(db.Factory);
            var registry = new BinaryPinnedChannelRegistry(
                new[] { new NodeChannel(ChannelId, "USB", dir, ChannelKind.Sideload, FeedKeyId.Format(feed.Root.KeyId), true) },
                new[] { new ChannelRootPin(ChannelId, feed.Root, 1, "dogfood-dev") });

            var past = FeedTestBuilder.Base.AddDays(2);
            var client = Client(registry, store, NewFactory(), past);
            var result = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);

            Assert.Equal(ChannelCheckStatus.Verified, result.Status);
            Assert.Single(result.StagedPacks);
            Assert.Contains(result.Warnings, w => w.Code == FeedVerifyCodes.ChannelExpiredSideloadWarning);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── SIDELOAD happy path: a local feed tree verifies through the SAME path ─────────────────────────
    [Fact(DisplayName = "U2: a sideload channel (local feed tree) verifies + stages through the same verify path")]
    public async Task Sideload_happy_path_verifies()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 5);
        var dir = WriteToTempDir(feed.Files);
        try
        {
            await using var db = await PacksTestStore.CreateAsync();
            var store = new DurableChannelSequenceStore(db.Factory);
            var registry = new BinaryPinnedChannelRegistry(
                new[] { new NodeChannel(ChannelId, "USB", dir, ChannelKind.Sideload, FeedKeyId.Format(feed.Root.KeyId), true) },
                new[] { new ChannelRootPin(ChannelId, feed.Root, 1, "dogfood-dev") });
            var client = Client(registry, store, NewFactory(), Fresh);

            var result = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);

            Assert.Equal(ChannelCheckStatus.Verified, result.Status);
            Assert.Single(result.StagedPacks);
            Assert.Equal(5, store.GetHighWater(TenantA, ChannelId));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── F6: a channel row with no local pin cannot acquire — needs the add-root ceremony ──────────────
    [Fact(DisplayName = "U2/F6: a channel present in the table but NOT locally pinned is a proposed channel — it acquires nothing (needs the add-root ceremony)")]
    public async Task Unpinned_channel_needs_trust_ceremony()
    {
        await using var db = await PacksTestStore.CreateAsync();
        var store = new DurableChannelSequenceStore(db.Factory);
        // A channel row with NO matching pin (config synced, trust never conveyed).
        var registry = new BinaryPinnedChannelRegistry(
            new[] { new NodeChannel(ChannelId, "Proposed", "http://127.0.0.1:9/feed/", ChannelKind.Online, "ed25519:whatever", true) },
            Array.Empty<ChannelRootPin>());
        var client = Client(registry, store, NewFactory(), Fresh);

        var result = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);

        Assert.Equal(ChannelCheckStatus.NeedsTrustCeremony, result.Status);
        Assert.Empty(result.StagedPacks);
        Assert.Contains(result.Failures, f => f.Code == ChannelFeedCodes.NeedsTrustCeremony);
    }

    // ── CHANNEL ISOLATION: one channel's high-water never gates another's ─────────────────────────────
    [Fact(DisplayName = "U2: the F2 high-water is per-channel — a high-water on channel A does not gate channel B")]
    public async Task Channel_isolation_highwater_independent()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 3);
        await using var server = await FeedTestServer.StartAsync(feed.Files);
        await using var db = await PacksTestStore.CreateAsync();
        var store = new DurableChannelSequenceStore(db.Factory);
        store.AdvanceHighWater(TenantA, "channel-A", 9); // A is far ahead

        // Channel B (its own high-water is 0, floor 1) verifies a seq-3 feed regardless of A's 9.
        var registry = OnlineRegistry(server.FeedBaseUrl, feed.Root, floor: 1, channelId: "channel-B");
        var client = Client(registry, store, server.HttpClientFactory, Fresh);
        var result = await client.CheckAsync(TenantA, "channel-B", CancellationToken.None);

        Assert.Equal(ChannelCheckStatus.Verified, result.Status);
        Assert.Equal(3, store.GetHighWater(TenantA, "channel-B"));
        Assert.Equal(9, store.GetHighWater(TenantA, "channel-A")); // untouched
    }

    // ── CROSS-TENANT ISOLATION: one tenant's high-water never gates another's ─────────────────────────
    [Fact(DisplayName = "U2: the F2 high-water is per-tenant — tenant A's high-water does not gate tenant B on the same channel")]
    public async Task Cross_tenant_highwater_independent()
    {
        var feed = await FeedTestBuilder.BuildAsync(sequence: 3);
        await using var server = await FeedTestServer.StartAsync(feed.Files);
        await using var db = await PacksTestStore.CreateAsync();
        var store = new DurableChannelSequenceStore(db.Factory);
        store.AdvanceHighWater(TenantA, ChannelId, 9); // tenant A is far ahead on this channel

        var client = Client(OnlineRegistry(server.FeedBaseUrl, feed.Root), store, server.HttpClientFactory, Fresh);
        var result = await client.CheckAsync(TenantB, ChannelId, CancellationToken.None); // tenant B, high-water 0

        Assert.Equal(ChannelCheckStatus.Verified, result.Status);
        Assert.Equal(3, store.GetHighWater(TenantB, ChannelId));
        Assert.Equal(9, store.GetHighWater(TenantA, ChannelId)); // A untouched
    }

    // ── PERSISTENCE: the F2 high-water SURVIVES a node restart (durable, not in-memory) ───────────────
    [Fact(DisplayName = "U2/F2: the anti-rollback high-water SURVIVES a node restart — a rolled-back index is still refused after a recycle")]
    public async Task Highwater_survives_restart()
    {
        var newer = await FeedTestBuilder.BuildAsync(sequence: 5);
        await using var db = await PacksTestStore.CreateAsync();

        // BEFORE restart: verify seq 5 (advances high-water to 5).
        await using (var server = await FeedTestServer.StartAsync(newer.Files))
        {
            var store = new DurableChannelSequenceStore(db.Factory);
            var client = Client(OnlineRegistry(server.FeedBaseUrl, newer.Root), store, server.HttpClientFactory, Fresh);
            var ok = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);
            Assert.Equal(ChannelCheckStatus.Verified, ok.Status);
        }

        // Simulate a restart: a fresh store object graph over the SAME encrypted file.
        await using var afterRestart = PacksTestStore.Reopen(db);
        var older = await FeedTestBuilder.BuildAsync(sequence: 3);
        await using (var server = await FeedTestServer.StartAsync(older.Files))
        {
            var store = new DurableChannelSequenceStore(afterRestart.Factory);
            Assert.Equal(5, store.GetHighWater(TenantA, ChannelId)); // persisted across the recycle
            var client = Client(OnlineRegistry(server.FeedBaseUrl, older.Root), store, server.HttpClientFactory, Fresh);
            var rolled = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);

            Assert.Equal(ChannelCheckStatus.VerificationFailed, rolled.Status);
            Assert.Contains(rolled.Failures, f => f.Code == FeedVerifyCodes.SequenceRollback);
        }
    }

    // ── UNREACHABLE: an online channel whose feed cannot be fetched is a transport failure ────────────
    [Fact(DisplayName = "U2: an online channel whose feed is unreachable reports ChannelUnreachable (transport), not a verification failure")]
    public async Task Unreachable_channel_is_transport_failure()
    {
        await using var db = await PacksTestStore.CreateAsync();
        var store = new DurableChannelSequenceStore(db.Factory);
        // A pinned channel pointing at a dead port.
        var registry = OnlineRegistry("http://127.0.0.1:1/feed/", FeedTestBuilder.WrongRoot());
        var client = Client(registry, store, NewFactory(), Fresh);

        var result = await client.CheckAsync(TenantA, ChannelId, CancellationToken.None);

        Assert.Equal(ChannelCheckStatus.ChannelUnreachable, result.Status);
        Assert.Empty(result.StagedPacks);
    }

    private static string WriteToTempDir(IReadOnlyDictionary<string, byte[]> files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "u2-sideload-" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, bytes) in files)
        {
            var full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
        }
        return dir;
    }
}
