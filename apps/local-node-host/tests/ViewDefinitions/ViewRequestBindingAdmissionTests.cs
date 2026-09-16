using System.Text.Json;
using Harborline.Api.Foundation.ViewDefinitions;

namespace Harborline.Api.LocalNodeHost.Tests.ViewDefinitions;

public sealed class ViewRequestBindingAdmissionTests
{
    private static readonly ViewRequestDescriptor Descriptor = new(
        "test.record.read", "GET", "/api/local-node/asset-registry/entities/{id}", "application/json",
        "device-reachable", false, "records:read", [new("id", ViewRequestValueKind.Text, ViewRequestPlacement.Path, "id")]);
    private static readonly Dictionary<string, ViewRequestDescriptor> Descriptors = new(StringComparer.Ordinal)
    {
        [Descriptor.Id] = Descriptor,
    };
    private static readonly ViewRequestBindingSources Sources = new(
        new Dictionary<string, ViewRequestValueKind> { ["recordId"] = ViewRequestValueKind.Text },
        new Dictionary<string, ViewRequestValueKind> { ["count"] = ViewRequestValueKind.Number });

    [Fact]
    public void Resolves_transport_and_enforcement_only_from_the_host_descriptor()
    {
        using var document = JsonDocument.Parse(Dispatch("""{"source":"selection","pointer":"/recordId"}"""));
        var result = ViewRequestBindingAdmission.Admit(document.RootElement, Descriptors, Sources);
        Assert.Same(Descriptor, result.Descriptor);
        Assert.Equal("records:read", result.Descriptor.AuthorizationCapability);
        Assert.Equal("/recordId", result.Bindings.GetProperty("id").GetProperty("pointer").GetString());
        document.Dispose();
        Assert.Equal("selection", result.Bindings.GetProperty("id").GetProperty("source").GetString());
    }

    [Theory]
    [InlineData("{\"source\":\"selection\",\"pointer\":\"/missing\"}")]
    [InlineData("{\"source\":\"input\",\"pointer\":\"/count\"}")]
    [InlineData("{\"source\":\"principal\",\"pointer\":\"/recordId\"}")]
    [InlineData("{\"source\":\"selection\",\"pointer\":\"/recordId/nested\"}")]
    [InlineData("{\"source\":\"selection\",\"pointer\":\"/recordId~2\"}")]
    [InlineData("{\"source\":\"selection\",\"pointer\":\"/recordId\",\"literal\":\"other\"}")]
    [InlineData("{\"literal\":12}")]
    public void Refuses_unresolvable_or_wrongly_typed_sources(string binding) => Refused(Dispatch(binding));

