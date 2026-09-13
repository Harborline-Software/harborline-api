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
        Assert.Equal("pack.projection.sealed_system_type_claim", PackSealedSystemTypeAdmissionCodes.RefusedCode);
    }

    [Fact]
    public void Only_the_platform_pack_may_catalogue_a_compiled_system_type()
    {
        var item = new PackSeedItem("FormDefinition", PackContentKind.RecordType, "1.0.0", "{}", Cid.FromBytes([]));

        Assert.True(PackSealedSystemTypeAdmission.IsPermittedPlatformCatalogueItem("harborline.platform", item));
        Assert.False(PackSealedSystemTypeAdmission.IsPermittedPlatformCatalogueItem("example.other", item));
    }

    [Fact(DisplayName = "402.4: a definition-only change changes only that definition's render-plan hash")]
    public void Definition_hash_is_independent_of_the_derived_render_plan()
    {
        var first = FormItem("first", "First title");
        var changed = FormItem("first", "Changed title");
        var sibling = FormItem("sibling", "Sibling title");

        Assert.True(RenderPlanCompiler.TryCompile(first, "pack", "1.0.0", out var firstPlan, out _));
        Assert.True(RenderPlanCompiler.TryCompile(changed, "pack", "1.0.0", out var changedPlan, out _));
        Assert.True(RenderPlanCompiler.TryCompile(sibling, "pack", "1.0.0", out var siblingPlan, out _));

        Assert.NotEqual(firstPlan!.DefinitionHash, changedPlan!.DefinitionHash);
        Assert.NotEqual(firstPlan.DefinitionHash, siblingPlan!.DefinitionHash);
        Assert.Equal(siblingPlan.DefinitionHash, CompileAgain(sibling).DefinitionHash);
    }

    private static RenderPlan CompileAgain(PackSeedItem item)
    {
        Assert.True(RenderPlanCompiler.TryCompile(item, "pack", "1.0.0", out var plan, out _));
        return plan!;
    }

    private static PackSeedItem FormItem(string id, string title)
    {
        var body = JsonSerializer.Serialize(new
        {
            overlay = new { title },
            fieldsMeta = new Dictionary<string, object>
            {
                ["name"] = new { type = "text", required = true },
            },
        });
        return new PackSeedItem(id, PackContentKind.FormDefinition, "1.0.0", body, Cid.FromBytes([]));
    }
}
