using System.Text.Json;

using Harborline.Api.Foundation.Assets;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;

namespace Harborline.Api.LocalNodeHost.Tests.Entities;

/// <summary>
/// Ticket 366 slice 1 — the behaviour of the record-write token: the mint's ordering guard, and the
/// store's refusal of a token minted against a schema that is not the stored record's (RW-H2, H10).
/// </summary>
public sealed class ValidatedRecordBodyAdmissionTests
{
    private static readonly SchemaId Thing = new("thing");
    private static readonly SchemaId Other = new("other");

    [Fact(DisplayName = "Ticket 366: the mint refuses before validating when the gate did not allow (holds RW-1 RW-2)")]
    public async Task MintRefusesWithoutAnAllowedDecision()
    {
        var validator = new CountingValidator();
        using var body = JsonDocument.Parse("""{"a":1}""");

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ValidatedRecordBody.AdmitAsync(validator, new Admission(false), Thing, body));

        Assert.Contains("entity.validation.not_admitted", refusal.Message, StringComparison.Ordinal);
        // The ordering is the point: the validator must not have run for a write the gate refused.
        Assert.Equal(0, validator.Calls);
    }

    [Fact(DisplayName = "Ticket 366: the mint validates, then the token carries that schema (holds RW-2 RW-9)")]
    public async Task MintValidatesAndCarriesTheSchema()
    {
        var validator = new CountingValidator();
        using var body = JsonDocument.Parse("""{"a":1}""");

        var token = await ValidatedRecordBody.AdmitAsync(validator, new Admission(true), Thing, body);

        Assert.Equal(1, validator.Calls);
        Assert.Equal(Thing, token.Schema);
        Assert.Same(body, token.Body);
    }

    [Fact(DisplayName = "Ticket 366: the store refuses an update token minted against another schema (holds RW-2 RW-9)")]
    public async Task StoreRefusesATokenFromAnotherSchema()
    {
        var validator = new CountingValidator();
        var store = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System, validator);
        using var body = JsonDocument.Parse("""{"a":1}""");
        var allowed = new Admission(true);

        var created = await ValidatedRecordBody.AdmitAsync(validator, allowed, Thing, body);
        var id = await store.CreateAsync(
            created,
            new CreateOptions("record", "test", Guid.NewGuid().ToString("N"),
                new ActorId("actor"), new TenantId("tenant")));

        // A body the validator accepted — against the WRONG schema. This is the shape that broke the
        // competition candidate: a non-record admission must never reach a record's stored schema.
        var mismatched = await ValidatedRecordBody.AdmitAsync(validator, allowed, Other, body);
        var refusal = await Assert.ThrowsAsync<EntityValidationException>(() =>
            store.UpdateAsync(id, mismatched, new UpdateOptions(new ActorId("actor"))));

        Assert.Equal("entity.validation.schema_mismatch", refusal.ReasonCode);

        // …and the refusal persisted nothing: the record still reads at its first version.
        var stored = await store.GetAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(Thing, stored.Schema);
        Assert.Equal(1, stored.CurrentVersion.Sequence);
    }

    [Fact(DisplayName = "Ticket 366: a token for the record's own schema updates it (holds RW-2)")]
    public async Task StoreAcceptsATokenForTheRecordsOwnSchema()
    {
        var validator = new CountingValidator();
        var store = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System, validator);
        using var first = JsonDocument.Parse("""{"a":1}""");
        using var second = JsonDocument.Parse("""{"a":2}""");
        var allowed = new Admission(true);

        var id = await store.CreateAsync(
            await ValidatedRecordBody.AdmitAsync(validator, allowed, Thing, first),
            new CreateOptions("record", "test", Guid.NewGuid().ToString("N"),
                new ActorId("actor"), new TenantId("tenant")));
        await store.UpdateAsync(
            id,
            await ValidatedRecordBody.AdmitAsync(validator, allowed, Thing, second),
            new UpdateOptions(new ActorId("actor")));

        var stored = await store.GetAsync(id);
        Assert.NotNull(stored);
        Assert.Equal(2, stored.CurrentVersion.Sequence);
        Assert.Equal(2, stored.Body.RootElement.GetProperty("a").GetInt32());
    }

    private sealed record Admission(bool IsAllowed) : IWriteAdmission;

    private sealed class CountingValidator : IEntityValidator
    {
        internal int Calls { get; private set; }

        public Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }
}
