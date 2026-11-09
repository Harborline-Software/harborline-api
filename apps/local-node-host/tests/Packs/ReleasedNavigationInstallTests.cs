using System.Text.Json.Nodes;
using Harborline.Api.Blocks.Workflow.Durable;
using Harborline.Api.Foundation.Assets.Common;
using Harborline.Api.Foundation.Authorization;
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
using Harborline.Api.LocalNodeHost.Health;
using Harborline.Api.LocalNodeHost.Tests.Authorization;
using Xunit;

namespace Harborline.Api.LocalNodeHost.Tests.Packs;

public sealed class ReleasedNavigationInstallTests
{
    private static readonly TenantId Tenant = new("aaaaaaaa-0000-0000-0000-000000000150");
    private const string Definition = """
        {"identity":"test.navigation","version":"1.0.0","workspace":"workshop",
         "surface_catalogue":{"id":"catalogue","surfaces":[{"id":"surface","label":"Surface"}]},
         "editor_destinations":[{"id":"editor","surface_id":"surface","definition_kinds":["form"],"projections":["react"]}],
         "navigation_entries":[{"id":"entry","label":"Entry","pillar":"forms","editor_destination_id":"editor"}]}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signed_pack_with_released_navigation_installs(bool host)
    {
        var (installer, context, store, bytes, key) = await FixtureAsync(Definition, host);
        using (key)
        {
            var result = await installer.InstallAsync(bytes, context);
            Assert.True(result.Installed, string.Join(", ", result.RefusalCodes));
            Assert.Single(store.ListInstalled(Tenant));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signed_pack_with_unreadable_navigation_refuses_before_commit(bool host)
    {
        var (installer, context, store, bytes, key) = await FixtureAsync("{}", host);
        using (key)
        {
            var result = await installer.InstallAsync(bytes, context);
            Assert.False(result.Installed);
            var refusal = Assert.Single(result.Preview.AdmissionRefusals);
            Assert.Equal("released-navigation.definition.invalid", refusal.Code);
            Assert.Equal("/", refusal.Pointer);
            Assert.Empty(store.ListInstalled(Tenant));
        }
    }

    [Fact]
    public void Missing_destination_preserves_the_producer_code_and_pointer()
    {
        var invalid = Definition.Replace("\"editor_destination_id\":\"editor\"", "\"editor_destination_id\":\"missing\"", StringComparison.Ordinal);
        var refusal = Assert.Single(PackReleasedNavigationAdmission.Validate(
            [new("test.pack", "test.navigation", PackContentKind.ReleasedNavigationDefinition, "1.0.0", invalid)]));
        Assert.Equal("released-navigation.editor-destination.missing", refusal.Code);
        Assert.Equal("/navigation_entries/0/editor_destination_id", refusal.Pointer);
    }

    private static async Task<(PackInstaller Installer, PackInstallContext Context, InMemoryPackInstallStore Store, byte[] Bytes, KeyPair Key)> FixtureAsync(string json, bool host)
    {
        var key = KeyPair.Generate();
        var codec = new PackFileCodec();
        var exporter = new PackExporter(new PackContentCanonicalizer(), new PackDcpCanonicalizer(),
            new PackValidator(new PackContentPiiScanner()), new DcpValidator(DcpCounselRegister.FromEmbeddedResource()),
            codec, timeProvider: TimeProvider.System);
        var export = await exporter.ExportAsync(new PackExportRequest(
            "test.released-navigation", "1.0.0", "Navigation", "Admission fixture", PackScopeTier.Vertical,
            [new PackContentSource("test.navigation", PackContentKind.ReleasedNavigationDefinition, "1.0.0", JsonNode.Parse(json)!)],
            Array.Empty<PackDependencyRef>(), Array.Empty<string>(), Epoch: 1,
            Dcp: DomainComplianceProfile.General("test-author")), new Ed25519Signer(key));
        Assert.True(export.Succeeded, string.Join("; ", export.Validation.Errors.Select(error => error.Message)));
        var store = new InMemoryPackInstallStore();
        IPackContentAdmission admission = host
            ? new PackWorkflowAdmissionAdapter(new WorkflowAdmissionValidator())
            : new WorkflowRefusingPackContentAdmission();
        var installer = new PackInstaller(new PackVerifier(new Ed25519Verifier(), codec), store, admission,
            new InMemoryPackInstallAudit(), TestAuthorization.AllowGate());
        var trust = new InMemoryPackTrustStore([new PackTrustRoot(TrustScope.OwnRoster, key.PrincipalId, 1, TrustRootStatus.Current)]);
        var context = new PackInstallContext(Tenant, trust, PackRevocationList.Empty,
            AdmittedInstant.FromRecordedAct(new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero)), TimeSpan.FromDays(30), Principal: "test-operator");
        return (installer, context, store, export.FileBytes!, key);
    }
}
