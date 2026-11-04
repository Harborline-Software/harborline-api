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
    [InlineData("target_type_id", "collision", "records.reference.target_unresolved", "/target_type_id")]
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
        var request = Request(Body(target, pinId: pinId));
        if (pinId == "collision")
        {
            var source = request.Contents[0];
            var reference = source.Content["fields"]![0]!["reference"]!;
            reference.AsObject().Remove("pin");
            reference["target_type_id"] = "consumer.work";
            reference["parent"] = false;
            request = request with { Contents = [source, source with { Key = "consumer.alias" }] };
        }
        var hostile = await new RecordsTypeReferenceUncheckedExporter().ExportAsync(request, new Ed25519Signer(key));

        var outcome = await installer.InstallAsync(hostile.FileBytes!, context);

        Assert.False(outcome.Installed);
        Assert.Equal(pinId == "collision" ? 2 : 1, outcome.Preview.AdmissionRefusals.Count);
        Assert.All(outcome.Preview.AdmissionRefusals, refusal =>
            Assert.Equal((code, "/fields/0/reference" + suffix), (refusal.Code, refusal.Pointer)));
        Assert.Null(store.GetVersion(tenant, "consumer", "1.0.0"));
        var unverified = installer.Check([], context);
        Assert.Equal(PackInstallVerdict.Refused, unverified.Verdict);
        Assert.Single(unverified.Refusals);
        Assert.DoesNotContain("pack.install.refused.records_admission_unsupported", unverified.RefusalCodes);
    }

    [Theory(DisplayName = "T-615 CWE-345: a signed edge from a Records item that its body does not declare is refused at the item")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Install_refuses_extra_signed_records_edge(bool extra)
    {
        // Oracle: the T-EXACT literal edge the body derives; the extra edge targets the same installed item as ParentOf,
        // so it passes the unmet-reference check and only the Records body comparison can refuse it.
        PackContentReferenceEdge Edge(PackFeatureEdgeRelation relation) => new("consumer.work", PackContentKind.AssetTypeDefinition,
            "eam-core", "records/eam.asset-class", PackContentKind.AssetTypeDefinition, relation);
        using var key = KeyPair.Generate();
        var tenant = new TenantId("tenant-a");
        var store = new InMemoryPackInstallStore();
        store.Commit(new PackInstallTransaction(tenant, RecordsInstallClosureGateTests.AssetClassProducer(),
            new PackInstallWatermark("eam-core", "7.0.0", new Dictionary<string, int>()), []));
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), new PackFileCodec()), store,
            new WorkflowRefusingPackContentAdmission(), new InMemoryPackInstallAudit(), TestAuthorization.AllowGate())
        { RecordsAdmission = RecordsInstallClosureGateTests.Records().AdmitInstallAsync };
        var context = new PackInstallContext(tenant,
            new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, key.PrincipalId, 1, TrustRootStatus.Current)]),
            PackRevocationList.Empty, AdmittedInstant.FromRecordedAct(DateTimeOffset.FromUnixTimeSeconds(1791633600)), TimeSpan.FromDays(30), Principal: "test-operator");
        PackContentReferenceEdge[] signed = extra
            ? [Edge(PackFeatureEdgeRelation.References), Edge(PackFeatureEdgeRelation.ParentOf)]
            : [Edge(PackFeatureEdgeRelation.References)];
        var file = await new RecordsTypeReferenceUncheckedExporter(signed).ExportAsync(Request(Body()), new Ed25519Signer(key));

        var outcome = await installer.InstallAsync(file.FileBytes!, context);

        if (!extra)
        {
            Assert.True(outcome.Installed, string.Join("; ", outcome.Preview.Refusals));
            return;
        }
        Assert.False(outcome.Installed);
        var refusal = Assert.Single(outcome.Preview.AdmissionRefusals);
        Assert.Equal(("consumer.work", "pack.install.refused.records_edge_undeclared", ""), (refusal.ContentKey, refusal.Code, refusal.Pointer));
        var located = Assert.Single(outcome.Preview.Refusals);
        Assert.Equal(("pack.install.refused.admission", "/contents/0/contentBase64"), (located.Code, located.Pointer));
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

    [Theory(DisplayName = "T-615: every unpinned target has a located internal outcome or refusal")]
    [InlineData("target_type_id", "records.reference.target_unresolved", "/target_type_id")]
    [InlineData("target_class_id", "records.reference.target_unresolved", "/target_class_id")]
    public async Task Unpinned_external_targets_refuse(string member, string code, string suffix)
    {
        var request = Request(Body(member));
        request.Contents[0].Content["fields"]![0]!["reference"]!.AsObject().Remove("pin");
        var validation = await Exporter().ValidateAsync(request);
        var error = Assert.Single(validation.Errors);
        Assert.Equal((code, "/contents/0/contentBase64/fields/0/reference" + suffix), (error.Code, error.Target));
    }

    [Theory(DisplayName = "T-615: dependency declarations are checked by exact package and interface")]
    [InlineData("mixed", null)]
    [InlineData("absent", "records.reference.dependency_undeclared")]
    [InlineData("missing", "records.reference.dependency_undeclared")]
    [InlineData("wrong", "records.reference.interface_incompatible")]
    [InlineData("prefix", "records.reference.dependency_undeclared")]
    [InlineData("self", "records.reference.pin_unexpected")]
    public async Task Reference_declarations_are_exact(string declaration, string? code)
    {
        var request = Request(Body()) with { VerifiedProducers = [RecordsInstallClosureGateTests.AssetClassProducer()] };
        request.Contents[0].Content["envelope"]!["requires"] = declaration switch
        {
            "mixed" => JsonNode.Parse("[{\"capability\":\"eam-core@1\"},{\"capability\":\"other@1\"}]"),
            "wrong" => JsonNode.Parse("[{\"capability\":\"eam-core@2\"}]"),
            "prefix" => JsonNode.Parse("[{\"capability\":\"eam-core-extra@1\"}]"),
            _ => new JsonArray(),
        };
        if (declaration == "missing") request.Contents[0].Content["envelope"]!.AsObject().Remove("requires");
        if (declaration == "self") request.Contents[0].Content["fields"]![0]!["reference"]!["pin"]!["package_id"] = "consumer";
        var validation = await Exporter().ValidateAsync(request);
        if (code is null) Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
        else
        {
            var error = Assert.Single(validation.Errors);
            Assert.Equal((code, "/contents/0/contentBase64/fields/0/reference" + (declaration == "self" ? "/pin" : "")),
                (error.Code, error.Target));
        }
    }

    [Theory(DisplayName = "T-615 T-MAL: malformed Records claims refuse at export before signing")]
    [InlineData(false, "records.document_invalid", "")]
    [InlineData(true, "pack.install.refused.records_unpublished_refusal", "/record_type_id")]
    public async Task Malformed_records_export_refuses(bool missingId, string code, string suffix)
    {
        var request = Request(Body());
        var body = request.Contents[0].Content;
        if (missingId)
        {
            body.AsObject().Remove("record_type_id");
            body["fields"]!.AsArray().Insert(0, JsonNode.Parse("{\"field_key\":\"plain\",\"display_name\":\"Plain\"}"));
        }
        else body["record_type_id"] = 7;
        var validation = await Exporter().ValidateAsync(request);
        var error = Assert.Single(validation.Errors);
        Assert.Equal((code, "/contents/0/contentBase64" + suffix), (error.Code, error.Target));
    }

    [Fact(DisplayName = "T-615: another signed exposure of the same producer cannot make the exact target ambiguous")]
    public async Task Unrelated_exposure_does_not_replace_the_exact_target()
    {
        var producer = RecordsInstallClosureGateTests.AssetClassProducer();
        var seed = producer.SeedItems[0];
        var other = new PackContentCanonicalizer().Canonicalize(new PackContentSource("records/eam.other", seed.Kind,
            seed.Version, JsonNode.Parse(seed.CanonicalJson.Replace("eam.asset-class", "eam.other", StringComparison.Ordinal))!));
        producer = producer with
        {
            SeedItems = [seed, new(other.Key, other.Kind, other.Version, System.Text.Encoding.UTF8.GetString(other.CanonicalBytes.Span), other.ContentAddress)],
            Exposes = ["records/eam.asset-class", "records/eam.other"],
            ExposedDefinitions = [new("records/eam.asset-class", "3.1.4", Digest), new("records/eam.other", "3.1.4", Digest)],
        };
        var validation = await Exporter().ValidateAsync(Request(Body()) with { VerifiedProducers = [producer] });
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact(DisplayName = "T-615 T-ORDER/K9: a Records claim never suppresses the legacy extractor's parentType refusal")]
    public async Task Records_claim_does_not_skip_legacy_extract_or_change_K9_precedence()
    {
        var request = Request(Body());
        request.Contents[0].Content["parentType"] = "unprovided.type";
        var extracted = PackContentReferenceExtractor.Extract(PackContentKind.AssetTypeDefinition, request.Contents[0].Content);
        Assert.Equal(["unprovided.type|ParentOf"], extracted.Select(reference => reference.ToContentKey + "|" + reference.Relation));
        var validation = await Exporter().ValidateAsync(request);
        Assert.Equal(("pack.validation.reference.dependency_undeclared", "consumer.work"),
            (validation.Errors[0].Code, validation.Errors[0].Target));
        Assert.Contains(validation.Errors, error => error.Code == "records.document_invalid");
    }

    [Fact(DisplayName = "T-615: signed Records edges are ordinal and deduplicated while every source reference keeps its pointer")]
    public async Task Signed_records_edges_are_ordered_and_deduplicated()
    {
        var producer = RecordsInstallClosureGateTests.AssetClassProducer();
        var seed = producer.SeedItems[0];
        var otherContent = new PackContentCanonicalizer().Canonicalize(new PackContentSource("records/eam.other", seed.Kind,
            seed.Version, JsonNode.Parse(seed.CanonicalJson.Replace("eam.asset-class", "eam.other", StringComparison.Ordinal))!));
        var other = new PackSeedItem(otherContent.Key, otherContent.Kind, otherContent.Version,
            System.Text.Encoding.UTF8.GetString(otherContent.CanonicalBytes.Span), otherContent.ContentAddress);
        producer = producer with { SeedItems = [seed, other], Exposes = [seed.Key, other.Key],
            ExposedDefinitions = [new(seed.Key, "3.1.4", Digest), new(other.Key, "3.1.4", Digest)] };
        var body = JsonNode.Parse(Body())!;
        var fields = body["fields"]!.AsArray();
        var first = fields[0]!.DeepClone();
        first["reference"]!["target_type_id"] = "eam.other";
        first["reference"]!["pin"]!["definition_id"] = "eam.other";
        first["field_key"] = "other";
        first["reference"]!["parent"] = false;
        fields.Insert(0, first);
        var duplicate = fields[1]!.DeepClone();
        duplicate["field_key"] = "again";
        duplicate["reference"]!["parent"] = false;
        fields.Add(duplicate);
        var another = first.DeepClone();
        another["field_key"] = "another";
        another["reference"]!["pin"]!["package_id"] = "aaa";
        fields.Add(another);
        body["envelope"]!["requires"]!.AsArray().Add(new JsonObject { ["capability"] = "aaa@1" });
        var secondProducer = producer with { PackKey = "aaa", SeedItems = [other], Exposes = [other.Key],
            ExposedDefinitions = [new(other.Key, "3.1.4", Digest)] };
        var source = new PackContentSource("consumer.z", PackContentKind.AssetTypeDefinition, "1.0.0", body);
        var second = source with { Key = "consumer.a", Content = body.DeepClone() };
        second.Content["record_type_id"] = "consumer.a";
        var request = Request(Body()) with { Contents = [source, second], VerifiedProducers = [producer, secondProducer],
            Dependencies = [new("eam-core", "7.0.0", []), new("aaa", "7.0.0", [])] };
        var resolved = PackContentReferenceDeriver.ResolveRecords("consumer", request.Contents, request.Dependencies, request.VerifiedProducers);
        Assert.Equal(["consumer.z/fields/0/reference", "consumer.z/fields/1/reference", "consumer.z/fields/2/reference",
            "consumer.z/fields/3/reference", "consumer.a/fields/0/reference", "consumer.a/fields/1/reference",
            "consumer.a/fields/2/reference", "consumer.a/fields/3/reference"],
            resolved.Select(reference => reference.ContentKey + reference.Pointer));
        using var key = KeyPair.Generate();
        var outcome = await Exporter().ExportAsync(request, new Ed25519Signer(key));
        Assert.True(outcome.Succeeded, string.Join("; ", outcome.Validation.Errors));
        Assert.Equal(["consumer.a|aaa|records/eam.other", "consumer.a|eam-core|records/eam.asset-class", "consumer.a|eam-core|records/eam.other",
            "consumer.z|aaa|records/eam.other", "consumer.z|eam-core|records/eam.asset-class", "consumer.z|eam-core|records/eam.other"],
            outcome.File!.Envelope!.Payload.Manifest.ContentReferences!.Select(edge => edge.FromContentKey + "|" + edge.ToPackKey + "|" + edge.ToContentKey));
    }

    [Fact(DisplayName = "T-615 legacy bytes: an empty reference set remains absent in the signed manifest")]
    public async Task Empty_references_are_omitted()
    {
        var request = Request("{\"displayName\":\"Legacy\"}") with { Dependencies = [] };
        using var key = KeyPair.Generate();
        var outcome = await Exporter().ExportAsync(request, new Ed25519Signer(key));
        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.File!.Envelope!.Payload.Manifest.ContentReferences);
        var canonical = System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(outcome.File.Envelope.Payload.Manifest));
        Assert.DoesNotContain("contentReferences", canonical, StringComparison.Ordinal);
    }

}
