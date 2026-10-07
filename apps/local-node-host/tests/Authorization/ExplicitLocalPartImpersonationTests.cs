using System.Text.Json;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.LocalNodeHost.Data;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.Authorization;

/// <summary>T-970: the production record writer cannot pre-claim another record type's server-minted id,
/// so the records:read gate's <c>/records/{localPart}</c> scope keeps naming one record.</summary>
public sealed class ExplicitLocalPartImpersonationTests
{
    [Fact(DisplayName = "T-970: an explicit local part cannot squat another record type's derived id through NodeEntityWriter")]
    public async Task Explicit_local_part_cannot_squat_a_derived_id_of_another_record_type()
    {
        var at = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var tenant = new TenantId("t970");
        var actor = new ActorId("operator");
        var entities = new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System);
        var writer = new NodeEntityWriter(
            Substitute.For<IDbContextFactory<LocalNodeDbContext>>(),
            entities,
            NullEntityValidator.Instance,
            TestAuthorization.Gate(true));
        var authority = new AuthorizationWriteContext(actor, tenant, AdmittedInstant.FromRecordedAct(at));
        var victimOptions = new CreateOptions("record", "tenant", "victim-nonce", actor, tenant);
        var victimId = InMemoryEntityStore.DeriveEntityId(new SchemaId("victim-schema"), victimOptions);
        using var squatBody = JsonDocument.Parse("{\"name\":\"squat\"}");
        using var victimBody = JsonDocument.Parse("{\"name\":\"victim\"}");

        // Different scheme: the store key differs, but the gate scope /records/{localPart} would not.
        await Assert.ThrowsAsync<ArgumentException>(async () => await writer.CreateAsync(
            new SchemaId("squat-schema"), squatBody,
            new CreateOptions("other", "tenant", "squat", actor, tenant, ExplicitLocalPart: victimId.LocalPart),
            authority));
        Assert.Null(await entities.GetAsync(new EntityId("other", "tenant", victimId.LocalPart)));

        Assert.Equal(victimId, await writer.CreateAsync(new SchemaId("victim-schema"), victimBody, victimOptions, authority));
        Assert.Equal(new SchemaId("victim-schema"), (await entities.GetAsync(victimId))!.Schema);
    }
}
