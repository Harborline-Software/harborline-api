using System.Text.Json.Nodes;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;
using Xunit;
using NSubstitute;
using Microsoft.Extensions.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.DependencyInjection;
using Harborline.Api.Blocks.Assets.Registry.Services;
using Harborline.Api.LocalNodeHost.Data.Compose;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class RecordsTypeReferenceExtractionTests
{
    // Oracles: T-615 extractor Q7/Q8 and the Platform Goal 3 P1 signed fixture (3.1.4, store digest, interface 1).
    private const string Digest = "sha256:ca09327e39bb416e6a5e7025adf6e6c2ecf9967bcc2b48122ed14d1eb99e6caf";
    private static readonly PackDependencyRef[] Dependencies = [new("eam-core", "7.0.0")];
    internal static string Body(string target = "target_type_id", string id = "eam.asset-class", string pinId = "eam.asset-class") =>
        $$$$"""{"record_type_id":"consumer.work","name":"Work","class_id":"consumer.assets","record_class":"master","envelope":{"tenant":"tenant-a","section":"consumer","contract":{"major":1,"minor":0},"package_id":"consumer","requires":[{"capability":"eam-core@1"}]},"fields":[{"field_key":"asset","display_name":"Asset","reference":{"{{{{target}}}}":"{{{{id}}}}","cardinality":"one","on_delete":"block","parent":true,"pin":{"package_id":"eam-core","definition_id":"{{{{pinId}}}}","version":"3.1.4","digest":"{{{{Digest}}}}","interface_version":1}}}]}""";

    private static PackExportRequest Request(string body) => new("consumer", "1.0.0", "Consumer", "Reference proof",
        PackScopeTier.Horizontal, [new("consumer.work", PackContentKind.AssetTypeDefinition, "1.0.0", JsonNode.Parse(body)!)],
        Dependencies, [], 1, Dcp: DomainComplianceProfile.General("test-author"));

    private static PackExporter Exporter() => new(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
        new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
        new PackFileCodec(), TimeProvider.System);

    [Fact(DisplayName = "T-615 T-EXACT: a Records parent reference signs the exact verified producer content key as References")]
    public async Task Exact_producer_joins_definition_identity_to_content_key()
    {
        using var key = KeyPair.Generate();
        var outcome = await Exporter().ExportAsync(Request(Body()) with
        { VerifiedProducers = [RecordsInstallClosureGateTests.AssetClassProducer()] }, new Ed25519Signer(key));
        Assert.True(outcome.Succeeded, string.Join("; ", outcome.Validation.Errors));
        var edge = Assert.Single(outcome.File!.Envelope!.Payload.Manifest.ContentReferences!);
        Assert.Equal(("consumer.work", "eam-core", "records/eam.asset-class", "References"),
            (edge.FromContentKey, edge.ToPackKey, edge.ToContentKey, edge.Relation.ToString()));
    }

    [Fact(DisplayName = "T-615 Q7: a missing producer snapshot refuses rather than guessing from a namespace")]
    public async Task Missing_snapshot_refuses()
    {
        var validation = await Exporter().ValidateAsync(Request(Body()));
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.Code == "records.reference.closure_missing"
            && error.Target == "/contents/0/contentBase64/fields/0/reference/pin");
    }

    [Theory(DisplayName = "T-615 T-CLASS/T-ORDER: Class targets refuse and pin_stale precedes Class closure refusal")]
    [InlineData("eam.asset-class", "records.reference.closure_missing", "/pin")]
    [InlineData("other.type", "records.reference.pin_stale", "")]
    public async Task Class_refusal_preserves_first_match(string pinId, string code, string suffix)
    {
        var validation = await Exporter().ValidateAsync(Request(Body("target_class_id", pinId: pinId)) with
        { VerifiedProducers = [RecordsInstallClosureGateTests.AssetClassProducer()] });
        var error = Assert.Single(validation.Errors);
        Assert.Equal((code, "/contents/0/contentBase64/fields/0/reference" + suffix), (error.Code, error.Target));
    }

    [Theory(DisplayName = "T-615 Q7: exact producer facts are required; latest, ambiguity and altered facts refuse")]
    [InlineData("latest")]
    [InlineData("ambiguous")]
    [InlineData("digest")]
    [InlineData("interface")]
    [InlineData("version")]
    public async Task Inexact_producer_refuses(string change)
    {
        var producer = RecordsInstallClosureGateTests.AssetClassProducer();
        InstalledPack[] installed = change switch
        {
            "latest" => [producer with { Version = "7.1.0" }],
            "ambiguous" => [producer, producer],
            "digest" => [producer with { ExposedDefinitions = [new("records/eam.asset-class", "3.1.4", "sha256:" + new string('0', 64))] }],
            "interface" => [producer with { InterfaceVersion = 2 }],
            _ => [producer with { ExposedDefinitions = [new("records/eam.asset-class", "3.1.5", Digest)] }],
        };
        var validation = await Exporter().ValidateAsync(Request(Body()) with { VerifiedProducers = installed });
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, e => e.Code is "records.reference.closure_missing" or "records.reference.closure_changed");
    }
    [Theory(DisplayName = "T-615 T-MAL/T-CLASS/T-ORDER: signed Records references refuse missing edges and preserve Class pin precedence")]
    [InlineData("target_type_id", "eam.asset-class", "records.reference.closure_missing", "")]
    [InlineData("target_class_id", "eam.asset-class", "records.reference.closure_missing", "/pin")]
    [InlineData("target_class_id", "other.type", "records.reference.pin_stale", "")]
    public async Task Install_refuses_missing_signed_edge(string target, string pinId, string code, string suffix)
    {
        using var key = KeyPair.Generate();
        var tenant = new TenantId("tenant-a");
        var producer = RecordsInstallClosureGateTests.AssetClassProducer();
        var store = new InMemoryPackInstallStore();
        store.Commit(new PackInstallTransaction(tenant, producer, new PackInstallWatermark("eam-core", "7.0.0", new Dictionary<string, int>()), []));
        var codec = new PackFileCodec();
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), codec), store,
            new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(), TestAuthorization.AllowGate())
        { RecordsAdmission = RecordsInstallClosureGateTests.Records().AdmitInstallAsync };
        var context = new PackInstallContext(tenant,
            new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, key.PrincipalId, 1, TrustRootStatus.Current)]),
            PackRevocationList.Empty, AdmittedInstant.FromRecordedAct(DateTimeOffset.FromUnixTimeSeconds(1791633600)), TimeSpan.FromDays(30), Principal: "test-operator");
        var hostile = await new RecordsTypeReferenceUncheckedExporter().ExportAsync(Request(Body(target, pinId: pinId)), new Ed25519Signer(key));

        var outcome = await installer.InstallAsync(hostile.FileBytes!, context);

        Assert.False(outcome.Installed);
        Assert.Equal(code, Assert.Single(outcome.Preview.AdmissionRefusals).Code);
        Assert.Equal("/fields/0/reference" + suffix, Assert.Single(outcome.Preview.AdmissionRefusals).Pointer);
        Assert.Null(store.GetVersion(tenant, "consumer", "1.0.0"));
    }

    [Fact(DisplayName = "T-615: duplicate and self references keep one located outcome per occurrence; identity collisions refuse")]
    public void Internal_join_is_exact_and_located()
    {
        var body = JsonNode.Parse(Body())!;
        var reference = body["fields"]![0]!["reference"]!;
        reference.AsObject().Remove("pin");
        reference["target_type_id"] = "consumer.work";
        reference["parent"] = false;
        var second = body["fields"]![0]!.DeepClone();
        second["field_key"] = "other";
        body["fields"]!.AsArray().Add(second);
        var source = new PackContentSource("consumer.work", PackContentKind.AssetTypeDefinition, "1.0.0", body);

        var outcomes = PackContentReferenceDeriver.ResolveRecords("consumer", [source], [], null);

        Assert.Equal(["/fields/0/reference", "/fields/1/reference"], outcomes.Select(result => result.Pointer));
        Assert.All(outcomes, result =>
        {
            Assert.Equal("consumer.work", result.InternalContentKey);
            Assert.Null(result.Edge);
            Assert.Null(result.RefusalCode);
        });
        var collision = PackContentReferenceDeriver.ResolveRecords("consumer", [source, source with { Key = "consumer.alias" }], [], null);
        Assert.Equal(4, collision.Count);
        Assert.All(collision, result => Assert.Equal("records.reference.target_unresolved", result.RefusalCode));
    }

    [Fact(DisplayName = "T-615 graph: declared Records edges survive; gated bodies never use the graph's fallback resolver")]
    public void Graph_reads_signed_records_edges_and_skips_gated_bodies()
    {
        var content = new PackContentCanonicalizer().Canonicalize(new PackContentSource("consumer.work",
            PackContentKind.AssetTypeDefinition, "1.0.0", JsonNode.Parse("{\"record_type_id\":\"consumer.work\",\"parentType\":\"untrusted.target\"}")!));
        var pack = RecordsInstallClosureGateTests.AssetClassProducer() with
        {
            PackKey = "consumer",
            SeedItems = [new PackSeedItem(content.Key, content.Kind, content.Version,
                System.Text.Encoding.UTF8.GetString(content.CanonicalBytes.Span), content.ContentAddress)],
            ContentReferences = [new("consumer.work", PackContentKind.AssetTypeDefinition, "eam-core", "records/eam.asset-class",
                PackContentKind.AssetTypeDefinition, PackFeatureEdgeRelation.References)],
        };
        var edge = Assert.Single(PackContentEdgeIndexBuilder.Build([pack]).ContentEdges);
        Assert.Equal(("eam-core", "records/eam.asset-class", "References"), (edge.ToPackKey, edge.ToContentKey, edge.Relation.ToString()));
        Assert.Empty(PackContentEdgeIndexBuilder.Build([pack with { ContentReferences = null }]).ContentEdges);
    }

    [Fact(DisplayName = "T-615 Q7: the affirmed compose ceremony passes its tenant's installed snapshot to the exporter")]
    public async Task Compose_ceremony_supplies_the_installed_snapshot()
    {
        using var provider = new ServiceCollection().AddLogging().AddInMemoryAssetTypeSystem().BuildServiceProvider();
        var registry = provider.GetRequiredService<IEntityTypeRegistry>();
        GeneralPackFixture.SeedInto(registry);
        var tenant = new TenantId("compose-test");
        var packs = Substitute.For<IPackInstallStore>();
        packs.ListInstalled(tenant).Returns([RecordsInstallClosureGateTests.AssetClassProducer()]);
        var ceremony = new ComposeCeremony(registry, new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new InMemoryDraftCompositionStore(), clock: TimeProvider.System, packs: packs);
        var composed = await ceremony.ComposeAsync(new ComposeRequest(null, GeneralPackFixture.PackKey,
            GeneralPackFixture.PackVersion, "General", "Snapshot proof", PackScopeTier.Horizontal,
            GeneralPackFixture.TypeIds(), DomainComplianceProfile.General("test-author")), tenant);
        Assert.Equal(ComposeStatus.Ok, composed.Status);
        Assert.Equal(ComposeStatus.Ok, ceremony.Affirm(composed.Draft!.ComposeId, tenant, composed.Draft.SnapshotHash).Status);
        var exporter = Substitute.For<IPackExporter>();
        exporter.ExportAsync(Arg.Any<PackExportRequest>(), Arg.Any<IOperationSigner>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var request = call.Arg<PackExportRequest>();
                Assert.Equal(("eam-core", "7.0.0"),
                    (Assert.Single(request.VerifiedProducers!).PackKey, Assert.Single(request.VerifiedProducers!).Version));
                return Exporter().ExportAsync(request, call.Arg<IOperationSigner>(), call.Arg<CancellationToken>());
            });
        using var key = KeyPair.Generate();
        var result = await ceremony.ExportAsync(composed.Draft.ComposeId, tenant, exporter, new Ed25519Signer(key), 1);
        Assert.Equal(ComposeStatus.Ok, result.Status);
        packs.Received(1).ListInstalled(tenant);
    }

}
