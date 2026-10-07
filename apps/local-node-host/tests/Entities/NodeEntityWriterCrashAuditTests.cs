using System.Text.Json;

using Harborline.Api.Blocks.FinancialLedger.Data;
using Harborline.Api.Blocks.FinancialLedger.Models;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Audit;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// T-1048 (DES-0029 ck-6): legal-entity writes commit the row and audit in one EF transaction. A simulated
/// stop on entry to react leaves both durable, and a fresh harness delivers the owed audit once.
/// Generic records use a volatile store and best-effort post-commit audit; the interruption cases below
/// pin that limitation, without claiming process-loss recovery. T-616 owns the durable Records boundary.
/// </summary>
public sealed class NodeEntityWriterCrashAuditTests : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("tenant-t1048f");
    private static readonly ActorId Actor = new("t1048f-operator");
    private static readonly SchemaId Schema = new("records.t1048f");
    private static readonly AuthorizationWriteContext Authority = new(Actor, Tenant, AdmittedInstant.FromRecordedAct(At));

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

    [Fact(DisplayName = "T-1048: a legal-entity create interrupted after commit delivers its audit once through a fresh harness")]
    public async Task LegalEntityCreate_CrashAfterCommit_DeliversItsAuditOnceAfterRestart()
    {
        var id = new LegalEntityId("t1048f-legal");

        await Assert.ThrowsAsync<ProcessStopped>(async () => await Writer(new CrashBeforeReact()).CreateLegalEntityAsync(
            new CreateLegalEntityCommand(id, "Crash LLC", "Llc", "DisregardedEntity", null), Authority));

        await using var restarted = _audit.Reopen();
        await using (var db = LocalNode(restarted))
            Assert.Equal("Crash LLC", (await db.Set<LegalEntity>().SingleAsync(row => row.Id == id)).LegalName);
        await AssertDeliveredOnceAsync(restarted, new AuditEventType("RecordWritten"), "t1048f-legal", "legal-entity");
    }

    [Fact(DisplayName = "T-1048 scope: a generic create interrupted before react leaves no durable audit receipt")]
    public async Task RecordCreate_InterruptedBeforeReact_LeavesNoDurableAuditReceipt()
    {
        using var body = JsonDocument.Parse("""{"name":"created"}""");

        await Assert.ThrowsAsync<ProcessStopped>(async () =>
            await Writer(new CrashBeforeReact()).CreateAsync(Schema, body, Options("t1048f-created"), Authority));

        var id = new EntityId("entity", "test", "t1048f-created");
        Assert.Equal("created", (await _entities.GetAsync(id))!.Body.RootElement.GetProperty("name").GetString());
        await using var restarted = _audit.Reopen();
        await AssertNoGenericRecoveryAsync(restarted, id, new AuditEventType("RecordWritten"));
    }

    [Fact(DisplayName = "T-1048 scope: a generic update interrupted before react leaves no durable audit receipt")]
    public async Task RecordUpdate_InterruptedBeforeReact_LeavesNoDurableAuditReceipt()
    {
        var id = await SeedAsync("t1048f-updated");
        using var body = JsonDocument.Parse("""{"name":"after"}""");

        await Assert.ThrowsAsync<ProcessStopped>(async () =>
            await Writer(new CrashBeforeReact()).UpdateAsync(id, body, new UpdateOptions(Actor), Authority));

        Assert.Equal("after", (await _entities.GetAsync(id))!.Body.RootElement.GetProperty("name").GetString());
        await using var restarted = _audit.Reopen();
        await AssertNoGenericRecoveryAsync(restarted, id, new AuditEventType("RecordWritten"));
    }

    [Fact(DisplayName = "T-1048 scope: a generic delete interrupted before react leaves no durable audit receipt")]
    public async Task RecordDelete_InterruptedBeforeReact_LeavesNoDurableAuditReceipt()
    {
        var id = await SeedAsync("t1048f-deleted");

        await Assert.ThrowsAsync<ProcessStopped>(async () =>
            await Writer(new CrashBeforeReact()).DeleteAsync(id, new DeleteOptions(Actor, Justification: "gone"), Authority));

        Assert.Null(await _entities.GetAsync(id));
        Assert.Equal(At, (await _entities.GetAsync(id, VersionSelector.AtSequence(2)))!.DeletedAt);
        await using var restarted = _audit.Reopen();
        await AssertNoGenericRecoveryAsync(restarted, id, new AuditEventType("RecordDeleted"));
    }

    [Fact(DisplayName = "T-1048: an uninterrupted legal-entity write returns the committed audit id, already delivered")]
    public async Task LegalEntityCreate_ReturnsTheCommittedAuditId_Delivered()
    {
        var written = await Writer().CreateLegalEntityAsync(
            new CreateLegalEntityCommand(new LegalEntityId("t1048f-live"), "Live LLC", "Llc", "DisregardedEntity", null), Authority);

        var record = Assert.Single((await _audit.Reader.ListAsync(Tenant,
            new AuditEventReaderQuery(EventType: new AuditEventType("RecordWritten")))).Records);
        Assert.NotNull(written.AuditId);
        Assert.Equal(written.AuditId, record.AuditId);
        Assert.Equal(Actor, record.Actor);
        Assert.Empty(await OwedAsync(_audit));
    }

    [Fact]
    public async Task LegalEntityCreate_AppendFailure_ReturnsNoTraceId_AndRecoversTheOwedAuditOnce()
    {
        await _audit.ExecuteAsync("CREATE TRIGGER t1048f_legal_delivery_fault BEFORE INSERT ON search_audit_trail BEGIN SELECT RAISE(ABORT, 'legal audit delivery failure'); END;");
        var id = new LegalEntityId("t1048f-delivery-fault");

        var written = await Writer().CreateLegalEntityAsync(
            new CreateLegalEntityCommand(id, "Owed LLC", "Llc", "DisregardedEntity", null), Authority);

        Assert.Null(written.AuditId);
        await using (var db = LocalNode(_audit))
            Assert.Equal("Owed LLC", (await db.Set<LegalEntity>().SingleAsync(row => row.Id == id)).LegalName);
        var owedId = Guid.Parse(Assert.Single(await OwedAsync(_audit)));
        Assert.Empty((await _audit.Reader.ListAsync(Tenant,
            new AuditEventReaderQuery(EventType: new AuditEventType("RecordWritten")))).Records);
        await _audit.ExecuteAsync("DROP TRIGGER t1048f_legal_delivery_fault;");

        await using var restarted = _audit.Reopen();
        Assert.Equal(1, await restarted.Outbox.DrainAsync());
        var recovered = Assert.Single((await restarted.Reader.ListAsync(Tenant,
            new AuditEventReaderQuery(EventType: new AuditEventType("RecordWritten")))).Records);
        Assert.Equal(owedId, recovered.AuditId);
        Assert.Equal("t1048f-delivery-fault", recovered.Payload.Payload.Body["recordId"]?.ToString());
        Assert.Equal(0, await restarted.Outbox.DrainAsync());
        Assert.Empty(await OwedAsync(restarted));
    }

    [Fact]
    public async Task LegalEntityCreate_AuditStagingFailure_RollsBackTheLegalEntity()
    {
        await _audit.ExecuteAsync("CREATE TRIGGER t1048f_legal_audit_fault BEFORE INSERT ON search_audit_outbox BEGIN SELECT RAISE(ABORT, 'legal audit staging failure'); END;");
        var failure = await Assert.ThrowsAsync<DbUpdateException>(async () => await Writer().CreateLegalEntityAsync(
            new CreateLegalEntityCommand(new LegalEntityId("t1048f-audit-fault"), "Fault LLC", "Llc", "DisregardedEntity", null), Authority));
        var sqlite = Assert.IsType<SqliteException>(failure.InnerException);
        Assert.Equal(19, sqlite.SqliteErrorCode);
        Assert.Equal(1811, sqlite.SqliteExtendedErrorCode);
        Assert.Contains("legal audit staging failure", sqlite.Message, StringComparison.Ordinal);
        await using var db = LocalNode(_audit);
        Assert.Empty(await db.Set<LegalEntity>().ToListAsync());
        Assert.Equal(0, await StagedCountAsync(_audit));
        Assert.Empty(await _audit.DeliveredAsync(Tenant, new AuditEventType("RecordWritten")));
    }

    [Fact]
    public async Task GenericCreate_ReturnsItsBestEffortAuditId_WithoutAnOutboxReceipt()
    {
        using var body = JsonDocument.Parse("""{"name":"live"}""");
        var written = await Writer().CreateWithReceiptAsync(Schema, body, Options("t1048f-generic-live"), Authority);
        var record = Assert.Single(await _audit.DeliveredAsync(Tenant, new AuditEventType("RecordWritten")));
        Assert.NotNull(written.AuditId);
        Assert.Equal(written.AuditId, record.AuditId);
        Assert.Equal("t1048f-generic-live", record.Payload.Payload.Body["recordId"]?.ToString());
        Assert.Equal(0, await StagedCountAsync(_audit));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task GenericWrite_AuditAppendFailure_PreservesTheCommittedVolatileEffect(string operation)
    {
        var id = new EntityId("entity", "test", "t1048f-best-effort");
        if (operation != "create") await SeedAsync("t1048f-best-effort");
        await _audit.ExecuteAsync("CREATE TRIGGER t1048f_generic_audit_fault BEFORE INSERT ON search_audit_trail BEGIN SELECT RAISE(ABORT, 'generic audit append failure'); END;");
        using var body = JsonDocument.Parse("""{"name":"after"}""");
        if (operation == "create")
        {
            var written = await Writer().CreateWithReceiptAsync(Schema, body, Options("t1048f-best-effort"), Authority);
            Assert.Null(written.AuditId);
        }
        else if (operation == "update")
            await Writer().UpdateAsync(id, body, new UpdateOptions(Actor), Authority);
        else
            await Writer().DeleteAsync(id, new DeleteOptions(Actor, Justification: "gone"), Authority);
        var stored = Assert.IsType<Entity>(await _entities.GetAsync(id,
            operation == "delete" ? VersionSelector.AtSequence(2) : VersionSelector.Latest));
        if (operation == "delete")
        {
            Assert.Null(await _entities.GetAsync(id));
            Assert.Equal(At, stored.DeletedAt);
        }
        else Assert.Equal("after", stored.Body.RootElement.GetProperty("name").GetString());
        Assert.Equal(0, await StagedCountAsync(_audit));
        Assert.Empty(await _audit.DeliveredAsync(Tenant, new AuditEventType("RecordWritten")));
        Assert.Empty(await _audit.DeliveredAsync(Tenant, new AuditEventType("RecordDeleted")));
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
        accepted: new AuthorizedActAudit(_audit.Trail, new Ed25519Signer(KeyPair.Generate()), NullLogger<AuthorizedActAudit>.Instance),
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

    private static async Task AssertNoGenericRecoveryAsync(DurableAuditHarness restarted, EntityId id, AuditEventType eventType)
    {
        var freshStore = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        Assert.Null(await freshStore.GetAsync(id));
        Assert.Equal(0, await StagedCountAsync(restarted));
        Assert.Empty(await restarted.DeliveredAsync(Tenant, eventType));
        Assert.Empty(await OwedAsync(restarted));
    }

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
        harness.Store.CreateLocalNodeContext([new FinancialLedgerEntityModule(),
            new Harborline.Api.Blocks.FinancialPeriods.Data.FinancialPeriodsEntityModule(), new Data.Audit.AuditOutboxEntityModule()]);

    private sealed class LocalNodeFactory(DurableAuditHarness harness) : IDbContextFactory<LocalNodeDbContext>
    {
        public LocalNodeDbContext CreateDbContext() => LocalNode(harness);
    }

    /// <summary>Simulates interruption before react; this is not an operating-system process kill.</summary>
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
