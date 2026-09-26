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
    internal const string ArtifactName = "access-administration-pack-1.1.4.export.json";
    internal const string T742ArtifactName = "access-administration-pack-1.1.5.export.json";
    internal const string T742Version = "1.1.5";
    internal const string ProbeArtifactName = "access-administration-pack-1.1.4-atomicity-probe.0.export.json";
    internal const string ProbeVersion = "1.1.4-atomicity-probe.0";
    internal const string ProbeRefusalKey = "m6.late-refusal";
    internal static readonly DateTimeOffset IssuedAt = new(2026, 9, 15, 16, 33, 0, TimeSpan.Zero);
    internal static readonly Guid Nonce = new("fd3a5943-e24b-4fa5-9059-2ca967967820");

    internal static async Task<byte[]> GenerateAsync(string root, bool atomicityProbe = false)
    {
        var source = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(root, SourcePath)))!;
        var contents = source["contents"]!.AsArray();
        if (atomicityProbe)
        {
            var holder = contents.Single(item => item!["key"]!.GetValue<string>() == "access.holders")!;
            var early = holder.DeepClone();
            early["key"] = "m6.early-view";
            early["content"]!["key"] = "m6.early-view";
            var late = holder.DeepClone();
            late["key"] = ProbeRefusalKey;
            late["content"]!["key"] = ProbeRefusalKey;
            late["content"]!["viewKind"] = "views.not-registered";
            contents.Add(early);
            contents.Add(late);
        }
        else
        {
        var workflow = contents.Single(item => item!["key"]!.GetValue<string>() == "access.privileged-grant-review")!;
        workflow["key"] = "access.scoped-grant-review";
        workflow["content"]!["key"] = "access.scoped-grant-review";
        workflow["version"] = "1.0.0";
        workflow["content"]!["version"] = "1.0.0";
        workflow["content"]!["postSubmitProjection"] = "access.scoped-grant-review";
        var holders = contents.Single(item => item!["key"]!.GetValue<string>() == "access.holders")!;
        holders["version"] = "1.0.3";
        holders["content"]!["version"] = "1.0.3";
        var review = holders["content"]!["parameters"]!["actions"]!.AsArray()
            .Single(action => action!["operation"]!.GetValue<string>() == "access.grant.review")!;
        review["workflow"] = "access.scoped-grant-review";
        review["label"] = "Review scoped grant";
        }

        // Intentionally public and reproducible. This conformance identity carries no production authority.
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes("Harborline T-433 public conformance fixture; NEVER a production key"));
        using var key = KeyPair.FromSeed(seed);
        var signer = new FixtureSigner(new Ed25519Signer(key), atomicityProbe
            ? new Guid("80b5ce08-310b-4b0d-803b-0e6fd4ab96a9") : Nonce);
        var request = new PackExportRequest(
            source["key"]!.GetValue<string>(), atomicityProbe ? ProbeVersion : "1.1.4", source["name"]!.GetValue<string>(),
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

    /// <summary>
    /// T-742's successor to the immutable signed releases. It preserves the 1.1.3 journey contents
    /// and removes author-controlled editor choices only from its two value-domain fields.
    /// </summary>
    internal const string T742GrantFormVersion = "1.0.2";
    internal const string T742WorkflowVersion = "1.0.2";
    internal const string T742HoldersVersion = "1.0.4";

    internal static async Task<byte[]> GenerateT742Async(string root)
    {
        var source = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(root, SourcePath)))!;
        var contents = source["contents"]!.AsArray();
        var form = contents.Single(item => item!["key"]!.GetValue<string>() == "access.grant-a-role")!;
        var fields = form["content"]!["overlay"]!["fields"]!;
        fields["reason"]!.AsObject().Remove("controlHint");
        fields["residency"]!.AsObject().Remove("controlHint");
        // The form's content changed (the two hints were removed), so it cannot keep publishing under
        // the already-pinned (key, version) tuple 1.0.1 that 1.1.1/1.1.3/1.1.4 carry: a node that has
        // already projected that tuple would see different content at the same coordinate and refuse
        // this pack with pack.form.pinned_tuple_conflict (PackSeedProjector.MatchesPinnedPackDefinition).
        // Publish the changed form at the next version and repoint every reference to it. Every OTHER
        // content item whose payload now embeds that new form version (the workflow's subjectFormRef,
        // the view's grant-submit inputForm) has therefore also changed content, so IT must bump its own
        // (item and content) version too, or it hits the same pinned-tuple conflict in its own right.
        form["version"] = T742GrantFormVersion;
        var workflow = contents.Single(item => item!["key"]!.GetValue<string>() == "access.privileged-grant-review")!;
        workflow["version"] = T742WorkflowVersion;
        workflow["content"]!["version"] = T742WorkflowVersion;
        workflow["content"]!["subjectFormRef"]!["version"] = T742GrantFormVersion;
        var holders = contents.Single(item => item!["key"]!.GetValue<string>() == "access.holders")!;
        holders["version"] = T742HoldersVersion;
        holders["content"]!["version"] = T742HoldersVersion;
        var grant = holders["content"]!["parameters"]!["actions"]!.AsArray()
            .Single(action => action!["operation"]!.GetValue<string>() == "access.grant.submit")!;
        grant["inputForm"]!["version"] = T742GrantFormVersion;
        // The retired "views.entity-list/grid" token that 1.1.1-1.1.4 carry is projected onto the
        // platform's canonical table kind ONLY for those specific, already-released (packKey, version)
        // pairs (ReleasedViewKindCompatibility.ReleasedPredecessors); "new and unknown pack versions
        // receive no compatibility treatment" by that shim's own contract, so this NEW version must
        // carry the canonical token directly or the descriptor registry refuses it with
        // view_definition.kind_unknown.
        holders["content"]!["viewKind"] = Harborline.Blocks.EntityViews.ViewKindIds.Table;

        var seed = SHA256.HashData(Encoding.UTF8.GetBytes("Harborline T-433 public conformance fixture; NEVER a production key"));
        using var key = KeyPair.FromSeed(seed);
        var signer = new FixtureSigner(new Ed25519Signer(key), new Guid("31e22206-7645-4983-b35a-f35ae3f53b09"));
        var request = new PackExportRequest(
            source["key"]!.GetValue<string>(), T742Version, source["name"]!.GetValue<string>(),
            source["description"]!.GetValue<string>(), Enum.Parse<PackScopeTier>(source["scopeTier"]!.GetValue<string>()),
            source["contents"]!.AsArray().Select(item => new PackContentSource(item!["key"]!.GetValue<string>(),
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
            : throw new InvalidOperationException("T-742 export refused: " + string.Join(", ", result.Validation.Errors.Select(error => error.Code)));
    }

    private sealed class FixtureTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => IssuedAt;
    }

    // PackExporter supplies random operation nonces in production. Fix only this test signer's nonce.
    private sealed class FixtureSigner(IOperationSigner inner, Guid fixedNonce) : IOperationSigner
    {
        public PrincipalId IssuerId => inner.IssuerId;
        public ValueTask<SignedOperation<T>> SignAsync<T>(T payload, DateTimeOffset issuedAt, Guid nonce,
            CancellationToken ct = default) => inner.SignAsync(payload, issuedAt, fixedNonce, ct);
    }
}
