using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Model;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Entities;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Definitions;
using Harborline.Api.Foundation.Forms;
using Harborline.Api.Foundation.Forms.Models;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.Kernel.Schema;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.PackProjection;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;

using Xunit;

using NodeOperatorCommand = global::Harborline.Api.NodeOperatorCli.OperatorCli;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Ticket 357 — the MVP pack's cross-pillar binding survives the HEADLESS round trip. A hand-authored pack
/// carrying one Records type (<c>AssetTypeDefinition</c>) and one Form (<c>FormDefinition</c>) the type binds
/// as its property form travels through the operator CLI verbs the ticket 118 map names for steps 4–7
/// (<c>pack export</c> → <c>pack verify</c> → <c>pack install</c> → <c>pack activate</c>, driven over a REAL
/// listener with the production route handlers and the production <see cref="PackSeedProjector"/>), and the
/// binding then RESOLVES to the installed form's identity — the map's step 9 read is raw loopback HTTP by
/// deliberate CLI exemption.
/// </summary>
/// <remarks>
/// Also pins the versioned-shape contract: a pack authored on the PREVIOUS content version (no binding field)
/// still verifies and activates, and a pack that declares the previous version while CARRYING the field is
/// refused BY NAME — as is a binding naming a form the pack does not contain (a cross-pack binding is out of
/// scope for v1 and refused, never silently dropped).
/// </remarks>
public sealed class PackFormBindingRoundTripTests : IAsyncLifetime
{
    private const string PackKey = "harborline.notes-lite";
    private const string TypeKey = "notes.entry";
    private const string FormKey = "notes.capture";
    private const string FormVersion = "1.2.0";
    private const string ElectricalFormKey = "notes.electrical";
    private const string MechanicalFormKey = "notes.mechanical";
    private const string AssetTypesRoute = "/api/local-node/asset-registry/types";

    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000357"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;
    private IEntityTypeRegistry _registry = null!;
    private IFormDefinitionStore _forms = null!;
    private MutableActiveTeamAccessor _activeTeam = null!;
    private string _address = null!;
    private string _scratch = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddLogging();
        builder.Services.AddInMemoryAssetTypeSystem();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.TenantKey.ITenantKeyProvider,
            Harborline.Api.Foundation.Recovery.TenantKey.InMemoryTenantKeyProvider>();
        builder.Services.AddSingleton<Harborline.Api.Foundation.Recovery.Crypto.IFieldEncryptor,
            Harborline.Api.Foundation.Recovery.Crypto.TenantKeyProviderFieldEncryptor>();
        builder.Services.AddTestAuthorizationGate().AddTestNodeForms(
            configureWriters: static (services, entityMutations, _) =>
                services.AddEntityStoreWorkflowDefinitionStore(entityMutations));
        builder.Services.AddSingleton<ICapabilityAuthorityRegistry>(CapabilityAuthorityRegistry.Canonical);
        builder.Services.AddSingleton<IWorkflowAdmissionValidator, WorkflowAdmissionValidator>();
        _app = builder.Build();

