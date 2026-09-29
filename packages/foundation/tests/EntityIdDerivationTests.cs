using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Foundation.Assets.Common;

namespace Harborline.Api.Foundation.Tests;

public sealed class EntityIdDerivationTests
{
    [Fact(DisplayName = "records-eng-28: explicit local parts cannot impersonate schema-derived ids")]
    public void Explicit_local_part_cannot_equal_another_record_types_derived_id()
    {
        var options = new CreateOptions(
            "record", "tenant", "same-nonce", new ActorId("issuer"), new TenantId("tenant"));
        var derived = InMemoryEntityStore.DeriveEntityId(new SchemaId("first-record-schema"), options);

        Assert.Throws<ArgumentException>(() => InMemoryEntityStore.DeriveEntityId(
            new SchemaId("second-record-schema"), options with { ExplicitLocalPart = derived.LocalPart }));
    }

    [Theory]
    [InlineData("fb17d763-c523-43b2-9718-c9eec3a75939")]
    [InlineData("fb17d763c52343b29718c9eec3a75939")]
    [InlineData("fb17d763c52343b29718c9eec3a75939fb17d763c52343b29718c9eec3a75939")]
    public void Existing_explicit_id_formats_remain_usable(string localPart)
    {
        var options = new CreateOptions("record", "tenant", "nonce", new ActorId("issuer"),
            new TenantId("tenant"), ExplicitLocalPart: localPart);

        Assert.Equal(localPart, InMemoryEntityStore.DeriveEntityId(new SchemaId("schema"), options).LocalPart);
    }

    [Fact(DisplayName = "T-970: every derived local part is refused as an explicit local part")]
    public void Every_derived_local_part_is_reserved()
    {
        for (var i = 0; i < 512; i++)
        {
            var options = new CreateOptions("record", "tenant", $"nonce-{i}", new ActorId("issuer"), new TenantId("tenant"));
            var derived = InMemoryEntityStore.DeriveEntityId(new SchemaId($"schema-{i % 7}"), options);
            Assert.Throws<ArgumentException>(() => InMemoryEntityStore.DeriveEntityId(
                new SchemaId("other-schema"), options with { ExplicitLocalPart = derived.LocalPart }));
        }
    }

    [Theory(DisplayName = "T-970: explicit local parts outside the derived shape stay usable")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaa")]    // 25 characters
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaa")]  // 27 characters
    [InlineData("Aaaaaaaaaaaaaaaaaaaaaaaaaa")]   // not lowercase Base32
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaa1a")]   // '1' is not Base32
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaa8a")]   // '8' is not Base32
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaab")]   // final character cannot end a 128-bit digest
    public void Near_miss_explicit_local_parts_are_accepted(string localPart)
    {
        var options = new CreateOptions("record", "tenant", "nonce", new ActorId("issuer"),
            new TenantId("tenant"), ExplicitLocalPart: localPart);

        Assert.Equal(localPart, InMemoryEntityStore.DeriveEntityId(new SchemaId("schema"), options).LocalPart);
    }
}
