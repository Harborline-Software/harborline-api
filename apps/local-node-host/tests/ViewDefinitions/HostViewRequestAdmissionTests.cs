using System.Text.Json;
using System.Text.Json.Nodes;
using Harborline.Api.Blocks.AccessGrant;
using Harborline.Api.Blocks.Assets.Registry.Audit;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.ViewDefinitions;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using NSubstitute;

namespace Harborline.Api.LocalNodeHost.Tests.ViewDefinitions;

public sealed class HostViewRequestAdmissionTests
{
    private readonly HostViewKindDescriptorRegistry descriptors = new(
        new InMemoryEntityTypeRegistry(new InMemoryRegistryAuditLog()),
        Substitute.For<IFormDefinitionStore>(), Substitute.For<ISchemaRegistry>(),
        Harborline.Blocks.EntityViews.ViewKindRegistry.Platform);

    [Fact]
    public async Task Canonical_platform_table_kind_is_admitted()
    {
        await descriptors.AdmitAsync(Definition() with { ViewKind = "layout.table" });
    }

    [Theory]
    [InlineData("views.entity-list/grid")]
    [InlineData("views.not-registered")]
    public async Task Non_platform_kind_is_refused_outside_released_pack_compatibility(string viewKind)
    {
        var error = await Assert.ThrowsAsync<ViewDefinitionGovernanceException>(() =>
            descriptors.AdmitAsync(Definition() with { ViewKind = viewKind }).AsTask());

        Assert.Equal("view_definition.kind_unknown", error.ErrorCode);
    }

    [Fact]
    public async Task Missing_input_form_is_a_stable_governance_refusal()
    {
        var definition = Definition();
        var parameters = JsonNode.Parse(definition.Parameters.GetRawText())!;
        var action = parameters["actions"]![0]!.AsObject();
        action.Remove("input");
        action["inputForm"] = JsonSerializer.SerializeToNode(new { formId = "missing.input", version = "1.0.0" });
        var registry = new HostViewKindDescriptorRegistry(
            new InMemoryEntityTypeRegistry(new InMemoryRegistryAuditLog()),
            new NoopFormDefinitionStore(), Substitute.For<ISchemaRegistry>(),
            Harborline.Blocks.EntityViews.ViewKindRegistry.Platform);
        var error = await Assert.ThrowsAsync<ViewDefinitionGovernanceException>(() => registry.AdmitAsync(
            definition with { Parameters = JsonSerializer.SerializeToElement(parameters) }).AsTask());
        Assert.Equal("view_definition.request_binding_invalid", error.ErrorCode);
    }

