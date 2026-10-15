using System.Text;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Trust;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Review follow-up to PR 397: <see cref="PackInterfaceRequirementCheck.FindUnmet"/> reads
/// <c>definitionEnvelope.requires</c> and the producer's <c>envelope.requires</c> with one rule for a capability that
/// is not a string: it states no requirement. Before, a number, boolean, object or array capability under
/// <c>definitionEnvelope</c> threw <see cref="InvalidOperationException"/> out of activation, while the same entry
/// under <c>envelope</c> was skipped. A body or <c>definitionEnvelope</c> that is not an object threw the same way.
/// </summary>
public sealed class PackInterfaceRequirementCheckTests
{
    private static readonly DateTimeOffset Installed = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("""{"capability":"acme.ledger@1"}""")]
    [InlineData("""["acme.ledger@1"]""")]
    [InlineData("null")]
    public void Non_string_capability_states_no_requirement_under_either_envelope(string capability)
    {
        var target = Pack(
            ("consumer/definition", $$$"""{"definitionEnvelope":{"requires":[{"capability":{{{capability}}} }]}}"""),
            ("consumer/producer", $$$"""{"envelope":{"requires":[{"capability":{{{capability}}} }]}}"""));

        Assert.Empty(PackInterfaceRequirementCheck.FindUnmet(target, []));
    }

    [Theory]
    [InlineData("""{"definitionEnvelope":7}""")]
    [InlineData("""{"definitionEnvelope":"legacy"}""")]
    [InlineData("""{"definitionEnvelope":[{"requires":[{"capability":"acme.ledger@1"}]}]}""")]
    [InlineData("""{"definitionEnvelope":{"requires":{"capability":"acme.ledger@1"}}}""")]
    [InlineData("""[{"definitionEnvelope":{"requires":[{"capability":"acme.ledger@1"}]}}]""")]
    [InlineData("\"acme.ledger@1\"")]
    public void Non_object_body_or_definition_envelope_states_no_requirement(string body)
    {
        Assert.Empty(PackInterfaceRequirementCheck.FindUnmet(Pack(("consumer/alpha", body)), []));
    }

    [Fact]
    public void Non_string_capability_is_skipped_and_a_well_formed_requirement_beside_it_is_still_read()
    {
        var target = Pack(("consumer/alpha",
            """{"definitionEnvelope":{"requires":[{"capability":7},{"capability":{}},{"capability":"acme.ledger@1"},{"capability":[1]}]}}"""));

        var unmet = Assert.Single(PackInterfaceRequirementCheck.FindUnmet(target, []));

        Assert.Equal(new PackUnmetInterfaceRequirement("consumer/alpha", "acme.ledger", 1), unmet);
    }

    private static InstalledPack Pack(params (string Key, string Json)[] items) => new(
        "tenant.consumer", "1.0.0", PackScopeTier.Vertical, PackLifecycleState.Draft,
        items.Select(item => new PackSeedItem(item.Key, PackContentKind.AssetTypeDefinition, "1", item.Json,
            Cid.FromBytes(Encoding.UTF8.GetBytes(item.Json)))).ToArray(),
        new Dictionary<string, int>(), Installed, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
        TrustScope.OwnRoster, Array.Empty<PackDependencyRef>());
}
