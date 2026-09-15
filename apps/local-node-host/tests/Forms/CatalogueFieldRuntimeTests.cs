using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Forms.DependencyInjection;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.IdentityAtlas.Permissions;
using Harborline.Api.Kernel.Audit;
using Harborline.Api.LocalNodeHost.Data.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.LocalNodeHost.Health;

namespace Harborline.Api.LocalNodeHost.Tests.Forms;

public sealed class CatalogueFieldRuntimeTests
{
    private static readonly TenantId Tenant = new("catalogue-runtime");
    private static readonly CatalogueFieldCoordinate Coordinate = new(1, "FormDefinition", "source", "1.0.0", "title");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_one_field_request_never_expands_to_siblings(bool allowed)
    {
        var fixture = new Fixture();
        var request = JsonSerializer.SerializeToElement(new[] { fixture.Request()[1] });
        var result = await fixture.Runtime(_ => allowed).ProjectAsync("platform.detail.form", "1.0.0", request,
            TestAuthorization.Write(Tenant));
        Assert.Equal(allowed ? new[] { "title" } : [], result.Values.Keys);
        Assert.Equal(allowed ? new[] { "title" } : [], result.FieldsMeta.Keys);
        Assert.Equal(allowed ? new[] { "title" } : [], fixture.Reads);
        Assert.Single(fixture.Decisions);
        Assert.Equal(1, fixture.Binds);
    }

