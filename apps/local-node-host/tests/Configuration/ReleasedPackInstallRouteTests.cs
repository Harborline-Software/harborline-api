using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// T-667: a Released package installs. Release (T-461) produced a provider-neutral document and
/// activation (T-644, T-460) consumed installed Active packs, and nothing joined them, so
/// <c>POST /configuration/prepare</c> could not resolve a candidate over a Released package. These run
/// the whole chain over the REAL exporter, the REAL install engine and the real SQLCipher packs
/// database: propose, save, check, release, install, prepare, activate.
/// </summary>
public sealed class ReleasedPackInstallRouteTests : IAsyncLifetime
{
    private const string RecordsEdit = """{"recordType":"invoice","fields":[{"name":"purchaseOrderNumber","kind":"identifier"}]}""";
    private const string FormsEdit = """{"formId":"invoice","sections":[{"id":"header","fields":["purchaseOrderNumber"]}]}""";
    private const string Rationale = "Capture the purchase order number on invoices.";
    private const string PackageKey = "acme.invoice-purchase-order";
    private const string Revision = "1.1.0";
    // The author states the kind. These are the transport's own names, and nothing in the api maps a
    // definition key to one of them — that is the invariant Only_the_producer_states_the_content_kind holds.
    private const string RecordsKind = "AssetTypeDefinition";
    private const string FormsKind = "FormDefinition";
    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000667"));
    private static readonly DateTimeOffset Frozen = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private PacksTestStore _db = null!;
    private DurablePackInstallStore _store = null!;
    private ConfigurationProposalStore _proposals = null!;
    private ConfigurationActivationTarget _activation = null!;
    private NodePrincipalSigner _node = null!;
    private TenantId _tenant;

