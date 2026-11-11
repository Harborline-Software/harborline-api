using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Workflow;

public sealed partial class WorkflowDefinitionRouteTests
{
    private Exception? _c3Failure;

    // Fault injection at the admission dependency exercises otherwise unreachable catch branches;
    // the real route, lifecycle and persistence path still execute.
    private sealed class C3RoleAdmission(Func<Exception?> failure) : IRoleGateAdmission
    {
        private readonly IRoleGateAdmission _inner = TestAuthorization.RoleGate();
        public ValueTask AdmitAsync(RoleGatedDefinition definition, CancellationToken ct = default)
        {
            if (failure() is { } exception) throw exception;
            return _inner.AdmitAsync(definition, ct);
        }
        public ValueTask<IReadOnlyList<RoleGateFinding>> InspectActiveAsync(CancellationToken ct = default) =>
            _inner.InspectActiveAsync(ct);
    }

    [Fact(DisplayName = "C3 lane shape on the second transition is safe and never published")]
    public async Task C3_GateLane()
    {
        var body = JsonSerializer.SerializeToNode(AdmissibleBody())!;
        body["transitions"]![1]!["requiredRoles"] = new JsonArray(17);
        using var response = await _client.PutAsJsonAsync($"{Base}/{Key}", body);
        var refusal = await AssertC3(response, "workflow.gate_lane.invalid_entry",
            "/transitions/1/requiredRoles", "17", "Workflow gate lane refused");
        Assert.Equal("transition.requiredRoles", refusal.GetProperty("detail").GetProperty("field").GetString());
        Assert.Equal(0, refusal.GetProperty("detail").GetProperty("index").GetInt32());
    }

    [Fact(DisplayName = "C3 role admission gives only gate and rule and never publishes")]
    public async Task C3_RoleGate()
    {
        var body = JsonSerializer.SerializeToNode(AdmissibleBody())!;
        body["transitions"]![1]!["requiredRoles"] = new JsonArray("tenant/SecretRole");
        using var response = await _client.PutAsJsonAsync($"{Base}/{Key}", body);
        var refusal = await AssertC3(response, "authorization.role_gate.unresolved_role",
            "/transitions/1/requiredRoles", "SecretRole", "Role-gated definition refused");
        var detail = refusal.GetProperty("detail");
        Assert.Equal(2, detail.EnumerateObject().Count());
        Assert.Equal("transition:t-approve", detail.GetProperty("gate").GetString());
        Assert.Equal("unresolved_role", detail.GetProperty("rule").GetString());
    }

    [Fact(DisplayName = "C3 provenance refusal uses the whole authored document and never publishes")]
    public async Task C3_Provenance()
    {
        _c3Failure = new DefinitionProvenanceException("authorization.definition_provenance.layer_mismatch",
            CascadeLayer.Tenant, CascadeLayer.Base, "secret-package");
        using var response = await _client.PutAsJsonAsync($"{Base}/{Key}", AdmissibleBody());
        await AssertC3(response, "authorization.definition_provenance.layer_mismatch", "",
            "secret-package", "Definition provenance refused");
    }

    [Theory(DisplayName = "C3 admission locates the offending state or second transition")]
    [InlineData(true, "workflow.admission.unreachable_state", "/states/4")]
    [InlineData(false, "workflow.admission.dangling_reference", "/transitions/1")]
    public async Task C3_AdmissionLocator(bool state, string code, string pointer)
    {
        var body = JsonSerializer.SerializeToNode(AdmissibleBody())!;
        if (state)
            body["states"]!.AsArray().Add(new JsonObject { ["id"] = "secret-state", ["kind"] = "Terminal" });
        else
            body["transitions"]![1]!["guard"] = "secret-guard";
        using var response = await _client.PutAsJsonAsync($"{Base}/{Key}", body);
        await AssertC3(response, code, pointer, "secret-", "WorkflowDefinition refused at admission");
    }

    [Theory(DisplayName = "C3 ambiguous admission locators identify the whole document without disclosure or publication")]
    [InlineData("Draft")]
    [InlineData("t-approve")]
    public async Task C3_AmbiguousAdmissionLocator(string locator)
    {
        var body = JsonSerializer.SerializeToNode(AdmissibleBody())!;
        body["actions"]![0]!["id"] = locator;
        body["actions"]![0]!.AsObject().Remove("classification");
        using var response = await _client.PutAsJsonAsync($"{Base}/{Key}", body);
        await AssertC3(response, "workflow.admission.action_unclassified", "",
            locator, "WorkflowDefinition refused at admission");
    }

    [Fact(DisplayName = "C3 large admission batches preserve every violation and indexed pointer without publication")]
    public async Task C3_LargeAdmissionBatch()
    {
        var body = JsonSerializer.SerializeToNode(AdmissibleBody())!;
        var actions = new JsonArray();
        for (var i = 0; i < 1000; i++)
            actions.Add(new JsonObject { ["id"] = $"secret-action-{i}" });
        body["actions"] = actions;
        using var response = await _client.PutAsJsonAsync($"{Base}/{Key}", body);
        await AssertC3(response, "workflow.admission.action_binding_invalid", "/actions/0",
            "secret-action-", "WorkflowDefinition refused at admission", 2000);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var refusals = document.RootElement.GetProperty("refusals");
        Assert.Equal("workflow.admission.action_binding_invalid", refusals[999].GetProperty("code").GetString());
        Assert.Equal("/actions/999", refusals[999].GetProperty("pointer").GetString());
        Assert.Equal("workflow.admission.action_unclassified", refusals[1000].GetProperty("code").GetString());
        Assert.Equal("/actions/0", refusals[1000].GetProperty("pointer").GetString());
        Assert.Equal("workflow.admission.action_unclassified", refusals[1999].GetProperty("code").GetString());
        Assert.Equal("/actions/999", refusals[1999].GetProperty("pointer").GetString());
    }

