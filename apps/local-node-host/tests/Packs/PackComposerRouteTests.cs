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
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Merge;
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

    [Fact]
    public async Task Public_rename_map_is_signed_and_reattaches_the_literal_tenant_override()
    {
        await using var fixture = await RenameRouteFixture.StartAsync(TestPackGate.AllowAll());
        using var response = await fixture.Client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, RenameExportBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
        Assert.Equal(1, fixture.Signer.Calls);
        Assert.Empty(fixture.Exporter.Validated);
        AssertRenameMap(Assert.Single(fixture.Exporter.Exported).RenamedFrom);

        var verified = fixture.Verifier.Verify(bytes, fixture.Trust);
        Assert.Equal("Verified", verified.Verdict.ToString());
        Assert.NotNull(verified.Manifest);
        Assert.NotNull(verified.Contents);
        Assert.Equal("acme.inspections", verified.Manifest.Key);
        Assert.Equal("1.1.0", verified.Manifest.Version);
        AssertRenameMap(verified.Manifest.RenamedFrom);

        using var upload = new ByteArrayContent(bytes);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var verifyResponse = await fixture.Client.PostAsync(PackComposerRoutes.VerifyRoute, upload);
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        using var verdict = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync());
        Assert.Equal("Verified", verdict.RootElement.GetProperty("verdict").GetString());
        Assert.Equal("acme.inspections", verdict.RootElement.GetProperty("manifestKey").GetString());
        Assert.Equal("OwnRoster", verdict.RootElement.GetProperty("vouchingScope").GetString());
        Assert.Equal(1, verdict.RootElement.GetProperty("epoch").GetInt64());

        // Prior seed data is local planner input, never a new export of a bare-key pack.
        var oldItem = new PackContentCanonicalizer().Canonicalize(new PackContentSource(
            "intake-form", PackContentKind.FormDefinition, "1.0.0",
            new JsonObject { ["title"] = "Intake", ["assignee"] = "role:approver" }));
        PackSeedItem[] oldSeeds =
        [
            new("intake-form", PackContentKind.FormDefinition, "1.0.0",
                System.Text.Encoding.UTF8.GetString(oldItem.CanonicalBytes.Span), oldItem.ContentAddress),
        ];
        PackTenantOverride[] overrides =
        [
            new("intake-form", JsonNode.Parse("""{"title":"Tenant title"}""")!),
        ];

        var plan = PackReattachPlanner.Plan(oldSeeds, verified.Contents, overrides, verified.Manifest.RenamedFrom);

        Assert.Null(plan.RefusalCode);
        Assert.Null(plan.RefusalMember);
        Assert.Empty(plan.Conflicts);
        var retained = Assert.Single(plan.Reattached);
        Assert.Equal("acme.intake-form", retained.ContentKey);
        Assert.Equal("""{"title":"Tenant title"}""", retained.OverlayPatch.ToJsonString());
        Assert.DoesNotContain(plan.Reattached, item => item.ContentKey == "intake-form");

        IReadOnlyList<PackContentRename>?[] missingOrReversed =
        [
            null,
            [new("follow-up", "acme.follow-up"), new("intake-form", "acme.intake-form")],
        ];
        foreach (var map in missingOrReversed)
        {
            var control = PackReattachPlanner.Plan(oldSeeds, verified.Contents, overrides, map);
            Assert.Empty(control.Reattached);
            var orphan = Assert.Single(control.Conflicts);
            Assert.Equal("intake-form", orphan.ContentKey);
            Assert.Equal("OrphanedByRemoval", orphan.Kind.ToString());
            Assert.Equal("", orphan.Path);
            Assert.Equal("""{"title":"Tenant title"}""", orphan.OverlayValueJson);
        }
    }

    [Fact]
    public async Task Validate_only_forwards_the_same_rename_map_to_real_validation_without_signing()
    {
        await using var fixture = await RenameRouteFixture.StartAsync(TestPackGate.AllowAll());
        using var response = await fixture.Client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute + "?validateOnly=true", RenameExportBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(result.RootElement.GetProperty("valid").GetBoolean());
        Assert.Empty(result.RootElement.GetProperty("codes").EnumerateArray());
        AssertRenameMap(Assert.Single(fixture.Exporter.Validated).RenamedFrom);
        Assert.Empty(fixture.Exporter.Exported);
        Assert.Equal(0, fixture.Signer.Calls);

        var invalid = RenameExportBody();
        invalid["contents"]![0]!["content"]!["submittedValues"] = new JsonObject { ["amount"] = 5 };
        using var refused = await fixture.Client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute + "?validateOnly=true", invalid);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        using var refusal = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.False(refusal.RootElement.GetProperty("valid").GetBoolean());
        Assert.Contains(refusal.RootElement.GetProperty("codes").EnumerateArray(),
            finding => finding.GetProperty("code").GetString() == "pack.validation.content.pii.instance_data"
                && finding.GetProperty("target").GetString() == "acme.intake-form:$.submittedValues");
        Assert.Equal(2, fixture.Exporter.Validated.Count);
        AssertRenameMap(fixture.Exporter.Validated[1].RenamedFrom);
        Assert.Empty(fixture.Exporter.Exported);
        Assert.Equal(0, fixture.Signer.Calls);
    }

    [Theory]
    [InlineData("omitted", true)]
    [InlineData("null", true)]
    [InlineData("empty", true)]
    [InlineData("omitted", false)]
    [InlineData("null", false)]
    [InlineData("empty", false)]
    public async Task Optional_rename_map_preserves_null_and_empty_in_both_route_branches(
        string presence, bool validateOnly)
    {
        await using var fixture = await RenameRouteFixture.StartAsync(TestPackGate.AllowAll());
        var body = RenameExportBody();
        if (presence == "omitted") body.Remove("renamedFrom");
        else body["renamedFrom"] = presence == "null" ? null : new JsonArray();
        using var response = await fixture.Client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute + (validateOnly ? "?validateOnly=true" : ""), body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var observed = validateOnly
            ? Assert.Single(fixture.Exporter.Validated)
            : Assert.Single(fixture.Exporter.Exported);
        if (presence == "empty")
        {
            Assert.NotNull(observed.RenamedFrom);
            Assert.Empty(observed.RenamedFrom);
        }
        else Assert.Null(observed.RenamedFrom);

        if (validateOnly)
        {
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(result.RootElement.GetProperty("valid").GetBoolean());
            Assert.Empty(result.RootElement.GetProperty("codes").EnumerateArray());
            Assert.Empty(fixture.Exporter.Exported);
            Assert.Equal(0, fixture.Signer.Calls);
        }
        else
        {
            Assert.Empty(fixture.Exporter.Validated);
            Assert.Equal(1, fixture.Signer.Calls);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            var verified = fixture.Verifier.Verify(bytes, fixture.Trust);
            Assert.Equal("Verified", verified.Verdict.ToString());
            Assert.NotNull(verified.Manifest);
            using var canonical = JsonDocument.Parse(CanonicalJson.Serialize(verified.Manifest));
            var signedMember = canonical.RootElement.GetProperty("RenamedFrom");
            if (presence == "empty")
            {
                Assert.NotNull(verified.Manifest.RenamedFrom);
                Assert.Empty(verified.Manifest.RenamedFrom);
                Assert.Equal(JsonValueKind.Array, signedMember.ValueKind);
                Assert.Empty(signedMember.EnumerateArray());
            }
            else
            {
                Assert.Null(verified.Manifest.RenamedFrom);
                Assert.Equal(JsonValueKind.Null, signedMember.ValueKind);
            }
        }
    }

    [Theory]
    [InlineData("deny", true)]
    [InlineData("deny", false)]
    [InlineData("other-pack", true)]
    [InlineData("other-pack", false)]
    public async Task Rename_metadata_does_not_bypass_author_or_pack_scope(string grant, bool validateOnly)
    {
        var gate = grant == "deny" ? TestPackGate.Denying() : TestPackGate.ScopedTo("other.publisher");
        await using var fixture = await RenameRouteFixture.StartAsync(gate);
        using var response = await fixture.Client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute + (validateOnly ? "?validateOnly=true" : ""), RenameExportBody());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var refusal = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("authorization.permission_required", refusal.RootElement.GetProperty("code").GetString());
        Assert.Equal("packages:author", refusal.RootElement.GetProperty("permission").GetString());
        Assert.Empty(fixture.Exporter.Validated);
        Assert.Empty(fixture.Exporter.Exported);
        Assert.Equal(0, fixture.Signer.Calls);
    }

    [Fact]
    public async Task A_grant_for_the_actual_pack_still_allows_export_with_rename_metadata()
    {
        await using var fixture = await RenameRouteFixture.StartAsync(TestPackGate.ScopedTo("acme.inspections"));
        using var response = await fixture.Client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, RenameExportBody());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var verified = fixture.Verifier.Verify(await response.Content.ReadAsByteArrayAsync(), fixture.Trust);
        Assert.Equal("Verified", verified.Verdict.ToString());
        Assert.NotNull(verified.Manifest);
        AssertRenameMap(verified.Manifest.RenamedFrom);
        Assert.Equal(1, fixture.Signer.Calls);
    }

    [Fact]
    public async Task Tampering_with_only_the_signed_rename_map_fails_real_verification()
    {
        await using var fixture = await RenameRouteFixture.StartAsync(TestPackGate.AllowAll());
        using var response = await fixture.Client.PostAsJsonAsync(PackComposerRoutes.ExportRoute, RenameExportBody());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("Verified", fixture.Verifier.Verify(bytes, fixture.Trust).Verdict.ToString());
        var file = Assert.IsType<PackFile>(fixture.Codec.TryDecode(bytes));
        var envelope = file.Envelope!;
        var tampered = file with
        {
            Envelope = envelope with
            {
                Payload = envelope.Payload with
                {
                    Manifest = envelope.Payload.Manifest with
                    {
                        RenamedFrom =
                        [
                            new("acme.intake-form", "intake-form"),
                            new("acme.follow-up", "different-follow-up"),
                        ],
                    },
                },
            },
        };
        var alteredBytes = fixture.Codec.Encode(tampered);
        Assert.NotNull(fixture.Codec.TryDecode(alteredBytes));
        var rejected = fixture.Verifier.Verify(alteredBytes, fixture.Trust);
        Assert.Equal("VerificationFailed", rejected.Verdict.ToString());
        Assert.Contains("pack.verify.signature_invalid", rejected.Details);
        Assert.Null(rejected.Manifest);
        Assert.Null(rejected.Contents);

        using var upload = new ByteArrayContent(alteredBytes);
        upload.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var verifyResponse = await fixture.Client.PostAsync(PackComposerRoutes.VerifyRoute, upload);
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        using var verdict = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync());
        Assert.Equal("VerificationFailed", verdict.RootElement.GetProperty("verdict").GetString());
        Assert.Contains(verdict.RootElement.GetProperty("details").EnumerateArray(),
            detail => detail.GetString() == "pack.verify.signature_invalid");
        Assert.Equal(JsonValueKind.Null, verdict.RootElement.GetProperty("manifestKey").ValueKind);
        Assert.Equal(1, fixture.Signer.Calls);
    }

    [Theory]
    [InlineData("null-entry", false)]
    [InlineData("null-entry", true)]
    [InlineData("null-later-entry", false)]
    [InlineData("null-later-entry", true)]
    [InlineData("null-new-key", false)]
    [InlineData("null-new-key", true)]
    [InlineData("blank-new-key", false)]
    [InlineData("blank-new-key", true)]
    [InlineData("unqualified-new-key", false)]
    [InlineData("unqualified-new-key", true)]
    [InlineData("empty-new-segment", false)]
    [InlineData("empty-new-segment", true)]
    [InlineData("missing-new-content", false)]
    [InlineData("missing-new-content", true)]
    [InlineData("case-only-missing-new-content", false)]
    [InlineData("case-only-missing-new-content", true)]
    [InlineData("null-old-key", false)]
    [InlineData("null-old-key", true)]
    [InlineData("blank-old-key", false)]
    [InlineData("blank-old-key", true)]
    [InlineData("duplicate-new-key", false)]
    [InlineData("duplicate-new-key", true)]
    [InlineData("duplicate-old-key", false)]
    [InlineData("duplicate-old-key", true)]
    [InlineData("duplicate-both-keys", false)]
    [InlineData("duplicate-both-keys", true)]
    public async Task Malformed_rename_entries_are_refused_before_signing_in_both_branches(
        string scenario, bool validateOnly)
    {
        await using var fixture = await RenameRouteFixture.StartAsync(TestPackGate.AllowAll());
        var body = MalformedRenameExportBody(scenario);
        var expected = ExpectedRenameFindings(scenario);

        using var response = await fixture.Client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute + (validateOnly ? "?validateOnly=true" : ""), body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.NotEqual("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        using var refusal = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (validateOnly) Assert.False(refusal.RootElement.GetProperty("valid").GetBoolean());
        Assert.Equal(expected, refusal.RootElement.GetProperty("codes").EnumerateArray()
            .Select(finding => (finding.GetProperty("code").GetString()!, finding.GetProperty("target").GetString()!))
            .ToArray());
        Assert.Equal(0, fixture.Signer.Calls);

        PackExportRequest observed;
        if (validateOnly)
        {
            observed = Assert.Single(fixture.Exporter.Validated);
            Assert.Empty(fixture.Exporter.Exported);
        }
        else
        {
            observed = Assert.Single(fixture.Exporter.Exported);
            Assert.Empty(fixture.Exporter.Validated);
        }

        // The same actually bound request also exercises the real exporter outcome contract.
        var direct = await fixture.Exporter.ExportAsync(observed, fixture.Signer);
        Assert.False(direct.Succeeded);
        Assert.Null(direct.File);
        Assert.Null(direct.FileBytes);
        Assert.Equal(expected, direct.Validation.Errors.Select(error => (error.Code, error.Target!)).ToArray());
        Assert.Equal(0, fixture.Signer.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_findings_accumulate_with_actual_content_pii_findings(bool validateOnly)
    {
        await using var fixture = await RenameRouteFixture.StartAsync(TestPackGate.AllowAll());
        var body = MalformedRenameExportBody("null-entry");
        body["contents"]![0]!["content"]!["submittedValues"] = new JsonObject { ["amount"] = 5 };

        using var response = await fixture.Client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute + (validateOnly ? "?validateOnly=true" : ""), body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var refusal = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var findings = refusal.RootElement.GetProperty("codes").EnumerateArray().ToArray();
        Assert.Contains(findings, finding =>
            finding.GetProperty("code").GetString() == "pack.validation.content.pii.instance_data"
            && finding.GetProperty("target").GetString() == "acme.intake-form:$.submittedValues");
        Assert.Contains(findings, finding =>
            finding.GetProperty("code").GetString() == "pack.validation.content.key.missing"
            && finding.GetProperty("target").GetString() == "renamedFrom/0");
        Assert.True(Array.FindIndex(findings, finding =>
                finding.GetProperty("code").GetString() == "pack.validation.content.pii.instance_data")
            < Array.FindIndex(findings, finding =>
                finding.GetProperty("code").GetString() == "pack.validation.content.key.missing"));
        Assert.Equal(0, fixture.Signer.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nonblank_legacy_old_keys_preserve_surrounding_spaces_without_qualification(bool validateOnly)
    {
        await using var fixture = await RenameRouteFixture.StartAsync(TestPackGate.AllowAll());
        var body = RenameExportBody();
        body["renamedFrom"]![0]!["oldKey"] = " intake-form ";

        using var response = await fixture.Client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute + (validateOnly ? "?validateOnly=true" : ""), body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        if (validateOnly)
        {
            using var validation = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(validation.RootElement.GetProperty("valid").GetBoolean());
            Assert.Empty(validation.RootElement.GetProperty("codes").EnumerateArray());
            Assert.Equal(" intake-form ", Assert.Single(fixture.Exporter.Validated).RenamedFrom![0].OldKey);
            Assert.Empty(fixture.Exporter.Exported);
            Assert.Equal(0, fixture.Signer.Calls);
        }
        else
        {
            var verified = fixture.Verifier.Verify(await response.Content.ReadAsByteArrayAsync(), fixture.Trust);
            Assert.Equal("Verified", verified.Verdict.ToString());
            Assert.NotNull(verified.Manifest);
            Assert.NotNull(verified.Manifest.RenamedFrom);
            Assert.Collection(verified.Manifest.RenamedFrom,
                first =>
                {
                    Assert.Equal("acme.intake-form", first.NewKey);
                    Assert.Equal(" intake-form ", first.OldKey);
                },
                second =>
                {
                    Assert.Equal("acme.follow-up", second.NewKey);
                    Assert.Equal("follow-up", second.OldKey);
                });
            Assert.Equal(1, fixture.Signer.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Distinct_key_case_is_preserved_by_ordinal_rename_validation(bool validateOnly)
    {
        await using var fixture = await RenameRouteFixture.StartAsync(TestPackGate.AllowAll());
        var body = RenameExportBody();
        body["contents"]![1]!["key"] = "acme.Intake-form";
        body["renamedFrom"]![1]!["newKey"] = "acme.Intake-form";
        body["renamedFrom"]![0]!["oldKey"] = "Form";
        body["renamedFrom"]![1]!["oldKey"] = "form";

        using var response = await fixture.Client.PostAsJsonAsync(
            PackComposerRoutes.ExportRoute + (validateOnly ? "?validateOnly=true" : ""), body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        IReadOnlyList<PackContentRename>? map;
        if (validateOnly)
        {
            using var validation = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(validation.RootElement.GetProperty("valid").GetBoolean());
            Assert.Empty(validation.RootElement.GetProperty("codes").EnumerateArray());
            map = Assert.Single(fixture.Exporter.Validated).RenamedFrom;
            Assert.Equal(0, fixture.Signer.Calls);
        }
        else
        {
            var verified = fixture.Verifier.Verify(await response.Content.ReadAsByteArrayAsync(), fixture.Trust);
            Assert.Equal("Verified", verified.Verdict.ToString());
            Assert.NotNull(verified.Manifest);
            map = verified.Manifest.RenamedFrom;
            Assert.Equal(1, fixture.Signer.Calls);
        }

        Assert.NotNull(map);
        Assert.Collection(map,
            first =>
            {
                Assert.Equal("acme.intake-form", first.NewKey);
                Assert.Equal("Form", first.OldKey);
            },
            second =>
            {
                Assert.Equal("acme.Intake-form", second.NewKey);
                Assert.Equal("form", second.OldKey);
            });
    }

    private static JsonObject MalformedRenameExportBody(string scenario)
    {
        var body = RenameExportBody();
        var map = body["renamedFrom"]!.AsArray();
        switch (scenario)
        {
            case "null-entry":
                map.Clear();
                map.Add((JsonNode?)null);
                break;
            case "null-later-entry":
                map[1] = null;
                break;
            case "null-new-key":
                map[0]!["newKey"] = null;
                break;
            case "blank-new-key":
                map[0]!["newKey"] = " ";
                break;
            case "unqualified-new-key":
                map[0]!["newKey"] = "intake-form";
                break;
            case "empty-new-segment":
                map[0]!["newKey"] = "acme..intake-form";
                break;
            case "missing-new-content":
                map[0]!["newKey"] = "acme.not-exported";
                break;
            case "case-only-missing-new-content":
                map[0]!["newKey"] = "acme.Intake-form";
                break;
            case "null-old-key":
                map[0]!["oldKey"] = null;
                break;
            case "blank-old-key":
                map[0]!["oldKey"] = " ";
                break;
            case "duplicate-new-key":
                map[1]!["newKey"] = "acme.intake-form";
                break;
            case "duplicate-old-key":
                map[1]!["oldKey"] = "intake-form";
                break;
            case "duplicate-both-keys":
                map[1]!["newKey"] = "acme.intake-form";
                map[1]!["oldKey"] = "intake-form";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        return body;
    }

    private static (string Code, string Target)[] ExpectedRenameFindings(string scenario) => scenario switch
    {
        "null-entry" or "null-new-key" or "blank-new-key" or "null-old-key" or "blank-old-key" =>
            [("pack.validation.content.key.missing", "renamedFrom/0")],
        "null-later-entry" => [("pack.validation.content.key.missing", "renamedFrom/1")],
        "unqualified-new-key" or "empty-new-segment" =>
        [
            ("pack.validation.content.key.unqualified", "renamedFrom/0"),
            ("pack.validation.content.item.missing", "renamedFrom/0"),
        ],
        "missing-new-content" or "case-only-missing-new-content" =>
            [("pack.validation.content.item.missing", "renamedFrom/0")],
        "duplicate-new-key" or "duplicate-old-key" or "duplicate-both-keys" =>
            [("pack.validation.content.key.duplicate", "renamedFrom/1")],
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    private static JsonObject RenameExportBody() => JsonSerializer.SerializeToNode(new
    {
        key = "acme.inspections",
        version = "1.1.0",
        name = "Acme Inspections",
        description = "Content key migration.",
        scopeTier = "Vertical",
        contents = new[]
        {
            new
            {
                key = "acme.intake-form",
                kind = "FormDefinition",
                version = "1.1.0",
                content = new { title = "Intake", assignee = "role:approver" },
            },
            new
            {
                key = "acme.follow-up",
                kind = "FormDefinition",
                version = "1.1.0",
                content = new { title = "Follow-up", assignee = "role:approver" },
            },
        },
        dependencies = Array.Empty<object>(),
        capabilityRequirements = new[] { "forms.dynamic" },
        renamedFrom = new[]
        {
            new { newKey = "acme.intake-form", oldKey = "intake-form" },
            new { newKey = "acme.follow-up", oldKey = "follow-up" },
        },
    })!.AsObject();

    private static void AssertRenameMap(IReadOnlyList<PackContentRename>? map)
    {
        Assert.NotNull(map);
        Assert.Collection(map,
            first =>
            {
                Assert.Equal("acme.intake-form", first.NewKey);
                Assert.Equal("intake-form", first.OldKey);
            },
            second =>
            {
                Assert.Equal("acme.follow-up", second.NewKey);
                Assert.Equal("follow-up", second.OldKey);
            });
    }

    private sealed class ObservingRenameExporter(IPackExporter inner) : IPackExporter
    {
        public List<PackExportRequest> Validated { get; } = [];
        public List<PackExportRequest> Exported { get; } = [];

        public ValueTask<PackValidationResult> ValidateAsync(PackExportRequest request, CancellationToken ct = default)
        {
            Validated.Add(request);
            return inner.ValidateAsync(request, ct);
        }

        public ValueTask<PackExportOutcome> ExportAsync(
            PackExportRequest request, IOperationSigner signer, CancellationToken ct = default)
        {
            Exported.Add(request);
            return inner.ExportAsync(request, signer, ct);
        }
    }

    private sealed class RenameRouteFixture(
        WebApplication application, HttpClient httpClient, KeyPair signingKey, CountingSigner countingSigner,
        ObservingRenameExporter observingExporter, PackFileCodec fileCodec, PackVerifier packVerifier, IPackTrustStore trustStore)
        : IAsyncDisposable
    {
        public HttpClient Client { get; } = httpClient;
        public CountingSigner Signer { get; } = countingSigner;
        public ObservingRenameExporter Exporter { get; } = observingExporter;
        public PackFileCodec Codec { get; } = fileCodec;
        public PackVerifier Verifier { get; } = packVerifier;
        public IPackTrustStore Trust { get; } = trustStore;

        public static async Task<RenameRouteFixture> StartAsync(Harborline.Api.Foundation.Authorization.AuthorizationGate gate)
        {
            var builder = WebApplication.CreateBuilder();
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var key = KeyPair.Generate();
            var signer = new CountingSigner(new Ed25519Signer(key));
            var codec = new PackFileCodec();
            var exporter = new ObservingRenameExporter(new PackExporter(
                new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
                new PackValidator(new PackContentPiiScanner()),
                new DcpValidator(DcpCounselRegister.FromEmbeddedResource()), codec, timeProvider: TimeProvider.System));
            var verifier = new PackVerifier(new Ed25519Verifier(), codec);
            var trust = new InMemoryPackTrustStore(
            [
                new(TrustScope.OwnRoster, key.PrincipalId, 1, TrustRootStatus.Current),
            ]);
            var activeTeam = new MutableActiveTeamAccessor(TeamContextFor(TeamA, "Team A"));
            app.Use(async (http, next) =>
            {
                http.Features.Set(DesktopPlaneRequestFeature.Instance);
                await next(http);
            });
            PackComposerRoutes.Map(app, exporter, verifier, trust, signer, activeTeam,
                gate, TimeProvider.System, NullLogger.Instance);
            await app.StartAsync();
            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
            return new RenameRouteFixture(app, client, key, signer, exporter, codec, verifier, trust);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.StopAsync();
            await application.DisposeAsync();
            signingKey.Dispose();
        }
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
