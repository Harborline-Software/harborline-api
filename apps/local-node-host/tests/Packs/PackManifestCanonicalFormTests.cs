using System.Text;
using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Blobs;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Goal 3 P3 SIG-1: the signed manifest gains an optional <see cref="PackManifest.ExposedDefinitions"/> member that is
/// omitted when null, so an unbound manifest keeps its canonical bytes and content address and every existing signature
/// still verifies, while a bound manifest signs its entries, verifies and round-trips.
/// </summary>
/// <remarks>
/// Oracles: <see cref="UnboundCanonical"/>, <see cref="BoundCanonical"/> and their content addresses were produced by a
/// separate reference implementation of the canonical form (ordinal-sorted member names, no whitespace, nulls written
/// unless the member is omitted when null, enums as numbers) and of the CID v1 raw SHA-256 base32 address, not by the
/// code under test. The exposed-definition entry is the Platform Goal 3 P1 literal (catalogue version 3.1.4 and its
/// store digest); the Meter literals are the P0 fixture's.
/// <para>
/// These literals are synthetic. The C1 byte-identity evidence on real artifacts is in
/// <c>_shared/conformance/packs/access-replacement/</c>, whose manifests carry <c>exposes</c> and
/// <c>interfaceVersion</c> and no exposed-definition entries. The C1 guard is the SIGNED exports 1.1.4 and 1.1.5 and the
/// signed 1.1.4 atomicity probe: <see cref="T433AccessReplacementProducerTests"/> regenerates them byte for byte through
/// <see cref="PackExporter"/>, hash-pins them and signature-verifies them
/// (<c>Public_conformance_generator_reproduces_the_checked_in_signed_bytes</c>,
/// <c>T742_export_removes_hints_only_from_value_domain_fields</c>,
/// <c>Replacement_artifact_is_hash_pinned_decodable_and_signature_verified</c> and
/// <c>Atomicity_probe_is_reproducible_signed_and_pins_the_actual_late_content_pointer</c>). A null
/// <see cref="PackManifest.ExposedDefinitions"/> that leaked into the signed canonical form or the transport file would
/// fail them. The other exports are not part of that guard: 1.1.1 and 1.1.3 are unsigned export documents that are only
/// hash-pinned (<c>T742_does_not_change_legacy_signed_exports</c>), and 1.1.2 is signed but no test reads it.
/// </para>
/// </remarks>
public sealed class PackManifestCanonicalFormTests : IDisposable
{
    private const string ContentAddress = "bafkreienvj5tn3avmqrmi5txahm6b4uouehiwbf66g5ajzvrnechsy4hta";

    private const string UnboundCanonical =
        """{"CapabilityRequirements":[],"Contents":[{"ContentAddress":"bafkreienvj5tn3avmqrmi5txahm6b4uouehiwbf66g5ajzvrnechsy4hta","Key":"records/eam.asset-class","Kind":5,"Version":"7.0.0"}],"Dcp":null,"Dependencies":[],"Description":"SIG-1 fixture","Exposes":["records/eam.asset-class"],"InterfaceVersion":1,"Key":"eam-core","Name":"EAM core","ProviderSlot":null,"RenamedFrom":null,"ScopeTier":0,"Version":"7.0.0"}""";

    private const string UnboundAddress = "bafkreiaiyl3dyfzcstzlmd7kxpkhh7shlaejqu6xfv3lomic2dja73ezta";

    private const string BoundCanonical =
        """{"CapabilityRequirements":[],"Contents":[{"ContentAddress":"bafkreienvj5tn3avmqrmi5txahm6b4uouehiwbf66g5ajzvrnechsy4hta","Key":"records/eam.asset-class","Kind":5,"Version":"7.0.0"}],"Dcp":null,"Dependencies":[],"Description":"SIG-1 fixture","ExposedDefinitions":[{"DefinitionKey":"records/eam.asset-class","Digest":"sha256:ca09327e39bb416e6a5e7025adf6e6c2ecf9967bcc2b48122ed14d1eb99e6caf","Version":"3.1.4"}],"Exposes":["records/eam.asset-class"],"InterfaceVersion":1,"Key":"eam-core","Name":"EAM core","ProviderSlot":null,"RenamedFrom":null,"ScopeTier":0,"Version":"7.0.0"}""";

    private const string BoundAddress = "bafkreicb6kznvuy3ititvdtn66ldij5ahvqnxhvul4bpg5dxsdmdpzcs7y";

    private const string MeterDigest = "sha256:a0eda87a3a13a042813b0a8f3b09ede94811258877e5629a944766ea6160a919";
    private const string MeterTransportDigest = "sha256:c8673e0347e4a18a671d23f1e4fb9e59e129ec675d3ec13ac3b47a7ce074832c";

    private const string Meter =
        """{"class_id":"eam.equipment","envelope":{"contract":{"major":1,"minor":0},"exposes":{"interface_version":1},"package_id":"eam-core","section":"eam","tenant":"tenant-a"},"fields":[{"display_name":"Reading","field_key":"reading"}],"name":"Meter","record_class":"master","record_type_id":"eam.meter"}""";

