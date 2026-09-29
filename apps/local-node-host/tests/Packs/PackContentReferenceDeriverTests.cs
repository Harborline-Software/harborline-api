using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// K9 (T-152 D7) resolution rules of the one deriver the exporter and the install gate share. These pin the
/// survivors of the ck-2 S5/S6 scoped Stryker run: namespace boundaries, self-namespace references, the
/// longest-prefix match, the null-when-empty contract and deterministic edge order.
/// </summary>
public sealed class PackContentReferenceDeriverTests
{
    private static PackContentSource Type(string key, string? parent) => new(
        key, PackContentKind.AssetTypeDefinition, "1.0.0",
        parent is null ? new JsonObject { ["id"] = key } : new JsonObject { ["id"] = key, ["parentType"] = parent });

    [Fact(DisplayName = "K9: a key that only shares a prefix with the pack's own key is undeclared, not self")]
    public void Bare_prefix_is_not_ownership()
    {
        var edges = PackContentReferenceDeriver.Derive(
            "fleet", [Type("fleet.vehicle", "fleet-ops.truck")], [], out var undeclared);

        Assert.Null(edges);
        var reference = Assert.Single(undeclared);
        Assert.Equal("fleet.vehicle", reference.FromContentKey);
        Assert.Equal("fleet-ops.truck", reference.ToContentKey);
    }

    [Fact(DisplayName = "K9: a reference inside the pack's own namespace is neither an edge nor a refusal")]
    public void Own_namespace_reference_is_intra_app()
    {
        var edges = PackContentReferenceDeriver.Derive(
            "fleet-ops", [Type("fleet-ops.vehicle", "fleet-ops.not-shipped")], [], out var undeclared);

        Assert.Null(edges);
        Assert.Empty(undeclared);
    }

    [Fact(DisplayName = "K9: a reference to a sibling item is intra-app even when its key carries no namespace")]
    public void Sibling_reference_is_intra_app_without_a_namespace()
    {
        var edges = PackContentReferenceDeriver.Derive(
            "estate", [Type("building", "site"), Type("site", null)], [], out var undeclared);

        Assert.Null(edges);
        Assert.Empty(undeclared);
    }

    [Fact(DisplayName = "K9: an undeclared reference yields no edge, and a pack with no edges returns null")]
    public void Undeclared_reference_is_not_also_an_edge()
    {
        var edges = PackContentReferenceDeriver.Derive(
            "fleet-ops", [Type("fleet-ops.vehicle", "core-records.property")], [], out var undeclared);

        Assert.Null(edges);
        Assert.Single(undeclared);
    }

    [Fact(DisplayName = "K9: the longest declared key owns a dotted target, whatever the declaration order")]
    public void Longest_declared_prefix_wins()
    {
        var edges = PackContentReferenceDeriver.Derive(
            "consumer", [Type("consumer.site", "harborline.general.building")],
            ["harborline", "harborline.general"], out var undeclared);

        Assert.Empty(undeclared);
        Assert.Equal("harborline.general", Assert.Single(edges!).ToPackKey);
    }

    [Fact(DisplayName = "K9: edges are ordered by referencing key, not by target package")]
    public void Edges_sort_by_referencing_key_first()
    {
        var edges = PackContentReferenceDeriver.Derive(
            "consumer",
            [Type("consumer.b", "alpha.x"), Type("consumer.a", "beta.x")],
            ["alpha", "beta"], out _);

        Assert.Equal(["consumer.a", "consumer.b"], edges!.Select(edge => edge.FromContentKey).ToArray());
    }

    [Fact(DisplayName = "K9: the deriver refuses a blank own key or null inputs")]
    public void Arguments_are_guarded()
    {
        Assert.Throws<ArgumentException>(() => PackContentReferenceDeriver.Derive(" ", [], [], out _));
        Assert.Throws<ArgumentNullException>(() => PackContentReferenceDeriver.Derive("p", null!, [], out _));
        Assert.Throws<ArgumentNullException>(() => PackContentReferenceDeriver.Derive("p", [], null!, out _));
    }
}
