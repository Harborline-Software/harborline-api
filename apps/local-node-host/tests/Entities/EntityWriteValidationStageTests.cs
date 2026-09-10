using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.Kernel.Schema.DependencyInjection;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Ticket 151 slice 2 (ledger L1418) — a live record write is validated by a REAL validator before
/// persistence, the gate still decides first, and the headless coordinator face runs the same stage.
/// </summary>
public sealed class EntityWriteValidationStageTests
{
    private const string RecordSchemaText =
        """
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "type": "object",
          "required": ["name", "count"],
          "properties": {
            "name": { "type": "string", "minLength": 1 },
            "count": { "type": "integer", "minimum": 1 }
          }
        }
        """;

    private static readonly ActorId Actor = new("operator");
    private static readonly TenantId Tenant = new("validation-stage");
    private static readonly DateTimeOffset At = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        InMemoryEntityStore Store,
        NodeEntityWriter Writer,
        IEntityWriteCoordinator Headless,
        IEntityValidator Validator,
        SchemaId Schema);

    private static async Task<Fixture> BuildAsync(bool allowed = true)
    {
        var registry = TestNodeRecordSchemas.NewRegistry();
        var schema = (await registry.RegisterAsync(RecordSchemaText)).Id;
        var validator = new SchemaRegistryEntityValidator(registry);
        var store = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var writer = new NodeEntityWriter(
            Substitute.For<IDbContextFactory<LocalNodeDbContext>>(),
            store,
            new EntityBodyAdmission(validator),
            TestNodeRecordSchemas.Over(registry),
            TestAuthorization.Gate(allowed));
        return new Fixture(store, writer, writer, validator, schema);
    }

    private static CreateOptions Options(string localPart) =>
        new("entity", "test", localPart, Actor, Tenant, At, ExplicitLocalPart: localPart);

    [Fact(DisplayName = "Ticket 151 (a): a body missing a required property is refused with the named reason and pointer, nothing persists")]
    public async Task Create_WhenRequiredPropertyMissing_IsRefusedAndPersistsNothing()
    {
        var fixture = await BuildAsync();
        using var body = JsonDocument.Parse("""{"name":"no count"}""");
        var options = Options("missing-required");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await fixture.Writer.CreateAsync(
                fixture.Schema, body, options, new AuthorizationWriteContext(Actor, Tenant, At)));

        Assert.Equal(EntityValidationException.BodyInvalid, refusal.ReasonCode);
        Assert.NotEmpty(refusal.Pointers);
        Assert.DoesNotContain("no count", refusal.Message, StringComparison.Ordinal);
        Assert.Null(await fixture.Store.GetAsync(new EntityId("entity", "test", "missing-required")));
    }

    [Fact(DisplayName = "Ticket 151 (a): a wrong-typed property is refused at its pointer, nothing persists")]
    public async Task Create_WhenPropertyHasTheWrongType_IsRefusedAtItsPointer()
    {
        var fixture = await BuildAsync();
        using var body = JsonDocument.Parse("""{"name":"wrong type","count":"seven"}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await fixture.Writer.CreateAsync(
                fixture.Schema, body, Options("wrong-type"), new AuthorizationWriteContext(Actor, Tenant, At)));

        Assert.Equal(EntityValidationException.BodyInvalid, refusal.ReasonCode);
        Assert.Contains("/count", refusal.Pointers);
        Assert.Null(await fixture.Store.GetAsync(new EntityId("entity", "test", "wrong-type")));
    }

    [Fact(DisplayName = "Ticket 151 (b): a valid body persists and reads back")]
    public async Task Create_WhenBodySatisfiesTheSchema_PersistsAndReadsBack()
    {
        var fixture = await BuildAsync();
        using var body = JsonDocument.Parse("""{"name":"valid","count":3}""");

        var id = await fixture.Writer.CreateAsync(
            fixture.Schema, body, Options("valid"), new AuthorizationWriteContext(Actor, Tenant, At));

        var stored = await fixture.Store.GetAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(fixture.Schema, stored.Schema);
        Assert.Equal("valid", stored.Body.RootElement.GetProperty("name").GetString());
    }

    [Fact(DisplayName = "Ticket 151 (c): an unknown schema is refused by name, nothing persists")]
    public async Task Create_WhenSchemaIsUnknown_IsRefusedAndPersistsNothing()
    {
        var fixture = await BuildAsync();
        using var body = JsonDocument.Parse("""{"name":"valid","count":3}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await fixture.Writer.CreateAsync(
                new SchemaId("schema:not-registered"),
                body,
                Options("unknown-schema"),
                new AuthorizationWriteContext(Actor, Tenant, At)));

        Assert.Equal(EntityValidationException.SchemaUnknown, refusal.ReasonCode);
        Assert.Null(await fixture.Store.GetAsync(new EntityId("entity", "test", "unknown-schema")));
    }

    [Fact(DisplayName = "Ticket 151 (d): an unauthorized caller with an invalid body is refused by the GATE, not the validator")]
    public async Task Create_WhenDeniedAndInvalid_RefusesAtTheGateFirst()
    {
        var fixture = await BuildAsync(allowed: false);
        using var body = JsonDocument.Parse("""{"name":""}""");

        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () =>
            await fixture.Writer.CreateAsync(
                fixture.Schema, body, Options("denied"), new AuthorizationWriteContext(Actor, Tenant, At)));

        Assert.Null(await fixture.Store.GetAsync(new EntityId("entity", "test", "denied")));
    }

    [Fact(DisplayName = "Ticket 151 (e): the headless coordinator face runs the same validation stage")]
    public async Task HeadlessCoordinator_RunsTheSameValidator()
    {
        var fixture = await BuildAsync();
        using var invalid = JsonDocument.Parse("""{"name":"headless"}""");

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await fixture.Headless.CreateAsync(
                fixture.Schema, invalid, Options("headless-invalid"), Actor, Tenant, At));
        Assert.Equal(EntityValidationException.BodyInvalid, refusal.ReasonCode);
        Assert.Null(await fixture.Store.GetAsync(new EntityId("entity", "test", "headless-invalid")));

        using var valid = JsonDocument.Parse("""{"name":"headless","count":1}""");
        var id = await fixture.Headless.CreateAsync(
            fixture.Schema, valid, Options("headless-valid"), Actor, Tenant, At);
        Assert.NotNull(await fixture.Store.GetAsync(id));
    }

    [Fact(DisplayName = "Ticket 151: an update is validated against the STORED record's schema")]
    public async Task Update_ValidatesAgainstTheStoredRecordsSchema()
    {
        var fixture = await BuildAsync();
        var authority = new AuthorizationWriteContext(Actor, Tenant, At);
        using var initial = JsonDocument.Parse("""{"name":"valid","count":3}""");
        var id = await fixture.Writer.CreateAsync(fixture.Schema, initial, Options("updatable"), authority);

        using var invalid = JsonDocument.Parse("""{"name":"valid","count":0}""");
        var refusal = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await fixture.Writer.UpdateAsync(id, invalid, new UpdateOptions(Actor), authority));
        Assert.Equal(EntityValidationException.BodyInvalid, refusal.ReasonCode);
        Assert.Contains("/count", refusal.Pointers);

        using var next = JsonDocument.Parse("""{"name":"valid","count":9}""");
        await fixture.Writer.UpdateAsync(id, next, new UpdateOptions(Actor), authority);
        var stored = await fixture.Store.GetAsync(id);
        Assert.Equal(9, stored!.Body.RootElement.GetProperty("count").GetInt32());
    }

    [Fact(DisplayName = "Ticket 151: the store refuses a token validated against another schema")]
    public async Task Update_WhenTokenCarriesAnotherSchema_IsRefused()
    {
        var fixture = await BuildAsync();
        var authority = new AuthorizationWriteContext(Actor, Tenant, At);
        using var initial = JsonDocument.Parse("""{"name":"valid","count":3}""");
        var id = await fixture.Writer.CreateAsync(fixture.Schema, initial, Options("mismatched"), authority);

        using var body = JsonDocument.Parse("""{"name":"valid","count":4}""");
        var token = TestEntityWritePipeline.Validated(new SchemaId("schema:other"), body);

        var refusal = await Assert.ThrowsAsync<EntityValidationException>(async () =>
            await fixture.Store.UpdateAsync(id, token, new UpdateOptions(Actor)));
        Assert.Equal(EntityValidationException.SchemaUnknown, refusal.ReasonCode);
    }

    [Fact(DisplayName = "Ticket 151: a schema that declares nothing still accepts a well-formed body")]
    public async Task Validate_WhenSchemaDeclaresNothing_Accepts()
    {
        var registry = TestNodeRecordSchemas.NewRegistry();
        var open = (await registry.RegisterAsync("""{"$schema":"https://json-schema.org/draft/2020-12/schema"}""")).Id;
        using var body = JsonDocument.Parse("""{"anything":true}""");

        await new SchemaRegistryEntityValidator(registry).ValidateAsync(open, body);
    }

    [Fact(DisplayName = "Ticket 151: the shipped composition binds the schema-registry validator, not a null object")]
    public void Composition_BindsTheRealValidator()
    {
        var provider = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddHarborlineKernelSchemaRegistry()
            .BuildServiceProvider();

        Assert.IsType<SchemaRegistryEntityValidator>(provider.GetRequiredService<IEntityValidator>());
    }

    [Fact(DisplayName = "Ticket 151: the validation stage cannot run before an allowed decision")]
    public async Task Admission_WithoutAnAllowedDecision_MintsNothing()
    {
        var registry = TestNodeRecordSchemas.NewRegistry();
        var schema = (await registry.RegisterAsync(RecordSchemaText)).Id;
        var admission = new EntityBodyAdmission(new SchemaRegistryEntityValidator(registry));
        using var body = JsonDocument.Parse("""{"name":"valid","count":3}""");

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await admission.AdmitAsync(TestWriteAdmission.Denied, schema, body));
    }
}