        _key = KeyPair.Generate();
        var signer = new Ed25519Signer(_key);
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(
                TrustScope.OwnRoster, _key.PrincipalId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        });
        _activeTeam = new MutableActiveTeamAccessor(
            new TeamContext(TeamA, "Notes Co", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));

        _registry = _app.Services.GetRequiredService<IEntityTypeRegistry>();
        _forms = _app.Services.GetRequiredService<IFormDefinitionStore>();
        var schemas = _app.Services.GetRequiredService<ISchemaRegistry>();
        var workflows = _app.Services.GetRequiredService<IWorkflowDefinitionStore>();
        var roleGate = _app.Services.GetRequiredService<IRoleGateAdmission>();
        var packStore = new InMemoryPackInstallStore();
        var projector = new PackSeedProjector(
            packStore,
            _registry,
            NullLogger<PackSeedProjector>.Instance,
            forms: _forms,
            schemas: schemas,
            workflows: workflows,
            time: TimeProvider.System,
            authorizedForms: TestAuthorization.FormLifecycle(_forms, TestAuthorization.AllowGate(), roleGate),
            authorizedWorkflows: TestAuthorization.WorkflowLifecycle(workflows, TestAuthorization.AllowGate(), roleGate));
        var installer = new PackInstaller(
            verifier, packStore, new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator()),
            new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());

        var authz = TestPackGate.AllowAll();
        _app.Use(async (http, next) =>
        {
            http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        PackComposerRoutes.Map(
            _app, exporter, verifier, trustStore, signer, _activeTeam, authz, TimeProvider.System, NullLogger.Instance);
        PackInstallRoutes.Map(
            _app, installer, packStore, trustStore, PackRevocationList.Empty, _activeTeam, authz, TimeProvider.System,
            NullLogger.Instance, authorizingPrincipal: _key.PrincipalId.ToBase64Url(), projector: projector);
        AssetRegistryRoutes.Map(
            _app.MapDeviceReachableProductDataGroup(),
            _registry,
            _app.Services.GetRequiredService<IRegistryEntityRepository>(),
            _app.Services.GetRequiredService<ITypedRelationshipStore>(),
            _app.Services.GetRequiredService<IConditionAssessmentStore>(),
            _app.Services.GetRequiredService<IFormSubmissionRecordStore>(),
            _activeTeam,
            TimeProvider.System);

        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _client = new HttpClient { BaseAddress = new Uri(_address) };
        _scratch = Directory.CreateTempSubdirectory("harborline-357-").FullName;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _key?.Dispose();
        if (_scratch is not null && Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    [Fact(DisplayName = "ticket 357: export → verify → install → activate through the CLI, and the pack's form binding resolves")]
    public async Task Bound_type_round_trips_headlessly_and_the_binding_resolves()
    {
        var packFile = await CliExportAsync(PackBody(bindingKey: FormKey, typeContentVersion: "1.1.0"));

        // (1) verify — the map's step 5 verb. A bound type inside the same pack is fully Verified.
        var verify = await CliAsync("pack", "verify", "--file", packFile);
        Assert.Equal(0, verify.ExitCode);
        using (var doc = JsonDocument.Parse(verify.Stdout))
        {
            Assert.Equal("Verified", doc.RootElement.GetProperty("verdict").GetString());
        }

        // (2) install + (3) activate — steps 6 and 7.
        Assert.Equal(0, (await CliAsync("pack", "install", "--file", packFile)).ExitCode);
        Assert.Equal(0, (await CliAsync(
            "pack", "activate", "--pack-key", PackKey, "--version", "1.0.0")).ExitCode);

        // (4) THE ASSERTION (step 9, raw loopback by CLI exemption): the activated type's property form is
        // the INSTALLED form's identity — the pack-local content key resolved to (id, version), the same
        // tuple the form itself was published under.
        var detail = await GetTypeDetailAsync(TypeKey);
        var propertyForm = detail.GetProperty("propertyForm");
        Assert.Equal(FormKey, propertyForm.GetProperty("definition").GetString());
        Assert.Equal(FormVersion, propertyForm.GetProperty("version").GetString());
        Assert.Equal("Pack", detail.GetProperty("provenance").GetString());

        // The registry resolver (the production read path) agrees, and the form it names is published.
        var tenant = NodeTenant.Resolve(_activeTeam);
        var resolved = await _registry.TryResolvePropertyFormAsync(tenant, new EntityTypeId(TypeKey));
        Assert.Equal(
            new FormBindingRef(new FormDefinitionId(FormKey), SemanticVersion.Parse(FormVersion)), resolved);
        var published = await _forms.GetAsync(new DefinitionCoordinates(tenant, FormKey, FormVersion));
        Assert.Equal(FormDefinitionStatus.Published, published.Status);
    }

    [Fact(DisplayName = "ticket 357: a binding naming a form the pack does not contain is refused at verify by name")]
    public async Task Binding_outside_the_pack_is_refused_at_verify()
    {
        var packFile = await CliExportAsync(
            PackBody(bindingKey: "other.pack.form", typeContentVersion: "1.1.0"));

        var verify = await CliAsync("pack", "verify", "--file", packFile);
        Assert.Equal(0, verify.ExitCode);
        using var doc = JsonDocument.Parse(verify.Stdout);
        Assert.Equal("VerificationFailed", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Contains(
            PackVerificationCodes.FormBindingNotInPack,
            doc.RootElement.GetProperty("details").EnumerateArray().Select(e => e.GetString()));

        // Verify-before-effect: install of the same artifact refuses too, so nothing dangles.
        Assert.NotEqual(0, (await CliAsync("pack", "install", "--file", packFile)).ExitCode);
    }

    [Fact(DisplayName = "ticket 357: a pack authored on the PREVIOUS content version (no binding) still verifies and activates")]
    public async Task Previous_content_version_without_the_field_still_round_trips()
    {
        var packFile = await CliExportAsync(PackBody(bindingKey: null, typeContentVersion: "1.0.0"));

        var verify = await CliAsync("pack", "verify", "--file", packFile);
        using (var doc = JsonDocument.Parse(verify.Stdout))
        {
            Assert.Equal("Verified", doc.RootElement.GetProperty("verdict").GetString());
        }

        Assert.Equal(0, (await CliAsync("pack", "install", "--file", packFile)).ExitCode);
        Assert.Equal(0, (await CliAsync(
            "pack", "activate", "--pack-key", PackKey, "--version", "1.0.0")).ExitCode);

        var detail = await GetTypeDetailAsync(TypeKey);
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("propertyForm").ValueKind);
    }

    [Fact(DisplayName = "ticket 357: a pack DECLARING the previous content version while carrying the field is refused by name")]
    public async Task Binding_under_the_previous_declared_version_is_refused()
    {
        var packFile = await CliExportAsync(PackBody(bindingKey: FormKey, typeContentVersion: "1.0.0"));

        var verify = await CliAsync("pack", "verify", "--file", packFile);
        using var doc = JsonDocument.Parse(verify.Stdout);
        Assert.Equal("VerificationFailed", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Contains(
            PackVerificationCodes.FormBindingSchemaUnsupported,
            doc.RootElement.GetProperty("details").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact(DisplayName = "ticket 364: a two-entry inspection map verifies, installs, activates, and resolves each published form")]
    public async Task Inspection_form_bindings_round_trip_headlessly_and_resolve()
    {
        var inspectionBindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["electrical"] = ElectricalFormKey,
            ["mechanical"] = MechanicalFormKey,
        };
        var packFile = await CliExportAsync(PackBody(
            bindingKey: null,
            typeContentVersion: "1.2.0",
            inspectionBindings: inspectionBindings));

        var verify = await CliAsync("pack", "verify", "--file", packFile);
        Assert.Equal(0, verify.ExitCode);
        using (var doc = JsonDocument.Parse(verify.Stdout))
        {
            Assert.Equal("Verified", doc.RootElement.GetProperty("verdict").GetString());
        }

        Assert.Equal(0, (await CliAsync("pack", "install", "--file", packFile)).ExitCode);
        Assert.Equal(0, (await CliAsync(
            "pack", "activate", "--pack-key", PackKey, "--version", "1.0.0")).ExitCode);

        var detail = await GetTypeDetailAsync(TypeKey);
        var inspectionForms = detail.GetProperty("inspectionForms").EnumerateArray()
            .ToDictionary(
                form => form.GetProperty("discipline").GetString()!,
                form => (form.GetProperty("definition").GetString(), form.GetProperty("version").GetString()),
                StringComparer.Ordinal);
        Assert.Equal((ElectricalFormKey, FormVersion), inspectionForms["electrical"]);
        Assert.Equal((MechanicalFormKey, FormVersion), inspectionForms["mechanical"]);

        var tenant = NodeTenant.Resolve(_activeTeam);
        foreach (var formKey in inspectionBindings.Values)
        {
            var published = await _forms.GetAsync(new DefinitionCoordinates(tenant, formKey, FormVersion));
            Assert.Equal(FormDefinitionStatus.Published, published.Status);
        }
    }

    [Fact(DisplayName = "ticket 364: an inspection map naming a form the pack does not contain is refused at verify by name")]
    public async Task Inspection_form_binding_outside_the_pack_is_refused_at_verify()
    {
        var packFile = await CliExportAsync(PackBody(
            bindingKey: null,
            typeContentVersion: "1.2.0",
            inspectionBindings: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["electrical"] = "other.pack.form",
            }));

        var verify = await CliAsync("pack", "verify", "--file", packFile);
        Assert.Equal(0, verify.ExitCode);
        using var doc = JsonDocument.Parse(verify.Stdout);
        Assert.Equal("VerificationFailed", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Contains(
            PackVerificationCodes.InspectionFormBindingNotInPack,
            doc.RootElement.GetProperty("details").EnumerateArray().Select(e => e.GetString()));
        Assert.NotEqual(0, (await CliAsync("pack", "install", "--file", packFile)).ExitCode);
    }

    [Fact(DisplayName = "394: an install refusal names dependency index 1, and the CLI preserves that body verbatim")]
    public async Task Refused_install_carries_dependency_pointer_and_cli_preserves_the_response_body()
    {
        var packFile = await CliExportAsync(PackBody(
            bindingKey: null,
            typeContentVersion: "1.2.0",
            dependencies:
            [
                ("missing.first", "1.0.0"),
                ("missing.second", "1.0.0"),
            ]));
        var bytes = await File.ReadAllBytesAsync(packFile);
        using var response = await _client.PostAsync(PackInstallRoutes.InstallRoute, new ByteArrayContent(bytes));
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        using (var document = JsonDocument.Parse(body))
        {
            Assert.Contains(
                document.RootElement.GetProperty("refusals").EnumerateArray(),
                refusal => refusal.GetProperty("code").GetString() == PackInstallCodes.RefusedUnmetDependency
                    && refusal.GetProperty("pointer").GetString() == "/dependencies/1");
            Assert.Contains(
                document.RootElement.GetProperty("preview").GetProperty("refusals").EnumerateArray(),
                refusal => refusal.GetProperty("pointer").GetString() == "/dependencies/1");
        }

        var cli = await CliAsync("pack", "install", "--file", packFile);
        Assert.NotEqual(0, cli.ExitCode);
        Assert.Equal(body.Trim(), cli.Stderr.Trim());
    }

    // ── the headless driver ────────────────────────────────────────────────────────────────────────────

    private async Task<string> CliExportAsync(object body)
    {
        var requestPath = Path.Combine(_scratch, $"{Guid.NewGuid():N}.export.json");
        var outPath = Path.Combine(_scratch, $"{Guid.NewGuid():N}.pack");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(body));

        var export = await CliAsync("pack", "export", "--request", requestPath, "--out", outPath);
        Assert.Equal(0, export.ExitCode);
        Assert.True(File.Exists(outPath), $"the CLI wrote no pack file: {export.Stderr}");
        return outPath;
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> CliAsync(params string[] command)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var args = new List<string> { "--url", _address, "--token", "operator-secret", "--json" };
        args.AddRange(command);
        var exitCode = await NodeOperatorCommand.RunAsync(args, _client, stdout, stderr);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private async Task<JsonElement> GetTypeDetailAsync(string typeId)
    {
        var resp = await _client.GetAsync($"{AssetTypesRoute}/{typeId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    // ── the hand-authored pack: one Records type + one Form it binds ───────────────────────────────────

    private static object PackBody(
        string? bindingKey,
        string typeContentVersion,
        IReadOnlyDictionary<string, string>? inspectionBindings = null,
        IReadOnlyList<(string Key, string Version)>? dependencies = null)
    {
        var typeContent = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["id"] = TypeKey,
            ["displayName"] = "Note",
            ["traits"] = new[] { "Maintainable" },
            ["conditionScaleMax"] = 5,
        };
        if (bindingKey is not null)
        {
            typeContent["propertyFormBinding"] = bindingKey;
        }
        if (inspectionBindings is { Count: > 0 })
        {
            typeContent["inspectionFormBindings"] = inspectionBindings;
        }

        var contents = new List<object>
        {
            new
            {
                key = TypeKey,
                kind = "AssetTypeDefinition",
                version = typeContentVersion,
                content = typeContent,
            },
            FormContent(FormKey),
        };
        foreach (var formKey in inspectionBindings?.Values.Distinct(StringComparer.Ordinal) ?? [])
        {
            if (!string.Equals(formKey, FormKey, StringComparison.Ordinal)
                && !string.Equals(formKey, "other.pack.form", StringComparison.Ordinal))
            {
                contents.Add(FormContent(formKey));
            }
        }

        return new
        {
            key = PackKey,
            version = "1.0.0",
            name = "Notes Lite",
            description = "One Records type and the Form bound to it (ticket 357).",
            scopeTier = "Horizontal",
            contents,
            dependencies = dependencies is null
                ? Array.Empty<object>()
                : dependencies.Select(dependency => (object)new { key = dependency.Key, version = dependency.Version }).ToArray(),
            capabilityRequirements = Array.Empty<string>(),
        };
    }

    private static object FormContent(string key) => new
    {
        key,
        kind = "FormDefinition",
        version = FormVersion,
        content = new
        {
            overlay = new
            {
                fields = new Dictionary<string, object>
                {
                    ["note"] = new { label = Text("Note"), controlHint = "text", piiSensitivity = "None" },
                },
                sections = new object[]
                {
                    new { id = "main", title = Text("Main"), fields = new[] { "note" } },
                },
                rules = Array.Empty<object>(),
                title = Text("Note capture"),
                description = Text("Capture a note."),
            },
            fieldsMeta = new Dictionary<string, object>
            {
                ["note"] = new { type = "text", required = true, options = (string[]?)null },
            },
        },
    };

    private static object Text(string en) => new
    {
        defaultLocale = "en",
        values = new Dictionary<string, string> { ["en"] = en },
    };

    private sealed class MutableActiveTeamAccessor(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