    public async Task InitializeAsync()
    {
        _db = await PacksTestStore.CreateAsync(keySalt: 67);
        _store = new DurablePackInstallStore(_db.Factory);
        var gate = TestPackGate.AllowAll();
        _activation = new ConfigurationActivationTarget(_db.Factory, _store, gate, new InMemoryPackInstallAudit());

        var seed = new byte[32];
        for (var index = 0; index < seed.Length; index++) seed[index] = (byte)(index + 67);
        _node = new NodePrincipalSigner(seed);
        _proposals = new ConfigurationProposalStore(_db.Factory, _activation, _node.Signer);

        var codec = new PackFileCodec();
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, TimeProvider.System);
        var installer = new PackInstaller(new Harborline.Api.Foundation.Packs.Verify.PackVerifier(new Ed25519Verifier(), codec),
            _store, new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        // The same trust surface the composition root builds: a Released package is verified against the
        // node's own roster root, not against a root this path invented for itself.
        var trustStore = HostedPackInstallApiEndpoint.BuildTrustStore(_node, NullLogger.Instance);
        var releases = new ReleasedPackInstaller(_proposals, exporter, installer, _node, new Ed25519Verifier(),
            trustStore, PackRevocationList.Empty, _store, gate);

        var activeTeam = new ActiveTeam(new TeamContext(TeamA, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        _tenant = NodeTenant.Resolve(activeTeam);
        PlatformPackTestPreload.Activate(_store, _tenant);

        var builder = WebApplication.CreateBuilder();
        // Ticket 294 slice 3b: the desktop actor (no compile-time operator id).
        Harborline.Api.LocalNodeHost.Tests.Authorization.TestDesktopOperator.AddTestDesktopOperator(builder.Services);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Use(async (http, next) =>
        {
            if (http.Request.Headers.ContainsKey("X-Test-Desktop")) http.Features.Set(DesktopPlaneRequestFeature.Instance);
            await next(http);
        });
        var clock = new FrozenClock(Frozen);
        ConfigurationProposalRoutes.Map(_app, _proposals, activeTeam, gate, clock, NullLogger.Instance, releases);
        ConfigurationActivationRoutes.Map(_app, _activation, activeTeam, gate, clock, NullLogger.Instance);
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
        _client.DefaultRequestHeaders.Add("X-Test-Desktop", "1");

        Seed("acme.finance", "1.0.0", ["records/invoice", "forms/invoice"]);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _node?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _db.DisposeAsync();
    }

    /// <summary>
    /// ck-2 S8, owner ruling 2026-09-29 on Q1 (ADR 0028, DES-0014 K8). This was T-667's acceptance 1,
    /// which installed a release under a new pack key that re-stated acme.finance's definitions and won
    /// them through the caller's ownership choice. That takeover is now refused through the same route,
    /// with the same ownership choice in the body, and nothing is installed or prepared over.
    /// </summary>
    [Fact]
    public async Task Released_package_cannot_take_over_another_packages_definition()
    {
        var baseline = await PinEffectiveAsync();
        var releasedDigest = await ReleaseAsync();

        using var refused = await _client.PostAsJsonAsync(InstallRoute(releasedDigest), OwnedByRelease());
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, body.ToString());
        Assert.Equal("refused", body.GetProperty("status").GetString());
        var refusals = body.GetProperty("refusals").EnumerateArray().ToArray();
        Assert.Equal(new[] { "forms/invoice", "records/invoice" },
            refusals.Select(item => item.GetProperty("target").GetString()).Order(StringComparer.Ordinal).ToArray());
        Assert.All(refusals, item =>
        {
            Assert.Equal(ReleasedPackInstaller.ForeignDefinitionCode, item.GetProperty("code").GetString());
            Assert.Contains("'acme.finance'", item.GetProperty("message").GetString(), StringComparison.Ordinal);
        });

        // Nothing installed, so the release is still invisible to preparation and the baseline stands.
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == PackageKey);
        Assert.Empty(_store.GetOverrides(_tenant, "acme.finance"));
        using var prepared = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.PrepareRoute,
            new { expectedBaselineDigest = baseline, activePackageKeys = new[] { "acme.finance", PackageKey } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, prepared.StatusCode);
        Assert.Equal("configuration-package-not-active", (await prepared.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("refusals")[0].GetProperty("code").GetString());
        Assert.Equal(baseline, await EffectiveDigestAsync());
    }

    /// <summary>
    /// ck-2 S8: the edit's packageKey is the author's statement, not the proof of ownership. An edit that
    /// names the release as its package but re-states a key acme.finance already ships is the same
    /// takeover, and the caller's ownership choice for the release does not admit it.
    /// </summary>
    [Fact]
    public async Task Released_package_cannot_claim_another_packages_definition_under_its_own_key()
    {
        const string proposalId = "proposal-s8-own-key";
        await StartAsync(proposalId);
        await AutosaveAsync(proposalId, "forms/invoice", FormsEdit, FormsKind, owner: "tenant.release");
        var digest = await SaveCheckReleaseAsync(proposalId, "tenant.release");

        using var refused = await _client.PostAsJsonAsync(InstallRoute(digest), new
        {
            ownership = new[] { new { definitionKey = "forms/invoice", packageKey = "tenant.release" } },
        });
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, body.ToString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(ReleasedPackInstaller.ForeignDefinitionCode, refusal.GetProperty("code").GetString());
        Assert.Equal("forms/invoice", refusal.GetProperty("target").GetString());
        Assert.Contains("'acme.finance'", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == "tenant.release");
    }

    /// <summary>ck-2 S8: an edit is a narrowing only under the kind the owner's definition has.</summary>
    [Fact]
    public async Task A_narrowing_body_under_another_content_kind_is_refused_as_a_foreign_definition()
    {
        const string proposalId = "proposal-s8-kind";
        await StartAsync(proposalId);
        await AutosaveAsync(proposalId, "forms/invoice", """{"id":"forms/invoice"}""", RecordsKind);
        var digest = await SaveCheckReleaseAsync(proposalId, "tenant.release");

        using var refused = await _client.PostAsJsonAsync(InstallRoute(digest), new { });
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, body.ToString());
        Assert.Equal(ReleasedPackInstaller.ForeignDefinitionCode,
            Assert.Single(body.GetProperty("refusals").EnumerateArray()).GetProperty("code").GetString());
        Assert.Empty(_store.GetOverrides(_tenant, "acme.finance"));
    }

