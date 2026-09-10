using System.Text.Json;

using Harborline.Api.Foundation.Assets;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using NSubstitute;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Ticket 151 / L1418 — the REAL pre-commit validator: a record write is validated by the authority's
/// schema registry before persistence, an unknown schema is a named refusal rather than a pass, and the
/// headless coordinator path runs the same stage in the same order as the route.
/// </summary>
public sealed class SchemaRegistryEntityValidatorTests
{
    private static readonly TenantId Tenant = new("schema-validator-tests");
    private static readonly ActorId Actor = new("operator");
    private static readonly DateTimeOffset At = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private static InMemorySchemaRegistry Registry() =>
        new(new FixedTimeProvider(At));

    private static SchemaRegistryEntityValidator Validator(ISchemaRegistry registry) =>
        new(registry, NodeRecordsSchemas.All);

    private static JsonDocument Body(string json) => JsonDocument.Parse(json);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact(DisplayName = "Validator: an unknown schema is refused by name, never passed")]
    public async Task UnknownSchema_IsRefusedByName()
    {
        var validator = Validator(Registry());
        using var body = Body("""{"legalName":"Fine Co"}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() =>
            validator.ValidateAsync(new SchemaId("not-a-registered-schema"), body));

        Assert.Equal(EntityValidationReasons.SchemaUnknown, refusal.ReasonCode);
        Assert.Contains("not-a-registered-schema", refusal.Message);
        Assert.DoesNotContain("Fine Co", refusal.Message);
    }

    [Fact(DisplayName = "Validator: a missing required property is refused with its pointer")]
    public async Task MissingRequiredProperty_IsRefusedWithPointer()
    {
        var validator = Validator(Registry());
        using var body = Body("""{"kind":"Llc","taxClassification":"DisregardedEntity"}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() =>
            validator.ValidateAsync(EntityRoutes.LegalEntitySchema, body));

        Assert.Equal(EntityValidationReasons.BodyInvalid, refusal.ReasonCode);
        Assert.Contains("/legalName", refusal.Pointers);
    }

    [Fact(DisplayName = "Validator: a wrong property type is refused with its pointer")]
    public async Task WrongPropertyType_IsRefusedWithPointer()
    {
        var validator = Validator(Registry());
        using var body = Body("""{"legalName":42,"kind":"Llc","taxClassification":"DisregardedEntity"}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() =>
            validator.ValidateAsync(EntityRoutes.LegalEntitySchema, body));

        Assert.Equal(EntityValidationReasons.BodyInvalid, refusal.ReasonCode);
        Assert.Contains("/legalName", refusal.Pointers);
        Assert.DoesNotContain("42", refusal.Message);
    }

    [Fact(DisplayName = "Validator: a conforming body is admitted")]
    public async Task ConformingBody_IsAdmitted()
    {
        var validator = Validator(Registry());
        using var body = Body("""{"legalName":"Harborline LLC","kind":"Llc","taxClassification":"DisregardedEntity"}""");

        await validator.ValidateAsync(EntityRoutes.LegalEntitySchema, body);
    }

    [Fact(DisplayName = "Validator: a schema that declares nothing still admits a well-formed body")]
    public async Task SchemaDeclaringNothing_AdmitsWellFormedBody()
    {
        var registry = Registry();
        var permissive = await registry.RegisterAsync("""{"$schema":"https://json-schema.org/draft/2020-12/schema"}""");
        var validator = Validator(registry);
        using var body = Body("""{"anything":"at all"}""");

        await validator.ValidateAsync(permissive.Id, body);
    }

    [Fact(DisplayName = "Validator: a pack-activated registry schema resolves by its own id")]
    public async Task RegistryIssuedSchema_ResolvesDirectly()
    {
        var registry = Registry();
        var registered = await registry.RegisterAsync("""
            {"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object",
             "required":["assetTag"],"properties":{"assetTag":{"type":"string"}}}
            """);
        var validator = Validator(registry);
        using var bad = Body("""{"assetTag":7}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() =>
            validator.ValidateAsync(registered.Id, bad));
        Assert.Contains("/assetTag", refusal.Pointers);

        using var good = Body("""{"assetTag":"AT-1"}""");
        await validator.ValidateAsync(registered.Id, good);
    }

    // ── The headless record-create path: IEntityWriteCoordinator, no HTTP route ──────────────────────

    private static (NodeEntityWriter Writer, InMemoryEntityStore Store) HeadlessWriter(bool allow)
    {
        var storage = new InMemoryAssetStorage();
        var store = new InMemoryEntityStore(storage, new FixedTimeProvider(At));
        var writer = new NodeEntityWriter(
            Substitute.For<Microsoft.EntityFrameworkCore.IDbContextFactory<Data.LocalNodeDbContext>>(),
            store,
            Validator(Registry()),
            TestAuthorization.Gate(allow));
        return (writer, store);
    }

    private static CreateOptions Options(string localPart) =>
        new("entity", "test", localPart, Actor, Tenant, ExplicitLocalPart: localPart);

    [Fact(DisplayName = "Headless create: an invalid body is refused by the validator, nothing persists")]
    public async Task Headless_InvalidBody_IsRefusedAndNothingPersists()
    {
        var (writer, store) = HeadlessWriter(allow: true);
        IEntityWriteCoordinator coordinator = writer;
        using var body = Body("""{"legalName":42,"kind":"Llc","taxClassification":"DisregardedEntity"}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await coordinator.CreateAsync(EntityRoutes.LegalEntitySchema, body, Options("headless-bad"),
                Actor, Tenant, At));

        Assert.Equal(EntityValidationReasons.BodyInvalid, refusal.ReasonCode);
        Assert.Contains("/legalName", refusal.Pointers);
        Assert.Null(await store.GetAsync(new EntityId("entity", "test", "headless-bad")));
    }

    [Fact(DisplayName = "Headless create: an unknown schema is refused, nothing persists")]
    public async Task Headless_UnknownSchema_IsRefusedAndNothingPersists()
    {
        var (writer, store) = HeadlessWriter(allow: true);
        IEntityWriteCoordinator coordinator = writer;
        using var body = Body("""{"legalName":"Fine Co"}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await coordinator.CreateAsync(new SchemaId("unheard-of"), body, Options("headless-unknown"),
                Actor, Tenant, At));

        Assert.Equal(EntityValidationReasons.SchemaUnknown, refusal.ReasonCode);
        Assert.Null(await store.GetAsync(new EntityId("entity", "test", "headless-unknown")));
    }

    [Fact(DisplayName = "Headless create: a conforming body persists and reads back")]
    public async Task Headless_ConformingBody_PersistsAndReadsBack()
    {
        var (writer, store) = HeadlessWriter(allow: true);
        IEntityWriteCoordinator coordinator = writer;
        using var body = Body("""{"legalName":"Harborline LLC","kind":"Llc","taxClassification":"DisregardedEntity"}""");

        var id = await coordinator.CreateAsync(EntityRoutes.LegalEntitySchema, body, Options("headless-good"),
            Actor, Tenant, At);

        var stored = await store.GetAsync(id);
        Assert.NotNull(stored);
        Assert.Equal("Harborline LLC", stored!.Body.RootElement.GetProperty("legalName").GetString());
    }

    [Fact(DisplayName = "Headless create: an unauthorized caller with an invalid body is refused by the GATE, not the validator")]
    public async Task Headless_UnauthorizedInvalidBody_IsRefusedByTheGate()
    {
        var (writer, store) = HeadlessWriter(allow: false);
        IEntityWriteCoordinator coordinator = writer;
        using var body = Body("""{"legalName":42}""");

        // Gate first (ADR 0065 clause 4): the refusal is an authorization denial, never a validation one,
        // even though the body could not have passed validation either.
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await coordinator.CreateAsync(EntityRoutes.LegalEntitySchema, body, Options("headless-denied"),
                Actor, Tenant, At));

        Assert.Null(await store.GetAsync(new EntityId("entity", "test", "headless-denied")));
    }
}
