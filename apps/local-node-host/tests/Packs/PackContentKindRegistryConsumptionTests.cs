using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Foundation.Definitions;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackContentKindRegistryConsumptionTests
{
    [Fact]
    public void Pinned_registry_matches_its_actual_embedded_checked_in_export()
    {
        Assert.True(PackContentKindRegistry.VerifyCheckedInExport());
    }

    [Fact]
    public void Api_content_kind_names_and_values_match_all_pinned_shipped_rows_without_aliases()
    {
        var expected = PackContentKindRegistry.Kinds
            .Where(row => row.Status == PackContentKindStatus.Shipped)
            .Select(row => (row.Name, row.Value))
            .OrderBy(row => row.Value)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .ToArray();
        var actual = Enum.GetNames<PackContentKind>()
            .Select(name => (Name: name, Value: (int)Enum.Parse<PackContentKind>(name)))
            .OrderBy(row => row.Value)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expected);
        Assert.All(expected.GroupBy(row => row.Name, StringComparer.Ordinal), group => Assert.Single(group));
        Assert.All(expected.GroupBy(row => row.Value), group => Assert.Single(group));
        Assert.All(actual.GroupBy(row => row.Value), group => Assert.Single(group));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Api_pillar_names_and_values_match_the_complete_pinned_pillar_set_without_aliases()
    {
        var expected = PackContentKindRegistry.Pillars
            .Select(row => (row.Name, row.Value))
            .OrderBy(row => row.Value)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .ToArray();
        var actual = Enum.GetNames<PackPillar>()
            .Select(name => (Name: name, Value: (int)Enum.Parse<PackPillar>(name)))
            .OrderBy(row => row.Value)
            .ThenBy(row => row.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(expected);
        Assert.All(expected.GroupBy(row => row.Name, StringComparer.Ordinal), group => Assert.Single(group));
        Assert.All(expected.GroupBy(row => row.Value), group => Assert.Single(group));
        Assert.All(actual.GroupBy(row => row.Value), group => Assert.Single(group));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Every_shipped_kind_uses_its_pinned_pillar_in_the_map_and_actual_admission_classifier()
    {
        var shipped = PackContentKindRegistry.Kinds
            .Where(row => row.Status == PackContentKindStatus.Shipped)
            .Reverse()
            .ToArray();
        var expectedPillars = PackContentKindRegistry.Pillars
            .ToDictionary(row => row.Name, row => row.Value, StringComparer.Ordinal);
        var items = shipped
            .Select(row => Item($"fixtures.registry/{row.Name}", row.Value))
            .ToArray();

        var destinations = PackDestinationClassifier.Classify(items);

        Assert.Equal(items.Select(item => item.Key), destinations.Select(destination => destination.ContentKey));
        Assert.Equal(shipped.Length, destinations.Count);
        for (var index = 0; index < shipped.Length; index++)
        {
            var expectedPillar = expectedPillars[shipped[index].Pillar];
            Assert.Equal(expectedPillar, (int)PackPillarMap.ForKind(items[index].Kind));
            Assert.Equal(expectedPillar, (int)destinations[index].Pillar);
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 3)]
    [InlineData(4, 5)]
    [InlineData(5, 0)]
    [InlineData(6, 5)]
    [InlineData(7, 6)]
    [InlineData(8, 7)]
    [InlineData(9, 8)]
    [InlineData(10, 9)]
    [InlineData(11, 10)]
    [InlineData(12, 11)]
    [InlineData(13, 4)]
    [InlineData(14, 99)]
    [InlineData(15, 99)]
    [InlineData(16, 99)]
    [InlineData(17, 12)]
    [InlineData(18, 13)]
    [InlineData(19, 13)]
    public void Authoritative_shipped_numbering_keeps_the_literal_pillar(int kindValue, int pillarValue)
    {
        Assert.Equal(pillarValue, (int)PackPillarMap.ForKind((PackContentKind)kindValue));
        var destination = Assert.Single(PackDestinationClassifier.Classify(
            [Item("fixtures.registry/numbered", kindValue)]));
        Assert.Equal("fixtures.registry/numbered", destination.ContentKey);
        Assert.Equal(pillarValue, (int)destination.Pillar);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(100000)]
    public void Reserved_and_undefined_values_stay_other_and_contribution(int kindValue)
    {
        var kind = (PackContentKind)kindValue;
        Assert.False(Enum.IsDefined(kind));
        Assert.Equal(99, (int)PackPillarMap.ForKind(kind));
        Assert.Equal("contribution", PackPillarMap.GenericNoun(kind));

        var destination = Assert.Single(PackDestinationClassifier.Classify(
            [Item("fixtures.registry/fallback", kindValue)]));
        Assert.Equal("fixtures.registry/fallback", destination.ContentKey);
        Assert.Equal(99, (int)destination.Pillar);
    }

    [Theory]
    [InlineData(5, "recordType")]
    [InlineData(0, "form")]
    [InlineData(9, "report")]
    [InlineData(16, "contribution")]
    public void Existing_generic_nouns_remain_literal_tokens(int kindValue, string noun)
    {
        Assert.Equal(noun, PackPillarMap.GenericNoun((PackContentKind)kindValue));
    }

    private static PackContentItem Item(string key, int kindValue)
    {
        byte[] body = "{}"u8.ToArray();
        return new PackContentItem(key, (PackContentKind)kindValue, "1.0.0", body, Cid.FromBytes(body));
    }
}
