using System.Collections.Generic;
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
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Engine;
using Harborline.Api.Foundation.Forms.Engine.Capabilities;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Forms;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

/// <summary>
/// ADR 0055 forms-engine wiring (2026-06-25) — route-level end-to-end tests that
/// prove the built-but-unwired engine is now CONSUMABLE through the node host.
/// Hosts the SAME production route handlers <see cref="HostedFormsApiEndpoint"/>
/// registers (<see cref="FormsRoutes.Map"/> — the single source of truth, no
/// test/prod wire drift) over a real in-process Kestrel listener using the SAME
/// composition (<see cref="NodeFormsComposition.AddNodeForms"/>), and drives them
/// with a real <see cref="HttpClient"/>. Mirrors <c>CalendarRouteTests</c>.
/// </summary>
/// <remarks>
/// Proves the FormDefinition → FormView → submit round-trip: a registered +
/// published first-party form renders a localized view over HTTP; a valid
/// candidate submits and mints a new instance; the instance is then readable
/// back by id (INV-S1 same-tenant read); a schema-invalid candidate is a 422
/// carrying the JSON-Pointer-located errors; cross-tenant isolation holds
/// (switching the active team renders a different — not-found — form). Uses the
/// in-memory entity store the route contract is agnostic to (the durable node-EF
/// forms store is the follow-up).
/// </remarks>
public sealed class FormsRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-00000000fa01"));
    private static readonly TeamId TeamB = new(Guid.Parse("bbbb0000-0000-0000-0000-00000000fb01"));
    private static readonly TenantId TenantA = ActiveTeamTenantContext.ProjectTenantId(TeamA);
    private static readonly ActorId Operator = new("local");

    private const string Base = "/api/local-node/forms";
    private const string FormId = "inspection.basic.v1";

    // The form gates everything on the operator's role so the minted capability
    // can read + write every section (the single-operator full-authority posture).
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

        // The PRODUCTION forms composition (engine + schema registry + entity store +
        // audit + macaroon capability pair + durable form-definition store). Add the
        // field encryptor the recovery coordinator would normally supply (INV-S3) — a
        // test double over an in-memory tenant-key provider so PII fields round-trip.
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms();

        _app = builder.Build();

        // Register the form's JSON Schema in the kernel registry, then register +
        // publish a first-party FormDefinition referencing that schema CID.
        var registry = _app.Services.GetRequiredService<ISchemaRegistry>();
        var store = _app.Services.GetRequiredService<IFormDefinitionStore>();

        var schema = await registry.RegisterAsync(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "properties": {
                "station": { "type": "string" },
                "result": { "type": "string", "enum": ["PASS", "FAIL"] },
                "inspector": { "type": "string" }
              },
              "required": ["station", "result"],
              "additionalProperties": false
            }
            """);

        var def = new FormDefinition(
            Id: new FormDefinitionId(FormId),
            Version: new SemanticVersion(1, 0, 0),
            Status: FormDefinitionStatus.Draft,
            Tenant: TenantA,
            Owner: IdentityRef.System,
            SchemaRef: schema.Id,
            Overlay: new HarborlineOverlay(
                Fields: new Dictionary<string, FieldOverlay>
                {
                    ["station"] = new(InternationalizedText.FromInvariant("Station"), ControlHint: "text"),
                    ["result"] = new(InternationalizedText.FromInvariant("Result"), ControlHint: "text"),
                    // PII-tagged: never rendered, encrypted on save (INV-S3).
                    ["inspector"] = new(InternationalizedText.FromInvariant("Inspector"),
                        PiiSensitivity: PiiSensitivity.Sensitive),
                },
                Sections: new[]
                {
                    new FormSection(
                        Id: "main",
                        Title: InternationalizedText.FromInvariant("Inspection"),
                        Fields: new[] { "station", "result", "inspector" },
                        Access: new SectionAccess(
                            ReadRoles: new[] { FormsRoutes.NodeOperatorRole },
                            WriteRoles: new[] { FormsRoutes.NodeOperatorRole })),
                },
                Rules: Array.Empty<RuleDefinition>(),
                Title: InternationalizedText.FromInvariant("Basic Inspection")),
            Lineage: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        await store.RegisterAsync(def);
        await store.PublishAsync(new DefinitionCoordinates(TenantA, def.Id.Value, def.Version.ToString()));

        _activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));

        // The durable forms mechanism owns this route; the node-wide middleware must skip it.
        NodeMutationIdempotency.UseOnce(_app, TimeProvider.System);

        // Map the SAME production routes (mirrors HostedFormsApiEndpoint wiring — no [FromServices]).
        FormsRoutes.Map(
            _app,
            _app.Services.GetRequiredService<IFormEngine>(),
            _app.Services.GetRequiredService<IFormCapabilityIssuer>(),
            _app.Services.GetRequiredService<IFormCapabilityVerifier>(),
            _activeTeam,
            OperatorRoles,
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

    [Fact(DisplayName = "render: GET returns the published form's localized view (PII field redacted)")]
    public async Task Render_Returns_FormView()
    {
        var doc = await _client.GetFromJsonAsync<JsonElement>($"{Base}/{FormId}");

        Assert.Equal(FormId, doc.GetProperty("formId").GetString());
        Assert.Equal("1.0.0", doc.GetProperty("version").GetString());

        var section = doc.GetProperty("sections")[0];
        Assert.Equal("main", section.GetProperty("id").GetString());

        var fields = section.GetProperty("fields");
        Assert.Equal(3, fields.GetArrayLength());

        // The PII field is present in the structure but never readable in a view.
        var inspector = FieldByName(fields, "inspector");
        Assert.True(inspector.GetProperty("isSensitive").GetBoolean());
        Assert.False(inspector.GetProperty("isReadable").GetBoolean());
    }

    [Fact(DisplayName = "submit: a valid candidate persists a new instance, readable back by id (INV-S1)")]
    public async Task Submit_RoundTrips_Instance()
    {
        var candidate = new { station = "Burj Khalifa", result = "PASS", inspector = "A. Khan" };
        var resp = await _client.PostAsJsonAsync($"{Base}/{FormId}/submit", candidate);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var created = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var instanceId = created.GetProperty("instanceId").GetString()!;
        Assert.StartsWith("forminst:", instanceId);

        // Read it back through the SAME tenant — INV-S1 same-tenant read returns the
        // non-PII values; the PII field stays redacted.
        var view = await _client.GetFromJsonAsync<JsonElement>(
            $"{Base}/{FormId}?instance={Uri.EscapeDataString(instanceId)}");

        var fields = view.GetProperty("sections")[0].GetProperty("fields");
        Assert.Equal("Burj Khalifa", FieldByName(fields, "station").GetProperty("value").GetString());
        Assert.Equal("PASS", FieldByName(fields, "result").GetProperty("value").GetString());
        // PII field: redacted (null value) even on read-back.
        Assert.Equal(JsonValueKind.Null, FieldByName(fields, "inspector").GetProperty("value").ValueKind);
    }

    [Fact(DisplayName = "submit: a schema-invalid candidate is a 422 with JSON-Pointer errors")]
    public async Task Submit_Invalid_422()
    {
        // 'result' must be PASS|FAIL; 'station' is required.
        var bad = new { result = "MAYBE" };
        var resp = await _client.PostAsJsonAsync($"{Base}/{FormId}/submit", bad);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("isValid").GetBoolean());
        var errors = body.GetProperty("errors");
        Assert.True(errors.GetArrayLength() > 0);

        // ADR 0055 localizable-validation: every wire error carries a stable `code` (the failing
        // JSON-Schema keyword) so the client localizes instead of parsing the English `message`.
        foreach (var err in errors.EnumerateArray())
        {
            Assert.True(err.TryGetProperty("code", out var code), "wire error is missing `code`");
            Assert.False(string.IsNullOrWhiteSpace(code.GetString()));
            Assert.True(err.TryGetProperty("message", out var msg) && !string.IsNullOrWhiteSpace(msg.GetString()));
        }
    }

    [Fact(DisplayName = "tenant: switching the active team renders a different (not-found) form — server-side tenant")]
    public async Task Tenant_IsServerSide_CrossTenantIsolated()
    {
        _activeTeam.Active = TeamContextFor(TeamB, "Team B");

        var resp = await _client.GetAsync($"{Base}/{FormId}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        _activeTeam.Active = TeamContextFor(TeamA, "Team A");
    }

    [Fact(DisplayName = "render: an unknown form id is a 404")]
    public async Task Render_UnknownForm_404()
    {
        var resp = await _client.GetAsync($"{Base}/does.not.exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // ── Ticket 094: the one Forms error envelope, pinned on the wire ──────────────────

    [Fact(DisplayName = "094: an unknown form id answers { code, detail } — never an English `error`")]
    public async Task Render_UnknownForm_Is_The_Code_Envelope()
    {
        var resp = await _client.GetAsync($"{Base}/does.not.exist");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("form_definition.not_published", body.GetProperty("code").GetString());
        Assert.Equal("does.not.exist", body.GetProperty("detail").GetProperty("formId").GetString());
        Assert.False(body.TryGetProperty("error", out _));
    }

    [Fact(DisplayName = "094: an over-long Idempotency-Key keeps its diagnostic as structured detail")]
    public async Task Overlong_Idempotency_Key_Carries_Structured_Detail()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/{FormId}/submit")
        {
            Content = JsonContent.Create(new { station = "Marina", result = "PASS" }),
        };
        req.Headers.TryAddWithoutValidation(FormsRoutes.IdempotencyKeyHeader, new string('k', 4096));
        var resp = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forms.idempotency_key_too_long", body.GetProperty("code").GetString());

        // The parameterized diagnostic the English sentence used to carry survives as named fields:
        // WHICH header, and what the limit is.
        var detail = body.GetProperty("detail");
        Assert.Equal(FormsRoutes.IdempotencyKeyHeader, detail.GetProperty("header").GetString());
        Assert.True(detail.GetProperty("maxLength").GetInt32() > 0);
        Assert.False(body.TryGetProperty("error", out _));
    }

    // ── #144 runtime form-fill: the optional Into-Case-Ref header (shape only, never authorized on) ──

    [Fact(DisplayName = "submit: an Into-Case-Ref header is accepted and the submission still commits (201)")]
    public async Task Submit_With_Case_Ref_Header_Still_Commits()
    {
        // This host wires only AddNodeForms (no registry projections), so the header links nothing here —
        // the point is that the route ACCEPTS the header and the submit is byte-identical: a bare 201 with
        // no projection/skips fields. (The linkage e2e lives in AssetRegistryRouteTests where the registry
        // projection is wired.)
        var candidate = new { station = "Marina", result = "PASS" };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/{FormId}/submit")
        {
            Content = JsonContent.Create(candidate),
        };
        req.Headers.TryAddWithoutValidation(FormsRoutes.IntoCaseRefHeader, "entity:some/record-1");
        var resp = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("forminst:", body.GetProperty("instanceId").GetString());
        // Byte-shape guard: a case-ref submit carries no projection/skips fields when nothing was linked.
        Assert.False(body.TryGetProperty("projection", out _));
        Assert.False(body.TryGetProperty("skips", out _));
    }

    [Fact(DisplayName = "submit: NO Into-Case-Ref header ⇒ a bare 201 (byte-identical to before #144)")]
    public async Task Submit_Without_Case_Ref_Header_Is_Byte_Identical()
    {
        var candidate = new { station = "JLT", result = "PASS" };
        var resp = await _client.PostAsJsonAsync($"{Base}/{FormId}/submit", candidate);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("projection", out _));
        Assert.False(body.TryGetProperty("skips", out _));
    }

    [Fact(DisplayName = "submit: durable forms idempotency is not stacked by node middleware")]
    public async Task Submit_DurableIdempotency_IsNotStacked()
    {
        using var first = new HttpRequestMessage(HttpMethod.Post, $"{Base}/{FormId}/submit")
        {
            Content = JsonContent.Create(new { station = "JLT", result = "PASS" }),
        };
        first.Headers.Add(FormsRoutes.IdempotencyKeyHeader, "forms-durable");
        using var second = new HttpRequestMessage(HttpMethod.Post, $"{Base}/{FormId}/submit")
        {
            Content = JsonContent.Create(new { station = "Marina", result = "PASS" }),
        };
        second.Headers.Add(FormsRoutes.IdempotencyKeyHeader, "forms-durable");

        using var firstResponse = await _client.SendAsync(first);
        using var secondResponse = await _client.SendAsync(second);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        Assert.Equal(
            await firstResponse.Content.ReadAsStringAsync(),
            await secondResponse.Content.ReadAsStringAsync());
    }

    [Fact(DisplayName = "submit: an over-long Into-Case-Ref header is rejected (400) — a bounded token")]
    public async Task Submit_With_Over_Long_Case_Ref_Header_Is_400()
    {
        var candidate = new { station = "DIFC", result = "PASS" };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/{FormId}/submit")
        {
            Content = JsonContent.Create(candidate),
        };
        req.Headers.TryAddWithoutValidation(FormsRoutes.IntoCaseRefHeader, new string('x', 201));
        var resp = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private static JsonElement FieldByName(JsonElement fields, string name)
    {
        foreach (var f in fields.EnumerateArray())
        {
            if (f.GetProperty("name").GetString() == name)
            {
                return f;
            }
        }
        throw new Xunit.Sdk.XunitException($"field '{name}' not found in view");
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
