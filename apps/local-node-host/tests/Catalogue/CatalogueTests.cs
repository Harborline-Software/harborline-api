using System.Text.Json;

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
    public void Platform_export_carries_only_catalogue_bootstrap_content()
    {
        using var stream = typeof(PlatformPackPreloadHostedService).Assembly.GetManifestResourceStream(
            "Harborline.Api.LocalNodeHost.Packs.platform-pack.export.json")!;
        using var document = JsonDocument.Parse(stream);
        var contents = document.RootElement.GetProperty("contents").EnumerateArray().ToArray();

        Assert.Equal("harborline.platform", document.RootElement.GetProperty("key").GetString());
        Assert.Equal(16, contents.Count(item => item.GetProperty("kind").GetString() == "RecordType"));
        var allowedKinds = new[]
        {
            "RecordType", "NavWorkspaceConfig", "RoleDefinition", "AuthorizationCapabilityBinding",
        };
        Assert.All(contents, item => Assert.Contains(item.GetProperty("kind").GetString(), allowedKinds));
        Assert.DoesNotContain(contents, item => item.GetProperty("kind").GetString() is
            "FormDefinition" or "WorkflowDefinition" or "ProtocolDefinition" or "AssetTypeDefinition");
        Assert.Equal(2, contents.Count(item => item.GetProperty("kind").GetString() == "RoleDefinition"));
        Assert.Equal(2, contents.Count(item => item.GetProperty("kind").GetString() == "AuthorizationCapabilityBinding"));
    }

    [Fact]
    public void Every_Pack_Content_Kind_Has_A_Sealed_Platform_System_Record_Type()
    {
        var expected = Enum.GetValues<PackContentKind>()
            .Where(kind => kind != PackContentKind.RecordType)
            .ToArray();
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
        var item = new PackSeedItem("FormDefinition", PackContentKind.RecordType, "1.0.0", "{}", Cid.FromBytes([]));

        Assert.True(PackSealedSystemTypeAdmission.ClaimsSealedSystemType(item));
        Assert.Equal("pack.projection.sealed_system_type_claim", PackSealedSystemTypeAdmission.RefusedCode);
    }

    [Fact]
    public void Only_the_platform_pack_may_catalogue_a_compiled_system_type()
    {
        var item = new PackSeedItem("FormDefinition", PackContentKind.RecordType, "1.0.0", "{}", Cid.FromBytes([]));

        Assert.True(PackSealedSystemTypeAdmission.IsPermittedPlatformCatalogueItem("harborline.platform", item));
        Assert.False(PackSealedSystemTypeAdmission.IsPermittedPlatformCatalogueItem("example.other", item));
    }
}
