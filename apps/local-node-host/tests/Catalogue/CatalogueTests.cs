using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Catalogue;

public sealed class CatalogueTests
{
    [Fact]
    public void Every_Pack_Content_Kind_Has_A_Sealed_Platform_System_Record_Type()
    {
        var expected = Enum.GetValues<PackContentKind>();
        var actualKinds = SystemRecordType.All.Select(type => type.Kind).ToArray();

        Assert.Empty(expected.Except(actualKinds));
        Assert.Equal(expected.Length, SystemRecordType.All.Count);
        Assert.Equal(expected.OrderBy(kind => kind), actualKinds.OrderBy(kind => kind));
        Assert.All(SystemRecordType.All, type =>
        {
            Assert.True(type.Sealed);
            Assert.Equal("platform", type.Provenance.Kind);
            Assert.Equal("harborline.platform", type.Provenance.PackKey);
        });
    }

    [Fact]
    public void Pack_Item_Claiming_A_Compiled_System_Type_Is_Refused_With_A_Named_Reason()
    {
        var item = new PackSeedItem("FormDefinition", PackContentKind.FormDefinition, "1.0.0", "{}", Cid.FromBytes([]));

        Assert.True(PackSealedSystemTypeAdmission.ClaimsSealedSystemType(item));
        Assert.Equal("pack.projection.sealed_system_type_claim", PackSealedSystemTypeAdmissionCodes.RefusedCode);
    }
}
