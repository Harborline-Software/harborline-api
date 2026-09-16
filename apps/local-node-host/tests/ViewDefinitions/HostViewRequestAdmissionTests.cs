using System.Text.Json;
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
        Substitute.For<IFormDefinitionStore>(), Substitute.For<ISchemaRegistry>());

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
    [InlineData("authorization.grant.revoke.v1", false)]
    [InlineData("authorization.grant.narrow-scope.v1", true)]
    public async Task Grant_actions_compile_only_host_owned_selected_session_enforcement(string descriptorId, bool narrow)
    {
        var bindings = new Dictionary<string, object> { ["grantId"] = new { source = "selection", pointer = "/grantId" } };
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
        SchemaVersion = 1, ViewKind = "views.entity-list/grid", Title = "Example",
        Parameters = JsonSerializer.SerializeToElement(new
        {
            entityType = "AccessGrant", fields = new[] { new { id = "grantId", label = "Grant" } },
            actions = new[] { new
            {
                id = "read", label = "Read record", operation = "opaque.example.read",
                input = new { fieldsMeta = new { recordId = new { type = "text", required = true } }, overlay = new { } },
                dispatch = new { schemaVersion = 1, kind = "request", descriptorId,
                    bindings = new { id = new { source, pointer } } },
            } },
        }),
    };
}
