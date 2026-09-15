using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Validation;

namespace Harborline.Api.Conformance;

/// <summary>Public test fixture material only; never use this signing identity for a node or trust root.</summary>
internal static class AccessReplacementFixture
{
    internal const string SourcePath = "_shared/packs/access-administration/access-administration-pack.export.json";
    internal const string DirectoryPath = "_shared/conformance/packs/access-replacement";
    internal const string ArtifactName = "access-administration-pack-1.1.2.export.json";
    internal static readonly DateTimeOffset IssuedAt = new(2026, 9, 15, 16, 33, 0, TimeSpan.Zero);
    internal static readonly Guid Nonce = new("fd3a5943-e24b-4fa5-9059-2ca967967820");

    internal static async Task<byte[]> GenerateAsync(string root)
    {
        var source = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(root, SourcePath)))!;
        var contents = source["contents"]!.AsArray();
        var workflow = contents.Single(item => item!["key"]!.GetValue<string>() == "access.privileged-grant-review")!;
        workflow["key"] = "access.scoped-grant-review";
        workflow["content"]!["key"] = "access.scoped-grant-review";
        workflow["version"] = "1.0.0";
        workflow["content"]!["version"] = "1.0.0";
        workflow["content"]!["postSubmitProjection"] = "access.scoped-grant-review";
        var holders = contents.Single(item => item!["key"]!.GetValue<string>() == "access.holders")!;
        holders["version"] = "1.0.1";
        holders["content"]!["version"] = "1.0.1";
        var review = holders["content"]!["parameters"]!["actions"]!.AsArray()
            .Single(action => action!["operation"]!.GetValue<string>() == "access.grant.review")!;
        review["workflow"] = "access.scoped-grant-review";
        review["label"] = "Review scoped grant";

        // Intentionally public and reproducible. This conformance identity carries no production authority.
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes("Harborline T-433 public conformance fixture; NEVER a production key"));
        using var key = KeyPair.FromSeed(seed);
        var signer = new FixtureSigner(new Ed25519Signer(key));
        var request = new PackExportRequest(
            source["key"]!.GetValue<string>(), "1.1.2", source["name"]!.GetValue<string>(),
            source["description"]!.GetValue<string>(), Enum.Parse<PackScopeTier>(source["scopeTier"]!.GetValue<string>()),
            contents.Select(item => new PackContentSource(item!["key"]!.GetValue<string>(),
                Enum.Parse<PackContentKind>(item["kind"]!.GetValue<string>()), item["version"]!.GetValue<string>(),
                item["content"]!.DeepClone())).ToArray(),
            source["dependencies"]!.AsArray().Select(item => new PackDependencyRef(
                item!["key"]!.GetValue<string>(), item["version"]!.GetValue<string>(),
                item["declaredDependencyKeys"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray())).ToArray(),
            source["capabilityRequirements"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray(),
            1, Dcp: DomainComplianceProfile.General(signer.IssuerId.ToBase64Url()),
            Exposes: source["exposes"]!.AsArray().Select(value => value!.GetValue<string>()).ToArray(),
            InterfaceVersion: source["interfaceVersion"]!.GetValue<int>());
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            new PackFileCodec(), new FixtureTime());
        var result = await exporter.ExportAsync(request, signer);
        return result.Succeeded && result.FileBytes is not null ? result.FileBytes
            : throw new InvalidOperationException("Conformance export refused: " + string.Join(", ", result.Validation.Errors.Select(error => error.Code)));
    }

    private sealed class FixtureTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => IssuedAt;
    }

    // PackExporter supplies random operation nonces in production. Fix only this test signer's nonce.
    private sealed class FixtureSigner(IOperationSigner inner) : IOperationSigner
    {
        public PrincipalId IssuerId => inner.IssuerId;
        public ValueTask<SignedOperation<T>> SignAsync<T>(T payload, DateTimeOffset issuedAt, Guid nonce,
            CancellationToken ct = default) => inner.SignAsync(payload, issuedAt, Nonce, ct);
    }
}
