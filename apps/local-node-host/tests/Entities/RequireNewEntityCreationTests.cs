using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class RequireNewEntityCreationTests
{
    private static readonly SchemaId Schema = new("require-new-schema");
    private static CreateOptions Options(string id, bool requireNew = false) => new(
        "test", "require-new", id, new ActorId("author"), new TenantId("tenant"),
        DateTimeOffset.UnixEpoch, id, RequireNew: requireNew);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_create_preserves_default_adoption_but_require_new_refuses_equal_body(bool requireNew)
    {
        using var storage = new InMemoryAssetStorage();
        var store = new InMemoryEntityStore(storage, TimeProvider.System);
        using var body = JsonDocument.Parse("{\"value\":1}");
        var id = await store.CreateAsync(Schema, body, Options("existing"));
        var before = await store.GetAsync(id);
        if (requireNew)
            await Assert.ThrowsAsync<IdempotencyConflictException>(() => store.CreateAsync(Schema, body, Options("existing", true)));
        else
            Assert.Equal(id, await store.CreateAsync(Schema, body, Options("existing")));
        Assert.Equal(before!.CurrentVersion, (await store.GetAsync(id))!.CurrentVersion);
        Assert.Single(storage.Entities);
        Assert.Single(storage.Versions[id]);
        // Refusal releases the original lock; a different new entity can still be claimed.
        await store.CreateAsync(Schema, body, Options("next", true));
        Assert.Equal(2, storage.Entities.Count);
    }

    [Fact]
    public async Task Canceled_required_insert_leaves_no_claim_and_retry_succeeds()
    {
        using var storage = new InMemoryAssetStorage();
        var store = new InMemoryEntityStore(storage, TimeProvider.System);
        using var body = JsonDocument.Parse("{\"value\":1}");
        var options = Options("canceled", true);
        var canceled = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateAsync(Schema, body, options, canceled));
        Assert.Empty(storage.Entities);
        Assert.Empty(storage.Versions);
        await store.CreateAsync(Schema, body, options);
        Assert.Single(storage.Entities);
        Assert.Single(storage.Versions);
    }
}
