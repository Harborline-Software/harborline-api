using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Harborline.Api.LocalNodeHost.Data.Scheduling;
using Harborline.Api.LocalNodeHost.Tests.TestDoubles;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Scheduling;

public sealed class NodeSchedulingDraftStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"scheduling-{Guid.NewGuid():N}.db");
    private IDbContextFactory<NodeLocalSchedulingDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<NodeLocalSchedulingDbContext>()
            .UseSqlite(
                SqliteTestDatabase.ConnectionString(_path),
                sqlite => sqlite.MigrationsHistoryTable(
                    NodeLocalSchedulingDbContext.MigrationsHistoryTableName))
            .Options;
        _factory = new TestFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        SqliteTestDatabase.Delete(_path);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Production_migration_creates_draft_and_audit_tables()
    {
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Contains(
            "20260712120000_AddSchedulingDefinitionDrafts",
            await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Save_survives_new_store_and_records_server_actor_audit()
    {
        var first = Store();
        var saved = await first.SaveAsync("tenant-a", "protocol-a", Json("""{"title":"Draft"}"""), 0, "actor-a", default);
        Assert.Equal(1, saved.Revision);

        var restarted = Store();
        var loaded = await restarted.GetAsync("tenant-a", "protocol-a", default);
        Assert.NotNull(loaded);
        Assert.Equal("Draft", loaded.Definition.GetProperty("title").GetString());
        await using var db = await _factory.CreateDbContextAsync();
        var audit = Assert.Single(await db.DraftAudit.ToListAsync());
        Assert.Equal("actor-a", audit.ActorId);
        Assert.Equal(1, audit.Revision);
    }

    [Fact]
    public async Task Expected_revision_conflict_writes_neither_revision_nor_audit()
    {
        var store = Store();
        await store.SaveAsync("tenant-a", "p", Json("{}"), 0, "actor", default);
        var conflict = await Assert.ThrowsAsync<SchedulingDraftConflictException>(() =>
            store.SaveAsync("tenant-a", "p", Json("{}"), 0, "actor", default));
        Assert.Equal(1, conflict.CurrentRevision);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Drafts.CountAsync());
        Assert.Equal(1, await db.DraftAudit.CountAsync());
    }

    [Fact]
    public async Task Invalid_by_validator_draft_can_save_and_validation_never_writes()
    {
        var invalid = Json("{}");
        Assert.NotEmpty(new SchedulingDraftValidator().Validate(invalid));
        var saved = await Store().SaveAsync("tenant-a", "p", invalid, 0, "actor", default);
        Assert.Equal(1, saved.Revision);
        _ = new SchedulingDraftValidator().Validate(Json("{}"));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.Drafts.CountAsync());
        Assert.Equal(1, await db.DraftAudit.CountAsync());
    }

    [Fact]
    public async Task Same_id_is_isolated_by_tenant_for_get_and_list()
    {
        var store = Store();
        await store.SaveAsync("tenant-a", "p", Json("""{"title":"A"}"""), 0, "a", default);
        await store.SaveAsync("tenant-b", "p", Json("""{"title":"B"}"""), 0, "b", default);
        Assert.Equal("A", (await store.GetAsync("tenant-a", "p", default))!.Definition.GetProperty("title").GetString());
        Assert.Equal("B", (await store.GetAsync("tenant-b", "p", default))!.Definition.GetProperty("title").GetString());
        Assert.Single(await store.ListAsync("tenant-a", default));
    }

    private NodeSchedulingDraftStore Store() => new(_factory, TimeProvider.System);
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private sealed class TestFactory(DbContextOptions<NodeLocalSchedulingDbContext> options)
        : IDbContextFactory<NodeLocalSchedulingDbContext>
    {
        public NodeLocalSchedulingDbContext CreateDbContext() => new(options);
    }
}
