using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class SchemaRegistryEntityValidatorTests
{
    private const string RecordsSchema = """
        {"type":"object","required":["name","count"],"properties":{"name":{"type":"string"},"count":{"type":"integer"}}}
        """;

    [Fact]
    public async Task RegisteredSchema_RefusesMissingAndWrongType_WithNamedReasonAndPointers()
    {
        var registry = new InMemorySchemaRegistry(TimeProvider.System);
        var schema = await registry.RegisterAsync(RecordsSchema);
        var validator = new SchemaRegistryEntityValidator(registry);
        using var missing = JsonDocument.Parse("{\"count\":1}");
        using var wrongType = JsonDocument.Parse("{\"name\":\"record\",\"count\":\"one\"}");

        var missingRefusal = await Assert.ThrowsAsync<EntityValidationException>(
            () => validator.ValidateAsync(schema.Id, missing));
        var typeRefusal = await Assert.ThrowsAsync<EntityValidationException>(
            () => validator.ValidateAsync(schema.Id, wrongType));

        Assert.Equal(SchemaRegistryEntityValidator.BodyInvalidCode, missingRefusal.Code);
        Assert.Contains("/name", missingRefusal.Pointers);
        Assert.Equal(SchemaRegistryEntityValidator.BodyInvalidCode, typeRefusal.Code);
        Assert.Contains("/count", typeRefusal.Pointers);
    }

    [Fact]
    public async Task UnknownSchema_IsRefusedByName_WithoutInspectingTheBody()
    {
        var validator = new SchemaRegistryEntityValidator(new InMemorySchemaRegistry(TimeProvider.System));
        using var body = JsonDocument.Parse("{\"secret\":\"not echoed\"}");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(
            () => validator.ValidateAsync(new SchemaId("schema:unknown"), body));

        Assert.Equal(SchemaRegistryEntityValidator.SchemaUnknownCode, refusal.Code);
        Assert.Equal([string.Empty], refusal.Pointers);
        Assert.DoesNotContain("secret", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdmittedHeadlessRecordWrite_ValidatesBeforePersistence_AndValidBodiesReadBack()
    {
        var registry = new InMemorySchemaRegistry(TimeProvider.System);
        var schema = await registry.RegisterAsync(RecordsSchema);
        var validator = new SchemaRegistryEntityValidator(registry);
        var store = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var writer = new NodeEntityWriter(
            Substitute.For<IDbContextFactory<LocalNodeDbContext>>(), store, validator,
            TestAuthorization.Gate(true));
        var actor = new ActorId("operator");
        var tenant = new TenantId("validator-test");
        var authority = new AuthorizationWriteContext(actor, tenant, DateTimeOffset.UtcNow);
        var options = new CreateOptions("entity", "test", "validator-record", actor, tenant,
            ExplicitLocalPart: "validator-record");
        using var invalid = JsonDocument.Parse("{\"name\":\"record\",\"count\":\"one\"}");
        using var valid = JsonDocument.Parse("{\"name\":\"record\",\"count\":1}");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(
            () => writer.CreateAsync(schema.Id, invalid, options, authority).AsTask());
        Assert.Equal(SchemaRegistryEntityValidator.BodyInvalidCode, refusal.Code);
        Assert.Null(await store.GetAsync(new EntityId("entity", "test", "validator-record")));

        var id = await writer.CreateAsync(schema.Id, valid, options, authority);
        Assert.NotNull(await store.GetAsync(id));
    }

    [Fact]
    public void NodeFormsComposition_ReplacesTheAcceptingNullValidator()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Substitute.For<IAuthorizedAuditTrail>());

        services.AddNodeForms();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SchemaRegistryEntityValidator>(provider.GetRequiredService<IEntityValidator>());
    }

    [Fact]
    public async Task RefusedRecordWrite_RecordsCodeAndPointersInTheDecisionTraceWithoutTheBody()
    {
        const string marker = "entity-validation-trace-marker";
        var registry = new InMemorySchemaRegistry(TimeProvider.System);
        var schema = await registry.RegisterAsync(RecordsSchema);
        var trail = new InMemoryAuditTrail();
        using var keys = Harborline.Api.Foundation.Crypto.KeyPair.Generate();
        var audit = new Harborline.Api.LocalNodeHost.Health.AuthorizationRefusalAudit(
            trail,
            new Harborline.Api.Foundation.Crypto.Ed25519Signer(keys),
            NullLogger<Harborline.Api.LocalNodeHost.Health.AuthorizationRefusalAudit>.Instance);
        var store = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var writer = new NodeEntityWriter(
            Substitute.For<IDbContextFactory<LocalNodeDbContext>>(),
            store,
            new SchemaRegistryEntityValidator(registry),
            TestAuthorization.Gate(true),
            audit);
        var actor = new ActorId("trace-operator");
        var tenant = new TenantId("validator-trace-test");
        var authority = new AuthorizationWriteContext(actor, tenant, DateTimeOffset.UtcNow);
        var options = new CreateOptions("entity", "test", "trace-refusal", actor, tenant,
            ExplicitLocalPart: "trace-refusal");
        using var invalid = JsonDocument.Parse("{\"name\":\"" + marker + "\",\"count\":\"one\"}");

        await Assert.ThrowsAsync<EntityValidationException>(
            () => writer.CreateAsync(schema.Id, invalid, options, authority).AsTask());

        var entries = new List<AuditRecord>();
        await foreach (var entry in trail.QueryAsync(new AuditQuery(tenant))) entries.Add(entry);
        var trace = JsonSerializer.Serialize(Assert.Single(entries).Payload.Payload);
        Assert.Contains("entity.validation.body_invalid", trace, StringComparison.Ordinal);
        Assert.Contains("/count", trace, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, trace, StringComparison.Ordinal);
    }
}