    [Theory]
    [InlineData("\"method\":\"DELETE\",")]
    [InlineData("\"route\":\"https://example.invalid/collect\",")]
    [InlineData("\"authorizationCapability\":\"public\",")]
    [InlineData("\"actor\":\"operator\",")]
    [InlineData("\"tenant\":\"other\",")]
    [InlineData("\"schemaVersion\":1,")]
    public void Refuses_pack_overrides_and_duplicate_properties(string extra) =>
        Refused(Dispatch("""{"literal":"record-1"}""", extra));

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"kind\":\"request\",\"descriptorId\":\"test.record.read\",\"bindings\":{\"id\":{\"literal\":\"r\"}}}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"script\",\"descriptorId\":\"test.record.read\",\"bindings\":{\"id\":{\"literal\":\"r\"}}}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"request\",\"descriptorId\":\"unknown\",\"bindings\":{\"id\":{\"literal\":\"r\"}}}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"request\",\"descriptorId\":\"test.record.read\",\"bindings\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"kind\":\"request\",\"descriptorId\":\"test.record.read\",\"bindings\":{\"id\":{\"literal\":\"r\"},\"actor\":{\"literal\":\"a\"}}}")]
    public void Refuses_unknown_contract_and_missing_or_extra_inputs(string dispatch) => Refused(dispatch);

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("X-Harborline-Antiforgery")]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-Host")]
    public void Unsafe_host_header_descriptors_are_refused_before_activation(string header) =>
        InvalidDescriptor(Descriptor with { Inputs = [new("id", ViewRequestValueKind.Text, ViewRequestPlacement.Header, header)] });

    [Fact]
    public void Missing_unknown_duplicate_or_inconsistent_placements_are_refused()
    {
        InvalidDescriptor(Descriptor with { Inputs = [new("id", ViewRequestValueKind.Text)] });
        InvalidDescriptor(Descriptor with { Inputs = [new("id", ViewRequestValueKind.Text, (ViewRequestPlacement)99, "id")] });
        InvalidDescriptor(Descriptor with { Inputs = [new("id", ViewRequestValueKind.Text, ViewRequestPlacement.Path, "missing")] });
        InvalidDescriptor(Descriptor with { Inputs = [new("id", ViewRequestValueKind.Text, ViewRequestPlacement.Path, "id"),
            new("id", ViewRequestValueKind.Text, ViewRequestPlacement.Path, "id")] });
        InvalidDescriptor(Descriptor with { Inputs = [new("id", ViewRequestValueKind.Text, ViewRequestPlacement.BodyRoot, "")] });
    }

    [Fact]
    public void Form_payload_root_and_idempotency_header_are_host_owned_and_serialized_explicitly()
    {
        var form = new ViewRequestDescriptor("forms.submit.v1", "POST", "/api/local-node/forms/{formId}/submit",
            "application/json", "selected-session", true, "forms:write",
            [new("form", ViewRequestValueKind.Text, ViewRequestPlacement.Path, "formId"),
             new("values", ViewRequestValueKind.Object, ViewRequestPlacement.BodyRoot, ""),
             new("requestId", ViewRequestValueKind.Text, ViewRequestPlacement.Header, "Idempotency-Key")]);
        var dispatch = JsonSerializer.SerializeToElement(new { schemaVersion = 1, kind = "request", descriptorId = form.Id,
            bindings = new { form = new { literal = "example.form" }, values = new { source = "input", pointer = "" },
                requestId = new { literal = "example-submit-v1" } } });
        var compiled = ViewRequestBindingAdmission.Admit(dispatch,
            new Dictionary<string, ViewRequestDescriptor> { [form.Id] = form }, Sources with { HasInputObject = true });
        Assert.Equal("""[{"name":"form","kind":"Text","placement":"Path","wireName":"formId"},{"name":"values","kind":"Object","placement":"BodyRoot","wireName":""},{"name":"requestId","kind":"Text","placement":"Header","wireName":"Idempotency-Key"}]""",
            JsonSerializer.Serialize(compiled.Descriptor.Inputs, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static void InvalidDescriptor(ViewRequestDescriptor descriptor)
    {
        using var dispatch = JsonDocument.Parse(Dispatch("""{"literal":"record"}"""));
        var error = Assert.Throws<ViewDefinitionGovernanceException>(() => ViewRequestBindingAdmission.Resolve(
            dispatch.RootElement, new Dictionary<string, ViewRequestDescriptor> { [descriptor.Id] = descriptor }));
        Assert.Equal("view_definition.request_binding_invalid", error.ErrorCode);
    }

    private static string Dispatch(string binding, string extra = "") =>
        "{\"schemaVersion\":1,\"kind\":\"request\",\"descriptorId\":\"test.record.read\"," + extra + "\"bindings\":{\"id\":" + binding + "}}";

    private static void Refused(string dispatch)
    {
        using var document = JsonDocument.Parse(dispatch);
        var error = Assert.Throws<ViewDefinitionGovernanceException>(() =>
            ViewRequestBindingAdmission.Admit(document.RootElement, Descriptors, Sources));
        Assert.Equal("view_definition.request_binding_invalid", error.ErrorCode);
    }
}
