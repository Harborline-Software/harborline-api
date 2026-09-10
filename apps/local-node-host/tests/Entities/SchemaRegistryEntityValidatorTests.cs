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
}
