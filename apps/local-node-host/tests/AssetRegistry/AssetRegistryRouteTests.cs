using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.AssetRegistry;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.AssetRegistry;

/// <summary>
/// ADR 0101 Rev 3.1 Wave 2b — the Asset Type System goes VISIBLE + LIVE on the node. Route-level
/// end-to-end tests over a real in-process Kestrel listener using the SAME production composition
/// (<see cref="NodeFormsComposition.AddNodeForms"/> + <see cref="NodeAssetRegistryComposition.AddNodeAssetRegistry"/>)
/// and the SAME route handlers the hosted endpoints register. The crown-jewel test proves LIVE capture:
/// submitting a condition-rating inspection form through the real forms submit route writes the entity's
/// typed condition assessment ("one act, two artifacts") — readable back over the condition-history
/// route — through the decorated, gate-protected projector.
/// </summary>
public sealed class AssetRegistryRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000a5501"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbb0000-0000-0000-0000-0000000b5501"));
    private static readonly ActorId Operator = new("local");

    private const string AssetBase = "/api/local-node/asset-registry";
    private const string FormsBase = "/api/local-node/forms";
    private const string ConditionForm = "condition.capture.v1";

    /// <summary>
    /// A form whose JSON schema admits a grade WIDER (1..10) than its binding's <c>ScaleMax</c> (5) — the
    /// exact F3 shape: a grade that passes schema validation but exceeds the binding scale, so the
    /// projector audit-skips it. The route must now surface that skip (never silent success).
    /// </summary>
    private const string WideConditionForm = "condition.capture.wide.v1";

    /// <summary>
    /// A form whose condition-rating binding sources its target from the VISIT CASE (#144). Its rating
    /// lands on the entity named by the <c>Into-Case-Ref</c> header — the proof that VisitCase resolution
    /// fires through the real submit route with the case header.
    /// </summary>
    private const string CaseConditionForm = "condition.capture.case.v1";

    private static readonly IReadOnlyList<string> OperatorRoles = new[] { FormsRoutes.NodeOperatorRole };

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // Field encryptor the recovery coordinator would normally supply (INV-S3).
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        var auditKeys = KeyPair.Generate();
        builder.Services.AddSingleton<IOperationSigner>(new Ed25519Signer(auditKeys));
        builder.Services.AddSingleton<IAuthorizedAuditTrail, InMemoryAuditTrail>();

        // The production forms composition, THEN the Wave-2b asset registry live wiring (which decorates
        // the forms engine so a submit fires the condition-capture projector).
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();
        builder.Services.AddNodeAssetRegistry();

        _app = builder.Build();

        // Register + publish the condition-rating inspection form (schema: a rating + a target ref).
        var registry = _app.Services.GetRequiredService<ISchemaRegistry>();
        var store = _app.Services.GetRequiredService<IFormDefinitionStore>();
        var tenant = ActiveTeamTenantContext.ProjectTenantId(TeamA);

        var schema = await registry.RegisterAsync(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "condition": { "type": "integer", "minimum": 1, "maximum": 5 },
                "asset_ref": { "type": "string" }
              },
              "required": ["condition"],
              "additionalProperties": false
            }
            """);

        var def = new FormDefinition(
            Id: new FormDefinitionId(ConditionForm),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: tenant,
            Owner: IdentityRef.System,
            SchemaRef: schema.Id,
            Overlay: new HarborlineOverlay(
                Fields: new Dictionary<string, FieldOverlay>
                {
                    ["condition"] = new(InternationalizedText.FromInvariant("Condition"), ControlHint: "condition-rating"),
                    ["asset_ref"] = new(InternationalizedText.FromInvariant("Asset"), ControlHint: "text"),
                },
                Sections: new[]
                {
                    new FormSection(
                        Id: "main",
                        Title: InternationalizedText.FromInvariant("Condition"),
                        Fields: new[] { "condition", "asset_ref" },
                        Access: new SectionAccess(
                            ReadRoles: new[] { FormsRoutes.NodeOperatorRole },
                            WriteRoles: new[] { FormsRoutes.NodeOperatorRole })),
                },
                Rules: Array.Empty<RuleDefinition>(),
                Title: InternationalizedText.FromInvariant("Condition Capture")),
            Lineage: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        await store.RegisterAsync(def);
        await store.PublishAsync(new DefinitionCoordinates(tenant, def.Id.Value, def.Version.ToString()));

        // Seed a pack type (visible to every tenant) + register the condition-rating field binding for
        // this tenant + form (config, never payload).
        _app.Services.GetRequiredService<IEntityTypeRegistry>().SeedType(new EntityTypeSeed(
            new EntityTypeId("water-heater"),
            new EntityTypeDescriptor("Water Heater", EntityTrait.Maintainable | EntityTrait.Movable),
            CascadeLayer.Pack));
        await _app.Services.GetRequiredService<IConditionRatingFieldBindingStore>().RegisterAsync(
            tenant, new ConditionRatingFieldBinding(
                new FormDefinitionId(ConditionForm), "/condition",
                ConditionEntityRefSource.SubmissionField, "asset_ref", ScaleMax: 5));

        // F3: a second form whose schema admits 1..10 but whose binding rates on a 1..5 scale — a grade
        // in (5, 10] passes validation but exceeds the binding, so the projector audit-skips it and the
        // route surfaces the skip.
        var wideSchema = await registry.RegisterAsync(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "condition": { "type": "integer", "minimum": 1, "maximum": 10 },
                "asset_ref": { "type": "string" }
              },
              "required": ["condition"],
              "additionalProperties": false
            }
            """);
        var wideDef = def with
        {
            Id = new FormDefinitionId(WideConditionForm),
            SchemaRef = wideSchema.Id,
        };
        await store.RegisterAsync(wideDef);
        await store.PublishAsync(new DefinitionCoordinates(tenant, wideDef.Id.Value, wideDef.Version.ToString()));
        await _app.Services.GetRequiredService<IConditionRatingFieldBindingStore>().RegisterAsync(
            tenant, new ConditionRatingFieldBinding(
                new FormDefinitionId(WideConditionForm), "/condition",
                ConditionEntityRefSource.SubmissionField, "asset_ref", ScaleMax: 5));

        // #144: a third form whose condition-rating binding sources its target from the VISIT CASE (not a
        // submission field). Its target is resolved from the `Into-Case-Ref` header the route now carries —
        // proving VisitCase resolution fires THROUGH the route (the crown-jewel #144 e2e).
        var caseDef = def with { Id = new FormDefinitionId(CaseConditionForm) };
        await store.RegisterAsync(caseDef);
        await store.PublishAsync(new DefinitionCoordinates(tenant, caseDef.Id.Value, caseDef.Version.ToString()));
        await _app.Services.GetRequiredService<IConditionRatingFieldBindingStore>().RegisterAsync(
            tenant, new ConditionRatingFieldBinding(
                new FormDefinitionId(CaseConditionForm), "/condition",
                ConditionEntityRefSource.VisitCase, ScaleMax: 5));

        _activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));

        // Map the SAME production routes both hosted endpoints register (no [FromServices]).
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        AssetRegistryRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _app.Services.GetRequiredService<IEntityTypeRegistry>(),
            _app.Services.GetRequiredService<IRegistryEntityRepository>(),
            _app.Services.GetRequiredService<ITypedRelationshipStore>(),
            _app.Services.GetRequiredService<IConditionAssessmentStore>(),
            _app.Services.GetRequiredService<IFormSubmissionRecordStore>(),
            _activeTeam,
            TimeProvider.System);
        FormsRoutes.Map(
            _app,
            _app.Services.GetRequiredService<IFormEngine>(),
            _app.Services.GetRequiredService<IFormCapabilityIssuer>(),
            _app.Services.GetRequiredService<IFormCapabilityVerifier>(),
            _activeTeam,
            OperatorRoles,
            TimeProvider.System);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task<string> CreateEntityAsync(string displayName, string type = "water-heater")
    {
        var resp = await _client.PostAsJsonAsync($"{AssetBase}/entities", new { type, displayName });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetString()!;
    }

    /// <summary>Submits <paramref name="body"/> to <paramref name="formId"/> carrying the #144 Into-Case-Ref header.</summary>
    private async Task<HttpResponseMessage> SubmitIntoAsync(string formId, object body, string caseRef)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{FormsBase}/{formId}/submit")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.TryAddWithoutValidation(FormsRoutes.IntoCaseRefHeader, caseRef);
        return await _client.SendAsync(req);
    }

    [Fact(DisplayName = "types: the effective catalog lists the seeded pack type with its traits")]
    public async Task Types_List_Includes_Seed()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types");
        var types = doc.GetProperty("types");

        var wh = types.EnumerateArray().Single(t => t.GetProperty("id").GetString() == "water-heater");
        Assert.Equal("Water Heater", wh.GetProperty("displayName").GetString());
        var traits = wh.GetProperty("traits").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("maintainable", traits);
        Assert.Contains("movable", traits);
    }

    [Fact(DisplayName = "entities: create → get by id → list")]
    public async Task Entities_Create_Get_List()
    {
        var id = await CreateEntityAsync("Unit 4B water heater");

        var detail = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{id}");
        Assert.Equal(id, detail.GetProperty("id").GetString());
        Assert.Equal("water-heater", detail.GetProperty("type").GetString());
        Assert.Equal("Unit 4B water heater", detail.GetProperty("displayName").GetString());

        var list = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities?type=water-heater");
        Assert.Contains(list.GetProperty("entities").EnumerateArray(), e => e.GetProperty("id").GetString() == id);
    }

    [Fact(DisplayName = "tree: a contains edge shows a child + breadcrumb as-of now")]
    public async Task Tree_Contains_Edge()
    {
        var building = await CreateEntityAsync("Building A");
        var heater = await CreateEntityAsync("Water heater");

        var edge = await _client.PostAsJsonAsync($"{AssetBase}/edges",
            new { kind = "contains", from = building, to = heater });
        Assert.Equal(HttpStatusCode.Created, edge.StatusCode);

        var tree = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{building}/tree");
        Assert.Contains(tree.GetProperty("children").EnumerateArray(), c => c.GetProperty("id").GetString() == heater);

        // The child's breadcrumb path resolves to its container.
        var childTree = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heater}/tree");
        var path = childTree.GetProperty("path").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Contains(building, path);
    }

    [Fact(DisplayName = "edges: an endpoint that does not exist is rejected (static error, no payload reflection)")]
    public async Task Edge_Rejected_When_Endpoint_Missing()
    {
        var real = await CreateEntityAsync("Real");
        var resp = await _client.PostAsJsonAsync($"{AssetBase}/edges",
            new { kind = "contains", from = real, to = "does-not-exist" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "LIVE CAPTURE: submitting the condition form writes the entity's condition (one act, two artifacts)")]
    public async Task Submitting_The_Condition_Form_Captures_The_Entity_Condition()
    {
        var heaterId = await CreateEntityAsync("Water heater to inspect");

        // Before: no condition history.
        var before = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heaterId}/condition");
        Assert.Empty(before.GetProperty("history").EnumerateArray());

        // ONE act: submit the condition-rating inspection form through the REAL forms submit route.
        var submit = await _client.PostAsJsonAsync(
            $"{FormsBase}/{ConditionForm}/submit",
            new { condition = 4, asset_ref = heaterId });
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        // TWO artifacts: the submission persisted AND the entity's typed condition assessment landed —
        // through the decorated ProjectingFormEngine + the gate-protected projector, live.
        var after = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heaterId}/condition");
        var record = Assert.Single(after.GetProperty("history").EnumerateArray());
        Assert.Equal(4, record.GetProperty("grade").GetInt32());
        Assert.Equal(5, record.GetProperty("scaleMax").GetInt32());
        Assert.Equal(ConditionForm, record.GetProperty("sourceForm").GetString());   // provenance back to the form
        Assert.Equal("/condition", record.GetProperty("sourceField").GetString());   // and the field
    }

    [Fact(DisplayName = "F3: an out-of-range grade is audit-skipped AND surfaced in the response (never silent success)")]
    public async Task Out_Of_Range_Grade_Surfaces_A_Skip_And_Records_Nothing()
    {
        var heaterId = await CreateEntityAsync("Water heater on a 1-5 scale");

        // Grade 8 passes the wide form's schema (max 10) but exceeds the binding's scale (5). The submit
        // still COMMITS (201) — a skip is not a validation failure — but the projector could not land the
        // condition capture.
        var submit = await _client.PostAsJsonAsync(
            $"{FormsBase}/{WideConditionForm}/submit",
            new { condition = 8, asset_ref = heaterId });
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        // F3: the response tells the user honestly — projection "skipped" + a machine reason + the field.
        var body = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("skipped", body.GetProperty("projection").GetString());
        var skip = Assert.Single(body.GetProperty("skips").EnumerateArray());
        Assert.Equal("grade-out-of-range", skip.GetProperty("reason").GetString());
        Assert.Equal("/condition", skip.GetProperty("field").GetString());
        // The skip response never echoes a submitted value.
        Assert.False(skip.TryGetProperty("value", out _));

        // Skip-and-audit semantics unchanged: nothing landed on the entity's condition history.
        var after = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heaterId}/condition");
        Assert.Empty(after.GetProperty("history").EnumerateArray());
    }

    [Fact(DisplayName = "F3: an in-range submit still returns a bare 201 (no projection/skips fields — byte-identical)")]
    public async Task In_Range_Submit_Has_No_Skip_Fields()
    {
        var heaterId = await CreateEntityAsync("Water heater, good grade");

        var submit = await _client.PostAsJsonAsync(
            $"{FormsBase}/{WideConditionForm}/submit",
            new { condition = 4, asset_ref = heaterId });
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        var body = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("projection", out _)); // omitted-when-null: no skip, no pending
        Assert.False(body.TryGetProperty("skips", out _));

        var after = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heaterId}/condition");
        Assert.Single(after.GetProperty("history").EnumerateArray()); // the capture DID land
    }

    // ── #144 runtime form-fill: fill a form INTO a record via the Into-Case-Ref header ─────────────

    [Fact(DisplayName = "#144 LIVE CAPTURE via VisitCase THROUGH the route: the case header resolves the target")]
    public async Task Submitting_With_The_Case_Header_Resolves_VisitCase_And_Links_The_Submission()
    {
        var heaterId = await CreateEntityAsync("Water heater filled via a record page");

        // Before: no condition history AND no submitted forms.
        var beforeCond = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heaterId}/condition");
        Assert.Empty(beforeCond.GetProperty("history").EnumerateArray());
        var beforeSubs = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heaterId}/submissions");
        Assert.Empty(beforeSubs.GetProperty("submissions").EnumerateArray());

        // ONE act: submit the VISIT-CASE-bound condition form — NO asset_ref in the body; the ONLY thing
        // that names the target is the Into-Case-Ref header. This proves VisitCase resolves through the route.
        var submit = await SubmitIntoAsync(CaseConditionForm, new { condition = 3 }, heaterId);
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);
        var submitBody = await submit.Content.ReadFromJsonAsync<JsonElement>();
        var instanceId = submitBody.GetProperty("instanceId").GetString()!;

        // Artifact 1 — the condition assessment landed via VisitCase (resolved from the header).
        var afterCond = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heaterId}/condition");
        var assessment = Assert.Single(afterCond.GetProperty("history").EnumerateArray());
        Assert.Equal(3, assessment.GetProperty("grade").GetInt32());

        // Artifact 2 — the generic submission → record link landed, listable + reopenable by instance.
        var afterSubs = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heaterId}/submissions");
        var row = Assert.Single(afterSubs.GetProperty("submissions").EnumerateArray());
        Assert.Equal(CaseConditionForm, row.GetProperty("formId").GetString());
        Assert.Equal(instanceId, row.GetProperty("instanceId").GetString());
    }

    [Fact(DisplayName = "#144 submissions: a submission-field form filled into a record is also linked")]
    public async Task A_submission_field_form_filled_into_a_record_is_linked_too()
    {
        var heaterId = await CreateEntityAsync("Water heater, submission-field form");

        // The submission-field-bound form ALSO gets the generic record link (the link is not binding-gated).
        var submit = await SubmitIntoAsync(ConditionForm, new { condition = 4, asset_ref = heaterId }, heaterId);
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        var subs = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heaterId}/submissions");
        var row = Assert.Single(subs.GetProperty("submissions").EnumerateArray());
        Assert.Equal(ConditionForm, row.GetProperty("formId").GetString());
    }

    [Fact(DisplayName = "#144 submissions: a submit with NO case header leaves no record link")]
    public async Task A_submit_without_the_case_header_leaves_no_link()
    {
        var heaterId = await CreateEntityAsync("Water heater, standalone fill");

        var submit = await _client.PostAsJsonAsync(
            $"{FormsBase}/{ConditionForm}/submit", new { condition = 4, asset_ref = heaterId });
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        // The condition still landed (via asset_ref) but no generic record link was written (no case header).
        var subs = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/entities/{heaterId}/submissions");
        Assert.Empty(subs.GetProperty("submissions").EnumerateArray());
    }

    [Fact(DisplayName = "#144 submissions: an unknown entity id is an opaque 404")]
    public async Task Submissions_For_An_Unknown_Entity_Is_404()
    {
        var resp = await _client.GetAsync($"{AssetBase}/entities/does-not-exist/submissions");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "#144 submissions: a record's submitted forms are invisible to another tenant")]
    public async Task Submissions_Are_Tenant_Scoped()
    {
        var heaterId = await CreateEntityAsync("Team A record");
        var submit = await SubmitIntoAsync(CaseConditionForm, new { condition = 2 }, heaterId);
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        _activeTeam.Active = TeamContextFor(TeamB, "Team B");
        var crossTenant = await _client.GetAsync($"{AssetBase}/entities/{heaterId}/submissions");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode); // opaque 404 — never another org's rows

        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
    }

    [Fact(DisplayName = "tenant: switching the active team hides another org's entity (opaque 404)")]
    public async Task Tenant_Isolation_Is_Server_Side()
    {
        var id = await CreateEntityAsync("Team A only");

        _activeTeam.Active = TeamContextFor(TeamB, "Team B");
        var resp = await _client.GetAsync($"{AssetBase}/entities/{id}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
    }

    // ── Type management (the Type Manager editor) ─────────────────────────────────────

    [Fact(DisplayName = "types: create a tenant greenfield type → get by id → appears in the catalog")]
    public async Task Types_Create_Greenfield_Get_And_List()
    {
        var create = await _client.PostAsJsonAsync($"{AssetBase}/types", new
        {
            id = "shed",
            displayName = "Storage shed",
            traits = new[] { "container", "maintainable" },
            conditionScaleMax = 4,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var detail = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/shed");
        Assert.Equal("Storage shed", detail.GetProperty("displayName").GetString());
        Assert.Equal("Tenant", detail.GetProperty("provenance").GetString());
        Assert.False(detail.GetProperty("overridesSeed").GetBoolean());
        Assert.False(detail.GetProperty("seedExists").GetBoolean());
        Assert.Equal(4, detail.GetProperty("conditionScaleMax").GetInt32());
        var traits = detail.GetProperty("traits").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Contains("container", traits);
        Assert.Contains("maintainable", traits);

        var catalog = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types");
        Assert.Contains(catalog.GetProperty("types").EnumerateArray(), t => t.GetProperty("id").GetString() == "shed");
    }

    [Fact(DisplayName = "REFUTE-PROOF: editing a SEEDED type creates a tenant override — the shared seed is never mutated")]
    public async Task Editing_A_Seeded_Type_Creates_An_Override_And_Never_Mutates_The_Seed()
    {
        // Team A "edits" the pack-seeded water-heater type via the route.
        var put = await _client.PutAsJsonAsync($"{AssetBase}/types/water-heater", new
        {
            displayName = "Boiler (AcmeCo term)",
            traits = new[] { "maintainable", "movable" },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("overridesSeed").GetBoolean());
        Assert.Equal("Tenant", body.GetProperty("provenance").GetString());

        // Team A now sees its own override …
        var aView = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/water-heater");
        Assert.Equal("Boiler (AcmeCo term)", aView.GetProperty("displayName").GetString());
        Assert.True(aView.GetProperty("overridesSeed").GetBoolean());

        // … but the SHARED SEED is provably untouched: switch to Team B (which never overrode) and the
        // effective type is still the pristine PACK seed. A route "edit the seed" attempt cannot mutate it.
        _activeTeam.Active = TeamContextFor(TeamB, "Team B");
        var bView = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/water-heater");
        Assert.Equal("Water Heater", bView.GetProperty("displayName").GetString());
        Assert.Equal("Pack", bView.GetProperty("provenance").GetString());
        Assert.False(bView.GetProperty("overridesSeed").GetBoolean());
        Assert.True(bView.GetProperty("seedExists").GetBoolean());
        Assert.False(bView.GetProperty("hasTenantRow").GetBoolean());

        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
    }

    [Fact(DisplayName = "types: revert discards the override so the shared pack seed is effective again")]
    public async Task Revert_Restores_The_Pack_Seed()
    {
        await _client.PutAsJsonAsync($"{AssetBase}/types/water-heater", new
        {
            displayName = "Boiler",
            traits = new[] { "maintainable", "movable" },
        });

        var revert = await _client.PostAsync($"{AssetBase}/types/water-heater/revert", content: null);
        Assert.Equal(HttpStatusCode.OK, revert.StatusCode);
        var reverted = await revert.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(reverted.GetProperty("overridesSeed").GetBoolean());
        Assert.False(reverted.GetProperty("hasTenantRow").GetBoolean());

        var view = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/water-heater");
        Assert.Equal("Water Heater", view.GetProperty("displayName").GetString());
        Assert.Equal("Pack", view.GetProperty("provenance").GetString());
    }

    [Fact(DisplayName = "types: a tenant's created type is invisible to another tenant (opaque 404)")]
    public async Task Type_Create_Is_Tenant_Scoped()
    {
        var create = await _client.PostAsJsonAsync($"{AssetBase}/types", new
        {
            id = "acme-widget",
            displayName = "Acme widget",
            traits = new[] { "maintainable" },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        _activeTeam.Active = TeamContextFor(TeamB, "Team B");
        var crossTenant = await _client.GetAsync($"{AssetBase}/types/acme-widget");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
    }

    [Fact(DisplayName = "types: property-form + per-discipline inspection forms + capital planning round-trip")]
    public async Task Bindings_And_Capital_Planning_Round_Trip()
    {
        var put = await _client.PutAsJsonAsync($"{AssetBase}/types/water-heater", new
        {
            displayName = "Water heater",
            traits = new[] { "maintainable", "movable" },
            disciplines = new[] { "plumbing", "hvac" },
            propertyForm = new { definition = "wh-props", version = "2.1.0" },
            inspectionForms = new[]
            {
                new { discipline = "plumbing", definition = "wh-plumbing-insp", version = "1.0.0" },
                new { discipline = "hvac", definition = "wh-hvac-insp", version = "1.2.0" },
            },
            conditionScaleMax = 5,
            expectedUsefulLifeYears = 12,
            typicalReplacementCost = new { amount = 1400.50m, currency = "AED" },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var detail = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/water-heater");
        Assert.Equal("wh-props", detail.GetProperty("propertyForm").GetProperty("definition").GetString());
        Assert.Equal("2.1.0", detail.GetProperty("propertyForm").GetProperty("version").GetString());
        Assert.Equal(12, detail.GetProperty("expectedUsefulLifeYears").GetInt32());
        Assert.Equal("AED", detail.GetProperty("typicalReplacementCost").GetProperty("currency").GetString());
        Assert.Equal(1400.50m, detail.GetProperty("typicalReplacementCost").GetProperty("amount").GetDecimal());

        var inspections = detail.GetProperty("inspectionForms").EnumerateArray().ToArray();
        Assert.Equal(2, inspections.Length);
        Assert.Contains(inspections, i => i.GetProperty("discipline").GetString() == "plumbing"
            && i.GetProperty("definition").GetString() == "wh-plumbing-insp");
        Assert.Contains(inspections, i => i.GetProperty("discipline").GetString() == "hvac"
            && i.GetProperty("version").GetString() == "1.2.0");
    }

    [Fact(DisplayName = "types: a bad form version is rejected (static error)")]
    public async Task Editing_With_A_Bad_Form_Version_Is_Rejected()
    {
        var put = await _client.PutAsJsonAsync($"{AssetBase}/types/water-heater", new
        {
            displayName = "Water heater",
            traits = new[] { "maintainable" },
            propertyForm = new { definition = "wh-props", version = "not-a-version" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    // ── Ticket 155: the loss-confirmation gate on PUT /types/{id} ─────────────────────
    // PUT is full-replacement, so an omitted field IS a removal. The gate refuses any removal or
    // narrowing without an explicit disposition (migrate | flag | accept — the DefinitionPublisher
    // vocabulary), naming what would be lost; ordinary renames/additions stay one-click.

    private async Task BindWaterHeaterFormsAsync()
    {
        var bind = await _client.PutAsJsonAsync($"{AssetBase}/types/water-heater", new
        {
            displayName = "Water heater",
            traits = new[] { "maintainable", "movable" },
            disciplines = new[] { "plumbing" },
            propertyForm = new { definition = "wh-props", version = "2.1.0" },
            inspectionForms = new[]
            {
                new { discipline = "plumbing", definition = "wh-plumbing-insp", version = "1.0.0" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, bind.StatusCode); // additive — one-click, no disposition
    }

    [Fact(DisplayName = "155 GATE: dropping a form binding (by omission) without a disposition is refused, naming the loss")]
    public async Task Dropping_A_Form_Binding_Without_A_Disposition_Is_Refused()
    {
        await BindWaterHeaterFormsAsync();

        // The silent-null trap: bindings simply OMITTED from the PUT body. Must refuse, not null them.
        var drop = await _client.PutAsJsonAsync($"{AssetBase}/types/water-heater", new
        {
            displayName = "Water heater",
            traits = new[] { "maintainable", "movable" },
            disciplines = new[] { "plumbing" },
        });
        Assert.Equal(HttpStatusCode.Conflict, drop.StatusCode);
        var body = await drop.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("narrowing_requires_disposition", body.GetProperty("error").GetString());
        var losses = body.GetProperty("losses").EnumerateArray().Select(l => l.GetString()).ToArray();
        Assert.Contains("propertyForm:wh-props", losses);
        Assert.Contains("inspectionForm:plumbing", losses);

        // Nothing was silently nulled — the bindings are still effective.
        var detail = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/water-heater");
        Assert.Equal("wh-props", detail.GetProperty("propertyForm").GetProperty("definition").GetString());
        Assert.Single(detail.GetProperty("inspectionForms").EnumerateArray());
    }

    [Fact(DisplayName = "155 GATE: the same narrowing PUT WITH a disposition succeeds")]
    public async Task Dropping_A_Form_Binding_With_A_Disposition_Succeeds()
    {
        await BindWaterHeaterFormsAsync();

        var drop = await _client.PutAsJsonAsync($"{AssetBase}/types/water-heater", new
        {
            displayName = "Water heater",
            traits = new[] { "maintainable", "movable" },
            disciplines = new[] { "plumbing" },
            disposition = "accept",
        });
        Assert.Equal(HttpStatusCode.OK, drop.StatusCode);

        // The disposition is ECHOED so the choice and what it covered are client-visible + loggable
        // (response-only: per ticket 155's parked note, nothing is migrated or flagged yet).
        var body = await drop.Content.ReadFromJsonAsync<JsonElement>();
        var dispositioned = body.GetProperty("dispositioned");
        Assert.Equal("accept", dispositioned.GetProperty("kind").GetString());
        var echoed = dispositioned.GetProperty("losses").EnumerateArray().Select(l => l.GetString()).ToArray();
        Assert.Contains("propertyForm:wh-props", echoed);
        Assert.Contains("inspectionForm:plumbing", echoed);

        var detail = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/water-heater");
        Assert.True(!detail.TryGetProperty("propertyForm", out var pf) || pf.ValueKind == JsonValueKind.Null);
        Assert.Empty(detail.GetProperty("inspectionForms").EnumerateArray());
    }

    [Fact(DisplayName = "155 NO NEW FRICTION: a rename + addition on a bound type stays one-click")]
    public async Task Ordinary_Rename_And_Addition_Stay_One_Click()
    {
        await BindWaterHeaterFormsAsync();

        // Rename + ADD a discipline, KEEPING everything already bound — no disposition needed.
        var edit = await _client.PutAsJsonAsync($"{AssetBase}/types/water-heater", new
        {
            displayName = "Hot water heater",
            traits = new[] { "maintainable", "movable" },
            disciplines = new[] { "plumbing", "hvac" },
            propertyForm = new { definition = "wh-props", version = "2.1.0" },
            inspectionForms = new[]
            {
                new { discipline = "plumbing", definition = "wh-plumbing-insp", version = "1.0.0" },
            },
        });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var detail = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/water-heater");
        Assert.Equal("Hot water heater", detail.GetProperty("displayName").GetString());
        Assert.Equal("wh-props", detail.GetProperty("propertyForm").GetProperty("definition").GetString());
    }

    // NOTE: this test previously created the gauge with conditionScaleMax = 4 and asserted that
    // OMITTING it was gated. Under the descriptor's documented semantics (null = the platform
    // default of 5), 4 → null is an effective WIDENING to 5 — one-click, not a loss — so the test
    // is re-expressed with expectedUsefulLifeYears, whose omission IS a genuine removal.
    [Fact(DisplayName = "155 GATE: a greenfield type's omitted scalar is not silently nulled either")]
    public async Task Greenfield_Omitted_Scalar_Is_Gated_Too()
    {
        var create = await _client.PostAsJsonAsync($"{AssetBase}/types", new
        {
            id = "gauge",
            displayName = "Pressure gauge",
            traits = new[] { "maintainable" },
            expectedUsefulLifeYears = 12,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // expectedUsefulLifeYears omitted → a removal → refused without a disposition.
        var drop = await _client.PutAsJsonAsync($"{AssetBase}/types/gauge", new
        {
            displayName = "Pressure gauge",
            traits = new[] { "maintainable" },
        });
        Assert.Equal(HttpStatusCode.Conflict, drop.StatusCode);
        var body = await drop.Content.ReadFromJsonAsync<JsonElement>();
        var losses = body.GetProperty("losses").EnumerateArray().Select(l => l.GetString()).ToArray();
        Assert.Contains("expectedUsefulLifeYears", losses);

        // Still intact.
        var detail = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/gauge");
        Assert.Equal(12, detail.GetProperty("expectedUsefulLifeYears").GetInt32());

        // With a disposition the removal is deliberate and goes through.
        var accepted = await _client.PutAsJsonAsync($"{AssetBase}/types/gauge", new
        {
            displayName = "Pressure gauge",
            traits = new[] { "maintainable" },
            disposition = "flag",
        });
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var after = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/gauge");
        Assert.True(!after.TryGetProperty("expectedUsefulLifeYears", out var life) || life.ValueKind == JsonValueKind.Null);
    }

    [Fact(DisplayName = "155: a disposition outside the vocabulary is rejected (static error)")]
    public async Task An_Invalid_Disposition_Is_Rejected()
    {
        var put = await _client.PutAsJsonAsync($"{AssetBase}/types/water-heater", new
        {
            displayName = "Water heater",
            traits = new[] { "maintainable", "movable" },
            disposition = "yolo",
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_disposition", body.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "155: POST validates the disposition too — an invalid value is rejected on create")]
    public async Task An_Invalid_Disposition_On_Create_Is_Rejected()
    {
        var create = await _client.PostAsJsonAsync($"{AssetBase}/types", new
        {
            id = "yolo-type",
            displayName = "Yolo type",
            traits = new[] { "maintainable" },
            disposition = "yolo",
        });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        var body = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_disposition", body.GetProperty("error").GetString());
    }

    [Fact(DisplayName = "155 EFFECTIVE SCALE: lowering from the platform default (null → 2) is a real 5→2 and is refused")]
    public async Task Lowering_The_Default_Condition_Scale_Is_Refused()
    {
        // The water-heater seed has NO ConditionScaleMax — its effective scale is the platform
        // default (5). Setting 2 is therefore a genuine lowering and must be gated.
        var put = await _client.PutAsJsonAsync($"{AssetBase}/types/water-heater", new
        {
            displayName = "Water Heater",
            traits = new[] { "maintainable", "movable" },
            conditionScaleMax = 2,
        });
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("narrowing_requires_disposition", body.GetProperty("error").GetString());
        var losses = body.GetProperty("losses").EnumerateArray().Select(l => l.GetString()).ToArray();
        Assert.Contains("conditionScaleMax", losses);
    }

    [Fact(DisplayName = "155 EFFECTIVE SCALE: dropping an explicit 4 back to the default (5) is a widening — one-click")]
    public async Task Dropping_A_Four_Point_Scale_To_The_Default_Is_One_Click()
    {
        var create = await _client.PostAsJsonAsync($"{AssetBase}/types", new
        {
            id = "dial4",
            displayName = "Four-point dial",
            traits = new[] { "maintainable" },
            conditionScaleMax = 4,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // 4 → null is effectively 4 → 5: wider, so no disposition needed.
        var widen = await _client.PutAsJsonAsync($"{AssetBase}/types/dial4", new
        {
            displayName = "Four-point dial",
            traits = new[] { "maintainable" },
        });
        Assert.Equal(HttpStatusCode.OK, widen.StatusCode);
        var detail = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/dial4");
        Assert.True(!detail.TryGetProperty("conditionScaleMax", out var scale) || scale.ValueKind == JsonValueKind.Null);
    }

    [Fact(DisplayName = "155 EFFECTIVE SCALE: dropping an explicit 5 back to the default (5) is a no-op — one-click")]
    public async Task Dropping_A_Five_Point_Scale_To_The_Default_Is_One_Click()
    {
        var create = await _client.PostAsJsonAsync($"{AssetBase}/types", new
        {
            id = "dial5",
            displayName = "Five-point dial",
            traits = new[] { "maintainable" },
            conditionScaleMax = 5,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        // 5 → null is effectively 5 → 5: no change, so no disposition needed.
        var drop = await _client.PutAsJsonAsync($"{AssetBase}/types/dial5", new
        {
            displayName = "Five-point dial",
            traits = new[] { "maintainable" },
        });
        Assert.Equal(HttpStatusCode.OK, drop.StatusCode);
        var detail = await _client.GetFromJsonAsync<JsonElement>($"{AssetBase}/types/dial5");
        Assert.True(!detail.TryGetProperty("conditionScaleMax", out var scale) || scale.ValueKind == JsonValueKind.Null);
    }

    [Fact(DisplayName = "155 CHANGE vs DROP: a version-only repoint is gated with the distinct Changed labels")]
    public async Task A_Version_Only_Repoint_Is_Refused_With_The_Changed_Labels()
    {
        await BindWaterHeaterFormsAsync();

        // Same definitions, repointed versions: still gated (the route cannot see the version's own
        // widening/narrowing classification) but labeled as CHANGES, not removals.
        var repoint = await _client.PutAsJsonAsync($"{AssetBase}/types/water-heater", new
        {
            displayName = "Water heater",
            traits = new[] { "maintainable", "movable" },
            disciplines = new[] { "plumbing" },
            propertyForm = new { definition = "wh-props", version = "3.0.0" },
            inspectionForms = new[]
            {
                new { discipline = "plumbing", definition = "wh-plumbing-insp", version = "2.0.0" },
            },
        });
        Assert.Equal(HttpStatusCode.Conflict, repoint.StatusCode);
        var body = await repoint.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("narrowing_requires_disposition", body.GetProperty("error").GetString());
        var losses = body.GetProperty("losses").EnumerateArray().Select(l => l.GetString()).ToArray();
        Assert.Contains("propertyFormChanged:wh-props", losses);
        Assert.Contains("inspectionFormChanged:plumbing", losses);
        // The removal labels are reserved for actual removals.
        Assert.DoesNotContain("propertyForm:wh-props", losses);
        Assert.DoesNotContain("inspectionForm:plumbing", losses);
    }

    [Fact(DisplayName = "types: a trait-less create is rejected")]
    public async Task Creating_A_Trait_Less_Type_Is_Rejected()
    {
        var create = await _client.PostAsJsonAsync($"{AssetBase}/types", new
        {
            id = "no-traits",
            displayName = "No traits",
            traits = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
    }

    private static TeamContext TeamContextFor(TeamId teamId, string name)
        => new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    /// <summary>A mutable active-team accessor so a test can switch the active team mid-flight.</summary>
    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