    [Theory]
    [InlineData("id", CatalogueFieldSourceCodes.SourceBindingMismatch)]
    [InlineData("version", CatalogueFieldSourceCodes.SourceBindingMismatch)]
    [InlineData("hash", CatalogueFieldSourceCodes.SourceBindingMismatch)]
    [InlineData("provenance", CatalogueFieldSourceCodes.SourceBindingMismatch)]
    [InlineData("kind", CatalogueFieldSourceCodes.UnsupportedCoordinate)]
    [InlineData("schema", CatalogueFieldSourceCodes.UnsupportedCoordinate)]
    [InlineData("latest", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("duplicate", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("order", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("empty", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("single-object", CatalogueFieldSourceCodes.MalformedCoordinate)]
    public async Task Every_explicit_request_is_validated_atomically_before_any_closure(string mutation, string code)
    {
        var fixture = new Fixture();
        var requests = System.Text.Json.Nodes.JsonNode.Parse(fixture.Request().GetRawText())!.AsArray();
        switch (mutation)
        {
            case "id": requests[3]!["coordinate"]!["id"] = "other"; break;
            case "version": requests[3]!["coordinate"]!["version"] = "9.0.0"; break;
            case "hash": requests[3]!["sourceBinding"]!["definitionHash"] = "sha256:" + new string('b', 64); break;
            case "provenance": requests[3]!["sourceBinding"]!["provenance"] = System.Text.Json.Nodes.JsonNode.Parse(
                "{\"kind\":\"pack\",\"packKey\":\"other\",\"packVersion\":\"1.0.0\"}"); break;
            case "kind": requests[3]!["coordinate"]!["kind"] = "ViewDefinition"; break;
            case "schema": requests[3]!["coordinate"]!["schemaVersion"] = 2; break;
            case "latest": requests[3]!["coordinate"]!["version"] = "latest"; break;
            case "duplicate": requests[3] = requests[0]!.DeepClone(); break;
            case "order": requests[0]!["coordinate"]!["field"] = "title"; requests[1]!["coordinate"]!["field"] = "formId"; break;
            case "empty": requests.Clear(); break;
        }
        var request = JsonSerializer.SerializeToElement(mutation == "single-object" ? requests[0] : requests);
        var failure = await Assert.ThrowsAsync<CatalogueFieldSourceException>(() => fixture.Runtime(_ => true).ProjectAsync(
            "platform.detail.form", "1.0.0", request, TestAuthorization.Write(Tenant)).AsTask());
        Assert.Equal(code, failure.Code);
        Assert.Equal(0, fixture.Binds);
        Assert.Empty(fixture.Reads);
        Assert.Empty(fixture.Decisions);
    }

    [Fact]
    public async Task Legacy_write_that_cannot_be_canonically_indexed_remains_successful_and_typed_source_stays_cold()
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        using var forms = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        var legacy = Source() with { Overlay = HarborlineOverlay.Empty with { Title = InternationalizedText.FromInvariant("\uD800") } };
        var persisted = await forms.RegisterAndPublishAsync(legacy, TestAuthorization.Write(Tenant));
        Assert.Equal(FormDefinitionStatus.Published, persisted.Status);
        Assert.Equal(persisted, await store.GetAsync(new DefinitionCoordinates(Tenant, "source", "1.0.0")));
        Assert.Null(forms.CatalogueSources.Resolve(Tenant, Coordinate));
    }

    [Fact]
    public async Task Newer_published_revision_does_not_redirect_the_existing_immutable_source()
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        using var forms = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        await forms.RegisterAndPublishAsync(Source(), TestAuthorization.Write(Tenant));
        var original = forms.CatalogueSources.Resolve(Tenant, Coordinate)!;
        await forms.RegisterAndPublishAsync(Source() with
        {
            Version = new SemanticVersion(2, 0, 0),
            Overlay = HarborlineOverlay.Empty with { Title = InternationalizedText.FromInvariant("new revision") },
        }, TestAuthorization.Write(Tenant));
        Assert.Equal(original.Identity, forms.CatalogueSources.Resolve(Tenant, Coordinate)!.Identity);
        Assert.Equal("sentinel-do-not-read", original.Bind(Coordinate, original.Identity.Binding)().Value
            .GetProperty("values").GetProperty("en").GetString());
    }

    [Fact]
    public async Task Lifecycle_source_binding_and_values_are_unchanged_by_caller_collection_mutation()
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        using var forms = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        var source = Source();
        await forms.RegisterAndPublishAsync(source, TestAuthorization.Write(Tenant));
        var handle = forms.CatalogueSources.Resolve(Tenant, Coordinate)!;
        var binding = handle.Identity.Binding;
        ((Dictionary<string, string>)source.Overlay.Title!.Values)["en"] = "caller-replacement";
        Assert.Equal(binding, forms.CatalogueSources.Resolve(Tenant, Coordinate)!.Identity.Binding);
        Assert.Equal("sentinel-do-not-read", handle.Bind(Coordinate, binding)().Value.GetProperty("values").GetProperty("en").GetString());
    }

    [Fact]
    public async Task Source_change_after_gate_is_refused_before_any_protected_getter()
    {
        var fixture = new Fixture();
        var runtime = fixture.Runtime(_ => { fixture.Changed = true; return true; });
        var failure = await Assert.ThrowsAsync<CatalogueFieldSourceException>(() => runtime.ProjectAsync(
            "platform.detail.form", "1.0.0", fixture.Request(), TestAuthorization.Write(Tenant)).AsTask());
        Assert.Equal(CatalogueFieldSourceCodes.SourceChangedAfterAuthorization, failure.Code);
        Assert.Empty(fixture.Reads);
        Assert.Single(fixture.Decisions);
    }

    [Fact]
    public async Task Allowed_null_title_is_present_and_not_substituted_with_the_id()
    {
        var fixture = new Fixture { NullTitle = true };
        var result = await fixture.Runtime(_ => true).ProjectAsync("platform.detail.form", "1.0.0",
            fixture.Request(), TestAuthorization.Write(Tenant));
        Assert.Equal(JsonValueKind.Null, result.Values["title"].ValueKind);
        Assert.True(result.FieldsMeta.ContainsKey("title"));
    }

    [Fact]
    public void Runtime_capability_requires_the_actual_composed_reader()
    {
        using var absent = new ServiceCollection().AddCatalogueFieldSourceRuntime("1.0.0").BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => absent.GetRequiredService<IPackPlatformCompatibility>());
        using var composed = new ServiceCollection().AddTestAuthorizationGate().AddInMemoryFormDefinitionStore()
            .AddCatalogueFieldSourceRuntime("1.0.0").BuildServiceProvider();
        Assert.Contains(CatalogueFieldSourceContract.CapabilityId, composed.GetRequiredService<IPackPlatformCompatibility>().Provides);
        var declaration = JsonSerializer.Deserialize<CatalogueFieldSource>(CatalogueFieldSourceContractTests.Declaration)!;
        Assert.Null(composed.GetRequiredService<CatalogueFieldSourceAdmission>().ValidateSupport(declaration));
    }

