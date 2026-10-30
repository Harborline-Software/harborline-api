using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Authorization.SeparationOfDuty;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.Kernel.Runtime;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Entities;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Health.WebSession;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

public sealed class AssetRegistryEntityCreateAuthorizationOrderTests
{
    [Theory]
    [InlineData("tenant-type")]
    [InlineData("missing-type")]
    [InlineData("bound-type")]
    [InlineData("seed-type")]
    public async Task Denied_create_decides_once_before_any_type_seed_form_or_store_access(string type)
    {
        await using var host = await Host.StartAsync();
        host.AllowWrite = false;

        using var response = await host.PostAsync(new { type, displayName = "Note", values = new { title = "Note" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("authorization.permission_required", body.GetProperty("code").GetString());
        Assert.Equal("records:write", body.GetProperty("permission").GetString());
        host.AssertNoStateAccess();
        AssertRecordRequest(Assert.Single(host.Requests));
        Assert.Equal(1, host.Clock.Reads);
        var refusal = Assert.Single(await host.RowsAsync());
        Assert.Equal("AuthorizationRefused", refusal.EventType.Value);
        Assert.Equal(Assert.Single(host.Requests).Target.RecordId, refusal.Target!.Value.RecordId);
        Assert.Equal(refusal.AuditId, body.GetProperty("auditId").GetGuid());
        Assert.NotEqual(Guid.Empty, refusal.AuditId);
        Assert.Equal(refusal.AuditId.ToString("D"), Assert.Single(response.Headers.GetValues("X-Harborline-Audit-Id")));
        Assert.Empty(host.Trail.AcceptedDecisions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Allowed_create_preserves_unbound_and_canonical_bound_data_with_one_decision(bool bound)
    {
        await using var host = await Host.StartAsync();
        using var response = await host.PostAsync(new
        {
            type = bound ? "bound-type" : "tenant-type", displayName = "  Note  ",
            scanKey = "  scan-1084  ", values = new { title = "Note" },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Equal("/api/local-node/asset-registry/entities/" + id, response.Headers.Location!.OriginalString);
        Assert.Equal("Note", body.GetProperty("displayName").GetString());
        Assert.Equal(bound ? "bound-type" : "tenant-type", body.GetProperty("type").GetString());
        var indexed = await host.Registry.GetByIdAsync(Tenant, new RegistryEntityId(id));
        Assert.NotNull(indexed);
        Assert.Equal("Note", indexed.DisplayName);
        Assert.Equal("scan-1084", indexed.ScanKey);
        var request = Assert.Single(host.Requests);
        AssertRecordRequest(request);
        Assert.Equal(request.Target.RecordId, id);
        Assert.Equal(request.Target.RecordId, indexed.Id.Value);
        Assert.Equal(1, host.Clock.Reads);
        Assert.Equal(request.At, indexed.CreatedAt.Value);
        if (bound)
        {
            var canonical = await host.Services.GetRequiredService<IEntityStore>()
                .GetAsync(new EntityId("record", "asset-registry", id));
            Assert.NotNull(canonical);
            Assert.Equal("Note", canonical.Body.RootElement.GetProperty("title").GetString());
            Assert.Equal("Note", body.GetProperty("values").GetProperty("title").GetString());
            Assert.Equal(Tenant, canonical.Tenant);
            Assert.Equal("selected-party", Assert.Single(host.Mutations.ReceivedCalls()).GetArguments()
                .OfType<CreateOptions>().Single().Issuer.Value);
            AssertStages(host.Stages);
            Assert.Equal(1, host.Validator.Calls);
            Assert.Single(host.Forms.ReceivedCalls());
            Assert.Single(host.Mutations.ReceivedCalls());
            var accepted = Assert.Single(host.Trail.AcceptedDecisions);
            Assert.Same(request.Instant, accepted.Request.Instant);
            Assert.Equal(request, accepted.Request);
            var row = Assert.Single(await host.RowsAsync());
            Assert.Equal("RecordWritten", row.EventType.Value);
            Assert.Equal("selected-principal", row.Actor!.Value.Value);
            Assert.Equal("record", row.Target!.Value.RecordKind);
            Assert.Equal(id, row.Target.Value.RecordId);
            Assert.Equal("/records/" + id, row.Target.Value.Scope.Value);
            Assert.Equal("/records/" + id, row.Act!.Value.Scope.Value);
            Assert.Equal(id, row.Payload.Payload.Body["recordId"]);
            Assert.Equal(host.Schema.Value, row.Payload.Payload.Body["schema"]);
            Assert.Equal("43300000-0000-4000-8000-000000000084", row.Payload.Payload.Body["correlation_id"]);
            Assert.Equal(row.AuditId, body.GetProperty("auditId").GetGuid());
        }
        else
        {
            Assert.Null(indexed.PropertyForm);
            Assert.Empty(host.Forms.ReceivedCalls());
            Assert.Empty(host.Mutations.ReceivedCalls());
            Assert.Empty(await host.RowsAsync());
        }
    }

    [Theory]
    [InlineData("missing-type", false, false, false, 400, "unknown_type")]
    [InlineData("tenant-type", true, false, false, 400, "client_id_forbidden")]
    [InlineData("bound-type", false, true, false, 400, "values_object_required")]
    [InlineData("bound-type", false, false, true, 503, null)]
    [InlineData("bound-type", false, false, false, 409, "property_form_unavailable")]
    public async Task Allowed_branch_refusals_follow_the_single_record_decision(
        string type, bool clientId, bool malformedValues, bool noWriter, int status, string? error)
    {
        await using var host = await Host.StartAsync(noWriter, publishForm: error != "property_form_unavailable");
        using var values = JsonDocument.Parse(malformedValues ? "[]" : "{\"title\":\"Note\"}");
        using var response = await host.PostAsync(new
        {
            type, displayName = "Note", id = clientId ? "client-id" : null, values = values.RootElement,
        });
        Assert.Equal(status, (int)response.StatusCode);
        if (error is not null)
            Assert.Equal(error, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        AssertRecordRequest(Assert.Single(host.Requests));
        Assert.NotEqual("client-id", Assert.Single(host.Requests).Target.RecordId);
        Assert.Equal(1, host.Clock.Reads);
        Assert.Empty(host.Mutations.ReceivedCalls());
        Assert.Empty(host.RegistryWrites.ReceivedCalls());
        Assert.Empty(await host.RowsAsync());
    }

    [Theory]
    [InlineData("", "Note")]
    [InlineData("tenant-type", " ")]
    public async Task Request_only_shape_refusal_stays_before_authority(string type, string displayName)
    {
        await using var host = await Host.StartAsync();
        using var response = await host.PostAsync(new { type, displayName });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("type_and_display_name_required",
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        host.AssertNoStateAccess();
        Assert.Empty(host.Requests);
        Assert.Equal(0, host.Clock.Reads);
        Assert.Empty(await host.RowsAsync());
    }

    [Fact]
    public async Task Retained_allow_still_requires_canonical_body_validation_before_any_mutation()
    {
        await using var host = await Host.StartAsync();
        using var response = await host.PostAsync(new { type = "bound-type", displayName = "Note", values = new { } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertValidationWire(body, "entity.validation.body_invalid", "/title");
        await AssertNoRefusedWriteAsync(host);
        AssertRefusalReceipt(host, body, Assert.Single(await host.RowsAsync()), "entity.validation.body_invalid");
    }

    [Theory]
    [InlineData("unknown", "entity.validation.schema_unknown")]
    [InlineData("rule", "entity.validation.rule_refused")]
    [InlineData("unavailable", "entity.validation.rules_unavailable")]
    public async Task Keyed_record_validator_refusals_keep_actual_code_root_and_receipt(string condition, string code)
    {
        RuleDefinition[] rules = condition == "unknown" ? [] : [RecordRule(condition == "rule"
            ? """{">":[{"var":"rent"},0]}"""
            : """{"no-such-operator":[{"var":"rent"}]}""")];
        await using var host = await Host.StartAsync(rules: rules, unknownSchema: condition == "unknown");

        using var response = await host.PostAsync(new
        {
            type = "bound-type", displayName = "Private submitted label", values = new { title = "Private submitted value", rent = 0 },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertValidationWire(body, code, "");
        Assert.DoesNotContain("Private submitted", body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("hidden-rule", body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("records.unavailable-hidden-schema", body.GetRawText(), StringComparison.Ordinal);
        await AssertNoRefusedWriteAsync(host);
        AssertRefusalReceipt(host, body, Assert.Single(await host.RowsAsync()), code);
    }

    [Fact]
    public async Task Keyed_record_rule_admits_a_satisfying_record_with_the_real_receipt()
    {
        await using var host = await Host.StartAsync(rules: [RecordRule("""{">":[{"var":"rent"},0]}""")]);

        using var response = await host.PostAsync(new { type = "bound-type", displayName = "Note", values = new { title = "Note", rent = 1200 } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var request = Assert.Single(host.Requests);
        AssertRecordRequest(request);
        Assert.Equal(request.Target.RecordId, body.GetProperty("id").GetString());
        var stored = await host.Services.GetRequiredService<IEntityStore>().GetAsync(
            new EntityId("record", "asset-registry", request.Target.RecordId));
        Assert.NotNull(stored);
        Assert.Equal(1200, stored.Body.RootElement.GetProperty("rent").GetInt32());
        Assert.NotNull(await host.Registry.GetByIdAsync(Tenant, new RegistryEntityId(request.Target.RecordId)));
        Assert.Equal(1, host.Clock.Reads);
        Assert.Equal(1, host.Validator.Calls);
        AssertStages(host.Stages);
        Assert.Equal(request, Assert.Single(host.Trail.AcceptedDecisions).Request);
        var row = Assert.Single(await host.RowsAsync());
        Assert.Equal("RecordWritten", row.EventType.Value);
        Assert.Equal(row.AuditId, body.GetProperty("auditId").GetGuid());
    }

    [Theory]
    [InlineData("""{"type":"object","not":{"type":"object"}}""", "{}", "")]
    [InlineData("""{"type":"object","properties":{"title":{"type":"string"}}}""", """{"title":37}""", "/title")]
    [InlineData("""{"type":"object","properties":{"owner":{"type":"object","properties":{"name":{"type":"string"}}}}}""", """{"owner":{"name":37}}""", "/owner/name")]
    [InlineData("""{"type":"object","properties":{"lines":{"type":"array","items":{"type":"string"}}}}""", """{"lines":[37]}""", "/lines/0")]
    [InlineData("""{"type":"object","properties":{"a/b~c":{"type":"string"}}}""", """{"a/b~c":37}""", "/a~1b~0c")]
    [InlineData("""{"type":"object","properties":{"":{"type":"string"}}}""", """{"":37}""", "/")]
    public async Task Schema_refusal_preserves_the_actual_values_document_location(string schema, string values, string pointer)
    {
        await using var host = await Host.StartAsync(schemaText: schema);
        using var document = JsonDocument.Parse(values);

        using var response = await host.PostAsync(new { type = "bound-type", displayName = "Note", values = document.RootElement });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertValidationWire(await response.Content.ReadFromJsonAsync<JsonElement>(), "entity.validation.body_invalid", pointer);
        await AssertNoRefusedWriteAsync(host);
    }

    [Fact]
    public async Task Every_actual_schema_location_gets_an_individual_refusal()
    {
        await using var host = await Host.StartAsync(schemaText:
            """{"type":"object","properties":{"title":{"type":"string"},"other":{"type":"string"}}}""");

        using var response = await host.PostAsync(new { type = "bound-type", displayName = "Note", values = new { title = 37, other = 42 } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertValidationWire(await response.Content.ReadFromJsonAsync<JsonElement>(), "entity.validation.body_invalid", "/other", "/title");
        await AssertNoRefusedWriteAsync(host);
    }

    [Theory]
    [InlineData(RefusalAuditMode.Absent)]
    [InlineData(RefusalAuditMode.Failed)]
    public async Task Missing_or_failed_refusal_sink_does_not_fabricate_a_receipt(RefusalAuditMode auditMode)
    {
        await using var host = await Host.StartAsync(auditMode: auditMode);

        using var response = await host.PostAsync(new { type = "bound-type", displayName = "Note", values = new { title = 37 } });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        AssertValidationWire(body, "entity.validation.body_invalid", "/title");
        Assert.False(body.TryGetProperty("auditId", out _));
        await AssertNoRefusedWriteAsync(host);
        Assert.Empty(await host.RowsAsync());
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("absent")]
    [InlineData("null-member")]
    public async Task Missing_producer_locations_fail_closed_without_a_default_root(string condition)
    {
        await using var host = await Host.StartAsync(auditMode: RefusalAuditMode.Absent);
        host.Validator.Failure = new EntityValidationException("private-exception-sentinel",
            "entity.validation.body_invalid", condition switch { "absent" => null!, "null-member" => [null!], _ => [] });

        using var response = await host.PostAsync(new { type = "bound-type", displayName = "Note", values = new { title = "Note" } });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        await AssertNoRefusedWriteAsync(host);
        Assert.Empty(await host.RowsAsync());
    }

    [Fact]
    public async Task Registry_index_failure_preserves_the_committed_record_and_real_receipt()
    {
        await using var host = await Host.StartAsync();
        host.FailRegistryIndex = true;

        using var response = await host.PostAsync(new { type = "bound-type", displayName = "Note", values = new { title = "Note" } });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("registry_index_failed", body.GetProperty("code").GetString());
        Assert.False(body.TryGetProperty("stage", out _));
        var request = Assert.Single(host.Requests);
        AssertRecordRequest(request);
        Assert.Equal(request.Target.RecordId, body.GetProperty("id").GetString());
        Assert.Equal("record:asset-registry/" + request.Target.RecordId, body.GetProperty("canonicalRecordId").GetString());
        Assert.NotNull(await host.Services.GetRequiredService<IEntityStore>().GetAsync(
            new EntityId("record", "asset-registry", request.Target.RecordId)));
        Assert.Null(await host.Registry.GetByIdAsync(Tenant, new RegistryEntityId(request.Target.RecordId)));
        AssertStages(host.Stages);
        Assert.Equal(request, Assert.Single(host.Trail.AcceptedDecisions).Request);
        var row = Assert.Single(await host.RowsAsync());
        Assert.Equal("RecordWritten", row.EventType.Value);
        Assert.Equal(row.AuditId, body.GetProperty("auditId").GetGuid());
        Assert.DoesNotContain("private-index-sentinel", body.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("principal")]
    [InlineData("tenant")]
    [InlineData("instant")]
    [InlineData("correlation")]
    [InlineData("operation")]
    [InlineData("install-target")]
    [InlineData("record-id")]
    [InlineData("target-kind")]
    [InlineData("target-id")]
    [InlineData("target-scope")]
    [InlineData("act-scope")]
    public async Task Retained_decision_must_match_the_entire_record_create_context_before_preparation(string mismatch)
    {
        await using var host = await Host.StartAsync();
        var authority = TestAuthorization.Write(Tenant, "selected-principal") with { CorrelationId = Guid.Parse("43300000-0000-4000-8000-000000000084") };
        var request = authority.Request(AuthorizationOperation.Parse("records:write"), "record", "prepared");
        var different = ScopeExpression.Parse("/records/other");
        request = mismatch switch
        {
            "principal" => request with { Principal = new ActorId("other-principal") },
            "tenant" => request with { Tenant = new TenantId("other-tenant") },
            "instant" => request with { Instant = AdmittedInstant.FromRecordedAct(authority.At.AddSeconds(1)) },
            "correlation" => request with { CorrelationId = null },
            "operation" => request with { Act = new PermissionAtom(AuthorizationOperation.Parse("records:read"), ScopeExpression.Parse("/records/prepared")) },
            "install-target" => authority.InstallWide(AuthorizationOperation.Parse("records:write")),
            "record-id" => authority.Request(AuthorizationOperation.Parse("records:write"), "record", "other"),
            "target-kind" => request with { Target = new AuthorizationTarget("other-kind", "prepared", ScopeExpression.Parse("/records/prepared")) },
            "target-id" => request with { Target = new AuthorizationTarget("record", "other", ScopeExpression.Parse("/records/prepared")) },
            "target-scope" => request with { Target = new AuthorizationTarget("record", "prepared", different) },
            "act-scope" => request with { Act = new PermissionAtom(AuthorizationOperation.Parse("records:write"), different) },
            _ => request,
        };
        // Deliberately malformed internal evidence exercises the consumer guard, not the gate's producer validation.
        var decision = new AuthorizationDecision(request,
            mismatch == "denied" ? AuthorizationVerdict.Denied : AuthorizationVerdict.Allowed, [], [], [], []);
        var preparations = 0;
        using var body = JsonDocument.Parse("{\"title\":\"Note\"}");
        async Task Invoke() => await host.Writer.CreateWithDecisionAsync(body, "prepared", authority, decision, _ =>
        {
            preparations++;
            return ValueTask.FromResult((host.Schema, Options("prepared")));
        });
        if (mismatch == "denied") await Assert.ThrowsAsync<AuthorizationDeniedException>(Invoke);
        else await Assert.ThrowsAsync<ArgumentException>(Invoke);
        Assert.Equal(0, preparations);
        Assert.Empty(host.Requests);
        Assert.Equal(new[] { WritePipelineStage.Authorize }, host.Stages);
        Assert.Equal(0, host.Validator.Calls);
        Assert.Empty(host.Mutations.ReceivedCalls());
        Assert.Empty(await host.RowsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_create_preserves_prepared_tenant_and_derived_id_guards(bool foreignTenant)
    {
        await using var host = await Host.StartAsync();
        var authority = TestAuthorization.Write(Tenant, "selected-principal");
        var decision = await TestAuthorization.AllowGate().DecideAsync(authority.Request(AuthorizationOperation.Parse("records:write"), "record", "prepared"));
        using var body = JsonDocument.Parse("{\"title\":\"Note\"}");
        var options = foreignTenant ? Options("prepared") with { Tenant = new TenantId("other-tenant") } : Options("other-id");
        await Assert.ThrowsAsync<ArgumentException>(async () => await host.Writer.CreateWithDecisionAsync(
            body, "prepared", authority, decision, _ => ValueTask.FromResult((host.Schema, options))));
        Assert.Equal(new[] { WritePipelineStage.Authorize, WritePipelineStage.Bind }, host.Stages);
        Assert.Empty(host.Requests);
        Assert.Equal(0, host.Validator.Calls);
        Assert.Empty(host.Mutations.ReceivedCalls());
        Assert.Empty(await host.RowsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Generic_create_entries_still_decide_for_the_actual_record_id(bool deferred)
    {
        await using var host = await Host.StartAsync();
        using var body = JsonDocument.Parse("{\"title\":\"Note\"}");
        var authority = TestAuthorization.Write(Tenant, "selected-principal");
        var options = Options("generic-1084");
        var written = deferred
            ? await host.Writer.CreateWithReceiptAsync(body, "generic-1084", authority, _ => ValueTask.FromResult((host.Schema, options)))
            : await host.Writer.CreateWithReceiptAsync(host.Schema, body, options, authority);
        Assert.Equal("generic-1084", written.Entity.LocalPart);
        var request = Assert.Single(host.Requests);
        Assert.Equal("record", request.Target.RecordKind);
        Assert.Equal("generic-1084", request.Target.RecordId);
        Assert.Equal("/records/generic-1084", request.Target.Scope.Value);
        Assert.Equal("/records/generic-1084", request.Act.Scope.Value);
        Assert.Equal("records:write", request.Act.Operation.Value);
        AssertStages(host.Stages);
        Assert.Equal(1, host.Validator.Calls);
        Assert.Single(host.Mutations.ReceivedCalls());
        Assert.Single(host.Trail.AcceptedDecisions);
        Assert.Equal("RecordWritten", Assert.Single(await host.RowsAsync()).EventType.Value);
    }

    [Fact]
    public async Task Public_pack_bound_entry_keeps_its_actual_record_decision()
    {
        await using var host = await Host.StartAsync();
        using var body = JsonDocument.Parse("{\"title\":\"Note\"}");
        var writer = new PackBoundRegistryRecordWriter(host.Forms, host.Writer,
            host.Services.GetRequiredService<IEntityStore>(), host.RegistryWrites);
        var written = await writer.CreateAsync(new EntityTypeId("bound-type"),
            new FormBindingRef(new FormDefinitionId("create-order.capture"), new SemanticVersion(1, 0, 0)),
            "Note", null, body, new ActorId("selected-party"), TestAuthorization.Write(Tenant, "selected-principal"));
        var request = Assert.Single(host.Requests);
        Assert.Equal("record", request.Target.RecordKind);
        Assert.Equal(written.Entity.Id.Value, request.Target.RecordId);
        Assert.Equal("/records/" + written.Entity.Id.Value, request.Target.Scope.Value);
        Assert.Equal("records:write", request.Act.Operation.Value);
        AssertStages(host.Stages);
        Assert.Equal("RecordWritten", Assert.Single(await host.RowsAsync()).EventType.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Client_id_never_selects_the_server_authorization_target(bool allowed)
    {
        await using var host = await Host.StartAsync();
        host.AllowWrite = allowed;
        using var response = await host.PostAsync(new { type = "bound-type", displayName = "Note", id = "10840000-0000-4000-8000-000000000084" });
        Assert.Equal(allowed ? HttpStatusCode.BadRequest : HttpStatusCode.Forbidden, response.StatusCode);
        var request = Assert.Single(host.Requests);
        AssertRecordRequest(request);
        Assert.NotEqual("10840000-0000-4000-8000-000000000084", request.Target.RecordId);
        Assert.Equal(1, host.Clock.Reads);
        host.AssertNoStateAccess();
        Assert.DoesNotContain(await host.RowsAsync(), row => row.EventType.Value == "RecordWritten");
    }

    [Fact]
    public async Task Pack_bound_retained_entry_rejects_another_prospective_id_before_form_or_writer_work()
    {
        await using var host = await Host.StartAsync();
        var authority = TestAuthorization.Write(Tenant, "selected-principal");
        var decision = await TestAuthorization.AllowGate().DecideAsync(
            authority.Request(AuthorizationOperation.Parse("records:write"), "record", "prepared"));
        using var body = JsonDocument.Parse("{\"title\":\"Note\"}");
        var writer = new PackBoundRegistryRecordWriter(host.Forms, host.Writer,
            host.Services.GetRequiredService<IEntityStore>(), host.RegistryWrites);
        await Assert.ThrowsAsync<ArgumentException>(async () => await writer.CreateWithDecisionAsync(
            new RegistryEntityId("different-id"), new EntityTypeId("bound-type"),
            new FormBindingRef(new FormDefinitionId("create-order.capture"), new SemanticVersion(1, 0, 0)),
            "Note", null, body, new ActorId("selected-party"), authority, decision));
        host.AssertNoStateAccess();
        Assert.Empty(host.Requests);
        Assert.Empty(await host.RowsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contradictory_created_identity_stops_before_receipt_and_registry_index(bool otherAuthority)
    {
        await using var host = await Host.StartAsync();
        var authority = TestAuthorization.Write(Tenant, "selected-principal");
        var decision = await TestAuthorization.AllowGate().DecideAsync(
            authority.Request(AuthorizationOperation.Parse("records:write"), "record", "prepared"));
        host.ReturnedId = new EntityId("record", otherAuthority ? "other-authority" : "asset-registry",
            otherAuthority ? "prepared" : "other-id");
        using var body = JsonDocument.Parse("{\"title\":\"Note\"}");
        var writer = new PackBoundRegistryRecordWriter(host.Forms, host.Writer,
            host.Services.GetRequiredService<IEntityStore>(), host.RegistryWrites);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await writer.CreateWithDecisionAsync(
            new RegistryEntityId("prepared"), new EntityTypeId("bound-type"),
            new FormBindingRef(new FormDefinitionId("create-order.capture"), new SemanticVersion(1, 0, 0)),
            "Note", null, body, new ActorId("selected-party"), authority, decision));
        Assert.Equal(new[] { WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
            WritePipelineStage.Validate, WritePipelineStage.Commit }, host.Stages);
        Assert.Equal(1, host.Validator.Calls);
        Assert.Single(host.Forms.ReceivedCalls());
        Assert.Single(host.Mutations.ReceivedCalls());
        Assert.Empty(host.RegistryWrites.ReceivedCalls());
        Assert.Empty(host.Trail.AcceptedDecisions);
        Assert.Empty(await host.RowsAsync());
        Assert.Empty(host.Requests);
    }

    private static readonly TenantId Tenant = new("10840000-0000-0000-0000-000000000001");
    private static CreateOptions Options(string id) => new("record", "asset-registry", id, new ActorId("selected-party"), Tenant, ExplicitLocalPart: id);

    private static RuleDefinition RecordRule(string expression) => new(
        new DefinitionEnvelope<string, string, TenantId, string?>("hidden-rule", "1.0.0", Tenant, CascadeLayer.Tenant, null, [], Contract: null),
        RuleTier.JsonLogic, RuleScope.Schema, string.Empty, expression, RuleActionKind.Validate);

    private static void AssertValidationWire(JsonElement body, string code, params string[] pointers)
    {
        Assert.Equal("validate", body.GetProperty("stage").GetString());
        Assert.Equal(body.TryGetProperty("auditId", out _) ? new[] { "auditId", "refusals", "stage" } : new[] { "refusals", "stage" },
            body.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        var refusals = body.GetProperty("refusals").EnumerateArray().ToArray();
        Assert.NotEmpty(refusals);
        Assert.All(refusals, refusal =>
        {
            Assert.Equal(new[] { "code", "pointer" }, refusal.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(code, refusal.GetProperty("code").GetString());
        });
        Assert.Equal(pointers, refusals.Select(refusal => refusal.GetProperty("pointer").GetString()).OrderBy(pointer => pointer, StringComparer.Ordinal));
    }

    private static async Task AssertNoRefusedWriteAsync(Host host)
    {
        var request = Assert.Single(host.Requests);
        AssertRecordRequest(request);
        Assert.Equal(1, host.Clock.Reads);
        Assert.Equal(1, host.Validator.Calls);
        Assert.Equal(new[] { WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
            WritePipelineStage.Validate }, host.Stages);
        Assert.Empty(host.Mutations.ReceivedCalls());
        Assert.Empty(host.RegistryWrites.ReceivedCalls());
        Assert.Empty(host.Trail.AcceptedDecisions);
        Assert.Null(await host.Services.GetRequiredService<IEntityStore>().GetAsync(new EntityId("record", "asset-registry", request.Target.RecordId)));
        Assert.Null(await host.Registry.GetByIdAsync(Tenant, new RegistryEntityId(request.Target.RecordId)));
        Assert.DoesNotContain(await host.RowsAsync(), row => row.EventType.Value == "RecordWritten");
    }

    private static void AssertRefusalReceipt(Host host, JsonElement body, AuditRecord row, string code)
    {
        Assert.Equal("AuthorizationRefused", row.EventType.Value);
        Assert.Equal(code, row.Payload.Payload.Body["code"]);
        Assert.Equal("records:write", row.Payload.Payload.Body["permission"]);
        Assert.Equal(Tenant, row.TenantId);
        Assert.Equal("selected-principal", row.Actor!.Value.Value);
        Assert.Equal(Assert.Single(host.Requests).At, row.OccurredAt);
        Assert.NotEqual(Guid.Empty, row.AuditId);
        Assert.Equal(row.AuditId, body.GetProperty("auditId").GetGuid());
    }

    public enum RefusalAuditMode { Recorded, Absent, Failed }

    private static void AssertRecordRequest(AuthorizationGateRequest request)
    {
        Assert.Equal("records:write", request.Act.Operation.Value);
        Assert.Equal("record", request.Target.RecordKind);
        Assert.True(Guid.TryParseExact(request.Target.RecordId, "D", out var id));
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal("/records/" + request.Target.RecordId, request.Act.Scope.Value);
        Assert.Equal("/records/" + request.Target.RecordId, request.Target.Scope.Value);
        Assert.Equal("selected-principal", request.Principal.Value);
        Assert.Equal("10840000-0000-0000-0000-000000000001", request.Tenant.Value);
        Assert.Equal(Guid.Parse("43300000-0000-4000-8000-000000000084"), request.CorrelationId);
    }

    private static void AssertStages(List<WritePipelineStage> stages) => Assert.Equal(new[]
    {
        WritePipelineStage.Authorize, WritePipelineStage.Bind, WritePipelineStage.Mutate,
        WritePipelineStage.Validate, WritePipelineStage.Commit, WritePipelineStage.React,
    }, stages);

    private sealed class Host : IAsyncDisposable, IWritePipelineObserver
    {
        private WebApplication _app = null!;
        private HttpClient _client = null!;
        private ServiceProvider _teamServices = null!;
        private readonly HashSet<Guid> _seedRows = [];
        public bool AllowWrite { get; set; } = true;
        public bool FailRegistryIndex { get; set; }
        public EntityId? ReturnedId { get; set; }
        public List<AuthorizationGateRequest> Requests { get; } = [];
        public List<WritePipelineStage> Stages { get; } = [];
        public CountingClock Clock { get; } = new();
        public IEntityTypeRegistry Types { get; } = Substitute.For<IEntityTypeRegistry>();
        public IRegistryEntityRepository RegistryWrites { get; } = Substitute.For<IRegistryEntityRepository>();
        public IFormDefinitionStore Forms { get; } = Substitute.For<IFormDefinitionStore>();
        public IEntityMutationStore Mutations { get; } = Substitute.For<IEntityMutationStore>();
        public CountingValidator Validator { get; private set; } = null!;
        public CapturingTrail Trail { get; private set; } = null!;
        public IRegistryEntityRepository Registry { get; private set; } = null!;
        public SchemaId Schema { get; private set; }
        public IServiceProvider Services => _app.Services;
        public NodeEntityWriter Writer => Services.GetRequiredService<NodeEntityWriter>();
        public void OnStage(WritePipelineStage stage) => Stages.Add(stage);

        public static async Task<Host> StartAsync(bool noWriter = false, bool publishForm = true,
            string? schemaText = null, RuleDefinition[]? rules = null, bool unknownSchema = false,
            RefusalAuditMode auditMode = RefusalAuditMode.Recorded)
        {
            var host = new Host();
            var grantScopes = new List<ScopeExpression> { ScopeExpression.Parse("/") };
            var source = new TestRouteGate.ScopedGrantSource(request =>
            {
                host.Requests.Add(request);
                return request.Act.Operation.Value != "records:write" || host.AllowWrite;
            }, grantScopes);
            var gate = new AuthorizationGate(source, new EmptyRecordStandingResolver(), source);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddTestDesktopOperator();
            builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
                Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
            builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
                Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
            builder.Services.AddTestAuthorizationGate();
            builder.Services.AddSingleton(gate);
            builder.Services.AddTestNodeForms(configureWriters: (services, mutations, _) =>
                services.AddSingleton(sp =>
                {
                    var realMutations = mutations(sp);
                    host.Mutations.CreateAsync(Arg.Any<ValidatedRecordBody>(), Arg.Any<CreateOptions>(), Arg.Any<CancellationToken>())
                        .Returns(call => host.ReturnedId is { } returnedId ? Task.FromResult(returnedId)
                            : realMutations.CreateAsync(call.Arg<ValidatedRecordBody>(), call.Arg<CreateOptions>(), call.Arg<CancellationToken>()));
                    host.Validator = new CountingValidator(sp.GetRequiredKeyedService<IEntityValidator>(CompiledSchemaEntityValidator.RecordWriteKey));
                    var refusals = auditMode == RefusalAuditMode.Absent ? null : sp.GetRequiredService<AuthorizationRefusalAudit>();
                    if (auditMode == RefusalAuditMode.Failed)
                    {
                        var failedTrail = Substitute.For<IAuditTrail>();
                        failedTrail.AppendAsync(Arg.Any<AuditRecord>(), Arg.Any<CancellationToken>())
                            .Returns(_ => ValueTask.FromException(new InvalidOperationException("private-audit-sentinel")));
                        refusals = new AuthorizationRefusalAudit(failedTrail, sp.GetRequiredService<IOperationSigner>(),
                            sp.GetRequiredService<ILogger<AuthorizationRefusalAudit>>());
                    }
                    return new NodeEntityWriter(null!, host.Mutations, host.Validator, gate,
                        refusals, sp.GetRequiredService<AuthorizedActAudit>(), host);
                }));
            builder.Services.AddAuthorizedActAudit();
            builder.Services.AddAuthorizationRefusalAudit();
            builder.Services.AddNodeAssetRegistry();
            builder.Services.AddSingleton<IAuthorizedAuditTrail>(sp =>
                host.Trail = new CapturingTrail(sp.GetRequiredService<InMemoryAuditTrail>()));
            host._app = builder.Build();
            var realTypes = host.Services.GetRequiredService<IEntityTypeRegistry>();
            host.Registry = host.Services.GetRequiredService<IRegistryEntityRepository>();
            host.Schema = unknownSchema ? new SchemaId("records.unavailable-hidden-schema")
                : (await host.Services.GetRequiredService<ISchemaRegistry>().RegisterAsync(schemaText ??
                    """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""")).Id;
            var form = new FormDefinition(new FormDefinitionId("create-order.capture"), new SemanticVersion(1, 0, 0),
                FormDefinitionStatus.Draft, Tenant, IdentityRef.System, host.Schema,
                new HarborlineOverlay(new Dictionary<string, FieldOverlay>(), [], rules ?? [], InternationalizedText.FromInvariant("Create")),
                null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            var realForms = host.Services.GetRequiredService<IFormDefinitionStore>();
            await realForms.RegisterAsync(form);
            if (publishForm) await realForms.PublishAsync(new DefinitionCoordinates(Tenant, form.Id.Value, form.Version.ToString()));
            await realTypes.CreateTypeAsync(new EntityType { Id = new EntityTypeId("tenant-type"), TenantId = Tenant,
                Descriptor = new EntityTypeDescriptor("Tenant", EntityTrait.Movable), Provenance = CascadeLayer.Tenant },
                new Instant(DateTimeOffset.UnixEpoch));
            realTypes.SeedType(new EntityTypeSeed(new EntityTypeId("seed-type"), new EntityTypeDescriptor("Seed", EntityTrait.Movable), CascadeLayer.Pack));
            realTypes.SeedType(new EntityTypeSeed(new EntityTypeId("bound-type"),
                new EntityTypeDescriptor("Bound", EntityTrait.Movable, PropertyFormBinding: new FormBindingRef(form.Id, form.Version)), CascadeLayer.Pack));
            grantScopes.Clear();
            grantScopes.Add(ScopeExpression.Parse("/records"));
            host.Types.GetTypeAsync(Arg.Any<TenantId>(), Arg.Any<EntityTypeId>(), Arg.Any<CancellationToken>())
                .Returns(call => realTypes.GetTypeAsync(call.Arg<TenantId>(), call.Arg<EntityTypeId>(), call.Arg<CancellationToken>()));
            host.Types.GetSeed(Arg.Any<EntityTypeId>()).Returns(call => realTypes.GetSeed(call.Arg<EntityTypeId>()));
            host.Types.TryResolvePropertyFormAsync(Arg.Any<TenantId>(), Arg.Any<EntityTypeId>(), Arg.Any<CancellationToken>())
                .Returns(call => realTypes.TryResolvePropertyFormAsync(call.Arg<TenantId>(), call.Arg<EntityTypeId>(), call.Arg<CancellationToken>()));
            host.Forms.GetAsync(Arg.Any<DefinitionCoordinates>(), Arg.Any<CancellationToken>())
                .Returns(call => realForms.GetAsync(call.Arg<DefinitionCoordinates>(), call.Arg<CancellationToken>()));
            host.RegistryWrites.UpsertAsync(Arg.Any<RegistryEntity>(), Arg.Any<Instant>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(call => host.FailRegistryIndex
                    ? Task.FromException(new InvalidOperationException("private-index-sentinel"))
                    : host.Registry.UpsertAsync(call.Arg<RegistryEntity>(), call.Arg<Instant>(), call.Arg<string?>(), call.Arg<CancellationToken>()));
            host._teamServices = new ServiceCollection().BuildServiceProvider();
            var active = new ActiveTeam(new TeamContext(new TeamId(Guid.Parse(Tenant.Value)), "Create", host._teamServices, TimeProvider.System));
            AuthorizationDenialTranslation.Use(host._app);
            host._app.Use(async (http, next) =>
            {
                http.Features.Set(new SelectedSessionRequestPrincipal("account", Tenant, new PrincipalUserId("selected-principal"),
                    new CanonicalPartyReference("selected-party"), "membership", 1, [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination"));
                http.Features.Set(new SelectedRequestCorrelation(Guid.Parse("43300000-0000-4000-8000-000000000084")));
                await next(http);
            });
            var writer = new PackBoundRegistryRecordWriter(host.Forms, host.Writer,
                host.Services.GetRequiredService<IEntityStore>(), host.RegistryWrites);
            AssetRegistryRoutes.Map(host._app.MapDeviceReachableProductDataGroup(), host.Types, host.RegistryWrites,
                host.Services.GetRequiredService<ITypedRelationshipStore>(), host.Services.GetRequiredService<IConditionAssessmentStore>(),
                host.Services.GetRequiredService<IFormSubmissionRecordStore>(), active, host.Clock, noWriter ? null : writer);
            await host._app.StartAsync();
            var addresses = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            host._client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.Single()) };
            foreach (var row in await host.RowsAsync()) host._seedRows.Add(row.AuditId);
            host.Requests.Clear(); host.Stages.Clear(); host.Trail.AcceptedDecisions.Clear();
            host.Types.ClearReceivedCalls(); host.Forms.ClearReceivedCalls(); host.Mutations.ClearReceivedCalls(); host.RegistryWrites.ClearReceivedCalls();
            return host;
        }

        public Task<HttpResponseMessage> PostAsync<T>(T body) => _client.PostAsJsonAsync("/api/local-node/asset-registry/entities", body);
        public async Task<List<AuditRecord>> RowsAsync()
        {
            var rows = new List<AuditRecord>();
            await foreach (var row in Services.GetRequiredService<IAuditTrail>().QueryAsync(new AuditQuery(Tenant)))
                if (!_seedRows.Contains(row.AuditId)) rows.Add(row);
            return rows;
        }
        public void AssertNoStateAccess()
        {
            Assert.Empty(Types.ReceivedCalls()); Assert.Empty(Forms.ReceivedCalls());
            Assert.Empty(Mutations.ReceivedCalls()); Assert.Empty(RegistryWrites.ReceivedCalls());
            Assert.Equal(0, Validator.Calls); Assert.Empty(Stages);
        }
        public async ValueTask DisposeAsync()
        {
            _client?.Dispose();
            if (_app is not null) { await _app.StopAsync(); await _app.DisposeAsync(); }
            if (_teamServices is not null) await _teamServices.DisposeAsync();
        }
    }

    private sealed class CountingClock : TimeProvider
    {
        public int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return new DateTimeOffset(2026, 10, 17, 19, 0, 0, TimeSpan.Zero); }
    }
    private sealed class CountingValidator(IEntityValidator inner) : IEntityValidator
    {
        public int Calls { get; private set; }
        public EntityValidationException? Failure { get; set; }
        public Task ValidateAsync(SchemaId schema, JsonDocument body, CancellationToken ct = default)
        { Calls++; return inner.ValidateAsync(schema, body, ct); }
        public Task ValidateRecordAsync(SchemaId schema, JsonDocument body, TenantId tenant, EntityBinding? binding, DateTimeOffset at, CancellationToken ct = default)
        { Calls++; return Failure is { } failure ? Task.FromException(failure) : inner.ValidateRecordAsync(schema, body, tenant, binding, at, ct); }
    }
    private sealed class CapturingTrail(InMemoryAuditTrail inner) : IAuthorizedAuditTrail
    {
        public List<AuthorizationDecision> AcceptedDecisions { get; } = [];
        public ValueTask AppendAsync(AuditRecord record, CancellationToken ct = default) => inner.AppendAsync(record, ct);
        public IAsyncEnumerable<AuditRecord> QueryAsync(AuditQuery query, CancellationToken ct = default) => inner.QueryAsync(query, ct);
        public ValueTask AppendAuthorizedAsync(AuditRecord record, AuthorizationDecision decision, CancellationToken ct = default, SeparationOfDutyDecision? approval = null)
        { AcceptedDecisions.Add(decision); return inner.AppendAuthorizedAsync(record, decision, ct, approval); }
    }
    private sealed class ActiveTeam(TeamContext active) : IActiveTeamAccessor
    {
        public TeamContext? Active => active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged { add { } remove { } }
    }
}
