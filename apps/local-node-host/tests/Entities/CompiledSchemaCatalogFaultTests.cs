using System.Text.Json;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Kernel.Schema;

using NSubstitute;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Ticket 367 — a catalog activation or lookup fault cannot leave a record write
/// with a stale or missing schema that it treats as valid.
/// </summary>
public sealed class CompiledSchemaCatalogFaultTests
{
    private static readonly SchemaId Name = new("records.catalog-fault");
    private static readonly Schema Registered = new(
        new SchemaId("schema:missing-artefact"),
        "{\"type\":\"object\"}",
        [],
        [],
        [],
        Cid.Parse("bafkreihdwdcefgh4dqkjv67uzcmw7ojee6xedzdetojuzjevtenxquvyku"));

    [Fact(DisplayName = "367: an activation registry fault clears its binding and the next write refuses by name — holds RW-3 RW-5 RW-H7")]
    public async Task Activate_RegistryThrows_ClearsBindingAndNextWriteRefusesByName()
    {
        var registry = Registry();
        var registrations = 0;
        registry.RegisterAsync(Arg.Any<string>(), ct: Arg.Any<CancellationToken>())
            .Returns(_ => ++registrations == 1
                ? new ValueTask<Schema>(Registered)
                : throw new InvalidOperationException("registry unavailable"));
        var catalog = new CompiledSchemaCatalog(registry);
        var validator = new CompiledSchemaEntityValidator(registry, catalog);

        await catalog.ActivateAsync(Name, Registered.JsonSchemaText);
        Assert.True(catalog.TryGet(Name, out _));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.ActivateAsync(Name, "{\"type\":\"object\"}").AsTask());

        Assert.False(catalog.TryGet(Name, out _));
        await AssertSchemaUnknownAsync(validator);
    }

    [Fact(DisplayName = "367: an activation registry cancellation propagates and clears its binding — holds RW-5 RW-H7")]
    public async Task Activate_RegistryCancels_PropagatesCancellation()
    {
        var registry = Registry();
        var registrations = 0;
        registry.RegisterAsync(Arg.Any<string>(), ct: Arg.Any<CancellationToken>())
            .Returns(_ => ++registrations == 1
                ? new ValueTask<Schema>(Registered)
                : throw new OperationCanceledException());
        var catalog = new CompiledSchemaCatalog(registry);

        await catalog.ActivateAsync(Name, Registered.JsonSchemaText);
        Assert.True(catalog.TryGet(Name, out _));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.ActivateAsync(Name, "{\"type\":\"object\"}").AsTask());

        Assert.False(catalog.TryGet(Name, out _));
    }

    [Fact(DisplayName = "367: a null registry activation result or empty schema text leaves a named refusal — holds RW-3 RW-5 RW-H7")]
    public async Task Activate_NullResultOrEmptyText_NextWriteRefusesByName()
    {
        var nullRegistry = Registry();
        var nullRegistrations = 0;
        nullRegistry.RegisterAsync(Arg.Any<string>(), ct: Arg.Any<CancellationToken>())
            .Returns(_ => ++nullRegistrations == 1
                ? new ValueTask<Schema>(Registered)
                : new ValueTask<Schema>((Schema)null!));
        var nullCatalog = new CompiledSchemaCatalog(nullRegistry);
        var nullValidator = new CompiledSchemaEntityValidator(nullRegistry, nullCatalog);

        await nullCatalog.ActivateAsync(Name, Registered.JsonSchemaText);
        Assert.True(nullCatalog.TryGet(Name, out _));
        Assert.Null(await nullCatalog.ActivateAsync(Name, "{\"type\":\"object\"}"));
        Assert.False(nullCatalog.TryGet(Name, out _));
        await AssertSchemaUnknownAsync(nullValidator);

        var emptyRegistry = Registry();
        var emptyRegistrations = 0;
        emptyRegistry.RegisterAsync(string.Empty, ct: Arg.Any<CancellationToken>())
            .Returns<ValueTask<Schema>>(_ => throw new InvalidSchemaException("schema text is empty"));
        emptyRegistry.RegisterAsync(Arg.Is<string>(text => text != string.Empty), ct: Arg.Any<CancellationToken>())
            .Returns(_ => ++emptyRegistrations == 1
                ? new ValueTask<Schema>(Registered)
                : throw new InvalidOperationException("unexpected registration"));
        var emptyCatalog = new CompiledSchemaCatalog(emptyRegistry);
        var emptyValidator = new CompiledSchemaEntityValidator(emptyRegistry, emptyCatalog);

        await emptyCatalog.ActivateAsync(Name, Registered.JsonSchemaText);
        Assert.True(emptyCatalog.TryGet(Name, out _));
        await Assert.ThrowsAsync<InvalidSchemaException>(() => emptyCatalog.ActivateAsync(Name, string.Empty).AsTask());
        Assert.False(emptyCatalog.TryGet(Name, out _));
        await AssertSchemaUnknownAsync(emptyValidator);
    }

    [Fact(DisplayName = "367: a catalog binding whose compiled artefact is missing refuses by name — holds RW-3 RW-5 RW-H7")]
    public async Task Validate_MissingCompiledArtefact_RefusesByName()
    {
        var registry = Registry();
        registry.RegisterAsync(Arg.Any<string>(), ct: Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Schema>(Registered));
        registry.ValidateAsync(Registered.Id, Arg.Any<ReadOnlyMemory<byte>>(), ct: Arg.Any<CancellationToken>())
            .Returns<ValueTask<SchemaValidationResult>>(_ => throw new SchemaNotFoundException("compiled artefact missing"));
        var catalog = new CompiledSchemaCatalog(registry);
        var validator = new CompiledSchemaEntityValidator(registry, catalog);

        await catalog.ActivateAsync(Name, Registered.JsonSchemaText);
        Assert.True(catalog.TryGet(Name, out var activated));
        Assert.Equal(Registered.Id, activated.CompiledSchemaId);

        await AssertSchemaUnknownAsync(validator);
    }

    private static ISchemaRegistry Registry()
    {
        var registry = Substitute.For<ISchemaRegistry>();
        registry.GetAsync(Arg.Any<SchemaId>(), ct: Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Schema?>((Schema?)null));
        return registry;
    }

    private static async Task AssertSchemaUnknownAsync(CompiledSchemaEntityValidator validator)
    {
        using var body = JsonDocument.Parse("{\"valid\":true}");
        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() => validator.ValidateAsync(Name, body));
        Assert.Equal(CompiledSchemaEntityValidator.SchemaUnknown, refusal.ReasonCode);
    }
}