    [Fact(DisplayName = "C3 admission preserves two ordered violations and their separate pointers")]
    public async Task C3_TwoViolations()
    {
        var body = JsonSerializer.SerializeToNode(AdmissibleBody())!;
        body["actions"]![0]!.AsObject().Remove("classification");
        var second = body["actions"]![0]!.DeepClone();
        second["id"] = "secret-second";
        body["actions"]!.AsArray().Add(second);
        using var response = await _client.PutAsJsonAsync($"{Base}/{Key}", body);
        await AssertC3(response, "workflow.admission.action_unclassified", "/actions/0",
            "secret-second", "WorkflowDefinition refused at admission", 2);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var refusal = document.RootElement.GetProperty("refusals")[1];
        Assert.Equal("workflow.admission.action_unclassified", refusal.GetProperty("code").GetString());
        Assert.Equal("/actions/1", refusal.GetProperty("pointer").GetString());
    }

    [Theory(DisplayName = "C3 admission unresolved or empty locator identifies the whole document")]
    [InlineData("secret-missing")]
    [InlineData("")]
    public async Task C3_UnresolvedLocator(string locator)
    {
        var body = JsonSerializer.SerializeToNode(AdmissibleBody())!;
        if (locator.Length == 0)
        {
            _c3Failure = new Harborline.Api.Blocks.Workflow.Durable.WorkflowAdmissionException(new()
            {
                Violations = [new("workflow.admission.initial_state_missing", "secret-message", "")],
            });
        }
        else body["initialState"] = locator;
        using var response = await _client.PutAsJsonAsync($"{Base}/{Key}", body);
        await AssertC3(response, "workflow.admission.initial_state_missing", "",
            "secret-", "WorkflowDefinition refused at admission");
    }

    [Theory(DisplayName = "C3 malformed or invalid body returns a safe section 10 code without publication")]
    [InlineData("{\"secret-input\":", "workflow_definition.malformed_request_body", "Malformed request body")]
    [InlineData("{\"states\":[{\"secret-input\":true}]}", "workflow_definition.definition_invalid", "workflow definition")]
    public async Task C3_BadRequest(string body, string code, string prefix)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _client.PutAsync($"{Base}/{Key}", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-input", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(prefix, raw, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        if (code == "workflow_definition.definition_invalid")
            Assert.Equal("{\"code\":\"workflow_definition.definition_invalid\",\"detail\":{\"parameter\":null}}", raw);
        else Assert.Equal("{\"code\":\"workflow_definition.malformed_request_body\"}", raw);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Base}/{Key}")).StatusCode);
    }

    [Fact(DisplayName = "C3 conversion preserves ordinary 400 and 409 errors and the success body")]
    public async Task C3_OrdinaryProtocolAndSuccess()
    {
        _c3Failure = new Harborline.Api.Blocks.Workflow.Durable.WorkflowDefinitionConflictException(
            "invoice-approval.v1", "1.0.0", "private-tenant");
        using var conflict = await _client.PutAsJsonAsync($"{Base}/{Key}", AdmissibleBody());
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var conflictBody = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Single(conflictBody.RootElement.EnumerateObject());
        Assert.Equal("A revision of 'invoice-approval.v1' at version 1.0.0 already exists.",
            conflictBody.RootElement.GetProperty("error").GetString());

        using var content = new StringContent("[]", Encoding.UTF8, "application/json");
        using var invalid = await _client.PutAsync($"{Base}/{Key}", content);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var invalidBody = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync());
        Assert.Single(invalidBody.RootElement.EnumerateObject());
        Assert.Equal("Request body must be a workflow definition object.",
            invalidBody.RootElement.GetProperty("error").GetString());

        _c3Failure = null;
        using var success = await _client.PutAsJsonAsync($"{Base}/{Key}", AdmissibleBody());
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        using var saved = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
        Assert.Equal(2, saved.RootElement.EnumerateObject().Count());
        Assert.Equal("invoice-approval.v1", saved.RootElement.GetProperty("key").GetString());
        Assert.Equal("1.0.0", saved.RootElement.GetProperty("version").GetString());
    }

    private async Task<JsonElement> AssertC3(HttpResponseMessage response, string code, string pointer,
        string rejected, string prefix, int count = 1)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rejected, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(prefix, raw, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.Equal("publish", root.GetProperty("stage").GetString());
        var refusals = root.GetProperty("refusals");
        Assert.Equal(count, refusals.GetArrayLength());
        foreach (var entry in refusals.EnumerateArray())
            Assert.All(entry.EnumerateObject(), property =>
                Assert.Contains(property.Name, new[] { "code", "pointer", "detail" }));
        var refusal = refusals[0];
        Assert.Equal(code, refusal.GetProperty("code").GetString());
        Assert.Equal(pointer, refusal.GetProperty("pointer").GetString());
        Assert.False(refusal.TryGetProperty("target", out _));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{Base}/{Key}")).StatusCode);
        return refusal.Clone();
    }
}
