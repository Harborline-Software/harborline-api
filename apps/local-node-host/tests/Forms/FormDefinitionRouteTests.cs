using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// ADR 0055 form-builder REAL persistence (production slice 1, 2026-06-27) —
/// route-level end-to-end tests for the form-DEFINITION authoring surface. Hosts
/// the SAME production route handlers <see cref="HostedFormsApiEndpoint"/>
/// registers (<see cref="FormDefinitionRoutes.Map"/> — the single source of truth,
/// no test/prod wire drift) over a real in-process Kestrel listener using the
/// SAME composition (<see cref="NodeFormsComposition.AddNodeForms"/>), and drives
/// them with a real <see cref="HttpClient"/>. Mirrors <see cref="FormsRouteTests"/>.
/// </summary>
/// <remarks>
/// Proves the BUILD → SAVE → RELOAD round-trip the Harborline App form builder rides on:
/// a PUT saves an authored definition (the server synthesises + registers the JSON
/// Schema, then registers + publishes the <see cref="FormDefinition"/>); a GET
/// loads it back with its overlay + layout intact (the server-projected layout the
/// Harborline App reads straight off); the list surfaces it; the synthesised schema
/// enforces required + enum on a runtime submit; cross-tenant isolation holds
/// (switching the active team cannot see another tenant's definition); a re-save
/// mints the next version. Uses the in-memory entity store the route contract is
/// agnostic to (the durable node-EF forms store is the follow-up).
/// </remarks>
public sealed class FormDefinitionRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-00000000fc01"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbb0000-0000-0000-0000-00000000fc02"));

    private const string DefBase = "/api/local-node/forms/definitions";
    private const string RuntimeBase = "/api/local-node/forms";
    private const string FormId = "tenant-intake.v1";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;
    private MutableAuthorizationContext _authorization = null!;
    private AuthorizedFormDefinitionLifecycle _definitions = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddTestKernelClock();

        // Ticket 151: definition authoring (PUT save / restore) is now gated on forms:author.
        // Allow-all by default so the pre-gate round-trip tests hold; the gate tests narrow it.
        _authorization = new MutableAuthorizationContext();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Authorization.IAuthorizationContext>(_authorization);
        builder.Services.AddTestAuthorizationGate();
        // Ticket 205 slice 4: the route guards resolve at the gate. It follows the SAME mutable holding
        // set this host already flips, so a test that narrows the caller's permissions narrows the decision.
        builder.Services.AddSingleton(Harborline.Api.LocalNodeHost.Tests.Authorization.TestRouteGate.Following(
            permission => _authorization.HasPermission(permission)));

        // INV-S3 field encryptor the recovery coordinator normally supplies.
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestNodeForms();

        _app = builder.Build();
        _activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));

        // Map BOTH surfaces (mirrors HostedFormsApiEndpoint): the authoring definition
        // routes under test + the runtime render/submit routes (so a saved definition
        // can be proven to enforce its synthesised schema on a real submit).
        _definitions = _app.Services.GetRequiredService<AuthorizedFormDefinitionLifecycle>();
        FormDefinitionRoutes.Map(
            _app,
            _definitions,
            _app.Services.GetRequiredService<ISchemaRegistry>(),
            _activeTeam,
            TimeProvider.System);

        FormsRoutes.Map(
            _app,
            _app.Services.GetRequiredService<Harborline.Api.Foundation.Forms.Engine.IFormEngine>(),
            _app.Services.GetRequiredService<Harborline.Api.Foundation.Forms.Engine.Capabilities.IFormCapabilityIssuer>(),
            _app.Services.GetRequiredService<Harborline.Api.Foundation.Forms.Engine.Capabilities.IFormCapabilityVerifier>(),
            _activeTeam,
            new[] { FormsRoutes.NodeOperatorRole },
            TimeProvider.System);

        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>The authoring save body — a tenant-intake form with a grid section,
    /// a required text field, and a select with options (mirrors the Harborline App
    /// `toSaveRequest` output).</summary>
    private static object SaveBody() => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["name"] = new { label = Text("Tenant name"), controlHint = "text", piiSensitivity = "None" },
                ["unit"] = new { label = Text("Unit type"), controlHint = "select", piiSensitivity = "None" },
            },
            sections = new[]
            {
                new
                {
                    id = "applicant",
                    title = Text("Applicant"),
                    fields = new[] { "name", "unit" },
                    layout = new { kind = "grid", direction = "row", wrap = "wrap", columns = 2, gap = 4 },
                    fieldPlacement = new Dictionary<string, object> { ["unit"] = new { colSpan = 2, grow = 0 } },
                },
            },
            rules = Array.Empty<object>(),
            title = Text("Tenant intake"),
            description = Text("Collect applicant details."),
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["name"] = new { type = "text", required = true, options = (string[]?)null },
            ["unit"] = new { type = "select", required = false, options = new[] { "studio", "one-bed" } },
        },
    };

    private static object Text(string en) => new { defaultLocale = "en", values = new Dictionary<string, string> { ["en"] = en } };

    [Fact(DisplayName = "save→load: a PUT-saved definition reloads with overlay + grid layout intact")]
    public async Task Save_Then_Load_RoundTrips()
    {
        var save = await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saved = await save.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(FormId, saved.GetProperty("formId").GetString());
        Assert.Equal("1.0.0", saved.GetProperty("version").GetString());

        // Reload from the store (NOT memory) — the round-trip the Harborline App route proves.
        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}");
        Assert.Equal(FormId, loaded.GetProperty("formId").GetString());

        var overlay = loaded.GetProperty("overlay");
        var section = overlay.GetProperty("sections")[0];
        Assert.Equal("applicant", section.GetProperty("id").GetString());

        // The server-projected layout the Harborline App reads STRAIGHT OFF (no client re-projection).
        var layout = section.GetProperty("layout");
        Assert.Equal("grid", layout.GetProperty("kind").GetString());
        Assert.Equal(2, layout.GetProperty("columns").GetInt32());
        Assert.Equal(2, section.GetProperty("fieldPlacement").GetProperty("unit").GetProperty("colSpan").GetInt32());

        // The control hints survive onto the overlay fields.
        Assert.Equal("select", overlay.GetProperty("fields").GetProperty("unit").GetProperty("controlHint").GetString());
    }

    [Fact(DisplayName = "save→load: section standing gates round-trip through the real PUT route")]
    public async Task SectionStandingGate_RoundTripsThroughPutAndGet()
    {
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new
                    {
                        label = Text("Name"), controlHint = "text", piiSensitivity = "None",
                        readRoles = Array.Empty<string>(), writeRoles = Array.Empty<string>(),
                        readStandings = new[] { "record-handler" },
                        writeStandings = new[] { "record-handler" },
                        aspects = new
                        {
                            access = new
                            {
                                readRoles = Array.Empty<string>(), writeRoles = Array.Empty<string>(),
                                readStandings = new[] { "record-handler" },
                                writeStandings = new[] { "record-handler" },
                            },
                        },
                    },
                },
                sections = new[]
                {
                    new
                    {
                        id = "main",
                        title = Text("Main"),
                        fields = new[] { "name" },
                        access = new
                        {
                            readRoles = Array.Empty<string>(),
                            writeRoles = Array.Empty<string>(),
                            readStandings = new[] { "record-handler" },
                            writeStandings = new[] { "record-handler" },
                        },
                    },
                },
                rules = Array.Empty<object>(),
                aspects = new
                {
                    access = new
                    {
                        readRoles = Array.Empty<string>(), writeRoles = Array.Empty<string>(),
                        readStandings = new[] { "record-handler" },
                        writeStandings = new[] { "record-handler" },
                    },
                },
            },
            fieldsMeta = new Dictionary<string, object>
            {
                ["name"] = new { type = "text", required = false, options = (string[]?)null },
            },
        };

        using var saved = await _client.PutAsJsonAsync($"{DefBase}/standing-gated-form", body);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/standing-gated-form");
        var overlay = loaded.GetProperty("overlay");
        var access = overlay.GetProperty("sections")[0].GetProperty("access");
        Assert.Equal("record-handler", access.GetProperty("readStandings")[0].GetString());
        Assert.Equal("record-handler", access.GetProperty("writeStandings")[0].GetString());
        var field = overlay.GetProperty("fields").GetProperty("name");
        Assert.Equal("record-handler", field.GetProperty("readStandings")[0].GetString());
        Assert.Equal("record-handler", field.GetProperty("writeStandings")[0].GetString());
        Assert.Equal("record-handler", field.GetProperty("aspects").GetProperty("access")
            .GetProperty("readStandings")[0].GetString());
        Assert.Equal("record-handler", overlay.GetProperty("aspects").GetProperty("access")
            .GetProperty("writeStandings")[0].GetString());
    }

    private const string ConfigFormId = "config-form.v1";

    /// <summary>A save body carrying per-field config (F-17): a currency field with a
    /// NON-DEFAULT currency code (EUR) — plus an UNKNOWN key to prove the wire drops it
    /// structurally (fail-closed) — and a file field with accept + multiple.</summary>
    private static object ConfigSaveBody() => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["price"] = new
                {
                    label = Text("Price"),
                    controlHint = "currency",
                    piiSensitivity = "None",
                    // `evil` is not a FieldConfig member — STJ drops it on deserialize.
                    config = new { currencyCode = "EUR", evil = "should-be-stripped" },
                },
                ["doc"] = new
                {
                    label = Text("Attachment"),
                    controlHint = "file",
                    piiSensitivity = "None",
                    config = new { accept = ".pdf,image/*", multiple = false },
                },
            },
            sections = new[]
            {
                new
                {
                    id = "sec",
                    title = Text("Section"),
                    fields = new[] { "price", "doc" },
                    layout = new { kind = "stack" },
                    fieldPlacement = new Dictionary<string, object>(),
                },
            },
            rules = Array.Empty<object>(),
            title = Text("Config form"),
            description = Text("Per-field config."),
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["price"] = new { type = "currency", required = false, options = (string[]?)null },
            ["doc"] = new { type = "file", required = false, options = (string[]?)null },
        },
    };

    [Fact(DisplayName = "config save→load: currency code + file accept/multiple round-trip; unknown key dropped")]
    public async Task Config_RoundTrips_And_FailsClosed()
    {
        var save = await _client.PutAsJsonAsync($"{DefBase}/{ConfigFormId}", ConfigSaveBody());
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // Reload from the store — the round-trip that was silently dropping config before F-17.
        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{ConfigFormId}");
        var fields = loaded.GetProperty("overlay").GetProperty("fields");

        var priceConfig = fields.GetProperty("price").GetProperty("config");
        Assert.Equal("EUR", priceConfig.GetProperty("currencyCode").GetString());
        // Fail-closed: the unknown `evil` key never landed in the store.
        Assert.False(priceConfig.TryGetProperty("evil", out _));

        var docConfig = fields.GetProperty("doc").GetProperty("config");
        Assert.Equal(".pdf,image/*", docConfig.GetProperty("accept").GetString());
        Assert.False(docConfig.GetProperty("multiple").GetBoolean());
    }

    [Fact(DisplayName = "config back-compat: a field with no config reloads with no config property")]
    public async Task Config_BackCompat_NoConfig_Field()
    {
        // The default SaveBody() fields carry no config — they must reload without one.
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());
        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}");
        var nameField = loaded.GetProperty("overlay").GetProperty("fields").GetProperty("name");
        Assert.False(nameField.TryGetProperty("config", out _));
    }

    [Fact(DisplayName = "list: a saved definition appears in the tenant's definition list")]
    public async Task List_Surfaces_Saved()
    {
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());

        var list = await _client.GetFromJsonAsync<JsonElement>(DefBase);
        var ids = list.EnumerateArray().Select(e => e.GetProperty("formId").GetString()).ToList();
        Assert.Contains(FormId, ids);
    }

    [Fact(DisplayName = "schema synthesis: the saved definition enforces required + enum on a runtime submit")]
    public async Task Synthesised_Schema_Enforces_Required_And_Enum()
    {
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());

        // 'name' is required; 'unit' must be one of the enum values. Omit name, bad unit → 422.
        var bad = await _client.PostAsJsonAsync($"{RuntimeBase}/{FormId}/submit", new { unit = "penthouse" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);

        // A valid candidate submits.
        var ok = await _client.PostAsJsonAsync($"{RuntimeBase}/{FormId}/submit", new { name = "Jordan", unit = "studio" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact(DisplayName = "re-save: saving an existing id mints the next version")]
    public async Task ReSave_Mints_NextVersion()
    {
        var first = await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1.0.0", firstBody.GetProperty("version").GetString());

        var second = await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1.0.1", secondBody.GetProperty("version").GetString());
    }

    [Fact(DisplayName = "tenant: a definition saved under team A is invisible to team B (server-side tenant)")]
    public async Task CrossTenant_Isolated()
    {
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());

        _activeTeam.Active = TeamContextFor(TeamB, "Team B");
        var resp = await _client.GetAsync($"{DefBase}/{FormId}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
        var back = await _client.GetAsync($"{DefBase}/{FormId}");
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
    }

    [Fact(DisplayName = "load: an unsaved form id is a 404 (the builder seeds a fresh form)")]
    public async Task Load_Unsaved_404()
    {
        var resp = await _client.GetAsync($"{DefBase}/never.saved.v1");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact(DisplayName = "094: an unsaved form id answers { code, detail } — never an English `error`")]
    public async Task Load_Unsaved_Is_The_Code_Envelope()
    {
        var resp = await _client.GetAsync($"{DefBase}/never.saved.v1");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("form_definition.not_found", body.GetProperty("code").GetString());
        Assert.Equal("never.saved.v1", body.GetProperty("detail").GetProperty("formId").GetString());
        Assert.False(body.TryGetProperty("error", out _));
    }

    [Fact(DisplayName = "094: a malformed version keeps the offending value as structured detail")]
    public async Task Malformed_Version_Carries_Structured_Detail()
    {
        var resp = await _client.GetAsync($"{DefBase}/{FormId}/versions/not-a-version");

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("form_definition.version_malformed", body.GetProperty("code").GetString());
        Assert.Equal("not-a-version", body.GetProperty("detail").GetProperty("version").GetString());
        Assert.False(body.TryGetProperty("error", out _));
    }

    /// <summary>An authoring save body that carries a NESTED item tree (ADR 0055 Rev 7):
    /// a top-level field, a cardinality-1 <c>group</c> with two nested fields, and a
    /// cardinality-N <c>collection</c> with a bounded row template — the shape the Harborline App
    /// `sectionFormItems` PUTs. Proves the tree survives the wired node persist round-trip.</summary>
    private static object NestedSaveBody() => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
                ["street"] = new { label = Text("Street"), controlHint = "text", piiSensitivity = "None" },
                ["city"] = new { label = Text("City"), controlHint = "text", piiSensitivity = "None" },
                ["lineItem"] = new { label = Text("Line item"), controlHint = "text", piiSensitivity = "None" },
            },
            sections = new[]
            {
                new
                {
                    id = "applicant",
                    title = Text("Applicant"),
                    fields = new[] { "name" },
                    // The nested item tree: a flat field, a group, and a bounded collection.
                    items = new object[]
                    {
                        new { kind = "field", key = "name" },
                        new
                        {
                            kind = "group",
                            key = "address",
                            title = Text("Address"),
                            items = new object[]
                            {
                                new { kind = "field", key = "street" },
                                new { kind = "field", key = "city" },
                            },
                        },
                        new
                        {
                            kind = "collection",
                            key = "lines",
                            title = Text("Lines"),
                            items = new object[] { new { kind = "field", key = "lineItem" } },
                            cardinality = new { min = 1, max = 10 },
                        },
                    },
                },
            },
            rules = Array.Empty<object>(),
            title = Text("Nested intake"),
            description = Text("Collect nested details."),
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["name"] = new { type = "text", required = true, options = (string[]?)null },
        },
    };

    [Fact(DisplayName = "nested: a PUT-saved nested item tree round-trips intact through the node (F1)")]
    public async Task Nested_Definition_RoundTrips_Through_Node()
    {
        const string nestedId = "nested-intake.v1";
        var save = await _client.PutAsJsonAsync($"{DefBase}/{nestedId}", NestedSaveBody());
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // Reload from the STORE (not memory) — the persist round-trip the Harborline App rides on.
        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{nestedId}");
        var section = loaded.GetProperty("overlay").GetProperty("sections")[0];

        // The nested tree survived the node persist round-trip (the F1 fix — before it,
        // OverlaySectionDto had no `items` member so System.Text.Json dropped it silently).
        var items = section.GetProperty("items");
        Assert.Equal(3, items.GetArrayLength());

        // [0] a flat top-level field.
        Assert.Equal("field", items[0].GetProperty("kind").GetString());
        Assert.Equal("name", items[0].GetProperty("key").GetString());

        // [1] a group with its two nested fields + title.
        var group = items[1];
        Assert.Equal("group", group.GetProperty("kind").GetString());
        Assert.Equal("address", group.GetProperty("key").GetString());
        Assert.Equal("Address", group.GetProperty("title").GetProperty("values").GetProperty("en").GetString());
        var groupItems = group.GetProperty("items");
        Assert.Equal(2, groupItems.GetArrayLength());
        Assert.Equal("street", groupItems[0].GetProperty("key").GetString());
        Assert.Equal("city", groupItems[1].GetProperty("key").GetString());

        // [2] a collection with its cardinality bounds + row template.
        var collection = items[2];
        Assert.Equal("collection", collection.GetProperty("kind").GetString());
        Assert.Equal("lines", collection.GetProperty("key").GetString());
        Assert.Equal(1, collection.GetProperty("cardinality").GetProperty("min").GetInt32());
        Assert.Equal(10, collection.GetProperty("cardinality").GetProperty("max").GetInt32());
        Assert.Equal("lineItem", collection.GetProperty("items")[0].GetProperty("key").GetString());
    }

    [Fact(DisplayName = "nested: an over-DEPTH tree is REJECTED at the wired node with the stable code (F1 bounds on-path)")]
    public async Task OverDepth_Nested_Definition_Rejected_At_Node()
    {
        // 8 nested groups ⇒ the innermost field is validated at depth 9 > MaxDepth (8).
        // Proves the fail-closed FormTreeLimits bounds are exercised on the WIRED PUT path
        // (not just the foundation-store unit tests) — the PROC-A8 on-path proof.
        static object NestGroups(int depth)
        {
            object node = new { kind = "field", key = "leaf" };
            for (var i = depth; i >= 1; i--)
            {
                node = new { kind = "group", key = $"g{i}", items = new[] { node } };
            }
            return node;
        }

        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["leaf"] = new { label = Text("Leaf"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new[]
                {
                    new { id = "s", title = Text("S"), fields = new[] { "leaf" }, items = new[] { NestGroups(8) } },
                },
                rules = Array.Empty<object>(),
                title = Text("Too deep"),
                description = Text("Too deep"),
            },
            fieldsMeta = new Dictionary<string, object>(),
        };

        var resp = await _client.PutAsJsonAsync($"{DefBase}/too-deep.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        // The client localizes off the STABLE code, not the English prose.
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(FormDefinitionCodes.TreeDepthExceeded, problem.GetProperty("code").GetString());

        // And nothing was published — the rejected definition is not loadable.
        var loaded = await _client.GetAsync($"{DefBase}/too-deep.v1");
        Assert.Equal(HttpStatusCode.NotFound, loaded.StatusCode);
    }

    /// <summary>An authoring save body carrying the F-14 wizard-page grain: two pages
    /// (the second with a SPINE-1 <c>visibleWhen</c> guard) + wizard settings (review /
    /// confirmation / on-success config) — the shape the Harborline App <c>pagesToOverlay</c> PUTs.</summary>
    private static object PagedSaveBody() => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
                ["employment"] = new { label = Text("Employment"), controlHint = "text", piiSensitivity = "None" },
                ["employer"] = new { label = Text("Employer"), controlHint = "text", piiSensitivity = "None" },
            },
            sections = new object[]
            {
                new { id = "sec-a", title = Text("About you"), fields = new[] { "name", "employment" } },
                new { id = "sec-b", title = Text("Work"), fields = new[] { "employer" } },
            },
            rules = Array.Empty<object>(),
            title = Text("Wizard intake"),
            description = Text("Paged intake."),
            pages = new object[]
            {
                new { id = "page-1", title = Text("About you"), sections = new[] { "sec-a" } },
                new
                {
                    id = "page-2",
                    title = Text("Work"),
                    sections = new[] { "sec-b" },
                    visibleWhen = "{\"==\":[{\"var\":\"employment\"},\"employed\"]}",
                },
            },
            wizard = new
            {
                review = true,
                confirmation = true,
                confirmationMessage = Text("Thanks!"),
                onSuccess = new { redirectUrl = "https://example.test/done", hostCallback = "formDone" },
            },
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["name"] = new { type = "text", required = true, options = (string[]?)null },
        },
    };

    [Fact(DisplayName = "pages: a PUT-saved wizard-page grain round-trips intact through the node (F-14)")]
    public async Task Paged_Definition_RoundTrips_Through_Node()
    {
        const string pagedId = "wizard-intake.v1";
        var save = await _client.PutAsJsonAsync($"{DefBase}/{pagedId}", PagedSaveBody());
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // Reload from the STORE (not memory) — the persist round-trip the Harborline App rides on.
        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{pagedId}");
        var overlay = loaded.GetProperty("overlay");

        var pages = overlay.GetProperty("pages");
        Assert.Equal(2, pages.GetArrayLength());
        Assert.Equal("page-1", pages[0].GetProperty("id").GetString());
        Assert.Equal("sec-a", pages[0].GetProperty("sections")[0].GetString());
        // A guardless page carries NO visibleWhen key (omitted, not null).
        Assert.False(pages[0].TryGetProperty("visibleWhen", out _));
        Assert.Equal(
            "{\"==\":[{\"var\":\"employment\"},\"employed\"]}",
            pages[1].GetProperty("visibleWhen").GetString());

        var wizard = overlay.GetProperty("wizard");
        Assert.True(wizard.GetProperty("review").GetBoolean());
        Assert.True(wizard.GetProperty("confirmation").GetBoolean());
        Assert.Equal("Thanks!", wizard.GetProperty("confirmationMessage").GetProperty("values").GetProperty("en").GetString());
        Assert.Equal("https://example.test/done", wizard.GetProperty("onSuccess").GetProperty("redirectUrl").GetString());
        Assert.Equal("formDone", wizard.GetProperty("onSuccess").GetProperty("hostCallback").GetString());
    }

    [Fact(DisplayName = "pages: a PAGELESS save carries no pages/wizard keys on reload (F-14 back-compat)")]
    public async Task Pageless_Definition_Stays_Pageless_On_The_Wire()
    {
        const string flatId = "flat-intake.v1";
        var save = await _client.PutAsJsonAsync($"{DefBase}/{flatId}", SaveBody());
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{flatId}");
        var overlay = loaded.GetProperty("overlay");
        // Byte-identical back-compat: the keys are OMITTED, not null.
        Assert.False(overlay.TryGetProperty("pages", out _));
        Assert.False(overlay.TryGetProperty("wizard", out _));
    }

    [Fact(DisplayName = "pages: a bad page grain (unassigned section) is REJECTED at the node with the stable code")]
    public async Task Bad_Page_Grain_Rejected_At_Node_With_Stable_Code()
    {
        // page-1 covers only sec-a; sec-b is unassigned ⇒ fail-closed 422 + stable code.
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
                    ["employer"] = new { label = Text("Employer"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new object[]
                {
                    new { id = "sec-a", title = Text("A"), fields = new[] { "name" } },
                    new { id = "sec-b", title = Text("B"), fields = new[] { "employer" } },
                },
                rules = Array.Empty<object>(),
                title = Text("Bad pages"),
                description = Text("Bad pages"),
                pages = new object[]
                {
                    new { id = "page-1", title = Text("Only page"), sections = new[] { "sec-a" } },
                },
            },
            fieldsMeta = new Dictionary<string, object>(),
        };

        var resp = await _client.PutAsJsonAsync($"{DefBase}/bad-pages.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(FormDefinitionCodes.PagesUnassignedSection, problem.GetProperty("code").GetString());

        // And nothing was published — the rejected definition is not loadable.
        var loaded = await _client.GetAsync($"{DefBase}/bad-pages.v1");
        Assert.Equal(HttpStatusCode.NotFound, loaded.StatusCode);
    }

    // ── F-20 — validation depth (constraints + checks + submit gate) ────────────

    /// <summary>A save body whose fieldsMeta carries the FULL authored validation set
    /// (minLength / maxLength / pattern / minimum) — the F-20 schema-synthesis input.</summary>
    private static object ValidatedSaveBody() => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["legalName"] = new { label = Text("Legal name"), controlHint = "text", piiSensitivity = "None" },
                ["tin"] = new { label = Text("Tax ID"), controlHint = "text", piiSensitivity = "None" },
                ["spend"] = new { label = Text("Monthly spend"), controlHint = "number", piiSensitivity = "None" },
            },
            sections = new object[]
            {
                new { id = "sec-a", title = Text("Vendor"), fields = new[] { "legalName", "tin", "spend" } },
            },
            rules = Array.Empty<object>(),
            title = Text("Vendor onboarding"),
            description = Text("F-20 constraints."),
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["legalName"] = new
            {
                type = "text",
                required = true,
                validations = new object[]
                {
                    new { code = "required" },
                    new { code = "minLength", param = "3" },
                    new { code = "maxLength", param = "120" },
                },
            },
            ["tin"] = new
            {
                type = "text",
                required = false,
                validations = new object[] { new { code = "pattern", param = "^[0-9]{2}-[0-9]{7}$" } },
            },
            ["spend"] = new
            {
                type = "number",
                required = false,
                validations = new object[]
                {
                    new { code = "minimum", param = "0" },
                    new { code = "maximum", param = "1000000" },
                },
            },
        },
    };

    [Fact(DisplayName = "F-20: authored constraints synthesize into the schema and ENFORCE on a runtime submit")]
    public async Task Validations_Synthesize_And_Enforce_On_Submit()
    {
        const string id = "validated-vendor.v1";
        var save = await _client.PutAsJsonAsync($"{DefBase}/{id}", ValidatedSaveBody());
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // A violating candidate fails 422 with the SAME stable keyword codes + params
        // the client mirror emits (the parity contract).
        var bad = await _client.PostAsJsonAsync($"{RuntimeBase}/{id}/submit", new
        {
            legalName = "ab",          // minLength 3
            tin = "not-a-tin",         // pattern
            spend = -5,                // minimum 0
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var result = await bad.Content.ReadFromJsonAsync<JsonElement>();
        var codes = result.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("code").GetString())
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "minLength", "minimum", "pattern" }, codes);

        // A conforming candidate persists.
        var good = await _client.PostAsJsonAsync($"{RuntimeBase}/{id}/submit", new
        {
            legalName = "Blue Harbor Works",
            tin = "12-3456789",
            spend = 250,
        });
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
    }

    // ── F1 (deep review of #1683) — money-column Σ balance is EXACT-decimal end-to-end ──────────

    /// <summary>The A2 journal-entry anchor as a save body: a `lines` collection (min 2) with
    /// currency debit/credit cells + the canonical Σdebits = Σcredits Table-scope Validate rule.
    /// Currency is typed as a decimal-string (money) property by the synthesizer, so (a) the
    /// renderer's string values survive schema validation and (b) the engine folds Σ exactly.</summary>
    private static object JournalSaveBody(bool debitRequired = false) => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["debit"] = new { label = Text("Debit"), controlHint = "currency", piiSensitivity = "None" },
                ["credit"] = new { label = Text("Credit"), controlHint = "currency", piiSensitivity = "None" },
            },
            sections = new object[]
            {
                new
                {
                    id = "entry",
                    title = Text("Entry"),
                    fields = Array.Empty<string>(),
                    items = new object[]
                    {
                        new
                        {
                            kind = "collection",
                            key = "lines",
                            title = Text("Lines"),
                            items = new object[]
                            {
                                new { kind = "field", key = "debit" },
                                new { kind = "field", key = "credit" },
                            },
                            cardinality = new { min = 2 },
                        },
                    },
                },
            },
            rules = new object[]
            {
                new
                {
                    id = "debits-equal-credits",
                    tier = "JsonLogic",
                    scope = "Table",
                    scopeTarget = "lines/sum/debit",
                    expression = "{\"==\":[{\"var\":\"table.sum(lines.debit)\"},{\"var\":\"table.sum(lines.credit)\"}]}",
                    action = "Validate",
                },
            },
            title = Text("Journal entry"),
            description = Text("Month-end close."),
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["debit"] = new { type = "currency", required = debitRequired, options = (string[]?)null },
            ["credit"] = new { type = "currency", required = false, options = (string[]?)null },
        },
    };

    [Fact(DisplayName = "F1: a balanced fractional-cents money entry submits; off by a cent blocks (exact Σ)")]
    public async Task Money_Balance_Is_Exact_On_Submit()
    {
        const string id = "journal-money.v1";
        var save = await _client.PutAsJsonAsync($"{DefBase}/{id}", JournalSaveBody());
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // Balanced with fractional cents — decimal STRING money values survive schema validation
        // (they'd be REJECTED against the old `type:number`) and the Σ fold is exact, so it posts.
        var balanced = await _client.PostAsJsonAsync($"{RuntimeBase}/{id}/submit", new
        {
            lines = new object[]
            {
                new { debit = "100.10", credit = "0" },
                new { debit = "200.20", credit = "0" },
                new { debit = "0", credit = "300.30" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, balanced.StatusCode);

        // Off by a single cent — the exact-decimal Σ blocks with the stable balance code.
        var off = await _client.PostAsJsonAsync($"{RuntimeBase}/{id}/submit", new
        {
            lines = new object[]
            {
                new { debit = "100.10", credit = "0" },
                new { debit = "0", credit = "100.11" },
            },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, off.StatusCode);
        var result = await off.Content.ReadFromJsonAsync<JsonElement>();
        var codes = result.GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("code").GetString())
            .ToArray();
        Assert.Contains("debits-equal-credits", codes);
    }

    [Fact(DisplayName = "POINTER-PARITY: a missing required grid cell anchors at the row-object grain (server)")]
    public async Task Grid_Required_Cell_Pointer_Is_Row_Object_Grain()
    {
        // Deep review of #1683 pointer-parity: the SERVER's JSON-Schema `required` reports a missing
        // required grid cell at the ROW-OBJECT instance location, whereas the CLIENT's constraint
        // descent anchors at the CELL (`/lines/0/debit`, gridValidation.test.ts). Both fail closed
        // with the `required` code; the grain differs by design. Pin the server grain here.
        const string id = "journal-required.v1";
        var save = await _client.PutAsJsonAsync($"{DefBase}/{id}", JournalSaveBody(debitRequired: true));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // A row missing the required `debit` cell.
        var resp = await _client.PostAsJsonAsync($"{RuntimeBase}/{id}/submit", new
        {
            lines = new object[]
            {
                new { credit = "100.00" },
                new { debit = "100.00", credit = "0" },
            },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var result = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var required = result.GetProperty("errors").EnumerateArray()
            .FirstOrDefault(e => e.GetProperty("code").GetString() == "required");
        Assert.Equal(JsonValueKind.Object, required.ValueKind); // a `required` error was present.
        var pointer = required.GetProperty("jsonPointer").GetString();
        // Row-object grain: descends into the offending row, anchors at the row (not the cell).
        Assert.Equal("/lines/0", pointer);
    }

    [Theory(DisplayName = "F-20: malformed constraint config is rejected fail-closed with a stable code")]
    [InlineData("minLength", "not-a-number", "text", "form.constraint.bad_param")]
    [InlineData("pattern", "([unclosed", "text", "form.constraint.bad_pattern")]
    [InlineData("minimum", "12", "text", "form.constraint.type_mismatch")]
    [InlineData("minLength", "3", "number", "form.constraint.type_mismatch")]
    // F1 (deep review of #1683): money (currency) is a decimal STRING on the wire, not a number and
    // not a free string — numeric bounds (minimum/maximum) are not JSON-Schema keywords on a string
    // (they would silently no-op), and its shape pattern is engine-owned. Both are rejected closed.
    [InlineData("minimum", "0", "currency", "form.constraint.type_mismatch")]
    [InlineData("maximum", "100", "currency", "form.constraint.type_mismatch")]
    [InlineData("pattern", "^x$", "currency", "form.constraint.type_mismatch")]
    [InlineData("minLength", "1", "currency", "form.constraint.type_mismatch")]
    public async Task Malformed_Constraint_Config_Rejected(string code, string param, string type, string expectedCode)
    {
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["f1"] = new { label = Text("Field"), controlHint = type, piiSensitivity = "None" },
                },
                sections = new object[]
                {
                    new { id = "sec-a", title = Text("A"), fields = new[] { "f1" } },
                },
                rules = Array.Empty<object>(),
                title = Text("Bad constraints"),
                description = Text("Bad constraints"),
            },
            fieldsMeta = new Dictionary<string, object>
            {
                ["f1"] = new { type, required = false, validations = new object[] { new { code, param } } },
            },
        };

        var resp = await _client.PutAsJsonAsync($"{DefBase}/bad-constraints.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedCode, problem.GetProperty("code").GetString());
    }

    [Fact(DisplayName = "F-20: conflicting bounds (min > max) are rejected with the bounds-conflict code")]
    public async Task Conflicting_Bounds_Rejected()
    {
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["f1"] = new { label = Text("Field"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new object[] { new { id = "sec-a", title = Text("A"), fields = new[] { "f1" } } },
                rules = Array.Empty<object>(),
                title = Text("Conflict"),
                description = Text("Conflict"),
            },
            fieldsMeta = new Dictionary<string, object>
            {
                ["f1"] = new
                {
                    type = "text",
                    required = false,
                    validations = new object[]
                    {
                        new { code = "minLength", param = "10" },
                        new { code = "maxLength", param = "3" },
                    },
                },
            },
        };

        var resp = await _client.PutAsJsonAsync($"{DefBase}/conflicting-bounds.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("form.constraint.bounds_conflict", problem.GetProperty("code").GetString());
    }

    [Fact(DisplayName = "F-20/F4: a page with a MISSING title admits with the page-id fallback — never a 500")]
    public async Task Page_Missing_Title_Falls_Back_Never_500()
    {
        const string id = "untitled-page.v1";
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new object[] { new { id = "sec-a", title = Text("A"), fields = new[] { "name" } } },
                rules = Array.Empty<object>(),
                title = Text("Untitled page"),
                description = Text("F4"),
                // NOTE: no `title` member on the page — the F4 wire gap.
                pages = new object[] { new { id = "page-1", sections = new[] { "sec-a" } } },
            },
            fieldsMeta = new Dictionary<string, object>(),
        };

        var resp = await _client.PutAsJsonAsync($"{DefBase}/{id}", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{id}");
        var page = loaded.GetProperty("overlay").GetProperty("pages")[0];
        // The fallback title is the page id (the ToModel empty-dto fallback).
        Assert.Equal("page-1", page.GetProperty("title").GetProperty("values").GetProperty("en").GetString());
    }

    [Fact(DisplayName = "F-20: page checks + async checks round-trip through the node intact")]
    public async Task Checks_And_AsyncChecks_RoundTrip()
    {
        const string id = "checked-vendor.v1";
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["requester"] = new { label = Text("Requester"), controlHint = "text", piiSensitivity = "None" },
                    ["approver"] = new { label = Text("Approver"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new object[]
                {
                    new { id = "sec-a", title = Text("A"), fields = new[] { "requester", "approver" } },
                },
                rules = new object[]
                {
                    new
                    {
                        id = "sod-check",
                        tier = "JsonLogic",
                        scope = "Schema",
                        scopeTarget = "",
                        expression = "{\"!=\":[{\"var\":\"approver\"},{\"var\":\"requester\"}]}",
                        action = "Validate",
                    },
                },
                title = Text("Checked"),
                description = Text("Checked"),
                pages = new object[]
                {
                    new { id = "page-1", title = Text("Only"), sections = new[] { "sec-a" }, checks = new[] { "sod-check" } },
                },
                asyncChecks = new object[]
                {
                    new
                    {
                        id = "dup-vendor",
                        connector = "vendor-duplicate-lookup",
                        field = "requester",
                        failCode = "duplicate-vendor",
                        inputs = new[] { "approver" },
                        debounceMs = 250,
                    },
                },
            },
            fieldsMeta = new Dictionary<string, object>(),
        };

        var save = await _client.PutAsJsonAsync($"{DefBase}/{id}", body);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{id}");
        var overlay = loaded.GetProperty("overlay");
        Assert.Equal("sod-check", overlay.GetProperty("pages")[0].GetProperty("checks")[0].GetString());
        var check = overlay.GetProperty("asyncChecks")[0];
        Assert.Equal("vendor-duplicate-lookup", check.GetProperty("connector").GetString());
        Assert.Equal("requester", check.GetProperty("field").GetString());
        Assert.Equal("approver", check.GetProperty("inputs")[0].GetString());
        Assert.Equal(250, check.GetProperty("debounceMs").GetInt32());
    }

    [Fact(DisplayName = "F-20 invariant e2e: required on a guard-hidden page never blocks submit; its value prunes")]
    public async Task Hidden_Page_Required_Never_Blocks_Submit_E2E()
    {
        const string id = "gated-required.v1";
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
                    ["employment"] = new { label = Text("Employment"), controlHint = "text", piiSensitivity = "None" },
                    ["employer"] = new { label = Text("Employer"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new object[]
                {
                    new { id = "sec-a", title = Text("A"), fields = new[] { "name", "employment" } },
                    new { id = "sec-b", title = Text("B"), fields = new[] { "employer" } },
                },
                rules = Array.Empty<object>(),
                title = Text("Gated"),
                description = Text("Gated"),
                pages = new object[]
                {
                    new { id = "page-1", title = Text("A"), sections = new[] { "sec-a" } },
                    new
                    {
                        id = "page-2",
                        title = Text("B"),
                        sections = new[] { "sec-b" },
                        visibleWhen = "{\"==\":[{\"var\":\"employment\"},\"employed\"]}",
                    },
                },
            },
            fieldsMeta = new Dictionary<string, object>
            {
                ["name"] = new { type = "text", required = true, options = (string[]?)null },
                // employer is REQUIRED — but lives on the guard-hidden page.
                ["employer"] = new { type = "text", required = true, options = (string[]?)null },
            },
        };

        var save = await _client.PutAsJsonAsync($"{DefBase}/{id}", body);
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        // The page-2 guard is FALSE (employment ≠ employed): employer's required must
        // NOT block, and the stale employer value is PRUNED from the stored body.
        var submit = await _client.PostAsJsonAsync($"{RuntimeBase}/{id}/submit", new
        {
            name = "Jordan",
            employment = "retired",
            employer = "Stale Corp", // hidden at submit ⇒ pruned server-side
        });
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        // With the guard TRUE, the same missing employer DOES block — required is
        // visibility-aware, not dropped.
        var blocked = await _client.PostAsJsonAsync($"{RuntimeBase}/{id}/submit", new
        {
            name = "Jordan",
            employment = "employed",
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        var result = await blocked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            result.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("code").GetString() == "required"
                 && e.GetProperty("jsonPointer").GetString() == "/employer");
    }

    // ── F1 (deep review of #1671) — required strings reject "" at the schema tier ──

    [Fact(DisplayName = "F1: a statically-required string field REJECTS an empty-string submit (synthesized minLength: 1)")]
    public async Task Required_String_Rejects_EmptyString_Submit()
    {
        // SaveBody's 'name' is required with NO authored minLength — the exact F1
        // corner: the registry's `required` keyword is presence-only, so before the
        // synthesized floor a direct submit with "" was ACCEPTED while the client
        // blocked the same input.
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());

        var bad = await _client.PostAsJsonAsync($"{RuntimeBase}/{FormId}/submit", new { name = "", unit = "studio" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        var result = await bad.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(
            result.GetProperty("errors").EnumerateArray(),
            e => e.GetProperty("code").GetString() == "minLength"
                 && e.GetProperty("jsonPointer").GetString() == "/name");

        // A one-character answer satisfies the synthesized floor (it is exactly 1).
        var ok = await _client.PostAsJsonAsync($"{RuntimeBase}/{FormId}/submit", new { name = "J", unit = "studio" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact(DisplayName = "F1: an OPTIONAL string field still accepts an absent value (no floor synthesized)")]
    public async Task Optional_String_Unaffected_By_MinLength_Floor()
    {
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());

        // 'unit' is optional — omitting it must still submit (only REQUIRED string
        // fields get the synthesized minLength floor).
        var ok = await _client.PostAsJsonAsync($"{RuntimeBase}/{FormId}/submit", new { name = "Jordan" });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    // ── F3 (deep review of #1671) — rule-compile admission gate at PUT ───────────

    [Fact(DisplayName = "F3: an UNCOMPILABLE rule expression is rejected at PUT with form.rules.uncompilable")]
    public async Task Uncompilable_Rule_Rejected_At_Put()
    {
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new object[] { new { id = "sec-a", title = Text("A"), fields = new[] { "name" } } },
                rules = new object[]
                {
                    new
                    {
                        id = "broken-rule",
                        tier = "JsonLogic",
                        scope = "Field",
                        scopeTarget = "name",
                        // Not valid JSON — previously ADMITTED (only non-emptiness was
                        // checked for rule expressions) and silently degraded the node's
                        // submit gate to schema-only (the F3 brick-a-submit asymmetry).
                        expression = "{not valid json",
                        action = "Validate",
                    },
                },
                title = Text("Broken rules"),
                description = Text("F3"),
            },
            fieldsMeta = new Dictionary<string, object>(),
        };

        var resp = await _client.PutAsJsonAsync($"{DefBase}/broken-rule.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(FormDefinitionCodes.RulesUncompilable, problem.GetProperty("code").GetString());

        // Nothing was published — the rejected definition is not loadable.
        var loaded = await _client.GetAsync($"{DefBase}/broken-rule.v1");
        Assert.Equal(HttpStatusCode.NotFound, loaded.StatusCode);
    }

    [Fact(DisplayName = "L1145: an unknown restricting rule kind refuses the route write visibly")]
    public async Task Unknown_Restricting_Rule_Kind_Refuses_Route_Write_Before_Store()
    {
        const string definitionId = "unknown-rule-kind.v1";
        const string ruleId = "future-restriction";
        const string unknownKind = "DenyUnlessReviewed";
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new object[] { new { id = "sec-a", title = Text("A"), fields = new[] { "name" } } },
                rules = new object[]
                {
                    new
                    {
                        id = ruleId,
                        tier = "JsonLogic",
                        scope = "Schema",
                        scopeTarget = "",
                        expression = "true",
                        action = unknownKind,
                    },
                },
                title = Text("Unknown restriction"),
            },
            fieldsMeta = new Dictionary<string, object>(),
        };

        var response = await _client.PutAsJsonAsync($"{DefBase}/{definitionId}", body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var refusal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(RestrictingDefinitionKindValidator.KindUnknownCode, refusal.GetProperty("code").GetString());
        // Ticket 094: the refusal is a code plus named detail fields — never an English sentence the
        // test has to substring-scan.
        var detail = refusal.GetProperty("detail");
        Assert.Equal(definitionId, detail.GetProperty("definition").GetString());
        Assert.Equal(ruleId, detail.GetProperty("target").GetString());
        Assert.Equal(unknownKind, detail.GetProperty("unknownKind").GetString());
        Assert.False(refusal.TryGetProperty("error", out _));

        // The route validates the raw action before its historical unknown→Visibility lowering
        // and before the definition lifecycle store can register a revision.
        var loaded = await _client.GetAsync($"{DefBase}/{definitionId}");
        Assert.Equal(HttpStatusCode.NotFound, loaded.StatusCode);
    }

    [Fact(DisplayName = "F3: a page guard that parses as JSON but does NOT compile is rejected at PUT")]
    public async Task Uncompilable_Guard_Rejected_At_Put()
    {
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
                    ["extra"] = new { label = Text("Extra"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new object[]
                {
                    new { id = "sec-a", title = Text("A"), fields = new[] { "name" } },
                    new { id = "sec-b", title = Text("B"), fields = new[] { "extra" } },
                },
                rules = Array.Empty<object>(),
                title = Text("Broken guard"),
                description = Text("F3"),
                pages = new object[]
                {
                    new { id = "page-1", title = Text("A"), sections = new[] { "sec-a" } },
                    new
                    {
                        id = "page-2",
                        title = Text("B"),
                        sections = new[] { "sec-b" },
                        // Valid JSON (passes the F-20 parse gate) but a malformed
                        // table-aggregate var — compiles on neither engine; previously
                        // admitted and, fail-closed at render, hid the page FOREVER.
                        visibleWhen = "{\"var\":\"table.sum(\"}",
                    },
                },
            },
            fieldsMeta = new Dictionary<string, object>(),
        };

        var resp = await _client.PutAsJsonAsync($"{DefBase}/broken-guard.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(FormDefinitionCodes.RulesGuardUncompilable, problem.GetProperty("code").GetString());
    }

    // (Compilable rules + guards still admitting post-F3 is proven by the existing
    // Checks_And_AsyncChecks_RoundTrip and Hidden_Page_Required_Never_Blocks_Submit_E2E
    // tests — both PUT real JsonLogic rules/guards through the same gate.)

    // ── F-23 layout breadth: content/action blocks + responsive zone intents ──────

    /// <summary>A save body exercising the full F-23 surface: a content block, both
    /// action kinds, a photo-strip group ZONE (flex + width intents + collapse), and
    /// responsive section intents — mirrors the unit-inspection anchor's shape.</summary>
    private static object LayoutBreadthSaveBody() => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["condition"] = new { label = Text("Condition"), controlHint = "select", piiSensitivity = "None" },
                ["photoFront"] = new { label = Text("Photo (front)"), controlHint = "file", piiSensitivity = "None" },
                ["photoBack"] = new { label = Text("Photo (back)"), controlHint = "file", piiSensitivity = "None" },
                ["notes"] = new { label = Text("Notes"), controlHint = "textarea", piiSensitivity = "None" },
            },
            sections = new object[]
            {
                new
                {
                    id = "kitchen",
                    title = Text("Kitchen"),
                    fields = new[] { "condition", "notes" },
                    layout = new
                    {
                        kind = "grid", direction = "row", wrap = "wrap", columns = 2, gap = 4,
                        collapseBelow = "md", density = "compact", align = "start",
                    },
                    items = new object[]
                    {
                        new
                        {
                            kind = "content",
                            key = "instructions",
                            content = new object[]
                            {
                                new { kind = "heading", text = Text("Before you start"), level = 3 },
                                new { kind = "paragraph", text = Text("Check every appliance.") },
                            },
                        },
                        new
                        {
                            kind = "action",
                            key = "jump",
                            action = new { kind = "scroll-to-section", label = Text("Skip to bath"), sectionId = "bath" },
                        },
                        new
                        {
                            kind = "action",
                            key = "guide",
                            action = new { kind = "open-url", label = Text("Open guide"), url = "https://example.test/guide" },
                        },
                        new { kind = "field", key = "condition" },
                        new
                        {
                            kind = "group",
                            key = "photoStrip",
                            title = Text("Photos"),
                            layout = new
                            {
                                kind = "flex", direction = "row", wrap = "wrap", columns = 2, gap = 2,
                                collapseBelow = "sm",
                            },
                            placement = new Dictionary<string, object>
                            {
                                ["photoFront"] = new { colSpan = 1, grow = 0, width = "1/2" },
                                ["photoBack"] = new { colSpan = 1, grow = 0, width = "1/2", align = "end" },
                            },
                            items = new object[]
                            {
                                new { kind = "field", key = "photoFront" },
                                new { kind = "field", key = "photoBack" },
                            },
                        },
                        new { kind = "field", key = "notes" },
                    },
                },
                new
                {
                    id = "bath",
                    title = Text("Bath"),
                    fields = Array.Empty<string>(),
                },
            },
            rules = Array.Empty<object>(),
            title = Text("Unit inspection"),
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["condition"] = new { type = "select", required = true, options = new[] { "good", "fair", "poor" } },
            ["photoFront"] = new { type = "file", required = false, options = (string[]?)null },
            ["photoBack"] = new { type = "file", required = false, options = (string[]?)null },
            ["notes"] = new { type = "textarea", required = false, options = (string[]?)null },
        },
    };

    [Fact(DisplayName = "F-23 save→load: content/action blocks + zone intents round-trip intact")]
    public async Task LayoutBreadth_Save_Then_Load_RoundTrips()
    {
        const string id = "unit-inspection.v1";
        var save = await _client.PutAsJsonAsync($"{DefBase}/{id}", LayoutBreadthSaveBody());
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{id}");
        var section = loaded.GetProperty("overlay").GetProperty("sections")[0];

        // Section-grain responsive intents survive the round-trip.
        var layout = section.GetProperty("layout");
        Assert.Equal("md", layout.GetProperty("collapseBelow").GetString());
        Assert.Equal("compact", layout.GetProperty("density").GetString());
        Assert.Equal("start", layout.GetProperty("align").GetString());

        var items = section.GetProperty("items");

        // The content block survives with both nodes + the heading level.
        var content = items[0];
        Assert.Equal("content", content.GetProperty("kind").GetString());
        Assert.Equal(2, content.GetProperty("content").GetArrayLength());
        Assert.Equal("heading", content.GetProperty("content")[0].GetProperty("kind").GetString());
        Assert.Equal(3, content.GetProperty("content")[0].GetProperty("level").GetInt32());
        Assert.Equal("Before you start",
            content.GetProperty("content")[0].GetProperty("text").GetProperty("values").GetProperty("en").GetString());

        // Both action kinds survive with their targets.
        Assert.Equal("scroll-to-section", items[1].GetProperty("action").GetProperty("kind").GetString());
        Assert.Equal("bath", items[1].GetProperty("action").GetProperty("sectionId").GetString());
        Assert.Equal("open-url", items[2].GetProperty("action").GetProperty("kind").GetString());
        Assert.Equal("https://example.test/guide", items[2].GetProperty("action").GetProperty("url").GetString());

        // The photo-strip ZONE survives: flex layout + collapse + per-child width intents.
        var zone = items[4];
        Assert.Equal("group", zone.GetProperty("kind").GetString());
        Assert.Equal("flex", zone.GetProperty("layout").GetProperty("kind").GetString());
        Assert.Equal("sm", zone.GetProperty("layout").GetProperty("collapseBelow").GetString());
        Assert.Equal("1/2", zone.GetProperty("placement").GetProperty("photoFront").GetProperty("width").GetString());
        Assert.Equal("end", zone.GetProperty("placement").GetProperty("photoBack").GetProperty("align").GetString());
    }

    [Fact(DisplayName = "F-23 back-compat: a blockless/zoneless definition emits NONE of the new wire keys")]
    public async Task LayoutBreadth_LegacyShape_IsByteIdentical()
    {
        const string id = "legacy-layout.v1";
        await _client.PutAsJsonAsync($"{DefBase}/{id}", SaveBody());

        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{id}");
        var section = loaded.GetProperty("overlay").GetProperty("sections")[0];

        // The Rev-6 grid layout keeps EXACTLY its pre-F-23 keys — none of the new ones.
        var layout = section.GetProperty("layout");
        Assert.False(layout.TryGetProperty("collapseBelow", out _));
        Assert.False(layout.TryGetProperty("density", out _));
        Assert.False(layout.TryGetProperty("align", out _));
        var placement = section.GetProperty("fieldPlacement").GetProperty("unit");
        Assert.False(placement.TryGetProperty("width", out _));
        Assert.False(placement.TryGetProperty("align", out _));
    }

    [Theory(DisplayName = "F-23 fail-closed: malformed blocks/intents are 422 with a stable code")]
    [InlineData("run-script", "https://example.test", null, "form.blocks.action_unknown_kind")]
    [InlineData("open-url", "javascript:alert(1)", null, "form.blocks.action_bad_url")]
    [InlineData("open-url", null, null, "form.blocks.action_bad_url")]
    [InlineData("scroll-to-section", null, "sec-ghost", "form.blocks.action_unknown_section")]
    public async Task LayoutBreadth_MalformedAction_Is422WithStableCode(
        string kind, string? url, string? sectionId, string expectedCode)
    {
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new object[]
                {
                    new
                    {
                        id = "main",
                        title = Text("Main"),
                        fields = new[] { "name" },
                        items = new object[]
                        {
                            new { kind = "field", key = "name" },
                            new { kind = "action", key = "bad", action = new { kind, label = Text("Go"), url, sectionId } },
                        },
                    },
                },
                rules = Array.Empty<object>(),
                title = Text("Bad action"),
            },
            fieldsMeta = new Dictionary<string, object>
            {
                ["name"] = new { type = "text", required = false, options = (string[]?)null },
            },
        };

        var resp = await _client.PutAsJsonAsync($"{DefBase}/bad-action.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedCode, err.GetProperty("code").GetString());
    }

    [Fact(DisplayName = "F-23 fail-closed: an unknown zone breakpoint token is 422 with a stable code")]
    public async Task LayoutBreadth_UnknownBreakpoint_Is422WithStableCode()
    {
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new object[]
                {
                    new
                    {
                        id = "main",
                        title = Text("Main"),
                        fields = new[] { "name" },
                        layout = new
                        {
                            kind = "grid", direction = "row", wrap = "wrap", columns = 2, gap = 4,
                            collapseBelow = "720px", // pixels are exactly what intents forbid
                        },
                    },
                },
                rules = Array.Empty<object>(),
                title = Text("Bad breakpoint"),
            },
            fieldsMeta = new Dictionary<string, object>
            {
                ["name"] = new { type = "text", required = false, options = (string[]?)null },
            },
        };

        var resp = await _client.PutAsJsonAsync($"{DefBase}/bad-breakpoint.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("form.layout.unknown_breakpoint", err.GetProperty("code").GetString());
    }

    // ── SPINE-2 item 6: the classification tagging editor round-trip + admission ─────

    private const string ClassFormId = "erasure-request.v1";

    /// <summary>A save body tagging a field (pii), a section (cui inherited by its fields), and a
    /// collection container (identifier inherited by its column) — the item-6 grains.</summary>
    private static object ClassificationSaveBody(bool ackSensitiveCheck) => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["fullName"] = new
                {
                    label = Text("Full legal name"),
                    controlHint = "text",
                    piiSensitivity = "None",
                    // field-grain classification ADDS pii on top of the inherited section-grain cui
                    // (monotonic-union: a finer grain must be a SUPERSET of what it inherits).
                    aspects = new { classification = new { tags = new[]
                    {
                        new { system = "shipyard/data-classification", code = "cui" },
                        new { system = "shipyard/data-classification", code = "pii" },
                    } } },
                },
                ["idNumber"] = new { label = Text("Government ID"), controlHint = "text", piiSensitivity = "None" },
                ["acctNo"] = new { label = Text("Account number"), controlHint = "text", piiSensitivity = "None" },
            },
            sections = new object[]
            {
                new
                {
                    id = "identity",
                    title = Text("Identity verification"),
                    fields = new[] { "fullName", "idNumber" },
                    layout = new { kind = "stack" },
                    // section-grain classification: cui flows to fullName + idNumber
                    aspects = new { classification = new { tags = new[] { new { system = "shipyard/data-classification", code = "cui" } } } },
                },
                new
                {
                    id = "banking",
                    title = Text("Banking"),
                    fields = Array.Empty<string>(),
                    layout = new { kind = "stack" },
                    // a collection container carrying an identifier tag inherited by its column field.
                    items = new object[]
                    {
                        new
                        {
                            kind = "collection",
                            key = "accounts",
                            title = Text("Accounts"),
                            aspects = new { classification = new { tags = new[] { new { system = "shipyard/data-classification", code = "identifier" } } } },
                            items = new object[] { new { kind = "field", key = "acctNo" } },
                        },
                    },
                },
            },
            rules = Array.Empty<object>(),
            title = Text("Erasure request"),
            // an async check feeding the pii-classified fullName — gated on the ack.
            asyncChecks = new object[]
            {
                new
                {
                    id = "dup",
                    connector = "subject-lookup",
                    field = "fullName",
                    failCode = "duplicate-subject",
                    allowsSensitiveInputs = ackSensitiveCheck,
                },
            },
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["fullName"] = new { type = "text", required = true, options = (string[]?)null },
            ["idNumber"] = new { type = "text", required = false, options = (string[]?)null },
            ["acctNo"] = new { type = "text", required = false, options = (string[]?)null },
        },
    };

    [Fact(DisplayName = "item 6: classification aspects (field/section/collection grains) round-trip through the node")]
    public async Task Classification_Aspects_RoundTrip()
    {
        var save = await _client.PutAsJsonAsync($"{DefBase}/{ClassFormId}", ClassificationSaveBody(ackSensitiveCheck: true));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);

        var loaded = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{ClassFormId}");
        var overlay = loaded.GetProperty("overlay");

        // Field-grain tags survive (the cui+pii superset).
        var fieldTags = overlay.GetProperty("fields").GetProperty("fullName")
            .GetProperty("aspects").GetProperty("classification").GetProperty("tags");
        var fieldCodes = fieldTags.EnumerateArray().Select(t => t.GetProperty("code").GetString()).ToList();
        Assert.Contains("pii", fieldCodes);
        Assert.Contains("cui", fieldCodes);

        // Section-grain tag survives.
        var identity = overlay.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("id").GetString() == "identity");
        Assert.Equal("cui", identity.GetProperty("aspects").GetProperty("classification").GetProperty("tags")[0].GetProperty("code").GetString());

        // Collection-container-grain tag survives on the item tree.
        var banking = overlay.GetProperty("sections").EnumerateArray().First(s => s.GetProperty("id").GetString() == "banking");
        var collection = banking.GetProperty("items")[0];
        Assert.Equal("identifier", collection.GetProperty("aspects").GetProperty("classification").GetProperty("tags")[0].GetProperty("code").GetString());
    }

    [Fact(DisplayName = "item 6: an unknown-kind tag in the predefined system is REJECTED at PUT (aspect.unknown_kind 422)")]
    public async Task Classification_UnknownKind_Is422()
    {
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["x"] = new
                    {
                        label = Text("X"),
                        controlHint = "text",
                        piiSensitivity = "None",
                        aspects = new { classification = new { tags = new[] { new { system = "shipyard/data-classification", code = "pii-typo" } } } },
                    },
                },
                sections = new object[]
                {
                    new { id = "s", title = Text("S"), fields = new[] { "x" }, layout = new { kind = "stack" } },
                },
                rules = Array.Empty<object>(),
                title = Text("Bad kind"),
            },
            fieldsMeta = new Dictionary<string, object> { ["x"] = new { type = "text", required = false, options = (string[]?)null } },
        };

        var resp = await _client.PutAsJsonAsync($"{DefBase}/bad-kind.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("aspect.unknown_kind", err.GetProperty("code").GetString());
    }

    [Fact(DisplayName = "item 6: an async check feeding a pii-classified field WITHOUT ack is REJECTED at PUT (422)")]
    public async Task Classification_SensitiveAsyncInput_WithoutAck_Is422()
    {
        var resp = await _client.PutAsJsonAsync($"{DefBase}/erasure-noack.v1", ClassificationSaveBody(ackSensitiveCheck: false));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("aspect.sensitive_input_unacknowledged", err.GetProperty("code").GetString());
    }

    // ── F-22 (item 7): version history + restore-as-new-draft ─────────────────────

    /// <summary>A minimal single-field save body carrying a custom TITLE — so two saves produce
    /// content-distinguishable revisions (v1 vs v2) for the version-history tests.</summary>
    private static object SaveBodyTitled(string title) => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["name"] = new { label = Text("Name"), controlHint = "text", piiSensitivity = "None" },
            },
            sections = new[]
            {
                new
                {
                    id = "s",
                    title = Text("Section"),
                    fields = new[] { "name" },
                    layout = new { kind = "stack" },
                    fieldPlacement = new Dictionary<string, object>(),
                },
            },
            rules = Array.Empty<object>(),
            title = Text(title),
            description = Text("d"),
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["name"] = new { type = "text", required = true, options = (string[]?)null },
        },
    };

    [Fact(DisplayName = "versions: the history lists every retained revision, newest-first, with status")]
    public async Task Versions_List_All_Revisions_NewestFirst()
    {
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBodyTitled("v1"));
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBodyTitled("v2"));

        var versions = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}/versions");
        var list = versions.EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("1.0.1", list[0].GetProperty("version").GetString()); // newest first
        Assert.Equal("1.0.0", list[1].GetProperty("version").GetString());
        Assert.Equal("Published", list[0].GetProperty("status").GetString());
        // Ticket 153 review: version rows carry cascadeLayer like every sibling pillar.
        Assert.Equal("Tenant", list[0].GetProperty("cascadeLayer").GetString());
    }

    [Fact(DisplayName = "versions/{v}: a specific prior revision is served read-only with its own content")]
    public async Task Version_Get_Specific_Revision()
    {
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBodyTitled("original"));
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBodyTitled("edited"));

        var v1 = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}/versions/1.0.0");
        Assert.Equal("original", v1.GetProperty("overlay").GetProperty("title").GetProperty("values").GetProperty("en").GetString());
        var v2 = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}/versions/1.0.1");
        Assert.Equal("edited", v2.GetProperty("overlay").GetProperty("title").GetProperty("values").GetProperty("en").GetString());
    }

    [Fact(DisplayName = "versions/{v}: an unknown version is 404; a malformed version is 400")]
    public async Task Version_Get_Unknown_Or_Malformed()
    {
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBodyTitled("v1"));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{DefBase}/{FormId}/versions/9.9.9")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync($"{DefBase}/{FormId}/versions/not-a-version")).StatusCode);
    }

    [Fact(DisplayName = "restore: an old version is restored as a NEW DRAFT derived from it, history untouched")]
    public async Task Restore_Creates_New_Draft_Without_Mutating_History()
    {
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBodyTitled("original")); // 1.0.0
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBodyTitled("edited")); // 1.0.1

        var restore = await _client.PostAsJsonAsync($"{DefBase}/{FormId}/restore", new { version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var restored = await restore.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1.0.2", restored.GetProperty("version").GetString()); // minted next patch

        var versions = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}/versions");
        var byVersion = versions.EnumerateArray().ToDictionary(e => e.GetProperty("version").GetString()!, e => e);
        // History is APPEND-ONLY: the two originals persist unchanged; the restore is a new head.
        Assert.Equal(3, byVersion.Count);
        Assert.Equal("Published", byVersion["1.0.0"].GetProperty("status").GetString());
        Assert.Equal("Published", byVersion["1.0.1"].GetProperty("status").GetString());
        // The restored revision is a DRAFT, derived-from the source version.
        Assert.Equal("Draft", byVersion["1.0.2"].GetProperty("status").GetString());
        Assert.Equal("1.0.0", byVersion["1.0.2"].GetProperty("derivedFrom").GetString());

        // Its CONTENT matches the source (original), not the head (edited).
        var v102 = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}/versions/1.0.2");
        Assert.Equal("original", v102.GetProperty("overlay").GetProperty("title").GetProperty("values").GetProperty("en").GetString());

        // The published GET-by-id head is UNCHANGED by the restore (the draft is not published).
        var head = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}");
        Assert.Equal("edited", head.GetProperty("overlay").GetProperty("title").GetProperty("values").GetProperty("en").GetString());
    }

    [Fact(DisplayName = "restore→save: the PUT after a restore SUCCEEDS and mints PAST the restored draft (no 409 — #1686 Finding 1)")]
    public async Task Restore_Then_Save_Succeeds_And_Mints_Past_Draft()
    {
        // The reproduced BLOCKER: PUT v1 → PUT v2 → restore(1.0.0) → PUT. Before the fix the PUT
        // minted off the PUBLISHED head (1.0.1)+1 = 1.0.2, colliding with the restored DRAFT 1.0.2
        // → 409, forever (no publish-draft route). The fix mints off the MAX of ALL revisions.
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBodyTitled("original")); // 1.0.0
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBodyTitled("edited"));   // 1.0.1

        var restore = await _client.PostAsJsonAsync($"{DefBase}/{FormId}/restore", new { version = "1.0.0" });
        Assert.Equal(HttpStatusCode.OK, restore.StatusCode);
        var restored = await restore.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1.0.2", restored.GetProperty("version").GetString()); // restored DRAFT

        // The next PUT — the builder re-saving the reopened restored draft — must NOT 409. It mints
        // off the max of ALL revisions (incl. the 1.0.2 draft) → 1.0.3, and publishes it.
        var save = await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBodyTitled("resaved"));
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saved = await save.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1.0.3", saved.GetProperty("version").GetString());

        // History is intact: 1.0.0/1.0.1 published, 1.0.2 the restored draft, 1.0.3 the re-save.
        var versions = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}/versions");
        var byVersion = versions.EnumerateArray().ToDictionary(e => e.GetProperty("version").GetString()!, e => e);
        Assert.Equal(4, byVersion.Count);
        Assert.Equal("Draft", byVersion["1.0.2"].GetProperty("status").GetString());
        Assert.Equal("Published", byVersion["1.0.3"].GetProperty("status").GetString());

        // The published head advanced to the re-saved revision (the draft is skipped by GET-by-id).
        var head = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}");
        Assert.Equal("resaved", head.GetProperty("overlay").GetProperty("title").GetProperty("values").GetProperty("en").GetString());
    }

    [Fact(DisplayName = "restore: an unknown source version is 404")]
    public async Task Restore_Unknown_Version_404()
    {
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBodyTitled("v1"));
        var resp = await _client.PostAsJsonAsync($"{DefBase}/{FormId}/restore", new { version = "9.9.9" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Ticket 151: definition authoring is permission-gated (forms:author) ────────

    [Fact(DisplayName = "ticket 151: PUT save without forms:author is refused (403), nothing persists")]
    public async Task Save_Without_FormsAuthor_Is_Refused()
    {
        _authorization.Allow(Harborline.Api.Foundation.IdentityAtlas.TeamRolePermissions.RecordsWrite);

        var save = await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());
        Assert.Equal(HttpStatusCode.Forbidden, save.StatusCode);

        var list = await _client.GetFromJsonAsync<JsonElement>(DefBase);
        Assert.Equal(0, list.GetArrayLength());
    }

    [Fact(DisplayName = "ticket 151: restore without forms:author is refused (403)")]
    public async Task Restore_Without_FormsAuthor_Is_Refused()
    {
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());
        _authorization.Allow(Harborline.Api.Foundation.IdentityAtlas.TeamRolePermissions.RecordsWrite);

        var restore = await _client.PostAsJsonAsync($"{DefBase}/{FormId}/restore", new { version = "1.0.0" });
        Assert.Equal(HttpStatusCode.Forbidden, restore.StatusCode);
    }

    // ── Ticket 153 (L1351/L1352): cascadeLayer travels on the wire ────────────────

    [Fact(DisplayName = "ticket 153: list + detail emit the envelope cascadeLayer on the wire")]
    public async Task CascadeLayer_Emitted_On_List_And_Detail()
    {
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());

        var list = await _client.GetFromJsonAsync<JsonElement>(DefBase);
        var row = list.EnumerateArray().Single(e => e.GetProperty("formId").GetString() == FormId);
        Assert.Equal("Tenant", row.GetProperty("cascadeLayer").GetString());

        var detail = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{FormId}");
        Assert.Equal("Tenant", detail.GetProperty("cascadeLayer").GetString());
    }

    // ── Ticket 156 (L1539): a partial definition saves freely as Draft ────────────

    /// <summary>A half-finished save body: one declared field, NO sections yet.</summary>
    private static object PartialDraftBody() => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["name"] = new { label = Text("Tenant name"), controlHint = "text", piiSensitivity = "None" },
            },
            sections = Array.Empty<object>(),
            rules = Array.Empty<object>(),
            title = Text("Half-finished intake"),
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["name"] = new { type = "text", required = false, options = (string[]?)null },
        },
        draft = true,
    };

    [Fact(DisplayName = "ticket 156: a partial (zero-section) definition saves as Draft without publishing")]
    public async Task Draft_Save_Allows_Partial_And_Does_Not_Publish()
    {
        const string draftId = "draft-intake.v1";
        var save = await _client.PutAsJsonAsync($"{DefBase}/{draftId}", PartialDraftBody());
        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        var saved = await save.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1.0.0", saved.GetProperty("version").GetString());

        // Not published — but DISCOVERABLE: with no published head, GET-by-id serves the
        // latest draft, clearly marked (the form must never look lost after a draft-only save).
        var byId = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{draftId}");
        Assert.Equal("Draft", byId.GetProperty("status").GetString());
        Assert.Equal("1.0.0", byId.GetProperty("version").GetString());

        // The revision persisted as a Draft and its content is loadable read-only.
        var versions = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{draftId}/versions");
        var revision = Assert.Single(versions.EnumerateArray());
        Assert.Equal("Draft", revision.GetProperty("status").GetString());
        var content = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{draftId}/versions/1.0.0");
        Assert.Equal("Half-finished intake", content.GetProperty("overlay").GetProperty("title").GetProperty("values").GetProperty("en").GetString());
    }

    [Fact(DisplayName = "ticket 156: a non-draft save keeps the at-least-one-section floor")]
    public async Task NonDraft_Save_Still_Rejects_Zero_Sections()
    {
        var body = JsonSerializer.SerializeToElement(PartialDraftBody());
        var noDraft = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body.GetRawText())!;
        noDraft.Remove("draft");

        var resp = await _client.PutAsJsonAsync($"{DefBase}/published-partial.v1", noDraft);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "ticket 156: a later full save publishes the next version past the draft with every gate")]
    public async Task Draft_Then_Full_Save_Publishes_Next_Version()
    {
        const string draftId = "draft-then-publish.v1";
        await _client.PutAsJsonAsync($"{DefBase}/{draftId}", PartialDraftBody()); // 1.0.0 Draft

        var publish = await _client.PutAsJsonAsync($"{DefBase}/{draftId}", SaveBody());
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        var published = await publish.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("1.0.1", published.GetProperty("version").GetString()); // minted PAST the draft

        var versions = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{draftId}/versions");
        var byVersion = versions.EnumerateArray().ToDictionary(e => e.GetProperty("version").GetString()!, e => e);
        Assert.Equal("Draft", byVersion["1.0.0"].GetProperty("status").GetString());
        Assert.Equal("Published", byVersion["1.0.1"].GetProperty("status").GetString());
    }

    // ── Ticket 157 (L1543): the placeholder label never publishes ─────────────────

    /// <summary>The SaveBody() shape with the 'name' field still carrying the builder's
    /// placeholder label (or any caller-chosen label, for the family/missing-label tests).</summary>
    private static object PlaceholderLabelBody(bool draft = false, string label = "New field") => new
    {
        overlay = new
        {
            fields = new Dictionary<string, object>
            {
                ["name"] = new { label = Text(label), controlHint = "text", piiSensitivity = "None" },
            },
            sections = new[]
            {
                new { id = "sec", title = Text("Section"), fields = new[] { "name" } },
            },
            rules = Array.Empty<object>(),
            title = Text("Placeholder form"),
        },
        fieldsMeta = new Dictionary<string, object>
        {
            ["name"] = new { type = "text", required = false, options = (string[]?)null },
        },
        draft,
    };

    [Fact(DisplayName = "ticket 157: publishing a field labelled \"New field\" is refused, naming the field")]
    public async Task Placeholder_Label_Rejected_At_Publish_Naming_The_Field()
    {
        var resp = await _client.PutAsJsonAsync($"{DefBase}/placeholder.v1", PlaceholderLabelBody());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("form.label.placeholder", body.GetProperty("code").GetString());
        Assert.Equal("name", body.GetProperty("detail").GetProperty("target").GetString());

        // Nothing persisted: the refused revision left no trace in the history.
        var versions = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/placeholder.v1/versions");
        Assert.Empty(versions.EnumerateArray());
    }

    [Fact(DisplayName = "ticket 157: a DRAFT may hold the placeholder label (deliberate exception)")]
    public async Task Placeholder_Label_Allowed_On_Draft()
    {
        var resp = await _client.PutAsJsonAsync($"{DefBase}/placeholder-draft.v1", PlaceholderLabelBody(draft: true));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var versions = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/placeholder-draft.v1/versions");
        Assert.Equal("Draft", Assert.Single(versions.EnumerateArray()).GetProperty("status").GetString());
    }

    [Theory(DisplayName = "ticket 157 review: the placeholder DEDUP family is refused at publish too")]
    [InlineData("New field 2")]
    [InlineData("New field (copy)")]
    public async Task Placeholder_Dedup_Family_Rejected_At_Publish(string label)
    {
        var resp = await _client.PutAsJsonAsync(
            $"{DefBase}/placeholder-family.v1", PlaceholderLabelBody(label: label));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("form.label.placeholder", body.GetProperty("code").GetString());
        Assert.Equal("name", body.GetProperty("detail").GetProperty("target").GetString());
    }

    [Fact(DisplayName = "ticket 157 review: a legitimate label merely CONTAINING the phrase still publishes")]
    public async Task Label_Containing_Placeholder_Phrase_Publishes()
    {
        var resp = await _client.PutAsJsonAsync(
            $"{DefBase}/legit-label.v1", PlaceholderLabelBody(label: "Add a new field request"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact(DisplayName = "ticket 157 review: a field with NO usable label is refused at publish (form.label.missing)")]
    public async Task Missing_Label_Rejected_At_Publish()
    {
        var resp = await _client.PutAsJsonAsync(
            $"{DefBase}/blank-label.v1", PlaceholderLabelBody(label: "   "));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("form.label.missing", body.GetProperty("code").GetString());
        Assert.Equal("name", body.GetProperty("detail").GetProperty("target").GetString());

        // Same drafts-exempt posture as the placeholder gate.
        var draft = await _client.PutAsJsonAsync(
            $"{DefBase}/blank-label.v1", PlaceholderLabelBody(draft: true, label: "   "));
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
    }

    // ── Ticket 156 review: draft discoverability (list + GET-by-id read paths) ────

    [Fact(DisplayName = "ticket 156 review: a draft-only form is listed via ?includeDrafts (default list unchanged)")]
    public async Task Draft_Only_Form_Listed_Via_IncludeDrafts_OptIn()
    {
        const string draftId = "draft-listed.v1";
        await _client.PutAsJsonAsync($"{DefBase}/{draftId}", PartialDraftBody());

        // Default list: published-only, exactly as before — the draft-only form has no row.
        var defaults = await _client.GetFromJsonAsync<JsonElement>(DefBase);
        Assert.DoesNotContain(defaults.EnumerateArray(),
            row => row.GetProperty("formId").GetString() == draftId);

        // The opt-in (both accepted spellings) surfaces the latest draft, clearly marked.
        foreach (var flag in new[] { "1", "true" })
        {
            var list = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}?includeDrafts={flag}");
            var row = list.EnumerateArray().Single(r => r.GetProperty("formId").GetString() == draftId);
            Assert.Equal("Draft", row.GetProperty("status").GetString());
            Assert.Equal("1.0.0", row.GetProperty("version").GetString());
        }
    }

    [Fact(DisplayName = "ticket 176: Forms LIST stays byte-identical to the pre-catalogue scan with and without drafts")]
    public async Task Forms_List_Is_Byte_Identical_To_The_Pre_Catalogue_Scan()
    {
        // Store order is form id then version. The draft-only id deliberately belongs between the
        // two published ids: appending it after a projected published list changes the wire array.
        await _client.PutAsJsonAsync($"{DefBase}/b-published.v1", SaveBody());
        await _client.PutAsJsonAsync($"{DefBase}/a-draft-only.v1", PartialDraftBody());
        await _client.PutAsJsonAsync($"{DefBase}/c-published.v1", SaveBody());

        var expectedWithoutDrafts = await PreCatalogueListJsonAsync(includeDrafts: false);
        var expectedWithDrafts = await PreCatalogueListJsonAsync(includeDrafts: true);

        using var withoutDraftsResponse = await _client.GetAsync(DefBase);
        using var withDraftsResponse = await _client.GetAsync($"{DefBase}?includeDrafts=1");
        var actualWithoutDrafts = await withoutDraftsResponse.Content.ReadAsByteArrayAsync();
        var actualWithDrafts = await withDraftsResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(Encoding.UTF8.GetBytes(expectedWithoutDrafts), actualWithoutDrafts);
        Assert.Equal(Encoding.UTF8.GetBytes(expectedWithDrafts), actualWithDrafts);
    }

    // Golden oracle transcribed from origin/main's former single store scan. It is intentionally
    // independent of the catalogue projection so a wire-visible reordering fails this test.
    private async Task<string> PreCatalogueListJsonAsync(bool includeDrafts)
    {
        var tenant = NodeTenant.Resolve(_activeTeam);
        var revisions = new List<FormDefinition>();
        await foreach (var definition in _definitions.ListByTenantAsync(tenant))
        {
            revisions.Add(definition);
        }

        var latestDraftByForm = new Dictionary<FormDefinitionId, SemanticVersion>();
        var publishedForms = new HashSet<FormDefinitionId>();
        foreach (var definition in revisions)
        {
            if (definition.Status == FormDefinitionStatus.Draft
                && (!latestDraftByForm.TryGetValue(definition.Id, out var seen)
                    || definition.Version.CompareTo(seen) > 0))
            {
                latestDraftByForm[definition.Id] = definition.Version;
            }
            else if (definition.Status == FormDefinitionStatus.Published)
            {
                publishedForms.Add(definition.Id);
            }
        }

        var summaries = new List<FormDefinitionSummaryDto>();
        foreach (var definition in revisions)
        {
            if (definition.Status == FormDefinitionStatus.Published)
            {
                summaries.Add(FormDefinitionSummaryDto.From(
                    definition,
                    latestDraftByForm.TryGetValue(definition.Id, out var draft)
                        && draft.CompareTo(definition.Version) > 0
                        ? draft.ToString()
                        : null));
            }
            else if (includeDrafts
                && definition.Status == FormDefinitionStatus.Draft
                && !publishedForms.Contains(definition.Id)
                && definition.Version.CompareTo(latestDraftByForm[definition.Id]) == 0)
            {
                summaries.Add(FormDefinitionSummaryDto.From(definition));
            }
        }

        return JsonSerializer.Serialize(summaries, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Fact(DisplayName = "ticket 156 review: a published head with a NEWER draft advertises latestDraftVersion (list + detail)")]
    public async Task Newer_Draft_Advertised_On_Published_Head()
    {
        const string id = "head-with-draft.v1";
        await _client.PutAsJsonAsync($"{DefBase}/{id}", SaveBody());                 // 1.0.0 Published
        await _client.PutAsJsonAsync($"{DefBase}/{id}", PartialDraftBody());         // 1.0.1 Draft

        // Detail: the published head is still served, but the newer draft is detectable —
        // a reloading client must not silently mint its next draft off the stale head.
        var detail = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/{id}");
        Assert.Equal("Published", detail.GetProperty("status").GetString());
        Assert.Equal("1.0.0", detail.GetProperty("version").GetString());
        Assert.Equal("1.0.1", detail.GetProperty("latestDraftVersion").GetString());

        // List: the published row advertises the same marker (and no draft-only row appears).
        var list = await _client.GetFromJsonAsync<JsonElement>(DefBase);
        var row = list.EnumerateArray().Single(r => r.GetProperty("formId").GetString() == id);
        Assert.Equal("Published", row.GetProperty("status").GetString());
        Assert.Equal("1.0.1", row.GetProperty("latestDraftVersion").GetString());

        // A published row with NO newer draft omits the key entirely (additive wire).
        await _client.PutAsJsonAsync($"{DefBase}/{FormId}", SaveBody());
        var plain = (await _client.GetFromJsonAsync<JsonElement>(DefBase))
            .EnumerateArray().Single(r => r.GetProperty("formId").GetString() == FormId);
        Assert.False(plain.TryGetProperty("latestDraftVersion", out _));
    }

    [Fact(DisplayName = "ticket 156 review: a DRAFT save still runs classification admission (restricts refuse)")]
    public async Task Draft_Save_Runs_Classification_Admission()
    {
        // The same forged predefined-system tag the publish path refuses (aspect.unknown_kind):
        // a draft persists AND syncs to peers, so classification content must refuse here too.
        var body = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["x"] = new
                    {
                        label = Text("X"),
                        controlHint = "text",
                        piiSensitivity = "None",
                        aspects = new { classification = new { tags = new[] { new { system = "shipyard/data-classification", code = "pii-typo" } } } },
                    },
                },
                sections = Array.Empty<object>(),
                rules = Array.Empty<object>(),
                title = Text("Bad kind draft"),
            },
            fieldsMeta = new Dictionary<string, object> { ["x"] = new { type = "text", required = false, options = (string[]?)null } },
            draft = true,
        };

        var resp = await _client.PutAsJsonAsync($"{DefBase}/bad-kind-draft.v1", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("aspect.unknown_kind", err.GetProperty("code").GetString());

        // Refused fail-closed: nothing persisted, nothing to sync.
        var versions = await _client.GetFromJsonAsync<JsonElement>($"{DefBase}/bad-kind-draft.v1/versions");
        Assert.Empty(versions.EnumerateArray());
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
