using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>
/// Goal 3 P3 EXP-1: the exporter copies a request's exposed-definition entries into the signed manifest unchanged and
/// computes nothing, and refuses a malformed set with <c>pack.export.exposed_definition_malformed</c> before anything is
/// signed.
/// </summary>
/// <remarks>
/// Oracles: the code literal is the provisional spelling proposed for owner confirmation (open question O-6); the
/// entry facts are the Platform Goal 3 P1 literals (catalogue version 3.1.4 and its store digest). Targets are the
/// literal entry positions.
/// </remarks>
public sealed class PackExporterExposedDefinitionsTests : IDisposable
{
    private const string Digest = "sha256:ca09327e39bb416e6a5e7025adf6e6c2ecf9967bcc2b48122ed14d1eb99e6caf";

    private const string Malformed = "pack.export.exposed_definition_malformed";

    private static readonly PackExposedDefinition AssetClass = new("records/eam.asset-class", "3.1.4", Digest);

    private readonly KeyPair _keyPair = KeyPair.Generate();

    public void Dispose() => _keyPair.Dispose();

    public static TheoryData<string, string> MalformedSets() => new()
    {
        { "entries without an exposure", "exposedDefinitions" },
        { "entries without an interface version", "exposedDefinitions" },
        { "an empty list", "exposedDefinitions" },
        { "a null entry", "exposedDefinitions/0" },
        { "an unexposed key", "exposedDefinitions/0" },
        { "a repeated key", "exposedDefinitions/1" },
        { "a blank version", "exposedDefinitions/0" },
        { "an uppercase digest", "exposedDefinitions/0" },
        { "a digest without its algorithm", "exposedDefinitions/0" },
        { "a short digest", "exposedDefinitions/0" },
    };

    [Theory(DisplayName = "goal 3 P3 EXP-1: malformed exposed-definition entries are refused before signing")]
    [MemberData(nameof(MalformedSets))]
    public async Task Malformed_exposed_definitions_are_refused_before_signing(string malformed, string target)
    {
        var request = malformed switch
        {
            "entries without an exposure" => Request([AssetClass]) with { Exposes = null, InterfaceVersion = null },
            "entries without an interface version" => Request([AssetClass]) with { InterfaceVersion = null },
            "an empty list" => Request([]),
            "a null entry" => Request([null!]),
            "an unexposed key" => Request([AssetClass with { DefinitionKey = "records/eam.other" }]),
            "a repeated key" => Request([AssetClass, AssetClass with { Version = "3.1.5" }]),
            "a blank version" => Request([AssetClass with { Version = " " }]),
            "an uppercase digest" => Request([AssetClass with { Digest = Digest.ToUpperInvariant().Replace("SHA256:", "sha256:", StringComparison.Ordinal) }]),
            "a digest without its algorithm" => Request([AssetClass with { Digest = Digest["sha256:".Length..] }]),
            "a short digest" => Request([AssetClass with { Digest = Digest[..^1] }]),
            _ => throw new ArgumentOutOfRangeException(nameof(malformed), malformed, null),
        };
        var signer = new CountingSigner(new Ed25519Signer(_keyPair));

        var export = await Exporter().ExportAsync(request, signer);

        Assert.False(export.Succeeded);
        Assert.Null(export.File);
        Assert.Null(export.FileBytes);
        Assert.Equal(0, signer.Calls);
        Assert.Equal([(Malformed, target)], export.Validation.Errors.Select(error => (error.Code, error.Target)));
    }

    [Fact(DisplayName = "goal 3 P3 EXP-1: absent entries export as before and well-formed ones are signed exactly as requested")]
    public async Task Absent_or_well_formed_exposed_definitions_export_unchanged()
    {
        var unbound = await Exporter().ExportAsync(Request(null), new Ed25519Signer(_keyPair));
        PackExposedDefinition[] entries = [AssetClass, new("records/eam.meter", "1.0.0", "sha256:" + new string('0', 64))];
        var bound = await Exporter().ExportAsync(Request(entries), new Ed25519Signer(_keyPair));

        Assert.True(unbound.Succeeded);
        Assert.Null(unbound.File!.Envelope!.Payload.Manifest.ExposedDefinitions);
        Assert.True(bound.Succeeded);
        Assert.Equal(entries, bound.File!.Envelope!.Payload.Manifest.ExposedDefinitions);
    }

    private static PackExportRequest Request(IReadOnlyList<PackExposedDefinition>? exposedDefinitions) => new(
        Key: "eam-core",
        Version: "7.0.0",
        Name: "EAM core",
        Description: "EXP-1 fixture",
        ScopeTier: PackScopeTier.Horizontal,
        Contents:
        [
            new PackContentSource("records/eam.asset-class", PackContentKind.AssetTypeDefinition, "7.0.0", JsonNode.Parse("""{"marker":"asset-class"}""")!),
            new PackContentSource("records/eam.meter", PackContentKind.AssetTypeDefinition, "7.0.0", JsonNode.Parse("""{"marker":"meter"}""")!),
        ],
        Dependencies: [],
        CapabilityRequirements: [],
        Epoch: 1,
        Dcp: DomainComplianceProfile.General("test-author"),
        Exposes: ["records/eam.asset-class", "records/eam.meter"],
        InterfaceVersion: 1,
        ExposedDefinitions: exposedDefinitions);

    private static PackExporter Exporter() => new(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
        new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
        new PackFileCodec(), timeProvider: TimeProvider.System);

    // Delegates to the real signer and counts every signing call, so a refusal provably signed nothing.
    private sealed class CountingSigner(IOperationSigner inner) : IOperationSigner
    {
        public int Calls { get; private set; }

        public PrincipalId IssuerId => inner.IssuerId;

        public ValueTask<SignedOperation<T>> SignAsync<T>(T payload, DateTimeOffset issuedAt, Guid nonce,
            CancellationToken ct = default)
        {
            Calls++;
            return inner.SignAsync(payload, issuedAt, nonce, ct);
        }
    }
}
