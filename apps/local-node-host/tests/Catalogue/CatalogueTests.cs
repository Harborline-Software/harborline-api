using System.Text.Json;
using System.Security.Cryptography;

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
    public void Released_platform_export_source_has_the_pinned_fixture_digest()
    {
        using var stream = typeof(PlatformPackPreloadHostedService).Assembly.GetManifestResourceStream(
            "Harborline.Api.LocalNodeHost.Packs.platform-pack.export.json")!;
        Assert.Equal("b3c2d53dde5880e3852fce68e6ad56b65e7aa78aca55ce59185623bdaa10977d",
            Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

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
            "RecordType", "NavWorkspaceConfig", "RoleDefinition", "AuthorizationCapabilityBinding", "ViewDefinition", "FormDefinition", "CascadeDefaults",
        };
        Assert.All(contents, item => Assert.Contains(item.GetProperty("kind").GetString(), allowedKinds));
        Assert.DoesNotContain(contents, item => item.GetProperty("kind").GetString() is
            "WorkflowDefinition" or "ProtocolDefinition" or "AssetTypeDefinition");
        Assert.Equal(new[] { "platform.detail.form", "platform.pack.author" }, contents.Where(item => item.GetProperty("kind").GetString() == "FormDefinition").Select(item => item.GetProperty("key").GetString()));
        Assert.Equal(2, contents.Count(item => item.GetProperty("kind").GetString() == "RoleDefinition"));
        Assert.Equal(new[] { "platform.binding.catalogue-read", "platform.binding.records-read" },
            contents.Where(item => item.GetProperty("kind").GetString() == "AuthorizationCapabilityBinding")
                .Select(item => item.GetProperty("key").GetString()));
        Assert.Equal(39, contents.Count(item => item.GetProperty("kind").GetString() == "ViewDefinition"));
        Assert.Equal("platform.defaults.pack-author", Assert.Single(contents, item => item.GetProperty("kind").GetString() == "CascadeDefaults").GetProperty("key").GetString());
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
        Assert.Equal("First title", firstPlan.Bindings.GetProperty("overlay").GetProperty("title").GetString());
        Assert.Equal("Changed title", changedPlan.Bindings.GetProperty("overlay").GetProperty("title").GetString());
        Assert.True(firstPlan.Bindings.GetProperty("fields").GetProperty("name").GetProperty("required").GetBoolean());
        Assert.NotEqual(firstPlan.DefinitionHash, siblingPlan!.DefinitionHash);
        Assert.Equal(siblingPlan.DefinitionHash, CompileAgain(sibling).DefinitionHash);
    }

    [Fact]
    public void View_actions_compile_only_declared_operations_and_unique_identities()
    {
        foreach (var operation in new[] { "pack.validate", "pack.export", "pack.verify", "pack.install", "pack.activate", "record.create", "record.read" })
        {
            var item = ActionView(operation, duplicate: false);
            var plan = CompileAgain(item);
            Assert.Equal("run", plan.Bindings.GetProperty("actions")[0].GetProperty("id").GetString());
            Assert.Equal("Run", plan.Bindings.GetProperty("actions")[0].GetProperty("label").GetString());
            Assert.Equal(operation, plan.Bindings.GetProperty("parameters").GetProperty("actions")[0].GetProperty("operation").GetString());
        }
        foreach (var item in new[] { ActionView("shell.execute", false), ActionView("pack.export", true) })
        {
            Assert.False(RenderPlanCompiler.TryCompile(item, "pack", "1.0.0", out var plan, out var code));
            Assert.Null(plan);
            Assert.Equal(PackRenderPlanCodes.BindingUnresolved, code);
        }
    }

    private static PackSeedItem ActionView(string operation, bool duplicate)
    {
        var action = new { id = "run", label = "Run", operation };
        var body = JsonSerializer.Serialize(new
        {
            viewKind = "views.entity-list/grid",
            parameters = new { entityType = "FormDefinition", actions = duplicate ? new[] { action, action } : new[] { action } },
        });
        return new PackSeedItem("actions", PackContentKind.ViewDefinition, "1.0.0", body, Cid.FromBytes([]));
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
