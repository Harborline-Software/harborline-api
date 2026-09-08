using System.Text.Json.Nodes;

using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Crypto;
using Harborline.Api.Foundation.Packs.Dcp;
using Harborline.Api.Foundation.Packs.Export;
using Harborline.Api.Foundation.Packs.Graph;
using Harborline.Api.Foundation.Packs.Install;
using Harborline.Api.Foundation.Packs.Install.Admission;
using Harborline.Api.Foundation.Packs.Install.Audit;
using Harborline.Api.Foundation.Packs.Install.Compatibility;
using Harborline.Api.Foundation.Packs.Install.Trust;
using Harborline.Api.Foundation.Packs.Model;
using Harborline.Api.Foundation.Packs.Serialization;
using Harborline.Api.Foundation.Packs.Trust;
using Harborline.Api.Foundation.Packs.Validation;
using Harborline.Api.Foundation.Packs.Verify;
using Harborline.Api.LocalNodeHost.Data.PackProjection;

using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.CapabilityCompatibility;

public sealed class PackCapabilityCompatibilityTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000037");
    private static readonly DateTimeOffset Now = new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Provides_excludes_a_pillar_without_a_registered_projector_case()
    {
        var futurePillar = (PackPillar)98;
        var pillars = Enum.GetValues<PackPillar>().Append(futurePillar);

        var compatibility = new PackPlatformCompatibility(
            "2.0.0",
            pillars,
            PackSeedProjector.RegisteredCases);

        Assert.Contains(PackPlatformCompatibility.PillarCapability(PackPillar.Forms), compatibility.Provides);
        Assert.DoesNotContain(PackPlatformCompatibility.PillarCapability(futurePillar), compatibility.Provides);
    }

    [Fact]
    public async Task Preview_checks_version_floor_only_after_capability_is_satisfied()
    {
        var provided = new PackPlatformCompatibility(
            "2.0.0",
            [new PackProjectorCase(PackContentKind.FormDefinition, ["forms.dynamic"])]);
        using var floorFixture = await CreateFixtureAsync(
            EnvelopeRequirement("forms.dynamic", "3.0.0"),
            provided);

        var floorPreview = floorFixture.Installer.Preview(floorFixture.PackBytes, floorFixture.Context);

        Assert.Equal(PackInstallVerdict.Refused, floorPreview.Verdict);
        Assert.Contains("pack.install.refused.platform_version_floor", floorPreview.RefusalCodes);
        Assert.DoesNotContain("pack.install.refused.missing_platform_capability", floorPreview.RefusalCodes);

        using var capabilityFixture = await CreateFixtureAsync(
            EnvelopeRequirement("packs.pillar.future", "3.0.0"),
            provided);

        var capabilityPreview = capabilityFixture.Installer.Preview(
            capabilityFixture.PackBytes,
            capabilityFixture.Context);

        Assert.Contains("pack.install.refused.missing_platform_capability", capabilityPreview.RefusalCodes);
        Assert.DoesNotContain("pack.install.refused.platform_version_floor", capabilityPreview.RefusalCodes);
    }

    [Fact]
    public void Activate_refuses_a_persisted_draft_with_an_unmet_requirement()
    {
        using var keyPair = KeyPair.Generate();
        var store = new InMemoryPackInstallStore();
        var content = EnvelopeRequirement("packs.pillar.future").ToJsonString();
        var seed = new PackSeedItem(
            "missing-capability-form",
            PackContentKind.FormDefinition,
            "1.0.0",
            content,
            Harborline.Api.Foundation.Blobs.Cid.FromBytes(System.Text.Encoding.UTF8.GetBytes(content)));
        var pack = new InstalledPack(
            "test.activation-capability",
            "1.0.0",
            PackScopeTier.Horizontal,
            PackLifecycleState.Draft,
            [seed],
            new Dictionary<string, int>(),
            Now,
            keyPair.PrincipalId,
            1,
            TrustScope.OwnRoster,
            Array.Empty<PackDependencyRef>());
        store.Commit(new PackInstallTransaction(
            Tenant,
            pack,
            new PackInstallWatermark(pack.PackKey, pack.Version, new Dictionary<string, int>()),
            Array.Empty<PackTenantOverride>()));
        var installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), new PackFileCodec()),
            store,
            new WorkflowRefusingPackContentAdmission(),
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
            new PackPlatformCompatibility("2.0.0", Array.Empty<PackProjectorCase>()));

        var activation = installer.Activate(Tenant, pack.PackKey, pack.Version, Now, "test-operator");

        Assert.False(activation.Activated);
        Assert.Equal("pack.install.activate.unmet_platform_requirement", activation.Error);
        Assert.Equal(PackLifecycleState.Draft, store.GetVersion(Tenant, pack.PackKey, pack.Version)!.Lifecycle);
    }

    [Fact]
    public async Task Preview_refuses_missing_envelope_capability_before_store_mutates()
    {
        using var fixture = await CreateFixtureAsync(
            new JsonObject
            {
                ["definitionEnvelope"] = new JsonObject
                {
                    ["requires"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["capability"] = "packs.pillar.future",
                        },
                    },
                },
            });

        var preview = fixture.Installer.Preview(fixture.PackBytes, fixture.Context);

        Assert.Equal(PackInstallVerdict.Refused, preview.Verdict);
        Assert.Contains("pack.install.refused.missing_platform_capability", preview.RefusalCodes);
        Assert.Empty(fixture.Store.ListInstalled(Tenant));

        var install = fixture.Installer.Install(fixture.PackBytes, fixture.Context);
        Assert.False(install.Installed);
        Assert.Empty(fixture.Store.ListInstalled(Tenant));
    }

    [Fact]
    public async Task Preview_refusal_names_the_missing_capability_and_declaring_content()
    {
        using var fixture = await CreateFixtureAsync(EnvelopeRequirement("packs.pillar.future"));

        var preview = fixture.Installer.Preview(fixture.PackBytes, fixture.Context);

        var unmet = Assert.Single(preview.UnmetPlatformRequirements);
        Assert.Equal("packs.pillar.future", unmet.Capability);
        Assert.Equal("missing-capability-form", unmet.DeclaredBy);
    }

    private static JsonObject EnvelopeRequirement(string capability, string? minimumPlatformVersion = null)
        => new()
        {
            ["definitionEnvelope"] = new JsonObject
            {
                ["requires"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["capability"] = capability,
                        ["minimumPlatformVersion"] = minimumPlatformVersion,
                    },
                },
            },
        };

    private static async Task<Fixture> CreateFixtureAsync(
        JsonNode content,
        IPackPlatformCompatibility? platform = null)
    {
        var keyPair = KeyPair.Generate();
        var codec = new PackFileCodec();
        var exporter = new PackExporter(
            new PackContentCanonicalizer(),
            new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()),
            new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, timeProvider: TimeProvider.System);
        var export = await exporter.ExportAsync(
            new PackExportRequest(
                Key: "test.capability-compatibility",
                Version: "1.0.0",
                Name: "Capability compatibility test pack",
                Description: "Exercises platform capability admission.",
                ScopeTier: PackScopeTier.Horizontal,
                Contents:
                [
                    new PackContentSource(
                        "missing-capability-form",
                        PackContentKind.FormDefinition,
                        "1.0.0",
                        content),
                ],
                Dependencies: Array.Empty<PackDependencyRef>(),
                CapabilityRequirements: Array.Empty<string>(),
                Epoch: 1,
                Dcp: DomainComplianceProfile.General("test-author")),
            new Ed25519Signer(keyPair));
        Assert.True(export.Succeeded, string.Join(
            "; ", export.Validation.Errors.Select(error => $"{error.Code}: {error.Message}")));

        var trustStore = new InMemoryPackTrustStore(
        [
            new PackTrustRoot(TrustScope.OwnRoster, keyPair.PrincipalId, 1, TrustRootStatus.Current),
        ]);
        var store = new InMemoryPackInstallStore();
        var installer = new PackInstaller(
            new PackVerifier(new Ed25519Verifier(), codec),
            store,
            new WorkflowRefusingPackContentAdmission(),
            new InMemoryPackInstallAudit(),
            Harborline.Api.LocalNodeHost.Tests.Authorization.TestAuthorization.AllowGate(),
            platform);
        var context = new PackInstallContext(
            Tenant,
            trustStore,
            PackRevocationList.Empty,
            Now,
            TimeSpan.FromDays(30),
            Principal: "test-operator");

        return new Fixture(installer, context, store, export.FileBytes!, keyPair);
    }

    private sealed record Fixture(
        IPackInstaller Installer,
        PackInstallContext Context,
        InMemoryPackInstallStore Store,
        byte[] PackBytes,
        KeyPair KeyPair) : IDisposable
    {
        public void Dispose() => KeyPair.Dispose();
    }
}
