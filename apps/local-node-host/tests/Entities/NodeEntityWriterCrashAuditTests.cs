using System.Text.Json;

using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Tests.Audit;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T-1048 (DES-0029 ck-6): every <see cref="NodeEntityWriter"/> write commits its accepted-act audit with the
/// change, so a crash after the commit and before react loses nothing. The crash is the process stopping on
/// entry to react; recovery is a new harness (new outbox, trail and reader) over the same encrypted
/// <c>local-node.db</c>, whose drain delivers the owed entry exactly once.
/// </summary>
public sealed class NodeEntityWriterCrashAuditTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("tenant-t1048f");
    private static readonly ActorId Actor = new("t1048f-operator");
    private static readonly SchemaId Schema = new("records.t1048f");
    private static readonly AuthorizationWriteContext Authority = new(Actor, Tenant, At);

    private DurableAuditHarness _audit = null!;
    private InMemoryEntityStore _entities = null!;

    public async Task InitializeAsync()
    {
        _audit = await DurableAuditHarness.CreateAsync();
        _entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        await using var db = LocalNode(_audit);
        await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
    }

    public async Task DisposeAsync() => await _audit.DisposeAsync();

    [Fact(DisplayName = "T-1048: a legal-entity create that crashes after its commit delivers its audit once after a restart")]
    public async Task LegalEntityCreate_CrashAfterCommit_DeliversItsAuditOnceAfterRestart()
    {
        var id = new LegalEntityId("t1048f-legal");

        await Assert.ThrowsAsync<ProcessStopped>(async () => await Writer(new CrashBeforeReact()).CreateLegalEntityAsync(
            new CreateLegalEntityCommand(id, "Crash LLC", "Llc", "DisregardedEntity", null), Authority));

        await using var restarted = _audit.Reopen();
        await using (var db = LocalNode(restarted))
            Assert.Equal("Crash LLC", (await db.Set<LegalEntity>().SingleAsync(row => row.Id == id)).LegalName);
        await AssertDeliveredOnceAsync(restarted, NodeEntityWriter.RecordWrittenEventType, "t1048f-legal", "legal-entity");
    }

    [Fact(DisplayName = "T-1048: a record create that crashes after its commit delivers its audit once after a restart")]
    public async Task RecordCreate_CrashAfterCommit_DeliversItsAuditOnceAfterRestart()
    {
        using var body = JsonDocument.Parse("""{"name":"created"}""");

        await Assert.ThrowsAsync<ProcessStopped>(async () =>
            await Writer(new CrashBeforeReact()).CreateAsync(Schema, body, Options("t1048f-created"), Authority));

        await using var restarted = _audit.Reopen();
        await AssertDeliveredOnceAsync(restarted, NodeEntityWriter.RecordWrittenEventType, "t1048f-created", Schema.Value);
    }

    [Fact(DisplayName = "T-1048: a record update that crashes after its commit delivers its audit once after a restart")]
    public async Task RecordUpdate_CrashAfterCommit_DeliversItsAuditOnceAfterRestart()
    {
        var id = await SeedAsync("t1048f-updated");
        using var body = JsonDocument.Parse("""{"name":"after"}""");

        await Assert.ThrowsAsync<ProcessStopped>(async () =>
            await Writer(new CrashBeforeReact()).UpdateAsync(id, body, new UpdateOptions(Actor), Authority));

        await using var restarted = _audit.Reopen();
        await AssertDeliveredOnceAsync(restarted, NodeEntityWriter.RecordWrittenEventType, "t1048f-updated", Schema.Value);
    }

    [Fact(DisplayName = "T-1048: a record delete that crashes after its commit delivers its audit once after a restart")]
    public async Task RecordDelete_CrashAfterCommit_DeliversItsAuditOnceAfterRestart()
    {
        var id = await SeedAsync("t1048f-deleted");

        await Assert.ThrowsAsync<ProcessStopped>(async () =>
            await Writer(new CrashBeforeReact()).DeleteAsync(id, new DeleteOptions(Actor, Justification: "gone"), Authority));

        await using var restarted = _audit.Reopen();
        await AssertDeliveredOnceAsync(restarted, NodeEntityWriter.RecordDeletedEventType, "t1048f-deleted", Schema.Value);
    }

    [Fact(DisplayName = "T-1048: an uninterrupted write returns the id of the audit it committed, already delivered")]
    public async Task Create_ReturnsTheCommittedAuditId_Delivered()
    {
        using var body = JsonDocument.Parse("""{"name":"live"}""");

        var written = await Writer().CreateWithReceiptAsync(Schema, body, Options("t1048f-live"), Authority);

        var record = Assert.Single(await _audit.DeliveredAsync(Tenant, NodeEntityWriter.RecordWrittenEventType));
        Assert.Equal(written.AuditId, record.AuditId);
        Assert.Equal(Actor, record.Actor);
        Assert.Empty(await OwedAsync(_audit));
    }

    [Fact(DisplayName = "T-1048: a legal-entity create refused at commit stages no audit")]
    public async Task LegalEntityCreate_RefusedAtCommit_StagesNoAudit()
    {
        var command = new CreateLegalEntityCommand(new LegalEntityId("t1048f-dup"), "Dup LLC", "Llc", "DisregardedEntity", null);
        await Writer().CreateLegalEntityAsync(command, Authority);

        // The second insert of the same id fails the save, and the audit staged on it rolls back with it.
        await Assert.ThrowsAnyAsync<DbUpdateException>(async () => await Writer().CreateLegalEntityAsync(command, Authority));

        Assert.Equal(1, await StagedCountAsync(_audit));
    }

    [Fact(DisplayName = "T-1048: a record create the store refuses at commit stages no audit")]
    public async Task RecordCreate_RefusedAtCommit_StagesNoAudit()
    {
        await SeedAsync("t1048f-existing");
        using var body = JsonDocument.Parse("""{"name":"again"}""");
        var stages = new CrashBeforeReact(crash: false);

        await Assert.ThrowsAsync<IdempotencyConflictException>(async () => await Writer(stages).CreateAsync(
            Schema, body, Options("t1048f-existing") with { RequireNew = true }, Authority));

        Assert.Equal(WritePipelineStage.Commit, stages.Last);
        Assert.Equal(0, await StagedCountAsync(_audit));
    }

    [Fact(DisplayName = "T-1048: a record update the store refuses at commit stages no audit")]
    public async Task RecordUpdate_RefusedAtCommit_StagesNoAudit()
    {
        var id = await SeedAsync("t1048f-stale");
        var stale = (await _entities.GetAsync(id))!.CurrentVersion;
        using (var advance = JsonDocument.Parse("""{"name":"advanced"}"""))
            await ((IEntityMutationStore)_entities).UpdateAsync(id, advance, new UpdateOptions(Actor, ValidFrom: At.AddHours(-1)));
        using var body = JsonDocument.Parse("""{"name":"after"}""");
        var stages = new CrashBeforeReact(crash: false);

        await Assert.ThrowsAsync<ConcurrencyException>(async () => await Writer(stages).UpdateAsync(
            id, body, new UpdateOptions(Actor, ExpectedVersion: stale), Authority));

        Assert.Equal(WritePipelineStage.Commit, stages.Last);
        Assert.Equal(0, await StagedCountAsync(_audit));
    }

    private NodeEntityWriter Writer(IWritePipelineObserver? observer = null) => new(
        new LocalNodeFactory(_audit),
        _entities,
        NullEntityValidator.Instance,
        TestAuthorization.AllowGate(),
        pipelineObserver: observer,
        outbox: _audit.Outbox);

    private async Task<EntityId> SeedAsync(string localPart)
    {
        using var body = JsonDocument.Parse("""{"name":"before"}""");
        return await ((IEntityMutationStore)_entities).CreateAsync(
            Schema, body, Options(localPart) with { ValidFrom = At.AddDays(-1) });
    }

    private static CreateOptions Options(string localPart) =>
        new("entity", "test", localPart, Actor, Tenant, ExplicitLocalPart: localPart);

    private static async Task AssertDeliveredOnceAsync(
        DurableAuditHarness restarted, AuditEventType eventType, string recordId, string schema)
    {
        Assert.Equal(1, await StagedCountAsync(restarted));
        var record = Assert.Single(await restarted.DeliveredAsync(Tenant, eventType));
        Assert.Equal(recordId, record.Payload.Payload.Body["recordId"]?.ToString());
        Assert.Equal(schema, record.Payload.Payload.Body["schema"]?.ToString());
        Assert.Equal(Actor, record.Actor);
        Assert.Equal(At, record.OccurredAt);
        Assert.Empty(await OwedAsync(restarted));

        // A second recovery delivers nothing twice.
        await using var again = restarted.Reopen();
        Assert.Single(await again.DeliveredAsync(Tenant, eventType));
        Assert.Equal(1, await StagedCountAsync(again));
    }

    private static async Task<int> StagedCountAsync(DurableAuditHarness harness)
    {
        await using var db = harness.Store.CreateContext();
        return await db.AuditOutbox.CountAsync(row => row.TenantId == Tenant.Value);
    }

    private static async Task<List<string>> OwedAsync(DurableAuditHarness harness)
    {
        await using var db = harness.Store.CreateContext();
        return await db.AuditOutbox.Where(row => row.PublishedAtUnixMs == null).Select(row => row.AuditId).ToListAsync();
    }

    private static LocalNodeDbContext LocalNode(DurableAuditHarness harness) =>
        harness.Store.CreateLocalNodeContext([new FinancialLedgerEntityModule()]);

    private sealed class LocalNodeFactory(DurableAuditHarness harness) : IDbContextFactory<LocalNodeDbContext>
    {
        public LocalNodeDbContext CreateDbContext() => LocalNode(harness);
    }

    /// <summary>The process stopping: nothing after commit runs, and nothing in memory survives the restart.</summary>
    private sealed class ProcessStopped : Exception;

    private sealed class CrashBeforeReact(bool crash = true) : IWritePipelineObserver
    {
        public WritePipelineStage? Last { get; private set; }

        public void OnStage(WritePipelineStage stage)
        {
            Last = stage;
            if (crash && stage == WritePipelineStage.React) throw new ProcessStopped();
        }
    }
}
