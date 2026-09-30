using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// ck-10 S2 (DES-0029, ADR 0038): the generic record create, update and delete run the six stages through
/// <see cref="WritePipeline.RunAsync"/>. Each refusal stops the write at its stage, and delete now binds
/// the record it removes and records the accepted act.
/// </summary>
public sealed class RecordWritePipelineTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = new("records-pipeline");
    private static readonly ActorId Actor = new("records-operator");
    private static readonly SchemaId Schema = new("records.pipeline");
    private static readonly AuthorizationWriteContext Authority = new(Actor, Tenant, At);

    [Fact(DisplayName = "ck-10 S2: record create runs all six stages in order and stores the body validate admitted")]
    public async Task Create_RunsTheSixStagesInOrder()
    {
        var h = new Harness();
        using var body = JsonDocument.Parse("""{"name":"created"}""");

        var written = await h.Writer.CreateWithReceiptAsync(Schema, body, Options("created"), Authority);

        Assert.Equal(WritePipeline.Order, h.Stages);
        var stored = await h.Entities.GetAsync(written.Entity);
        Assert.Equal("created", stored!.Body.RootElement.GetProperty("name").GetString());
        Assert.Equal(At, stored.CreatedAt);
    }

    [Fact(DisplayName = "ck-10 S2: a record the validator refuses stops at validate and commits nothing")]
    public async Task Create_ValidationRefusal_StopsAtValidate()
    {
        var h = new Harness(new RefusingValidator());
        using var body = JsonDocument.Parse("""{"name":"refused"}""");

        await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await h.Writer.CreateAsync(Schema, body, Options("refused"), Authority));

        Assert.Equal(WritePipelineStage.Validate, h.Stages[^1]);
        Assert.Null(await h.Entities.GetAsync(InMemoryEntityStore.DeriveEntityId(Schema, Options("refused"))));
    }

    [Fact(DisplayName = "ck-10 S2: record update runs all six stages; a missing record is refused at bind")]
    public async Task Update_RunsTheSixStages_AndRefusesAMissingRecordAtBind()
    {
        var h = new Harness();
        var id = await h.Seed("updated");
        using var body = JsonDocument.Parse("""{"name":"after"}""");

        await h.Writer.UpdateAsync(id, body, new UpdateOptions(Actor), Authority);
        Assert.Equal(WritePipeline.Order, h.Stages);
        Assert.Equal("after", (await h.Entities.GetAsync(id))!.Body.RootElement.GetProperty("name").GetString());

        h.Stages.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await h.Writer.UpdateAsync(new EntityId("entity", "test", "missing"), body, new UpdateOptions(Actor), Authority));
        Assert.Equal(WritePipelineStage.Bind, h.Stages[^1]);
    }

    [Fact(DisplayName = "ck-10 S2: record delete runs all six stages and records the accepted act against its decision")]
    public async Task Delete_RunsTheSixStages_AndAuditsTheAcceptedAct()
    {
        var h = new Harness();
        var id = await h.Seed("deleted");

        await h.Writer.DeleteAsync(id, new DeleteOptions(Actor, Justification: "gone"), Authority);

        Assert.Equal(WritePipeline.Order, h.Stages);
        Assert.Null(await h.Entities.GetAsync(id));
        var call = Assert.Single(h.Trail.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IAuthorizedAuditTrail.AppendAuthorizedAsync));
        var record = (AuditRecord)call.GetArguments()[0]!;
        Assert.Equal(NodeEntityWriter.RecordDeletedEventType, record.EventType);
        Assert.Equal(Tenant, record.TenantId);
    }

    [Fact(DisplayName = "ck-10 S2: delete binds the record: another tenant's record is refused at bind and survives")]
    public async Task Delete_AnotherTenantsRecord_IsRefusedAtBind()
    {
        var h = new Harness();
        var id = await h.Seed("foreign", new TenantId("another-tenant"));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await h.Writer.DeleteAsync(id, new DeleteOptions(Actor), Authority));

        Assert.Equal(WritePipelineStage.Bind, h.Stages[^1]);
        Assert.NotNull(await h.Entities.GetAsync(id));
        Assert.Empty(h.Trail.ReceivedCalls());
    }

    [Fact(DisplayName = "ck-10 S2: delete of a missing record is refused at bind")]
    public async Task Delete_MissingRecord_IsRefusedAtBind()
    {
        var h = new Harness();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await h.Writer.DeleteAsync(new EntityId("entity", "test", "missing"), new DeleteOptions(Actor), Authority));

        Assert.Equal(WritePipelineStage.Bind, h.Stages[^1]);
    }

    private static CreateOptions Options(string localPart, TenantId? tenant = null) =>
        new("entity", "test", localPart, Actor, tenant ?? Tenant, ExplicitLocalPart: localPart);

    private sealed class Harness : IWritePipelineObserver
    {
        public Harness(IEntityValidator? validator = null)
        {
            Entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
            var audit = new AuthorizedActAudit(Trail, new Ed25519Signer(KeyPair.Generate()), NullLogger<AuthorizedActAudit>.Instance);
            Writer = new NodeEntityWriter(Substitute.For<IDbContextFactory<LocalNodeDbContext>>(), Entities,
                validator ?? NullEntityValidator.Instance, TestAuthorization.AllowGate(), accepted: audit, pipelineObserver: this);
        }

        public InMemoryEntityStore Entities { get; }
        public IAuthorizedAuditTrail Trail { get; } = Substitute.For<IAuthorizedAuditTrail>();
        public NodeEntityWriter Writer { get; }
        public List<WritePipelineStage> Stages { get; } = [];

        public void OnStage(WritePipelineStage stage) => Stages.Add(stage);

        public async Task<EntityId> Seed(string localPart, TenantId? tenant = null)
        {
            using var body = JsonDocument.Parse("""{"name":"before"}""");
            return await Entities.CreateAsync(Schema, body, Options(localPart, tenant) with { ValidFrom = At.AddDays(-1) });
        }
    }

    private sealed class RefusingValidator : IEntityValidator
    {
        public Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default) =>
            throw new EntityValidationException("refused", "records.pipeline.refused", ["/name"]);
    }
}
