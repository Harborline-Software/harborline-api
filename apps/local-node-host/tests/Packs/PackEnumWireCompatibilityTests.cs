using System.Text.Json;

using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackEnumWireCompatibilityTests
{
    // Oracle: api 5b313eaf PackEnums.cs / PackPillar.cs and T-738's 2026-09-29 numbering contract.
    private static readonly (string Name, int Value, int Pillar)[] Kinds =
    [
        ("FormDefinition", 0, 1),
        ("WorkflowDefinition", 1, 2),
        ("StandardsCatalog", 2, 4),
        ("NavWorkspaceConfig", 3, 3),
        ("CascadeDefaults", 4, 5),
        ("AssetTypeDefinition", 5, 0),
        ("TerminologyOverride", 6, 5),
        ("TemplateDefinition", 7, 6),
        ("TaxonomyDefinition", 8, 7),
        ("ReportDefinition", 9, 8),
        ("ViewDefinition", 10, 9),
        ("ScheduleDefinition", 11, 10),
        ("DataExchangeDefinition", 12, 11),
        ("StandingRuleDefinition", 13, 4),
        ("RoleDefinition", 14, 99),
        ("AuthorizationCapabilityBinding", 15, 99),
        ("RecordType", 16, 99),
        ("Layout", 17, 12),
        ("Resource", 18, 13),
        ("Bookable", 19, 13),
        ("ReleasedNavigationDefinition", 21, 3),
    ];

    [Fact]
    public void Content_kind_names_and_numbers_keep_the_literal_wire_contract_without_aliases()
    {
        var actual = Enum.GetNames<PackContentKind>()
            .Select(name => (name, (int)Enum.Parse<PackContentKind>(name))).ToArray();

        Assert.Equal(Kinds.Select(row => (row.Name, row.Value)), actual);
        Assert.All(actual.GroupBy(row => row.Item2), group => Assert.Single(group));
        Assert.False(Enum.IsDefined((PackContentKind)20)); // Assistance is Reserved, never a member.
    }

    [Fact]
    public void Pillar_names_and_numbers_keep_the_literal_wire_contract_without_aliases()
    {
        (string, int)[] expected =
        [
            ("Records", 0), ("Forms", 1), ("Automations", 2), ("Navigation", 3),
            ("Rules", 4), ("Settings", 5), ("Documents", 6), ("Taxonomy", 7),
            ("Reports", 8), ("Views", 9), ("Scheduling", 10), ("DataExchange", 11),
            ("Layout", 12), ("Booking", 13), ("Other", 99),
        ];
        var actual = Enum.GetNames<PackPillar>()
            .Select(name => (name, (int)Enum.Parse<PackPillar>(name))).ToArray();

        Assert.Equal(expected, actual);
        Assert.All(actual.GroupBy(row => row.Item2), group => Assert.Single(group));
    }

    [Fact]
    public void Every_content_kind_keeps_its_literal_pillar_mapping()
    {
        foreach (var row in Kinds)
            Assert.Equal(row.Pillar, (int)PackPillarMap.ForKind((PackContentKind)row.Value));
    }

    [Fact]
    public void Codec_writes_each_literal_kind_name_and_round_trips_its_number()
    {
        var codec = new PackFileCodec();
        foreach (var row in Kinds)
        {
            var bytes = codec.Encode(new PackFile(null,
                [new PackContentPayload("fixture", (PackContentKind)row.Value, "1.0.0", "e30=")]));
            using var json = JsonDocument.Parse(bytes);
            Assert.Equal(row.Name, json.RootElement.GetProperty("contents")[0].GetProperty("kind").GetString());

            var decoded = Assert.IsType<PackFile>(codec.TryDecode(bytes));
            var item = Assert.Single(decoded.Contents);
            Assert.Equal(row.Value, (int)item.Kind);
            Assert.Equal(row.Name, item.Kind.ToString());
        }
    }
}
