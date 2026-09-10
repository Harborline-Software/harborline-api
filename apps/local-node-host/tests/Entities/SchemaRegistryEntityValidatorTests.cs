using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.Kernel.Schema.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data.Entities;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

public sealed class SchemaRegistryEntityValidatorTests
{
    private const string RecordSchema = """
        {
          "type": "object",
          "required": ["name", "count"],
          "properties": {
            "name": { "type": "string" },
            "count": { "type": "integer", "minimum": 1 }
          }
        }
        """;

    [Fact]
    public async Task ValidateAsync_ActivatedSchema_RefusesRequiredAndTypeFailuresWithPointers()
    {
        var registry = new InMemorySchemaRegistry(TimeProvider.System);
        var schema = await registry.RegisterAsync(RecordSchema);
        var validator = new SchemaRegistryEntityValidator(registry);
        using var body = JsonDocument.Parse("{\"count\":\"wrong\"}");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(
            () => validator.ValidateAsync(schema.Id, body));

        Assert.Equal("entity.validation.body_invalid", refusal.ReasonCode);
        Assert.Contains("/name", refusal.Pointers);
        Assert.Contains("/count", refusal.Pointers);
    }

    [Fact]
    public async Task ValidateAsync_UnknownSchema_RefusesWithoutCandidateEcho()
    {
        var validator = new SchemaRegistryEntityValidator(new InMemorySchemaRegistry(TimeProvider.System));
        using var body = JsonDocument.Parse("{\"secret\":\"not echoed\"}");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(
            () => validator.ValidateAsync(new SchemaId("schema:unknown"), body));

        Assert.Equal("entity.validation.schema_unknown", refusal.ReasonCode);
        Assert.Equal([string.Empty], refusal.Pointers);
        Assert.DoesNotContain("secret", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RealValidator_UsesAnActivatedSchema_AndRefusesBeforePersistence()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddHarborlineKernelSchemaRegistry();
        services.AddSingleton<IEntityValidator, SchemaRegistryEntityValidator>();
        services.AddSingleton<IEntityMutationStore>(sp => new InMemoryEntityStore(
            new InMemoryAssetStorage(), TimeProvider.System, sp.GetRequiredService<IEntityValidator>()));
        using var provider = services.BuildServiceProvider();

        var validator = provider.GetRequiredService<IEntityValidator>();
        Assert.IsType<SchemaRegistryEntityValidator>(validator);
        var registry = provider.GetRequiredService<ISchemaRegistry>();
        var schema = await registry.RegisterAsync(RecordSchema);
        var store = provider.GetRequiredService<IEntityMutationStore>();
        var options = new CreateOptions(
            "records", "validator-test", "valid", new ActorId("operator"), new TenantId("validator-test"));

        using var valid = JsonDocument.Parse("{\"name\":\"Harbor\",\"count\":1}");
        var id = await store.CreateAsync(schema.Id, valid, options);
        Assert.NotNull(await store.GetAsync(id));

        using var invalid = JsonDocument.Parse("{\"name\":3}");
        var invalidOptions = options with { Nonce = "invalid" };
        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() => store.CreateAsync(
            schema.Id, invalid, invalidOptions));
        Assert.Equal("entity.validation.body_invalid", refusal.ReasonCode);
        Assert.Null(await store.GetAsync(InMemoryEntityStore.DeriveEntityId(schema.Id, invalidOptions)));
    }
}