    private readonly KeyPair _keyPair = KeyPair.Generate();
    private readonly PackFileCodec _codec = new();

    public void Dispose() => _keyPair.Dispose();

    [Fact(DisplayName = "goal 3 P3 SIG-1: a manifest without exposed definitions keeps its pinned canonical bytes and content address")]
    public void A_manifest_without_exposed_definitions_keeps_its_pinned_canonical_bytes_and_content_address()
    {
        var bytes = CanonicalJson.Serialize(Manifest(exposedDefinitions: null));

        Assert.Equal(UnboundCanonical, Encoding.UTF8.GetString(bytes));
        Assert.Equal(UnboundAddress, Cid.FromBytes(bytes).Value);
    }

    [Fact(DisplayName = "goal 3 P3 SIG-1: a manifest with exposed definitions signs them into its canonical bytes and content address")]
    public void A_manifest_with_exposed_definitions_signs_them_into_its_canonical_bytes()
    {
        var bytes = CanonicalJson.Serialize(Manifest(
            [new PackExposedDefinition("records/eam.asset-class", "3.1.4",
                "sha256:ca09327e39bb416e6a5e7025adf6e6c2ecf9967bcc2b48122ed14d1eb99e6caf")]));

        Assert.Equal(BoundCanonical, Encoding.UTF8.GetString(bytes));
        Assert.Equal(BoundAddress, Cid.FromBytes(bytes).Value);
        Assert.NotEqual(UnboundAddress, BoundAddress);
    }

    [Fact(DisplayName = "goal 3 P3 SIG-1: an exported pack with exposed definitions signs, verifies and re-encodes; altering or dropping them fails verification")]
    public async Task An_exported_pack_with_exposed_definitions_signs_verifies_and_round_trips()
    {
        var signed = new PackExposedDefinition("records/eam.meter", "1.0.0", MeterDigest);
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            _codec, timeProvider: TimeProvider.System);
        var export = await exporter.ExportAsync(new PackExportRequest(
            Key: "eam-core", Version: "7.0.0", Name: "EAM core", Description: "SIG-1 fixture", ScopeTier: PackScopeTier.Horizontal,
            Contents: [new PackContentSource("records/eam.meter", PackContentKind.AssetTypeDefinition, "7.0.0", JsonNode.Parse(Meter)!)],
            Dependencies: [], CapabilityRequirements: [], Epoch: 1, Dcp: DomainComplianceProfile.General("test-author"),
            Exposes: ["records/eam.meter"], InterfaceVersion: 1, ExposedDefinitions: [signed]), new Ed25519Signer(_keyPair));
        Assert.True(export.Succeeded, string.Join("; ", export.Validation.Errors.Select(error => $"{error.Code}: {error.Message}")));
        var verifier = new PackVerifier(new Ed25519Verifier(), _codec);
        var trust = new InMemoryPackTrustStore(
            [new PackTrustRoot(TrustScope.OwnRoster, _keyPair.PrincipalId, 1, TrustRootStatus.Current)]);

        var bytes = export.FileBytes!;

        var verified = verifier.Verify(bytes, trust);

        Assert.Equal(PackVerdict.Verified, verified.Verdict);
        Assert.Equal([signed], verified.Manifest!.ExposedDefinitions);
        var file = _codec.TryDecode(bytes)!;
        var envelope = file.Envelope!;
        Assert.Equal([signed], envelope.Payload.Manifest.ExposedDefinitions);
        // Decoded and re-encoded, the pack still verifies with its entries.
        Assert.Equal(PackVerdict.Verified, verifier.Verify(_codec.Encode(file), trust).Verdict);

        // The entries are inside the signed subject: an altered digest, or a node that drops the member, fails.
        foreach (var forged in new IReadOnlyList<PackExposedDefinition>?[] { [signed with { Digest = MeterTransportDigest }], null })
        {
            var tampered = file with
            {
                Envelope = envelope with
                {
                    Payload = envelope.Payload with { Manifest = envelope.Payload.Manifest with { ExposedDefinitions = forged } },
                },
            };
            var refused = verifier.Verify(_codec.Encode(tampered), trust);
            Assert.Equal(PackVerdict.VerificationFailed, refused.Verdict);
            Assert.Equal([PackVerificationCodes.SignatureInvalid], refused.Details);
        }
    }

    private static PackManifest Manifest(IReadOnlyList<PackExposedDefinition>? exposedDefinitions) => new(
        Key: "eam-core",
        Version: "7.0.0",
        Name: "EAM core",
        Description: "SIG-1 fixture",
        ScopeTier: PackScopeTier.Horizontal,
        Contents: [new PackContentRef("records/eam.asset-class", PackContentKind.AssetTypeDefinition, "7.0.0", Cid.Parse(ContentAddress))],
        Dependencies: [],
        CapabilityRequirements: [],
        Exposes: ["records/eam.asset-class"],
        InterfaceVersion: 1,
        ExposedDefinitions: exposedDefinitions);
}
