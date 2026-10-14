using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.Kernel.Runtime.Teams;
using Harborline.Api.LocalNodeHost.Data.Configuration;
using Harborline.Api.LocalNodeHost.Data.Financial;
using Harborline.Api.LocalNodeHost.Data.Packs;
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Packs;

using Xunit;

using ConfigurationGeneration = Harborline.Blocks.BuilderDefinitions.ConfigurationGeneration;
using RecordClass = Harborline.Blocks.BuilderDefinitions.RecordClass;
using RecordsExposure = Harborline.Blocks.BuilderDefinitions.RecordsExposure;
using RecordsRequirement = Harborline.Blocks.BuilderDefinitions.RecordsRequirement;
using RecordTypeDefinition = Harborline.Kernel.SchemaValidation.Records.RecordTypeDefinition;
using RecordTypeDefinitionJson = Harborline.Blocks.BuilderDefinitions.RecordTypeDefinitionJson;
using RecordTypeDocument = Harborline.Blocks.BuilderDefinitions.RecordTypeDocument;
using RecordsIntentValidator = Harborline.Kernel.SchemaValidation.Records.RecordsIntentValidator;

namespace Harborline.Api.LocalNodeHost.Tests.Configuration;

/// <summary>
/// Live producer exposure through the host: the landed platform producer (ConfigurationProposal at
/// Platform 2c7bef, driven by the real ConfigurationProposalStore over the real effective generation)
/// releases a document, and the real signer, offer verification, converter, exporter, pack verifier and
/// installer consume those exact bytes. The explicit altered-and-resigned adversarial control modifies
/// producer output; all other released documents are the ones the store signed and holds.
/// </summary>
/// <remarks>
/// Oracles. Keys, kinds, owners, revisions and interface versions are the literals this fixture authors
/// (edit bodies and installed seeds below), never values read back from the producer or a production
/// constant. Ownership comes from the seeded installed packs, so the effective generation's ownership
/// selection is asserted against the literal seed owner before release. Exposure bodies use transport kinds
/// this host already supports (FormDefinition, AssetTypeDefinition); Records content is consumed only by the
/// package-compile control and is never installed here. Root exposure arrays are ordinal because the landed
/// PlatformPackageManifest sorts them; the literal fixtures below are authored so that ordinal order is the
/// order written here.
/// </remarks>
public sealed class ReleasedPackProducerExposureIntegrationTests : IAsyncLifetime
{
    private const string ReleaseKey = "tenant.release";
    private const string FinanceKey = "acme.finance";
    private const string Revision = "1.1.0";
    private const string Rationale = "Expose the release's own definitions.";
    private const string FormKind = "FormDefinition";
    private const string AssetKind = "AssetTypeDefinition";

    // acme.finance's installed definition. The envelope already declares acme.finance's own interface 7, so a
    // narrowing edit that keeps it and drops "pack" is a narrowing, and carries a foreign interface version.
    private const string FinanceInvoiceSeed =
        """{"id":"forms/invoice","pack":"acme.finance","envelope":{"exposes":{"interface_version":7}}}""";
    private const string FinanceInvoiceNarrowed =
        """{"id":"forms/invoice","envelope":{"exposes":{"interface_version":7}}}""";

    private static readonly TeamId TeamA = new(Guid.Parse("aaaa0000-0000-0000-0000-000000000274"));
    private static readonly DateTimeOffset Frozen = new(2026, 10, 9, 9, 0, 0, TimeSpan.Zero);

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private PacksTestStore _db = null!;
    private DurablePackInstallStore _store = null!;
    private ConfigurationProposalStore _proposals = null!;
    private NodePrincipalSigner _node = null!;
    private CountingPackExporter _exporter = null!;
    private PackFileCodec _codec = null!;
    private TenantId _tenant;