    [Fact]
    public async Task Admitted_record_read_emits_the_same_route_and_gate_the_host_executes()
    {
        var definition = Definition();
        await descriptors.AdmitAsync(definition);
        var item = new PackSeedItem("example.holders", PackContentKind.ViewDefinition, "1.0.0",
            JsonSerializer.Serialize(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Cid.FromBytes([]));
        Assert.True(RenderPlanCompiler.TryCompile(item, "example.pack", "1.0.0", out var plan, out var code), code);
        var action = plan!.Bindings.GetProperty("actions")[0];
        var transport = action.GetProperty("dispatch").GetProperty("descriptor");
        Assert.Equal(AssetRegistryRoutes.ReadEntityRequest.RouteTemplate, transport.GetProperty("routeTemplate").GetString());
        Assert.Equal("records:read", transport.GetProperty("authorizationCapability").GetString());
        Assert.Equal("GET", transport.GetProperty("method").GetString());
        Assert.Equal("opaque.example.read", action.GetProperty("operation").GetString());
        Assert.Equal("text", action.GetProperty("input").GetProperty("fields").GetProperty("recordId").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("unknown", "input", "/recordId")]
    [InlineData("records.read.v1", "input", "/missing")]
    [InlineData("records.read.v1", "selection", "/missing")]
    [InlineData("records.read.v1", "actor", "/recordId")]
    public async Task Activation_refuses_unregistered_transports_and_unresolved_source_fields(string id, string source, string pointer)
    {
        var exception = await Assert.ThrowsAsync<ViewDefinitionGovernanceException>(() =>
            descriptors.AdmitAsync(Definition(id, source, pointer)).AsTask());
        Assert.Equal("view_definition.request_binding_invalid", exception.ErrorCode);
    }

    [Fact]
    public async Task A_selected_grant_identity_is_bound_by_field_name_without_row_index_inference()
    {
        await descriptors.AdmitAsync(Definition(source: "selection", pointer: "/grantId"));
    }

    [Theory]
    [InlineData("authorization.grant.review.v1")]
    [InlineData("records.read.v1")]
    [InlineData("authorization.holders.read.v1")]
    [InlineData("unknown")]
    public async Task Data_source_admission_refuses_mutations_non_lists_and_unknown_descriptors(string descriptorId)
    {
        var exception = await Assert.ThrowsAsync<ViewDefinitionGovernanceException>(() =>
            descriptors.AdmitAsync(DataSourceDefinition(descriptorId)).AsTask());
        Assert.Equal("view_definition.request_binding_invalid", exception.ErrorCode);
    }

    [Theory]
    [InlineData("authorization.grant.review.v1")]
    [InlineData("records.read.v1")]
    [InlineData("authorization.holders.read.v1")]
    [InlineData("unknown")]
    public void Data_source_compilation_refuses_mutations_non_lists_and_unknown_descriptors(string descriptorId)
    {
        var item = new PackSeedItem("example.holders", PackContentKind.ViewDefinition, "1.0.0",
            JsonSerializer.Serialize(DataSourceDefinition(descriptorId), JsonSerializerOptions.Web), Cid.FromBytes([]));
        Assert.False(RenderPlanCompiler.TryCompile(item, "example.pack", "1.0.0", out var plan, out var code));
        Assert.Null(plan);
        Assert.Equal(PackRenderPlanCodes.BindingUnresolved, code);
    }

    [Fact]
    public async Task Data_source_admits_and_compiles_registered_read_only_list_metadata()
    {
        var definition = DataSourceDefinition("authorization.holders.selected.read.v1");
        await descriptors.AdmitAsync(definition);
        var item = new PackSeedItem("example.holders", PackContentKind.ViewDefinition, "1.0.0",
            JsonSerializer.Serialize(definition, JsonSerializerOptions.Web), Cid.FromBytes([]));
        Assert.True(RenderPlanCompiler.TryCompile(item, "example.pack", "1.0.0", out var plan, out var code), code);
        var descriptor = plan!.Bindings.GetProperty("dataSource").GetProperty("descriptor");
        Assert.Equal("GET", descriptor.GetProperty("method").GetString());
        Assert.Equal("/rows", descriptor.GetProperty("rowsPointer").GetString());
        Assert.Equal("/grantId", descriptor.GetProperty("rowIdentityPointer").GetString());
    }

    private static ViewDefinition DataSourceDefinition(string descriptorId)
    {
        var bindings = new Dictionary<string, object>();
        if (descriptorId is "authorization.grant.review.v1" or "records.read.v1")
        {
            bindings[descriptorId == "records.read.v1" ? "id" : "target"] = new { literal = "example-target" };
            bindings["correlationId"] = new { literal = "43300000-0000-4000-8000-000000000101" };
        }
        return Definition() with { Parameters = JsonSerializer.SerializeToElement(new
        {
            entityType = "AccessGrant", fields = new[] { new { id = "grantId", label = "Grant" } },
            dataSource = new { schemaVersion = 1, kind = "request", descriptorId, bindings },
            actions = Array.Empty<object>(),
        }) };
    }

    [Theory]
    [InlineData("authorization.grant.revoke.v1", false)]
    [InlineData("authorization.grant.review.v1", false)]
    [InlineData("authorization.grant.narrow-scope.v1", true)]
    public async Task Grant_actions_compile_only_host_owned_selected_session_enforcement(string descriptorId, bool narrow)
    {
        var bindings = new Dictionary<string, object> { ["target"] = new { source = "selection", pointer = "/grantId" } };
        bindings["correlationId"] = new { source = "invocation", pointer = "/correlationId" };
        if (narrow)
        {
            bindings["scope"] = new { source = "input", pointer = "/scope" };
            bindings["successorId"] = new { source = "input", pointer = "/successorId" };
        }
        var definition = Definition() with { Parameters = JsonSerializer.SerializeToElement(new
        {
            entityType = "AccessGrant", fields = new[] { new { id = "grantId", label = "Grant" } },
            actions = new[] { new
            {
                id = "opaque", label = "Apply", operation = "opaque.example.action",
                input = new { fieldsMeta = new { scope = new { type = "text" }, successorId = new { type = "text" } }, overlay = new { } },
                dispatch = new { schemaVersion = 1, kind = "request", descriptorId, bindings },
            } },
        }) };
        await descriptors.AdmitAsync(definition);
        var item = new PackSeedItem("example.holders", PackContentKind.ViewDefinition, "1.0.0",
            JsonSerializer.Serialize(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Cid.FromBytes([]));
        Assert.True(RenderPlanCompiler.TryCompile(item, "example.pack", "1.0.0", out var plan, out var code), code);
        var descriptor = plan!.Bindings.GetProperty("actions")[0].GetProperty("dispatch").GetProperty("descriptor");
        Assert.Equal("selected-session", descriptor.GetProperty("audience").GetString());
        Assert.True(descriptor.GetProperty("requiresAntiforgery").GetBoolean());
        Assert.Equal("members:manage", descriptor.GetProperty("authorizationCapability").GetString());
        Assert.Equal("POST", descriptor.GetProperty("method").GetString());
    }

    private static ViewDefinition Definition(string descriptorId = "records.read.v1", string source = "input", string pointer = "/recordId") => new()
    {
        Tenant = "43300000-0000-4000-8000-000000000000", Key = "example.holders", Version = "1.0.0",
        SchemaVersion = 1, ViewKind = HostViewKindDescriptorRegistry.TableKind, Title = "Example",
        Parameters = JsonSerializer.SerializeToElement(new
        {
            entityType = "AccessGrant", fields = new[] { new { id = "grantId", label = "Grant" } },
            actions = new[] { new
            {
                id = "read", label = "Read record", operation = "opaque.example.read",
                input = new { fieldsMeta = new { recordId = new { type = "text", required = true } }, overlay = new { } },
                dispatch = new { schemaVersion = 1, kind = "request", descriptorId,
                    bindings = new { id = new { source, pointer }, correlationId = new { source = "invocation", pointer = "/correlationId" } } },
            } },
        }),
    };

    [Theory]
    [InlineData("application/octet-stream", "data-source")]
    [InlineData("application/octet-stream", "view")]
    [InlineData(".json", "view")]
    public async Task Admitted_binary_action_emits_file_input_and_refresh_contract(string accept, string refresh)
    {
        var definition = BinaryDefinition(accept, refresh);
        await descriptors.AdmitAsync(definition);
        var item = new PackSeedItem("example.holders", PackContentKind.ViewDefinition, "1.0.0",
            JsonSerializer.Serialize(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Cid.FromBytes([]));
        Assert.True(RenderPlanCompiler.TryCompile(item, "example.pack", "1.0.0", out var plan, out var code), code);
        var action = plan!.Bindings.GetProperty("actions")[0];
        Assert.Equal(accept, action.GetProperty("fileInput").GetProperty("accept").GetString());
        Assert.Equal(refresh, action.GetProperty("result").GetProperty("refresh").GetString());
    }

    [Theory]
    [InlineData("text/html", "data-source")]
    [InlineData("application/octet-stream", "execute-script")]
    public async Task Unknown_file_or_result_semantics_are_refused(string accept, string refresh)
    {
        await Assert.ThrowsAsync<ViewDefinitionGovernanceException>(() => descriptors.AdmitAsync(BinaryDefinition(accept, refresh)).AsTask());
    }

    private static ViewDefinition BinaryDefinition(string accept, string refresh)
    {
        var definition = Definition();
        var parameters = JsonNode.Parse(definition.Parameters.GetRawText())!;
        parameters["actions"] = JsonSerializer.SerializeToNode(new[] { new
        {
            id = "replace", label = "Replace", operation = "opaque.replace",
            fileInput = new { accept }, result = new { refresh },
            dispatch = new { schemaVersion = 1, kind = "request", descriptorId = "packs.replace.selected.v1",
                bindings = new { packKey = new { literal = "example.pack" }, artifact = new { source = "file", pointer = "" },
                    correlationId = new { source = "invocation", pointer = "/correlationId" } } },
        } });
        return definition with { Parameters = JsonSerializer.SerializeToElement(parameters) };
    }

    [Fact]
    public async Task Declarative_target_binding_does_not_weaken_the_grant_instance_fence()
    {
        var definition = Definition();
        var parameters = JsonNode.Parse(definition.Parameters.GetRawText())!;
        parameters["actions"] = JsonSerializer.SerializeToNode(new[] { new
        {
            id = "apply", label = "Apply", operation = "opaque.apply",
            dispatch = new { schemaVersion = 1, kind = "request", descriptorId = "authorization.grant.revoke.v1",
                bindings = new { target = new { source = "selection", pointer = "/grantId" },
                    correlationId = new { source = "invocation", pointer = "/correlationId" } } },
        } });
        Assert.False(PackAuthorizationContentAdmission.IsGrantInstance(parameters));
        await descriptors.AdmitAsync(definition with { Parameters = JsonSerializer.SerializeToElement(parameters) });
        parameters["actions"]![0]!["dispatch"]!["bindings"]!["target"] = JsonSerializer.SerializeToNode(new
        {
            literal = new { grantId = "smuggled", grantee = "some-person", role = "administrator" },
        });
        Assert.True(PackAuthorizationContentAdmission.IsGrantInstance(parameters));
        await Assert.ThrowsAsync<ViewDefinitionGovernanceException>(() => descriptors.AdmitAsync(
            definition with { Parameters = JsonSerializer.SerializeToElement(parameters) }).AsTask());
    }
}
