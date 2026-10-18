using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Health;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Route-level end-to-end for the Pack Composer B-1a export/verify surface: an own-roster pack
/// composed + signed via <c>POST /packs/export</c> round-trips through <c>POST /packs/verify</c> to
/// <c>Verified</c>; an instance-data pack is refused (422) at export; garbage is
/// <c>VerificationFailed</c> at verify. Mirrors <see cref="PackComposerRoutes.Map"/> exactly — no
/// test/prod wire drift.
/// </summary>
public sealed class PackComposerRouteTests : IAsyncLifetime
{
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-0000000000fc"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private KeyPair _key = null!;
    private CountingSigner _signer = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        // Ticket 294 slice 3b: the desktop actor (no compile-time operator id).
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();

        _key = KeyPair.Generate();
        _signer = new CountingSigner(new Ed25519Signer(_key));
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System);
        var verifier = new PackVerifier(new Ed25519Verifier(), codec);
        var trustStore = new InMemoryPackTrustStore(new[]
        {
            new PackTrustRoot(TrustScope.OwnRoster, _key.PrincipalId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current),
        });
        var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));

        PackComposerRoutes.Map(_app, exporter, verifier, trustStore, _signer, activeTeam,
            TestPackGate.AllowAll(), TimeProvider.System, NullLogger.Instance);

        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _key?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static object ValidExportBody(string contentKey = "acme.intake-form") => new
    {
        key = "acme.inspections",
        version = "1.0.0",
        name = "Acme Inspections",
        description = "Inspection intake pack.",
        scopeTier = "Vertical",
        contents = new[]
        {
            new
            {
                key = contentKey,
                kind = "FormDefinition",
                version = "1.0.0",
                content = new { title = "Intake", assignee = "role:approver" },
            },
        },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = new[] { "forms.dynamic" },
    };

    [Fact(DisplayName = "export → verify round-trips to Verified")]
    public async Task Export_then_verify_is_verified()
    {
        var validationResp = await _client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute + "?validateOnly=true", ValidExportBody());
        Assert.Equal(HttpStatusCode.OK, validationResp.StatusCode);
        Assert.Equal("application/json", validationResp.Content.Headers.ContentType?.MediaType);
        using var validationDoc = JsonDocument.Parse(await validationResp.Content.ReadAsStringAsync());
        Assert.True(validationDoc.RootElement.GetProperty("valid").GetBoolean());
        Assert.Empty(validationDoc.RootElement.GetProperty("codes").EnumerateArray());
        Assert.Equal(0, _signer.Calls);

        var exportResp = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, ValidExportBody());
        Assert.Equal(HttpStatusCode.OK, exportResp.StatusCode);
        var packBytes = await exportResp.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(packBytes);
        Assert.Equal(1, _signer.Calls);

        using var content = new ByteArrayContent(packBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var verifyResp = await _client.PostAsync(PackComposerRoutes.VerifyRoute, content);
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);

        using var doc = JsonDocument.Parse(await verifyResp.Content.ReadAsStringAsync());
        Assert.Equal("Verified", doc.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("acme.inspections", doc.RootElement.GetProperty("manifestKey").GetString());
        Assert.Equal("OwnRoster", doc.RootElement.GetProperty("vouchingScope").GetString());
    }

    [Fact(DisplayName = "export rejects an unparseable DCP regulatoryClass (400, never a silent downgrade)")]
    public async Task Export_unparseable_regulatory_class_is_rejected()
    {
        // Fail-closed honesty (ADR 0145): a typo'd regulated class must NOT silently coerce to General and
        // slip past the export gate's class-clearance check — a supplied-but-unparseable token is a 400.
        var body = new
        {
            key = "acme.inspections",
            version = "1.0.0",
            name = "Acme Inspections",
            description = "Inspection intake pack.",
            scopeTier = "Vertical",
            contents = new[]
            {
                new
                {
                    key = "acme.intake-form",
                    kind = "FormDefinition",
                    version = "1.0.0",
                    content = new { title = "Intake", assignee = "role:approver" },
                },
            },
            dependencies = Array.Empty<object>(),
            capabilityRequirements = new[] { "forms.dynamic" },
            dcp = new { regulatoryClass = "healthcare-typo" },
        };
        var resp = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact(DisplayName = "export refuses an instance-data pack (422)")]
    public async Task Export_instance_data_is_refused()
    {
        var body = new
        {
            key = "leaky.pack",
            version = "1.0.0",
            name = "Leaky",
            description = "d",
            scopeTier = "Vertical",
            contents = new[]
            {
                new
                {
                    key = "leaky.form",
                    kind = "FormDefinition",
                    version = "1.0.0",
                    content = new { title = "x", submittedValues = new { amount = "500" } },
                },
            },
            dependencies = Array.Empty<object>(),
            capabilityRequirements = Array.Empty<string>(),
        };

        var resp = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.Contains("pii.instance_data", text);
        var validationResp = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute + "?validateOnly=true", body);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, validationResp.StatusCode);
        using var validationDoc = JsonDocument.Parse(await validationResp.Content.ReadAsStringAsync());
        Assert.False(validationDoc.RootElement.GetProperty("valid").GetBoolean());
        Assert.Contains("pii.instance_data", validationDoc.RootElement.GetProperty("codes").GetRawText());
    }

    [Theory]
    [InlineData("intake-form")]
    [InlineData(".intake")]
    [InlineData("acme.")]
    [InlineData("acme..intake")]
    [InlineData("/intake")]
    [InlineData("acme/")]
    [InlineData("acme//intake")]
    [InlineData("acme./intake")]
    [InlineData("acme/ /intake")]
    [InlineData(" .intake")]
    [InlineData("acme. \t.intake")]
    public async Task Unqualified_keys_are_refused_before_signing_by_validator_exporter_and_routes(string key)
    {
        var source = new PackContentSource(key, PackContentKind.FormDefinition, "1.0.0",
            new JsonObject { ["title"] = "Namespace proof" });
        var item = new PackContentCanonicalizer().Canonicalize(source);
        var manifest = new PackManifest("unrelated.publisher", "1.0.0", "Namespace proof", "Definition only",
            PackScopeTier.Horizontal, [new(key, PackContentKind.FormDefinition, "1.0.0", item.ContentAddress)], [], []);
        var validation = new PackValidator(new PackContentPiiScanner()).Validate(manifest, [item]);
        var finding = Assert.Single(validation.Errors);
        Assert.Equal("pack.validation.content.key.unqualified", finding.Code);
        Assert.Equal(key, finding.Target);

        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), new PackFileCodec(), timeProvider: TimeProvider.System);
        var result = await exporter.ExportAsync(new PackExportRequest(
            "unrelated.publisher", "1.0.0", "Namespace proof", "Definition only", PackScopeTier.Horizontal,
            [source], [], [], 1, Dcp: DomainComplianceProfile.General(_key.PrincipalId.ToBase64Url())), _signer);
        Assert.False(result.Succeeded);
        Assert.Null(result.File);
        Assert.Null(result.FileBytes);
        Assert.Equal("pack.validation.content.key.unqualified", Assert.Single(result.Validation.Errors).Code);
        Assert.Equal(0, _signer.Calls);

        foreach (var suffix in new[] { "?validateOnly=true", "" })
        {
            var response = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute + suffix, ValidExportBody(key));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var code = Assert.Single(document.RootElement.GetProperty("codes").EnumerateArray());
            Assert.Equal("pack.validation.content.key.unqualified", code.GetProperty("code").GetString());
            Assert.Equal(key, code.GetProperty("target").GetString());
            Assert.False(document.RootElement.TryGetProperty("fileBytes", out _));
        }
        Assert.Equal(0, _signer.Calls);
    }

    [Theory]
    [InlineData("access.grant-a-role")]
    [InlineData("shared.equipment")]
    [InlineData("harborline.general.equipment")]
    [InlineData("records/eam.meter")]
    [InlineData("forms/invoice")]
    [InlineData("defaults/package")]
    [InlineData("authorization/tenant-read")]
    public async Task Qualified_keys_export_verbatim_under_a_different_pack_namespace(string key)
    {
        var validation = await _client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute + "?validateOnly=true", ValidExportBody(key));
        Assert.Equal(HttpStatusCode.OK, validation.StatusCode);
        Assert.Equal(0, _signer.Calls);
        var response = await _client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, ValidExportBody(key));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        var file = new PackFileCodec().TryDecode(await response.Content.ReadAsByteArrayAsync());
        Assert.NotNull(file);
        Assert.Equal("acme.inspections", file.Envelope!.Payload.Manifest.Key);
        Assert.Equal(key, Assert.Single(file.Envelope.Payload.Manifest.Contents).Key);
        Assert.Equal(key, Assert.Single(file.Contents).Key);
        Assert.Equal(1, _signer.Calls);
    }

    [Fact]
    public void Blank_keys_keep_the_existing_missing_finding()
    {
        var item = new PackContentCanonicalizer().Canonicalize(new PackContentSource(" ",
            PackContentKind.FormDefinition, "1.0.0", new JsonObject { ["title"] = "Blank key" }));
        var manifest = new PackManifest("acme.blank", "1.0.0", "Blank key", "Definition only",
            PackScopeTier.Horizontal, [new(" ", PackContentKind.FormDefinition, "1.0.0", item.ContentAddress)], [], []);
        var result = new PackValidator(new PackContentPiiScanner()).Validate(manifest, [item]);
        Assert.Contains(result.Errors, error => error.Code == "pack.validation.content.key.missing");
        Assert.DoesNotContain(result.Errors, error => error.Code == "pack.validation.content.key.unqualified");
    }

    [Fact]
    public void Namespace_findings_do_not_hide_other_content_diagnostics()
    {
        var canonicalizer = new PackContentCanonicalizer();
        var item = canonicalizer.Canonicalize(new PackContentSource("bare", PackContentKind.FormDefinition,
            "1.0.0", new JsonObject { ["submittedValues"] = new JsonObject { ["amount"] = 5 } }));
        var different = canonicalizer.Canonicalize(new PackContentSource("acme.other", PackContentKind.FormDefinition,
            "1.0.0", new JsonObject { ["title"] = "Different addressed bytes" }));
        var manifest = new PackManifest("acme.diagnostics", "1.0.0", "Diagnostics", "Definition only",
            PackScopeTier.Horizontal,
            [
                new("bare", PackContentKind.FormDefinition, "*", different.ContentAddress),
                new("bare", PackContentKind.FormDefinition, "1.0.0", item.ContentAddress),
                new("missing", PackContentKind.FormDefinition, "1.0.0", different.ContentAddress),
            ], [], []);
        var result = new PackValidator(new PackContentPiiScanner()).Validate(manifest, [item]);
        Assert.Equal(3, result.Errors.Count(error => error.Code == "pack.validation.content.key.unqualified"));
        Assert.Contains(result.Errors, error => error.Code == "pack.validation.content.key.duplicate" && error.Target == "bare");
        Assert.Contains(result.Errors, error => error.Code == "pack.validation.content.version.unpinned" && error.Target == "bare");
        Assert.Contains(result.Errors, error => error.Code == "pack.validation.content.item.missing" && error.Target == "missing");
        Assert.Contains(result.Errors, error => error.Code == "pack.validation.content.address.mismatch" && error.Target == "bare");
        Assert.Contains(result.Errors, error => error.Code == "pack.validation.content.pii.instance_data" && error.Target == "bare:$.submittedValues");
    }

    [Fact(DisplayName = "verify of garbage is VerificationFailed")]
    public async Task Verify_garbage_is_failed()
    {
        using var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes("not a pack"));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var resp = await _client.PostAsync(PackComposerRoutes.VerifyRoute, content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("VerificationFailed", doc.RootElement.GetProperty("verdict").GetString());
    }

    private sealed class CountingSigner(IOperationSigner inner) : IOperationSigner
    {
        public PrincipalId IssuerId => inner.IssuerId;
        public int Calls { get; private set; }

        public ValueTask<SignedOperation<T>> SignAsync<T>(T payload, DateTimeOffset issuedAt, Guid nonce, CancellationToken ct = default)
        {
            Calls++;
            return inner.SignAsync(payload, issuedAt, nonce, ct);
        }
    }

    private static TeamContext TeamContextFor(TeamId teamId, string name)
        => new(teamId, name, new ServiceCollection().BuildServiceProvider(), TimeProvider.System);

    private sealed class MutableActiveTeamAccessor : IActiveTeamAccessor
    {
        public MutableActiveTeamAccessor(TeamContext? active) => Active = active;
        public TeamContext? Active { get; set; }
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void _keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