    [Fact]
    public async Task HTTP_projection_retains_denial_audit_correlation_without_source_identity_or_sentinel_leakage()
    {
        var fixture = new Fixture();
        var runtime = fixture.Runtime(request => request.Target.Scope.Value.EndsWith("/cascadeLayer", StringComparison.Ordinal));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        using var keys = KeyPair.Generate();
        var trail = new InMemoryAuditTrail();
        builder.Services.AddTestAuthorizationGate().AddSingleton<IAuditTrail>(trail)
            .AddSingleton<IOperationSigner>(new Ed25519Signer(keys)).AddAuthorizationRefusalAudit();
        await using var app = builder.Build();
        app.Use(async (http, next) =>
        {
            http.Features.Set(new SelectedSessionRequestPrincipal("account", Tenant, new PrincipalUserId("principal"),
                new CanonicalPartyReference("party"), "membership", 1,
                [new PinnedGrantOwnerVersion("grant", 1)], 1, "session", "coordination"));
            await next(http);
        });
        CatalogueDetailRoutes.Map(app, runtime);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var response = await client.PostAsJsonAsync("/api/local-node/catalogue/details/platform.detail.form/1.0.0", fixture.Request());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["cascadeLayer"], body.GetProperty("projection").GetProperty("values").EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("sentinel-do-not-read", body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("\"source\"", body.GetRawText(), StringComparison.Ordinal);
        var records = new List<AuditRecord>();
        await foreach (var record in trail.QueryAsync(new AuditQuery(Tenant))) records.Add(record);
        Assert.Equal(3, records.Count);
        Assert.Equal(records.Select(record => record.AuditId).Order(), body.GetProperty("refusals").EnumerateArray()
            .Select(refusal => refusal.GetProperty("auditId").GetGuid()).Order());
        Assert.All(body.GetProperty("refusals").EnumerateArray(), refusal =>
            Assert.Equal(AuthorizationRefusalRenderer.PermissionRequiredCode, refusal.GetProperty("code").GetString()));
        Assert.Equal(4, fixture.Decisions.Count);
        Assert.Equal(["cascadeLayer"], fixture.Reads);
        await app.StopAsync();
    }

    [Fact]
    public async Task Denied_non_pii_title_has_zero_getter_reads_and_no_declaration_value_or_section_reference()
    {
        var fixture = new Fixture();
        var runtime = fixture.Runtime(request => request.Target.Scope.Value.EndsWith("/formId", StringComparison.Ordinal));
        var result = await runtime.ProjectAsync("platform.detail.form", "1.0.0", fixture.Request(), TestAuthorization.Write(Tenant));
        Assert.Equal(["formId"], result.Values.Keys);
        Assert.Equal(["formId"], result.FieldsMeta.Keys);
        Assert.Equal(["formId"], result.Overlay.GetProperty("fields").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["formId"], result.Overlay.GetProperty("sections")[0].GetProperty("fields").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(["formId"], fixture.Reads);
        Assert.Equal(3, result.Denials.Count);
        Assert.Equal(4, fixture.Decisions.Count);
        Assert.All(fixture.Decisions, request => Assert.Contains("/catalogue-fields/1/FormDefinition/1.0.0/", request.Target.Scope.Value));
        Assert.DoesNotContain("sentinel-do-not-read", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(4, fixture.Content.GetProperty("overlay").GetProperty("fields").EnumerateObject().Count());
        var next = await fixture.Runtime(_ => true).ProjectAsync("platform.detail.form", "1.0.0", fixture.Request(), TestAuthorization.Write(Tenant));
        Assert.Equal(["formId", "title", "version", "cascadeLayer"], next.Values.Keys);
        Assert.Equal("sentinel-do-not-read", next.Values["title"].GetProperty("values").GetProperty("en").GetString());
    }

    [Theory]
    [InlineData("mapping", CatalogueFieldSourceCodes.MalformedSourceMapping)]
    [InlineData("legacy", CatalogueFieldSourceCodes.LegacyMappingAbsent)]
    [InlineData("hash", CatalogueFieldSourceCodes.SourceBindingMismatch)]
    [InlineData("provenance", CatalogueFieldSourceCodes.SourceBindingMismatch)]
    [InlineData("version", CatalogueFieldSourceCodes.SourceVersionUnavailable)]
    [InlineData("cold-detail", CatalogueFieldSourceCodes.SourceVersionUnavailable)]
    [InlineData("missing-version", CatalogueFieldSourceCodes.MalformedCoordinate)]
    [InlineData("unknown", CatalogueFieldSourceCodes.UnknownCoordinate)]
    [InlineData("unsupported", CatalogueFieldSourceCodes.UnsupportedCoordinate)]
    public async Task Invalid_request_never_constructs_a_field_closure_or_calls_the_gate(string mutation, string code)
    {
        var fixture = new Fixture();
        var requests = System.Text.Json.Nodes.JsonNode.Parse(fixture.Request().GetRawText())!;
        var request = requests[0]!;
        switch (mutation)
        {
            case "mapping": fixture.Content = JsonSerializer.SerializeToElement(CatalogueFieldSourceContractTests.Content());
                var content = System.Text.Json.Nodes.JsonNode.Parse(fixture.Content.GetRawText())!;
                content["catalogueFieldSource"]!["fields"]![0]!["extra"] = true;
                fixture.Content = JsonSerializer.SerializeToElement(content); break;
            case "legacy": fixture.Content = JsonSerializer.SerializeToElement(CatalogueFieldSourceContractTests.Content(false)); break;
            case "hash": request["sourceBinding"]!["definitionHash"] = "sha256:" + new string('b', 64); break;
            case "provenance": request["sourceBinding"]!["provenance"] = System.Text.Json.Nodes.JsonNode.Parse("""{"kind":"pack","packKey":"forged","packVersion":"1.0.0"}"""); break;
            case "version": foreach (var entry in requests.AsArray()) entry!["coordinate"]!["version"] = "2.0.0"; break;
            case "cold-detail": fixture.ColdDetail = true; break;
            case "missing-version": request["coordinate"]!.AsObject().Remove("version"); break;
            case "unknown": request["coordinate"]!["field"] = "secret"; break;
            case "unsupported": request["coordinate"]!["kind"] = "ViewDefinition"; break;
        }
        var runtime = fixture.Runtime(_ => true);
        var failure = await Assert.ThrowsAsync<CatalogueFieldSourceException>(() => runtime.ProjectAsync(
            "platform.detail.form", "1.0.0", JsonSerializer.SerializeToElement(requests), TestAuthorization.Write(Tenant)).AsTask());
        Assert.Equal(code, failure.Code);
        Assert.Equal(0, fixture.Binds);
        Assert.Empty(fixture.Reads);
        Assert.Empty(fixture.Decisions);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("provenance")]
    [InlineData("value")]
    public async Task Returned_payload_binding_or_value_mismatch_discards_the_entire_projection(string mutation)
    {
        var fixture = new Fixture { PayloadMutation = mutation };
        var failure = await Assert.ThrowsAsync<CatalogueFieldSourceException>(() => fixture.Runtime(_ => true).ProjectAsync(
            "platform.detail.form", "1.0.0", fixture.Request(), TestAuthorization.Write(Tenant)).AsTask());
        Assert.Equal(CatalogueFieldSourceCodes.PayloadBindingMismatch, failure.Code);
        Assert.Equal(["formId"], fixture.Reads);
    }

    [Fact]
    public async Task Lifecycle_source_is_cold_until_published_and_withdrawal_invalidates_bound_getter()
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        var forms = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        Assert.Null(forms.CatalogueSources.Resolve(Tenant, Coordinate));
        await forms.RegisterAndPublishAsync(Source(), TestAuthorization.Write(Tenant));
        var handle = Assert.IsAssignableFrom<CatalogueFormSourceHandle>(forms.CatalogueSources.Resolve(Tenant, Coordinate));
        var read = handle.Bind(Coordinate, handle.Identity.Binding);
        Assert.Equal("sentinel-do-not-read", read().Value.GetProperty("values").GetProperty("en").GetString());
        await forms.WithdrawAsync(new DefinitionCoordinates(Tenant, "source", "1.0.0"), TestAuthorization.Write(Tenant));
        Assert.Null(forms.CatalogueSources.Resolve(Tenant, Coordinate));
        Assert.Equal(CatalogueFieldSourceCodes.SourceChangedAfterAuthorization,
            Assert.Throws<CatalogueFieldSourceException>(() => read()).Code);
    }

    [Fact]
    public async Task Binding_mismatch_and_tenant_mismatch_never_bind_a_field()
    {
        using var store = new InMemoryFormDefinitionStore(TimeProvider.System);
        var forms = TestAuthorization.FormLifecycle(store, TestAuthorization.AllowGate(), TestAuthorization.RoleGate());
        await forms.RegisterAndPublishAsync(Source(), TestAuthorization.Write(Tenant));
        var handle = forms.CatalogueSources.Resolve(Tenant, Coordinate)!;
        Assert.Null(forms.CatalogueSources.Resolve(new TenantId("foreign"), Coordinate));
        Assert.Equal(CatalogueFieldSourceCodes.SourceBindingMismatch, Assert.Throws<CatalogueFieldSourceException>(() =>
            handle.Bind(Coordinate, handle.Identity.Binding with { Provenance = new CatalogueFieldProvenance("pack", "forged", "1.0.0") })).Code);
    }

    internal static FormDefinition Source() => new(new FormDefinitionId("source"), new SemanticVersion(1, 0, 0),
        FormDefinitionStatus.Draft, Tenant, IdentityRef.System, new SchemaId("test:schema"),
        HarborlineOverlay.Empty with { Title = InternationalizedText.FromInvariant("sentinel-do-not-read") }, null,
        TestAuthorization.At, TestAuthorization.At);

    private sealed class Fixture : ICatalogueFormSources
    {
        internal readonly CatalogueFieldSourceBinding Binding = new("sha256:" + new string('a', 64), new("tenant"));
        internal JsonElement Content = JsonSerializer.SerializeToElement(CatalogueFieldSourceContractTests.Content());
        internal readonly List<string> Reads = [];
        internal readonly List<AuthorizationGateRequest> Decisions = [];
        internal int Binds;
        internal bool ColdDetail;
        internal bool Changed;
        internal bool NullTitle;
        internal string? PayloadMutation;
        private CatalogueFormSourceIdentity Identity(string id) => new(Tenant, "FormDefinition", id, "1.0.0", Binding);
        internal CatalogueDetailRuntime Runtime(Func<AuthorizationGateRequest, bool> allow)
        {
            var templates = new CatalogueDetailTemplates();
            templates.Store(Content, Identity("platform.detail.form"), Binding);
            return new(this, templates, TestAuthorization.Gate(allow, Decisions.Add));
        }
        internal JsonElement Request() => JsonSerializer.SerializeToElement(CatalogueFieldSourceContract.Fields.Select(field =>
            new CatalogueFieldReadRequest(Coordinate with { Field = field.FieldId }, Binding)).ToArray());
        public CatalogueFormSourceHandle? Resolve(TenantId tenant, CatalogueFieldCoordinate coordinate) =>
            tenant == Tenant && coordinate.Version == "1.0.0" && !(ColdDetail && coordinate.Id == "platform.detail.form")
                ? new Handle(this, Identity(coordinate.Id)) : null;
        private sealed class Handle(Fixture owner, CatalogueFormSourceIdentity identity) : CatalogueFormSourceHandle(identity)
        {
            public override Func<CatalogueFieldPayload> Bind(CatalogueFieldCoordinate coordinate, CatalogueFieldSourceBinding binding)
            {
                owner.Binds++;
                return () =>
                {
                    if (owner.Changed) throw new CatalogueFieldSourceException(CatalogueFieldSourceCodes.SourceChangedAfterAuthorization);
                    owner.Reads.Add(coordinate.Field);
                    var result = new CatalogueFieldPayload(Identity, coordinate, coordinate.Field switch
                    {
                        "formId" => JsonSerializer.SerializeToElement("source"),
                        "title" => owner.NullTitle ? JsonSerializer.SerializeToElement<object?>(null)
                            : JsonSerializer.SerializeToElement(new { defaultLocale = "en", values = new { en = "sentinel-do-not-read" } }),
                        "version" => JsonSerializer.SerializeToElement("1.0.0"),
                        _ => JsonSerializer.SerializeToElement("Tenant"),
                    });
                    return owner.PayloadMutation switch
                    {
                        "identity" => result with { Identity = Identity with { Id = "forged" } },
                        "provenance" => result with { Identity = Identity with { Binding = binding with { Provenance = new("pack", "forged", "1.0.0") } } },
                        "value" => result with { Value = JsonSerializer.SerializeToElement("forged") },
                        _ => result,
                    };
                };
            }
        }
    }
}
