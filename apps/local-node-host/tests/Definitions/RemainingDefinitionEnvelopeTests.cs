using System.Text.Json;
using System.Text.Json.Nodes;

using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Taxonomy.Models;
using NSubstitute;

using CatalogTemplateDefinition = Harborline.Api.Foundation.Catalog.Templates.TemplateDefinition;
using CatalogTemplateKind = Harborline.Api.Foundation.Catalog.Templates.TemplateKind;
using DocumentsTemplateDefinition = Harborline.Api.Foundation.Documents.Model.TemplateDefinition;

namespace Harborline.Api.LocalNodeHost.Tests.Definitions;

/// <summary>Exercises the remaining public definition models through their control envelopes.</summary>
public sealed class RemainingDefinitionEnvelopeTests
{
    [Fact(DisplayName = "an authoritative taxonomy keeps Harborline as its envelope scope")]
    public void Authoritative_taxonomy_keeps_legacy_name_as_its_envelope_scope()
    {
        var definition = new TaxonomyDefinition
        {
            Id = new TaxonomyDefinitionId("Sunfish", "Compliance", "Controls"),
            Version = new TaxonomyVersion(2, 1, 0),
            Governance = TaxonomyGovernanceRegime.Authoritative,
            Description = "Authoritative compliance controls.",
            Owner = ActorId.Harborline,
            PublishedAt = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero),
        };

