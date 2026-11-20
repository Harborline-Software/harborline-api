using System.Text;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class PackSafetyFloorsTests
{
    [Fact(DisplayName = "rules-ck-2: installation unions only catalog floors by elementwise maximum in either order")]
    public void ExtractFloors_UnionsCatalogFloorsByMaximumInEitherOrder()
    {
        var first = Item("first", PackContentKind.StandardsCatalog,
            """{"safetyFloors":{"shared":1,"firstOnly":2}}""");
        var second = Item("second", PackContentKind.StandardsCatalog,
            """{"safetyFloors":{"shared":3,"secondOnly":4}}""");
        var foreign = Item("foreign", PackContentKind.FormDefinition,
            """{"safetyFloors":{"shared":9,"foreignOnly":9}}""");

        // DES-0018 rules-ck-2, instantiated by T-588/installation-floor-union:
        // literal values and the count pin the complete dictionary independently of the producer.
        AssertUnion(PackSafetyFloors.ExtractFloors([first, second, foreign]));
        AssertUnion(PackSafetyFloors.ExtractFloors([second, first, foreign]));
        Assert.Empty(PackSafetyFloors.ExtractFloors([]));
        Assert.Empty(PackSafetyFloors.ExtractFloors([foreign]));
    }

    private static void AssertUnion(IReadOnlyDictionary<string, int> actual)
    {
        Assert.Equal(3, actual.Count);
        Assert.Equal(3, actual["shared"]);
        Assert.Equal(2, actual["firstOnly"]);
        Assert.Equal(4, actual["secondOnly"]);
    }

    private static PackContentItem Item(string key, PackContentKind kind, string json)
        => new(key, kind, "1.0.0", Encoding.UTF8.GetBytes(json), Cid.FromBytes([]));
}