    public async Task InitializeAsync()
    {
        _db = await PacksTestStore.CreateAsync(keySalt: 74);
        _store = new DurablePackInstallStore(_db.Factory);
        var gate = TestPackGate.AllowAll();
        var activation = new ConfigurationActivationTarget(_db.Factory, _store, gate, evidence: null);

        var seed = new byte[32];
        for (var index = 0; index < seed.Length; index++) seed[index] = (byte)(index + 74);
        _node = new NodePrincipalSigner(seed);
        _proposals = new ConfigurationProposalStore(_db.Factory, activation, _node.Signer);

        _codec = new PackFileCodec();
        // The real exporter, observed only: every call is delegated unchanged.
        _exporter = new CountingPackExporter(new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            _codec, TimeProvider.System));
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), _codec),
            _store, new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate());
        var trustStore = HostedPackInstallApiEndpoint.BuildTrustStore(_node, NullLogger.Instance);
        var releases = new ReleasedPackInstaller(_proposals, _exporter, installer, _node, new Ed25519Verifier(),
            trustStore, PackRevocationList.Empty, _store, gate);

        var activeTeam = new ActiveTeam(new TeamContext(TeamA, "Team A", new ServiceCollection().BuildServiceProvider(), TimeProvider.System));
        _tenant = NodeTenant.Resolve(activeTeam);
        PlatformPackTestPreload.Activate(_store, _tenant);

        var builder = WebApplication.CreateBuilder();
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
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _client = new HttpClient { BaseAddress = new Uri(addresses!.Addresses.First()) };
        _client.DefaultRequestHeaders.Add("X-Test-Desktop", "1");

        Seed(FinanceKey, "1.0.0", ("forms/invoice", PackContentKind.FormDefinition, FinanceInvoiceSeed));
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        _node?.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        await _db.DisposeAsync();
    }

    // Literal canonical Record Type body copied from the landed platform fixture
    // (hlp.blocks.builder-definitions.tests/RecordTypeDefinitionStoreTests.AssetClassJson, Platform 2c7bef).
    // This file does not read that fixture; the copy is the independent oracle.
    private const string AssetClassJson =
        """{"class_id":"eam.equipment","envelope":{"contract":{"major":1,"minor":0},"package_id":"eam-core","section":"eam","tenant":"tenant-a"},"fields":[{"binding":{"constraints":{"maximum_count":1,"minimum_count":0,"read_role_ids":[],"required":true},"kind":{"kind_id":"text","parameters":{},"version":"1.0.0"}},"defaults_provenance":{"kind_id":"text","kind_version":"1.0.0"},"display_name":"Asset tag","field_key":"asset_tag","governance":{"classification":"internal","confidential":false,"masked":true,"personal_data":true}},{"binding":{"constraints":{"maximum_count":1,"minimum_count":0,"read_role_ids":[],"required":false},"kind":{"kind_id":"count","parameters":{},"version":"1.0.0"}},"defaults_provenance":{"kind_id":"count","kind_version":"1.0.0"},"display_name":"Quantity","field_key":"quantity"},{"display_name":"Notes","field_key":"notes"}],"name":"Asset Class","record_class":"master","record_type_id":"eam.asset-class"}""" + "\n";

    /// <summary>
    /// Control 1. Compile-time consumption of the landed Records producer types through the existing
    /// BuilderDefinitions package reference and its Kernel.SchemaValidation/Contracts closure. This proves the
    /// authored source binds to those types and that their real codec and identity rules behave as the literal
    /// fixture states. It does not prove which package bytes were restored: project.assets.json, selected
    /// archive hashes and feed provenance are a separate controller requirement.
    /// </summary>
    [Fact]
    public void LandedRecordsProducerTypesAreConsumedFromPinnedPackage()
    {
        // The real codec reads the literal canonical body and writes it back byte-identically.
        RecordTypeDocument document = RecordTypeDefinitionJson.Deserialize(Encoding.UTF8.GetBytes(AssetClassJson));
        Assert.Equal(("tenant-a", "eam", "eam-core"),
            (document.Envelope.Tenant, document.Envelope.Section, document.Envelope.PackageId));
        Assert.Equal(("Asset Class", "eam.asset-class", "eam.equipment"), (document.Name, document.RecordTypeId, document.ClassId));
        Assert.Equal(RecordClass.Master, document.RecordClass);
        Assert.Null(document.Envelope.Exposes);
        Assert.Equal(new[] { "asset_tag", "quantity", "notes" }, document.Fields.Select(field => field.FieldKey).ToArray());
        Assert.Null(RecordTypeDefinitionJson.FirstMissing(document));
        Assert.Equal(AssetClassJson, Encoding.UTF8.GetString(RecordTypeDefinitionJson.SerializeCanonical(document)));

        // Kernel Records identity: the typed grammar the producer hands to the Records validator.
        RecordTypeDefinition identity = document.ToDefinition();
        Assert.Equal("eam.asset-class", identity.RecordTypeId);
        var validator = new RecordsIntentValidator();
        Assert.Empty(validator.Validate(identity, previousVersion: null));
        var duplicated = identity with { Fields = [identity.Fields[0], identity.Fields[0]] };
        Assert.Equal(new[] { ("records.identity.duplicate_field_key", "/fields/1/field_key") },
            validator.Validate(duplicated, previousVersion: null).Select(refusal => (refusal.Code, refusal.JsonPointer)).ToArray());
        var renamed = identity with { RecordTypeId = "eam.other" };
        Assert.Equal(new[] { ("records.identity.record_type_id_immutable", "/record_type_id") },
            validator.Validate(renamed, identity).Select(refusal => (refusal.Code, refusal.JsonPointer)).ToArray());

        // The envelope's exposure and requirement spellings are the snake_case members the release producer
        // reads (envelope.exposes.interface_version; envelope.requires[].capability as pack-key@N).
        var exposed = document with
        {
            Envelope = document.Envelope with
            {
                Exposes = new RecordsExposure(3),
                Requires = [new RecordsRequirement("acme.finance@1")],
            },
        };
        var bytes = RecordTypeDefinitionJson.SerializeCanonical(exposed);
        var envelope = JsonNode.Parse(bytes)!["envelope"]!;
        Assert.Equal(
            """{"contract":{"major":1,"minor":0},"exposes":{"interface_version":3},"package_id":"eam-core","requires":[{"capability":"acme.finance@1"}],"section":"eam","tenant":"tenant-a"}""",
            envelope.ToJsonString());
        var reread = RecordTypeDefinitionJson.Deserialize(bytes);
        Assert.Equal(3, reread.Envelope.Exposes!.InterfaceVersion);
        Assert.True(RecordsRequirement.TryParse("acme.finance@1", out var package, out var interfaceVersion));
        Assert.Equal(("acme.finance", 1), (package, interfaceVersion));
        Assert.False(RecordsRequirement.TryParse("acme.finance@0", out _, out _));
    }

    /// <summary>
    /// Control 2. Two new keys owned by the release, each declaring interface 3, released by the real producer
    /// over the unchanged effective baseline, then signed, offered, verified, converted, exported, verified as a
    /// pack and installed. Root, converted, signed and installed metadata all carry the literal keys and version.
    /// alpha's signed envelope.requires entry acme.finance@1 is enforced when the release activates: the live
    /// Active-interface check (PackInterfaceRequirementCheck, from PackInstaller activation) reads it where it was
    /// signed, so this control's own provider fixture advertises interface 1 and exposes forms/invoice. That is
    /// live Active-pack activation, not pinned Records closure admission.
    /// </summary>
    [Fact]
    public async Task RealOwnedReleasePreservesExposureThroughSigningVerificationAndInstall()
    {
        const string proposalId = "proposal-274-owned";
        const string alphaBody = """{"title":"Alpha","envelope":{"requires":[{"capability":"acme.finance@1"}],"exposes":{"interface_version":3}}}""";
        const string zetaBody = """{"title":"Zeta","envelope":{"exposes":{"interface_version":3}}}""";
        // This control's provider: acme.finance 1.0.0 advertises interface 1 and exposes forms/invoice, whose own
        // envelope declares that interface. It replaces this test's initial seed of the same version before the
        // baseline is read, so the closure still pins acme.finance at 1.0.0.
        const string providerInvoice = """{"id":"forms/invoice","pack":"acme.finance","envelope":{"exposes":{"interface_version":1}}}""";
        var provider = new InstalledPack(FinanceKey, "1.0.0", PackScopeTier.Horizontal, PackLifecycleState.Draft,
            [new PackSeedItem("forms/invoice", PackContentKind.FormDefinition, "1", providerInvoice,
                Cid.FromBytes(Encoding.UTF8.GetBytes(providerInvoice)))],
            new Dictionary<string, int>(), Frozen, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, Array.Empty<PackDependencyRef>(), Exposes: ["forms/invoice"], InterfaceVersion: 1);
        _store.Commit(new PackInstallTransaction(_tenant, provider,
            new PackInstallWatermark(FinanceKey, "1.0.0", new Dictionary<string, int>()), []));
        _store.Activate(_tenant, FinanceKey, "1.0.0");
        var advertised = _store.GetActive(_tenant, FinanceKey)!;
        Assert.Equal(("1.0.0", 1), (advertised.Version, advertised.InterfaceVersion));
        Assert.Equal(new[] { "forms/invoice" }, advertised.Exposes);
        var baseline = _proposals.ReadEffective(_tenant);
        Assert.Null(Owner(baseline, "alpha"));
        Assert.Null(Owner(baseline, "zeta"));

        await StartAsync(proposalId);
        await AutosaveAsync(proposalId, "zeta", zetaBody, AssetKind, ReleaseKey);
        await AutosaveAsync(proposalId, "alpha", alphaBody, FormKind, ReleaseKey);
        var digest = await SaveCheckReleaseAsync(proposalId, ReleaseKey);

        var offer = Offer(digest);
        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(offer.Released.Document.Span)));
        Assert.True(ConfigurationProposalStore.VerifyOffer(offer, new Ed25519Verifier()));
        var document = offer.Released.Document.ToArray();
        AssertBaselineUnchanged(baseline, document);

        using (var parsed = JsonDocument.Parse(document))
        {
            var root = parsed.RootElement;
            Assert.Equal(new[] { "alpha", "zeta" }, RootExposes(root));
            Assert.Equal(3, root.GetProperty("interfaceVersion").GetInt32());
            Assert.Equal(new[] { "acme.finance@1.0.0" }, Closure(root));
            AssertBody(alphaBody, JsonNode.Parse(Payload(root, "alpha").GetProperty("body").GetRawText()));
            AssertBody(zetaBody, JsonNode.Parse(Payload(root, "zeta").GetProperty("body").GetRawText()));
            Assert.Equal((ReleaseKey, FormKind), (Payload(root, "alpha").GetProperty("packageKey").GetString(),
                Payload(root, "alpha").GetProperty("contentKind").GetString()));
            Assert.Equal((ReleaseKey, AssetKind), (Payload(root, "zeta").GetProperty("packageKey").GetString(),
                Payload(root, "zeta").GetProperty("contentKind").GetString()));
        }

        var converted = ReleasedPackConversion.Convert(document, _node.Signer.IssuerId.ToBase64Url());
        var request = Assert.IsType<PackExportRequest>(converted.Request);
        Assert.Empty(converted.ForeignEdits);
        AssertBody(alphaBody, Assert.Single(request.Contents, content => content.Key == "alpha").Content);
        AssertBody(zetaBody, Assert.Single(request.Contents, content => content.Key == "zeta").Content);
        Assert.Equal(new[] { "alpha", "zeta" }, request.Exposes);
        Assert.Equal(3, request.InterfaceVersion);
        Assert.Equal(new[] { ("acme.finance", "1.0.0") }, request.Dependencies.Select(pin => (pin.Key, pin.Version)).ToArray());

        var installed = await InstallAsync(digest);
        Assert.Equal(HttpStatusCode.OK, installed.Status);
        Assert.Equal("installed", installed.Body.GetProperty("status").GetString());
        Assert.Equal((ReleaseKey, Revision), (installed.Body.GetProperty("packKey").GetString(), installed.Body.GetProperty("version").GetString()));

        // The exact artifact the installer received, verified again against the node's own roster root.
        var file = Assert.Single(_exporter.Exported);
        var verified = new PackVerifier(new Ed25519Verifier(), _codec).Verify(file, OwnRoster());
        Assert.Equal(PackVerdict.Verified, verified.Verdict);
        var manifest = verified.Manifest!;
        Assert.Equal((ReleaseKey, Revision), (manifest.Key, manifest.Version));
        Assert.Equal(new[] { "alpha", "zeta" }, manifest.Exposes);
        Assert.Equal(3, manifest.InterfaceVersion);
        Assert.Equal(new[] { ("alpha", PackContentKind.FormDefinition), ("zeta", PackContentKind.AssetTypeDefinition) },
            manifest.Contents.Select(content => (content.Key, content.Kind)).ToArray());
        Assert.Equal(new[] { ("acme.finance", "1.0.0") }, manifest.Dependencies.Select(pin => (pin.Key, pin.Version)).ToArray());

        var pack = Assert.Single(_store.ListInstalled(_tenant), item => item.PackKey == ReleaseKey);
        Assert.Equal((Revision, PackLifecycleState.Active), (pack.Version, pack.Lifecycle));
        Assert.Equal(new[] { "alpha", "zeta" }, pack.Exposes);
        Assert.Equal(3, pack.InterfaceVersion);
        Assert.Equal(new[] { ("acme.finance", "1.0.0") }, pack.Dependencies.Select(pin => (pin.Key, pin.Version)).ToArray());
        Assert.Collection(pack.SeedItems.OrderBy(item => item.Key, StringComparer.Ordinal),
            alpha =>
            {
                Assert.Equal(("alpha", PackContentKind.FormDefinition, Revision), (alpha.Key, alpha.Kind, alpha.Version));
                var body = alpha.ParseContent();
                AssertBody(alphaBody, body);
                Assert.Equal("Alpha", body["title"]!.GetValue<string>());
                Assert.Equal(3, body["envelope"]!["exposes"]!["interface_version"]!.GetValue<int>());
                Assert.Equal("acme.finance@1", body["envelope"]!["requires"]![0]!["capability"]!.GetValue<string>());
            },
            zeta =>
            {
                Assert.Equal(("zeta", PackContentKind.AssetTypeDefinition, Revision), (zeta.Key, zeta.Kind, zeta.Version));
                var body = zeta.ParseContent();
                AssertBody(zetaBody, body);
                Assert.Equal("Zeta", body["title"]!.GetValue<string>());
                Assert.Equal(3, body["envelope"]!["exposes"]!["interface_version"]!.GetValue<int>());
            });
        // The release installed beside acme.finance; it took nothing of acme.finance's.
        Assert.Equal(new[] { "forms/invoice" }, _store.GetActive(_tenant, FinanceKey)!.SeedItems.Select(item => item.Key).ToArray());
        Assert.Empty(_store.GetOverrides(_tenant, FinanceKey));
    }

    /// <summary>
    /// Control 3. One owned definition at interface 3 and one K8 narrowing of acme.finance's definition whose
    /// body declares acme.finance's own interface 7. Unfiltered aggregation of {3, 7} would refuse as
    /// configuration-release-interface-ambiguous; the release succeeds and exposes only the owned key at 3,
    /// while the foreign body, its stated owner and its pin travel unchanged and install as acme.finance's overlay.
    /// </summary>
    [Fact]
    public async Task RealMixedReleaseExposesOnlyCanonicalOwnedDefinitions()
    {
        const string proposalId = "proposal-274-mixed";
        const string noteBody = """{"title":"Tenant note","envelope":{"exposes":{"interface_version":3}}}""";
        var baseline = _proposals.ReadEffective(_tenant);
        Assert.Equal(FinanceKey, Owner(baseline, "forms/invoice"));

        await StartAsync(proposalId);
        await AutosaveAsync(proposalId, "forms/tenant-note", noteBody, FormKind, ReleaseKey);
        await AutosaveAsync(proposalId, "forms/invoice", FinanceInvoiceNarrowed, FormKind, FinanceKey);
        var digest = await SaveCheckReleaseAsync(proposalId, ReleaseKey);
        var offer = Offer(digest);
        Assert.True(ConfigurationProposalStore.VerifyOffer(offer, new Ed25519Verifier()));
        var document = offer.Released.Document.ToArray();
        AssertBaselineUnchanged(baseline, document);

        using (var parsed = JsonDocument.Parse(document))
        {
            var root = parsed.RootElement;
            Assert.Equal(new[] { "forms/tenant-note" }, RootExposes(root));
            Assert.Equal(3, root.GetProperty("interfaceVersion").GetInt32());
            Assert.Equal(new[] { "acme.finance@1.0.0" }, Closure(root));
            AssertBody(noteBody, JsonNode.Parse(Payload(root, "forms/tenant-note").GetProperty("body").GetRawText()));
            var foreign = Payload(root, "forms/invoice");
            AssertBody(FinanceInvoiceNarrowed, JsonNode.Parse(foreign.GetProperty("body").GetRawText()));
            Assert.Equal(FinanceKey, foreign.GetProperty("packageKey").GetString());
            Assert.Equal(FormKind, foreign.GetProperty("contentKind").GetString());
            Assert.Equal(7, foreign.GetProperty("body").GetProperty("envelope").GetProperty("exposes").GetProperty("interface_version").GetInt32());
            Assert.False(foreign.GetProperty("body").TryGetProperty("pack", out _));
        }

        var converted = ReleasedPackConversion.Convert(document, _node.Signer.IssuerId.ToBase64Url());
        var request = Assert.IsType<PackExportRequest>(converted.Request);
        Assert.Equal(new[] { "forms/tenant-note" }, request.Contents.Select(content => content.Key).ToArray());
        AssertBody(noteBody, Assert.Single(request.Contents).Content);
        Assert.Equal(new[] { "forms/tenant-note" }, request.Exposes);
        Assert.Equal(3, request.InterfaceVersion);
        var edit = Assert.Single(converted.ForeignEdits);
        AssertBody(FinanceInvoiceNarrowed, edit.Body);
        Assert.Equal(("forms/invoice", FinanceKey, PackContentKind.FormDefinition), (edit.DefinitionKey, edit.PackageKey, edit.Kind));
        Assert.Equal(7, edit.Body["envelope"]!["exposes"]!["interface_version"]!.GetValue<int>());
        Assert.Equal(new[] { ("acme.finance", "1.0.0") }, converted.Dependencies.Select(pin => (pin.Key, pin.Version)).ToArray());

        var installed = await InstallAsync(digest);
        Assert.Equal(HttpStatusCode.OK, installed.Status);
        Assert.Equal("installed", installed.Body.GetProperty("status").GetString());
        Assert.Equal(new[] { "forms/invoice" },
            installed.Body.GetProperty("narrowed").EnumerateArray().Select(item => item.GetString()).ToArray());

        var file = Assert.Single(_exporter.Exported);
        var manifest = new PackVerifier(new Ed25519Verifier(), _codec).Verify(file, OwnRoster()).Manifest!;
        Assert.Equal(new[] { "forms/tenant-note" }, manifest.Exposes);
        Assert.Equal(3, manifest.InterfaceVersion);
        Assert.Equal(new[] { "forms/tenant-note" }, manifest.Contents.Select(content => content.Key).ToArray());

        var release = Assert.Single(_store.ListInstalled(_tenant), item => item.PackKey == ReleaseKey);
        Assert.Equal(new[] { "forms/tenant-note" }, release.SeedItems.Select(item => item.Key).ToArray());
        AssertBody(noteBody, Assert.Single(release.SeedItems).ParseContent());
        Assert.Equal(new[] { "forms/tenant-note" }, release.Exposes);
        Assert.Equal(3, release.InterfaceVersion);
        // The foreign partition is retained as acme.finance's: its seed is untouched and it gains the overlay.
        var finance = _store.GetActive(_tenant, FinanceKey)!;
        Assert.Equal(FinanceInvoiceSeed, Assert.Single(finance.SeedItems).CanonicalJson);
        var overlay = Assert.Single(_store.GetOverrides(_tenant, FinanceKey));
        Assert.Equal("forms/invoice", overlay.ContentKey);
        Assert.Equal("""{"pack":null}""", overlay.OverlayPatch.ToJsonString());
    }

    /// <summary>
    /// Control 4. acme.finance owns forms/invoice in the effective baseline; the edit states the release as its
    /// package. The producer excludes that key from root exposure (keeping the owned note) and preserves the
    /// stated claim; the installer's installed-owner admission refuses before any export or write.
    /// </summary>
    [Fact]
    public async Task BaselineForeignDefinitionCannotGainExposureByForgedOwnClaim()
    {
        const string proposalId = "proposal-274-forged-claim";
        const string noteBody = """{"title":"Tenant note","envelope":{"exposes":{"interface_version":3}}}""";
        const string claimedBody = """{"id":"forms/invoice","envelope":{"exposes":{"interface_version":3}}}""";
        var baseline = _proposals.ReadEffective(_tenant);
        Assert.Equal(FinanceKey, Owner(baseline, "forms/invoice"));

        await StartAsync(proposalId);
        await AutosaveAsync(proposalId, "forms/tenant-note", noteBody, FormKind, ReleaseKey);
        await AutosaveAsync(proposalId, "forms/invoice", claimedBody, FormKind, ReleaseKey);
        var digest = await SaveCheckReleaseAsync(proposalId, ReleaseKey);
        var offer = Offer(digest);
        Assert.True(ConfigurationProposalStore.VerifyOffer(offer, new Ed25519Verifier()));
        var document = offer.Released.Document.ToArray();
        AssertBaselineUnchanged(baseline, document);

        using (var parsed = JsonDocument.Parse(document))
        {
            var root = parsed.RootElement;
            Assert.Equal(new[] { "forms/tenant-note" }, RootExposes(root));
            Assert.Equal(3, root.GetProperty("interfaceVersion").GetInt32());
            Assert.Equal(new[] { "acme.finance@1.0.0" }, Closure(root));
            Assert.Equal(ReleaseKey, Payload(root, "forms/invoice").GetProperty("packageKey").GetString());
        }

        // The transport sees two own-claimed items and a valid own exposure; ownership is the installer's call.
        var converted = ReleasedPackConversion.Convert(document, _node.Signer.IssuerId.ToBase64Url());
        Assert.Equal(new[] { "forms/invoice", "forms/tenant-note" },
            converted.Request!.Contents.Select(content => content.Key).ToArray());
        Assert.Equal(new[] { "forms/tenant-note" }, converted.Request.Exposes);
        Assert.Empty(converted.ForeignEdits);

        var before = Snapshot();
        var refused = await InstallAsync(digest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Equal("refused", refused.Body.GetProperty("status").GetString());
        var refusal = Assert.Single(refused.Body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("configuration-release-foreign-definition", refusal.GetProperty("code").GetString());
        Assert.Equal("forms/invoice", refusal.GetProperty("target").GetString());
        Assert.Contains("'acme.finance'", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.Equal(0, _exporter.ExportCount);
        Assert.Equal(before, Snapshot());
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == ReleaseKey);
        Assert.Null(_store.GetWatermark(_tenant, ReleaseKey));
        Assert.Empty(_store.GetOverrides(_tenant, FinanceKey));
        Assert.Equal(FinanceInvoiceSeed, Assert.Single(_store.GetActive(_tenant, FinanceKey)!.SeedItems).CanonicalJson);
    }

    /// <summary>
    /// Control 5. An authentic release of a forged own claim is altered afterwards to assert root exposure of
    /// acme.finance's key, and re-signed with the node's own (installer-trusted) signer so the offer verifies.
    /// A valid signature and a transport-valid exposure still do not admit ownership: the installed-owner
    /// refusal holds before any export or write.
    /// </summary>
    [Fact]
    public async Task SignedForgedExposureStillCannotOverrideInstalledOwnership()
    {
        const string proposalId = "proposal-274-resigned";
        const string claimedBody = """{"id":"forms/invoice","envelope":{"exposes":{"interface_version":3}}}""";
        var baseline = _proposals.ReadEffective(_tenant);
        Assert.Equal(FinanceKey, Owner(baseline, "forms/invoice"));

        await StartAsync(proposalId);
        await AutosaveAsync(proposalId, "forms/invoice", claimedBody, FormKind, ReleaseKey);
        var authenticDigest = await SaveCheckReleaseAsync(proposalId, ReleaseKey);
        var authentic = Offer(authenticDigest).Released.Document.ToArray();
        AssertBaselineUnchanged(baseline, authentic);
        using (var parsed = JsonDocument.Parse(authentic))
        {
            // The producer exposes nothing: the only exposed edit's canonical owner is acme.finance.
            Assert.False(parsed.RootElement.TryGetProperty("exposes", out _));
            Assert.False(parsed.RootElement.TryGetProperty("interfaceVersion", out _));
        }

        // The adversarial source: add the root ownership/exposure assertion the producer withheld.
        var forgedRoot = JsonNode.Parse(authentic)!.AsObject();
        forgedRoot["exposes"] = new JsonArray("forms/invoice");
        forgedRoot["interfaceVersion"] = 3;
        var forged = Encoding.UTF8.GetBytes(forgedRoot.ToJsonString());
        var forgedDigest = Convert.ToHexStringLower(SHA256.HashData(forged));
        Assert.NotEqual(authenticDigest, forgedDigest);
        await ResignAsync(authenticDigest, forged, forgedDigest);

        var forgedOffer = Offer(forgedDigest);
        Assert.True(ConfigurationProposalStore.VerifyOffer(forgedOffer, new Ed25519Verifier()));
        var converted = ReleasedPackConversion.Convert(forgedOffer.Released.Document.Span, _node.Signer.IssuerId.ToBase64Url());
        Assert.Equal(new[] { "forms/invoice" }, converted.Request!.Exposes);
        Assert.Equal(3, converted.Request.InterfaceVersion);

        var before = Snapshot();
        var refused = await InstallAsync(forgedDigest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        var refusal = Assert.Single(refused.Body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("configuration-release-foreign-definition", refusal.GetProperty("code").GetString());
        Assert.Equal("forms/invoice", refusal.GetProperty("target").GetString());
        Assert.Contains("'acme.finance'", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.Equal(0, _exporter.ExportCount);
        Assert.Equal(before, Snapshot());
        Assert.DoesNotContain(_store.ListInstalled(_tenant), item => item.PackKey == ReleaseKey);
        Assert.Empty(_store.GetOverrides(_tenant, FinanceKey));
    }

    /// <summary>
    /// Control 6. The effective baseline says tenant.release owns forms/owned-note; the edit states acme.finance.
    /// The producer selects the canonical owner for exposure and keeps the stated claim; the converter refuses the
    /// exposed foreign-claimed key, so nothing is exported or written and tenant.release stays as installed.
    /// </summary>
    [Fact]
    public async Task ContradictoryCanonicalOwnAndPayloadForeignExposureRefusesSafely()
    {
        const string proposalId = "proposal-274-contradiction";
        const string ownedSeed = """{"id":"forms/owned-note","pack":"tenant.release"}""";
        const string contradictoryBody = """{"id":"forms/owned-note","envelope":{"exposes":{"interface_version":3}}}""";
        Seed(ReleaseKey, "1.0.0", ("forms/owned-note", PackContentKind.FormDefinition, ownedSeed));
        var baseline = _proposals.ReadEffective(_tenant);
        Assert.Equal(ReleaseKey, Owner(baseline, "forms/owned-note"));

        await StartAsync(proposalId);
        await AutosaveAsync(proposalId, "forms/owned-note", contradictoryBody, FormKind, FinanceKey);
        var digest = await SaveCheckReleaseAsync(proposalId, ReleaseKey);
        var offer = Offer(digest);
        Assert.True(ConfigurationProposalStore.VerifyOffer(offer, new Ed25519Verifier()));
        var document = offer.Released.Document.ToArray();
        AssertBaselineUnchanged(baseline, document);

        using (var parsed = JsonDocument.Parse(document))
        {
            var root = parsed.RootElement;
            Assert.Equal(new[] { "forms/owned-note" }, RootExposes(root));
            Assert.Equal(3, root.GetProperty("interfaceVersion").GetInt32());
            Assert.Equal(new[] { "acme.finance@1.0.0" }, Closure(root));
            // The payload claim is preserved, not normalized to the canonical owner.
            Assert.Equal(FinanceKey, Payload(root, "forms/owned-note").GetProperty("packageKey").GetString());
        }

        var direct = Assert.Throws<ReleasedPackConversionException>(() =>
            ReleasedPackConversion.Convert(document, _node.Signer.IssuerId.ToBase64Url()));
        Assert.Equal(("configuration-release-foreign-definition", "forms/owned-note"), (direct.Code, direct.Target));

        var before = Snapshot();
        var refused = await InstallAsync(digest);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        var refusal = Assert.Single(refused.Body.GetProperty("refusals").EnumerateArray());
        Assert.Equal("configuration-release-foreign-definition", refusal.GetProperty("code").GetString());
        Assert.Equal("forms/owned-note", refusal.GetProperty("target").GetString());
        // The converter's exposure refusal, not the installer's installed-owner refusal.
        Assert.Contains("cannot expose", refusal.GetProperty("message").GetString(), StringComparison.Ordinal);

        Assert.Equal(0, _exporter.ExportCount);
        Assert.Equal(before, Snapshot());
        var release = Assert.Single(_store.ListInstalled(_tenant), item => item.PackKey == ReleaseKey);
        Assert.Equal(("1.0.0", PackLifecycleState.Active), (release.Version, release.Lifecycle));
        Assert.Equal(ownedSeed, Assert.Single(release.SeedItems).CanonicalJson);
        Assert.Empty(_store.GetOverrides(_tenant, ReleaseKey));
        Assert.Empty(_store.GetOverrides(_tenant, FinanceKey));
    }

    /// <summary>
    /// Control 7. A live release whose bodies declare no exposure (one carries an envelope without exposes)
    /// emits neither root property; conversion, the signed pack manifest and the installed pack keep null/null.
    /// </summary>
    [Fact]
    public async Task RealLegacyReleaseWithoutExposureRetainsAbsentFields()
    {
        const string proposalId = "proposal-274-legacy";
        const string plainBody = """{"title":"Plain"}""";
        const string envelopedBody = """{"title":"Enveloped","envelope":{"requires":[]}}""";
        var baseline = _proposals.ReadEffective(_tenant);

        await StartAsync(proposalId);
        await AutosaveAsync(proposalId, "forms/plain", plainBody, FormKind, ReleaseKey);
        await AutosaveAsync(proposalId, "forms/enveloped", envelopedBody, FormKind, ReleaseKey);
        var digest = await SaveCheckReleaseAsync(proposalId, ReleaseKey);
        var offer = Offer(digest);
        Assert.True(ConfigurationProposalStore.VerifyOffer(offer, new Ed25519Verifier()));
        var document = offer.Released.Document.ToArray();
        AssertBaselineUnchanged(baseline, document);

        using (var parsed = JsonDocument.Parse(document))
        {
            Assert.False(parsed.RootElement.TryGetProperty("exposes", out _));
            Assert.False(parsed.RootElement.TryGetProperty("interfaceVersion", out _));
            Assert.Empty(Closure(parsed.RootElement));
        }

        var request = ReleasedPackConversion.Convert(document, _node.Signer.IssuerId.ToBase64Url()).Request!;
        Assert.Null(request.Exposes);
        Assert.Null(request.InterfaceVersion);

        var installed = await InstallAsync(digest);
        Assert.Equal(HttpStatusCode.OK, installed.Status);
        var manifest = new PackVerifier(new Ed25519Verifier(), _codec).Verify(Assert.Single(_exporter.Exported), OwnRoster()).Manifest!;
        Assert.Null(manifest.Exposes);
        Assert.Null(manifest.InterfaceVersion);
        var pack = Assert.Single(_store.ListInstalled(_tenant), item => item.PackKey == ReleaseKey);
        Assert.Null(pack.Exposes);
        Assert.Null(pack.InterfaceVersion);
        Assert.Equal(new[] { "forms/enveloped", "forms/plain" },
            pack.SeedItems.Select(item => item.Key).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Compare the entire produced body with an authored literal, including unexpected members.</summary>
    private static void AssertBody(string expectedLiteral, JsonNode? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedLiteral), actual),
            $"The produced body differs from the complete literal: {expectedLiteral}");

    private static string InstallRoute(string digest) => $"/api/local-node/configuration/releases/{digest}/install";

    private static string Proposal(string proposalId) => $"/api/local-node/configuration/proposals/{proposalId}";

    private async Task StartAsync(string proposalId)
    {
        using var started = await _client.PostAsJsonAsync(ConfigurationProposalRoutes.ProposalsRoute, new { proposalId });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
    }

    private async Task AutosaveAsync(string proposalId, string definitionKey, string bodyJson, string contentKind, string owner)
    {
        using var response = await _client.PutAsJsonAsync($"{Proposal(proposalId)}/edits",
            new { definitionKey, packageKey = owner, bodyJson, contentKind });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<string> SaveCheckReleaseAsync(string proposalId, string packageKey)
    {
        using (var saved = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/versions", new { rationale = Rationale }))
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        using (var checked_ = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/checks", new { receiptId = "receipt-274" }))
            Assert.Equal(HttpStatusCode.OK, checked_.StatusCode);
        using var released = await _client.PostAsJsonAsync($"{Proposal(proposalId)}/release",
            new { ordinal = 1, packageKey, revision = Revision });
        var body = await released.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(released.StatusCode == HttpStatusCode.OK, body.ToString());
        return body.GetProperty("releasedPackage").GetProperty("digest").GetString()!;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> InstallAsync(string digest)
    {
        using var response = await _client.PostAsJsonAsync(InstallRoute(digest), new { });
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    private HostReleasedPackage Offer(string digest) =>
        Assert.Single(_proposals.Offered(_tenant), offer => offer.Released.Digest == digest);

    private IPackTrustStore OwnRoster() => new InMemoryPackTrustStore(
        [new PackTrustRoot(TrustScope.OwnRoster, _node.Signer.IssuerId, PackComposerRoutes.OwnRosterEpoch, TrustRootStatus.Current)]);

    /// <summary>
    /// Replaces the stored released bytes and signs the replacement with the node signer over exactly the subject
    /// the release path signs, so the offer's own verification passes. Only the bytes and signature change.
    /// </summary>
    private async Task ResignAsync(string authenticDigest, byte[] forged, string forgedDigest)
    {
        using var context = _db.CreateContext();
        var row = context.ReleasedPackages.Single(candidate => candidate.Digest == authenticDigest);
        var signed = await _node.Signer.SignAsync(new ReleasedPackageSubject(forgedDigest, row.PackageKey, row.Revision,
            row.SavedVersionDigest, row.BaselineDigest, row.Tenant, row.ReleasedBy), Frozen, Guid.NewGuid());
        row.Document = forged;
        row.SignatureJson = JsonSerializer.Serialize(signed, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        context.SaveChanges();
    }

    /// <summary>The effective generation did not move, and the release's own package record names it.</summary>
    private void AssertBaselineUnchanged(ConfigurationGeneration baseline, byte[] document)
    {
        Assert.Equal(baseline.Digest, _proposals.ReadEffective(_tenant).Digest);
        using var parsed = JsonDocument.Parse(document);
        var record = parsed.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("content").GetProperty("payload"))
            .Single(payload => payload.ValueKind == JsonValueKind.Object && !payload.TryGetProperty("definitionKey", out _));
        Assert.Equal(baseline.Digest, record.GetProperty("baselineDigest").GetString());
    }

    private static string? Owner(ConfigurationGeneration generation, string definitionKey) =>
        generation.References.GetProperty("ownership").EnumerateArray()
            .Where(owner => owner.GetProperty("definitionKey").GetString() == definitionKey)
            .Select(owner => owner.GetProperty("packageKey").GetString())
            .SingleOrDefault();

    private static string?[] RootExposes(JsonElement root) =>
        root.GetProperty("exposes").EnumerateArray().Select(item => item.GetString()).ToArray();

    private static string[] Closure(JsonElement root) =>
        root.GetProperty("closure").GetProperty("dependencies").EnumerateArray()
            .Select(item => $"{item.GetProperty("key").GetString()}@{item.GetProperty("version").GetString()}").ToArray();

    private static JsonElement Payload(JsonElement root, string definitionKey) =>
        root.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("content").GetProperty("payload"))
            .Single(payload => payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("definitionKey", out var key) && key.GetString() == definitionKey);

    /// <summary>Every installed version, seed, override, watermark and key-ownership row, as comparable text.</summary>
    private string Snapshot()
    {
        var lines = new List<string>();
        var packs = _store.ListInstalled(_tenant).OrderBy(pack => pack.PackKey, StringComparer.Ordinal)
            .ThenBy(pack => pack.Version, StringComparer.Ordinal).ToArray();
        foreach (var pack in packs)
        {
            lines.Add($"pack {pack.PackKey}@{pack.Version} {pack.Lifecycle} exposes={string.Join(",", pack.Exposes ?? Array.Empty<string>())} interface={pack.InterfaceVersion}");
            lines.AddRange(pack.SeedItems.Select(item => $"seed {pack.PackKey}@{pack.Version} {item.Key} {item.Kind} {item.Version} {item.CanonicalJson}"));
        }
        foreach (var key in packs.Select(pack => pack.PackKey).Append(ReleaseKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            lines.AddRange(_store.GetOverrides(_tenant, key).Select(item => $"override {key} {item.ContentKey} {item.OverlayPatch.ToJsonString()}"));
            var watermark = _store.GetWatermark(_tenant, key);
            lines.Add(watermark is null ? $"watermark {key} none"
                : $"watermark {key} {watermark.Version} {string.Join(",", watermark.Floors.OrderBy(floor => floor.Key, StringComparer.Ordinal).Select(floor => $"{floor.Key}={floor.Value}"))}");
        }
        lines.AddRange(_store.GetKeyOwnership(_tenant).OrderBy(owner => owner.Key, StringComparer.Ordinal)
            .Select(owner => $"owner {owner.Key} {owner.Value}"));
        return string.Join("\n", lines);
    }

    private void Seed(string packKey, string version, params (string Key, PackContentKind Kind, string Json)[] items)
    {
        var seeds = items.Select(item => new PackSeedItem(item.Key, item.Kind, "1", item.Json,
            Cid.FromBytes(Encoding.UTF8.GetBytes(item.Json)))).ToArray();
        var pack = new InstalledPack(packKey, version, PackScopeTier.Horizontal, PackLifecycleState.Draft, seeds,
            new Dictionary<string, int>(), Frozen, PrincipalId.FromBytes(new byte[PrincipalId.LengthInBytes]), 1,
            TrustScope.OwnRoster, Array.Empty<PackDependencyRef>());
        _store.Commit(new PackInstallTransaction(_tenant, pack, new PackInstallWatermark(packKey, version, new Dictionary<string, int>()), []));
        _store.Activate(_tenant, packKey, version);
    }

    /// <summary>Observes the real exporter: counts calls and keeps each signed file; never alters behavior.</summary>
    private sealed class CountingPackExporter(IPackExporter inner) : IPackExporter
    {
        private readonly List<byte[]> _exported = [];

        public int ExportCount { get; private set; }

        public IReadOnlyList<byte[]> Exported => _exported;

        public ValueTask<PackValidationResult> ValidateAsync(PackExportRequest request, CancellationToken ct = default) =>
            inner.ValidateAsync(request, ct);

        public async ValueTask<PackExportOutcome> ExportAsync(PackExportRequest request, IOperationSigner signer,
            CancellationToken ct = default)
        {
            ExportCount++;
            var outcome = await inner.ExportAsync(request, signer, ct).ConfigureAwait(false);
            if (outcome.FileBytes is not null) _exported.Add(outcome.FileBytes.ToArray());
            return outcome;
        }
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