        Assert.Equal(ActorId.Harborline, definition.Envelope.Tenant);
        Assert.IsType<ActorId>(definition.Envelope.Tenant);
        Assert.Equal(definition.Id, definition.Envelope.Identity);
        Assert.Equal(definition.Version, definition.Envelope.Version);
        Assert.Null(definition.Envelope.Provenance);
    }

    [Fact(DisplayName = "workflow mapper and store carry typed envelope coordinates")]
    public async Task Workflow_mapper_and_store_carry_typed_envelope_coordinates()
    {
        var authored = JsonSerializer.SerializeToElement(new
        {
            initialState = "draft",
            states = new object[]
            {
                new { id = "draft", kind = "Normal" },
                new { id = "complete", kind = "Terminal" },
            },
            triggers = new object[]
            {
                new { id = "complete", kind = "Event", eventType = "Completed" },
            },
            transitions = new object[]
            {
                new { id = "finish", from = "draft", on = "complete", to = "complete" },
            },
            actions = Array.Empty<object>(),
            guards = Array.Empty<object>(),
        });
        var model = WorkflowDefinitionWireMapper.ToModel(
            authored,
            tenant: "tenant-workflow-envelope",
            key: "workflow.approval",
            version: "2.3.4");

        Assert.IsType<WorkflowDefinitionKey>(model.Envelope.Identity);
        Assert.IsType<WorkflowDefinitionVersion>(model.Envelope.Version);
        Assert.IsType<TenantId>(model.Envelope.Tenant);
        Assert.Equal("workflow.approval", model.Key);
        Assert.Equal("2.3.4", model.Version);
        Assert.Equal("tenant-workflow-envelope", model.Tenant);

        IWorkflowDefinitionStore store = new EntityStoreWorkflowDefinitionStore(
            new InMemoryEntityStore(new InMemoryAssetStorage(), TimeProvider.System),
            Substitute.For<IWorkflowAdmissionValidator>(),
            TimeProvider.System);
        await store.RegisterAsync(model, authored);
        var stored = await store.GetAsync(new DefinitionCoordinates(
            new TenantId("tenant-workflow-envelope"), "workflow.approval", "2.3.4"));

        Assert.Equal(model.Envelope.Identity, stored.Envelope.Identity);
        Assert.Equal(model.Envelope.Version, stored.Envelope.Version);
        Assert.Equal(model.Envelope.Tenant, stored.Envelope.Tenant);
        Assert.Equal(model.Envelope.CascadeLayer, stored.Envelope.CascadeLayer);
        Assert.Equal(model.Envelope.Provenance, stored.Envelope.Provenance);
        Assert.Empty(stored.Envelope.Requires);
        Assert.Equal("workflow.approval", stored.Key);
        Assert.Equal("2.3.4", stored.Version);
        Assert.Equal("tenant-workflow-envelope", stored.Tenant);
    }

    [Fact(DisplayName = "catalog template legacy metadata resolves through a tenant envelope")]
    public void Catalog_template_legacy_metadata_resolves_through_a_tenant_envelope()
    {
        var envelope = new DefinitionEnvelope<string, string, TenantId, string?>(
            Identity: "catalog.lease-renewal",
            Version: "3.2.1",
            Tenant: new TenantId("tenant-catalog-template"),
            CascadeLayer: CascadeLayer.Pack,
            Provenance: "catalog.base-template@2.0.0",
            Requires: Array.Empty<DefinitionRequirement>());
        var template = new CatalogTemplateDefinition(
            Envelope: envelope,
            Kind: CatalogTemplateKind.Form,
            DataSchema: JsonNode.Parse("""{"type":"object"}""")!,
            UiSchema: JsonNode.Parse("""{"type":"VerticalLayout"}""")!);

        Assert.Equal(envelope, template.Envelope);
        Assert.Equal("catalog.lease-renewal", template.Id);
        Assert.Equal("3.2.1", template.Version);
        Assert.Equal("catalog.base-template@2.0.0", template.BaseRef);
        Assert.Equal(new TenantId("tenant-catalog-template"), template.Envelope.Tenant);
    }

    [Fact(DisplayName = "rule definition identity resolves through its tenant envelope")]
    public void Rule_definition_identity_resolves_through_its_tenant_envelope()
    {
        var envelope = new DefinitionEnvelope<string, string, TenantId, string?>(
            Identity: "rule.lease.total-positive",
            Version: "1.0.0",
            Tenant: new TenantId("tenant-rule-definition"),
            CascadeLayer: CascadeLayer.Tenant,
            Provenance: null,
            Requires: Array.Empty<DefinitionRequirement>());
        var rule = new RuleDefinition(
            Envelope: envelope,
            Tier: RuleTier.JsonLogic,
            Scope: RuleScope.Schema,
            ScopeTarget: string.Empty,
            Expression: """{">":[{"var":"total"},0]}""",
            Action: RuleActionKind.Validate);

        Assert.Equal(envelope, rule.Envelope);
        Assert.Equal("rule.lease.total-positive", rule.Id);
        Assert.Equal(new TenantId("tenant-rule-definition"), rule.Envelope.Tenant);
    }

    [Fact(DisplayName = "legacy catalog template JSON still resolves old properties through an envelope")]
    public void Legacy_catalog_template_json_still_resolves_old_properties_through_an_envelope()
    {
        const string json = """
            {
              "Id": "catalog.legacy-template",
              "Version": "1.4.0",
              "Kind": 0,
              "DataSchema": { "type": "object" },
              "UiSchema": { "type": "VerticalLayout" },
              "BaseRef": "catalog.base@1.0.0"
            }
            """;

        var template = JsonSerializer.Deserialize<CatalogTemplateDefinition>(json);

        Assert.NotNull(template);
        Assert.Equal("catalog.legacy-template", template.Id);
        Assert.Equal("1.4.0", template.Version);
        Assert.Equal("catalog.base@1.0.0", template.BaseRef);
        Assert.Equal(TenantId.System, template.Envelope.Tenant);
    }

    [Fact(DisplayName = "legacy documents template JSON still resolves old properties through an envelope")]
    public void Legacy_documents_template_json_still_resolves_old_properties_through_an_envelope()
    {
        const string json = """
            {
              "Key": "documents.legacy-invoice",
              "Version": "2.0.3",
              "DocumentType": "invoice",
              "RecordType": { "RecordType": "invoice", "Version": "1" },
              "Locale": { "Kind": 1, "Tag": null },
              "Style": null,
              "Structure": []
            }
            """;

        var template = JsonSerializer.Deserialize<DocumentsTemplateDefinition>(json);

        Assert.NotNull(template);
        Assert.Equal("documents.legacy-invoice", template.Key);
        Assert.Equal("2.0.3", template.Version);
        Assert.Equal(TenantId.System, template.Envelope.Tenant);
    }

    [Fact(DisplayName = "legacy rule JSON still resolves its old id through an envelope")]
    public void Legacy_rule_json_still_resolves_its_old_id_through_an_envelope()
    {
        const string json = """
            {
              "Id": "rule.legacy-positive-total",
              "Tier": 1,
              "Scope": 2,
              "ScopeTarget": "",
              "Expression": "true",
              "Action": 0,
              "ErrorMessage": null,
              "Presentation": null
            }
            """;

        var rule = JsonSerializer.Deserialize<RuleDefinition>(json);

        Assert.NotNull(rule);
        Assert.Equal("rule.legacy-positive-total", rule.Id);
        Assert.Equal("0.0.0", rule.Envelope.Version);
        Assert.Equal(TenantId.System, rule.Envelope.Tenant);
    }
}
