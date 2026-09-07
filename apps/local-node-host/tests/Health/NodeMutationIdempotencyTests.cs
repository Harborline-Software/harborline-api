using Xunit;

using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Health;

public sealed class NodeMutationIdempotencyTests
{
    [Fact(DisplayName = "idempotency store: expired response is evicted and the key becomes executable")]
    public void ExpiredResponse_IsEvicted()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        using var store = new NodeMutationIdempotency.Store(clock);
        var response = Response();

        store.Record("expired-key", "body-a", response);
        clock.Advance(TimeSpan.FromHours(1));
        store.Record("live-key", "body-live", response);
        // Touch the entry that will expire so it sits behind the live entry in the access-ordered LRU.
        Assert.Equal(
            NodeMutationIdempotency.LookupResult.Replay,
            store.TryGet("expired-key", "body-a", out _));

        clock.Advance(NodeMutationIdempotency.Store.DefaultTtl - TimeSpan.FromHours(1) + TimeSpan.FromMinutes(30));

        Assert.Equal(
            NodeMutationIdempotency.LookupResult.Miss,
            store.TryGet("expired-key", "body-a", out _));
        Assert.Equal(
            NodeMutationIdempotency.LookupResult.Replay,
            store.TryGet("live-key", "body-live", out _));
        store.Record("expired-key", "body-b", response);
        Assert.Equal(
            NodeMutationIdempotency.LookupResult.Replay,
            store.TryGet("expired-key", "body-b", out _));
    }

    [Fact(DisplayName = "idempotency store: cap evicts least-recently-used response")]
    public void Cap_EvictsLeastRecentlyUsedResponse()
    {
        using var store = new NodeMutationIdempotency.Store(TimeProvider.System, maxEntries: 2);
        store.Record("a", "body-a", Response());
        store.Record("b", "body-b", Response());

        Assert.Equal(NodeMutationIdempotency.LookupResult.Replay, store.TryGet("a", "body-a", out _));
        store.Record("c", "body-c", Response());

        Assert.Equal(NodeMutationIdempotency.LookupResult.Replay, store.TryGet("a", "body-a", out _));
        Assert.Equal(NodeMutationIdempotency.LookupResult.Miss, store.TryGet("b", "body-b", out _));
        Assert.Equal(NodeMutationIdempotency.LookupResult.Replay, store.TryGet("c", "body-c", out _));
        Assert.Equal(2, store.ResponseCount);
    }

    private static NodeMutationIdempotency.ResponseCapture Response() =>
        new(
            StatusCode: 201,
            ContentType: "application/json",
            Headers: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase),
            Body: "{}"u8.ToArray());

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
