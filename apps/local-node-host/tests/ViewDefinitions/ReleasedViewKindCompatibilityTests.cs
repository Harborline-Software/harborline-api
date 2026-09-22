using System.Text.Json;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

namespace Harborline.Api.LocalNodeHost.Tests.ViewDefinitions;

public sealed class ReleasedViewKindCompatibilityTests
{
    [Fact]
    public void Exact_released_predecessor_projects_the_canonical_kind_without_mutating_its_seed()
    {
        var seed = View("views.entity-list/grid");
        var originalJson = seed.CanonicalJson;
        var originalAddress = seed.ContentAddress;

        var projected = ReleasedViewKindCompatibility.Project(
            "harborline.access-administration", "1.1.3", seed);

        Assert.Equal("layout.table", ReadKind(projected));
        Assert.NotEqual(originalAddress, projected.ContentAddress);
        Assert.Equal(originalJson, seed.CanonicalJson);
        Assert.Equal(originalAddress, seed.ContentAddress);
    }

    [Fact]
    public void Unknown_pack_version_receives_no_retired_kind_compatibility()
    {
        var seed = View("views.entity-list/grid");

        var projected = ReleasedViewKindCompatibility.Project(
            "harborline.access-administration", "9.9.9", seed);

        Assert.Same(seed, projected);
        Assert.Equal("views.entity-list/grid", ReadKind(projected));
    }

    [Fact]
    public void Canonical_kind_is_not_rewritten_even_for_a_released_predecessor()
    {
        var seed = View("layout.table");

        var projected = ReleasedViewKindCompatibility.Project(
            "harborline.platform", "1.6.0", seed);

        Assert.Same(seed, projected);
    }

    private static PackSeedItem View(string viewKind)
    {
        var json = JsonSerializer.Serialize(new
        {
            key = "access.holders",
            version = "1.0.2",
            tenant = "bootstrap",
            schemaVersion = 1,
            viewKind,
            title = "Access holders",
            parameters = new { entityType = "AccessGrant" },
        });
        return new PackSeedItem(
            "access.holders",
            PackContentKind.ViewDefinition,
            "1.0.2",
            json,
            Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(json)));
    }

    private static string? ReadKind(PackSeedItem item)
    {
        using var document = JsonDocument.Parse(item.CanonicalJson);
        return document.RootElement.GetProperty("viewKind").GetString();
    }
}
