using System.Security.Cryptography;
using System.Text.Json;
using Harborline.Api.Conformance;
using Harborline.Api.Foundation.IdentityAtlas;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Verify;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

/// <summary>Producer-side contract for T-433's immutable Access journey fixtures.</summary>
public sealed class T433AccessReplacementProducerTests
{
    private static readonly string Root = FindRoot();
    private static readonly string InitialPath = Path.Combine(Root, "_shared", "packs", "access-administration", "access-administration-pack.export.json");
    private static readonly string ReplacementDirectory = Path.Combine(Root, "_shared", "conformance", "packs", "access-replacement");

    [Fact]
    public void Initial_pack_declares_exact_journey_definitions_and_actions()
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(InitialPath));
        var root = document.RootElement;
        Assert.Equal("harborline.access-administration", root.GetProperty("key").GetString());
        Assert.Equal("1.1.3", root.GetProperty("version").GetString());
        Assert.Equal(["access.holders"], root.GetProperty("exposes").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(1, root.GetProperty("interfaceVersion").GetInt32());

        var contents = root.GetProperty("contents").EnumerateArray().ToArray();
        Assert.Equal(new[]
        {
            "RoleDefinition/access.admitted-user@1.0.0",
            "RoleDefinition/access.form-submitter@1.0.0",
            "FormDefinition/access.grant-a-role@1.0.1",
            "WorkflowDefinition/access.privileged-grant-review@1.0.1",
            "ViewDefinition/access.holders@1.0.2",
            "NavWorkspaceConfig/access.navigation@1.1.0",
        }, contents.Select(Tuple));

        var holders = contents.Single(item => item.GetProperty("key").GetString() == "access.holders").GetProperty("content");
        Assert.Equal("views.entity-list/grid", holders.GetProperty("viewKind").GetString());
        Assert.Equal(TeamRolePermissions.MembersManage, holders.GetProperty("authorizationCapability").GetString());
        Assert.Equal(new[]
        {
            "access.grant.submit", "access.grant.review", "access.holder.read",
            "access.grant.narrow", "access.grant.revoke", "access.pack.replace",
        }, holders.GetProperty("parameters").GetProperty("actions").EnumerateArray()
            .Select(action => action.GetProperty("operation").GetString()));
        AssertInputBoundGrantActions(holders);
    }

    [Fact]
    public async Task Public_conformance_generator_reproduces_the_checked_in_signed_bytes()
    {
        var generated = await AccessReplacementFixture.GenerateAsync(Root);
        Assert.Equal(File.ReadAllBytes(Path.Combine(ReplacementDirectory, AccessReplacementFixture.ArtifactName)), generated);
    }

    [Fact(DisplayName = "T-742: the 1.1.5 export has no authored control hint on value-domain fields")]
    public async Task T742_export_removes_hints_only_from_value_domain_fields()
    {
        var generated = await AccessReplacementFixture.GenerateT742Async(Root);
        var artifact = File.ReadAllBytes(Path.Combine(ReplacementDirectory, AccessReplacementFixture.T742ArtifactName));
        Assert.Equal(artifact, generated);
        Assert.Equal("9a43ebd6087e715b97c66ac0aed9a7cf219726ca81fe54ad8bf6d32ce49e341d",
            Convert.ToHexStringLower(SHA256.HashData(artifact)));

        var file = Assert.IsType<PackFile>(new PackFileCodec().TryDecode(artifact));
        Assert.Equal(AccessReplacementFixture.T742Version, file.Envelope!.Payload.Manifest.Version);
        var trust = new InMemoryPackTrustStore([
            new PackTrustRoot(TrustScope.OwnRoster, file.Envelope.IssuerId, 1, TrustRootStatus.Current),
        ]);
        Assert.Equal(PackVerdict.Verified, new PackVerifier(new Ed25519Verifier(), new PackFileCodec()).Verify(artifact, trust).Verdict);
        var form = file.Contents.Single(item => item.Key == "access.grant-a-role");
        using var document = JsonDocument.Parse(Convert.FromBase64String(form.ContentBase64));
        var fields = document.RootElement.GetProperty("overlay").GetProperty("fields");
        var metadata = document.RootElement.GetProperty("fieldsMeta");
        foreach (var name in new[] { "reason", "residency" })
        {
            Assert.NotEmpty(metadata.GetProperty(name).GetProperty("options").EnumerateArray());
            Assert.False(fields.GetProperty(name).TryGetProperty("controlHint", out _));
        }

        foreach (var name in new[] { "person", "role", "scope", "effectiveFrom", "effectiveTo" })
            Assert.Equal("text", fields.GetProperty(name).GetProperty("controlHint").GetString());
    }

    [Fact(DisplayName = "T-742: legacy signed exports retain their pinned SHA-256 bytes")]
    public void T742_does_not_change_legacy_signed_exports()
    {
        Assert.Equal("99a3edfa484314c7a0523f4d12b9d7f87725f7b65fc95bbc34dc7ca79e7fdcb2",
            Hash("access-administration-pack-1.1.1.export.json"));
        Assert.Equal("b6f84fef3ffb4323167d5c9d1831098784a88b59bfd903198743ab9add4bb486",
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(InitialPath))));
        Assert.Equal("1fd2920df204e2330e3d533e220ea42ad825fd0e7f48f78dccab68be03f27852",
            Hash("access-administration-pack-1.1.4.export.json"));
    }

    [Fact]
    public void Replacement_artifact_is_hash_pinned_decodable_and_signature_verified()
    {
        var artifactPath = Path.Combine(ReplacementDirectory, "access-administration-pack-1.1.4.export.json");
        var first = File.ReadAllBytes(artifactPath);
        var second = File.ReadAllBytes(artifactPath);
        Assert.Equal(first, second);
        var hash = Convert.ToHexStringLower(SHA256.HashData(first));
        Assert.Equal("1fd2920df204e2330e3d533e220ea42ad825fd0e7f48f78dccab68be03f27852", hash);

        var codec = new PackFileCodec();
        var file = Assert.IsType<PackFile>(codec.TryDecode(first));
        var envelope = file.Envelope;
        Assert.NotNull(envelope);
        Assert.Equal("U5sTSqFz0sAkTuMvK0P0A4AA657_yeG9UY_JCgoFA6g", envelope.IssuerId.ToBase64Url());
        Assert.Equal(1, envelope.Payload.Epoch);
        Assert.Equal(DateTimeOffset.Parse("2026-09-15T16:33:00Z"), envelope.IssuedAt);

        var trust = new InMemoryPackTrustStore([
            new PackTrustRoot(TrustScope.OwnRoster, envelope.IssuerId, 1, TrustRootStatus.Current),
        ]);
        var verified = new PackVerifier(new Ed25519Verifier(), codec).Verify(first, trust);
        Assert.Equal(PackVerdict.Verified, verified.Verdict);
        Assert.Equal([PackVerificationCodes.Verified], verified.Details);
        Assert.Equal("harborline.access-administration", verified.Manifest!.Key);
        Assert.Equal("1.1.4", verified.Manifest.Version);
        Assert.Equal(["access.holders"], verified.Manifest.Exposes);
        Assert.Equal(1, verified.Manifest.InterfaceVersion);
    }

    [Fact]
    public void Replacement_manifest_pins_exact_workflow_and_bound_view_change_sets()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ReplacementDirectory, "replacement.manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal("1fd2920df204e2330e3d533e220ea42ad825fd0e7f48f78dccab68be03f27852", root.GetProperty("sha256").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(InitialPath))),
            root.GetProperty("source").GetProperty("sha256").GetString());
        Assert.Equal(new[]
        {
            "WorkflowDefinition/access.privileged-grant-review@1.0.1",
            "ViewDefinition/access.holders@1.0.2",
        }, Strings(root, "removed"));
        Assert.Equal(new[]
        {
            "WorkflowDefinition/access.scoped-grant-review@1.0.0",
            "ViewDefinition/access.holders@1.0.3",
        }, Strings(root, "added"));
        Assert.Equal(new[]
        {
            "RoleDefinition/access.admitted-user@1.0.0",
            "RoleDefinition/access.form-submitter@1.0.0",
            "FormDefinition/access.grant-a-role@1.0.1",
            "NavWorkspaceConfig/access.navigation@1.1.0",
        }, Strings(root, "retained"));
        Assert.False(root.TryGetProperty("supportingUnchanged", out _));
        Assert.Empty(Strings(root, "removed").Intersect(Strings(root, "added"), StringComparer.Ordinal));
        Assert.Empty(Strings(root, "retained").Intersect(
            Strings(root, "removed").Concat(Strings(root, "added")), StringComparer.Ordinal));

        var bytes = File.ReadAllBytes(Path.Combine(ReplacementDirectory, "access-administration-pack-1.1.4.export.json"));
        var file = Assert.IsType<PackFile>(new PackFileCodec().TryDecode(bytes));
        Assert.Equal(new[]
        {
            "RoleDefinition/access.admitted-user@1.0.0",
            "RoleDefinition/access.form-submitter@1.0.0",
            "FormDefinition/access.grant-a-role@1.0.1",
            "WorkflowDefinition/access.scoped-grant-review@1.0.0",
            "ViewDefinition/access.holders@1.0.3",
            "NavWorkspaceConfig/access.navigation@1.1.0",
        }, file.Envelope!.Payload.Manifest.Contents.Select(item => $"{item.Kind}/{item.Key}@{item.Version}"));

        var holderPayload = file.Contents.Single(item => item.Key == "access.holders");
        using var holder = JsonDocument.Parse(Convert.FromBase64String(holderPayload.ContentBase64));
        Assert.Equal("1.0.3", holder.RootElement.GetProperty("version").GetString());
        Assert.Equal(TeamRolePermissions.MembersManage, holder.RootElement.GetProperty("authorizationCapability").GetString());
        var review = holder.RootElement.GetProperty("parameters").GetProperty("actions").EnumerateArray()
            .Single(action => action.GetProperty("operation").GetString() == "access.grant.review");
        Assert.Equal("access.scoped-grant-review", review.GetProperty("workflow").GetString());
        AssertInputBoundGrantActions(holder.RootElement);
    }

    private static string Tuple(JsonElement item) => $"{item.GetProperty("kind").GetString()}/{item.GetProperty("key").GetString()}@{item.GetProperty("version").GetString()}";

    [Fact]
    public async Task Atomicity_probe_is_reproducible_signed_and_pins_the_actual_late_content_pointer()
    {
        var bytes = await AccessReplacementFixture.GenerateAsync(Root, atomicityProbe: true);
        Assert.Equal(File.ReadAllBytes(Path.Combine(ReplacementDirectory, AccessReplacementFixture.ProbeArtifactName)), bytes);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ReplacementDirectory, "atomicity-probe.manifest.json")));
        var pinned = manifest.RootElement;
        Assert.Equal("263e363a0224a7205520920ac776e8ef99e7a72cff5db01db3c0291dffafe7fc", pinned.GetProperty("sha256").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), pinned.GetProperty("sha256").GetString());
        var codec = new PackFileCodec();
        var file = Assert.IsType<PackFile>(codec.TryDecode(bytes));
        var trust = new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, file.Envelope!.IssuerId, 1, TrustRootStatus.Current)]);
        Assert.Equal(PackVerdict.Verified, new PackVerifier(new Ed25519Verifier(), codec).Verify(bytes, trust).Verdict);
        Assert.Equal(AccessReplacementFixture.ProbeVersion, file.Envelope.Payload.Manifest.Version);
        Assert.Equal(8, file.Contents.Count);
        var index = file.Contents.ToList().FindIndex(item => item.Key == AccessReplacementFixture.ProbeRefusalKey);
        Assert.Equal($"/contents/{index}/contentBase64", pinned.GetProperty("expectedRefusal").GetProperty("pointer").GetString());
        Assert.Equal("pack.view-definition.malformed", pinned.GetProperty("expectedRefusal").GetProperty("code").GetString());
        using var early = JsonDocument.Parse(Convert.FromBase64String(file.Contents[index - 1].ContentBase64));
        using var late = JsonDocument.Parse(Convert.FromBase64String(file.Contents[index].ContentBase64));
        Assert.Equal("views.entity-list/grid", early.RootElement.GetProperty("viewKind").GetString());
        Assert.Equal("views.not-registered", late.RootElement.GetProperty("viewKind").GetString());
        var holders = file.Contents.Single(item => item.Key == "access.holders");
        using var holder = JsonDocument.Parse(Convert.FromBase64String(holders.ContentBase64));
        AssertInputBoundGrantActions(holder.RootElement);
    }

    private static void AssertInputBoundGrantActions(JsonElement holders)
    {
        var actions = holders.GetProperty("parameters").GetProperty("actions").EnumerateArray().ToArray();
        foreach (var operation in new[] { "narrow", "revoke" })
        {
            var action = actions.Single(item => item.GetProperty("id").GetString() == operation);
            var fields = action.GetProperty("input").GetProperty("fieldsMeta");
            Assert.Equal("text", fields.GetProperty("targetGrant").GetProperty("type").GetString());
            Assert.True(fields.GetProperty("targetGrant").GetProperty("required").GetBoolean());
            Assert.Equal("Grant ID", action.GetProperty("input").GetProperty("overlay").GetProperty("fields").GetProperty("targetGrant").GetProperty("label").GetString());
            var dispatch = action.GetProperty("dispatch");
            Assert.Equal(operation == "narrow" ? "authorization.grant.narrow-scope.v1" : "authorization.grant.revoke.v1",
                dispatch.GetProperty("descriptorId").GetString());
            var bindings = dispatch.GetProperty("bindings");
            Assert.Equal("input", bindings.GetProperty("target").GetProperty("source").GetString());
            Assert.Equal("/targetGrant", bindings.GetProperty("target").GetProperty("pointer").GetString());
            Assert.Equal("invocation", bindings.GetProperty("correlationId").GetProperty("source").GetString());
            Assert.Equal("/correlationId", bindings.GetProperty("correlationId").GetProperty("pointer").GetString());
            if (operation == "narrow")
            {
                Assert.True(fields.GetProperty("scope").GetProperty("required").GetBoolean());
                Assert.Equal("input", bindings.GetProperty("scope").GetProperty("source").GetString());
                Assert.Equal("/scope", bindings.GetProperty("scope").GetProperty("pointer").GetString());
            }
        }
    }
    private static string[] Strings(JsonElement root, string property) => root.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static string Hash(string artifact) => Convert.ToHexStringLower(
        SHA256.HashData(File.ReadAllBytes(Path.Combine(ReplacementDirectory, artifact))));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harborline.Api.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
