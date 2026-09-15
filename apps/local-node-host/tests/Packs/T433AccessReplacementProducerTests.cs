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
        Assert.Equal("1.1.1", root.GetProperty("version").GetString());
        Assert.Equal(["access.holders"], root.GetProperty("exposes").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(1, root.GetProperty("interfaceVersion").GetInt32());

        var contents = root.GetProperty("contents").EnumerateArray().ToArray();
        Assert.Equal(new[]
        {
            "RoleDefinition/access.form-submitter@1.0.0",
            "FormDefinition/access.grant-a-role@1.0.1",
            "WorkflowDefinition/access.privileged-grant-review@1.0.1",
            "ViewDefinition/access.holders@1.0.0",
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
    }

    [Fact]
    public async Task Public_conformance_generator_reproduces_the_checked_in_signed_bytes()
    {
        var generated = await AccessReplacementFixture.GenerateAsync(Root);
        Assert.Equal(File.ReadAllBytes(Path.Combine(ReplacementDirectory, AccessReplacementFixture.ArtifactName)), generated);
    }

    [Fact]
    public void Replacement_artifact_is_hash_pinned_decodable_and_signature_verified()
    {
        var artifactPath = Path.Combine(ReplacementDirectory, "access-administration-pack-1.1.2.export.json");
        var first = File.ReadAllBytes(artifactPath);
        var second = File.ReadAllBytes(artifactPath);
        Assert.Equal(first, second);
        var hash = Convert.ToHexStringLower(SHA256.HashData(first));
        Assert.Equal("a5a1a6b1d8e75bc30ba902c0fb2a795e82931631a0ad644bb7018038a3bce122", hash);

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
        Assert.Equal("1.1.2", verified.Manifest.Version);
        Assert.Equal(["access.holders"], verified.Manifest.Exposes);
        Assert.Equal(1, verified.Manifest.InterfaceVersion);
    }

    [Fact]
    public void Replacement_manifest_pins_exact_workflow_and_bound_view_change_sets()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ReplacementDirectory, "replacement.manifest.json")));
        var root = manifest.RootElement;
        Assert.Equal("a5a1a6b1d8e75bc30ba902c0fb2a795e82931631a0ad644bb7018038a3bce122", root.GetProperty("sha256").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(InitialPath))),
            root.GetProperty("source").GetProperty("sha256").GetString());
        Assert.Equal(new[]
        {
            "WorkflowDefinition/access.privileged-grant-review@1.0.1",
            "ViewDefinition/access.holders@1.0.0",
        }, Strings(root, "removed"));
        Assert.Equal(new[]
        {
            "WorkflowDefinition/access.scoped-grant-review@1.0.0",
            "ViewDefinition/access.holders@1.0.1",
        }, Strings(root, "added"));
        Assert.Equal(new[]
        {
            "RoleDefinition/access.form-submitter@1.0.0",
            "FormDefinition/access.grant-a-role@1.0.1",
            "NavWorkspaceConfig/access.navigation@1.1.0",
        }, Strings(root, "retained"));
        Assert.False(root.TryGetProperty("supportingUnchanged", out _));
        Assert.Empty(Strings(root, "removed").Intersect(Strings(root, "added"), StringComparer.Ordinal));
        Assert.Empty(Strings(root, "retained").Intersect(
            Strings(root, "removed").Concat(Strings(root, "added")), StringComparer.Ordinal));

        var bytes = File.ReadAllBytes(Path.Combine(ReplacementDirectory, "access-administration-pack-1.1.2.export.json"));
        var file = Assert.IsType<PackFile>(new PackFileCodec().TryDecode(bytes));
        Assert.Equal(new[]
        {
            "RoleDefinition/access.form-submitter@1.0.0",
            "FormDefinition/access.grant-a-role@1.0.1",
            "WorkflowDefinition/access.scoped-grant-review@1.0.0",
            "ViewDefinition/access.holders@1.0.1",
            "NavWorkspaceConfig/access.navigation@1.1.0",
        }, file.Envelope!.Payload.Manifest.Contents.Select(item => $"{item.Kind}/{item.Key}@{item.Version}"));

        var holderPayload = file.Contents.Single(item => item.Key == "access.holders");
        using var holder = JsonDocument.Parse(Convert.FromBase64String(holderPayload.ContentBase64));
        Assert.Equal("1.0.1", holder.RootElement.GetProperty("version").GetString());
        Assert.Equal(TeamRolePermissions.MembersManage, holder.RootElement.GetProperty("authorizationCapability").GetString());
        var review = holder.RootElement.GetProperty("parameters").GetProperty("actions").EnumerateArray()
            .Single(action => action.GetProperty("operation").GetString() == "access.grant.review");
        Assert.Equal("access.scoped-grant-review", review.GetProperty("workflow").GetString());
    }

    private static string Tuple(JsonElement item) => $"{item.GetProperty("kind").GetString()}/{item.GetProperty("key").GetString()}@{item.GetProperty("version").GetString()}";
    private static string[] Strings(JsonElement root, string property) => root.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harborline.Api.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
