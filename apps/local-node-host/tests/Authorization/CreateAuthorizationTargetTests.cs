using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>DES-0029 ck-3: a create without an explicit id is authorized on the id the store writes
/// (the schema-derived id), never on the caller's nonce.</summary>
public sealed class CreateAuthorizationTargetTests
{
    [Fact(DisplayName = "ck-3: a nonce-only create is authorized on the derived id it is stored under")]
    public async Task Nonce_only_create_is_authorized_on_the_stored_id()
    {
        var at = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("ck3");
        var actor = new ActorId("operator");
        var schema = new SchemaId("ck3-schema");
        var options = new CreateOptions("record", "tenant", "ck3-nonce", actor, tenant);
        var derived = InMemoryEntityStore.DeriveEntityId(schema, options);
        var targets = new List<string>();
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        // The grant is scoped to the derived id only: a nonce-scoped target is denied.
        var writer = new NodeEntityWriter(
            Substitute.For<IDbContextFactory<LocalNodeDbContext>>(),
            entities,
            NullEntityValidator.Instance,
            TestAuthorization.Gate(
                request => request.Target.RecordId == derived.LocalPart,
                request => targets.Add(request.Target.RecordId)));
        using var body = JsonDocument.Parse("{\"name\":\"ck3\"}");

        var created = await writer.CreateAsync(schema, body, options, new AuthorizationWriteContext(actor, tenant, at));

        Assert.Equal(derived, created);
        Assert.Equal([created.LocalPart], targets);
        Assert.NotNull(await entities.GetAsync(created));

        using var coordinatorBody = JsonDocument.Parse("{\"name\":\"ck3\"}");
        var viaCoordinator = await ((IEntityWriteCoordinator)writer).CreateAsync(
            schema, coordinatorBody, options, actor, tenant, at);
        Assert.Equal(derived, viaCoordinator);
        Assert.Equal([created.LocalPart, created.LocalPart], targets);
    }

    [Fact(DisplayName = "ck-3: prepared options whose derived id differs from the admitted id are refused")]
    public async Task Prepared_options_must_derive_the_admitted_id()
    {
        var at = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("ck3");
        var actor = new ActorId("operator");
        var schema = new SchemaId("ck3-schema");
        // Admitted on the nonce, but a nonce-only create is stored under the derived id.
        var options = new CreateOptions("record", "tenant", "ck3-nonce", actor, tenant);
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var writer = new NodeEntityWriter(
            Substitute.For<IDbContextFactory<LocalNodeDbContext>>(),
            entities,
            NullEntityValidator.Instance,
            TestAuthorization.Gate(true));
        using var body = JsonDocument.Parse("{\"name\":\"ck3\"}");

        await Assert.ThrowsAsync<ArgumentException>(async () => await writer.CreateWithReceiptAsync(
            body, options.Nonce, new AuthorizationWriteContext(actor, tenant, at),
            _ => ValueTask.FromResult((schema, options))));
        Assert.Null(await entities.GetAsync(InMemoryEntityStore.DeriveEntityId(schema, options)));
    }
}