    /// <summary>
    /// ck-2 S8, the other half of the ruling: an edit of another package's definition that only NARROWS
    /// it is carried as that package's tenant narrowing overlay, not installed as the release's content.
    /// The release's own definition installs as usual; acme.finance keeps its definition and gains the
    /// overlay row the Narrow door writes.
    /// </summary>
    [Fact]
    public async Task A_released_edit_that_only_narrows_another_packages_definition_is_carried_as_its_overlay()
    {
        const string proposalId = "proposal-s8-narrow";
        await StartAsync(proposalId);
        await AutosaveAsync(proposalId, "forms/tenant-note", FormsEdit, FormsKind, owner: "tenant.release");
        // The seeded acme.finance form is {"id":"forms/invoice","pack":"acme.finance"}; this drops "pack".
        await AutosaveAsync(proposalId, "forms/invoice", """{"id":"forms/invoice"}""", FormsKind);
        var digest = await SaveCheckReleaseAsync(proposalId, "tenant.release");

        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), new { });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
        Assert.Equal("installed", body.GetProperty("status").GetString());
        Assert.Equal(new[] { "forms/invoice" }, body.GetProperty("narrowed").EnumerateArray().Select(item => item.GetString()).ToArray());

        var release = Assert.Single(_store.ListInstalled(_tenant), item => item.PackKey == "tenant.release");
        Assert.Equal(PackLifecycleState.Active, release.Lifecycle);
        Assert.Equal(["forms/tenant-note"], release.SeedItems.Select(item => item.Key).ToArray());
        var finance = _store.GetActive(_tenant, "acme.finance")!;
        Assert.Contains(finance.SeedItems, item => item.Key == "forms/invoice");
        var overlay = Assert.Single(_store.GetOverrides(_tenant, "acme.finance"));
        Assert.Equal("forms/invoice", overlay.ContentKey);
        Assert.Equal("""{"pack":null}""", overlay.OverlayPatch.ToJsonString());
    }

    /// <summary>
    /// T-982 (ck-2 S7, api half). The released document pins every package its edits reference
    /// (platform #198). A release that narrows acme.finance pins acme.finance at the baseline revision:
    /// the conversion carries that pin into the export request, install refuses
    /// <c>pack.install.refused.unmet_dependency</c> while acme.finance is not Active and installs
    /// nothing, and once it is Active the release installs with the dependency recorded on the pack.
    /// </summary>
    [Fact]
    public async Task A_released_package_carries_its_dependency_closure()
    {
        var digest = await ReleaseNarrowingAsync("proposal-982-closure");
        var converted = ReleasedPackConversion.Convert(ReleasedDocument(), _node.Signer.IssuerId.ToBase64Url());
        var pin = Assert.Single(converted.Request!.Dependencies);
        Assert.Equal(("acme.finance", "1.0.0"), (pin.Key, pin.Version));

        _store.Deactivate(_tenant, "acme.finance", "1.0.0");
        using (var refused = await _client.PostAsJsonAsync(InstallRoute(digest), new { }))
        {
            var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, body.ToString());
            var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
            Assert.Equal(PackInstallCodes.RefusedUnmetDependency, refusal.GetProperty("code").GetString());
            Assert.Equal("acme.finance", refusal.GetProperty("target").GetString());
        }
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == "tenant.release");
        Assert.Empty(_store.GetOverrides(_tenant, "acme.finance"));

        _store.Activate(_tenant, "acme.finance", "1.0.0");
        Assert.Equal("installed", (await InstallAsync(digest)).GetProperty("status").GetString());
        var installed = Assert.Single(_store.ListInstalled(_tenant), item => item.PackKey == "tenant.release");
        var recorded = Assert.Single(installed.Dependencies);
        Assert.Equal(("acme.finance", "1.0.0"), (recorded.Key, recorded.Version));
    }

    /// <summary>T-982: the pin is a minimum-inclusive floor, so an Active dependency below it is unmet.</summary>
    [Fact]
    public async Task A_released_package_is_refused_while_its_dependency_is_below_the_pin()
    {
        var digest = await ReleaseNarrowingAsync("proposal-982-below");
        Seed("acme.finance", "0.9.0", ["records/invoice", "forms/invoice"]);
        Assert.Equal("0.9.0", _store.GetActive(_tenant, "acme.finance")!.Version);

        using var refused = await _client.PostAsJsonAsync(InstallRoute(digest), new { });
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, body.ToString());
        var refusal = Assert.Single(body.GetProperty("refusals").EnumerateArray());
        Assert.Equal(PackInstallCodes.RefusedUnmetDependency, refusal.GetProperty("code").GetString());
        Assert.Equal("acme.finance", refusal.GetProperty("target").GetString());
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == "tenant.release");
    }

    /// <summary>
    /// T-982: the conversion refuses a released document whose closure is absent, malformed, or does
    /// not pin a package one of its edits names, each by its own code, before anything is exported.
    /// </summary>
    [Fact]
    public async Task A_released_document_without_a_matching_closure_is_refused_by_name()
    {
        await ReleaseNarrowingAsync("proposal-982-shape");
        var principal = _node.Signer.IssuerId.ToBase64Url();
        ReleasedPackConversionException Refused(Action<System.Text.Json.Nodes.JsonObject> mutate)
        {
            var document = System.Text.Json.Nodes.JsonNode.Parse(ReleasedDocument())!.AsObject();
            mutate(document);
            return Assert.Throws<ReleasedPackConversionException>(() =>
                ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document.ToJsonString()), principal));
        }
        System.Text.Json.Nodes.JsonArray Pins(System.Text.Json.Nodes.JsonObject document) =>
            document["closure"]!["dependencies"]!.AsArray();

        var absent = Refused(document => document.Remove("closure"));
        Assert.Equal(("configuration-release-closure-missing", "closure"), (absent.Code, absent.Target));
        var noList = Refused(document => document["closure"]!.AsObject().Remove("dependencies"));
        Assert.Equal("configuration-release-closure-missing", noList.Code);

        var mismatched = Refused(document => Pins(document).Clear());
        Assert.Equal(("configuration-release-closure-mismatch", "acme.finance"), (mismatched.Code, mismatched.Target));
        var renamed = Refused(document => Pins(document)[0]!["key"] = "acme.other");
        Assert.Equal(("configuration-release-closure-mismatch", "acme.finance"), (renamed.Code, renamed.Target));

        var blankVersion = Refused(document => Pins(document)[0]!["version"] = " ");
        Assert.Equal(("configuration-release-closure-malformed", "acme.finance"), (blankVersion.Code, blankVersion.Target));
        var duplicate = Refused(document => Pins(document).Add(Pins(document)[0]!.DeepClone()));
        Assert.Equal(("configuration-release-closure-malformed", "acme.finance"), (duplicate.Code, duplicate.Target));
        var self = Refused(document => Pins(document).Add(new System.Text.Json.Nodes.JsonObject { ["key"] = "tenant.release", ["version"] = "1.0.0" }));
        Assert.Equal(("configuration-release-closure-malformed", "tenant.release"), (self.Code, self.Target));
    }

    /// <summary>
    /// Acceptance 3. A tampered Released package is refused on THIS path, before anything is converted,
    /// exported or installed — not only on the release path that wrote it.
    /// </summary>
    [Fact]
    public async Task A_tampered_released_package_fails_signature_verification_on_the_installation_path()
    {
        var releasedDigest = await ReleaseAsync();

        // Edit one byte of the definition body inside the stored released document. The signature covers
        // the SHA-256 of these exact bytes, so the row now offers an artifact the signature never named.
        using (var context = _db.CreateContext())
        {
            var row = context.ReleasedPackages.Single();
            var document = Encoding.UTF8.GetString(row.Document);
            Assert.Contains("purchaseOrderNumber", document, StringComparison.Ordinal);
            row.Document = Encoding.UTF8.GetBytes(document.Replace("purchaseOrderNumber", "purchaseOrderNumbeR", StringComparison.Ordinal));
            context.SaveChanges();
        }

        using var refused = await _client.PostAsJsonAsync(InstallRoute(releasedDigest), new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("refused", body.GetProperty("status").GetString());
        Assert.Equal("configuration-release-missing",
            body.GetProperty("refusals")[0].GetProperty("code").GetString());
        // Nothing installed, so nothing could be prepared over: the tamper is terminal, not cosmetic.
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == PackageKey);

        // Installing under the digest the tampered bytes now hash to is refused on the signature itself:
        // the row is found, and the node's signature over the released artifact does not cover it.
        using var context2 = _db.CreateContext();
        var tampered = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(context2.ReleasedPackages.Single().Document));
        Assert.NotEqual(releasedDigest, tampered);
        using var byTamperedDigest = await _client.PostAsJsonAsync(InstallRoute(tampered), new { });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, byTamperedDigest.StatusCode);
        Assert.Equal("configuration-release-signature-invalid",
            (await byTamperedDigest.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("refusals")[0].GetProperty("code").GetString());
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == PackageKey);

        // Restore the document and re-point the row's own digest at it, so the only thing left wrong is
        // the signature itself. The Ed25519 verification is therefore on this path in its own right,
        // not merely a digest comparison that happens to disagree.
        using (var context = _db.CreateContext())
        {
            var row = context.ReleasedPackages.Single();
            var document = Encoding.UTF8.GetString(row.Document);
            row.Document = Encoding.UTF8.GetBytes(document.Replace("purchaseOrderNumbeR", "purchaseOrderNumber", StringComparison.Ordinal));
            var signature = JsonDocument.Parse(row.SignatureJson).RootElement.GetProperty("signature").GetString()!;
            var flipped = (signature[0] == 'A' ? 'B' : 'A') + signature[1..];
            row.SignatureJson = row.SignatureJson.Replace(signature, flipped, StringComparison.Ordinal);
            context.SaveChanges();
        }
        using var forged = await _client.PostAsJsonAsync(InstallRoute(releasedDigest), OwnedByRelease());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, forged.StatusCode);
        Assert.Equal("configuration-release-signature-invalid",
            (await forged.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("refusals")[0].GetProperty("code").GetString());
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == PackageKey);
    }

    /// <summary>
    /// Acceptance 2. The definition-key to content-kind mapping is stated in exactly one place — the
    /// producer's edit — and this fails if a second place states it.
    /// </summary>
    /// <remarks>
    /// The mechanism, because "stated in one place" is easy to assert and hard to enforce. A second
    /// statement can only be a rule that reads a definition key and produces a kind, whether as the
    /// answer, as an override or as a fallback. All three are caught behaviourally rather than by
    /// scanning for text:
    /// <list type="number">
    /// <item>Key independence: rename every definition key and keep the stated kinds, and the kinds the
    /// conversion produces are unchanged. A table consulted as the answer or as an override fails here.</item>
    /// <item>Kind fidelity: keep the definition keys and change the stated kinds, and the produced kinds
    /// follow the statement. A table consulted as an override fails here too, from the other side.</item>
    /// <item>No fallback: a stated kind this node's transport does not define is refused by name. A table
    /// consulted only when the stated kind is unusable would answer instead of refusing, and fail here.</item>
    /// </list>
    /// Together they say the produced kind is a function of the stated kind alone, which is what
    /// "stated in exactly one place" means operationally.
    /// </remarks>
    [Fact]
    public async Task Only_the_producer_states_the_content_kind_and_a_second_statement_would_fail_this()
    {
        await ReleaseAsync();
        var document = Encoding.UTF8.GetString(ReleasedDocument());
        var principal = _node.Signer.IssuerId.ToBase64Url();

        // ck-2 S8: these edits name acme.finance, so they convert as foreign edits, not as own content;
        // the kind is read the same way for both.
        static IReadOnlyDictionary<string, PackContentKind> Kinds(string document, string principal)
        {
            var converted = ReleasedPackConversion.Convert(Encoding.UTF8.GetBytes(document), principal);
            return (converted.Request?.Contents.Select(source => (source.Key, source.Kind)) ?? [])
                .Concat(converted.ForeignEdits.Select(edit => (Key: edit.DefinitionKey, edit.Kind)))
                .ToDictionary(item => item.Key, item => item.Kind, StringComparer.Ordinal);
        }

        var stated = Kinds(document, principal);
        Assert.Equal(PackContentKind.AssetTypeDefinition, stated["records/invoice"]);
        Assert.Equal(PackContentKind.FormDefinition, stated["forms/invoice"]);

        // (1) Key independence. The definition keys become names nothing could have a rule about; every
        // produced kind is the same one the document states.
        var renamed = Kinds(document
            .Replace("records/invoice", "zzz-first", StringComparison.Ordinal)
            .Replace("forms/invoice", "zzz-second", StringComparison.Ordinal), principal);
        Assert.Equal(PackContentKind.AssetTypeDefinition, renamed["zzz-first"]);
        Assert.Equal(PackContentKind.FormDefinition, renamed["zzz-second"]);
        Assert.Equal(stated.Values.Order().ToArray(), renamed.Values.Order().ToArray());

        // (2) Kind fidelity. The keys stay; the statements swap; the produced kinds swap with them.
        var swapped = Kinds(document
            .Replace($"\"contentKind\":\"{RecordsKind}\"", "\"contentKind\":\"@records@\"", StringComparison.Ordinal)
            .Replace($"\"contentKind\":\"{FormsKind}\"", $"\"contentKind\":\"{RecordsKind}\"", StringComparison.Ordinal)
            .Replace("\"contentKind\":\"@records@\"", $"\"contentKind\":\"{FormsKind}\"", StringComparison.Ordinal), principal);
        Assert.Equal(PackContentKind.FormDefinition, swapped["records/invoice"]);
        Assert.Equal(PackContentKind.AssetTypeDefinition, swapped["forms/invoice"]);

        // (3) No fallback. A kind the transport does not define is a named refusal, naming the definition;
        // nothing answers in its place.
        var unknown = Assert.Throws<ReleasedPackConversionException>(() => Kinds(
            document.Replace($"\"contentKind\":\"{FormsKind}\"", "\"contentKind\":\"InvoiceFormDefinition\"", StringComparison.Ordinal),
            principal));
        Assert.Equal("configuration-release-content-kind-unknown", unknown.Code);
        Assert.Equal("forms/invoice", unknown.Target);

        // And an item that states no kind at all is refused rather than classified from its key.
        var missing = Assert.Throws<ReleasedPackConversionException>(() => Kinds(
            document.Replace($"\"contentKind\":\"{FormsKind}\",", string.Empty, StringComparison.Ordinal), principal));
        Assert.Equal("configuration-release-content-kind-missing", missing.Code);
        Assert.Equal("forms/invoice", missing.Target);

        // And the entry point has nothing to fall back on either: an edit that states no kind is
        // refused where it is authored, so no released document can reach the converter without one.
        using var unstated = await _client.PutAsJsonAsync($"{Proposal("proposal-667")}/edits",
            new { definitionKey = "forms/invoice", packageKey = "acme.finance", bodyJson = FormsEdit });
        Assert.Equal(HttpStatusCode.BadRequest, unstated.StatusCode);
    }

    [Fact]
    public async Task Installing_an_unoffered_digest_refuses_and_installing_twice_is_the_same_pack()
    {
        using (var missing = await _client.PostAsJsonAsync(InstallRoute(new string('0', 64)), new { }))
        {
            Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
            Assert.Equal("configuration-release-missing",
                (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refusals")[0].GetProperty("code").GetString());
        }

        // ck-2 S8: a release may no longer take acme.finance's definitions, so this repeats acme.finance's
        // own upgrade, the R1 shape that still installs.
        var releasedDigest = await ReleaseAsync(packageKey: "acme.finance");
        Assert.Equal("installed", (await InstallAsync(releasedDigest)).GetProperty("status").GetString());
        // The same released bytes export the same pack coordinate, so a repeat is the same identity
        // rather than a second one; it never leaves two packs claiming the same definitions.
        using var again = await _client.PostAsJsonAsync(InstallRoute(releasedDigest), new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(_store.ListInstalled(_tenant), item => item.PackKey == "acme.finance" && item.Version == Revision);
    }

    /// <summary>
    /// The ordinary R1 shape: a domain expert evolves THEIR OWN pack. The released package carries the
    /// same pack key as the pack whose definitions it re-states, so it is that pack's successor rather
    /// than a stranger contesting its keys — the installer treats it as an upgrade and no ownership
    /// ceremony stands between the release and the effective digest.
    /// </summary>
    [Fact]
    public async Task Evolving_the_pack_that_owns_the_definitions_needs_no_ownership_choice_at_all()
    {
        var baseline = await PinEffectiveAsync();
        var releasedDigest = await ReleaseAsync(packageKey: "acme.finance", proposalId: "proposal-667-own");

        // No ownership in the request body: the released package IS acme.finance, one revision on.
        using var installed = await _client.PostAsJsonAsync(InstallRoute(releasedDigest), new { });
        var body = await installed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(installed.StatusCode == HttpStatusCode.OK, body.ToString());
        Assert.Equal("acme.finance", body.GetProperty("packKey").GetString());
        Assert.Equal(Revision, body.GetProperty("version").GetString());
        Assert.Equal(Revision, Assert.Single(_store.ListInstalled(_tenant),
            item => item.PackKey == "acme.finance" && item.Lifecycle == PackLifecycleState.Active).Version);

        using var prepared = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.PrepareRoute,
            new { expectedBaselineDigest = baseline, activePackageKeys = new[] { "acme.finance" } });
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var candidateDigest = (await prepared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("candidateDigest").GetString()!;
        Assert.NotEqual(baseline, candidateDigest);

        using var activated = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.ActivateRoute, new
        {
            expectedBaselineDigest = baseline,
            candidateDigest,
            evidenceIntent = new { id = "intent-667-own", reason = "Activate the evolved finance pack." },
        });
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        Assert.Equal(candidateDigest, await EffectiveDigestAsync());
    }

    [Fact(DisplayName = "T-909 ck-1: released installation refuses a record type that claims a compiled shape")]
    public async Task Released_install_refuses_a_compiled_shape_claim()
    {
        const string proposalId = "proposal-909";
        using (var started = await _client.PostAsJsonAsync(ConfigurationProposalRoutes.ProposalsRoute, new { proposalId }))
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        // ck-2 S8: the edits are acme.bootstrap's own, so the compiled-shape claim is what refuses.
        await AutosaveAsync(proposalId, "forms/bootstrap-note", FormsEdit, FormsKind, owner: "acme.bootstrap");
        await AutosaveAsync(proposalId, "kernel.field", """{"sealed":true}""", "RecordType", owner: "acme.bootstrap");
        using (var saved = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/versions", new { rationale = Rationale }))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var checked_ = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/checks", new { receiptId = "receipt-909" }))
            Assert.Equal(HttpStatusCode.OK, checked_.StatusCode);
        using var released = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/release",
            new { ordinal = 1, packageKey = "acme.bootstrap", revision = Revision });
        var releasedBody = await released.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(released.StatusCode == HttpStatusCode.OK, releasedBody.ToString());
        var digest = releasedBody.GetProperty("releasedPackage").GetProperty("digest").GetString()!;

        using var install = await _client.PostAsJsonAsync(InstallRoute(digest), new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, install.StatusCode);
        var refusal = Assert.Single((await install.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("refusals").EnumerateArray());
        Assert.Equal(Harborline.Kernel.Core.KernelBootstrapErrors.CompiledShapeReplacement, refusal.GetProperty("code").GetString());
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == "acme.bootstrap");
    }

    private static string InstallRoute(string digest) => $"/api/local-node/configuration/releases/{digest}/install";

    private static object OwnedByRelease() => new
    {
        ownership = new[]
        {
            new { definitionKey = "records/invoice", packageKey = PackageKey },
            new { definitionKey = "forms/invoice", packageKey = PackageKey },
        },
    };

    private async Task<JsonElement> InstallAsync(string digest)
    {
        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), new { });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body.ToString());
        return body;
    }

    private byte[] ReleasedDocument()
    {
        using var context = _db.CreateContext();
        return context.ReleasedPackages.AsNoTracking().Single().Document;
    }

    /// <summary>Runs T-461's path end to end and returns the Released package's own artifact digest.</summary>
    private async Task<string> ReleaseAsync(string packageKey = PackageKey, string proposalId = "proposal-667")
    {
        await StartAsync(proposalId);
        await AutosaveAsync(proposalId, "records/invoice", RecordsEdit, RecordsKind);
        await AutosaveAsync(proposalId, "forms/invoice", FormsEdit, FormsKind);
        return await SaveCheckReleaseAsync(proposalId, packageKey);
    }

    /// <summary>Releases tenant.release with one own definition and one narrowing edit of acme.finance's.</summary>
    private async Task<string> ReleaseNarrowingAsync(string proposalId)
    {
        await StartAsync(proposalId);
        await AutosaveAsync(proposalId, "forms/tenant-note", FormsEdit, FormsKind, owner: "tenant.release");
        await AutosaveAsync(proposalId, "forms/invoice", """{"id":"forms/invoice"}""", FormsKind);
        return await SaveCheckReleaseAsync(proposalId, "tenant.release");
    }

    private async Task StartAsync(string proposalId)
    {
        using var started = await _client.PostAsJsonAsync(ConfigurationProposalRoutes.ProposalsRoute, new { proposalId });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
    }

    private async Task<string> SaveCheckReleaseAsync(string proposalId, string packageKey)
    {
        using (var saved = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/versions", new { rationale = Rationale }))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var checked_ = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/checks", new { receiptId = "receipt-667" }))
            Assert.Equal(HttpStatusCode.OK, checked_.StatusCode);
        using var released = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/release",
            new { ordinal = 1, packageKey, revision = Revision });
        var body = await released.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(released.StatusCode == HttpStatusCode.OK, body.ToString());
        return body.GetProperty("releasedPackage").GetProperty("digest").GetString()!;
    }

    private static string Proposal(string proposalId) => $"/api/local-node/configuration/proposals/{proposalId}";

    private async Task AutosaveAsync(string proposalId, string definitionKey, string bodyJson, string contentKind,
        string owner = "acme.finance")
    {
        using var response = await _client.PutAsJsonAsync($"{Proposal(proposalId)}/edits",
            new { definitionKey, packageKey = owner, bodyJson, contentKind });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Commits one effective generation over the seeded pack, and returns its digest.</summary>
    private async Task<string> PinEffectiveAsync()
    {
        var derived = await EffectiveDigestAsync();
        using var prepared = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.PrepareRoute,
            new { expectedBaselineDigest = derived, activePackageKeys = new[] { "acme.finance" } });
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var candidateDigest = (await prepared.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("candidateDigest").GetString()!;
        using var activated = await _client.PostAsJsonAsync(ConfigurationActivationRoutes.ActivateRoute, new
        {
            expectedBaselineDigest = derived,
            candidateDigest,
            evidenceIntent = new { id = "intent-667-baseline", reason = "Pin the baseline generation." },
        });
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        return (await activated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("effectiveDigest").GetString()!;
    }

    private async Task<string> EffectiveDigestAsync()
    {
        using var response = await _client.GetAsync(ConfigurationActivationRoutes.EffectiveRoute);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("digest").GetString()!;
    }

    private void Seed(string packKey, string version, string[] contentKeys)
    {
        var seeds = contentKeys.Select(key => new PackSeedItem(key, PackContentKind.FormDefinition, "1",
            $$"""{"id":"{{key}}","pack":"{{packKey}}"}""", Cid.FromBytes(Encoding.UTF8.GetBytes(key + packKey)))).ToArray();
        var pack = new InstalledPack(packKey, version, PackScopeTier.Horizontal, PackLifecycleState.Draft, seeds,
            new Dictionary<string, int>(), Frozen, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, Array.Empty<PackDependencyRef>());
        _store.Commit(new PackInstallTransaction(_tenant, pack, new PackInstallWatermark(packKey, version, new Dictionary<string, int>()), []));
        _store.Activate(_tenant, packKey, version);
    }

    private sealed class FrozenClock(DateTimeOffset at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => at;
    }

    private sealed class ActiveTeam(TeamContext? active) : IActiveTeamAccessor
    {
        public TeamContext? Active { get; set; } = active;
        public Task SetActiveAsync(TeamId teamId, CancellationToken ct) => Task.CompletedTask;
        public event EventHandler<ActiveTeamChangedEventArgs>? ActiveChanged;
        private void Keep() => ActiveChanged?.Invoke(this, new ActiveTeamChangedEventArgs(null, null));
    }
}
